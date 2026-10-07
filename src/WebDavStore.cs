using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Text;
using System.Threading;
using System.Xml;

namespace PhotoImportV2
{
    /// <summary>Direct, non-resumable WebDAV with CREATE_NEW publication.</summary>
    public sealed class WebDavStore : IRemoteStore
    {
        private const string Dav = "DAV:";
        private const string PartialPrefix = ".photo-import-";
        private const int BufferSize = 65536;
        private const long XmlLimit = 8L * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly byte[] Properties = Utf8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
            "<d:resourcetype/><d:getcontentlength/><d:getetag/></d:prop></d:propfind>");
        private readonly Uri root;
        private readonly string[] rootSegments;
        private readonly ICredentials credentials;
        private readonly int timeout;
        private readonly int readTimeout;
        private readonly int maxDepth;
        private readonly int maxObjects;

        public WebDavStore(DestinationRecord destination)
            : this(destination, CredentialStore.Read(destination), 30000, 32, 100000) { }
        public WebDavStore(DestinationRecord destination, int idleTimeoutSeconds)
            : this(destination, CredentialStore.Read(destination), 30000, 32, 100000)
        { readTimeout = Math.Max(30, Math.Min(1800, idleTimeoutSeconds)) * 1000; }

        // Explicit credentials keep tests independent of the user's credential vault.
        internal WebDavStore(DestinationRecord destination, NetworkCredential credential, int timeoutMilliseconds, int depthLimit, int objectLimit)
        {
            DestinationRecord normalized = StorageSettings.Normalize(destination);
            if (normalized.Type != "webdav") throw new ArgumentException("需要 WebDAV 保存位置。", "destination");
            Uri parsed;
            if (!Uri.TryCreate(normalized.Endpoint, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https") || parsed.UserInfo.Length != 0 ||
                parsed.Query.Length != 0 || parsed.Fragment.Length != 0)
                throw new ArgumentException("WebDAV 根地址无效。", "destination");
            root = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
            rootSegments = DecodeSegments(root.AbsolutePath);
            if (timeoutMilliseconds < 1 || depthLimit < 1 || objectLimit < 1) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            timeout = timeoutMilliseconds; readTimeout = timeoutMilliseconds; maxDepth = depthLimit; maxObjects = objectLimit;
            if (credential != null)
            {
                // A cache binds authentication to this root, never to arbitrary hosts.
                var cache = new CredentialCache();
                foreach (string scheme in new[] { "Basic", "Digest", "Negotiate", "NTLM" }) cache.Add(root, scheme, credential);
                credentials = cache;
            }
        }

        public IEnumerable<RemoteObject> List()
        {
            var result = new List<RemoteObject>();
            var pending = new Queue<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(""); seen.Add("");
            int entries = 0;
            while (pending.Count != 0)
            {
                string collection = pending.Dequeue();
                foreach (DavEntry entry in PropFind(collection, 1))
                {
                    if (entry.Path == collection) continue;
                    if (++entries > maxObjects) throw new IOException("WebDAV 清单超过数量限制。");
                    if (!seen.Add(entry.Path)) throw new IOException("WebDAV 清单包含重复路径。");
                    if (entry.Collection)
                    {
                        if (entry.Path.Split('/').Length > maxDepth) throw new IOException("WebDAV 清单超过目录深度限制。");
                        pending.Enqueue(entry.Path);
                    }
                    else if (!IsPartial(entry.Path)) result.Add(entry.Object);
                }
            }
            // A truncated/invalid inventory must never look like a complete result.
            return result;
        }

        public RemoteObject Stat(string relativePath)
        {
            string path = StorageSettings.Relative(relativePath, false);
            using (HttpWebResponse response = Send(Request(ObjectUri(path, false), "HEAD")))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                Require(response, "HEAD", HttpStatusCode.OK);
                if (response.ContentLength < 0) throw new IOException("WebDAV HEAD 缺少有效文件长度。");
                return new RemoteObject { Path = path, Length = response.ContentLength, ETag = response.Headers[HttpResponseHeader.ETag] };
            }
        }

        public Stream OpenRead(string relativePath)
        {
            Uri uri = ObjectUri(StorageSettings.Relative(relativePath, false), false);
            HttpWebRequest request = Request(uri, "GET");
            HttpWebResponse response = Send(request);
            try
            {
                Require(response, "GET", HttpStatusCode.OK);
                Stream stream = response.GetResponseStream();
                if (stream == null) throw new IOException("WebDAV GET 未返回数据流。");
                if (stream.CanTimeout) stream.ReadTimeout = readTimeout;
                return new ResponseStream(stream, response);
            }
            catch { response.Close(); throw; }
        }

        public bool UploadNew(string relativePath, Stream input, long length, Action<long> progress)
        {
            string path = StorageSettings.Relative(relativePath, false);
            if (input == null) throw new ArgumentNullException("input");
            if (!input.CanRead) throw new ArgumentException("输入流不可读取。", "input");
            if (length < 0) throw new ArgumentOutOfRangeException("length");
            // This also primes challenge authentication before the unbuffered PUT.
            if (Occupied(path)) return false;
            EnsureParents(path);
            int slash = path.LastIndexOf('/');
            string partial = (slash < 0 ? "" : path.Substring(0, slash + 1)) + PartialName();
            string ownedETag = PutPartial(partial, input, length, progress);
            bool cleanupConfirmed = true;
            try
            {
                cleanupConfirmed = false; // A lost MOVE response has an ambiguous outcome.
                MoveResult moved = Move(partial, path, ownedETag);
                if (moved.Status == HttpStatusCode.Created) return true;
                cleanupConfirmed = IsDefiniteRejection(moved.Status);
                if (moved.Status == HttpStatusCode.PreconditionFailed)
                {
                    // If-Match applies to the source. A 412 alone cannot prove collision.
                    if (Occupied(path)) return false;
                    throw new IOException("WebDAV MOVE 的来源校验条件失败。");
                }
                if (moved.Status == HttpStatusCode.MethodNotAllowed || moved.Status == HttpStatusCode.NotImplemented)
                    throw new NotSupportedException("WebDAV 不支持 MOVE，无法安全发布；请启用 MOVE。");
                throw StatusError("MOVE", moved.Status);
            }
            finally
            {
                // Never DELETE the requested final name. Only a confirmed CREATE_NEW
                // partial with its original strong validator can be cleaned up.
                if (cleanupConfirmed) DeleteOwnedPartial(partial, ownedETag);
            }
        }

        public WorkResult Probe(bool writeTest)
        {
            var owned = new Dictionary<string, string>(StringComparer.Ordinal);
            string error = null, detail = null;
            int retained = 0;
            try
            {
                PropFind("", 0);
                string advertised = Options();
                if (!writeTest)
                {
                    return new WorkResult
                    {
                        Success = true,
                        Detail = "WebDAV 读取可用；" + advertised + "；未执行写入测试。"
                    };
                }
                byte[] first = Utf8.GetBytes("PhotoImport WebDAV probe " + Guid.NewGuid().ToString("N"));
                byte[] second = Utf8.GetBytes("PhotoImport WebDAV collision " + Guid.NewGuid().ToString("N"));
                string source = PartialName(), occupied = PartialName(), destination = PartialName();
                using (var stream = new MemoryStream(first, false)) owned[source] = PutPartial(source, stream, first.Length, null);
                using (var stream = new MemoryStream(second, false)) owned[occupied] = PutPartial(occupied, stream, second.Length, null);
                string sourceTag = owned[source];
                string occupiedTag = owned[occupied];
                // Exercise refusal using only probe-owned partials, not user files.
                owned.Remove(source); owned.Remove(occupied);
                MoveResult collision = Move(source, occupied, sourceTag);
                if (collision.Status != HttpStatusCode.PreconditionFailed)
                {
                    if (IsDefiniteRejection(collision.Status))
                    { owned[source] = sourceTag; owned[occupied] = occupiedTag; }
                    if (collision.Status == HttpStatusCode.MethodNotAllowed || collision.Status == HttpStatusCode.NotImplemented)
                        throw new NotSupportedException("WebDAV 不支持 MOVE，无法安全发布。");
                    throw new IOException("WebDAV 未确认 Overwrite:F 拒绝已占用名称，无法安全发布。");
                }
                owned[source] = sourceTag;
                owned[occupied] = occupiedTag;
                VerifyProbe(occupied, second);
                VerifyProbe(source, first);
                owned.Remove(source);
                MoveResult publication = Move(source, destination, sourceTag);
                if (publication.Status != HttpStatusCode.Created)
                {
                    if (IsDefiniteRejection(publication.Status)) owned[source] = sourceTag;
                    if (publication.Status == HttpStatusCode.MethodNotAllowed || publication.Status == HttpStatusCode.NotImplemented)
                        throw new NotSupportedException("WebDAV 不支持 MOVE，无法安全发布。");
                    throw StatusError("MOVE", publication.Status);
                }
                owned[destination] = StrongETag(publication.ETag) ? publication.ETag : sourceTag;
                VerifyProbe(destination, first);
                detail = "WebDAV 流式 PUT、MOVE 和 Overwrite:F 安全发布测试通过；不支持断点续传。";
            }
            catch (Exception ex)
            {
                // No server body, URL, credential target, account or secret is exposed.
                error = SafeError(ex);
                detail = "WebDAV 安全发布能力测试未通过。";
            }
            finally
            {
                foreach (KeyValuePair<string, string> entry in owned)
                    if (!DeleteOwnedPartial(entry.Key, entry.Value)) retained++;
            }
            if (retained != 0) detail += " 部分测试临时对象因缺少所有权校验或清理失败而保留。";
            return new WorkResult { Success = error == null, Error = error, Detail = detail };
        }

        private string Options()
        {
            using (HttpWebResponse response = Send(Request(root, "OPTIONS")))
            {
                if (response.StatusCode == HttpStatusCode.MethodNotAllowed || response.StatusCode == HttpStatusCode.NotImplemented)
                    return "MOVE 能力未声明，须通过写入测试确认";
                Require(response, "OPTIONS", HttpStatusCode.OK, HttpStatusCode.NoContent);
                string allow = response.Headers["Allow"];
                if (String.IsNullOrWhiteSpace(allow)) return "MOVE 能力未声明，须通过写入测试确认";
                foreach (string method in allow.Split(','))
                    if (method.Trim().Equals("MOVE", StringComparison.OrdinalIgnoreCase)) return "服务器声明支持 MOVE（尚需写入测试验证）";
                return "服务器未声明 MOVE，无法确认安全发布能力";
            }
        }

        private void VerifyProbe(string path, byte[] expected)
        {
            using (Stream stream = OpenRead(path))
            {
                int offset = 0;
                var buffer = new byte[expected.Length];
                while (offset < buffer.Length)
                {
                    int read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read == 0) throw new IOException("WebDAV 测试对象内容不完整。");
                    offset += read;
                }
                for (int i = 0; i < buffer.Length; i++)
                    if (buffer[i] != expected[i]) throw new IOException("WebDAV 测试对象内容不一致。");
                if (stream.ReadByte() != -1) throw new IOException("WebDAV 测试对象长度不一致。");
            }
        }

        private bool Occupied(string path)
        {
            using (HttpWebResponse response = Send(Request(ObjectUri(path, false), "HEAD")))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return false;
                Require(response, "HEAD", HttpStatusCode.OK, HttpStatusCode.NoContent);
                return true;
            }
        }

        private void EnsureParents(string path)
        {
            string[] segments = path.Split('/');
            string parent = "";
            for (int i = 0; i < segments.Length - 1; i++)
            {
                parent += (i == 0 ? "" : "/") + segments[i];
                using (HttpWebResponse response = Send(Request(ObjectUri(parent, true), "MKCOL")))
                {
                    if (response.StatusCode == HttpStatusCode.Created) continue;
                    if (response.StatusCode != HttpStatusCode.MethodNotAllowed) throw StatusError("MKCOL", response.StatusCode);
                }
                PropFind(parent, 0); // 405 can mean an existing file, not a directory.
            }
        }

        private string PutPartial(string partial, Stream input, long length, Action<long> progress)
        {
            HttpWebRequest request = Request(ObjectUri(partial, false), "PUT");
            request.Headers[HttpRequestHeader.IfNoneMatch] = "*";
            request.ContentType = "application/octet-stream";
            request.ContentLength = length;
            request.AllowWriteStreamBuffering = false;
            request.SendChunked = false;
            // Timeout covers the entire synchronous request, including its body.
            // Large transfers use idle I/O timeouts and separately bounded waits
            // for connecting and receiving the final response headers.
            request.Timeout = System.Threading.Timeout.Infinite;
            try
            {
                if (progress != null) progress(0);
                using (Stream output = Await<Stream>(request,
                    delegate(AsyncCallback callback) { return request.BeginGetRequestStream(callback, null); },
                    delegate(IAsyncResult result) { return request.EndGetRequestStream(result); }))
                {
                    long consumed = 0;
                    var buffer = new byte[BufferSize];
                    while (consumed < length)
                    {
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - consumed));
                        if (read == 0) throw new EndOfStreamException("上传输入短于声明长度。");
                        consumed += read;
                        output.Write(buffer, 0, read);
                        if (progress != null) progress(consumed);
                    }
                    if (input.ReadByte() != -1)
                    {
                        throw new IOException("上传输入长于声明长度。");
                    }
                }
                using (HttpWebResponse response = Await<HttpWebResponse>(request,
                    delegate(AsyncCallback callback) { return request.BeginGetResponse(callback, null); },
                    delegate(IAsyncResult result)
                    {
                        try { return (HttpWebResponse)request.EndGetResponse(result); }
                        catch (WebException ex)
                        {
                            var rejected = ex.Response as HttpWebResponse;
                            if (rejected != null) return rejected;
                            throw TransportError("PUT", ex);
                        }
                    }))
                {
                    // A 200/204 despite If-None-Match is not confirmed new ownership.
                    Require(response, "PUT", HttpStatusCode.Created);
                    string tag = response.Headers[HttpResponseHeader.ETag];
                    return StrongETag(tag) ? tag : null;
                }
            }
            catch (WebException ex) { request.Abort(); throw TransportError("PUT", ex); }
            catch { request.Abort(); throw; }
            // An interrupted/rejected PUT is ambiguous: never DELETE by UUID alone.
        }

        private T Await<T>(HttpWebRequest request, Func<AsyncCallback, IAsyncResult> begin, Func<IAsyncResult, T> end)
            where T : class, IDisposable
        {
            // Always observe End and dispose late results after Abort. Monitor avoids
            // a wait-handle or timer lifetime spanning the upload body.
            var gate = new object();
            bool complete = false, abandoned = false;
            T value = null;
            Exception failure = null;
            begin(delegate(IAsyncResult result)
            {
                T received = null;
                Exception caught = null;
                try { received = end(result); }
                catch (Exception ex) { caught = ex; }
                bool discard;
                lock (gate)
                {
                    discard = abandoned;
                    if (!discard) { value = received; failure = caught; complete = true; Monitor.PulseAll(gate); }
                }
                if (discard && received != null)
                {
                    try { received.Dispose(); }
                    catch (Exception) { } // A late aborted response must not crash a callback thread.
                }
            });
            bool timedOut;
            lock (gate)
            {
                if (!complete) Monitor.Wait(gate, timeout);
                timedOut = !complete;
                if (timedOut) abandoned = true;
            }
            if (timedOut)
            {
                request.Abort();
                throw new IOException("WebDAV PUT 连接或响应等待超时。");
            }
            if (failure != null)
            {
                var networkFailure = failure as WebException;
                if (networkFailure != null) throw TransportError("PUT", networkFailure);
                if (failure is IOException) throw new IOException(failure.Message);
                throw new IOException("WebDAV PUT 连接或响应失败。");
            }
            return value;
        }

        private MoveResult Move(string source, string destination, string etag)
        {
            HttpWebRequest request = Request(ObjectUri(source, false), "MOVE");
            request.Headers["Destination"] = ObjectUri(destination, false).AbsoluteUri;
            request.Headers["Overwrite"] = "F";
            if (StrongETag(etag)) request.Headers[HttpRequestHeader.IfMatch] = etag;
            using (HttpWebResponse response = Send(request))
                return new MoveResult { Status = response.StatusCode, ETag = response.Headers[HttpResponseHeader.ETag] };
        }

        private bool DeleteOwnedPartial(string path, string etag)
        {
            if (!IsPartial(path) || !StrongETag(etag)) return false;
            try
            {
                HttpWebRequest request = Request(ObjectUri(path, false), "DELETE");
                request.Headers[HttpRequestHeader.IfMatch] = etag;
                using (HttpWebResponse response = Send(request))
                    return response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.OK ||
                        response.StatusCode == HttpStatusCode.NotFound;
            }
            catch (IOException) { return false; }
        }

        private List<DavEntry> PropFind(string collection, int depth)
        {
            HttpWebRequest request = Request(ObjectUri(collection, true), "PROPFIND");
            request.Headers["Depth"] = depth.ToString(CultureInfo.InvariantCulture);
            request.ContentType = "application/xml; charset=utf-8";
            request.ContentLength = Properties.Length;
            try { using (Stream output = request.GetRequestStream()) output.Write(Properties, 0, Properties.Length); }
            catch (WebException ex) { request.Abort(); throw TransportError("PROPFIND", ex); }
            using (HttpWebResponse response = Send(request))
            {
                Require(response, "PROPFIND", (HttpStatusCode)207);
                var document = new XmlDocument { XmlResolver = null };
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = XmlLimit, MaxCharactersFromEntities = 1024,
                    IgnoreComments = true, IgnoreWhitespace = true
                };
                try
                {
                    using (Stream stream = response.GetResponseStream())
                    using (XmlReader reader = XmlReader.Create(stream, settings)) document.Load(reader);
                }
                catch (XmlException) { throw new IOException("WebDAV PROPFIND XML 无效或超过安全限制。"); }
                XmlElement multistatus = document.DocumentElement;
                if (multistatus == null || multistatus.LocalName != "multistatus" || multistatus.NamespaceURI != Dav)
                    throw new IOException("WebDAV PROPFIND 未返回 DAV 清单。");
                var namespaces = new XmlNamespaceManager(document.NameTable);
                namespaces.AddNamespace("d", Dav);
                var entries = new List<DavEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool self = false;
                foreach (XmlNode node in multistatus.SelectNodes("d:response", namespaces))
                {
                    if (entries.Count > maxObjects) throw new IOException("WebDAV 清单超过数量限制。");
                    XmlNode href = node.SelectSingleNode("d:href", namespaces);
                    if (href == null) throw new IOException("WebDAV 清单缺少路径。");
                    string path = RelativeHref(ObjectUri(collection, true), href.InnerText);
                    if (!seen.Add(path)) throw new IOException("WebDAV 清单包含重复路径。");
                    if (path != collection)
                    {
                        string prefix = collection.Length == 0 ? "" : collection + "/";
                        if (depth == 0 || !path.StartsWith(prefix, StringComparison.Ordinal) ||
                            path.Substring(prefix.Length).IndexOf('/') >= 0)
                            throw new IOException("WebDAV 清单超出请求深度。");
                    }
                    XmlNode status = node.SelectSingleNode("d:status", namespaces);
                    if (status != null && !SuccessfulStatus(status.InnerText)) throw new IOException("WebDAV 清单包含读取失败的对象。");
                    XmlNode resourceType = null, contentLength = null, entityTag = null;
                    foreach (XmlNode propstat in node.SelectNodes("d:propstat", namespaces))
                    {
                        XmlNode propertyStatus = propstat.SelectSingleNode("d:status", namespaces);
                        if (propertyStatus == null) throw new IOException("WebDAV 清单缺少属性状态。");
                        if (!SuccessfulStatus(propertyStatus.InnerText)) continue;
                        XmlNode prop = propstat.SelectSingleNode("d:prop", namespaces);
                        if (prop == null) throw new IOException("WebDAV 清单缺少属性。");
                        MergeProperty(ref resourceType, prop.SelectSingleNode("d:resourcetype", namespaces));
                        MergeProperty(ref contentLength, prop.SelectSingleNode("d:getcontentlength", namespaces));
                        MergeProperty(ref entityTag, prop.SelectSingleNode("d:getetag", namespaces));
                    }
                    if (resourceType == null) throw new IOException("WebDAV 清单无法确定对象类型。");
                    bool isCollection = resourceType.SelectSingleNode("d:collection", namespaces) != null;
                    long length = 0;
                    if (!isCollection && (contentLength == null ||
                        !Int64.TryParse(contentLength.InnerText, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0))
                        throw new IOException("WebDAV 清单文件长度无效。");
                    if (path == collection)
                    {
                        if (!isCollection) throw new IOException("WebDAV 保存位置不是目录。");
                        self = true;
                    }
                    entries.Add(new DavEntry
                    {
                        Path = path, Collection = isCollection,
                        Object = new RemoteObject { Path = path, Length = length, ETag = entityTag == null ? null : entityTag.InnerText }
                    });
                }
                if (!self) throw new IOException("WebDAV 清单缺少请求目录。");
                return entries;
            }
        }

        private static void MergeProperty(ref XmlNode property, XmlNode value)
        {
            if (value == null) return;
            if (property != null) throw new IOException("WebDAV 清单包含重复属性。");
            property = value;
        }

        private string RelativeHref(Uri collection, string href)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(href) || !Uri.TryCreate(collection, href, out uri) || !SameOrigin(uri) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new IOException("WebDAV 清单包含不安全的路径。");
            string[] segments = DecodeSegments(uri.AbsolutePath);
            if (segments.Length < rootSegments.Length) throw new IOException("WebDAV 清单路径越过保存位置。");
            for (int i = 0; i < rootSegments.Length; i++)
                if (segments[i] != rootSegments[i]) throw new IOException("WebDAV 清单路径越过保存位置。");
            string path = String.Join("/", segments, rootSegments.Length, segments.Length - rootSegments.Length);
            try { return StorageSettings.Relative(path, true); }
            catch (ArgumentException) { throw new IOException("WebDAV 清单包含不安全的相对路径。"); }
        }

        private static string[] DecodeSegments(string path)
        {
            path = path.Trim('/');
            if (path.Length == 0) return new string[0];
            string[] segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                var text = new StringBuilder();
                for (int j = 0; j < segments[i].Length; j++)
                {
                    if (segments[i][j] != '%') { text.Append(segments[i][j]); continue; }
                    var bytes = new List<byte>();
                    while (j < segments[i].Length && segments[i][j] == '%')
                    {
                        byte value;
                        if (j + 2 >= segments[i].Length || !Byte.TryParse(segments[i].Substring(j + 1, 2),
                            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
                            throw new IOException("WebDAV 路径转义无效。");
                        bytes.Add(value); j += 3;
                    }
                    j--;
                    try { text.Append(Utf8.GetString(bytes.ToArray())); }
                    catch (DecoderFallbackException) { throw new IOException("WebDAV 路径 UTF-8 编码无效。"); }
                }
                segments[i] = text.ToString();
                if (segments[i].Length == 0 || segments[i] == "." || segments[i] == ".." ||
                    segments[i].IndexOf('/') >= 0 || segments[i].IndexOf('\\') >= 0)
                    throw new IOException("WebDAV 路径段无效。");
                foreach (char c in segments[i]) if (Char.IsControl(c)) throw new IOException("WebDAV 路径包含控制字符。");
            }
            return segments;
        }

        private Uri ObjectUri(string path, bool collection)
        {
            StorageSettings.Relative(path, true);
            var encoded = new StringBuilder(root.AbsoluteUri);
            if (path.Length != 0)
            {
                string[] segments = path.Split('/');
                for (int i = 0; i < segments.Length; i++)
                {
                    if (i != 0) encoded.Append('/');
                    byte[] bytes = Utf8.GetBytes(segments[i]);
                    foreach (byte value in bytes)
                    {
                        if (value >= 'a' && value <= 'z' || value >= 'A' && value <= 'Z' ||
                            value >= '0' && value <= '9' || value == '-' || value == '_' || value == '.' || value == '~')
                            encoded.Append((char)value);
                        else encoded.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
                    }
                }
                if (collection) encoded.Append('/');
            }
            var uri = new Uri(encoded.ToString(), UriKind.Absolute);
            if (!SameOrigin(uri) || !uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal))
                throw new ArgumentException("WebDAV 路径越过保存位置。", "path");
            return uri;
        }

        private bool SameOrigin(Uri uri)
        {
            return uri.Scheme.Equals(root.Scheme, StringComparison.OrdinalIgnoreCase) &&
                uri.IdnHost.Equals(root.IdnHost, StringComparison.OrdinalIgnoreCase) && uri.Port == root.Port;
        }

        private HttpWebRequest Request(Uri uri, string method)
        {
            if (!SameOrigin(uri)) throw new IOException("拒绝跨站 WebDAV 请求。");
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = method;
            request.AllowAutoRedirect = false;
            request.Timeout = timeout;
            request.ReadWriteTimeout = readTimeout;
            request.UseDefaultCredentials = false;
            request.Credentials = credentials;
            request.PreAuthenticate = true;
            request.Proxy = null;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            return request;
        }

        private static HttpWebResponse Send(HttpWebRequest request)
        {
            try { return (HttpWebResponse)request.GetResponse(); }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null) return response;
                request.Abort();
                throw TransportError(request.Method, ex);
            }
        }

        private static IOException TransportError(string method, WebException error)
        {
            if (error.Response != null) error.Response.Close();
            return new IOException("WebDAV " + method + " 请求失败（" + error.Status + "）。");
        }

        private static void Require(HttpWebResponse response, string method, params HttpStatusCode[] statuses)
        {
            foreach (HttpStatusCode status in statuses) if (response.StatusCode == status) return;
            throw StatusError(method, response.StatusCode);
        }

        private static IOException StatusError(string method, HttpStatusCode status)
        {
            return new IOException("WebDAV " + method + " 请求失败（HTTP " + ((int)status).ToString(CultureInfo.InvariantCulture) + "）。");
        }

        private static bool IsDefiniteRejection(HttpStatusCode status)
        {
            int code = (int)status;
            return code >= 400 && code < 500 || status == HttpStatusCode.NotImplemented;
        }

        private static bool SuccessfulStatus(string status)
        {
            string[] parts = status.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int code;
            return parts.Length >= 2 && parts[0].StartsWith("HTTP/", StringComparison.Ordinal) &&
                Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out code) && code >= 200 && code < 300;
        }

        private static bool StrongETag(string tag)
        {
            if (String.IsNullOrEmpty(tag) || tag.Length < 2 || tag[0] != '"' || tag[tag.Length - 1] != '"') return false;
            for (int i = 1; i < tag.Length - 1; i++)
                if (tag[i] != 0x21 && !(tag[i] >= 0x23 && tag[i] <= 0x7e) && !(tag[i] >= 0x80 && tag[i] <= 0xff)) return false;
            return true;
        }

        private static string PartialName() { return PartialPrefix + Guid.NewGuid().ToString("N") + ".partial"; }

        private static bool IsPartial(string path)
        {
            string name = path.Substring(path.LastIndexOf('/') + 1);
            if (!name.StartsWith(PartialPrefix, StringComparison.Ordinal) || !name.EndsWith(".partial", StringComparison.Ordinal)) return false;
            Guid id;
            return Guid.TryParseExact(name.Substring(PartialPrefix.Length, name.Length - PartialPrefix.Length - ".partial".Length), "N", out id);
        }

        private static string SafeError(Exception error)
        {
            if ((error is IOException || error is NotSupportedException) &&
                error.Message.StartsWith("WebDAV ", StringComparison.Ordinal)) return error.Message;
            return "WebDAV 测试失败（" + error.GetType().Name + "）。";
        }

        private sealed class DavEntry
        {
            public string Path;
            public bool Collection;
            public RemoteObject Object;
        }
        private sealed class MoveResult
        {
            public HttpStatusCode Status;
            public string ETag;
        }

        private sealed class ResponseStream : Stream
        {
            private Stream stream;
            private HttpWebResponse response;
            public ResponseStream(Stream stream, HttpWebResponse response) { this.stream = stream; this.response = response; }
            public override bool CanRead { get { return stream != null && stream.CanRead; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override bool CanTimeout { get { return stream != null && stream.CanTimeout; } }
            public override int ReadTimeout { get { return Active.ReadTimeout; } set { Active.ReadTimeout = value; } }
            public override long Length { get { if (response == null) throw new ObjectDisposedException("WebDAV stream"); if (response.ContentLength < 0) throw new NotSupportedException(); return response.ContentLength; } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            private Stream Active { get { if (stream == null) throw new ObjectDisposedException("WebDAV stream"); return stream; } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                try { return Active.Read(buffer, offset, count); }
                catch (WebException ex) { throw TransportError("GET", ex); }
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing)
            {
                if (disposing && stream != null)
                {
                    try { stream.Dispose(); }
                    finally { stream = null; response.Close(); response = null; }
                }
                base.Dispose(disposing);
            }
        }
    }
}
