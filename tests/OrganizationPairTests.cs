using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PhotoImportV2
{
    /// <summary>Actual planning over tiny synthetic TIFF/JPEG fixtures in an owned temporary tree.</summary>
    public static class OrganizationPairTests
    {
        public static string Run()
        {
            ConflictingDates();
            DifferentFolders();
            return "PASS: 2 organization pair regression fixtures: conflicting JPG/NEF dates leave NRW unknown; identical basenames stay within their original folder.";
        }

        private static void ConflictingDates()
        {
            using (var fixture = new Fixture())
            {
                MediaItem jpg = fixture.Add("DCIM/100TEST/IMAGE.JPG", Jpeg(Tiff("2024:02:28 23:59:59")));
                MediaItem nef = fixture.Add("DCIM/100TEST/IMAGE.NEF", Tiff("2024:02:29 00:00:01"));
                MediaItem nrw = fixture.Add("DCIM/100TEST/IMAGE.NRW", Tiff(null));
                foreach (bool reverse in new[] { false, true })
                {
                    CandidatePlan plan = fixture.Plan(reverse);
                    Captured(plan, jpg, "2024-02-28", "JPG", false);
                    Captured(plan, nef, "2024-02-29", "RAW", false);
                    Unknown(plan, nrw);
                }
            }
        }

        private static void DifferentFolders()
        {
            using (var fixture = new Fixture())
            {
                MediaItem dated = fixture.Add("DCIM/100TEST/IMAGE.NEF", Tiff("2024:02:29 12:00:00"));
                MediaItem sameFolder = fixture.Add("DCIM/100TEST/IMAGE.JPG", Jpeg(Tiff(null)));
                MediaItem otherFolder = fixture.Add("DCIM/200TEST/IMAGE.NRW", Tiff(null));
                foreach (bool reverse in new[] { false, true })
                {
                    CandidatePlan plan = fixture.Plan(reverse);
                    Captured(plan, dated, "2024-02-29", "RAW", false);
                    Captured(plan, sameFolder, "2024-02-29", "JPG", true);
                    Unknown(plan, otherFolder);
                }
            }
        }

        private static void Captured(CandidatePlan plan, MediaItem original, string date, string category, bool recovered)
        {
            MediaItem item = Find(plan, original);
            Assert(item.CaptureDate == date && item.TargetRelativePath == date + "/" + category + "/" + Path.GetFileName(original.SourcePath),
                "Valid capture date or original filename was lost: " + original.RelativePath);
            Assert(!String.IsNullOrEmpty(item.CaptureDateSource) &&
                (recovered ? item.CaptureDateSource == "同组照片拍摄时间" : item.CaptureDateSource.StartsWith("EXIF", StringComparison.Ordinal)),
                "Date provenance does not distinguish original metadata from pair recovery.");
        }

        private static void Unknown(CandidatePlan plan, MediaItem original)
        {
            MediaItem item = Find(plan, original);
            Assert(item.CaptureDate == "未知日期" && item.CaptureDateSource == "未知日期" &&
                item.TargetRelativePath == "未知日期/RAW/" + Path.GetFileName(original.SourcePath),
                "Ambiguous or unrelated metadata leaked into the missing NRW: " + original.RelativePath);
        }

        private static MediaItem Find(CandidatePlan plan, MediaItem original)
        {
            MediaItem item = plan.Candidates.Single(f => f.SourcePath == original.SourcePath);
            Assert(item.RelativePath == original.RelativePath && item.Length == original.Length && item.WriteTicks == original.WriteTicks,
                "Planning changed the original source snapshot.");
            Assert(OrganizationSettings.Target(item) == item.TargetRelativePath, "Planned target failed common path validation.");
            return item;
        }

        private static byte[] Tiff(string date)
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            {
                writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write((uint)8);
                writer.Write((ushort)(date == null ? 0 : 1));
                if (date != null)
                {
                    byte[] text = Encoding.ASCII.GetBytes(date + "\0");
                    Assert(text.Length == 20, "Synthetic EXIF date must occupy 20 bytes.");
                    writer.Write((ushort)0x9003); writer.Write((ushort)2); writer.Write((uint)20); writer.Write((uint)26);
                    writer.Write((uint)0); writer.Write(text);
                }
                else writer.Write((uint)0);
                writer.Flush(); return buffer.ToArray();
            }
        }

        private static byte[] Jpeg(byte[] tiff)
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            {
                int length = tiff.Length + 8; // APP1 length includes two length bytes and Exif\0\0.
                writer.Write(new byte[] { 0xff, 0xd8, 0xff, 0xe1, (byte)(length >> 8), (byte)length });
                writer.Write(new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 });
                writer.Write(tiff); writer.Write(new byte[] { 0xff, 0xd9 });
                writer.Flush(); return buffer.ToArray();
            }
        }

        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        private sealed class Fixture : IDisposable
        {
            private readonly string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            private readonly string name = "photoimport-pairs-" + Guid.NewGuid().ToString("N");
            private readonly string root, card, target, id = Guid.NewGuid().ToString("D");
            private readonly List<MediaItem> files = new List<MediaItem>();
            private readonly Dictionary<string, byte[]> bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            internal Fixture()
            {
                root = Path.GetFullPath(Path.Combine(parent, name));
                card = Path.Combine(root, "card"); target = Path.Combine(root, "target");
                try
                {
                    Owned(root); Directory.CreateDirectory(root);
                    Owned(card); Directory.CreateDirectory(card);
                    Owned(target); Directory.CreateDirectory(target);
                    string marker = Path.Combine(card, IdentityVerifier.MarkerFileName); Owned(marker);
                    JsonFile.Write(marker, new CardMarker { Version = 1, CardId = id });
                }
                catch { Dispose(); throw; }
            }

            internal MediaItem Add(string relative, byte[] data)
            {
                string path = Path.GetFullPath(Path.Combine(card, relative.Replace('/', '\\')));
                Owned(path); Assert(path.StartsWith(Path.Combine(card, "DCIM") + "\\", StringComparison.OrdinalIgnoreCase), "Fixture source escaped DCIM.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, data);
                File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc));
                var info = new FileInfo(path);
                var item = new MediaItem { SourcePath = path, RelativePath = relative.Replace('/', '\\'), Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks };
                files.Add(item); bytes.Add(path, (byte[])data.Clone()); return item;
            }

            internal CandidatePlan Plan(bool reverse)
            {
                var requested = new List<MediaItem>(files); if (reverse) requested.Reverse();
                var request = new WorkRequest { Operation = "plan", SourceRoot = card, CardId = id,
                    CandidateFiles = requested, ComparisonTargets = new List<DestinationRecord> { ImportSettings.ValidateDestination("Synthetic pair target", target) },
                    ExcludedExtensions = new List<string>(), ImportDate = "2025-01-01",
                    Organization = new OrganizationOptions { Mode = "capture", DateLayout = "day", MissingDate = "unknown", SeparateMedia = true, VideoTimeMode = "camera", TimeZoneId = "UTC" } };
                WorkResult result = CandidatePlanner.Run(request);
                Assert(result.Success && result.Plan != null, "Synthetic pair planning failed: " + result.Error);
                Assert(result.Receipt == null && !result.Deleted && result.Plan.Candidates.Count == files.Count &&
                    result.Plan.ExistingCount == 0 && result.Plan.ExcludedCount == 0 && result.Plan.Warnings.Count == 0,
                    "Pair planning suppressed media or gained transfer/cleanup authority.");
                Assert(Directory.GetFileSystemEntries(target).Length == 0, "Read-only planning created destination paths.");
                foreach (MediaItem original in files)
                {
                    Owned(original.SourcePath);
                    Assert(original.TargetRelativePath == null && original.CaptureDate == null && original.CaptureDateSource == null,
                        "Pair planning mutated caller-owned organization fields.");
                    Assert(File.ReadAllBytes(original.SourcePath).SequenceEqual(bytes[original.SourcePath]) &&
                        File.GetLastWriteTimeUtc(original.SourcePath).Ticks == original.WriteTicks,
                        "Pair planning changed synthetic source bytes or modification time.");
                }
                return result.Plan;
            }

            private void Owned(string path)
            {
                string full = Path.GetFullPath(path);
                Assert(String.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(root) == name && name.StartsWith("photoimport-pairs-", StringComparison.Ordinal) &&
                    (String.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)),
                    "Refusing an operation outside the owned pair fixture.");
            }

            private void Remove(string directory)
            {
                Owned(directory);
                Assert((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "Refusing fixture cleanup through a link.");
                foreach (string path in Directory.GetFileSystemEntries(directory))
                {
                    Owned(path); FileAttributes attributes = File.GetAttributes(path);
                    Assert((attributes & FileAttributes.ReparsePoint) == 0, "Refusing cleanup of a fixture link.");
                    if ((attributes & FileAttributes.Directory) != 0) Remove(path);
                    else File.Delete(path);
                }
                Directory.Delete(directory, false);
            }

            public void Dispose()
            { Owned(root); if (Directory.Exists(root)) Remove(root); }
        }
    }
}
