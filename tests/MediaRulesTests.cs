using System;
using System.Collections.Generic;
using System.Globalization;

namespace PhotoImportV2
{
    /// <summary>Only synthetic strings are used; no files, devices, UI or application state.</summary>
    public static class MediaRulesTests
    {
        public static string Run()
        {
            var passed = new List<string>();
            Test(passed, "default .dat lists are independent", DefaultLists);
            Test(passed, "empty input permits no exclusions", EmptyInput);
            Test(passed, "ASCII separators uppercase duplicates and ordering", Parsing);
            Test(passed, "strict normalization copies collections and enumerables", Normalization);
            Test(passed, "32 distinct entries accepted and 33 rejected", EntryBoundary);
            Test(passed, "16 canonical characters accepted and 17 rejected", LengthBoundary);
            Test(passed, "wildcards paths punctuation and non-ASCII rejected", InvalidTokens);
            Test(passed, "invalid ASCII characters rejected", InvalidAscii);
            Test(passed, "null and invalid collection elements rejected", InvalidCollections);
            Test(passed, "all nine supported extensions are case insensitive", SupportedExtensions);
            Test(passed, "unsupported and extensionless paths rejected", UnsupportedExtensions);
            Test(passed, "exclusions match the exact final extension", ExactExclusions);
            Test(passed, "Includes equals supported and not excluded", InclusionRule);
            Test(passed, "new.dat never becomes media", DatNeverMedia);
            Test(passed, "normalization and matching are culture independent", CultureIndependent);
            return "PASS: " + passed.Count.ToString(CultureInfo.InvariantCulture) +
                " media rule fixture tests: " + String.Join("; ", passed.ToArray()) + ".";
        }

        private static void DefaultLists()
        {
            List<string> first = MediaRules.DefaultExcluded();
            List<string> second = MediaRules.DefaultExcluded();
            Equal(first, ".dat");
            Equal(second, ".dat");
            Assert(!Object.ReferenceEquals(first, second), "Default lists share a reference.");
            first.Add(".jpg");
            first[0] = ".nef";
            first.Clear();
            Equal(second, ".dat");
            second.Clear();
            Equal(MediaRules.DefaultExcluded(), ".dat");
        }

        private static void EmptyInput()
        {
            Equal(MediaRules.ParseExcluded(""));
            Equal(MediaRules.ParseExcluded(" ,; \t\r\n\v\f;;,, "));
            Equal(MediaRules.NormalizeExcluded(new string[0]));
            List<string> first = MediaRules.ParseExcluded("");
            List<string> second = MediaRules.ParseExcluded("");
            first.Add(".jpg");
            Equal(second);
            List<string> source = new List<string>();
            List<string> result = MediaRules.NormalizeExcluded(source);
            result.Add(".dat");
            Equal(source);
        }

        private static void Parsing()
        {
            Equal(MediaRules.ParseExcluded(" DAT, .JpG;JPEG\tnef\r\n.NRW MOV\f.mp4\vAVI tif .TIFF ;jpg, DAT "),
                ".dat", ".jpg", ".jpeg", ".nef", ".nrw", ".mov", ".mp4", ".avi", ".tif", ".tiff");
            Equal(MediaRules.ParseExcluded("A1B2; .a1b2, 7 .7"), ".a1b2", ".7");
            foreach (char separator in new[] { ',', ';', ' ', '\t', '\r', '\n', '\v', '\f' })
                Equal(MediaRules.ParseExcluded("JPG" + separator + ".NEF"), ".jpg", ".nef");
        }

        private static void Normalization()
        {
            var source = new List<string> { " .DAT ", "JPG", ".jpg", "\tNEF\r\n" };
            List<string> result = MediaRules.NormalizeExcluded(source);
            Equal(result, ".dat", ".jpg", ".nef");
            Equal(source, " .DAT ", "JPG", ".jpg", "\tNEF\r\n");
            Assert(!Object.ReferenceEquals(source, result), "Normalized list shares its input reference.");
            source[0] = ".mp4";
            source.Clear();
            Equal(result, ".dat", ".jpg", ".nef");
            result.Clear();
            Equal(source);
            var canonical = new List<string> { ".dat", ".jpg" };
            List<string> first = MediaRules.NormalizeExcluded(canonical);
            List<string> second = MediaRules.NormalizeExcluded(canonical);
            first[0] = ".nef";
            Equal(canonical, ".dat", ".jpg");
            Equal(second, ".dat", ".jpg");
            var singlePass = new SinglePassValues();
            Equal(MediaRules.NormalizeExcluded(singlePass), ".dat", ".jpg");
            Assert(singlePass.EnumerationCount == 1, "Normalization enumerated its source more than once.");
        }

        private static void EntryBoundary()
        {
            string[] entries = Entries(32);
            string[] canonical = new string[32];
            for (int i = 0; i < canonical.Length; i++) canonical[i] = "." + entries[i];
            Equal(MediaRules.ParseExcluded(String.Join(",", entries)), canonical);
            Equal(MediaRules.NormalizeExcluded(entries), canonical);
            Reject(delegate { MediaRules.ParseExcluded(String.Join(";", Entries(33))); });
            Reject(delegate { MediaRules.NormalizeExcluded(Entries(33)); });
            var duplicates = new List<string>(entries);
            foreach (string entry in entries) duplicates.Add("." + entry.ToUpperInvariant());
            Equal(MediaRules.ParseExcluded(String.Join(" ", duplicates.ToArray())), canonical);
            Equal(MediaRules.NormalizeExcluded(duplicates), canonical);
            duplicates.Add(".bad!");
            Reject(delegate { MediaRules.ParseExcluded(String.Join(",", duplicates.ToArray())); });
            Reject(delegate { MediaRules.NormalizeExcluded(duplicates); });
        }

        private static void LengthBoundary()
        {
            string maximumBare = new string('A', 15);
            string maximumCanonical = "." + maximumBare.ToLowerInvariant();
            Equal(MediaRules.ParseExcluded(maximumBare + ";." + maximumBare), maximumCanonical);
            Equal(MediaRules.NormalizeExcluded(new[] { maximumBare, "." + maximumBare }), maximumCanonical);
            Equal(MediaRules.ParseExcluded("A .1"), ".a", ".1");
            foreach (string value in new[] { new string('a', 16), "." + new string('a', 16) })
            {
                Reject(delegate { MediaRules.ParseExcluded(value); });
                Reject(delegate { MediaRules.NormalizeExcluded(new[] { value }); });
            }
        }

        private static void InvalidTokens()
        {
            foreach (string value in new[]
            {
                ".", "..", "..jpg", "jpg.", "a.b", "image.jpg", "*", "*.jpg", ".*", "?", "jp?g",
                "[jpg]", "{jpg}", "jpg|nef", "jp-g", "jp_g", "jp+g", "jpg:stream", "\"jpg\"", "'jpg'",
                @"C:\fixture\image.jpg", @"fixture\jpg", "fixture/jpg", "../jpg", @"..\jpg", "https://fixture.invalid/jpg",
                "%2ejpg", ".d\u00e1t", ".\u6570\u636e", "\uff0ejpg", ".\uff4a\uff50\uff47", "\u00a0jpg", "jpg\u00a0",
                "jpg\u2003nef", "jpg\u0085nef", ".jp\u200bg", ".jp\ufeffg", ".jp\u0130g", ".jp\ud800g", ".jp\udc00g"
            })
            {
                Reject(delegate { MediaRules.ParseExcluded(value); });
                Reject(delegate { MediaRules.NormalizeExcluded(new[] { value }); });
                Reject(delegate { MediaRules.ParseExcluded("jpg;" + value); });
            }
        }

        private static void InvalidAscii()
        {
            const string accepted = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789,; \t\r\n\v\f";
            for (int code = 0; code < 128; code++)
            {
                char character = (char)code;
                if (accepted.IndexOf(character) >= 0) continue;
                string value = ".jp" + character + "g";
                Reject(delegate { MediaRules.ParseExcluded(value); });
                Reject(delegate { MediaRules.NormalizeExcluded(new[] { value }); });
            }
        }

        private static void InvalidCollections()
        {
            Reject(delegate { MediaRules.ParseExcluded(null); });
            Reject(delegate { MediaRules.NormalizeExcluded(null); });
            foreach (string value in new[] { null, "", " \t\r\n\v\f ", "jpg nef", "jpg,nef", "jpg;nef", ".jpg\t.nef" })
                Reject(delegate { MediaRules.NormalizeExcluded(new[] { ".dat", value }); });
        }

        private static void SupportedExtensions()
        {
            foreach (string extension in Supported())
            {
                Assert(MediaRules.IsSupported("fixture" + extension), "Supported extension was rejected: " + extension);
                Assert(MediaRules.IsSupported(@"synthetic\folder.dat\fixture" + extension.ToUpperInvariant()),
                    "Uppercase extension or dotted directory was rejected: " + extension);
                Assert(MediaRules.Includes("fixture" + extension, new string[0]), "Empty exclusions rejected supported media.");
                Assert(MediaRules.Includes("fixture" + extension, MediaRules.DefaultExcluded()), "Default .dat excluded supported media.");
            }
            Assert(MediaRules.IsSupported("new.dat.jpg"), "Only the final extension should decide support.");
            Assert(MediaRules.IsSupported("synthetic/folder/fixture.NEF"), "Forward slash fixture was rejected.");
        }

        private static void UnsupportedExtensions()
        {
            foreach (string path in new[]
            {
                null, "", "fixture", "fixture.", "fixture.dat", "fixture.DAT", "fixture.png", "fixture.gif", "fixture.heic",
                "fixture.arw", "fixture.dng", "fixture.txt", "fixture.jpg.tmp", "fixture.jpeg2", @"folder.jpg\fixture",
                @"folder.mp4\", "folder.nef/fixture.dat"
            })
            {
                Assert(!MediaRules.IsSupported(path), "Unsupported fixture was accepted: " + path);
                Assert(!MediaRules.Includes(path, new string[0]), "Empty exclusions expanded support: " + path);
            }
        }

        private static void ExactExclusions()
        {
            string[] excluded = { ".JpG", ".DAT" };
            Assert(MediaRules.IsExcluded(@"synthetic\folder\fixture.JPG", excluded), "Case insensitive exact match failed.");
            Assert(MediaRules.IsExcluded("new.dat", excluded), "Unsupported .dat can still match an exclusion.");
            foreach (string path in new[] { "fixture.jpeg", "fixture.jpg.tmp", "fixture.notjpg", @"folder.jpg\fixture.nef", "fixture", "fixture.", "", null })
                Assert(!MediaRules.IsExcluded(path, excluded), "Exclusion matched a non-final or partial extension: " + path);
            Assert(!MediaRules.IsExcluded("fixture.jpg", new[] { ".jp", ".jpgx", ".jpg.tmp" }), "Partial exclusion matched .jpg.");
            Assert(!MediaRules.IsExcluded("fixture.jpg", new string[0]), "Empty exclusions matched media.");
            Assert(!MediaRules.IsExcluded("fixture.jpg", null), "Null exclusions matched media.");
            var source = new List<string> { ".JPG", ".DAT" };
            MediaRules.IsExcluded("fixture.jpg", source);
            Equal(source, ".JPG", ".DAT");
        }

        private static void InclusionRule()
        {
            string[][] exclusions = { null, new string[0], new[] { ".dat" }, new[] { ".JPG", ".Mp4" }, Supported() };
            var paths = new List<string> { null, "", "fixture", "new.dat", "fixture.png", "fixture.jpg.tmp" };
            foreach (string extension in Supported()) paths.Add("fixture" + extension.ToUpperInvariant());
            foreach (string[] excluded in exclusions)
                foreach (string path in paths)
                    Assert(MediaRules.Includes(path, excluded) == (MediaRules.IsSupported(path) && !MediaRules.IsExcluded(path, excluded)),
                        "Includes differs from its required rule: " + path);
            Assert(!MediaRules.Includes("fixture.JPG", new[] { ".jpg" }), "A supported excluded extension was included.");
            Assert(MediaRules.Includes("fixture.NEF", new[] { ".jpg" }), "An unrelated exclusion rejected media.");
        }

        private static void DatNeverMedia()
        {
            List<string> clearedDefault = MediaRules.DefaultExcluded();
            clearedDefault.Clear();
            IEnumerable<string>[] exclusions =
            {
                null, clearedDefault, MediaRules.DefaultExcluded(), MediaRules.ParseExcluded(""),
                MediaRules.ParseExcluded("jpg;nef"), MediaRules.NormalizeExcluded(new[] { "DAT" })
            };
            foreach (string path in new[] { "new.dat", "new.DAT", @"synthetic\new.dat", "folder.jpg/new.DaT" })
            {
                Assert(!MediaRules.IsSupported(path), "The .dat fixture became supported media.");
                foreach (IEnumerable<string> excluded in exclusions)
                    Assert(!MediaRules.Includes(path, excluded), "Changing exclusions made new.dat media.");
            }
        }

        private static void CultureIndependent()
        {
            CultureInfo previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                Equal(MediaRules.ParseExcluded("TIF;.tif;AVI;.avi"), ".tif", ".avi");
                Equal(MediaRules.NormalizeExcluded(new[] { "TIFF", ".tiff" }), ".tiff");
                Assert(MediaRules.IsSupported("fixture.TIF"), "Support depends on the current culture.");
                Assert(MediaRules.IsExcluded("fixture.AVI", new[] { ".avi" }), "Exclusion depends on the current culture.");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
        }

        private static string[] Supported()
        {
            return new[] { ".jpg", ".jpeg", ".nef", ".nrw", ".mov", ".mp4", ".avi", ".tif", ".tiff" };
        }

        private static string[] Entries(int count)
        {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = "e" + i.ToString(CultureInfo.InvariantCulture);
            return result;
        }

        private sealed class SinglePassValues : IEnumerable<string>
        {
            public int EnumerationCount { get; private set; }
            public IEnumerator<string> GetEnumerator()
            {
                EnumerationCount++;
                if (EnumerationCount != 1) throw new InvalidOperationException("Fixture enumerable was consumed twice.");
                yield return "DAT";
                yield return ".jpg";
                yield return ".dat";
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
        }

        private static void Equal(List<string> actual, params string[] expected)
        {
            Assert(actual.Count == expected.Length, "Unexpected number of extensions.");
            for (int i = 0; i < expected.Length; i++)
                Assert(actual[i] == expected[i], "Unexpected extension or order at index " + i.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private static void Reject(Action action)
        {
            bool rejected = false;
            try { action(); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Invalid fixture did not throw ArgumentException.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Test(List<string> passed, string name, Action action)
        {
            try { action(); passed.Add(name); }
            catch (Exception error) { throw new InvalidOperationException("Media rule test failed: " + name, error); }
        }
    }
}
