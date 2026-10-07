using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotoImportV2
{
    // Offline fixtures only. No app, removable media, network target or scheduler is used.
    public static class CandidatePlannerTests
    {
        public static string Run()
        {
            var passed = new List<string>();
            Test(passed, "same-name exact duplicate is read-only", SameNameDuplicate);
            Test(passed, "same-size different bytes remain candidates", DifferentContent);
            Test(passed, "renamed exact bytes are suppressed", RenamedDuplicate);
            Test(passed, "hash-suffixed exact bytes are suppressed", HashSuffixedDuplicate);
            Test(passed, "all enabled fallback targets are compared", FallbackTargets);
            Test(passed, "disabled targets are ignored", DisabledTarget);
            Test(passed, "missing target warns and retains on rerun", MissingTargetRerun);
            Test(passed, "deleted target file retains on rerun", DeletedTargetRerun);
            Test(passed, "corrupt target file retains on rerun", CorruptTargetRerun);
            Test(passed, "deleted target after inventory retains", DeletedAfterInventory);
            Test(passed, "corrupt target after inventory retains", CorruptAfterInventory);
            Test(passed, "replacement with identical length and mtime is rehashed on rerun", FreshReplacementRerun);
            Test(passed, "every intra-run positive match is freshly rehashed", FreshPositiveWithinRun);
            Test(passed, "intra-run non-match cache does not survive rerun", NonmatchCacheRerun);
            Test(passed, "source length change rejects the whole plan", SourceLengthChanged);
            Test(passed, "source mtime change rejects the whole plan", SourceTimeChanged);
            Test(passed, "source removal rejects the whole plan", SourceRemoved);
            Test(passed, "source change after partial progress rejects the whole plan", SourceChangedDuringRun);
            Test(passed, "marker loss rejects planning", MarkerLoss);
            Test(passed, "marker ID mismatch rejects planning", MarkerMismatch);
            Test(passed, "marker version mismatch rejects planning", MarkerVersionMismatch);
            Test(passed, "DAT is never a candidate", DatExcluded);
            Test(passed, "custom NEF exclusions are counted", CustomExclusions);
            Test(passed, "empty exclusions retain supported media only", EmptyExclusions);
            Test(passed, "extensions and exclusion normalization are case insensitive", CaseInsensitiveExclusions);
            Test(passed, "partial destination files cannot suppress candidates", PartialTargets);
            Test(passed, "partial source suffix is excluded", PartialSourceSuffix);
            Test(passed, "supported-looking partial source is refused", PartialSourceRefused);
            Test(passed, "equal ancestor and descendant targets are refused", OverlappingTargets);
            Test(passed, "local junction source and targets are rejected", JunctionPaths);
            Test(passed, "signature changes for card identity", SignatureCard);
            Test(passed, "signature changes for media snapshots", SignatureMedia);
            Test(passed, "signature changes for destinations", SignatureDestinations);
            Test(passed, "signature changes for exclusions", SignatureExclusions);
            Test(passed, "signature is stable for equivalent media order and exclusions", SignatureStable);
            Test(passed, "empty plans and invalid requests fail safely", EmptyAndInvalidRequests);
            return "PASS: " + passed.Count.ToString(CultureInfo.InvariantCulture) + " candidate planner fixture tests: " +
                String.Join("; ", passed.ToArray()) + ".";
        }

        private static void Test(List<string> passed, string name, Action action)
        {
            try { action(); passed.Add(name); }
            catch (Exception error) { throw new InvalidOperationException("Candidate planner test failed: " + name, error); }
        }

        private static void SameNameDuplicate()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", fixture.Data);
                fixture.MakeReadOnly(fixture.Source);
                fixture.MakeReadOnly(fixture.Destination);
                Expect(fixture.Plan(fixture.Request()), 1, 0, 0);
            }
        }

        private static void DifferentContent()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", Changed(fixture.Data));
                Expect(fixture.Plan(fixture.Request()), 0, 0, 0, fixture.Snapshot);
            }
        }

        private static void RenamedDuplicate()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("archive\\another-camera-name.JPEG", fixture.Data);
                Expect(fixture.Plan(fixture.Request()), 1, 0, 0);
            }
        }

        private static void HashSuffixedDuplicate()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", Changed(fixture.Data));
                fixture.WriteTarget("archive\\IMG0001." + Digest(fixture.Data) + ".2.JPG", fixture.Data);
                Expect(fixture.Plan(fixture.Request()), 1, 0, 0);
            }
        }

        private static void FallbackTargets()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", Changed(fixture.Data));
                MediaItem second = fixture.AddSource("IMG0002.NEF", Changed(fixture.Data));
                string fallback = fixture.Directory("tempFallback");
                fixture.WriteFile(Path.Combine(fallback, "renamed.JPG"), fixture.Data);
                fixture.WriteFile(Path.Combine(fallback, "renamed.NEF"), Changed(fixture.Data));
                WorkRequest request = fixture.Request(fixture.Snapshot, second);
                request.ComparisonTargets.Add(ImportSettings.ValidateDestination("Fallback fixture", fallback));
                Expect(fixture.Plan(request), 2, 0, 0);

                string missing = Path.Combine(fixture.Root, "missingPrimary");
                request.ComparisonTargets[0] = ImportSettings.ValidateDestination("Missing primary fixture", missing);
                Expect(fixture.Plan(request), 2, 0, 1);
            }
        }

        private static void DisabledTarget()
        {
            using (var fixture = new Fixture())
            {
                string disabled = fixture.Directory("disabledTarget");
                fixture.WriteFile(Path.Combine(disabled, "same.JPG"), fixture.Data);
                WorkRequest request = fixture.Request();
                var target = ImportSettings.ValidateDestination("Disabled fixture", disabled);
                target.Enabled = false;
                request.ComparisonTargets.Add(target);
                // Even an overlapping disabled target must not be inspected.
                target = ImportSettings.ValidateDestination("Disabled source fixture", fixture.CardRoot);
                target.Enabled = false;
                request.ComparisonTargets.Add(target);
                Expect(fixture.Plan(request), 0, 0, 0, fixture.Snapshot);
            }
        }

        private static void MissingTargetRerun()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", fixture.Data);
                WorkRequest request = fixture.Request();
                Expect(fixture.Plan(request), 1, 0, 0);
                fixture.MoveDirectory(fixture.TargetRoot, Path.Combine(fixture.Root, "parkedTarget"));
                CandidatePlan plan = Expect(fixture.Plan(request), 0, 0, 1, fixture.Snapshot);
                Assert(!String.IsNullOrWhiteSpace(plan.Warnings[0]), "Missing target warning is empty.");
                Assert(!System.IO.Directory.Exists(fixture.TargetRoot), "Planning recreated the missing target.");
            }
        }

        private static void DeletedTargetRerun()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", fixture.Data);
                WorkRequest request = fixture.Request();
                Expect(fixture.Plan(request), 1, 0, 0);
                fixture.DeleteFile(fixture.Destination);
                Expect(fixture.Plan(request), 0, 0, 0, fixture.Snapshot);
            }
        }

        private static void CorruptTargetRerun()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", fixture.Data);
                WorkRequest request = fixture.Request();
                Expect(fixture.Plan(request), 1, 0, 0);
                fixture.WriteFile(fixture.Destination, Changed(fixture.Data));
                Expect(fixture.Plan(request), 0, 0, 0, fixture.Snapshot);
            }
        }

        private static void DeletedAfterInventory()
        {
            TargetChangedAfterInventory(delegate(Fixture fixture) { fixture.DeleteFile(fixture.Destination); }, 1);
        }

        private static void CorruptAfterInventory()
        {
            TargetChangedAfterInventory(delegate(Fixture fixture) { fixture.WriteFile(fixture.Destination, Changed(fixture.Data)); }, 0);
        }

        private static void TargetChangedAfterInventory(Action<Fixture> mutate, int warnings)
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", fixture.Data);
                bool changed = false;
                WorkRequest request = fixture.Request();
                WorkResult result = fixture.Plan(request, delegate(CandidateProgress progress)
                {
                    // The source progress report occurs after target inventory and before opening the source.
                    if (!changed && progress.Processed == 0 && progress.Message.EndsWith(fixture.Snapshot.RelativePath, StringComparison.Ordinal))
                    { mutate(fixture); changed = true; }
                });
                Assert(changed, "Fixture mutation did not run after target inventory.");
                Expect(result, 0, 0, warnings, fixture.Snapshot);
            }
        }

        private static void FreshReplacementRerun()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", fixture.Data);
                WorkRequest request = fixture.Request();
                Expect(fixture.Plan(request), 1, 0, 0);
                fixture.ReplaceTargetPreservingMetadata(Changed(fixture.Data));
                Expect(fixture.Plan(request), 0, 0, 0, fixture.Snapshot);
                fixture.ReplaceTargetPreservingMetadata(fixture.Data);
                Expect(fixture.Plan(request), 1, 0, 0);
            }
        }

        private static void FreshPositiveWithinRun()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", fixture.Data);
                MediaItem second = fixture.AddSource("IMG0002.JPG", fixture.Data);
                bool changed = false;
                WorkRequest request = fixture.Request(fixture.Snapshot, second);
                WorkResult result = fixture.Plan(request, delegate(CandidateProgress progress)
                {
                    if (!changed && progress.Processed == 1)
                    { fixture.ReplaceTargetPreservingMetadata(Changed(fixture.Data)); changed = true; }
                });
                Assert(changed, "The target was not replaced between positive comparisons.");
                Expect(result, 1, 0, 0, second);
                Expect(fixture.Plan(request), 0, 0, 0, fixture.Snapshot, second);
            }
        }

        private static void NonmatchCacheRerun()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("DCIM\\100TEST\\IMG0001.JPG", Changed(fixture.Data));
                MediaItem second = fixture.AddSource("IMG0002.JPG", fixture.Data);
                bool changed = false;
                WorkRequest request = fixture.Request(fixture.Snapshot, second);
                WorkResult result = fixture.Plan(request, delegate(CandidateProgress progress)
                {
                    if (!changed && progress.Processed == 1)
                    { fixture.ReplaceTargetPreservingMetadata(fixture.Data); changed = true; }
                });
                Assert(changed && result.Success && result.Plan != null, "Non-match cache fixture did not complete.");
                Assert(result.Plan.ExcludedCount == 0 && result.Plan.Warnings.Count == 0 &&
                    result.Plan.ExistingCount + result.Plan.Candidates.Count == 2 && Contains(result.Plan.Candidates, fixture.Snapshot),
                    "A non-match must retain the first source; later retention may be conservative.");
                Expect(fixture.Plan(request), 2, 0, 0);
            }
        }

        private static void SourceLengthChanged()
        {
            using (var fixture = new Fixture())
            {
                MediaItem changed = fixture.AddSource("IMG0002.JPG", fixture.Data);
                WorkRequest request = fixture.Request(fixture.Snapshot, changed);
                fixture.WriteFile(changed.SourcePath, new byte[fixture.Data.Length + 1]);
                Refused(fixture.Plan(request));
            }
        }

        private static void SourceTimeChanged()
        {
            using (var fixture = new Fixture())
            {
                MediaItem changed = fixture.AddSource("IMG0002.JPG", fixture.Data);
                WorkRequest request = fixture.Request(fixture.Snapshot, changed);
                fixture.SetTime(changed.SourcePath, new DateTime(changed.WriteTicks, DateTimeKind.Utc).AddSeconds(10));
                Refused(fixture.Plan(request));
            }
        }

        private static void SourceRemoved()
        {
            using (var fixture = new Fixture())
            {
                MediaItem removed = fixture.AddSource("IMG0002.JPG", fixture.Data);
                WorkRequest request = fixture.Request(fixture.Snapshot, removed);
                fixture.DeleteFile(removed.SourcePath);
                Refused(fixture.Plan(request));
            }
        }

        private static void SourceChangedDuringRun()
        {
            using (var fixture = new Fixture())
            {
                MediaItem second = fixture.AddSource("IMG0002.JPG", fixture.Data);
                bool changed = false;
                WorkResult result = fixture.Plan(fixture.Request(fixture.Snapshot, second), delegate(CandidateProgress progress)
                {
                    if (!changed && progress.Processed == 1)
                    { fixture.SetTime(second.SourcePath, new DateTime(second.WriteTicks, DateTimeKind.Utc).AddSeconds(10)); changed = true; }
                });
                Assert(changed, "Source snapshot mutation did not run after partial progress.");
                Refused(result);
            }
        }

        private static void MarkerLoss()
        {
            using (var fixture = new Fixture())
            {
                WorkRequest request = fixture.Request();
                Expect(fixture.Plan(request), 0, 0, 0, fixture.Snapshot);
                fixture.DeleteFile(fixture.Marker);
                Refused(fixture.Plan(request));
            }
        }

        private static void MarkerMismatch()
        {
            using (var fixture = new Fixture())
            {
                WorkRequest request = fixture.Request();
                fixture.WriteMarker(1, Guid.NewGuid().ToString("D"));
                Refused(fixture.Plan(request));
            }
        }

        private static void MarkerVersionMismatch()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteMarker(2, fixture.CardId);
                Refused(fixture.Plan(fixture.Request()));
            }
        }

        private static void DatExcluded()
        {
            using (var fixture = new Fixture())
            {
                MediaItem dat = fixture.AddSource("CAMERA.DAT", fixture.Data);
                Expect(fixture.Plan(fixture.Request(fixture.Snapshot, dat)), 0, 1, 0, fixture.Snapshot);
            }
        }

        private static void CustomExclusions()
        {
            using (var fixture = new Fixture())
            {
                MediaItem dat = fixture.AddSource("CAMERA.DAT", fixture.Data);
                MediaItem nef = fixture.AddSource("IMG0002.NEF", fixture.Data);
                WorkRequest request = fixture.Request(fixture.Snapshot, dat, nef);
                request.ExcludedExtensions.Add(".NEF");
                Expect(fixture.Plan(request), 0, 2, 0, fixture.Snapshot);
            }
        }

        private static void EmptyExclusions()
        {
            using (var fixture = new Fixture())
            {
                MediaItem nef = fixture.AddSource("IMG0002.NEF", fixture.Data);
                MediaItem dat = fixture.AddSource("CAMERA.DAT", fixture.Data);
                WorkRequest request = fixture.Request(fixture.Snapshot, nef, dat);
                request.ExcludedExtensions.Clear();
                Expect(fixture.Plan(request), 0, 1, 0, fixture.Snapshot, nef);
            }
        }

        private static void CaseInsensitiveExclusions()
        {
            using (var fixture = new Fixture())
            {
                MediaItem nef = fixture.AddSource("IMG0002.nEf", fixture.Data);
                MediaItem movie = fixture.AddSource("CLIP.mOv", fixture.Data);
                WorkRequest request = fixture.Request(fixture.Snapshot, nef, movie);
                request.ExcludedExtensions = new List<string> { ".dAt", " NEF ", ".nEf" };
                Expect(fixture.Plan(request), 0, 1, 0, fixture.Snapshot, movie);
            }
        }

        private static void PartialTargets()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteTarget("archive\\IMG0001.JPG.partial", fixture.Data);
                fixture.WriteTarget("archive\\IMG0001.PARTIAL.JPG", fixture.Data);
                Expect(fixture.Plan(fixture.Request()), 0, 0, 0, fixture.Snapshot);
            }
        }

        private static void PartialSourceSuffix()
        {
            using (var fixture = new Fixture())
            {
                MediaItem partial = fixture.AddSource("IMG0002.JPG.partial", fixture.Data);
                Expect(fixture.Plan(fixture.Request(fixture.Snapshot, partial)), 0, 1, 0, fixture.Snapshot);
            }
        }

        private static void PartialSourceRefused()
        {
            using (var fixture = new Fixture())
            {
                MediaItem partial = fixture.AddSource("IMG0002.PARTIAL.JPG", fixture.Data);
                Refused(fixture.Plan(fixture.Request(fixture.Snapshot, partial)));
            }
        }

        private static void OverlappingTargets()
        {
            using (var fixture = new Fixture())
            {
                foreach (string path in new[] { fixture.CardRoot, fixture.Root, Path.GetDirectoryName(fixture.Source) })
                {
                    WorkRequest request = fixture.Request();
                    request.ComparisonTargets = new List<DestinationRecord> { ImportSettings.ValidateDestination("Overlap fixture", path) };
                    Refused(fixture.Plan(request));
                }
            }
        }

        private static void JunctionPaths()
        {
            using (var fixture = new Fixture())
            {
                string sourceLink = Path.Combine(fixture.CardRoot, "DCIM", "LINK");
                string targetLink = Path.Combine(fixture.Root, "targetLink");
                string archive = fixture.Directory("junctionArchive");
                fixture.WriteFile(Path.Combine(archive, "same.JPG"), fixture.Data);
                string nestedLink = Path.Combine(fixture.TargetRoot, "LINK");
                // All link endpoints are inside this fixture. Cleanup removes links as leaves.
                fixture.CreateJunction(sourceLink, Path.GetDirectoryName(fixture.Source));
                Refused(fixture.Plan(fixture.Request(fixture.SnapshotOf(Path.Combine(sourceLink, Path.GetFileName(fixture.Source))))));
                fixture.CreateJunction(targetLink, archive);
                WorkRequest request = fixture.Request();
                request.ComparisonTargets = new List<DestinationRecord> { ImportSettings.ValidateDestination("Junction target fixture", targetLink) };
                Expect(fixture.Plan(request), 0, 0, 1, fixture.Snapshot);
                fixture.CreateJunction(nestedLink, archive);
                Expect(fixture.Plan(fixture.Request()), 0, 0, 0, fixture.Snapshot);
            }
        }

        private static void SignatureCard()
        {
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.MarkerId = Guid.NewGuid().ToString("D"); });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.Root = Path.Combine(disk.Root, "anotherCard"); });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.Serial = "synthetic-other-serial"; });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.Capacity++; });
        }

        private static void SignatureMedia()
        {
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.Media[0].Length++; });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.Media[0].WriteTicks++; });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.Media[0].SourcePath += ".renamed"; });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.Media[0].RelativePath += ".renamed"; });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { disk.Media.RemoveAt(0); });
        }

        private static void SignatureDestinations()
        {
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { state.Destinations[0].Id = Guid.NewGuid().ToString("D"); });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { state.Destinations[0].Path = Path.Combine(state.Destinations[0].Path, "other"); });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { state.Destinations[0].Enabled = false; });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { state.Destinations.Reverse(); });
        }

        private static void SignatureExclusions()
        {
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { state.ExcludedExtensions.Add(".NEF"); });
            SignatureChanged(delegate(DiskSnapshot disk, AppState state) { state.ExcludedExtensions.Clear(); });
        }

        private static void SignatureChanged(Action<DiskSnapshot, AppState> mutate)
        {
            using (var fixture = new Fixture())
            {
                DiskSnapshot disk = fixture.Disk();
                AppState state = fixture.State();
                string before = CandidatePlanner.Signature(disk, state);
                mutate(disk, state);
                Assert(before != CandidatePlanner.Signature(disk, state), "Signature did not change with a planning input.");
            }
        }

        private static void SignatureStable()
        {
            using (var fixture = new Fixture())
            {
                MediaItem second = fixture.AddSource("IMG0002.NEF", Changed(fixture.Data));
                DiskSnapshot disk = fixture.Disk();
                disk.Media.Add(second);
                AppState state = fixture.State();
                string before = CandidatePlanner.Signature(disk, state);
                Assert(before == CandidatePlanner.Signature(disk, state), "Unchanged signature is not deterministic.");
                disk.Media.Reverse();
                state.ExcludedExtensions = new List<string> { " DAT ", ".dAt" };
                Assert(before == CandidatePlanner.Signature(disk, state), "Equivalent snapshots and normalized exclusions changed the signature.");
            }
        }

        private static void EmptyAndInvalidRequests()
        {
            using (var fixture = new Fixture())
            {
                WorkRequest request = fixture.Request();
                request.CandidateFiles.Clear();
                Expect(fixture.Plan(request), 0, 0, 0);
                request = fixture.Request();
                request.ComparisonTargets.Clear();
                Expect(fixture.Plan(request), 0, 0, 0, fixture.Snapshot);
                Refused(fixture.Plan(null));
                request = fixture.Request(); request.CandidateFiles = null;
                Refused(fixture.Plan(request));
                request = fixture.Request(); request.ComparisonTargets = null;
                Refused(fixture.Plan(request));
            }
        }

        private static CandidatePlan Expect(WorkResult result, int existing, int excluded, int warnings, params MediaItem[] candidates)
        {
            SafeResult(result);
            Assert(result.Success && result.Plan != null && String.IsNullOrEmpty(result.Error), "Planning failed: " + result.Error);
            CandidatePlan plan = result.Plan;
            Assert(plan.Candidates != null && plan.Warnings != null && plan.ExistingCount == existing && plan.ExcludedCount == excluded &&
                plan.Warnings.Count == warnings && plan.Candidates.Count == candidates.Length, "Unexpected candidate, existing, excluded or warning counts.");
            for (int index = 0; index < candidates.Length; index++)
                Assert(SameSnapshot(plan.Candidates[index], candidates[index]), "Candidate snapshot/order changed or wrong source retained.");
            return plan;
        }

        private static void Refused(WorkResult result)
        {
            SafeResult(result);
            Assert(!result.Success && result.Plan == null && !String.IsNullOrWhiteSpace(result.Error), "Unsafe input exposed a successful or partial plan.");
        }

        private static void SafeResult(WorkResult result)
        {
            Assert(result != null && result.Receipt == null && !result.Deleted, "Planning must never authorize cleanup or delete a source.");
        }

        private static bool Contains(List<MediaItem> items, MediaItem expected)
        {
            foreach (MediaItem item in items) if (SameSnapshot(item, expected)) return true;
            return false;
        }

        private static bool SameSnapshot(MediaItem left, MediaItem right)
        {
            return left != null && right != null && left.SourcePath == right.SourcePath && left.RelativePath == right.RelativePath &&
                left.Length == right.Length && left.WriteTicks == right.WriteTicks;
        }

        private static MediaItem Clone(MediaItem item)
        {
            return new MediaItem { SourcePath = item.SourcePath, RelativePath = item.RelativePath, Length = item.Length, WriteTicks = item.WriteTicks };
        }

        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message ?? "Assertion failed."); }

        private static byte[] Changed(byte[] bytes)
        { byte[] other = (byte[])bytes.Clone(); other[0] ^= 255; return other; }

        private static string Digest(byte[] bytes)
        { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root, CardRoot, TargetRoot, Source, Destination, Marker, CardId;
            internal readonly byte[] Data;
            internal readonly MediaItem Snapshot;
            private readonly string parent, uniqueName;
            private static readonly DateTime Stamp = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);

            internal Fixture()
            {
                parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                Assert(!parent.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(Path.GetPathRoot(parent)).DriveType == DriveType.Fixed,
                    "Planner fixtures require a local fixed temp drive.");
                uniqueName = "candidate-planner-fixture-" + Guid.NewGuid().ToString("N");
                Root = Path.GetFullPath(Path.Combine(parent, uniqueName));
                AssertOwned(Root);
                Assert(!System.IO.Directory.Exists(Root) && !File.Exists(Root), "Fixture root already exists.");
                CardRoot = Path.Combine(Root, "tempCard");
                TargetRoot = Path.Combine(Root, "tempTarget");
                Source = Path.Combine(CardRoot, "DCIM", "100TEST", "IMG0001.JPG");
                Destination = Path.Combine(TargetRoot, "DCIM", "100TEST", "IMG0001.JPG");
                Marker = Path.Combine(CardRoot, IdentityVerifier.MarkerFileName);
                CardId = Guid.NewGuid().ToString("D");
                Data = new byte[4096];
                for (int index = 0; index < Data.Length; index++) Data[index] = (byte)((index * 31 + index / 257) % 251);
                System.IO.Directory.CreateDirectory(Root);
                try
                {
                    WriteFile(Source, Data);
                    Directory("tempTarget");
                    WriteMarker(1, CardId);
                    Snapshot = SnapshotOf(Source);
                }
                catch { Dispose(); throw; }
            }

            internal WorkRequest Request(params MediaItem[] files)
            {
                var snapshots = new List<MediaItem>();
                foreach (MediaItem item in files.Length == 0 ? new[] { Snapshot } : files) snapshots.Add(Clone(item));
                return new WorkRequest
                {
                    Operation = "plan", SourceRoot = CardRoot, CardId = CardId, ExpectedCapacity = 0, ExpectedSerial = null,
                    CandidateFiles = snapshots,
                    ComparisonTargets = new List<DestinationRecord> { ImportSettings.ValidateDestination("Local target fixture", TargetRoot) },
                    ExcludedExtensions = new List<string> { ".dat" }
                };
            }

            internal DiskSnapshot Disk()
            {
                return new DiskSnapshot { Root = CardRoot, Serial = "synthetic-serial", Capacity = 0,
                    MarkerId = CardId, Media = new List<MediaItem> { Clone(Snapshot) } };
            }

            internal AppState State()
            {
                var state = new AppState
                {
                    DestinationFailure = "Next", ExcludedExtensions = new List<string> { ".dat" },
                    Destinations = new List<DestinationRecord>
                    {
                        ImportSettings.ValidateDestination("First fixture", TargetRoot),
                        ImportSettings.ValidateDestination("Second fixture", Path.Combine(Root, "signatureFallback"))
                    }
                };
                ImportSettings.Normalize(state);
                return state;
            }

            internal MediaItem AddSource(string name, byte[] bytes)
            {
                string path = Path.Combine(CardRoot, "DCIM", "100TEST", name);
                WriteFile(path, bytes);
                return SnapshotOf(path);
            }

            internal MediaItem SnapshotOf(string path)
            {
                AssertOwned(path);
                var info = new FileInfo(path);
                Assert(info.Exists && info.FullName.StartsWith(CardRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Invalid source fixture snapshot.");
                return new MediaItem { SourcePath = info.FullName, RelativePath = info.FullName.Substring(CardRoot.Length + 1),
                    Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks };
            }

            internal string Directory(string relative)
            {
                string path = Path.Combine(Root, relative);
                AssertOwned(path);
                System.IO.Directory.CreateDirectory(path);
                return path;
            }

            internal void WriteFile(string path, byte[] bytes)
            {
                AssertOwned(path);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
                SetTime(path, Stamp);
            }

            internal void WriteTarget(string relative, byte[] bytes)
            { WriteFile(Path.Combine(TargetRoot, relative), bytes); }

            internal void WriteMarker(int version, string cardId)
            {
                AssertOwned(Marker);
                JsonFile.Write(Marker, new CardMarker { Version = version, CardId = cardId,
                    CreatedUtc = Stamp.ToString("o", CultureInfo.InvariantCulture) });
            }

            internal void MakeReadOnly(string path)
            { AssertOwned(path); File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }

            internal void SetTime(string path, DateTime stamp)
            { AssertOwned(path); File.SetLastWriteTimeUtc(path, stamp); }

            internal void DeleteFile(string path)
            { AssertOwned(path); File.Delete(path); }

            internal void MoveDirectory(string from, string to)
            { AssertOwned(from); AssertOwned(to); System.IO.Directory.Move(from, to); }

            internal void ReplaceTargetPreservingMetadata(byte[] bytes)
            {
                AssertOwned(Destination);
                var before = new FileInfo(Destination);
                long length = before.Length;
                DateTime stamp = before.LastWriteTimeUtc;
                Assert(bytes.LongLength == length, "Replacement fixture must preserve length.");
                string previous = Destination + ".previous-fixture";
                AssertOwned(previous);
                Assert(!File.Exists(previous), "Replacement backup is already occupied.");
                File.Move(Destination, previous);
                WriteFile(Destination, bytes);
                SetTime(Destination, stamp);
                DeleteFile(previous);
                var after = new FileInfo(Destination);
                Assert(after.Length == length && after.LastWriteTimeUtc == stamp, "Replacement did not preserve destination metadata.");
            }

            internal WorkResult Plan(WorkRequest request, Action<CandidateProgress> progress = null)
            {
                Dictionary<string, string> expected = Capture();
                var inputs = new List<MediaItem>();
                if (request != null && request.CandidateFiles != null)
                    foreach (MediaItem item in request.CandidateFiles) inputs.Add(Clone(item));
                WorkResult result = CandidatePlanner.Run(request, delegate(CandidateProgress update)
                {
                    AssertUnchanged(expected);
                    Assert(update != null && update.Processed >= 0 && update.Processed <= update.Total, "Invalid planner progress.");
                    if (progress != null) progress(update);
                    // Only explicit fixture mutations can update the expected read-only inventory.
                    expected = Capture();
                });
                AssertUnchanged(expected);
                SafeResult(result);
                if (request != null && request.CandidateFiles != null)
                {
                    Assert(request.CandidateFiles.Count == inputs.Count, "Planner mutated the caller's source list.");
                    for (int index = 0; index < inputs.Count; index++)
                        Assert(SameSnapshot(inputs[index], request.CandidateFiles[index]), "Planner mutated a caller source snapshot.");
                }
                return result;
            }

            private Dictionary<string, string> Capture()
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CaptureDirectory(Root, result);
                return result;
            }

            private void CaptureDirectory(string directory, Dictionary<string, string> result)
            {
                AssertOwned(directory);
                Assert((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "Inventory must not follow a reparse point.");
                result.Add(directory, "directory:" + File.GetAttributes(directory) + ":" + System.IO.Directory.GetLastWriteTimeUtc(directory).Ticks);
                foreach (string path in System.IO.Directory.GetFileSystemEntries(directory))
                {
                    AssertOwned(path);
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) result.Add(path, "reparse:" + attributes);
                    else if ((attributes & FileAttributes.Directory) != 0) CaptureDirectory(path, result);
                    else
                    {
                        var info = new FileInfo(path);
                        result.Add(path, info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks + ":" +
                            attributes + ":" + Digest(File.ReadAllBytes(path)));
                    }
                }
            }

            private void AssertUnchanged(Dictionary<string, string> expected)
            {
                Dictionary<string, string> actual = Capture();
                Assert(actual.Count == expected.Count, "Planning copied, created or removed fixture entries.");
                foreach (KeyValuePair<string, string> entry in expected)
                {
                    string value;
                    Assert(actual.TryGetValue(entry.Key, out value) && value == entry.Value,
                        "Planning changed fixture bytes, mtime, attributes or entries: " + entry.Key);
                }
            }

            internal void CreateJunction(string link, string target)
            {
                AssertOwned(link); AssertOwned(target);
                Assert(!System.IO.Directory.Exists(link) && !File.Exists(link), "Junction fixture path already exists.");
                System.IO.Directory.CreateDirectory(link);
                byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
                byte[] display = Encoding.Unicode.GetBytes(target);
                byte[] data = new byte[16 + substitute.Length + 2 + display.Length + 2];
                Array.Copy(BitConverter.GetBytes(0xA0000003u), 0, data, 0, 4);
                Array.Copy(BitConverter.GetBytes((ushort)(data.Length - 8)), 0, data, 4, 2);
                Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, data, 10, 2);
                Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, data, 12, 2);
                Array.Copy(BitConverter.GetBytes((ushort)display.Length), 0, data, 14, 2);
                Array.Copy(substitute, 0, data, 16, substitute.Length);
                Array.Copy(display, 0, data, 18 + substitute.Length, display.Length);
                using (SafeFileHandle handle = OpenJunction(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the owned junction fixture.");
                    int returned;
                    if (!DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the owned local junction fixture.");
                }
                Assert((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Fixture junction was not created.");
            }

            private void AssertOwned(string path)
            {
                string full = Path.GetFullPath(path);
                Assert(String.Equals(Path.GetDirectoryName(Root), parent, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(Root) == uniqueName && uniqueName.StartsWith("candidate-planner-fixture-", StringComparison.Ordinal) &&
                    (String.Equals(full, Root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                    "Refusing an operation outside the unique owned fixture directory.");
            }

            private void RemoveOwnedDirectory(string directory)
            {
                AssertOwned(directory);
                Assert((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "Refusing cleanup through a junction.");
                foreach (string path in System.IO.Directory.GetFileSystemEntries(directory))
                {
                    AssertOwned(path);
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // Remove the link itself; never traverse its target or delete recursively.
                        if ((attributes & FileAttributes.Directory) != 0) System.IO.Directory.Delete(path, false);
                        else File.Delete(path);
                    }
                    else if ((attributes & FileAttributes.Directory) != 0) RemoveOwnedDirectory(path);
                    else
                    {
                        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                        File.Delete(path);
                    }
                }
                System.IO.Directory.Delete(directory, false);
            }

            public void Dispose()
            {
                AssertOwned(Root);
                if (System.IO.Directory.Exists(Root)) RemoveOwnedDirectory(Root);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle OpenJunction(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    }
}
