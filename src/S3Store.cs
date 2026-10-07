using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace PhotoImportV2
{
    // Path-style, SigV4 S3 without an SDK. ETags are opaque concurrency tokens;
    // callers must verify the complete object by reading it back and hashing it.
    public sealed class S3Store : IRemoteStore
    {
        internal const long MultipartThreshold = 64L * 1024 * 1024;
        internal const int PartBytes = 16 * 1024 * 1024;
        internal const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly DestinationRecord destination;
        private readonly Uri endpoint;
        private readonly string bucketPath;
        private readonly string prefix;
        private readonly Func<DestinationRecord, NetworkCredential> readCredential;
        private readonly int timeoutMs;
        private readonly int idleMs;
        private readonly object capabilityLock = new object();
        private NetworkCredential credential;
        private bool conditionalPut;
        private bool conditionalMultipart;

        public S3Store(DestinationRecord destination)
            : this(destination, CredentialStore.Read, 120000, 30000) { }
        public S3Store(DestinationRecord destination, int idleTimeoutSeconds)
            : this(destination, CredentialStore.Read, 30000, Math.Max(30, Math.Min(1800, idleTimeoutSeconds)) * 1000) { }

        // Injectable credentials and short timeouts keep fixtures offline and away
        // from the user's Credential Manager. No credentials are serialized.
        internal S3Store(DestinationRecord destination, Func<DestinationRecord, NetworkCredential> readCredential,
            int timeoutMs, int idleMs)
        {
            this.destination = StorageSettings.Normalize(destination);
            if (this.destination.Type != "s3") throw new ArgumentException("An S3 destination is required.");
            if (readCredential == null) throw new ArgumentNullException("readCredential");
            if (timeoutMs <= 0 || idleMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
            this.readCredential = readCredential;
            this.timeoutMs = timeoutMs;
            this.idleMs = idleMs;
            endpoint = new Uri(this.destination.Endpoint);
            bucketPath = Encode(Uri.UnescapeDataString(endpoint.AbsolutePath).TrimEnd('/') + "/" + this.destination.Bucket, true);
            prefix = String.IsNullOrEmpty(this.destination.Prefix) ? "" : this.destination.Prefix + "/";
        }

        public IEnumerable<RemoteObject> List()
        {
            string token = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var query = new Dictionary<string, string> {
                    { "list-type", "2" }, { "max-keys", "1000" }, { "prefix", prefix }, { "encoding-type", "url" }
                };
                if (token != null) query.Add("continuation-token", token);
                XmlDocument page;
                // Close the connection before yielding a page to the caller.
                using (var response = Send("GET", null, query, null, null, 0, EmptyHash))
                { RequireStatus(response, 200); page = ReadXml(response, "ListBucketResult"); }
                XmlElement root = page.DocumentElement;
                string encoding = Value(root, "EncodingType", false);
                if (encoding != null && encoding != "url") throw new IOException("Unsupported S3 listing encoding.");
                string truncated = Value(root, "IsTruncated", true);
                if (truncated != "false" && truncated != "true") throw new IOException("Invalid S3 pagination flag.");
                string next = null;
                if (truncated == "true")
                {
                    next = Value(root, "NextContinuationToken", true);
                    if (next.Length == 0 || next.Length > 16384 || !seen.Add(next)) throw new IOException("S3 pagination did not advance.");
                }
                foreach (XmlNode node in root.ChildNodes)
                {
                    var item = node as XmlElement;
                    if (item == null || item.LocalName != "Contents" || item.NamespaceURI != root.NamespaceURI) continue;
                    string key = Value(item, "Key", true);
                    if (encoding == "url") key = Decode(key);
                    if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("S3 returned an object outside the configured prefix.");
                    string relative = key.Substring(prefix.Length);
                    if (relative.Length == 0 || relative.EndsWith("/", StringComparison.Ordinal)) continue;
                    StorageSettings.Relative(relative, false);
                    long size;
                    if (!Int64.TryParse(Value(item, "Size", true), NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0)
                        throw new IOException("Invalid object length in S3 listing.");
                    yield return new RemoteObject { Path = relative, Length = size, ETag = Value(item, "ETag", false) };
                }
                if (truncated == "false") yield break;
                token = next;
            } while (true);
        }

        public RemoteObject Stat(string relativePath)
        {
            string key = ObjectKey(relativePath);
            try
            {
                using (var response = Send("HEAD", key, null, null, null, 0, EmptyHash))
                {
                    RequireStatus(response, 200);
                    if (response.Response.ContentLength < 0) throw new IOException("S3 HEAD omitted the object length.");
                    return new RemoteObject { Path = relativePath, Length = response.Response.ContentLength,
                        ETag = response.Response.Headers["ETag"] };
                }
            }
            catch (S3Failure error) { if (error.Status == 404) return null; throw; }
        }

        public Stream OpenRead(string relativePath)
        {
            var response = Send("GET", ObjectKey(relativePath), null, null, null, 0, EmptyHash);
            try { RequireStatus(response, 200); response.CheckLength = true; return response; }
            catch { response.Dispose(); throw; }
        }

        public bool UploadNew(string relativePath, Stream input, long length, Action<long> progress)
        {
            string key = ObjectKey(relativePath);
            if (input == null) throw new ArgumentNullException("input");
            if (!input.CanRead) throw new ArgumentException("The upload input must be readable.");
            if (length < 0 || length > (long)PartBytes * 10000) throw new ArgumentOutOfRangeException("length", "S3 uploads support at most 10000 bounded 16 MiB parts.");
            bool multipart = length > MultipartThreshold;
            EnsureCapabilities(multipart);
            if (multipart) return Multipart(key, input, length, progress, null);
            byte[] buffer = new byte[(int)length];
            ReadExactly(input, buffer, (int)length);
            RequireEnd(input);
            return Put(key, buffer, null, progress);
        }

        public WorkResult Probe(bool writeTest)
        {
            try
            {
                // One page is enough to authenticate/list; enumerating a huge
                // bucket is unnecessary, but an empty bucket must still be queried.
                using (var objects = List().GetEnumerator()) objects.MoveNext();
                if (!writeTest) return new WorkResult { Success = true,
                    Detail = "S3 身份验证和对象列表读取成功。只读检测未验证对象读取、条件上传、分片完成和清理能力。" };
                EnsureCapabilities(true);
                return new WorkResult { Success = true,
                    Detail = "S3 列表、微小测试对象回读、禁止覆盖上传、分片条件冲突拒绝和测试对象条件清理已验证。ETag 不是文件哈希，导入仍须完整回读并核验 SHA256。" };
            }
            catch (Exception error) { return new WorkResult { Success = false, Error = error.Message, Detail = "S3 能力检测失败，不能据此确认上传安全。" }; }
        }

        private void EnsureCapabilities(bool multipart)
        {
            lock (capabilityLock)
            {
                if (conditionalPut && (!multipart || conditionalMultipart)) return;
                VerifyFixture(multipart);
                conditionalPut = true;
                if (multipart) conditionalMultipart = true;
            }
        }

        private void VerifyFixture(bool multipart)
        {
            // Verify ignored preconditions only against a unique object belonging
            // to this invocation, never against a user's target or existing object.
            string owner = Guid.NewGuid().ToString("N");
            string relative = ".photo-import-probe-" + owner + ".tmp";
            string key = ObjectKey(relative);
            byte[] body = Utf8.GetBytes(owner);
            var metadata = new Dictionary<string, string> { { "x-amz-meta-photo-import-owner", owner } };
            bool created = false;
            bool attempted = false;
            bool safeDelete = false;
            bool cleaned = false;
            Exception failure = null;
            try
            {
                attempted = true;
                if (!Put(key, body, metadata))
                { attempted = false; throw new IOException("The unique S3 probe name is occupied; it was left untouched."); }
                created = true;
                VerifyOwnedFixture(key, owner, body);
                try
                {
                    using (var response = Send("DELETE", key, null,
                        new Dictionary<string, string> { { "If-Match", "\"never-match-" + Guid.NewGuid().ToString("N") + "\"" } }, null, 0, EmptyHash)) { }
                    throw new IOException("The S3 endpoint ignored conditional deletion; CREATE_NEW capability verification failed.");
                }
                catch (S3Failure error)
                {
                    if (error.Status != 412) throw new IOException("The S3 endpoint must support conditional DeleteObject for safe probe cleanup. " + error.Message);
                    safeDelete = true;
                }
                byte[] different = Utf8.GetBytes("different-" + owner);
                if (Put(key, different, metadata)) throw new IOException("The S3 endpoint ignored If-None-Match on PUT; uploads are disabled to prevent overwrites.");
                VerifyOwnedFixture(key, owner, body);
                if (multipart)
                {
                    using (var input = new MemoryStream(different, false))
                        if (Multipart(key, input, different.Length, null, metadata))
                            throw new IOException("The S3 endpoint ignored If-None-Match on CompleteMultipartUpload; multipart uploads are disabled to prevent overwrites.");
                    VerifyOwnedFixture(key, owner, body);
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (created && safeDelete)
                {
                    try
                    {
                        using (var head = Send("HEAD", key, null, null, null, 0, EmptyHash))
                        {
                            RequireStatus(head, 200);
                            if (head.Response.Headers["x-amz-meta-photo-import-owner"] != owner)
                                throw new IOException("Probe fixture ownership changed; no cleanup was attempted.");
                            string etag = RequireETag(head.Response.Headers["ETag"]);
                            using (var deleted = Send("DELETE", key, null,
                                new Dictionary<string, string> { { "If-Match", etag } }, null, 0, EmptyHash))
                                RequireStatus(deleted, 204, 200);
                            cleaned = true;
                        }
                    }
                    catch (Exception error)
                    {
                        failure = new IOException((failure == null ? "" : failure.Message + " ") + "Conditional probe cleanup failed: " + error.Message);
                    }
                }
            }
            if (failure != null) throw new IOException(failure.Message +
                (attempted && !cleaned ? " Probe fixture may remain at " + relative + "; inspect ownership before manual cleanup." : ""));
        }

        private void VerifyOwnedFixture(string key, string owner, byte[] body)
        {
            using (var head = Send("HEAD", key, null, null, null, 0, EmptyHash))
            {
                RequireStatus(head, 200);
                if (head.Response.Headers["x-amz-meta-photo-import-owner"] != owner || head.Response.ContentLength != body.Length)
                    throw new IOException("S3 probe fixture ownership or length changed.");
                RequireETag(head.Response.Headers["ETag"]);
            }
            using (var response = Send("GET", key, null, null, null, 0, EmptyHash))
            {
                RequireStatus(response, 200);
                for (int i = 0; i < body.Length; i++) if (response.ReadByte() != body[i]) throw new IOException("S3 probe readback did not match.");
                if (response.ReadByte() != -1) throw new IOException("S3 probe readback has extra bytes.");
            }
        }

        private bool Put(string key, byte[] buffer, IDictionary<string, string> metadata, Action<long> progress = null)
        {
            var headers = CopyHeaders(metadata);
            headers.Add("If-None-Match", "*");
            headers.Add("Content-Type", "application/octet-stream");
            try
            {
                using (var input = new MemoryStream(buffer, false))
                using (var response = Send("PUT", key, null, headers, input, buffer.Length, Hash(buffer, buffer.Length), progress))
                    RequireStatus(response, 200, 201);
                return true;
            }
            catch (S3Failure error) { if (error.Status == 412) return false; throw; }
        }

        private bool Multipart(string key, Stream input, long length, Action<long> progress, IDictionary<string, string> metadata)
        {
            string uploadId = null;
            bool completed = false;
            Exception failure = null;
            try
            {
                using (var created = Send("POST", key, new Dictionary<string, string> { { "uploads", "" } }, metadata, null, 0, EmptyHash))
                {
                    RequireStatus(created, 200);
                    uploadId = Value(ReadXml(created, "InitiateMultipartUploadResult").DocumentElement, "UploadId", true);
                    if (uploadId.Length == 0 || uploadId.Length > 8192) { uploadId = null; throw new IOException("Invalid S3 multipart upload ID; upload cannot be safely aborted."); }
                }
                byte[] buffer = new byte[PartBytes];
                var etags = new List<string>();
                long consumed = 0;
                do
                {
                    int count = (int)Math.Min(buffer.Length, length - consumed);
                    ReadExactly(input, buffer, count);
                    consumed += count;
                    var query = new Dictionary<string, string> { { "uploadId", uploadId },
                        { "partNumber", (etags.Count + 1).ToString(CultureInfo.InvariantCulture) } };
                    etags.Add(UploadPart(key, query, buffer, count, consumed - count, progress, metadata == null));
                } while (consumed < length);
                RequireEnd(input);
                byte[] xml = CompleteXml(etags);
                using (var content = new MemoryStream(xml, false))
                using (var response = Send("POST", key, new Dictionary<string, string> { { "uploadId", uploadId } },
                    new Dictionary<string, string> { { "If-None-Match", "*" }, { "Content-Type", "application/xml" } }, content, xml.Length, Hash(xml, xml.Length)))
                {
                    RequireStatus(response, 200);
                    // A 200 can contain an Error after S3 has sent its headers.
                    RequireETag(Value(ReadXml(response, "CompleteMultipartUploadResult").DocumentElement, "ETag", true));
                    completed = true;
                    return true;
                }
            }
            catch (S3Failure error) { failure = error; if (error.Status == 412) return false; throw; }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                if (uploadId != null && !completed)
                {
                    try
                    {
                        using (var aborted = Send("DELETE", key, new Dictionary<string, string> { { "uploadId", uploadId } }, null, null, 0, EmptyHash))
                            RequireStatus(aborted, 204, 200);
                    }
                    catch (S3Failure error)
                    {
                        if (error.Status != 404) throw new IOException((failure == null ? "" : failure.Message + " ") + "Abort of this invocation's multipart upload failed; an incomplete upload may remain. " + error.Message);
                    }
                    catch (Exception) { throw new IOException((failure == null ? "" : failure.Message + " ") + "Abort of this invocation's multipart upload failed; an incomplete upload may remain."); }
                }
            }
        }

        private string UploadPart(string key, IDictionary<string, string> query, byte[] buffer, int count,
            long offset, Action<long> progress, bool allowRetry)
        {
            // The buffer remains immutable until this part succeeds. Retrying the
            // same owned upload ID/part number replaces only our unpublished part.
            string payloadHash = Hash(buffer, count);
            long reported = offset;
            Action<long> wireProgress = progress == null ? null : new Action<long>(delegate(long value) {
                reported = Math.Max(reported, value);
                // Equal values keep the caller's idle watchdog alive during a
                // retransmission without counting source bytes twice.
                progress(reported);
            });
            int attempts = allowRetry ? 3 : 1;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using (var part = new MemoryStream(buffer, 0, count, false))
                    using (var response = Send("PUT", key, query, null, part, count, payloadHash, wireProgress, offset))
                    { RequireStatus(response, 200); return RequireETag(response.Response.Headers["ETag"]); }
                }
                catch (S3Failure error)
                {
                    int status = error.Status;
                    if (attempt >= attempts || !(status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504)) throw;
                }
                catch (S3NetworkFailure error) { if (attempt >= attempts || !error.Transient) throw; }
                Thread.Sleep(100 * attempt);
            }
        }

        private OwnedResponse Send(string method, string key, IDictionary<string, string> query, IDictionary<string, string> extra,
            Stream body, long length, string payloadHash, Action<long> progress = null, long progressOffset = 0)
        {
            NetworkCredential keys = Credentials();
            string path = bucketPath + (key == null ? "" : "/" + Encode(key, true));
            string canonicalQuery = Query(query);
            var uri = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + path + (canonicalQuery.Length == 0 ? "" : "?" + canonicalQuery));
            // Sign the actual .NET URI escaping; no path normalization/double encoding.
            if (uri.AbsolutePath != path) throw new ArgumentException("The object name cannot be represented without URL normalization.");
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = method;
            request.AllowAutoRedirect = false;
            request.AllowWriteStreamBuffering = false;
            // The resettable watchdog bounds connection establishment, then
            // network idle time. HttpWebRequest's total timeout would also limit
            // an actively progressing slow upload through GetResponse().
            request.Timeout = Timeout.Infinite;
            request.ReadWriteTimeout = idleMs;
            request.Credentials = null;
            request.PreAuthenticate = false;
            request.KeepAlive = false;
            request.ServicePoint.Expect100Continue = false;
            request.AutomaticDecompression = DecompressionMethods.None;
            if (method == "PUT" || method == "POST") request.ContentLength = length;
            var headers = CopyHeaders(extra);
            headers.Add("host", uri.Authority);
            headers.Add("x-amz-date", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            headers.Add("x-amz-content-sha256", payloadHash);
            string authorization = Sign(method, path, canonicalQuery, headers, payloadHash, keys.UserName, keys.Password, destination.Region);
            foreach (var header in headers)
            {
                if (header.Key.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) request.ContentType = header.Value;
                else request.Headers[header.Key] = header.Value;
            }
            request.Headers["Authorization"] = authorization;
            var timer = new IdleWatchdog(request, timeoutMs);
            try
            {
                if (body != null && length > 0)
                {
                    using (var output = request.GetRequestStream())
                    {
                        timer.Reset(idleMs);
                        byte[] buffer = new byte[65536];
                        long remaining = length;
                        while (remaining > 0)
                        {
                            int count = body.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (count == 0) throw new EndOfStreamException("The buffered S3 request body was truncated.");
                            try { output.Write(buffer, 0, count); }
                            catch (IOException) { throw new S3NetworkFailure(); }
                            timer.Reset(idleMs);
                            remaining -= count;
                            if (progress != null) progress(progressOffset + length - remaining);
                        }
                    }
                }
                if (body != null) timer.Reset(idleMs);
                var response = (HttpWebResponse)request.GetResponse();
                var owned = new OwnedResponse(request, response, timer, idleMs);
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    using (owned) throw new S3Failure((int)response.StatusCode, null);
                }
                return owned;
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null)
                {
                    using (var owned = new OwnedResponse(request, response, timer, idleMs))
                    {
                        string code = null;
                        try { code = Value(ReadXmlRaw(owned).DocumentElement, "Code", false); } catch (Exception) { }
                        throw new S3Failure((int)response.StatusCode, code);
                    }
                }
                timer.Dispose(); request.Abort();
                WebExceptionStatus status = error.Status;
                bool transient = status == WebExceptionStatus.ConnectFailure || status == WebExceptionStatus.ConnectionClosed ||
                    status == WebExceptionStatus.KeepAliveFailure || status == WebExceptionStatus.ReceiveFailure || status == WebExceptionStatus.SendFailure ||
                    status == WebExceptionStatus.Timeout || status == WebExceptionStatus.RequestCanceled || status == WebExceptionStatus.NameResolutionFailure ||
                    status == WebExceptionStatus.ProxyNameResolutionFailure;
                throw new S3NetworkFailure(transient);
            }
            catch { timer.Dispose(); request.Abort(); throw; }
        }

        private NetworkCredential Credentials()
        {
            lock (capabilityLock)
            {
                if (credential != null) return credential;
                NetworkCredential value;
                try { value = readCredential(destination); }
                catch (Exception) { throw new InvalidOperationException("无法读取 S3 凭据。请为此位置重新保存 Access Key 和 Secret Key 到 Windows 凭据管理器。"); }
                if (value == null || String.IsNullOrWhiteSpace(value.UserName) || String.IsNullOrEmpty(value.Password))
                    throw new InvalidOperationException("缺少 S3 凭据。请为此位置保存 Access Key 和 Secret Key 到 Windows 凭据管理器（CredentialTarget），不要将密码写入配置。");
                foreach (char c in value.UserName) if (c <= 32 || c >= 127 || c == '/' || c == ',' || c == '=')
                    throw new InvalidOperationException("S3 Access Key 无效，请更新此位置保存的凭据。");
                credential = new NetworkCredential(value.UserName, value.Password);
                return credential;
            }
        }

        private string ObjectKey(string relative)
        {
            string key = prefix + StorageSettings.Relative(relative, false);
            if (Utf8.GetByteCount(key) > 1024) throw new ArgumentException("S3 对象名称（含 Prefix）不能超过 1024 个 UTF-8 字节。");
            return key;
        }

        internal static string Sign(string method, string path, string query, IDictionary<string, string> headers,
            string payloadHash, string accessKey, string secretKey, string region)
        {
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in headers) sorted.Add(header.Key.ToLowerInvariant(), NormalizeHeader(header.Value));
            var canonicalHeaders = new StringBuilder();
            foreach (var header in sorted) canonicalHeaders.Append(header.Key).Append(':').Append(header.Value).Append('\n');
            string signedHeaders = String.Join(";", new List<string>(sorted.Keys).ToArray());
            string canonical = method + "\n" + path + "\n" + query + "\n" + canonicalHeaders + "\n" + signedHeaders + "\n" + payloadHash;
            string timestamp = sorted["x-amz-date"];
            string date = timestamp.Substring(0, 8);
            string scope = date + "/" + region + "/s3/aws4_request";
            byte[] signing = Hmac(Hmac(Hmac(Hmac(Utf8.GetBytes("AWS4" + secretKey), date), region), "s3"), "aws4_request");
            string signature = Hex(Hmac(signing, "AWS4-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Hash(Utf8.GetBytes(canonical), Utf8.GetByteCount(canonical))));
            return "AWS4-HMAC-SHA256 Credential=" + accessKey + "/" + scope + ",SignedHeaders=" + signedHeaders + ",Signature=" + signature;
        }

        internal static string Encode(string value, bool slash)
        {
            var result = new StringBuilder();
            foreach (byte c in Utf8.GetBytes(value))
            {
                if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.' || c == '~' || slash && c == '/') result.Append((char)c);
                else result.Append('%').Append(c.ToString("X2", CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }

        private static string Decode(string value)
        {
            using (var bytes = new MemoryStream())
            {
                for (int i = 0; i < value.Length; i++)
                {
                    if (value[i] == '%')
                    {
                        if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) throw new IOException("Malformed URL-encoded S3 key.");
                        bytes.WriteByte(Byte.Parse(value.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 2;
                    }
                    else
                    {
                        if (value[i] > 127) throw new IOException("S3 URL-encoded key contains unescaped characters.");
                        bytes.WriteByte((byte)value[i]); // '+' is a literal plus.
                    }
                }
                try { return Utf8.GetString(bytes.ToArray()); }
                catch (DecoderFallbackException) { throw new IOException("S3 object name contains invalid UTF-8."); }
            }
        }

        private static string Query(IDictionary<string, string> values)
        {
            if (values == null) return "";
            var pairs = new List<string>();
            foreach (var value in values) pairs.Add(Encode(value.Key, false) + "=" + Encode(value.Value, false));
            pairs.Sort(StringComparer.Ordinal);
            return String.Join("&", pairs.ToArray());
        }

        private static string NormalizeHeader(string value)
        { return String.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)); }

        private static Dictionary<string, string> CopyHeaders(IDictionary<string, string> headers)
        { return headers == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase); }

        private static byte[] Hmac(byte[] key, string value)
        { using (var hash = new HMACSHA256(key)) return hash.ComputeHash(Utf8.GetBytes(value)); }

        internal static string Hash(byte[] bytes, int count)
        { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes, 0, count)); }

        private static string Hex(byte[] bytes)
        { var result = new StringBuilder(bytes.Length * 2); foreach (byte value in bytes) result.Append(value.ToString("x2", CultureInfo.InvariantCulture)); return result.ToString(); }

        private static void ReadExactly(Stream input, byte[] buffer, int length)
        {
            int offset = 0;
            while (offset < length)
            {
                int read = input.Read(buffer, offset, Math.Min(65536, length - offset));
                if (read == 0) throw new EndOfStreamException("Upload input ended before its declared length.");
                offset += read;
            }
        }

        private static void RequireEnd(Stream input)
        { if (input.ReadByte() != -1) throw new IOException("Upload input exceeds its declared length; the object was not published."); }

        private static string RequireETag(string etag)
        {
            if (etag == null || etag.Length < 2 || etag.Length > 1024 || etag[0] != '"' || etag[etag.Length - 1] != '"')
                throw new IOException("S3 omitted a usable strong ETag; safe completion or conditional cleanup is unavailable.");
            for (int i = 1; i < etag.Length - 1; i++) if (etag[i] < 33 || etag[i] > 126 || etag[i] == '"') throw new IOException("Invalid S3 ETag.");
            return etag;
        }

        private static byte[] CompleteXml(List<string> etags)
        {
            using (var buffer = new MemoryStream())
            {
                using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = Utf8, OmitXmlDeclaration = true, CloseOutput = false }))
                {
                    writer.WriteStartElement("CompleteMultipartUpload");
                    for (int i = 0; i < etags.Count; i++)
                    {
                        writer.WriteStartElement("Part");
                        writer.WriteElementString("PartNumber", (i + 1).ToString(CultureInfo.InvariantCulture));
                        writer.WriteElementString("ETag", etags[i]); writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                return buffer.ToArray();
            }
        }

        private static XmlDocument ReadXmlRaw(Stream stream)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024, CloseInput = false })) document.Load(reader);
            if (document.DocumentElement == null) throw new IOException("Empty S3 XML response.");
            return document;
        }

        private static XmlDocument ReadXml(Stream stream, string rootName)
        {
            XmlDocument document = ReadXmlRaw(stream);
            if (document.DocumentElement.LocalName == "Error")
            {
                string code = Value(document.DocumentElement, "Code", false);
                throw new S3Failure(code == "PreconditionFailed" ? 412 : code == "ConditionalRequestConflict" ? 409 : 500, code);
            }
            if (document.DocumentElement.LocalName != rootName) throw new IOException("Unexpected S3 XML response.");
            return document;
        }

        private static string Value(XmlElement parent, string name, bool required)
        {
            string value = null;
            foreach (XmlNode node in parent.ChildNodes)
            {
                var child = node as XmlElement;
                if (child == null || child.LocalName != name || child.NamespaceURI != parent.NamespaceURI) continue;
                if (value != null || child.ChildNodes.Count > 1 || child.FirstChild is XmlElement) throw new IOException("Ambiguous S3 XML field.");
                value = child.InnerText;
            }
            if (required && value == null) throw new IOException("S3 XML response omitted " + name + ".");
            return value;
        }

        private static void RequireStatus(OwnedResponse response, params int[] allowed)
        {
            foreach (int status in allowed) if ((int)response.Response.StatusCode == status) return;
            throw new IOException("Unexpected S3 response status " + ((int)response.Response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
        }

        private sealed class S3Failure : IOException
        {
            internal readonly int Status;
            internal S3Failure(int status, string code) : base(MessageFor(status, code)) { Status = status; }
            private static string MessageFor(int status, string code)
            {
                // Never relay server Messages or arbitrary Codes: either can echo
                // credentials or signed headers supplied in the request.
                string[] known = { "AccessDenied", "SignatureDoesNotMatch", "InvalidAccessKeyId", "AuthorizationHeaderMalformed", "RequestTimeTooSkewed",
                    "ExpiredToken", "NoSuchBucket", "NoSuchKey", "PreconditionFailed", "ConditionalRequestConflict", "InvalidRequest", "NotImplemented",
                    "InternalError", "InvalidPart", "InvalidPartOrder", "EntityTooSmall", "NoSuchUpload" };
                if (Array.IndexOf(known, code) < 0) code = null;
                return "S3 请求失败（HTTP " + status.ToString(CultureInfo.InvariantCulture) + (String.IsNullOrEmpty(code) ? "" : ", " + code) +
                    "），请检查 Endpoint、Region、Bucket 权限和保存的凭据。禁止自动重定向；条件上传不会以覆盖方式重试。";
            }
        }

        private sealed class S3NetworkFailure : IOException
        {
            internal readonly bool Transient;
            internal S3NetworkFailure(bool transient = true) : base("S3 网络请求失败或超时，请检查 Endpoint、TLS 证书和网络连接。") { Transient = transient; }
        }

        private sealed class IdleWatchdog : IDisposable
        {
            private readonly object gate = new object();
            private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            private readonly HttpWebRequest request;
            private readonly Timer timer;
            private long deadline;
            private bool stopped;
            internal IdleWatchdog(HttpWebRequest request, int initialMs)
            {
                this.request = request; deadline = clock.ElapsedMilliseconds + initialMs;
                timer = new Timer(Check, null, Timeout.Infinite, Timeout.Infinite);
                timer.Change(initialMs, Timeout.Infinite);
            }
            internal void Reset(int milliseconds)
            {
                lock (gate)
                {
                    if (stopped) return;
                    deadline = clock.ElapsedMilliseconds + milliseconds;
                    timer.Change(milliseconds, Timeout.Infinite);
                }
            }
            private void Check(object ignored)
            {
                lock (gate)
                {
                    if (stopped) return;
                    long remaining = deadline - clock.ElapsedMilliseconds;
                    // A previously queued callback must observe the latest read/
                    // write deadline instead of aborting an active transfer.
                    if (remaining > 0) { timer.Change((int)Math.Min(Int32.MaxValue, remaining), Timeout.Infinite); return; }
                    stopped = true;
                }
                try { request.Abort(); } catch (ObjectDisposedException) { }
            }
            public void Dispose() { lock (gate) { stopped = true; timer.Dispose(); } }
        }

        private sealed class OwnedResponse : Stream
        {
            internal readonly HttpWebResponse Response;
            internal bool CheckLength;
            private readonly HttpWebRequest request;
            private readonly Stream stream;
            private readonly IdleWatchdog timer;
            private readonly int idleMs;
            private bool disposed;
            private long consumed;
            internal OwnedResponse(HttpWebRequest request, HttpWebResponse response, IdleWatchdog timer, int idleMs)
            {
                this.request = request; Response = response; this.timer = timer; this.idleMs = idleMs;
                stream = response.GetResponseStream();
                // Network activity, not total object size/duration, bounds GETs.
                timer.Reset(idleMs);
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (disposed) throw new ObjectDisposedException("S3 response stream");
                int read = stream.Read(buffer, offset, count);
                if (read > 0) timer.Reset(idleMs);
                consumed += read;
                if (CheckLength && count > 0 && read == 0 && Response.ContentLength >= 0 && consumed != Response.ContentLength)
                    throw new EndOfStreamException("S3 object response ended before its declared length.");
                return read;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing && !disposed)
                {
                    disposed = true; timer.Dispose();
                    request.Abort();
                    try { stream.Dispose(); } finally { Response.Close(); }
                }
                base.Dispose(disposing);
            }
            public override bool CanRead { get { return !disposed; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { if (disposed) throw new ObjectDisposedException("S3 response stream"); if (Response.ContentLength < 0) throw new NotSupportedException(); return Response.ContentLength; } }
            public override long Position { get { return consumed; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
