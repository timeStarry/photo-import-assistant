using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;

namespace PhotoImportV2
{
    // Loopback HTTP and pure credential decoding only. No OS credential reads/writes,
    // real media, NAS, mounted shares or caller-provided fixture paths.
    public static class WebDavStoreTests
    {
        public static string Run()
        {
            var passed = new List<string>();
            Test(passed, "depth-one inventory and split propstat", Inventory);
            Test(passed, "inventory depth and count bounds", InventoryBounds);
            Test(passed, "DTD and malformed XML refusal", UnsafeXml);
            Test(passed, "untrusted href and incomplete inventory refusal", UnsafeInventory);
            Test(passed, "HEAD and response-owned GET", ReadObjects);
            Test(passed, "progressing GET outlives header timeout", SlowRead);
            Test(passed, "UTF-8 path containment and encoding", Paths);
            Test(passed, "incremental nonseekable upload and progress", StreamingUpload);
            Test(passed, "slow upload exceeds connection timeout while making progress", SlowUpload);
            Test(passed, "stalled PUT body and final headers have finite waits", StalledUpload);
            Test(passed, "empty streaming upload", EmptyUpload);
            Test(passed, "occupied final consumes no input", ExistingName);
            Test(passed, "racing MOVE collision never overwrites", PublishCollision);
            Test(passed, "unsupported MOVE fails and conditionally cleans", UnsupportedMove);
            Test(passed, "changed source is not mistaken for final collision", ChangedPartial);
            Test(passed, "ambiguous MOVE retains partial and final", AmbiguousMove);
            Test(passed, "ambiguous PUT and UUID collision never delete", AmbiguousPut);
            Test(passed, "missing and weak validators retain partials", MissingValidators);
            Test(passed, "length mismatch and interrupted input never publish", InvalidInput);
            Test(passed, "redirects never send credentials to another origin", Redirects);
            Test(passed, "Basic authentication without input replay", Authentication);
            Test(passed, "finite response and read timeouts", Timeouts);
            Test(passed, "read-only capability probe performs no writes", ReadOnlyProbe);
            Test(passed, "write probe checks MOVE refusal using only owned partials", WriteProbe);
            Test(passed, "failed probe reports unsafe or unsupported MOVE", FailedProbe);
            Test(passed, "credential target matching and blob validation", CredentialValidation);
            return "PASS: " + passed.Count.ToString(CultureInfo.InvariantCulture) +
                " local WebDAV/credential tests: " + String.Join("; ", passed.ToArray()) + ".";
        }

        private static void Test(List<string> passed, string name, Action action)
        {
            try { action(); passed.Add(name); }
            catch (Exception ex) { throw new InvalidOperationException("WebDAV test failed: " + name, ex); }
        }

        private static void Inventory()
        {
            using (var server = new Server())
            {
                server.Collection("/dav/DCIM"); server.Collection("/dav/DCIM/100");
                server.File("/dav/readme #%.txt", Bytes(13));
                server.File("/dav/DCIM/100/照片.NEF", Bytes(29));
                server.File("/dav/.photo-import-" + Guid.NewGuid().ToString("N") + ".partial", Bytes(5));
                server.SplitProperties = true;
                List<RemoteObject> objects = server.Store().List().ToList();
                Assert(objects.Count == 2 && objects.Any(delegate(RemoteObject item) { return item.Path == "readme #%.txt" && item.Length == 13; }) &&
                    objects.Any(delegate(RemoteObject item) { return item.Path == "DCIM/100/照片.NEF" && item.Length == 29 && item.ETag != null; }), "Inventory metadata/paths differ.");
                Assert(server.Records.Count(delegate(RequestRecord record) { return record.Method == "PROPFIND"; }) == 3 &&
                    server.Records.Where(delegate(RequestRecord record) { return record.Method == "PROPFIND"; }).All(delegate(RequestRecord record) { return record.Depth == "1"; }),
                    "Inventory did not use one bounded request per collection.");
            }
        }

        private static void InventoryBounds()
        {
            using (var server = new Server())
            {
                server.Collection("/dav/a"); server.Collection("/dav/a/b"); server.File("/dav/a/b/x", Bytes(1));
                Throws<IOException>(delegate { server.Store(2000, 1, 20).List().ToList(); });
                Assert(server.Records.All(delegate(RequestRecord record) { return record.Path != "/dav/a/b/"; }), "Depth bound fetched an excessive collection.");
            }
            using (var server = new Server())
            {
                server.File("/dav/a", Bytes(1)); server.File("/dav/b", Bytes(1));
                Throws<IOException>(delegate { server.Store(2000, 8, 1).List().ToList(); });
            }
        }

        private static void UnsafeXml()
        {
            foreach (string xml in new[]
            {
                "<!DOCTYPE d:multistatus [<!ENTITY x SYSTEM \"http://127.0.0.1:9/never\">]><d:multistatus xmlns:d=\"DAV:\">&x;</d:multistatus>",
                "<!DOCTYPE d:multistatus [<!ENTITY x \"expand\">]><d:multistatus xmlns:d=\"DAV:\">&x;</d:multistatus>",
                "<d:multistatus xmlns:d=\"DAV:\"><broken",
                "<multistatus xmlns=\"wrong\"/>"
            })
            {
                using (var server = new Server())
                {
                    server.XmlOverride = xml;
                    Throws<IOException>(delegate { server.Store().List().ToList(); });
                    Assert(server.Records.Count == 1, "Invalid XML triggered another request.");
                }
            }
        }

        private static void UnsafeInventory()
        {
            foreach (string href in new[] { "/davish/a", "/outside/a", "http://127.0.0.1:9/dav/a",
                "/dav/a%2Fb", "/dav/a%5Cb", "/dav/%00x", "/dav/%FF", "/dav/a?query=x", "/dav/a#fragment",
                "/dav/nested/deeper", "/dav/../outside" })
            {
                using (var server = new Server())
                {
                    server.XmlOverride = Multi(Response("/dav/", true, 0, null) + Response(href, false, 1, "\"fixture\""));
                    Throws<IOException>(delegate { server.Store().List().ToList(); });
                    Assert(server.Records.Count == 1, "Unsafe href was followed.");
                }
            }
            foreach (string xml in new[]
            {
                Multi(Response("/dav/a", false, 1, null)), // No collection self.
                Multi(Response("/dav/", true, 0, null) + Response("/dav/a", false, -1, null)),
                Multi(Response("/dav/", true, 0, null) + Response("/dav/a", false, 1, null) + Response("/dav/a", false, 1, null)),
                Multi(Response("/dav/", true, 0, null) + "<d:response><d:href>/dav/a</d:href><d:status>HTTP/1.1 403 Forbidden</d:status></d:response>"),
                Multi(Response("/dav/", true, 0, null) + "<d:response><d:href>/dav/a</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>1</d:getcontentlength></d:prop><d:status>HTTP/1.1 404 Missing</d:status></d:propstat></d:response>")
            })
            {
                using (var server = new Server())
                {
                    server.XmlOverride = xml;
                    Throws<IOException>(delegate { server.Store().List().ToList(); });
                }
            }
        }

        private static void ReadObjects()
        {
            using (var server = new Server())
            {
                byte[] bytes = Bytes(150000); server.File("/dav/x", bytes);
                WebDavStore store = server.Store();
                Assert(store.Stat("missing") == null, "404 HEAD was not null.");
                RemoteObject item = store.Stat("x");
                Assert(item.Path == "x" && item.Length == bytes.Length && item.ETag != null, "HEAD metadata differs.");
                Stream read = store.OpenRead("x");
                Assert(read.CanRead && !read.CanSeek && !read.CanWrite && read.ReadByte() == bytes[0], "GET stream contract differs.");
                read.Dispose(); read.Dispose();
                Throws<ObjectDisposedException>(delegate { read.ReadByte(); });
                Assert(store.Stat("x").Length == bytes.Length, "Disposing unread GET did not release its response.");
                using (Stream stream = store.OpenRead("x")) Assert(Equal(ReadAll(stream), bytes), "GET data differ.");
                Throws<IOException>(delegate { store.OpenRead("missing"); });
                server.HeadFailure = 403;
                Throws<IOException>(delegate { store.Stat("x"); });
            }
        }

        private static void SlowRead()
        {
            using (var server = new Server())
            {
                byte[] bytes = Bytes(20); server.File("/dav/slow-read", bytes); server.SlowBody = true;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using (Stream stream = server.Store(200, 8, 100).OpenRead("slow-read"))
                    Assert(Equal(ReadAll(stream), bytes), "Progressing GET was cut off by a header/request timer.");
                Assert(watch.ElapsedMilliseconds > 800, "GET fixture did not exceed its header timeout.");
            }
        }

        private static void Paths()
        {
            using (var server = new Server())
            {
                WebDavStore store = server.Store();
                string path = "相册/照片 #?%+😀.NEF";
                byte[] bytes = Bytes(17);
                using (var input = new MemoryStream(bytes, false)) Assert(store.UploadNew(path, input, bytes.Length, null), "Unicode upload failed.");
                Assert(Equal(server.Get("/dav/" + path), bytes), "Unicode path was changed.");
                RequestRecord move = server.Records.Single(delegate(RequestRecord record) { return record.Method == "MOVE"; });
                Assert(move.Destination.EndsWith("/%E7%9B%B8%E5%86%8C/%E7%85%A7%E7%89%87%20%23%3F%25%2B%F0%9F%98%80.NEF", StringComparison.Ordinal),
                    "Destination was not UTF-8 percent encoded.");
                int requests = server.Records.Count;
                foreach (string bad in new[] { "", "../x", "/x", "a/../x", "a\\x", "a//x", "a:stream", "a/\0x" })
                    Throws<ArgumentException>(delegate { store.Stat(bad); });
                Assert(requests == server.Records.Count, "Unsafe relative path sent a request.");
                using (var input = new MemoryStream(bytes, false)) Assert(store.UploadNew("literal%2F", input, bytes.Length, null), "Literal percent name failed.");
                Assert(server.Records.Any(delegate(RequestRecord record) { return record.RawUrl.Contains("literal%252F"); }), "Literal percent was decoded twice.");
            }
        }

        private static void StreamingUpload()
        {
            using (var server = new Server())
            {
                byte[] bytes = Bytes(900123);
                using (var input = new IncrementalInput(bytes, server.FirstUploadBytes))
                {
                    var progress = new List<long>();
                    bool done = server.Store().UploadNew("DCIM/100/photo.NEF", input, bytes.Length, delegate(long consumed) { progress.Add(consumed); });
                    Assert(done && !input.Disposed && input.ReadCount > 2 && input.LargestRead <= 65536, "Input was buffered, disposed or replayed.");
                    Assert(progress[0] == 0 && progress[progress.Count - 1] == bytes.Length &&
                        progress.SequenceEqual(progress.OrderBy(delegate(long value) { return value; })), "Progress is not cumulative consumed bytes.");
                }
                Assert(Equal(server.Get("/dav/DCIM/100/photo.NEF"), bytes), "Published bytes differ.");
                RequestRecord put = server.Records.Single(delegate(RequestRecord record) { return record.Method == "PUT"; });
                RequestRecord move = server.Records.Single(delegate(RequestRecord record) { return record.Method == "MOVE"; });
                Assert(put.IfNoneMatch == "*" && put.ContentLength == bytes.Length && put.TransferEncoding == null &&
                    put.Path.EndsWith(".partial", StringComparison.Ordinal) && move.Overwrite == "F" && move.IfMatch != null, "Publication headers differ.");
                Assert(!server.Records.Any(delegate(RequestRecord record) { return record.Method == "DELETE"; }), "Successful upload deleted a path.");
                Assert(server.Partials == 0, "Successful upload left a partial.");
            }
        }

        private static void SlowUpload()
        {
            using (var server = new Server())
            using (var input = new SlowInput(Bytes(49152)))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Assert(server.Store(200, 8, 100).UploadNew("slow", input, 49152, null), "Progressing upload exceeded a total-request deadline.");
                Assert(watch.ElapsedMilliseconds > 800 && Equal(server.Get("/dav/slow"), Bytes(49152)), "Slow fixture did not exceed the connection/header timeout.");
            }
        }

        private static void StalledUpload()
        {
            using (var server = new Server())
            {
                server.StallPutBody = true;
                using (var input = new MemoryStream(Bytes(8 * 1024 * 1024), false))
                    Throws<IOException>(delegate { server.Store(200, 8, 100).UploadNew("stalled", input, input.Length, null); });
                Assert(!server.Records.Any(delegate(RequestRecord record) { return record.Method == "MOVE" || record.Method == "DELETE"; }), "Stalled PUT gained publication or cleanup authority.");
            }
            using (var server = new Server())
            {
                server.StallPutResponse = true;
                using (var input = new MemoryStream(Bytes(1024), false))
                    Throws<IOException>(delegate { server.Store(200, 8, 100).UploadNew("stalled", input, input.Length, null); });
                Assert(server.Partials == 1 && !server.Records.Any(delegate(RequestRecord record) { return record.Method == "MOVE" || record.Method == "DELETE"; }), "Lost PUT headers allowed deletion.");
            }
        }

        private static void EmptyUpload()
        {
            using (var server = new Server())
            using (var input = new MemoryStream(new byte[0], false))
            {
                Assert(server.Store().UploadNew("empty", input, 0, null) && server.Get("/dav/empty").Length == 0, "Zero-byte upload failed.");
            }
        }

        private static void ExistingName()
        {
            using (var server = new Server())
            {
                byte[] original = Bytes(9); server.File("/dav/occupied", original);
                using (var input = new UnreadableInput())
                    Assert(!server.Store().UploadNew("occupied", input, 100, delegate(long value) { throw new InvalidOperationException("Progress consumed occupied input."); }),
                        "Occupied final did not return false.");
                Assert(Equal(server.Get("/dav/occupied"), original) && server.Records.Count == 1, "Occupied final was changed.");
            }
        }

        private static void PublishCollision()
        {
            using (var server = new Server())
            {
                server.RaceFinal = true;
                using (var input = new MemoryStream(Bytes(70000), false))
                    Assert(!server.Store().UploadNew("final", input, input.Length, null), "Racing collision did not return false.");
                Assert(Equal(server.Get("/dav/final"), server.RacingBytes), "Racing final was overwritten.");
                Assert(server.Partials == 0, "Confirmed partial was not cleaned.");
                CheckDeletes(server);
                Assert(server.Records.Count(delegate(RequestRecord record) { return record.Method == "PUT"; }) == 1, "Collision retried input.");
            }
        }

        private static void UnsupportedMove()
        {
            foreach (int status in new[] { 405, 501 })
            using (var server = new Server())
            {
                server.MoveFailure = status;
                using (var input = new MemoryStream(Bytes(11), false))
                    Throws<NotSupportedException>(delegate { server.Store().UploadNew("final", input, input.Length, null); });
                Assert(!server.Contains("/dav/final") && server.Partials == 0, "Unsupported MOVE published a final or leaked a confirmed partial.");
                CheckDeletes(server);
                Assert(server.Records.All(delegate(RequestRecord record) { return record.Method != "PUT" || record.Path.EndsWith(".partial", StringComparison.Ordinal); }), "Fallback wrote final directly.");
            }
        }

        private static void ChangedPartial()
        {
            using (var server = new Server())
            {
                server.SwapPartial = true;
                using (var input = new MemoryStream(Bytes(11), false))
                    Throws<IOException>(delegate { server.Store().UploadNew("final", input, input.Length, null); });
                Assert(!server.Contains("/dav/final") && server.Partials == 1, "Changed partial gained publication/cleanup authority.");
                Assert(server.Records.Any(delegate(RequestRecord record) { return record.Method == "DELETE" && record.IfMatch != null; }), "Cleanup lacked conditional ownership.");
                CheckDeletes(server);
            }
        }

        private static void AmbiguousMove()
        {
            foreach (int status in new[] { 500, 207, 204, 302 })
            using (var server = new Server())
            {
                server.MoveFailure = status; server.AmbiguousFinal = true;
                using (var input = new MemoryStream(Bytes(17), false))
                    Throws<IOException>(delegate { server.Store().UploadNew("final", input, input.Length, null); });
                Assert(server.Contains("/dav/final") && server.Partials == 1 &&
                    !server.Records.Any(delegate(RequestRecord record) { return record.Method == "DELETE"; }), "Ambiguous MOVE deleted a final or partial.");
            }
        }

        private static void AmbiguousPut()
        {
            foreach (int status in new[] { 412, 200, 500 })
            using (var server = new Server())
            {
                server.PutFailure = status;
                using (var input = new MemoryStream(Bytes(17), false))
                    Throws<IOException>(delegate { server.Store().UploadNew("final", input, input.Length, null); });
                Assert(server.Partials == 1 && !server.Contains("/dav/final") &&
                    !server.Records.Any(delegate(RequestRecord record) { return record.Method == "DELETE" || record.Method == "MOVE"; }), "Unconfirmed PUT was deleted or published.");
            }
        }

        private static void MissingValidators()
        {
            foreach (string mode in new[] { "missing", "weak" })
            using (var server = new Server())
            {
                server.TagMode = mode; server.MoveFailure = 405;
                using (var input = new MemoryStream(Bytes(13), false))
                    Throws<NotSupportedException>(delegate { server.Store().UploadNew("final", input, input.Length, null); });
                Assert(server.Partials == 1 && !server.Records.Any(delegate(RequestRecord record) { return record.Method == "DELETE"; }), "Missing strong validator allowed deletion.");
            }
        }

        private static void InvalidInput()
        {
            foreach (int declared in new[] { 3, 5 })
            using (var server = new Server())
            using (var input = new MemoryStream(Bytes(4), false))
            {
                Throws<IOException>(delegate { server.Store().UploadNew("final", input, declared, null); });
                Assert(!server.Contains("/dav/final") &&
                    !server.Records.Any(delegate(RequestRecord record) { return record.Method == "MOVE" || record.Method == "DELETE"; }), "Length mismatch published or deleted an unconfirmed upload.");
            }
            using (var server = new Server())
            using (var input = new ThrowingInput())
            {
                Throws<IOException>(delegate { server.Store().UploadNew("final", input, 70000, null); });
                Assert(!server.Contains("/dav/final") && !server.Records.Any(delegate(RequestRecord record) { return record.Method == "MOVE" || record.Method == "DELETE"; }), "Interrupted input was published or deleted.");
            }
        }

        private static void Redirects()
        {
            using (var target = new Server())
            using (var source = new Server())
            {
                source.Redirect = target.Endpoint + "/secret"; source.BasicAuth = true;
                WebDavStore store = source.Store(2000, 8, 100, new NetworkCredential("fixture-user", "fixture-secret"));
                Throws<IOException>(delegate { store.Stat("x"); });
                Assert(target.Records.Count == 0, "Redirect contacted another origin.");
                WorkResult probe = store.Probe(false);
                Assert(!probe.Success && !probe.Error.Contains("fixture-user") && !probe.Error.Contains("fixture-secret") &&
                    !probe.Error.Contains(target.Endpoint), "Probe exposed credentials or redirect location.");
            }
        }

        private static void Authentication()
        {
            using (var server = new Server())
            {
                server.BasicAuth = true;
                using (var input = new IncrementalInput(Bytes(270000), server.FirstUploadBytes))
                {
                    Assert(server.Store(3000, 8, 100, new NetworkCredential("fixture-user", "fixture-secret")).UploadNew("auth", input, 270000, null), "Authenticated upload failed.");
                    Assert(input.BytesConsumed == 270000, "Authentication replayed input.");
                }
                Assert(server.Records.Count(delegate(RequestRecord record) { return record.Method == "PUT"; }) == 1 &&
                    server.Records.Single(delegate(RequestRecord record) { return record.Method == "PUT"; }).Authorization != null,
                    "PUT was not authenticated before streaming.");
            }
        }

        private static void Timeouts()
        {
            using (var server = new Server())
            {
                server.StallHeaders = true;
                Throws<IOException>(delegate { server.Store(200, 8, 100).Stat("x"); });
            }
            using (var server = new Server())
            {
                server.File("/dav/x", Bytes(5)); server.StallBody = true;
                using (Stream stream = server.Store(200, 8, 100).OpenRead("x"))
                {
                    Assert(stream.ReadByte() >= 0, "Stalled fixture did not send initial byte.");
                    Throws<IOException>(delegate { stream.ReadByte(); });
                }
            }
        }

        private static void ReadOnlyProbe()
        {
            using (var server = new Server())
            {
                WorkResult result = server.Store().Probe(false);
                Assert(result.Success && result.Detail.Contains("MOVE") && result.Detail.Contains("未执行") &&
                    server.Records.All(delegate(RequestRecord record) { return record.Method == "PROPFIND" || record.Method == "OPTIONS"; }), "Read-only probe performed a write.");
                server.MoveFailure = 405;
                result = server.Store().Probe(false);
                Assert(result.Success && result.Detail.Contains("未声明 MOVE"), "Read-only probe hid unavailable publication capability.");
            }
        }

        private static void WriteProbe()
        {
            using (var server = new Server())
            {
                server.File("/dav/user-file", Bytes(7));
                WorkResult result = server.Store().Probe(true);
                Assert(result.Success && result.Error == null && result.Detail.Contains("Overwrite:F") && server.Partials == 0 &&
                    Equal(server.Get("/dav/user-file"), Bytes(7)), "Write probe failed or changed a user file.");
                Assert(server.Records.Count(delegate(RequestRecord record) { return record.Method == "MOVE"; }) == 2 &&
                    server.Records.Where(delegate(RequestRecord record) { return record.Method == "MOVE"; }).All(delegate(RequestRecord record) { return record.Overwrite == "F"; }), "Probe did not test collision and creation.");
                CheckDeletes(server);
            }
        }

        private static void FailedProbe()
        {
            foreach (bool ignore in new[] { false, true })
            using (var server = new Server())
            {
                if (ignore) server.IgnoreOverwrite = true; else server.MoveFailure = 405;
                WorkResult result = server.Store().Probe(true);
                Assert(!result.Success && result.Error.Contains(ignore ? "Overwrite:F" : "MOVE") &&
                    server.Records.All(delegate(RequestRecord record) { return record.Method != "PUT" || record.Path.EndsWith(".partial", StringComparison.Ordinal); }), "Failed probe claimed safe capability.");
                if (ignore) Assert(!server.Records.Any(delegate(RequestRecord record) { return record.Method == "DELETE"; }), "Unsafe MOVE result allowed cleanup.");
                else Assert(server.Partials == 0, "Definitively unsupported MOVE left confirmed probe partials.");
            }
            using (var server = new Server())
            {
                server.TagMode = "missing";
                WorkResult result = server.Store().Probe(true);
                Assert(result.Success && result.Detail.Contains("保留") && server.Partials == 2, "Unverifiable probe cleanup was hidden.");
            }
        }

        private static void CredentialValidation()
        {
            var destination = new DestinationRecord { Type = "webdav", Endpoint = "https://example.invalid:8443/photos",
                Path = @"\\example.invalid@SSL@8443\DavWWWRoot\photos" };
            List<string> targets = CredentialStore.LegacyTargets(destination);
            Assert(targets.Contains("https://example.invalid:8443") &&
                targets.Contains("Microsoft_Windows_Network:target=example.invalid@SSL@8443"), "Legacy targets omit WebClient authority.");
            Assert(CredentialStore.MatchingTarget("Microsoft_Windows_Network:target=example.invalid@SSL@8443", targets) >= 0 &&
                CredentialStore.MatchingTarget("LegacyGeneric:target=https://example.invalid:8443", targets) >= 0, "Exact legacy matching failed.");
            foreach (string bad in new[] { "https://example.invalid.evil:8443", "https://example.invalid:8444",
                "Microsoft_Windows_Network:target=example.invalid.evil", "prefix-example.invalid",
                "https://example.invalid:8443/photos-elsewhere", "Domain:target=example.invalid" })
                Assert(CredentialStore.MatchingTarget(bad, targets) < 0, "Credential matching accepted a suffix/prefix/port/domain mismatch.");
            NetworkCredential credential = CredentialStore.DecodeBlob(@"fixture-domain\fixture-user", Encoding.Unicode.GetBytes("fixture-secret\0"));
            Assert(credential != null && credential.UserName == "fixture-user" && credential.Domain == "fixture-domain" &&
                credential.Password == "fixture-secret", "Unicode credential decoding failed.");
            Assert(CredentialStore.DecodeBlob("fixture-user", new byte[0]) != null, "Empty password was rejected.");
            foreach (byte[] blob in new[] { new byte[] { 1 }, new byte[] { 0, 216 }, new byte[2562],
                Encoding.Unicode.GetBytes("embedded\0secret"), Encoding.Unicode.GetBytes("\uffff") })
                Assert(CredentialStore.DecodeBlob("fixture-user", blob) == null, "Invalid credential blob was accepted.");
            Assert(CredentialStore.OwnedTarget(CredentialStore.TargetPrefix + Guid.NewGuid().ToString("N")) &&
                !CredentialStore.OwnedTarget("Microsoft_Windows_Network:target=example.invalid") &&
                !CredentialStore.OwnedTarget(CredentialStore.TargetPrefix + "not-an-id"), "Credential save ownership is too broad.");
        }

        private static void CheckDeletes(Server server)
        {
            Assert(server.Records.Where(delegate(RequestRecord record) { return record.Method == "DELETE"; })
                .All(delegate(RequestRecord record) { return record.Path.Contains("/.photo-import-") &&
                    record.Path.EndsWith(".partial", StringComparison.Ordinal) && record.IfMatch != null; }), "DELETE escaped conditional partial cleanup.");
        }
        private static byte[] Bytes(int count) { var bytes = new byte[count]; for (int i = 0; i < count; i++) bytes[i] = (byte)(i % 251); return bytes; }
        private static bool Equal(byte[] first, byte[] second) { return first != null && second != null && first.SequenceEqual(second); }
        private static byte[] ReadAll(Stream stream) { using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); } }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }
        private static string Escape(string text) { return SecurityElement.Escape(text); }
        private static string Multi(string responses) { return "<d:multistatus xmlns:d=\"DAV:\">" + responses + "</d:multistatus>"; }
        private static string Response(string href, bool collection, long length, string etag)
        {
            return "<d:response><d:href>" + Escape(href) + "</d:href><d:propstat><d:prop><d:resourcetype>" +
                (collection ? "<d:collection/>" : "") + "</d:resourcetype>" +
                (length < 0 ? "" : "<d:getcontentlength>" + length.ToString(CultureInfo.InvariantCulture) + "</d:getcontentlength>") +
                (etag == null ? "" : "<d:getetag>" + Escape(etag) + "</d:getetag>") +
                "</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
        }

        private sealed class RequestRecord
        {
            public string Method, Path, RawUrl, Destination, IfNoneMatch, IfMatch, Overwrite, Depth, TransferEncoding, Authorization;
            public long ContentLength;
        }
        private sealed class Item
        {
            public byte[] Bytes;
            public string Tag;
        }

        private sealed class Server : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly Thread accept;
            private readonly object sync = new object();
            private readonly Dictionary<string, Item> files = new Dictionary<string, Item>(StringComparer.Ordinal);
            private readonly HashSet<string> collections = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<RequestRecord> records = new List<RequestRecord>();
            private readonly List<Exception> failures = new List<Exception>();
            private readonly HashSet<HttpListenerContext> active = new HashSet<HttpListenerContext>();
            private readonly ManualResetEvent stopped = new ManualResetEvent(false);
            private readonly ManualResetEvent drained = new ManualResetEvent(true);
            private int handlers;
            private int nextTag;
            private readonly string realm = Guid.NewGuid().ToString("N");
            public readonly ManualResetEvent FirstUploadBytes = new ManualResetEvent(false);
            public readonly byte[] RacingBytes = Encoding.UTF8.GetBytes("racing fixture occupant");
            public readonly string Endpoint;
            public string XmlOverride, Redirect, TagMode;
            public bool SplitProperties, RaceFinal, SwapPartial, AmbiguousFinal, IgnoreOverwrite, BasicAuth, StallHeaders, StallBody, StallPutBody, StallPutResponse, SlowBody;
            public int MoveFailure, PutFailure, HeadFailure;

            public Server()
            {
                // Reserve a loopback port briefly; retry if another local listener won.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var reservation = new TcpListener(IPAddress.Loopback, 0);
                    reservation.Start();
                    int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                    reservation.Stop();
                    Endpoint = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/dav";
                    listener.Prefixes.Clear(); listener.Prefixes.Add(Endpoint.Substring(0, Endpoint.Length - 3));
                    try { listener.Start(); break; }
                    catch (HttpListenerException) { if (attempt == 9) throw; }
                }
                collections.Add("/dav");
                accept = new Thread(Accept) { IsBackground = true };
                accept.Start();
            }
            public WebDavStore Store() { return Store(3000, 32, 1000, null); }
            public WebDavStore Store(int milliseconds, int depth, int count) { return Store(milliseconds, depth, count, null); }
            public WebDavStore Store(int milliseconds, int depth, int count, NetworkCredential credential)
            {
                return new WebDavStore(new DestinationRecord { Name = "local fixture", Type = "webdav", Endpoint = Endpoint, Path = Endpoint },
                    credential, milliseconds, depth, count);
            }
            public List<RequestRecord> Records { get { lock (sync) return new List<RequestRecord>(records); } }
            public int Partials { get { lock (sync) return files.Keys.Count(delegate(string path) { return path.EndsWith(".partial", StringComparison.Ordinal); }); } }
            public void Collection(string path) { lock (sync) collections.Add(path); }
            public void File(string path, byte[] bytes) { lock (sync) files[path] = NewItem(bytes); }
            public bool Contains(string path) { lock (sync) return files.ContainsKey(path); }
            public byte[] Get(string path) { lock (sync) { Item item; return files.TryGetValue(path, out item) ? item.Bytes : null; } }
            private Item NewItem(byte[] bytes) { return new Item { Bytes = bytes, Tag = "\"fixture-" + (++nextTag).ToString(CultureInfo.InvariantCulture) + "\"" }; }
            private void Accept()
            {
                while (!stopped.WaitOne(0))
                {
                    HttpListenerContext context;
                    try { context = listener.GetContext(); }
                    catch (HttpListenerException) { return; }
                    catch (ObjectDisposedException) { return; }
                    lock (sync)
                    {
                        if (stopped.WaitOne(0)) { context.Response.Abort(); return; }
                        active.Add(context); handlers++; drained.Reset();
                    }
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { Handle(context); }
                        catch (HttpListenerException) { } // Expected client abort/timeout.
                        catch (IOException) { } // Expected truncated PUT/client cancellation.
                        catch (ObjectDisposedException) { }
                        catch (Exception ex) { lock (sync) failures.Add(ex); }
                        finally
                        {
                            try { context.Response.Close(); } catch (HttpListenerException) { } catch (ObjectDisposedException) { }
                            lock (sync) { active.Remove(context); if (--handlers == 0) drained.Set(); }
                        }
                    });
                }
            }
            private void Handle(HttpListenerContext context)
            {
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;
                string path = Uri.UnescapeDataString(request.Url.AbsolutePath).TrimEnd('/');
                var record = new RequestRecord
                {
                    Method = request.HttpMethod, Path = Uri.UnescapeDataString(request.Url.AbsolutePath), RawUrl = request.RawUrl,
                    Destination = request.Headers["Destination"], IfNoneMatch = request.Headers["If-None-Match"],
                    IfMatch = request.Headers["If-Match"], Overwrite = request.Headers["Overwrite"], Depth = request.Headers["Depth"],
                    ContentLength = request.ContentLength64, TransferEncoding = request.Headers["Transfer-Encoding"],
                    Authorization = request.Headers["Authorization"]
                };
                lock (sync) records.Add(record);
                if (StallHeaders) { stopped.WaitOne(5000); return; }
                if (BasicAuth && request.Headers["Authorization"] != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:fixture-secret")))
                {
                    response.StatusCode = 401; response.AddHeader("WWW-Authenticate", "Basic realm=\"" + realm + "\""); return;
                }
                if (Redirect != null)
                {
                    response.StatusCode = 302; response.RedirectLocation = Redirect; return;
                }
                if (request.HttpMethod == "OPTIONS")
                {
                    response.Headers["Allow"] = "OPTIONS, HEAD, GET, PROPFIND, PUT, DELETE, MKCOL" + (MoveFailure == 405 ? "" : ", MOVE");
                    return;
                }
                if (request.HttpMethod == "PROPFIND")
                {
                    string xml;
                    lock (sync)
                    {
                        if (!collections.Contains(path)) { response.StatusCode = 404; return; }
                        xml = XmlOverride ?? InventoryXml(path, record.Depth);
                    }
                    Respond(response, 207, Encoding.UTF8.GetBytes(xml), "application/xml"); return;
                }
                if (request.HttpMethod == "MKCOL")
                {
                    lock (sync)
                    {
                        if (collections.Contains(path) || files.ContainsKey(path)) { response.StatusCode = 405; return; }
                        if (!collections.Contains(path.Substring(0, path.LastIndexOf('/')))) { response.StatusCode = 409; return; }
                        collections.Add(path); response.StatusCode = 201; return;
                    }
                }
                if (request.HttpMethod == "HEAD" || request.HttpMethod == "GET")
                {
                    Item item;
                    lock (sync)
                    {
                        if (request.HttpMethod == "HEAD" && HeadFailure != 0) { response.StatusCode = HeadFailure; return; }
                        if (!files.TryGetValue(path, out item)) { response.StatusCode = 404; return; }
                    }
                    response.Headers["ETag"] = item.Tag; response.ContentLength64 = item.Bytes.Length;
                    if (request.HttpMethod == "GET")
                    {
                        if (StallBody)
                        {
                            response.OutputStream.WriteByte(item.Bytes[0]); response.OutputStream.Flush(); stopped.WaitOne(5000); return;
                        }
                        if (SlowBody)
                        {
                            foreach (byte value in item.Bytes)
                            {
                                response.OutputStream.WriteByte(value); response.OutputStream.Flush(); Thread.Sleep(50);
                            }
                            return;
                        }
                        response.OutputStream.Write(item.Bytes, 0, item.Bytes.Length);
                    }
                    return;
                }
                if (request.HttpMethod == "PUT")
                {
                    if (StallPutBody) { stopped.WaitOne(5000); return; }
                    byte[] bytes;
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192]; int read;
                        while ((read = request.InputStream.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            FirstUploadBytes.Set(); output.Write(buffer, 0, read);
                        }
                        bytes = output.ToArray();
                    }
                    lock (sync)
                    {
                        if (files.ContainsKey(path) && record.IfNoneMatch == "*") { response.StatusCode = 412; return; }
                        Item item = NewItem(bytes);
                        files[path] = item;
                        response.StatusCode = PutFailure == 0 ? 201 : PutFailure;
                        if (TagMode != "missing") response.Headers["ETag"] = (TagMode == "weak" ? "W/" : "") + item.Tag;
                    }
                    if (StallPutResponse) stopped.WaitOne(5000);
                    return;
                }
                if (request.HttpMethod == "MOVE")
                {
                    string destination = Uri.UnescapeDataString(new Uri(record.Destination).AbsolutePath).TrimEnd('/');
                    lock (sync)
                    {
                        Item source;
                        if (!files.TryGetValue(path, out source)) { response.StatusCode = 404; return; }
                        if (SwapPartial) { files[path] = NewItem(RacingBytes); source = files[path]; }
                        if (record.IfMatch != null && record.IfMatch != source.Tag) { response.StatusCode = 412; return; }
                        if (MoveFailure != 0)
                        {
                            if (AmbiguousFinal) files[destination] = NewItem(source.Bytes);
                            response.StatusCode = MoveFailure; return;
                        }
                        if (RaceFinal) files[destination] = NewItem(RacingBytes);
                        if (files.ContainsKey(destination) && record.Overwrite == "F" && !IgnoreOverwrite)
                        { response.StatusCode = 412; return; }
                        files[destination] = source; files.Remove(path);
                        response.StatusCode = 201;
                        if (TagMode != "missing") response.Headers["ETag"] = source.Tag;
                    }
                    return;
                }
                if (request.HttpMethod == "DELETE")
                {
                    lock (sync)
                    {
                        Item item;
                        if (!files.TryGetValue(path, out item)) { response.StatusCode = 404; return; }
                        if (record.IfMatch == null || record.IfMatch != item.Tag) { response.StatusCode = 412; return; }
                        files.Remove(path); response.StatusCode = 204;
                    }
                    return;
                }
                response.StatusCode = 405;
            }
            private string InventoryXml(string path, string depth)
            {
                var xml = new StringBuilder(Response(EncodedPath(path) + "/", true, 0, null));
                if (depth == "1")
                {
                    string prefix = path + "/";
                    foreach (string child in collections)
                        if (child.StartsWith(prefix, StringComparison.Ordinal) && child.Substring(prefix.Length).IndexOf('/') < 0)
                            xml.Append(Response(EncodedPath(child) + "/", true, 0, null));
                    foreach (KeyValuePair<string, Item> child in files)
                        if (child.Key.StartsWith(prefix, StringComparison.Ordinal) && child.Key.Substring(prefix.Length).IndexOf('/') < 0)
                        {
                            string item = Response(EncodedPath(child.Key), false, child.Value.Bytes.Length, SplitProperties ? null : child.Value.Tag);
                            if (SplitProperties)
                                item = item.Replace("</d:response>", "<d:propstat><d:prop><d:getetag>" + Escape(child.Value.Tag) +
                                    "</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>" +
                                    "<d:propstat><d:prop><d:displayname/></d:prop><d:status>HTTP/1.1 404 Missing</d:status></d:propstat></d:response>");
                            xml.Append(item);
                        }
                }
                return Multi(xml.ToString());
            }
            private static string EncodedPath(string path)
            {
                return String.Join("/", path.Split('/').Select(delegate(string segment) { return Uri.EscapeDataString(segment); }).ToArray());
            }
            private static void Respond(HttpListenerResponse response, int status, byte[] bytes, string type)
            {
                response.StatusCode = status; response.ContentType = type; response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            public void Dispose()
            {
                stopped.Set();
                lock (sync) foreach (HttpListenerContext context in active) context.Response.Abort();
                listener.Stop(); listener.Close();
                Assert(accept.Join(5000), "Loopback accept thread did not stop.");
                Assert(drained.WaitOne(5000), "Loopback handlers did not stop.");
                FirstUploadBytes.Dispose(); stopped.Dispose(); drained.Dispose();
                lock (sync) if (failures.Count != 0) throw new InvalidOperationException("Loopback fixture failed.", failures[0]);
            }
        }

        private abstract class InputStream : Stream
        {
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new InvalidOperationException("Input must not seek."); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
        private sealed class UnreadableInput : InputStream
        {
            public override int Read(byte[] buffer, int offset, int count) { throw new InvalidOperationException("Occupied final consumed input."); }
        }
        private sealed class ThrowingInput : InputStream
        {
            private bool read;
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (read) throw new IOException("Synthetic source interruption.");
                read = true; int length = Math.Min(count, 65536); Array.Clear(buffer, offset, length); return length;
            }
        }
        private sealed class IncrementalInput : InputStream
        {
            private readonly byte[] bytes;
            private readonly WaitHandle received;
            public int ReadCount, LargestRead, BytesConsumed;
            public bool Disposed;
            public IncrementalInput(byte[] bytes, WaitHandle received) { this.bytes = bytes; this.received = received; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (BytesConsumed > 0) Assert(received.WaitOne(5000), "Input was read again before the first block reached the server.");
                Assert(count <= 65536 && !Disposed, "Input was fully buffered or closed.");
                ReadCount++; LargestRead = Math.Max(LargestRead, count);
                int read = Math.Min(count, bytes.Length - BytesConsumed);
                Array.Copy(bytes, BytesConsumed, buffer, offset, read); BytesConsumed += read; return read;
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }
        private sealed class SlowInput : InputStream
        {
            private readonly byte[] bytes;
            private int offset;
            public SlowInput(byte[] bytes) { this.bytes = bytes; }
            public override int Read(byte[] buffer, int start, int count)
            {
                if (offset == bytes.Length) return 0;
                Thread.Sleep(50);
                int read = Math.Min(Math.Min(count, 2048), bytes.Length - offset);
                Array.Copy(bytes, offset, buffer, start, read); offset += read; return read;
            }
        }
    }
}
