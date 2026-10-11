using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PhotoImportV2
{
    public static class OrganizedTransferTests
    {
        public static string Run()
        {
            int count = 0;
            Action<Action> check = delegate(Action test) { test(); count++; };
            check(PlanIsReadOnly);
            check(LegacyDuplicate);
            check(DateAndTypeDuplicate);
            check(Signature);
            check(LocalCopy);
            check(LocalCollision);
            check(LocalCannotDelete);
            check(OrganizedLockedCleanup);
            check(delegate { RemoteCopy("webdav"); });
            check(delegate { RemoteCopy("s3"); });
            check(RemoteCollision);
            check(FallbackSnapshot);
            check(TraversalRefused);
            check(UnknownDate);
            check(CapturedPair);
            check(ModifiedFallback);
            check(delegate { LegacyMissingRelative(null); });
            check(delegate { LegacyMissingRelative(""); });
            check(MidnightAndReplanning);
            return "PASS: " + count + " organized planning, local/remote copy and cleanup integration fixtures.";
        }
        static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static OrganizationOptions Options(string mode = "import", bool separate = true)
        { return OrganizationSettings.Normalize(new OrganizationOptions { Mode = mode, SeparateMedia = separate, TimeZoneId = "UTC" }); }
        static void PlanIsReadOnly()
        {
            using (var f = new Fixture())
            {
                var request = f.Plan(Options());
                var result = CandidatePlanner.Run(request);
                Assert(result.Success && result.Plan.Candidates.Count == 1, "Organized planning failed.");
                var file = result.Plan.Candidates[0];
                Assert(file.TargetRelativePath == "2026-10-10/RAW/IMAGE.NEF" && file.RelativePath == f.File.RelativePath && file.SourcePath == f.Source, "Organization changed source or lost resolved path.");
                Assert(!Directory.Exists(Path.Combine(f.Target, "2026-10-10")) && Directory.GetFiles(f.Target, "*", SearchOption.AllDirectories).Length == 0 && File.Exists(f.Source), "Read-only planning created target paths.");
                Assert(request.CandidateFiles[0].TargetRelativePath == null, "Plan mutated its input snapshot.");
            }
        }
        static void LegacyDuplicate()
        {
            using (var f = new Fixture())
            {
                string old = Path.Combine(f.Target, f.File.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(old)); File.WriteAllBytes(old, f.Bytes);
                var result = CandidatePlanner.Run(f.Plan(Options()));
                Assert(result.Success && result.Plan.ExistingCount == 1 && result.Plan.Candidates.Count == 0, "Legacy DCIM duplicate was re-imported after changing organization.");
            }
        }
        static void DateAndTypeDuplicate()
        {
            using (var f = new Fixture())
            {
                string old = Path.Combine(f.Target, "2020-01-01", "RAW", "RENAMED.NEF"); Directory.CreateDirectory(Path.GetDirectoryName(old)); File.WriteAllBytes(old, f.Bytes);
                var result = CandidatePlanner.Run(f.Plan(Options()));
                Assert(result.Success && result.Plan.ExistingCount == 1 && result.Plan.Candidates.Count == 0, "Date-directory renamed duplicate was ignored.");
            }
        }
        static void Signature()
        {
            using (var f = new Fixture())
            {
                var disk = new DiskSnapshot { Root = f.Card, Serial = "fixture", MarkerId = f.Id, Media = new List<MediaItem> { f.File } };
                var state = new AppState { Destinations = f.Plan(Options()).ComparisonTargets, DestinationFailure = "Next", ExcludedExtensions = new List<string>(), Organization = Options("capture", false) };
                string first = CandidatePlanner.Signature(disk, state);
                state.Organization.SeparateMedia = true;
                Assert(first != CandidatePlanner.Signature(disk, state), "Type separation did not invalidate candidate plan.");
                first = CandidatePlanner.Signature(disk, state); state.Organization.MissingDate = "modified";
                Assert(first != CandidatePlanner.Signature(disk, state), "Missing-date rule did not invalidate candidate plan.");
                first = CandidatePlanner.Signature(disk, state); state.Organization.VideoTimeMode = "utc";
                Assert(first != CandidatePlanner.Signature(disk, state), "Video timezone did not invalidate candidate plan.");
            }
        }
        static void LocalCopy()
        {
            using (var f = new Fixture())
            {
                OrganizationSettings.Apply(f.File, Options(), "2026-10-10", null);
                var result = TransferEngine.Run(f.Copy("local"));
                Assert(result.Success && result.Receipt.DestinationPath == Path.Combine(f.Target, "2026-10-10", "RAW", "IMAGE.NEF"), "Local target ignored organized path.");
                Assert(File.ReadAllBytes(result.Receipt.DestinationPath).SequenceEqual(f.Bytes) && File.Exists(f.Source), "Organized copy changed contents/source.");
            }
        }
        static void LocalCollision()
        {
            using (var f = new Fixture())
            {
                OrganizationSettings.Apply(f.File, Options(), "2026-10-10", null);
                string target = Path.Combine(f.Target, f.File.TargetRelativePath.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(target));
                byte[] other = (byte[])f.Bytes.Clone(); other[0]++; File.WriteAllBytes(target, other);
                var result = TransferEngine.Run(f.Copy("local"));
                Assert(result.Success && result.Receipt.DestinationPath != target && File.ReadAllBytes(target).SequenceEqual(other), "Organized local collision overwrote existing media.");
            }
        }
        static void LocalCannotDelete()
        {
            using (var f = new Fixture())
            {
                OrganizationSettings.Apply(f.File, Options(), "2026-10-10", null);
                var result = TransferEngine.Run(f.Copy("local"));
                var cleanup = TransferEngine.Run(f.Delete(result.Receipt));
                Assert(result.Success && !cleanup.Success && File.Exists(f.Source), "Organization bypassed local cleanup restrictions.");
            }
        }
        static void OrganizedLockedCleanup()
        {
            using (var f = new Fixture())
            {
                OrganizationSettings.Apply(f.File, Options(), "2026-10-10", null);
                var copied = TransferEngine.Run(f.Copy("remote"));
                Assert(copied.Success, "Synthetic remote-designated copy failed.");
                var cleanup = TransferEngine.Run(f.Delete(copied.Receipt));
                Assert(cleanup.Success && cleanup.Deleted && !File.Exists(f.Source) && File.Exists(copied.Receipt.DestinationPath), "Locked cleanup lost organized receipt target.");
            }
        }
        static void RemoteCopy(string type)
        {
            using (var f = new Fixture())
            {
                OrganizationSettings.Apply(f.File, Options(), "2026-10-10", null);
                var request = f.Copy("remote"); request.Target = f.Remote(type);
                var store = new MemoryStore(); var result = RemoteTransferEngine.Copy(request, store, null);
                Assert(result.Success && store.Data.ContainsKey(f.File.TargetRelativePath) && result.Receipt.DestinationPath.EndsWith("/2026-10-10/RAW/IMAGE.NEF", StringComparison.Ordinal), "Remote target ignored date/type folders.");
                Assert(result.Receipt.DestinationKind == type && File.Exists(f.Source), "Direct remote cleanup policy changed.");
                Assert(!TransferEngine.Run(f.Delete(result.Receipt)).Success && File.Exists(f.Source), "Direct remote organized receipt authorized cleanup.");
            }
        }
        static void RemoteCollision()
        {
            using (var f = new Fixture())
            {
                OrganizationSettings.Apply(f.File, Options(), "2026-10-10", null);
                var request = f.Copy("remote"); request.Target = f.Remote("webdav"); var store = new MemoryStore();
                byte[] other = (byte[])f.Bytes.Clone(); other[0]++; store.Data[f.File.TargetRelativePath] = other;
                var result = RemoteTransferEngine.Copy(request, store, null);
                Assert(result.Success && store.Data.Count == 2 && store.Data[f.File.TargetRelativePath].SequenceEqual(other), "Organized remote collision overwrote media.");
            }
        }
        static void FallbackSnapshot()
        {
            using (var f = new Fixture())
            {
                OrganizationSettings.Apply(f.File, Options(), "2026-10-10", null);
                var request = f.Copy("remote"); request.Target = f.Remote("webdav");
                var remote = RemoteTransferEngine.Copy(request, new MemoryStore(), null);
                var local = TransferEngine.Run(f.Copy("local"));
                Assert(remote.Success && local.Success && remote.Receipt.DestinationPath.EndsWith(f.File.TargetRelativePath, StringComparison.Ordinal) && local.Receipt.DestinationPath.EndsWith(f.File.TargetRelativePath.Replace('/', '\\'), StringComparison.Ordinal), "Primary/fallback organization diverged.");
            }
        }
        static void TraversalRefused()
        {
            using (var f = new Fixture())
            {
                f.File.TargetRelativePath = "../IMAGE.NEF";
                Assert(!TransferEngine.Run(f.Copy("local")).Success && File.Exists(f.Source), "Local organization traversal accepted.");
                var request = f.Copy("remote"); request.Target = f.Remote("webdav"); bool rejected = false;
                try { RemoteTransferEngine.Copy(request, new MemoryStore(), null); } catch (Exception) { rejected = true; }
                Assert(rejected && Directory.GetFiles(f.Target).Length == 0, "Remote organization traversal accepted.");
            }
        }
        static void UnknownDate()
        {
            using (var f = new Fixture())
            {
                var result = CandidatePlanner.Run(f.Plan(Options("capture")));
                Assert(result.Success && result.Plan.Candidates[0].TargetRelativePath == "未知日期/RAW/IMAGE.NEF", "Missing EXIF silently became import date.");
            }
        }
        static void CapturedPair()
        {
            using (var f = new Fixture())
            {
                byte[] tiff;
                using (var buffer = new MemoryStream())
                using (var writer = new BinaryWriter(buffer))
                {
                    writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write((uint)8);
                    writer.Write((ushort)1); writer.Write((ushort)0x9003); writer.Write((ushort)2); writer.Write((uint)20); writer.Write((uint)26); writer.Write((uint)0);
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("2026:10:09 23:59:59\0")); writer.Flush(); tiff = buffer.ToArray();
                }
                System.IO.File.WriteAllBytes(f.Source, tiff); f.File.Length = tiff.Length; f.File.WriteTicks = System.IO.File.GetLastWriteTimeUtc(f.Source).Ticks;
                string jpgPath = Path.ChangeExtension(f.Source, ".JPG"); System.IO.File.WriteAllBytes(jpgPath, new byte[] { 1, 2, 3 });
                var request = f.Plan(Options("capture"));
                request.CandidateFiles.Add(new MediaItem { SourcePath = jpgPath, RelativePath = Path.ChangeExtension(f.File.RelativePath, ".JPG"), Length = 3, WriteTicks = System.IO.File.GetLastWriteTimeUtc(jpgPath).Ticks });
                var result = CandidatePlanner.Run(request);
                Assert(result.Success && result.Plan.Candidates.Count == 2, "Capture pair planning failed.");
                Assert(result.Plan.Candidates[0].TargetRelativePath == "2026-10-09/RAW/IMAGE.NEF" && result.Plan.Candidates[1].TargetRelativePath == "2026-10-09/JPG/IMAGE.JPG", "Reliable RAW date was not shared with unknown paired JPG.");
            }
        }
        static void ModifiedFallback()
        {
            using (var f = new Fixture())
            {
                System.IO.File.SetLastWriteTimeUtc(f.Source, new DateTime(2026, 10, 8, 22, 0, 0, DateTimeKind.Utc)); f.File.WriteTicks = System.IO.File.GetLastWriteTimeUtc(f.Source).Ticks;
                var options = Options("capture"); options.MissingDate = "modified";
                var result = CandidatePlanner.Run(f.Plan(options));
                Assert(result.Success && result.Plan.Candidates[0].TargetRelativePath == "2026-10-08/RAW/IMAGE.NEF" && result.Plan.Candidates[0].CaptureDateSource.Contains("修改"), "Missing capture did not use explicit modified fallback.");
            }
        }
        static void LegacyMissingRelative(string relative)
        {
            foreach (string kind in new[] { "local", "remote" })
                using (var f = new Fixture())
                {
                    string original = f.File.RelativePath; f.File.RelativePath = relative;
                    var result = TransferEngine.Run(f.Copy(kind));
                    Assert(result.Success && result.Receipt.DestinationPath == Path.Combine(f.Target, original), "Legacy implicit path with null/empty RelativePath regressed.");
                }
        }
        static void MidnightAndReplanning()
        {
            using (var f = new Fixture())
            {
                var disk = new DiskSnapshot { Root = f.Card, MarkerId = f.Id, Media = new List<MediaItem> { f.File } };
                var state = new AppState { Destinations = f.Plan(Options()).ComparisonTargets, DestinationFailure = "Next", ExcludedExtensions = new List<string>(), Organization = Options() };
                DateTime before = new DateTime(2026, 10, 10, 23, 59, 59, DateTimeKind.Utc), after = before.AddSeconds(1);
                Assert(CandidatePlanner.SignatureAt(disk, state, before) != CandidatePlanner.SignatureAt(disk, state, after), "Import date cache survived midnight.");
                var first = CandidatePlanner.Run(f.Plan(state.Organization)); Assert(first.Success, "Frozen date plan failed.");
                string approved = first.Plan.Candidates[0].TargetRelativePath;
                state.Organization.Mode = "flat"; state.Organization.SeparateMedia = false;
                var next = CandidatePlanner.Run(f.Plan(state.Organization));
                Assert(next.Success && next.Plan.Candidates[0].TargetRelativePath == "IMAGE.NEF" && first.Plan.Candidates[0].TargetRelativePath == approved && approved == "2026-10-10/RAW/IMAGE.NEF", "Changing rules mutated approved paths or failed to replan.");
                state.Organization.Mode = "capture";
                Assert(CandidatePlanner.SignatureAt(disk, state, before) == CandidatePlanner.SignatureAt(disk, state,after), "Capture date cache was needlessly invalidated at midnight.");
            }
        }
        sealed class MemoryStore : IRemoteStore
        {
            internal Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
            public IEnumerable<RemoteObject> List() { return Data.Select(p => new RemoteObject { Path = p.Key, Length = p.Value.Length }); }
            public RemoteObject Stat(string path) { return Data.ContainsKey(path) ? new RemoteObject { Path = path, Length = Data[path].Length } : null; }
            public Stream OpenRead(string path) { return new MemoryStream(Data[path], false); }
            public bool UploadNew(string path, Stream input, long length, Action<long> progress)
            {
                if (Data.ContainsKey(path)) return false;
                using (var memory = new MemoryStream()) { byte[] buffer = new byte[8192]; int n; while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { memory.Write(buffer, 0, n); progress(memory.Length); } Data.Add(path, memory.ToArray()); }
                return true;
            }
            public WorkResult Probe(bool writeTest) { return new WorkResult { Success = true }; }
        }
        sealed class Fixture : IDisposable
        {
            readonly string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
            internal readonly string Root, Card, Source, Target, Id = Guid.NewGuid().ToString("D");
            internal readonly byte[] Bytes = Enumerable.Range(0, 513).Select(i => (byte)(i % 251)).ToArray();
            internal readonly MediaItem File;
            internal Fixture()
            {
                Root = Path.Combine(parent, "photoimport-organized-" + Guid.NewGuid().ToString("N")); Card = Path.Combine(Root, "card"); Target = Path.Combine(Root, "target"); Source = Path.Combine(Card, "DCIM", "100TEST", "IMAGE.NEF");
                Directory.CreateDirectory(Path.GetDirectoryName(Source)); Directory.CreateDirectory(Target);
                JsonFile.Write(Path.Combine(Card, IdentityVerifier.MarkerFileName), new CardMarker { Version = 1, CardId = Id }); System.IO.File.WriteAllBytes(Source, Bytes);
                File = new MediaItem { SourcePath = Source, RelativePath = "DCIM\\100TEST\\IMAGE.NEF", Length = Bytes.Length, WriteTicks = System.IO.File.GetLastWriteTimeUtc(Source).Ticks };
            }
            internal WorkRequest Plan(OrganizationOptions options)
            { return new WorkRequest { Operation = "plan", SourceRoot = Card, CardId = Id, CandidateFiles = new List<MediaItem> { File }, ComparisonTargets = new List<DestinationRecord> { ImportSettings.ValidateDestination("Fixture", Target) }, ExcludedExtensions = new List<string>(), Organization = options, ImportDate = "2026-10-10" }; }
            internal WorkRequest Copy(string kind)
            { return new WorkRequest { Operation = "copy", SourceRoot = Card, CardId = Id, File = File, DestinationRoot = Target, DestinationKind = kind }; }
            internal WorkRequest Delete(TransferReceipt receipt)
            { return new WorkRequest { Operation = "delete", SourceRoot = Card, CardId = Id, File = File, DestinationRoot = Target, Receipt = receipt }; }
            internal DestinationRecord Remote(string type)
            { return StorageSettings.Normalize(new DestinationRecord { Id = Guid.NewGuid().ToString("D"), Name = "Fixture", Type = type, Endpoint = "https://storage.example", Path = "https://storage.example", Bucket = type == "s3" ? "fixture" : null, Prefix = "media" }); }
            public void Dispose()
            { Assert(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("photoimport-organized-", StringComparison.Ordinal), "Cleanup scope."); Remove(Root); }
            void Remove(string folder)
            {
                Assert((System.IO.File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 && (folder == Root || folder.StartsWith(Root + "\\", StringComparison.Ordinal)), "Unsafe cleanup link.");
                foreach (string path in Directory.GetFiles(folder)) { Assert((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Unsafe fixture link."); System.IO.File.Delete(path); }
                foreach (string path in Directory.GetDirectories(folder)) Remove(path); Directory.Delete(folder, false);
            }
        }
    }
}
