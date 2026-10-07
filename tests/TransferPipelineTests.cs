using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoImportV2
{
    public static class TransferPipelineTests
    {
        public static string Run()
        {
            int count = 0;
            Action<Action> check = delegate(Action test) { test(); count++; };
            check(delegate { Assert(StorageSettings.TypeOf(ImportSettings.ValidateDestination("Folder", @"C:\Photos")) == "folder", "Folder type."); });
            check(delegate { Assert(StorageSettings.TypeOf(ImportSettings.ValidateDestination("Share", @"\\server\Photos")) == "smb", "Share type."); });
            check(delegate { var d = StorageSettings.Normalize(ImportSettings.ValidateDestination("DAV", "https://nas.example/Photos")); Assert(d.Type == "webdav" && d.Path == "https://nas.example/Photos", "Legacy DAV migration."); });
            check(delegate { var d = S3(); var n = StorageSettings.Normalize(d); Assert(n.Path == "https://s3.example/photos/Z30" && n.Type == "s3", "S3 canonical path."); });
            check(delegate { var d = S3(); d.Prefix = "../outside"; Refused(delegate { StorageSettings.Normalize(d); }); });
            check(delegate { var d = S3(); d.Endpoint = "https://user:password@s3.example"; Refused(delegate { StorageSettings.Normalize(d); }); });
            check(delegate { var d = S3(); var clone = ImportSettings.Clone(d); clone.Bucket = "other"; Assert(d.Bucket == "photos" && clone.CredentialTarget == d.CredentialTarget, "Frozen provider clone."); });
            check(delegate { Assert(StorageSettings.Parallel(new AppState()) == 2 && StorageSettings.IdleSeconds(new AppState()) == 180, "Transfer defaults."); });
            check(delegate { Refused(delegate { StorageSettings.ValidateTransfer(new AppState { MaxParallel = 20 }); }); });
            check(delegate { Refused(delegate { StorageSettings.Relative("/outside", false); }); Refused(delegate { StorageSettings.Relative("a/../b", false); }); });
            check(delegate { using (var s = new MemoryStream(new byte[] { 1, 2, 3 })) { var p = new List<TransferProgress>(); string h = RemoteTransferEngine.Hash(s, 3, "上传", p.Add); Assert(h == Digest(new byte[] { 1, 2, 3 }) && p.Last().Bytes == 3, "Streaming hash/progress."); } });
            check(delegate { using (var s = new MemoryStream(new byte[] { 1, 2 })) Refused(delegate { RemoteTransferEngine.Hash(s, 3, "校验", null); }); });
            check(delegate { using (var s = new MemoryStream(new byte[] { 1, 2, 3, 4 })) Refused(delegate { RemoteTransferEngine.Hash(s, 3, "校验", null); }); });
            check(RemoteCopy); check(Collision); check(Tamper); check(Duplicate); check(RemoteCannotDelete);
            check(delegate { Schedule(false); }); check(delegate { Schedule(true); }); check(Cancel);
            check(delegate { FailureDrain(false); }); check(delegate { FailureDrain(true); });
            check(delegate { string first = CredentialStore.NewReference(), second = CredentialStore.NewReference(); Assert(first != second && CredentialStore.OwnedTarget(first) && CredentialStore.OwnedTarget(second), "Credential references reused."); });
            return "PASS: " + count + " storage, streaming integrity and bounded scheduling fixture tests.";
        }
        static DestinationRecord S3() { return new DestinationRecord { Id = Guid.NewGuid().ToString(), Name = "Object store", Type = "s3", Endpoint = "https://s3.example", Bucket = "photos", Region = "us-east-1", Prefix = "Z30", CredentialTarget = "PhotoImport/test-reference" }; }
        static void RemoteCopy()
        {
            using (var f = new Fixture())
            {
                var progress = new List<TransferProgress>(); var store = new MemoryStore();
                var result = RemoteTransferEngine.Copy(f.Request, store, progress.Add);
                Assert(result.Success && File.Exists(f.Source) && result.Receipt.Sha256 == Digest(f.Data), "Remote verified receipt.");
                Assert(!progress.Any(p => p.Stage == "读取") && progress.Any(p => p.Stage == "上传") && progress.Last().Stage == "校验", "New upload unnecessarily prehashed source.");
                Assert(store.Uploaded == f.Data.Length && store.Read == f.Data.Length, "Streamed upload/readback lengths.");
            }
        }
        static void Collision()
        {
            using (var f = new Fixture())
            {
                var store = new MemoryStore(); byte[] other = (byte[])f.Data.Clone(); other[0]++;
                store.Data[f.Relative] = other;
                var result = RemoteTransferEngine.Copy(f.Request, store, null);
                Assert(result.Success && store.Data.Count == 2 && store.Data[f.Relative][0] == other[0] && result.Receipt.DestinationPath.Contains(Digest(f.Data)), "Collision overwrite.");
            }
        }
        static void Duplicate()
        {
            using (var f = new Fixture())
            {
                var store = new MemoryStore(); store.Data[f.Relative] = f.Data;
                var result = RemoteTransferEngine.Copy(f.Request, store, null);
                Assert(result.Success && result.ReusedExisting && store.Uploaded == 0, "Duplicate was re-uploaded.");
            }
        }
        static void Tamper()
        {
            using (var f = new Fixture())
            {
                var store = new MemoryStore { Tamper = true };
                Refused(delegate { RemoteTransferEngine.Copy(f.Request, store, null); });
                Assert(File.Exists(f.Source), "Failed verification removed source.");
            }
        }
        static void RemoteCannotDelete()
        {
            using (var f = new Fixture())
            {
                var receipt = RemoteTransferEngine.Copy(f.Request, new MemoryStore(), null).Receipt;
                var result = TransferEngine.Run(new WorkRequest { Operation = "delete", SourceRoot = f.Root, CardId = f.Id, Receipt = receipt });
                Assert(!result.Success && File.Exists(f.Source), "HTTP receipt bypassed locked cleanup.");
            }
        }
        static void Schedule(bool automatic)
        {
            var files = Enumerable.Range(0, 9).Select(i => new MediaItem { RelativePath = i.ToString(), Length = i < 3 ? 100 : 1 }).ToList();
            var started = new List<string>(); int active = 0, large = 0, peak = 0, largePeak = 0;
            var gate = new object();
            ImportScheduler.Run(files, 2, 50, automatic, async delegate(MediaItem f) {
                lock (gate) { started.Add(f.RelativePath); active++; if (f.Length >= 50) large++; peak = Math.Max(peak, active); largePeak = Math.Max(largePeak, large); }
                await Task.Delay(8);
                lock (gate) { active--; if (f.Length >= 50) large--; } return true;
            }, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert(peak <= 2 && largePeak == 1 && started.Count == files.Count && active == 0, "Scheduler bounds/drain: peak=" + peak + ", large=" + largePeak + ", count=" + started.Count + ", active=" + active);
            Assert(started.Take(4).Any(s => Int32.Parse(s) < 3), "Large file starved.");
            if (automatic) Assert(started[0] == "3", "Automatic mode did not start with a small-file baseline.");
        }
        static void Cancel()
        {
            var files = Enumerable.Range(0, 8).Select(i => new MediaItem { RelativePath = i.ToString(), Length = 1 }).ToList();
            using (var c = new CancellationTokenSource())
            {
                int active = 0;
                try
                {
                    ImportScheduler.Run(files, 2, 50, false, async delegate(MediaItem f) { Interlocked.Increment(ref active); try { c.Cancel(); await Task.Delay(5, c.Token); return true; } finally { Interlocked.Decrement(ref active); } }, null, c.Token).GetAwaiter().GetResult();
                    throw new Exception("Cancellation ignored.");
                }
                catch (OperationCanceledException) { Assert(active == 0, "Cancellation left running transfer."); }
            }
        }
        static void FailureDrain(bool fault)
        {
            var files = Enumerable.Range(0, 3).Select(i => new MediaItem { RelativePath = i.ToString(), Length = 1 }).ToList();
            using (var c = new CancellationTokenSource())
            {
                int started = 0, finished = 0, canceled = 0;
                bool failed = false;
                try
                {
                    ImportScheduler.Run(files, 2, 50, false, async delegate(MediaItem f) {
                        Interlocked.Increment(ref started);
                        try
                        {
                            if (f.RelativePath == "0") { await Task.Delay(10); if (fault) throw new IOException("Synthetic journal failure."); return false; }
                            await Task.Delay(2000, c.Token); return true;
                        }
                        catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
                        finally { Interlocked.Increment(ref finished); }
                    }, null, c.Token, c.Cancel, true).GetAwaiter().GetResult();
                }
                catch (Exception) { failed = true; }
                Assert(failed && started == 2 && finished == 2 && canceled == 1 && c.IsCancellationRequested, "Fatal/Stop failure did not cancel and drain siblings or started a queued file.");
            }
        }
        static string Digest(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Refused(Action action) { bool refused = false; try { action(); } catch (Exception) { refused = true; } Assert(refused, "Unsafe input accepted."); }
        sealed class MemoryStore : IRemoteStore
        {
            internal Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>(); internal long Uploaded, Read; internal bool Tamper;
            public IEnumerable<RemoteObject> List() { return Data.Select(p => new RemoteObject { Path = p.Key, Length = p.Value.Length }); }
            public RemoteObject Stat(string p) { return Data.ContainsKey(p) ? new RemoteObject { Path = p, Length = Data[p].Length } : null; }
            public Stream OpenRead(string p) { Read += Data[p].Length; return new MemoryStream(Data[p], false); }
            public bool UploadNew(string p, Stream input, long length, Action<long> progress)
            {
                if (Data.ContainsKey(p)) return false;
                using (var memory = new MemoryStream())
                {
                    byte[] buffer = new byte[1024]; int n; while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { memory.Write(buffer, 0, n); Uploaded += n; progress(memory.Length); }
                    Data[p] = memory.ToArray(); if (Tamper) Data[p][0]++;
                }
                return true;
            }
            public WorkResult Probe(bool writeTest) { return new WorkResult { Success = true }; }
        }
        sealed class Fixture : IDisposable
        {
            internal readonly string Root, Source, Id = Guid.NewGuid().ToString(), Relative = "DCIM/100TEST/IMG0001.JPG";
            internal readonly byte[] Data = Enumerable.Range(0, 196731).Select(i => (byte)(i % 251)).ToArray();
            internal WorkRequest Request;
            readonly string parent = Path.GetFullPath(Path.GetTempPath());
            internal Fixture()
            {
                Root = Path.Combine(parent, "photoimport-pipeline-" + Guid.NewGuid().ToString("N"));
                Source = Path.Combine(Root, Relative.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(Source));
                JsonFile.Write(Path.Combine(Root, IdentityVerifier.MarkerFileName), new CardMarker { Version = 1, CardId = Id }); File.WriteAllBytes(Source, Data);
                Request = new WorkRequest { Operation = "copy", SourceRoot = Root, CardId = Id,
                    File = new MediaItem { SourcePath = Source, RelativePath = Relative.Replace('/', '\\'), Length = Data.Length, WriteTicks = File.GetLastWriteTimeUtc(Source).Ticks },
                    Target = new DestinationRecord { Name = "DAV", Type = "webdav", Path = "https://nas.example/Photos", Endpoint = "https://nas.example/Photos" } };
            }
            public void Dispose()
            {
                Assert(Path.GetDirectoryName(Root).TrimEnd('\\') == parent.TrimEnd('\\') && Path.GetFileName(Root).StartsWith("photoimport-pipeline-", StringComparison.Ordinal), "Fixture cleanup scope.");
                Remove(Root);
            }
            void Remove(string folder)
            {
                Assert((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 && (folder == Root || folder.StartsWith(Root + "\\", StringComparison.Ordinal)), "Unsafe fixture cleanup.");
                foreach (string p in Directory.GetFiles(folder)) { Assert((File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0, "Unexpected link."); File.Delete(p); }
                foreach (string p in Directory.GetDirectories(folder)) Remove(p);
                Directory.Delete(folder, false);
            }
        }
    }
}
