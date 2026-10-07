using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace PhotoImportV2
{
    // Loopback TCP HTTP fixtures need no URL ACL, cloud account, credentials,
    // user files or SDK. The mock independently verifies every wire signature.
    public static class S3StoreTests
    {
        private const string Access = "AKIDOFFLINE";
        private const string Secret = "offline-secret-never-a-real-credential";
        private const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static string Run()
        {
            var passed = new List<string>();
            Test(passed, "four published AWS SigV4 vectors", SigningVectors);
            Test(passed, "path-style Unicode escaping, HEAD, GET and opaque ETags", RoundTrip);
            Test(passed, "ListObjectsV2 encoded keys and opaque-token pagination", Pagination);
            Test(passed, "pagination loops and missing tokens fail", PaginationFailures);
            Test(passed, "XML DTD, wrong root and out-of-prefix keys fail", UnsafeXml);
            Test(passed, "GET streams immediately and disposal closes the response", Streaming);
            Test(passed, "truncated GET cannot pass streamed verification", TruncatedRead);
            Test(passed, "single PUT is CREATE_NEW and preserves input ownership", SinglePut);
            Test(passed, "ignored PUT preconditions disable target writes", IgnoredPut);
            Test(passed, "64 MiB boundary stays a single PUT", SingleBoundary);
            Test(passed, "bounded sequential multipart, progress and full streamed readback", MultipartSuccess);
            Test(passed, "multipart completion race never clobbers and aborts only own ID", MultipartRace);
            Test(passed, "unsupported and ignored conditional completion fail closed", UnsupportedMultipart);
            Test(passed, "part failure aborts only this upload", PartFailure);
            Test(passed, "503 UploadPart retries same immutable part then succeeds", PartRetry);
            Test(passed, "transient connection loss retries only the owned part", NetworkPartRetry);
            Test(passed, "permanent 503 part failure stops after three attempts and aborts", PartRetryExhaustion);
            Test(passed, "probe multipart parts are not retried", ProbePartFailure);
            Test(passed, "HTTP 200 embedded completion errors trigger abort", EmbeddedErrors);
            Test(passed, "409 completion conflicts never retry unconditionally", CompletionConflict);
            Test(passed, "short, long and interrupted inputs never publish", InputFailures);
            Test(passed, "progress interruption aborts multipart", ProgressFailure);
            Test(passed, "abort failure reports retained multipart", AbortFailure);
            Test(passed, "read-only probe performs no writes", ReadOnlyProbe);
            Test(passed, "write probe verifies conditions and cleans owned fixture", WriteProbe);
            Test(passed, "unsupported cleanup and fixture replacement fail safely", CleanupFailures);
            Test(passed, "missing credentials are actionable and never hit network", MissingCredentials);
            Test(passed, "authorization failures do not expose credentials", AuthFailure);
            Test(passed, "cross-origin redirects never forward credentials", Redirects);
            Test(passed, "header and streaming stalls have bounded timeouts", Timeouts);
            Test(passed, "active GET and PUT outlive connection timeout with wire progress", ActiveTransfers);
            Test(passed, "slow multipart wire writes keep cumulative progress alive", MultipartActiveProgress);
            Test(passed, "invalid relative names and lengths never hit network", InvalidInputs);
            return "PASS: " + passed.Count.ToString(CultureInfo.InvariantCulture) + " S3 offline fixture tests: " + String.Join("; ", passed.ToArray()) + ".";
        }

#if S3_STORE_TEST_RUNNER
        public static int Main()
        {
            try { Console.WriteLine(Run()); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
#endif

        private static void Test(List<string> passed, string name, Action action)
        {
            try { action(); passed.Add(name); }
            catch (Exception error) { throw new InvalidOperationException("S3 test failed: " + name, error); }
        }

        private static void SigningVectors()
        {
            const string access = "AKIAIOSFODNN7EXAMPLE";
            const string secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
            // https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-header-based-auth.html
            var headers = new Dictionary<string, string> { { "Host", "examplebucket.s3.amazonaws.com" },
                { "x-amz-date", "20130524T000000Z" }, { "x-amz-content-sha256", S3Store.EmptyHash }, { "Range", "bytes=0-9" } };
            CheckVector("GET", "/test.txt", "", headers, S3Store.EmptyHash, access, secret,
                "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");
            headers.Remove("Range");
            CheckVector("GET", "/", "lifecycle=", headers, S3Store.EmptyHash, access, secret,
                "fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543");
            CheckVector("GET", "/", "max-keys=2&prefix=J", headers, S3Store.EmptyHash, access, secret,
                "34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7");
            const string payload = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072";
            headers["x-amz-content-sha256"] = payload;
            headers.Add("Date", "Fri, 24 May 2013 00:00:00 GMT");
            headers.Add("x-amz-storage-class", "REDUCED_REDUNDANCY");
            CheckVector("PUT", "/test%24file.text", "", headers, payload, access, secret,
                "98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd");
        }

        private static void CheckVector(string method, string path, string query, IDictionary<string, string> headers,
            string payload, string access, string secret, string expected)
        { Assert(S3Store.Sign(method, path, query, headers, payload, access, secret, "us-east-1").EndsWith("Signature=" + expected, StringComparison.Ordinal), "Published AWS signature mismatch."); }

        private static void RoundTrip()
        {
            using (var server = new MockServer())
            {
                var store = Store(server, "照片 +%/~");
                byte[] bytes = Utf8.GetBytes("a photo fixture");
                string relative = "a+b %?#&/图像.jpg";
                using (var input = new MemoryStream(bytes, false)) Assert(store.UploadNew(relative, input, bytes.Length, null), "New object was not created.");
                RemoteObject stat = store.Stat(relative);
                Assert(stat.Length == bytes.Length && stat.Path == relative && stat.ETag == server.Object("照片 +%/~/" + relative).ETag, "HEAD metadata mismatch.");
                Assert(stat.ETag.IndexOf("opaque", StringComparison.Ordinal) >= 0, "ETag was treated as a content hash.");
                using (var read = store.OpenRead(relative)) Assert(Equal(ReadAll(read), bytes), "GET mismatch.");
                Assert(store.Stat("missing.jpg") == null, "404 HEAD must mean absent.");
                Assert(Fails(delegate { store.OpenRead("missing.jpg"); }).Message.IndexOf("404", StringComparison.Ordinal) >= 0, "GET 404 must fail.");
                Assert(server.HasRawPath("/gateway/photos/%E7%85%A7%E7%89%87%20%2B%25/~" + "/a%2Bb%20%25%3F%23%26/%E5%9B%BE%E5%83%8F.jpg"), "Incorrect path escaping.");
            }
        }

        private static void Pagination()
        {
            using (var server = new MockServer())
            {
                server.Pages = new[] {
                    Page("<Contents><Key>root%2Fa%2Bb%20%25%26.jpg</Key><Size>7</Size><ETag>&quot;opaque&quot;</ETag></Contents><Contents><Key>root%2Ffolder%2F</Key><Size>0</Size></Contents>", true, "opaque+/%=&token"),
                    Page("<Contents><Key>root%2F%E5%9B%BE.jpg</Key><Size>9</Size></Contents>", false, null)
                };
                var items = new List<RemoteObject>(Store(server, "root").List());
                Assert(items.Count == 2 && items[0].Path == "a+b %&.jpg" && items[1].Path == "图.jpg", "Listing decoding or prefix handling failed.");
                Assert(server.Requests.Count == 2 && server.Requests[1].Query["continuation-token"] == "opaque+/%=&token", "Continuation token was decoded or altered.");
                Assert(server.Requests[0].Query["prefix"] == "root/" && server.Requests[0].Query["encoding-type"] == "url", "Listing prefix/encoding missing.");
            }
        }

        private static void PaginationFailures()
        {
            using (var server = new MockServer())
            {
                server.Pages = new[] { Page("", true, "same"), Page("", true, "same") };
                Fails(delegate { new List<RemoteObject>(Store(server, "").List()); });
                Assert(server.Requests.Count == 2, "Pagination loop was not bounded.");
            }
            using (var server = new MockServer())
            {
                server.Pages = new[] { Page("", true, null) };
                Fails(delegate { new List<RemoteObject>(Store(server, "").List()); });
            }
        }

        private static void UnsafeXml()
        {
            foreach (string xml in new[] {
                "<!DOCTYPE x [<!ENTITY secret SYSTEM 'http://127.0.0.1:1/never'>]><ListBucketResult><IsTruncated>false</IsTruncated><Name>&secret;</Name></ListBucketResult>",
                "<Unrelated><IsTruncated>false</IsTruncated></Unrelated>",
                Page("<Contents><Key>outside.jpg</Key><Size>1</Size></Contents>", false, null),
                Page("<Contents><Key>root%2F..%2Foutside.jpg</Key><Size>1</Size></Contents>", false, null),
                Page("<Contents><Key>root%2F%FF.jpg</Key><Size>1</Size></Contents>", false, null),
                Page("<Contents><Key>root%2F%G0.jpg</Key><Size>1</Size></Contents>", false, null) })
            {
                using (var server = new MockServer())
                { server.Pages = new[] { xml }; Fails(delegate { new List<RemoteObject>(Store(server, "root").List()); }); }
            }
        }

        private static void Streaming()
        {
            using (var server = new MockServer())
            {
                server.StreamKey = "stream.jpg";
                var clock = Stopwatch.StartNew();
                Stream read = Store(server, "").OpenRead("stream.jpg");
                Assert(clock.ElapsedMilliseconds < 2000 && read.ReadByte() == 7, "OpenRead buffered the entire response.");
                read.Dispose(); read.Dispose();
                Assert(server.StreamClosed.WaitOne(2000), "Disposed GET did not close the underlying connection.");
                Fails(delegate { read.ReadByte(); });
            }
        }

        private static void SinglePut()
        {
            using (var server = new MockServer())
            {
                var store = Store(server, "");
                byte[] first = Utf8.GetBytes("original");
                using (var input = new MemoryStream(first, false))
                {
                    long progress = 0;
                    Assert(store.UploadNew("photo.jpg", input, first.Length, delegate(long count) { progress = count; }), "PUT failed.");
                    Assert(input.CanRead && progress == first.Length, "Input ownership or progress wrong.");
                }
                byte[] other = Utf8.GetBytes("replacement");
                using (var input = new MemoryStream(other, false)) Assert(!store.UploadNew("photo.jpg", input, other.Length, null), "Existing object was accepted.");
                Assert(Equal(server.Object("photo.jpg").Bytes, first), "PUT overwrote an existing object.");
                using (var empty = new MemoryStream()) Assert(store.UploadNew("empty.jpg", empty, 0, null), "Empty upload failed.");
                Assert(server.Object("empty.jpg").Length == 0, "Empty length wrong.");
                Assert(server.ObjectDeletesFor("photo.jpg") == 0, "Existing user object was deleted.");
            }
        }

        private static void TruncatedRead()
        {
            using (var server = new MockServer())
            {
                server.TruncatedGet = true;
                using (var read = Store(server, "").OpenRead("truncated.jpg"))
                    Fails(delegate { ReadAll(read); });
            }
        }

        private static void IgnoredPut()
        {
            using (var server = new MockServer())
            {
                server.IgnorePutCondition = true;
                server.Seed("photo.jpg", Utf8.GetBytes("keep"));
                using (var input = new MemoryStream(new byte[1]))
                    Assert(Fails(delegate { Store(server, "").UploadNew("photo.jpg", input, 1, null); }).Message.Contains("ignored If-None-Match"), "Ignored condition was not detected.");
                Assert(server.WritesFor("photo.jpg") == 0 && server.Object("photo.jpg").Bytes[0] == (byte)'k', "Unsafe target request escaped capability guard.");
            }
        }

        private static void SingleBoundary()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold))
            {
                Assert(Store(server, "").UploadNew("boundary.jpg", input, input.Length, null), "Boundary upload failed.");
                Assert(server.TargetPartCount("boundary.jpg") == 0 && server.WritesFor("boundary.jpg") == 1, "64 MiB must use single PUT.");
                Assert(input.MaximumRead <= 65536, "Input consumption was not incremental.");
            }
        }

        private static void MultipartSuccess()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 12345))
            {
                long last = 0; int updates = 0;
                var store = Store(server, "");
                Assert(store.UploadNew("large.jpg", input, input.Length, delegate(long count) {
                    Assert(count > last && count - last <= 65536, "Progress must be incremental and cumulative."); last = count; updates++;
                }), "Multipart failed.");
                Assert(last == input.Length && updates > 1000 && input.MaximumRead <= 65536, "Multipart progress/buffering wrong.");
                Assert(server.TargetPartCount("large.jpg") == 5 && server.MaxPartLength <= S3Store.PartBytes, "Multipart part count or bounds wrong.");
                Assert(server.Object("large.jpg").Length == input.Length && server.Object("large.jpg").ETag.Contains("-mpu"), "Multipart result wrong.");
                using (var actual = store.OpenRead("large.jpg"))
                using (var expected = new PatternStream(input.Length))
                using (var sha = SHA256.Create())
                {
                    byte[] actualHash = sha.ComputeHash(actual);
                    Assert(Equal(actualHash, sha.ComputeHash(expected)), "Full streamed readback SHA256 mismatch.");
                }
                Assert(server.AbortsFor("large.jpg") == 0, "Completed upload was aborted.");
            }
        }

        private static void MultipartRace()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.RaceKey = "large.jpg";
                Assert(!Store(server, "").UploadNew("large.jpg", input, input.Length, null), "Completion collision must return false.");
                Assert(Equal(server.Object("large.jpg").Bytes, Utf8.GetBytes("racing-original")), "Completion clobbered the racing original.");
                Assert(server.AbortsFor("large.jpg") == 1 && server.ForeignUploadPresent, "Abort must affect only its own ID.");
                Assert(server.ObjectDeletesFor("large.jpg") == 0, "Collision deleted a user object.");
            }
        }

        private static void UnsupportedMultipart()
        {
            foreach (bool ignore in new[] { false, true })
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.IgnoreMultipartCondition = ignore;
                server.RejectMultipartCondition = !ignore;
                Fails(delegate { Store(server, "").UploadNew("large.jpg", input, input.Length, null); });
                Assert(server.WritesFor("large.jpg") == 0 && input.Position == 0, "Unsupported endpoint received user data.");
                Assert(server.ForeignUploadPresent, "Capability failure aborted a foreign ID.");
            }
        }

        private static void PartFailure()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.FailPart = 2;
                Fails(delegate { Store(server, "").UploadNew("large.jpg", input, input.Length, null); });
                Assert(server.ObjectOrNull("large.jpg") == null && server.AbortsFor("large.jpg") == 1 && server.ForeignUploadPresent, "Part failure publication/abort wrong.");
            }
        }

        private static void EmbeddedErrors()
        {
            foreach (string code in new[] { "InternalError", "PreconditionFailed" })
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.EmbeddedError = code;
                if (code == "PreconditionFailed") Assert(!Store(server, "").UploadNew("large.jpg", input, input.Length, null), "Embedded precondition must return false.");
                else Fails(delegate { Store(server, "").UploadNew("large.jpg", input, input.Length, null); });
                Assert(server.AbortsFor("large.jpg") == 1 && server.ObjectOrNull("large.jpg") == null, "Embedded error did not abort.");
            }
        }

        private static void PartRetry()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.FailPart = 2; server.PartStatus = 503; server.PartFailuresRemaining = 1;
                long previous = 0; int repeats = 0;
                Assert(Store(server, "").UploadNew("large.jpg", input, input.Length, delegate(long count) {
                    Assert(count >= previous && count - previous <= 65536, "Retry progress decreased or double-counted bytes.");
                    if (count == previous) repeats++;
                    previous = count;
                }), "Transient part retry did not succeed.");
                Assert(previous == input.Length && repeats >= 256 && server.TargetPartCount("large.jpg") == 6, "Retry progress or attempt count wrong.");
                var attempts = new List<Request>();
                foreach (Request request in server.Requests)
                    if (request.Key == "large.jpg" && request.Method == "PUT" && request.Query["partNumber"] == "2") attempts.Add(request);
                Assert(attempts.Count == 2 && attempts[0].Query["uploadId"] == attempts[1].Query["uploadId"] && attempts[0].BodyHash == attempts[1].BodyHash && attempts[0].Length == attempts[1].Length,
                    "Retry changed the upload ID, part number, length or immutable payload.");
                Assert(server.AbortsFor("large.jpg") == 0 && server.ForeignUploadPresent && input.Position == input.Length, "Successful retry aborted or reread the source.");
            }
        }

        private static void PartRetryExhaustion()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.FailPart = 1; server.PartStatus = 503;
                Fails(delegate { Store(server, "").UploadNew("large.jpg", input, input.Length, null); });
                Assert(server.TargetPartCount("large.jpg") == 3 && server.AbortsFor("large.jpg") == 1 && server.ObjectOrNull("large.jpg") == null && server.ForeignUploadPresent,
                    "Permanent failure exceeded the retry budget or did not abort only its own upload.");
                Assert(input.Position == S3Store.PartBytes, "Part retry consumed the source again.");
            }
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.FailPart = 1; server.PartStatus = 403;
                Fails(delegate { Store(server, "").UploadNew("large.jpg", input, input.Length, null); });
                Assert(server.TargetPartCount("large.jpg") == 1 && server.AbortsFor("large.jpg") == 1, "Authorization failure was retried.");
            }
        }

        private static void NetworkPartRetry()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.DropPartReplies = 1;
                Assert(Store(server, "").UploadNew("large.jpg", input, input.Length, null), "Transient connection loss did not retry.");
                Assert(server.TargetPartCount("large.jpg") == 6 && server.AbortsFor("large.jpg") == 0 && server.ForeignUploadPresent && input.Position == input.Length,
                    "Network retry restarted the file, aborted a successful upload or consumed source twice.");
            }
        }

        private static void ProbePartFailure()
        {
            using (var server = new MockServer())
            {
                server.FailPart = 1; server.PartStatus = 503; server.FailFixturePart = true;
                Assert(!Store(server, "").Probe(true).Success, "Failed probe part was accepted.");
                int attempts = 0;
                foreach (Request request in server.Requests) if (request.Method == "PUT" && request.Query.ContainsKey("partNumber")) attempts++;
                Assert(attempts == 1 && server.ObjectCount == 0 && server.ForeignUploadPresent, "Probe part was retried or fixture cleanup touched other data.");
            }
        }

        private static void CompletionConflict()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.EmbeddedError = "ConditionalRequestConflict";
                Assert(Fails(delegate { Store(server, "").UploadNew("large.jpg", input, input.Length, null); }).Message.Contains("409"), "409 must remain a failure.");
                Assert(server.CompletionsFor("large.jpg") == 1 && server.AbortsFor("large.jpg") == 1, "Conflict caused unsafe completion retries.");
            }
        }

        private static void InputFailures()
        {
            foreach (long declared in new long[] { 1, 3 })
            using (var server = new MockServer())
            using (var input = new MemoryStream(new byte[2]))
            {
                Fails(delegate { Store(server, "").UploadNew("bad.jpg", input, declared, null); });
                Assert(server.WritesFor("bad.jpg") == 0, "Mismatched small input was sent.");
            }
            foreach (long delta in new long[] { -1, 1 })
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1 + delta))
            {
                Fails(delegate { Store(server, "").UploadNew("large.jpg", input, S3Store.MultipartThreshold + 1, null); });
                Assert(server.ObjectOrNull("large.jpg") == null && server.AbortsFor("large.jpg") == 1, "Mismatched multipart published.");
            }
        }

        private static void ProgressFailure()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                Fails(delegate { Store(server, "").UploadNew("large.jpg", input, input.Length, delegate(long count) {
                    if (count > S3Store.PartBytes) throw new IOException("synthetic progress interruption");
                }); });
                Assert(server.AbortsFor("large.jpg") == 1 && server.ObjectOrNull("large.jpg") == null && input.CanRead, "Interruption did not preserve source ownership or abort.");
            }
        }

        private static void AbortFailure()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                server.FailPart = 1; server.FailAbort = true;
                Exception error = Fails(delegate { Store(server, "").UploadNew("large.jpg", input, input.Length, null); });
                Assert(error.Message.Contains("incomplete upload may remain") && server.ForeignUploadPresent, "Retained upload failure not actionable.");
            }
        }

        private static void ReadOnlyProbe()
        {
            using (var server = new MockServer())
            {
                WorkResult result = Store(server, "").Probe(false);
                Assert(result.Success && result.Detail.Contains("未验证"), "Read-only capabilities overstated.");
                Assert(server.Requests.Count == 1 && server.Requests[0].Method == "GET", "Read-only probe made writes.");
            }
        }

        private static void WriteProbe()
        {
            using (var server = new MockServer())
            {
                server.Seed("keep.jpg", Utf8.GetBytes("keep"));
                WorkResult result = Store(server, "").Probe(true);
                Assert(result.Success, result.Error ?? "Probe failed.");
                Assert(server.ObjectCount == 1 && server.ObjectDeletesFor("keep.jpg") == 0 && server.ForeignUploadPresent, "Probe leaked a fixture or touched user data.");
                Assert(result.Detail.Contains("分片条件冲突拒绝"), "Probe capabilities are unclear.");
            }
        }

        private static void CleanupFailures()
        {
            using (var server = new MockServer())
            {
                server.RejectDeleteCondition = true;
                WorkResult result = Store(server, "").Probe(true);
                Assert(!result.Success && result.Error.Contains("Probe fixture may remain") && server.ObjectCount == 1, "Unsupported cleanup must retain owned fixture and report it.");
            }
            using (var server = new MockServer())
            {
                server.SwapOnCleanup = true;
                WorkResult result = Store(server, "").Probe(true);
                Assert(!result.Success && server.ObjectCount == 1 && server.AnyObject().Owner == "foreign", "Changed fixture was deleted.");
            }
            using (var server = new MockServer())
            {
                server.IgnoreDeleteCondition = true;
                Assert(!Store(server, "").Probe(true).Success && server.Requests.Count < 10, "Ignored cleanup must disable uploads.");
            }
        }

        private static void MissingCredentials()
        {
            using (var server = new MockServer())
            {
                var store = new S3Store(Destination(server, ""), delegate { return null; }, 3000, 1000);
                WorkResult result = store.Probe(false);
                Assert(!result.Success && result.Error.Contains("凭据管理器") && result.Error.Contains("Access Key") && server.Requests.Count == 0, "Missing credentials failure wrong.");
            }
        }

        private static void AuthFailure()
        {
            using (var server = new MockServer())
            {
                server.DenyAuth = true;
                WorkResult result = Store(server, "").Probe(false);
                Assert(!result.Success && result.Error.Contains("403") && !result.Error.Contains(Secret) && !result.Error.Contains(Access), "Credential data leaked in failure.");
                Assert(server.Requests.Count == 1, "Auth failure caused retries.");
            }
        }

        private static void Redirects()
        {
            using (var target = new MockServer())
            using (var server = new MockServer())
            {
                server.Redirect = target.Endpoint + "/photos/leak.jpg";
                Fails(delegate { Store(server, "").OpenRead("redirect.jpg"); });
                Assert(target.Requests.Count == 0, "Redirect forwarded a signed request across origins.");
            }
        }

        private static void Timeouts()
        {
            using (var server = new MockServer())
            {
                server.StallHeaders = true;
                var clock = Stopwatch.StartNew();
                Assert(!new S3Store(Destination(server, ""), Keys, 250, 250).Probe(false).Success, "Header stall succeeded.");
                Assert(clock.ElapsedMilliseconds < 3000, "Header timeout was not bounded.");
            }
            using (var server = new MockServer())
            {
                server.StreamKey = "stream.jpg";
                using (var read = new S3Store(Destination(server, ""), Keys, 500, 250).OpenRead("stream.jpg"))
                {
                    Assert(read.ReadByte() == 7, "Streaming first byte wrong.");
                    var clock = Stopwatch.StartNew();
                    Fails(delegate { read.ReadByte(); });
                    Assert(clock.ElapsedMilliseconds < 3000, "Streaming idle timeout was not bounded.");
                }
            }
        }

        private static void InvalidInputs()
        {
            using (var server = new MockServer())
            {
                var store = Store(server, "root");
                foreach (string name in new[] { "../a", "/a", "a//b", "a\\b", "a/./b", "a\n.jpg", "a:b", "", new string('a', 1024), new string('图', 400) })
                    Fails(delegate { store.Stat(name); });
                using (var input = new MemoryStream())
                {
                    Fails(delegate { store.UploadNew("a.jpg", input, -1, null); });
                    Fails(delegate { store.UploadNew("a.jpg", input, (long)S3Store.PartBytes * 10000 + 1, null); });
                }
                Assert(server.Requests.Count == 0, "Invalid inputs caused network requests.");
            }
        }

        private static void ActiveTransfers()
        {
            using (var server = new MockServer())
            {
                server.ActiveGet = true;
                using (var read = new S3Store(Destination(server, ""), Keys, 350, 250).OpenRead("active.jpg"))
                {
                    var clock = Stopwatch.StartNew();
                    for (int i = 0; i < 20; i++) Assert(read.ReadByte() == Pattern(i), "Active read data wrong.");
                    Assert(read.ReadByte() == -1 && clock.ElapsedMilliseconds > 350, "Fixture did not outlive the connection timeout.");
                }
            }
            using (var server = new MockServer())
            using (var input = new PatternStream(1024 * 1024))
            {
                long previous = 0; int updates = 0;
                var clock = Stopwatch.StartNew();
                var store = new S3Store(Destination(server, ""), Keys, 350, 250);
                Assert(store.UploadNew("active.jpg", input, input.Length, delegate(long count) {
                    Assert(input.Position == input.Length && count > previous && count - previous <= 65536, "Progress preceded buffered source consumption or decreased.");
                    previous = count; updates++; Thread.Sleep(40);
                }), "Actively progressing slow upload timed out.");
                Assert(previous == input.Length && updates == 16 && clock.ElapsedMilliseconds > 350, "Wire progress did not cover the slow upload.");
            }
        }

        private static void MultipartActiveProgress()
        {
            using (var server = new MockServer())
            using (var input = new PatternStream(S3Store.MultipartThreshold + 1))
            {
                long previous = 0; int firstPartUpdates = 0;
                var store = new S3Store(Destination(server, ""), Keys, 350, 250);
                Assert(store.UploadNew("large.jpg", input, input.Length, delegate(long count) {
                    Assert(count > previous && count - previous <= 65536, "Wire progress decreased or skipped an increment.");
                    long partEnd = Math.Min(input.Length, ((count - 1) / S3Store.PartBytes + 1) * S3Store.PartBytes);
                    Assert(input.Position == partEnd, "Progress came from reading a part instead of sending it.");
                    previous = count;
                    if (count <= S3Store.PartBytes) { firstPartUpdates++; Thread.Sleep(4); }
                }), "Slow active multipart part timed out.");
                Assert(firstPartUpdates == 256 && previous == input.Length, "Progress did not span all first-part network writes.");
            }
        }

        private static S3Store Store(MockServer server, string prefix)
        { return new S3Store(Destination(server, prefix), Keys, 15000, 3000); }
        private static NetworkCredential Keys(DestinationRecord ignored) { return new NetworkCredential(Access, Secret); }
        private static DestinationRecord Destination(MockServer server, string prefix)
        { return new DestinationRecord { Name = "Offline S3", Type = "s3", Endpoint = server.Endpoint, Bucket = "photos", Region = "us-east-1", Prefix = prefix, CredentialTarget = "offline-fixture" }; }
        private static string Page(string contents, bool truncated, string token)
        { return "<ListBucketResult xmlns=\"" + Namespace + "\"><EncodingType>url</EncodingType>" + contents + "<IsTruncated>" + (truncated ? "true" : "false") + "</IsTruncated>" + (token == null ? "" : "<NextContinuationToken>" + Escape(token) + "</NextContinuationToken>") + "</ListBucketResult>"; }
        private static string Escape(string value) { return System.Security.SecurityElement.Escape(value); }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static Exception Fails(Action action)
        { try { action(); } catch (Exception error) { return error; } throw new InvalidOperationException("Expected operation to fail."); }
        private static bool Equal(byte[] a, byte[] b)
        { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        private static byte[] ReadAll(Stream input)
        { using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); } }
        private static byte Pattern(long position) { return (byte)((position * 31 + 7) % 251); }

        private sealed class PatternStream : Stream
        {
            private readonly long length;
            private long position;
            internal int MaximumRead;
            internal PatternStream(long length) { this.length = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                MaximumRead = Math.Max(MaximumRead, count);
                int read = (int)Math.Min(count, length - position);
                for (int i = 0; i < read; i++) buffer[offset + i] = Pattern(position + i);
                position += read; return read;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return length; } }
            public override long Position { get { return position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        private sealed class StoredObject
        {
            internal byte[] Bytes;
            internal long Length;
            internal string ETag;
            internal string Owner;
        }
        private sealed class Upload
        {
            internal string Key;
            internal string Owner;
            internal readonly List<StoredObject> Parts = new List<StoredObject>();
        }
        private sealed class Request
        {
            internal string Method;
            internal string RawPath;
            internal string RawQuery;
            internal string Key;
            internal long Length;
            internal byte[] Body;
            internal string BodyHash;
            internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private sealed class MockServer : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread thread;
            private readonly object gate = new object();
            private readonly List<TcpClient> clients = new List<TcpClient>();
            private readonly Dictionary<string, StoredObject> objects = new Dictionary<string, StoredObject>(StringComparer.Ordinal);
            private readonly Dictionary<string, Upload> uploads = new Dictionary<string, Upload>(StringComparer.Ordinal);
            private readonly ManualResetEvent stopping = new ManualResetEvent(false);
            private readonly List<Exception> errors = new List<Exception>();
            internal readonly ManualResetEvent StreamClosed = new ManualResetEvent(false);
            internal readonly List<Request> Requests = new List<Request>();
            internal readonly string Endpoint;
            internal string[] Pages;
            internal bool IgnorePutCondition;
            internal bool IgnoreMultipartCondition;
            internal bool RejectMultipartCondition;
            internal bool RejectDeleteCondition;
            internal bool IgnoreDeleteCondition;
            internal bool SwapOnCleanup;
            internal bool DenyAuth;
            internal bool StallHeaders;
            internal bool ActiveGet;
            internal bool TruncatedGet;
            internal bool FailAbort;
            internal int FailPart;
            internal int PartStatus = 500;
            internal int PartFailuresRemaining = -1;
            internal int DropPartReplies;
            internal bool FailFixturePart;
            internal int MaxPartLength;
            internal string RaceKey;
            internal string EmbeddedError;
            internal string StreamKey;
            internal string Redirect;
            private int pageIndex;
            private int serial;

            internal MockServer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                Endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/gateway";
                uploads.Add("foreign-upload", new Upload { Key = "large.jpg", Owner = "foreign" });
                thread = new Thread(Accept); thread.IsBackground = true; thread.Start();
            }

            private void Accept()
            {
                while (!stopping.WaitOne(0))
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                    lock (gate) clients.Add(client);
                    ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                }
            }

            private void Handle(TcpClient client)
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 10000; stream.WriteTimeout = 10000;
                        Request request = ReadRequest(stream);
                        lock (gate) Requests.Add(request);
                        VerifySignature(request);
                        if (request.Method == "PUT" && request.Key == "large.jpg" && request.Query.ContainsKey("partNumber") && DropPartReplies > 0)
                        { DropPartReplies--; return; }
                        if (StallHeaders) { stopping.WaitOne(5000); return; }
                        if (DenyAuth) { Error(stream, 403, "AccessDenied", Secret); return; }
                        if (TruncatedGet && request.Key == "truncated.jpg" && request.Method == "GET")
                        { Header(stream, 200, 10, null); stream.Write(new byte[3], 0, 3); return; }
                        if (ActiveGet && request.Key == "active.jpg" && request.Method == "GET")
                        {
                            Header(stream, 200, 20, null);
                            for (int i = 0; i < 20; i++) { stream.WriteByte(Pattern(i)); stream.Flush(); Thread.Sleep(50); }
                            return;
                        }
                        if (request.Key == "redirect.jpg" && Redirect != null)
                        { Reply(stream, 302, new byte[0], new Dictionary<string, string> { { "Location", Redirect } }); return; }
                        if (request.Key == StreamKey && request.Method == "GET")
                        {
                            Header(stream, 200, 10, null); stream.WriteByte(7); stream.Flush();
                            try { if (stream.ReadByte() == -1) StreamClosed.Set(); }
                            catch (IOException) { StreamClosed.Set(); }
                            return;
                        }
                        lock (gate) Dispatch(stream, request);
                    }
                }
                catch (IOException) { if (!stopping.WaitOne(0) && StreamKey == null) { /* client aborted or timed out */ } }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (Exception error) { lock (gate) errors.Add(error); }
                finally { lock (gate) clients.Remove(client); }
            }

            private void Dispatch(NetworkStream stream, Request request)
            {
                if (request.Query.ContainsKey("list-type"))
                {
                    Assert(request.Query["list-type"] == "2", "Not ListObjectsV2.");
                    string xml = Pages == null ? Page("", false, null) : Pages[Math.Min(pageIndex++, Pages.Length - 1)];
                    XmlReply(stream, 200, xml); return;
                }
                if (request.Query.ContainsKey("uploads"))
                {
                    Assert(request.Method == "POST", "Wrong multipart initiation method.");
                    string id = "owned+/=" + (++serial).ToString(CultureInfo.InvariantCulture);
                    uploads.Add(id, new Upload { Key = request.Key, Owner = HeaderValue(request, "x-amz-meta-photo-import-owner") });
                    XmlReply(stream, 200, "<InitiateMultipartUploadResult xmlns=\"" + Namespace + "\"><UploadId>" + Escape(id) + "</UploadId></InitiateMultipartUploadResult>"); return;
                }
                if (request.Query.ContainsKey("uploadId")) { MultipartRequest(stream, request); return; }
                StoredObject existing;
                objects.TryGetValue(request.Key, out existing);
                if (request.Method == "PUT")
                {
                    Assert(HeaderValue(request, "If-None-Match") == "*", "PUT omitted CREATE_NEW header.");
                    if (existing != null && !IgnorePutCondition) { Error(stream, 412, "PreconditionFailed", "exists"); return; }
                    var item = FromRequest(request, "\"opaque-" + (++serial).ToString(CultureInfo.InvariantCulture) + "\"");
                    objects[request.Key] = item; Reply(stream, 200, new byte[0], ObjectHeaders(item)); return;
                }
                if (request.Method == "DELETE")
                {
                    string condition = HeaderValue(request, "If-Match");
                    Assert(condition != null, "Object DELETE omitted conditional cleanup.");
                    Assert(request.Key.StartsWith(".photo-import-probe-", StringComparison.Ordinal) || request.Key.Contains("/.photo-import-probe-"), "Attempted deletion of user object.");
                    if (RejectDeleteCondition) { Error(stream, 501, "NotImplemented", "conditional delete unsupported"); return; }
                    if (SwapOnCleanup && !condition.Contains("never-match") && existing != null)
                    { existing.Owner = "foreign"; existing.ETag = "\"foreign-replacement\""; existing.Bytes = Utf8.GetBytes("foreign"); existing.Length = existing.Bytes.Length; }
                    if (!IgnoreDeleteCondition && existing != null && condition != existing.ETag) { Error(stream, 412, "PreconditionFailed", "changed"); return; }
                    objects.Remove(request.Key); Reply(stream, 204, new byte[0], null); return;
                }
                if (existing == null) { Error(stream, 404, "NoSuchKey", "absent"); return; }
                if (request.Method == "HEAD") { Header(stream, 200, existing.Length, ObjectHeaders(existing)); return; }
                Assert(request.Method == "GET", "Unexpected S3 method.");
                Header(stream, 200, existing.Length, ObjectHeaders(existing));
                if (existing.Bytes != null) stream.Write(existing.Bytes, 0, existing.Bytes.Length);
                else
                {
                    byte[] buffer = new byte[65536]; long position = 0;
                    while (position < existing.Length)
                    {
                        int count = (int)Math.Min(buffer.Length, existing.Length - position);
                        for (int i = 0; i < count; i++) buffer[i] = Pattern(position + i);
                        stream.Write(buffer, 0, count); position += count;
                    }
                }
            }

            private void MultipartRequest(NetworkStream stream, Request request)
            {
                string id = request.Query["uploadId"];
                Assert(id != "foreign-upload", "Foreign multipart upload was touched.");
                Upload upload;
                if (!uploads.TryGetValue(id, out upload)) { Error(stream, 404, "NoSuchUpload", "absent"); return; }
                Assert(upload.Key == request.Key, "Multipart abort/upload targeted another key.");
                bool fixture = request.Key.Contains(".photo-import-probe-");
                if (request.Method == "DELETE")
                {
                    if (FailAbort && !fixture) { Error(stream, 500, "InternalError", "abort failed"); return; }
                    uploads.Remove(id); Reply(stream, 204, new byte[0], null); return;
                }
                if (request.Method == "PUT")
                {
                    int part = Int32.Parse(request.Query["partNumber"], CultureInfo.InvariantCulture);
                    Assert(part == upload.Parts.Count + 1 && request.Length <= S3Store.PartBytes, "Parts must be bounded, sequential and in order.");
                    MaxPartLength = Math.Max(MaxPartLength, (int)request.Length);
                    if (part == FailPart && (!fixture || FailFixturePart) && PartFailuresRemaining != 0)
                    {
                        if (PartFailuresRemaining > 0) PartFailuresRemaining--;
                        Error(stream, PartStatus, "InternalError", "part failed"); return;
                    }
                    var item = FromRequest(request, "\"part-&-" + part.ToString(CultureInfo.InvariantCulture) + "\"");
                    upload.Parts.Add(item); Reply(stream, 200, new byte[0], ObjectHeaders(item)); return;
                }
                Assert(request.Method == "POST" && HeaderValue(request, "If-None-Match") == "*", "Completion omitted CREATE_NEW.");
                if (RejectMultipartCondition) { Error(stream, 501, "NotImplemented", "conditional completion unsupported"); return; }
                var document = new XmlDocument { XmlResolver = null }; document.LoadXml(Utf8.GetString(request.Body));
                Assert(document.DocumentElement.Name == "CompleteMultipartUpload", "Completion XML root wrong.");
                int index = 0;
                foreach (XmlNode node in document.DocumentElement.ChildNodes)
                {
                    Assert(node["PartNumber"].InnerText == (index + 1).ToString(CultureInfo.InvariantCulture) && node["ETag"].InnerText == upload.Parts[index].ETag, "Completion XML changed a part's opaque ETag."); index++;
                }
                Assert(index == upload.Parts.Count, "Completion XML omitted parts.");
                if (request.Key == RaceKey) Seed(request.Key, Utf8.GetBytes("racing-original"));
                if (objects.ContainsKey(request.Key) && !IgnoreMultipartCondition) { Error(stream, 412, "PreconditionFailed", "exists"); return; }
                if (!fixture && EmbeddedError != null)
                { XmlReply(stream, 200, " \n<Error><Code>" + EmbeddedError + "</Code><Message>" + Secret + "</Message></Error>"); return; }
                long length = 0; foreach (StoredObject part in upload.Parts) length += part.Length;
                var itemComplete = new StoredObject { Length = length, ETag = "\"opaque-mpu-" + (++serial).ToString(CultureInfo.InvariantCulture) + "\"", Owner = upload.Owner };
                if (length <= 1024 * 1024)
                {
                    using (var bytes = new MemoryStream()) { foreach (StoredObject part in upload.Parts) bytes.Write(part.Bytes, 0, part.Bytes.Length); itemComplete.Bytes = bytes.ToArray(); }
                }
                objects[request.Key] = itemComplete; uploads.Remove(id);
                XmlReply(stream, 200, "<CompleteMultipartUploadResult xmlns=\"" + Namespace + "\"><ETag>" + Escape(itemComplete.ETag) + "</ETag></CompleteMultipartUploadResult>");
            }

            private static StoredObject FromRequest(Request request, string etag)
            { return new StoredObject { Bytes = request.Body, Length = request.Length, ETag = etag, Owner = HeaderValue(request, "x-amz-meta-photo-import-owner") }; }
            private static Dictionary<string, string> ObjectHeaders(StoredObject item)
            {
                var headers = new Dictionary<string, string> { { "ETag", item.ETag } };
                if (item.Owner != null) headers.Add("x-amz-meta-photo-import-owner", item.Owner);
                return headers;
            }
            private static string HeaderValue(Request request, string key)
            { string value; return request.Headers.TryGetValue(key, out value) ? value : null; }

            private static Request ReadRequest(NetworkStream stream)
            {
                var header = new MemoryStream(); int state = 0;
                while (state < 4)
                {
                    int c = stream.ReadByte(); if (c < 0) throw new EndOfStreamException();
                    header.WriteByte((byte)c); Assert(header.Length <= 65536, "Request headers too large.");
                    state = c == (state == 0 || state == 2 ? '\r' : '\n') ? state + 1 : c == '\r' ? 1 : 0;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] start = lines[0].Split(' '); int question = start[1].IndexOf('?');
                var request = new Request { Method = start[0], RawPath = question < 0 ? start[1] : start[1].Substring(0, question), RawQuery = question < 0 ? "" : start[1].Substring(question + 1) };
                Assert(request.RawPath.StartsWith("/gateway/photos", StringComparison.Ordinal), "Endpoint base path or path-style bucket missing.");
                request.Key = request.RawPath.Length == 15 ? "" : Uri.UnescapeDataString(request.RawPath.Substring("/gateway/photos/".Length));
                for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
                { int colon = lines[i].IndexOf(':'); request.Headers.Add(lines[i].Substring(0, colon), lines[i].Substring(colon + 1).Trim()); }
                foreach (string pair in request.RawQuery.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
                { int equal = pair.IndexOf('='); request.Query.Add(Uri.UnescapeDataString(pair.Substring(0, equal)), Uri.UnescapeDataString(pair.Substring(equal + 1))); }
                string contentLength = HeaderValue(request, "Content-Length");
                request.Length = contentLength == null ? 0 : Int64.Parse(contentLength, CultureInfo.InvariantCulture);
                Assert(request.Length >= 0 && request.Length <= S3Store.MultipartThreshold, "Unbounded HTTP upload body.");
                using (var body = request.Length <= 1024 * 1024 ? new MemoryStream() : null)
                using (var hash = SHA256.Create())
                {
                    byte[] buffer = new byte[65536]; long consumed = 0;
                    while (consumed < request.Length)
                    {
                        int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, request.Length - consumed));
                        if (count == 0) throw new EndOfStreamException();
                        if (body != null) body.Write(buffer, 0, count);
                        if (body == null || request.Query.ContainsKey("partNumber") && !request.Key.Contains(".photo-import-probe-"))
                        {
                            long basePosition = request.Query.ContainsKey("partNumber") ? (Int64.Parse(request.Query["partNumber"], CultureInfo.InvariantCulture) - 1) * S3Store.PartBytes : 0;
                            for (int i = 0; i < count; i++) Assert(buffer[i] == Pattern(basePosition + consumed + i), "Uploaded pattern data changed.");
                        }
                        hash.TransformBlock(buffer, 0, count, buffer, 0); consumed += count;
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0); request.BodyHash = Hex(hash.Hash);
                    request.Body = body == null ? null : body.ToArray();
                }
                return request;
            }

            private static void VerifySignature(Request request)
            {
                string authorization = HeaderValue(request, "Authorization");
                Match auth = Regex.Match(authorization ?? "", "^AWS4-HMAC-SHA256 Credential=([^/]+)/([0-9]{8})/([^/]+)/s3/aws4_request,SignedHeaders=([^,]+),Signature=([a-f0-9]{64})$");
                Assert(auth.Success && auth.Groups[1].Value == Access && auth.Groups[3].Value == "us-east-1", "Invalid credential scope.");
                string timestamp = HeaderValue(request, "x-amz-date");
                Assert(timestamp.Substring(0, 8) == auth.Groups[2].Value, "Signing timestamp mismatch.");
                string signedHeaders = auth.Groups[4].Value;
                string[] names = signedHeaders.Split(';'); string[] sorted = (string[])names.Clone(); Array.Sort(sorted, StringComparer.Ordinal);
                Assert(String.Join(";", sorted) == signedHeaders, "Signed headers not sorted.");
                var canonicalHeaders = new StringBuilder();
                foreach (string name in names)
                {
                    string value = HeaderValue(request, name); Assert(value != null && name == name.ToLowerInvariant(), "Missing or noncanonical signed header.");
                    canonicalHeaders.Append(name).Append(':').Append(Regex.Replace(value.Trim(), "\\s+", " ")).Append('\n');
                }
                foreach (string required in new[] { "host", "x-amz-date", "x-amz-content-sha256", "if-none-match", "if-match" })
                    if (HeaderValue(request, required) != null) Assert(Array.IndexOf(names, required) >= 0, "Safety header was not signed.");
                Assert(HeaderValue(request, "x-amz-content-sha256") == request.BodyHash, "Payload SHA256 does not match wire body.");
                string[] query = request.RawQuery.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries); Array.Sort(query, StringComparer.Ordinal);
                Assert(String.Join("&", query) == request.RawQuery, "Canonical query is not sorted.");
                string canonical = request.Method + "\n" + request.RawPath + "\n" + request.RawQuery + "\n" + canonicalHeaders + "\n" + signedHeaders + "\n" + request.BodyHash;
                string scope = auth.Groups[2].Value + "/us-east-1/s3/aws4_request";
                string toSign = "AWS4-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Digest(Utf8.GetBytes(canonical));
                byte[] key = Mac(Mac(Mac(Mac(Utf8.GetBytes("AWS4" + Secret), auth.Groups[2].Value), "us-east-1"), "s3"), "aws4_request");
                Assert(Hex(Mac(key, toSign)) == auth.Groups[5].Value, "Wire SigV4 signature mismatch.");
            }

            private static byte[] Mac(byte[] key, string body) { using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(Utf8.GetBytes(body)); }
            private static string Digest(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
            private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
            private static void Error(NetworkStream stream, int status, string code, string message)
            { XmlReply(stream, status, "<Error><Code>" + code + "</Code><Message>" + Escape(message) + "</Message></Error>"); }
            private static void XmlReply(NetworkStream stream, int status, string xml) { Reply(stream, status, Utf8.GetBytes(xml), null); }
            private static void Reply(NetworkStream stream, int status, byte[] body, IDictionary<string, string> headers)
            { Header(stream, status, body.Length, headers); if (body.Length > 0) stream.Write(body, 0, body.Length); }
            private static void Header(NetworkStream stream, int status, long length, IDictionary<string, string> extra)
            {
                var header = new StringBuilder("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(" Fixture\r\nConnection: close\r\nContent-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                if (extra != null) foreach (var pair in extra) header.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                header.Append("\r\n"); byte[] bytes = Encoding.ASCII.GetBytes(header.ToString()); stream.Write(bytes, 0, bytes.Length); stream.Flush();
            }

            internal void Seed(string key, byte[] body)
            { lock (gate) objects[key] = new StoredObject { Bytes = body, Length = body.Length, ETag = "\"seed-" + (++serial).ToString(CultureInfo.InvariantCulture) + "\"" }; }
            internal StoredObject Object(string key) { lock (gate) return objects[key]; }
            internal StoredObject ObjectOrNull(string key) { lock (gate) { StoredObject value; return objects.TryGetValue(key, out value) ? value : null; } }
            internal StoredObject AnyObject() { lock (gate) { foreach (var item in objects.Values) return item; throw new InvalidOperationException("No object."); } }
            internal bool ForeignUploadPresent { get { lock (gate) return uploads.ContainsKey("foreign-upload"); } }
            internal int ObjectCount { get { lock (gate) return objects.Count; } }
            internal bool HasRawPath(string path) { lock (gate) { foreach (Request request in Requests) if (request.RawPath == path) return true; return false; } }
            private int Count(string key, string method, string query, bool present)
            { lock (gate) { int count = 0; foreach (Request request in Requests) if (request.Key == key && request.Method == method && request.Query.ContainsKey(query) == present) count++; return count; } }
            internal int WritesFor(string key) { return Count(key, "PUT", "uploadId", false) + Count(key, "POST", "uploads", true); }
            internal int TargetPartCount(string key) { return Count(key, "PUT", "partNumber", true); }
            internal int AbortsFor(string key) { return Count(key, "DELETE", "uploadId", true); }
            internal int ObjectDeletesFor(string key) { return Count(key, "DELETE", "uploadId", false); }
            internal int CompletionsFor(string key) { return Count(key, "POST", "uploadId", true); }

            public void Dispose()
            {
                stopping.Set(); listener.Stop(); thread.Join(2000);
                lock (gate) foreach (TcpClient client in clients) client.Close();
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 2000) { lock (gate) if (clients.Count == 0) break; Thread.Sleep(10); }
                lock (gate) if (errors.Count != 0) throw new InvalidOperationException("Offline S3 server rejected a request.", errors[0]);
                // Events remain alive for already dispatched ThreadPool handlers.
            }
        }
    }
}
