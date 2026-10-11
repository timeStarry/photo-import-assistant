using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Web.Script.Serialization;

namespace PhotoImportV2
{
    /// <summary>In-memory policy, date and path fixtures; no cards, files, metadata or live configuration.</summary>
    public static class OrganizationTests
    {
        public static string Run()
        {
            var passed = new List<string>();
            Test(passed, "independent defaults and local time zone", Defaults);
            Test(passed, "individual missing settings default independently", PartialDefaults);
            Test(passed, "normalized snapshots do not share caller state", Clones);
            Test(passed, "every policy value and canonical spelling", PolicyValues);
            Test(passed, "invalid policies are rejected", InvalidPolicies);
            Test(passed, "explicit canonical and invalid time zones", TimeZones);
            Test(passed, "keys include every frozen setting", Keys);
            Test(passed, "concise Chinese summaries show only active layout and media policy", Summaries);
            Test(passed, "capture wall dates are never converted again", CaptureWallDates);
            Test(passed, "null and empty capture info become unknown", NullCapture);
            Test(passed, "capture provenance survives and missing labels default", CaptureSources);
            Test(passed, "metadata describes capture even in other layouts", CaptureOtherModes);
            Test(passed, "modified UTC ticks convert across days and daylight saving", ModifiedDates);
            Test(passed, "invalid modified timestamps remain unknown", InvalidModifiedDates);
            Test(passed, "caller time zone changes cannot alter a frozen snapshot", FrozenTimeZone);
            Test(passed, "capture date hierarchy", CaptureLayouts);
            Test(passed, "frozen import date hierarchy", ImportLayouts);
            Test(passed, "import dates require exact valid ISO dates", InvalidImportDates);
            Test(passed, "unknown dates do not invent a year or month", UnknownLayouts);
            Test(passed, "preserve retains original DCIM hierarchy", PreservePaths);
            Test(passed, "flat uses only the original basename", FlatPaths);
            Test(passed, "all nine media extensions including TIFF", MediaTypes);
            Test(passed, "media category goes inside date or original parent", MediaPositions);
            Test(passed, "raw Unicode spaces percent and filename case survive", OriginalNames);
            Test(passed, "Apply leaves original paths and metadata inputs unchanged", OriginalState);
            Test(passed, "null target preserves safe legacy relative path", LegacyTargets);
            Test(passed, "explicit targets normalize mixed separators", TargetSeparators);
            Test(passed, "explicit empty targets and renamed basenames are refused", RenamedTargets);
            Test(passed, "absolute traversal URL and ambiguous paths are refused", UnsafePaths);
            Test(passed, "unsafe Windows components are refused", UnsafeComponents);
            Test(passed, "reserved Windows device aliases are refused", DevicePaths);
            Test(passed, "partial paths are refused", PartialPaths);
            Test(passed, "unsafe original paths cannot be rescued by an explicit target", UnsafeOriginals);
            Test(passed, "path size and Unicode validity boundaries", PathBoundaries);
            Test(passed, "null files are refused", NullFiles);
            Test(passed, "failed Apply leaves all organization fields unchanged", AtomicApply);
            Test(passed, "dates keys and media rules are culture independent", CultureIndependent);
            Test(passed, "organization policy JSON roundtrip", PolicyJson);
            Test(passed, "synthetic config JSON roundtrip and clone isolation", ConfigJson);
            Test(passed, "invalid JSON policies are rejected on normalization", InvalidJsonPolicy);
            return "PASS: " + passed.Count.ToString(CultureInfo.InvariantCulture) +
                " organization fixture tests: " + String.Join("; ", passed.ToArray()) + ".";
        }

        private static void Defaults()
        {
            OrganizationOptions a = OrganizationSettings.Normalize(null), b = OrganizationSettings.Normalize(null);
            Equal(a.Mode, "capture"); Equal(a.DateLayout, "day"); Equal(a.MissingDate, "unknown");
            Equal(a.VideoTimeMode, "camera"); Equal(a.TimeZoneId, TimeZoneInfo.Local.Id);
            Assert(!a.SeparateMedia && !Object.ReferenceEquals(a, b), "Default snapshot shares state.");
            a.Mode = "flat"; a.TimeZoneId = "UTC"; a.SeparateMedia = true;
            Equal(b.Mode, "capture"); Equal(b.TimeZoneId, TimeZoneInfo.Local.Id); Assert(!b.SeparateMedia, "Defaults mutated.");
        }

        private static void PartialDefaults()
        {
            var input = new OrganizationOptions { Mode = "preserve", SeparateMedia = true };
            OrganizationOptions result = OrganizationSettings.Normalize(input);
            Equal(result.Mode, "preserve"); Equal(result.DateLayout, "day"); Equal(result.MissingDate, "unknown");
            Equal(result.VideoTimeMode, "camera"); Equal(result.TimeZoneId, TimeZoneInfo.Local.Id);
            Assert(result.SeparateMedia && input.DateLayout == null && input.TimeZoneId == null, "Partial input was changed.");
            result = OrganizationSettings.Normalize(new OrganizationOptions { Mode = " ", DateLayout = "", MissingDate = "\t", TimeZoneId = " " });
            Equal(OrganizationSettings.Key(result), OrganizationSettings.Key(null));
        }

        private static void Clones()
        {
            OrganizationOptions input = Options("import", "month", true), a = OrganizationSettings.Normalize(input), b = OrganizationSettings.Normalize(a);
            Assert(!Object.ReferenceEquals(input, a) && !Object.ReferenceEquals(a, b), "Policy reference was reused.");
            string key = OrganizationSettings.Key(b);
            input.Mode = "flat"; input.TimeZoneId = "China Standard Time";
            a.DateLayout = "year"; a.SeparateMedia = false; a.MissingDate = "modified"; a.VideoTimeMode = "utc";
            Equal(OrganizationSettings.Key(b), key); Equal(b.TimeZoneId, "UTC");
        }

        private static void PolicyValues()
        {
            foreach (string mode in new[] { "capture", "import", "preserve", "flat" })
                foreach (string layout in new[] { "day", "year", "month" })
                    foreach (string missing in new[] { "unknown", "modified" })
                        foreach (string video in new[] { "camera", "utc" })
                        {
                            OrganizationOptions input = Options(" " + mode.ToUpperInvariant() + " ", layout.ToUpperInvariant(), true);
                            input.MissingDate = missing.ToUpperInvariant(); input.VideoTimeMode = video.ToUpperInvariant();
                            OrganizationOptions n = OrganizationSettings.Normalize(input);
                            Equal(n.Mode, mode); Equal(n.DateLayout, layout); Equal(n.MissingDate, missing); Equal(n.VideoTimeMode, video);
                        }
        }

        private static void InvalidPolicies()
        {
            foreach (string value in new[] { "other", "capture/import", "../day", "UTC\0", "拍摄", "camera|utc" })
            {
                Refused(delegate { OrganizationSettings.Normalize(new OrganizationOptions { Mode = value }); });
                Refused(delegate { OrganizationSettings.Normalize(new OrganizationOptions { DateLayout = value }); });
                Refused(delegate { OrganizationSettings.Normalize(new OrganizationOptions { MissingDate = value }); });
                Refused(delegate { OrganizationSettings.Normalize(new OrganizationOptions { VideoTimeMode = value }); });
            }
        }

        private static void TimeZones()
        {
            Equal(OrganizationSettings.Normalize(new OrganizationOptions { TimeZoneId = " UTC " }).TimeZoneId, "UTC");
            Equal(OrganizationSettings.Normalize(new OrganizationOptions { TimeZoneId = "China Standard Time" }).TimeZoneId, "China Standard Time");
            foreach (string id in new[] { "Synthetic missing zone", "../UTC", "UTC\0", "UTC\nInjected", "\ud800", new string('x', 4096) })
                Refused(delegate { OrganizationSettings.Normalize(new OrganizationOptions { TimeZoneId = id }); });
        }

        private static void Keys()
        {
            OrganizationOptions original = Options("capture", "day", false);
            string key = OrganizationSettings.Key(original);
            Equal(key, OrganizationSettings.Key(OrganizationSettings.Normalize(original)));
            Equal(key, OrganizationSettings.Key(new OrganizationOptions { Mode = " CAPTURE ", TimeZoneId = " UTC " }));
            var alternatives = new List<OrganizationOptions>();
            OrganizationOptions o = OrganizationSettings.Normalize(original); o.Mode = "flat"; alternatives.Add(o);
            o = OrganizationSettings.Normalize(original); o.DateLayout = "year"; alternatives.Add(o);
            o = OrganizationSettings.Normalize(original); o.MissingDate = "modified"; alternatives.Add(o);
            o = OrganizationSettings.Normalize(original); o.SeparateMedia = true; alternatives.Add(o);
            o = OrganizationSettings.Normalize(original); o.VideoTimeMode = "utc"; alternatives.Add(o);
            o = OrganizationSettings.Normalize(original); o.TimeZoneId = "China Standard Time"; alternatives.Add(o);
            var seen = new HashSet<string> { key };
            foreach (OrganizationOptions changed in alternatives) Assert(seen.Add(OrganizationSettings.Key(changed)), "Key omitted a setting.");
            Refused(delegate { OrganizationSettings.Key(new OrganizationOptions { Mode = "invalid" }); });
        }

        private static void Summaries()
        {
            string[] modes = { "capture", "import", "preserve", "flat" };
            string[] labels = { "拍摄日期", "导入日期", "原始目录", "同一目录" };
            for (int i = 0; i < modes.Length; i++)
            {
                string text = OrganizationSettings.Summary(Options(modes[i], "month", true));
                Assert(text.Contains(labels[i]) && text.EndsWith("RAW/JPG分目录", StringComparison.Ordinal), "Summary omitted active policy.");
                Assert(text.Contains("yyyy/MM/yyyy-MM-dd") == (i < 2), "Summary included an inactive date layout.");
                Assert(!text.Contains("视频") && !text.Contains("时区") && !text.Contains("UTC") && !text.Contains("缺失"), "Summary exposed advanced settings.");
            }
            OrganizationOptions o = Options("capture", "year", false); o.MissingDate = "modified"; o.VideoTimeMode = "utc";
            string summary = OrganizationSettings.Summary(o);
            Equal(summary, "按拍摄日期 · yyyy/yyyy-MM-dd · 混合保存");
            Equal(OrganizationSettings.Summary(null), "按拍摄日期 · yyyy-MM-dd · 混合保存");
        }

        private static void CaptureWallDates()
        {
            foreach (DateTimeKind kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc, DateTimeKind.Local })
                foreach (string video in new[] { "camera", "utc" })
                {
                    MediaItem file = File("DCIM/100TEST/IMG.JpG");
                    OrganizationOptions o = Options("capture", "day", false); o.TimeZoneId = "China Standard Time"; o.VideoTimeMode = video;
                    OrganizationSettings.Apply(file, o, "bad irrelevant date", new CaptureInfo { Taken = new DateTime(2024, 2, 29, 23, 59, 59, kind), Source = "EXIF" });
                    Equal(file.CaptureDate, "2024-02-29"); Equal(file.CaptureDateSource, "EXIF"); Equal(file.TargetRelativePath, "2024-02-29/IMG.JpG");
                }
        }

        private static void NullCapture()
        {
            foreach (CaptureInfo capture in new[] { null, new CaptureInfo(), new CaptureInfo { Source = "EXIF" } })
            {
                MediaItem file = File("DCIM/IMG.JPG");
                file.CaptureDate = "old"; file.CaptureDateSource = "old"; file.TargetRelativePath = "old";
                OrganizationSettings.Apply(file, null, null, capture);
                Equal(file.CaptureDate, "未知日期"); Equal(file.CaptureDateSource, "未知日期"); Equal(file.TargetRelativePath, "未知日期/IMG.JPG");
            }
        }

        private static void CaptureSources()
        {
            foreach (string source in new[] { null, "", " ", "bad\0source", "\ud800" })
            {
                MediaItem file = File("DCIM/IMG.JPG"); CaptureInfo capture = Capture(); capture.Source = source;
                OrganizationSettings.Apply(file, Options("capture", "day", false), null, capture);
                Equal(file.CaptureDate, "2024-02-29"); Equal(file.CaptureDateSource, "拍摄时间");
            }
            MediaItem paired = File("DCIM/IMG.NEF"); CaptureInfo pair = Capture(); pair.Source = "同组照片拍摄时间";
            OrganizationSettings.Apply(paired, null, null, pair); Equal(paired.CaptureDateSource, pair.Source);
        }

        private static void CaptureOtherModes()
        {
            foreach (string mode in new[] { "import", "preserve", "flat" })
            {
                MediaItem file = File("DCIM/IMG.JPG");
                OrganizationSettings.Apply(file, Options(mode, "day", false), "2025-01-01", Capture());
                Equal(file.CaptureDate, "2024-02-29"); Equal(file.CaptureDateSource, "EXIF");
            }
        }

        private static void ModifiedDates()
        {
            Modified("UTC", new DateTime(2024, 1, 1, 18, 0, 0, DateTimeKind.Utc), "2024-01-01");
            Modified("China Standard Time", new DateTime(2024, 1, 1, 18, 0, 0, DateTimeKind.Utc), "2024-01-02");
            Modified("Pacific Standard Time", new DateTime(2024, 1, 1, 6, 0, 0, DateTimeKind.Utc), "2023-12-31");
            Modified("Pacific Standard Time", new DateTime(2024, 7, 1, 7, 30, 0, DateTimeKind.Utc), "2024-07-01");
            OrganizationOptions o = Options("capture", "day", false); o.MissingDate = "modified";
            MediaItem file = File("DCIM/IMG.JPG"); OrganizationSettings.Apply(file, o, null, Capture());
            Equal(file.CaptureDate, "2024-02-29"); Equal(file.CaptureDateSource, "EXIF");
        }

        private static void Modified(string zone, DateTime utc, string expected)
        {
            OrganizationOptions o = Options("capture", "day", false); o.MissingDate = "modified"; o.TimeZoneId = zone;
            MediaItem file = File("DCIM/IMG.JPG"); file.WriteTicks = utc.Ticks;
            OrganizationSettings.Apply(file, o, null, null);
            Equal(file.CaptureDate, expected); Equal(file.CaptureDateSource, "文件修改时间"); Equal(file.TargetRelativePath, expected + "/IMG.JPG");
        }

        private static void InvalidModifiedDates()
        {
            foreach (long ticks in new[] { 0L, -1L, DateTime.MaxValue.Ticks + 1, Int64.MaxValue })
            {
                MediaItem file = File("DCIM/IMG.JPG"); file.WriteTicks = ticks;
                OrganizationOptions o = Options("capture", "month", false); o.MissingDate = "modified";
                OrganizationSettings.Apply(file, o, null, null);
                Equal(file.CaptureDate, "未知日期"); Equal(file.CaptureDateSource, "未知日期");
            }
        }

        private static void FrozenTimeZone()
        {
            OrganizationOptions input = Options("capture", "day", false); input.MissingDate = "modified"; input.TimeZoneId = "China Standard Time";
            OrganizationOptions frozen = OrganizationSettings.Normalize(input); input.TimeZoneId = "UTC";
            MediaItem file = File("DCIM/IMG.JPG"); file.WriteTicks = new DateTime(2024, 1, 1, 18, 0, 0, DateTimeKind.Utc).Ticks;
            OrganizationSettings.Apply(file, frozen, null, null); Equal(file.CaptureDate, "2024-01-02");
            OrganizationOptions local = OrganizationSettings.Normalize(null); Equal(local.TimeZoneId, TimeZoneInfo.Local.Id);
            Equal(OrganizationSettings.Normalize(local).TimeZoneId, local.TimeZoneId);
        }

        private static void CaptureLayouts()
        {
            string[] layouts = { "day", "year", "month" }, parents = { "2024-02-29", "2024/2024-02-29", "2024/02/2024-02-29" };
            for (int i = 0; i < layouts.Length; i++) ExpectPath("capture", layouts[i], false, "DCIM/IMG.JPG", null, Capture(), parents[i] + "/IMG.JPG");
        }

        private static void ImportLayouts()
        {
            string[] layouts = { "day", "year", "month" }, parents = { "2025-01-02", "2025/2025-01-02", "2025/01/2025-01-02" };
            for (int i = 0; i < layouts.Length; i++)
            {
                ExpectPath("import", layouts[i], false, "DCIM/IMG.JPG", "2025-01-02", Capture(), parents[i] + "/IMG.JPG");
                ExpectPath("import", layouts[i], false, "DCIM/IMG.JPG", "2025-01-02", null, parents[i] + "/IMG.JPG");
            }
            ExpectPath("import", "day", false, "DCIM/IMG.JPG", "2024-02-29", null, "2024-02-29/IMG.JPG");
        }

        private static void InvalidImportDates()
        {
            foreach (string date in new[] { null, "", "未知日期", "2023-02-29", "2024-13-01", "0000-01-01", "2024-2-09", "2024/02/09", " 2024-02-09", "2024-02-09 ", "2024-02-09T00:00:00Z", "../2024-02-09" })
                Refused(delegate { OrganizationSettings.Apply(File("DCIM/IMG.JPG"), Options("import", "day", false), date, null); });
            ExpectPath("preserve", "day", false, "DCIM/IMG.JPG", "bad irrelevant import date", null, "DCIM/IMG.JPG");
            ExpectPath("flat", "day", false, "DCIM/IMG.JPG", null, null, "IMG.JPG");
        }

        private static void UnknownLayouts()
        {
            foreach (string layout in new[] { "day", "year", "month" })
                ExpectPath("capture", layout, true, "DCIM/IMG.NEF", null, null, "未知日期/RAW/IMG.NEF");
        }

        private static void PreservePaths()
        {
            foreach (string path in new[] { "DCIM/100TEST/IMG.JPG", @"DCIM\100TEST\IMG.JPG", "DCIM/100TEST\\Nested/IMG.JPG" })
                ExpectPath("preserve", "month", false, path, null, null, path.Replace('\\', '/'));
        }

        private static void FlatPaths()
        {
            ExpectPath("flat", "year", false, "DCIM/100TEST/Nested/IMG.JpG", null, Capture(), "IMG.JpG");
            ExpectPath("flat", "month", false, "IMG.NEF", null, null, "IMG.NEF");
        }

        private static void MediaTypes()
        {
            string[] extensions = { "NEF", "nrw", "JPG", "jpeg", "MOV", "mp4", "AVI", "tif", "TIFF" };
            string[] categories = { "RAW", "RAW", "JPG", "JPG", "VIDEO", "VIDEO", "VIDEO", "TIFF", "TIFF" };
            for (int i = 0; i < extensions.Length; i++)
            {
                string name = "Img.Mixed." + extensions[i];
                ExpectPath("capture", "day", true, "DCIM/" + name, null, Capture(), "2024-02-29/" + categories[i] + "/" + name);
                ExpectPath("capture", "day", false, "DCIM/" + name, null, Capture(), "2024-02-29/" + name);
            }
            foreach (string name in new[] { "IMG", "IMG.dat", "IMG.jpg.backup", "IMG.raw", "TIFF.JPG" })
                ExpectPath("flat", "day", true, "DCIM/" + name, null, null, (name == "TIFF.JPG" ? "JPG/" : "") + name);
        }

        private static void MediaPositions()
        {
            ExpectPath("capture", "month", true, "DCIM/100TEST/IMG.TIFF", null, Capture(), "2024/02/2024-02-29/TIFF/IMG.TIFF");
            ExpectPath("import", "year", true, "DCIM/100TEST/IMG.NEF", "2025-01-02", null, "2025/2025-01-02/RAW/IMG.NEF");
            ExpectPath("preserve", "day", true, "DCIM/100TEST/IMG.JPG", null, null, "DCIM/100TEST/JPG/IMG.JPG");
            ExpectPath("flat", "day", true, "DCIM/100TEST/CLIP.MOV", null, null, "VIDEO/CLIP.MOV");
            ExpectPath("preserve", "day", true, "IMG.tif", null, null, "TIFF/IMG.tif");
        }

        private static void OriginalNames()
        {
            foreach (string name in new[] { "照片 01.JpG", "100% + #1.JPG", "literal%20name.JPG", ".hidden.NEF", "IMG.0001.TIFF", "图\ud83d\udcf7.MP4" })
            {
                ExpectPath("flat", "day", false, "DCIM/" + name, null, null, name);
                ExpectPath("preserve", "day", false, "DCIM/Folder #1/" + name, null, null, "DCIM/Folder #1/" + name);
            }
        }

        private static void OriginalState()
        {
            MediaItem file = File(@"DCIM\100TEST\IMG.JPG"); file.SourcePath = "synthetic://card/source"; file.Length = 123;
            long ticks = file.WriteTicks; OrganizationOptions o = Options("capture", "month", true); CaptureInfo capture = Capture();
            string key = OrganizationSettings.Key(o);
            OrganizationSettings.Apply(file, o, null, capture);
            Equal(file.RelativePath, @"DCIM\100TEST\IMG.JPG"); Equal(file.SourcePath, "synthetic://card/source");
            Assert(file.Length == 123 && file.WriteTicks == ticks && capture.Taken == Capture().Taken, "Input metadata changed.");
            Equal(capture.Source, "EXIF"); Equal(key, OrganizationSettings.Key(o));
        }

        private static void LegacyTargets()
        {
            foreach (string path in new[] { "DCIM/100TEST/IMG.JPG", @"DCIM\100TEST\IMG.JPG", "IMG.JPG", "DCIM/Folder #1/100% IMG.JPG" })
            {
                MediaItem file = File(path); Equal(OrganizationSettings.Target(file), path.Replace('\\', '/'));
                Assert(file.TargetRelativePath == null, "Target froze legacy organization implicitly."); Equal(file.RelativePath, path);
            }
        }

        private static void TargetSeparators()
        {
            MediaItem file = File(@"DCIM\100TEST\IMG.JPG"); file.TargetRelativePath = "2024\\02/2024-02-29\\JPG/IMG.JPG";
            Equal(OrganizationSettings.Target(file), "2024/02/2024-02-29/JPG/IMG.JPG");
            Equal(file.TargetRelativePath, "2024\\02/2024-02-29\\JPG/IMG.JPG");
        }

        private static void RenamedTargets()
        {
            foreach (string path in new[] { "", " ", "2024/OTHER.JPG", "2024/img.JPG", "2024/IMG.jpg", "2024/IMG.JPG.backup", "2024/IMG.NEF" })
                RejectTarget(path);
        }

        private static void UnsafePaths()
        {
            foreach (string path in new[] { "/IMG.JPG", @"\IMG.JPG", @"C:\IMG.JPG", "C:IMG.JPG", @"\\server\share\IMG.JPG", @"\\?\C:\IMG.JPG", @"\\.\C:\IMG.JPG", @"\??\C:\IMG.JPG", "https://example/IMG.JPG", "s3://bucket/IMG.JPG", "../IMG.JPG", "./IMG.JPG", "date/../IMG.JPG", "date/./IMG.JPG", "date//IMG.JPG", @"date\\IMG.JPG", "date/IMG.JPG/", "date\\../IMG.JPG" })
                RejectTarget(path);
        }

        private static void UnsafeComponents()
        {
            foreach (string part in new[] { "date.", "date ", ".. ", ". ", "date:stream", "date?", "date*", "date|x", "date<x", "date>x", "date\"x", "date\0x", "date\nx", "date\u007fx" })
                RejectTarget(part + "/IMG.JPG");
        }

        private static void DevicePaths()
        {
            foreach (string stem in new[] { "CON", "con", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM0", "COM1", "COM9", "LPT0", "LPT1", "LPT9", "COM\u00b9", "LPT\u00b2", "COM\u00b3" })
            {
                RejectTarget(stem + "/IMG.JPG"); RejectTarget(stem + ".data/IMG.JPG"); RejectTarget(stem + " .data/IMG.JPG");
                MediaItem file = File("DCIM/" + stem + ".JPG"); Refused(delegate { OrganizationSettings.Target(file); });
            }
            MediaItem safe = File("DCIM/IMG.JPG"); safe.TargetRelativePath = "COM10/CONSOLE/IMG.JPG";
            Equal(OrganizationSettings.Target(safe), safe.TargetRelativePath);
        }

        private static void PartialPaths()
        {
            foreach (string path in new[] { "work.partial/IMG.JPG", "work.PARTIAL.extra/IMG.JPG", "date/.partial/IMG.JPG" }) RejectTarget(path);
            foreach (string name in new[] { "IMG.partial.JPG", "IMG.PARTIAL" })
            {
                MediaItem file = File("DCIM/" + name); file.TargetRelativePath = "date/" + name;
                Refused(delegate { OrganizationSettings.Target(file); });
            }
        }

        private static void UnsafeOriginals()
        {
            foreach (string path in new[] { null, "", "../IMG.JPG", "DCIM/../IMG.JPG", @"C:\DCIM\IMG.JPG", "DCIM/NUL/IMG.JPG", "DCIM/work.partial/IMG.JPG", "DCIM/IMG.JPG " })
            {
                MediaItem file = File(path); file.TargetRelativePath = "date/IMG.JPG";
                Refused(delegate { OrganizationSettings.Target(file); });
                Refused(delegate { OrganizationSettings.Apply(file, Options("flat", "day", false), null, null); });
            }
        }

        private static void PathBoundaries()
        {
            MediaItem file = File("DCIM/IMG.JPG"); file.TargetRelativePath = new string('a', 255) + "/IMG.JPG";
            Equal(OrganizationSettings.Target(file), file.TargetRelativePath);
            RejectTarget(new string('a', 256) + "/IMG.JPG");
            string parent = String.Join("/", new string[] { new string('a', 255), new string('b', 255), new string('c', 255), new string('d', 255) });
            RejectTarget(parent + "/" + parent + "/" + parent + "/" + parent + "/IMG.JPG");
            foreach (string broken in new[] { "\ud800", "\udc00", "x\ud800y" }) RejectTarget(broken + "/IMG.JPG");
        }

        private static void NullFiles()
        {
            Refused(delegate { OrganizationSettings.Target(null); });
            Refused(delegate { OrganizationSettings.Apply(null, null, null, null); });
        }

        private static void AtomicApply()
        {
            MediaItem file = File("DCIM/IMG.JPG"); file.CaptureDate = "old date"; file.CaptureDateSource = "old source"; file.TargetRelativePath = "old target";
            Refused(delegate { OrganizationSettings.Apply(file, Options("import", "day", false), "2024-02-30", Capture()); });
            Equal(file.CaptureDate, "old date"); Equal(file.CaptureDateSource, "old source"); Equal(file.TargetRelativePath, "old target");
            OrganizationOptions o = Options("capture", "day", false); o.TimeZoneId = "Synthetic missing zone";
            Refused(delegate { OrganizationSettings.Apply(file, o, null, Capture()); });
            Equal(file.CaptureDate, "old date"); Equal(file.CaptureDateSource, "old source"); Equal(file.TargetRelativePath, "old target");
        }

        private static void CultureIndependent()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                foreach (string culture in new[] { "tr-TR", "ar-SA", "th-TH" })
                {
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    ExpectPath("capture", "month", true, "DCIM/IMG.TIFF", null, Capture(), "2024/02/2024-02-29/TIFF/IMG.TIFF");
                    ExpectPath("import", "month", true, "DCIM/IMG.TIFF", "2024-02-29", null, "2024/02/2024-02-29/TIFF/IMG.TIFF");
                    Equal(OrganizationSettings.Normalize(new OrganizationOptions { Mode = "IMPORT", VideoTimeMode = "UTC" }).Mode, "import");
                }
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }

        private static void PolicyJson()
        {
            var json = new JavaScriptSerializer();
            OrganizationOptions policy = Options("import", "month", true); policy.MissingDate = "modified"; policy.VideoTimeMode = "utc"; policy.TimeZoneId = "China Standard Time";
            OrganizationOptions copy = json.Deserialize<OrganizationOptions>(json.Serialize(policy));
            Equal(OrganizationSettings.Key(copy), OrganizationSettings.Key(policy));
            copy.Mode = "flat"; copy.SeparateMedia = false; Equal(policy.Mode, "import"); Assert(policy.SeparateMedia, "JSON clone changed original.");
            Equal(OrganizationSettings.Key(json.Deserialize<OrganizationOptions>("{}")), OrganizationSettings.Key(null));
        }

        private static void ConfigJson()
        {
            var json = new JavaScriptSerializer();
            var state = new AppState { Theme = "Synthetic", Organization = Options("preserve", "year", true), MaxParallel = 3 };
            state.Organization.MissingDate = "modified"; state.Organization.VideoTimeMode = "utc"; state.Organization.TimeZoneId = "China Standard Time";
            state.Cards.Add(new CardRecord { Id = "synthetic-card", Name = "fixture", AutoImport = true, DeleteMode = "Never" });
            AppState copy = json.Deserialize<AppState>(json.Serialize(state));
            Equal(OrganizationSettings.Key(copy.Organization), OrganizationSettings.Key(state.Organization));
            Equal(copy.Theme, "Synthetic"); Assert(copy.MaxParallel == 3 && copy.Cards.Count == 1 && copy.Cards[0].AutoImport, "Config roundtrip lost unrelated settings.");
            OrganizationOptions frozen = OrganizationSettings.Normalize(copy.Organization); copy.Organization.Mode = "flat"; copy.Organization.TimeZoneId = "UTC";
            Equal(frozen.Mode, "preserve"); Equal(frozen.TimeZoneId, "China Standard Time"); Equal(state.Organization.Mode, "preserve");
            AppState legacy = json.Deserialize<AppState>("{\"Theme\":\"Synthetic\"}");
            Equal(OrganizationSettings.Key(legacy.Organization), OrganizationSettings.Key(null));
        }

        private static void InvalidJsonPolicy()
        {
            var json = new JavaScriptSerializer();
            foreach (string value in new[] { "{\"Mode\":\"other\"}", "{\"DateLayout\":\"week\"}", "{\"MissingDate\":\"today\"}", "{\"VideoTimeMode\":\"local\"}", "{\"TimeZoneId\":\"Synthetic missing zone\"}" })
            {
                OrganizationOptions policy = json.Deserialize<OrganizationOptions>(value);
                Refused(delegate { OrganizationSettings.Normalize(policy); });
            }
        }

        private static OrganizationOptions Options(string mode, string layout, bool separate)
        { return new OrganizationOptions { Mode = mode, DateLayout = layout, SeparateMedia = separate, TimeZoneId = "UTC" }; }
        private static MediaItem File(string relative)
        { return new MediaItem { RelativePath = relative, SourcePath = "synthetic://ignored", WriteTicks = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc).Ticks }; }
        private static CaptureInfo Capture()
        { return new CaptureInfo { Taken = new DateTime(2024, 2, 29, 10, 11, 12, DateTimeKind.Unspecified), Source = "EXIF" }; }
        private static void ExpectPath(string mode, string layout, bool separate, string original, string importDate, CaptureInfo capture, string expected)
        {
            MediaItem file = File(original); OrganizationSettings.Apply(file, Options(mode, layout, separate), importDate, capture);
            Equal(file.TargetRelativePath, expected); Equal(OrganizationSettings.Target(file), expected); Equal(file.RelativePath, original);
        }
        private static void RejectTarget(string target)
        { MediaItem file = File("DCIM/IMG.JPG"); file.TargetRelativePath = target; Refused(delegate { OrganizationSettings.Target(file); }); }
        private static void Test(List<string> passed, string name, Action body) { body(); passed.Add(name); }
        private static void Equal(string actual, string expected)
        { Assert(String.Equals(actual, expected, StringComparison.Ordinal), "Expected '" + expected + "', got '" + actual + "'."); }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Refused(Action body)
        {
            try { body(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Unsafe organization input was accepted.");
        }
    }
}
