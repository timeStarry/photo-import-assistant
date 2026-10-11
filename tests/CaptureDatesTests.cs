using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PhotoImportV2
{
    /// <summary>Synthetic byte arrays and virtual streams only; no filesystem dates or media decoding.</summary>
    public static class CaptureDatesTests
    {
        private const string Original = "2024:02:29 23:58:47";
        private static readonly DateTime Expected = new DateTime(2024, 2, 29, 23, 58, 47);
        private static readonly DateTime Epoch = new DateTime(1904, 1, 1);

        public static string Run()
        {
            var passed = new List<string>();
            Test(passed, "empty null and unsupported input", EmptyUnknown);
            Test(passed, "extension and signature must agree", Signatures);
            Test(passed, "little endian TIFF Exif pointer", TiffLittle);
            Test(passed, "big endian TIFF Exif pointer", TiffBig);
            Test(passed, "TIFF NEF NRW aliases and case", TiffAliases);
            Test(passed, "direct DateTimeOriginal IFD", TiffDirect);
            Test(passed, "modified and digitized dates are not capture", TiffModified);
            Test(passed, "original wins over other TIFF fields", TiffOriginalPreferred);
            Test(passed, "invalid EXIF calendar values", TiffDates);
            Test(passed, "invalid TIFF byte order magic and BigTIFF", TiffHeaders);
            Test(passed, "IFD offset bounds", TiffIfdOffsets);
            Test(passed, "truncated IFD table and next pointer", TiffTruncation);
            Test(passed, "EXIF value offset bounds", TiffValueOffsets);
            Test(passed, "EXIF ASCII type count and terminator", TiffTypes);
            Test(passed, "Exif pointer type count and bounds", TiffExifPointers);
            Test(passed, "self and cross IFD cycles", TiffCycles);
            Test(passed, "IFD traversal ceiling", TiffDirectoryBudget);
            Test(passed, "IFD entry ceiling", TiffEntryBudget);
            Test(passed, "next IFD capture field", TiffNextIfd);
            Test(passed, "little endian JPEG APP1", JpegLittle);
            Test(passed, "big endian JPEG APP1", JpegBig);
            Test(passed, "JPEG skips unrelated APP segments", JpegSegments);
            Test(passed, "JPEG corrupt segment lengths", JpegLengths);
            Test(passed, "EXIF offsets cannot escape APP1", JpegExifBoundary);
            Test(passed, "JPEG stops at scan and end markers", JpegStops);
            Test(passed, "JPEG fill and marker read ceilings", JpegBudget);
            Test(passed, "MOV MP4 version zero mvhd", MovieV0);
            Test(passed, "version one mvhd beyond UInt32", MovieV1);
            Test(passed, "integer wall clock default ignores zone", VideoDefault);
            Test(passed, "integer UTC conversion crosses next midnight", VideoUtcForward);
            Test(passed, "integer UTC conversion crosses previous midnight", VideoUtcBackward);
            Test(passed, "invalid integer UTC zone returns unknown", VideoZoneErrors);
            Test(passed, "unset overflow unsupported version and flags", MovieInvalidTimes);
            Test(passed, "modification track and media dates ignored", MovieModified);
            Test(passed, "atom sizes cannot escape parents or wrap", MovieSizes);
            Test(passed, "truncated atom headers and mvhd", MovieTruncation);
            Test(passed, "extended and top level to EOF atom sizes", MovieExtended);
            Test(passed, "huge virtual mdat skipped by Seek", MovieHugeMdat);
            Test(passed, "atom traversal budget", MovieBudget);
            Test(passed, "explicit keyed creation date precedes integer fallback", MetadataPreferred);
            Test(passed, "plain full meta and arbitrary child ordering", MetadataLayouts);
            Test(passed, "explicit offsets fractions and wall clocks", MetadataOffsets);
            Test(passed, "UTC flag preserves explicit calendar values", MetadataUtcFlag);
            Test(passed, "invalid explicit strings use valid integer fallback", MetadataInvalidDates);
            Test(passed, "handler namespace key and data type checked", MetadataTypes);
            Test(passed, "corrupt key and item sizes", MetadataLengths);
            Test(passed, "string and metadata key ceilings", MetadataBudget);
            Test(passed, "caller retains stream and controls position", StreamOwnership);
            Test(passed, "sliced MemoryStream uses exposed coordinates", SlicedMemoryStream);
            Test(passed, "partial reads and premature EOF", StreamPartialReads);
            Test(passed, "real read seek and length IOExceptions propagate", StreamIoFailures);
            Test(passed, "culture independent repeated calls", CultureAndRepeat);
            Test(passed, "all truncation prefixes are bounded unknown", TruncationPrefixes);
            Test(passed, "conflicting original fields are unknown", TiffConflictingDates);
            return "PASS: " + passed.Count.ToString(CultureInfo.InvariantCulture) +
                " capture date fixture tests: " + String.Join("; ", passed.ToArray()) + ".";
        }

        private static void EmptyUnknown()
        {
            Unknown(CaptureDates.Read(null, ".jpg", false, null));
            foreach (string extension in new[] { ".jpg", ".jpeg", ".tif", ".tiff", ".nef", ".nrw", ".mov", ".mp4" })
                Unknown(Read(new byte[0], extension));
            foreach (string extension in new[] { null, "", ".avi", ".png", ".dat", ".jpg.tmp", "jpg" })
            {
                var stream = new FixtureStream(Tiff(true));
                Unknown(CaptureDates.Read(stream, extension, false, null));
                Assert(stream.ReadCalls == 0 && stream.SeekCalls == 0, "Unsupported extension consumed data.");
            }
            var nonSeek = new FixtureStream(Tiff(true)) { Seekable = false };
            Unknown(CaptureDates.Read(nonSeek, ".nef", false, null));
            Assert(nonSeek.ReadCalls == 0, "Non-seekable stream was consumed.");
            var unreadable = new FixtureStream(Tiff(true)) { Readable = false };
            Unknown(CaptureDates.Read(unreadable, ".tif", false, null));
        }

        private static void Signatures()
        {
            Unknown(Read(Tiff(true), ".jpg"));
            Unknown(Read(Jpeg(Tiff(false)), ".tiff"));
            Unknown(Read(Movie(Expected, 0), ".nef"));
            Unknown(Read(new byte[20], ".nrw"));
            Unknown(Read(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }, ".jpg"));
        }

        private static void TiffLittle() { Taken(Read(Tiff(true), ".tif"), Expected, "EXIF"); }
        private static void TiffBig() { Taken(Read(Tiff(false), ".tiff"), Expected, "EXIF"); }

        private static void TiffAliases()
        {
            foreach (bool little in new[] { true, false })
                foreach (string extension in new[] { ".TIF", ".TIFF", ".NEF", ".NRW" })
                    Taken(Read(Tiff(little), extension, true, "invalid fixture zone"), Expected, "EXIF");
        }

        private static void TiffDirect()
        {
            foreach (bool little in new[] { true, false }) Taken(Read(Tiff(little, Original, false), ".nef"), Expected, "EXIF");
        }

        private static void TiffModified()
        {
            foreach (bool little in new[] { true, false })
                foreach (ushort tag in new ushort[] { 0x0132, 0x9004 })
                    Unknown(Read(Tiff(little, Original, true, tag), ".tiff"));
        }

        private static void TiffOriginalPreferred()
        {
            foreach (bool little in new[] { true, false })
            {
                byte[] bytes = TiffFields(little, true, new ushort[] { 0x0132, 0x9004, 0x9003 },
                    new[] { "2023:01:01 01:02:03", "2022:01:01 01:02:03", Original });
                Taken(Read(bytes, ".nrw"), Expected, "EXIF");
            }
        }

        private static void TiffDates()
        {
            foreach (string text in new[] { "0000:00:00 00:00:00", "2023:02:29 12:00:00", "2024:13:01 12:00:00",
                "2024:04:31 12:00:00", "2024:01:01 24:00:00", "2024:01:01 12:60:00", "2024:01:01 12:00:60",
                "2024-02-29 23:58:47", "2024:02:29T23:58:47", "2024:02:29 23:58: 7" })
                foreach (bool little in new[] { true, false }) Unknown(Read(Tiff(little, text), ".nef"));
        }

        private static void TiffHeaders()
        {
            foreach (int index in new[] { 0, 1, 2, 3 })
            {
                byte[] bytes = Tiff(true); bytes[index] ^= 0xff;
                Unknown(Read(bytes, ".tif"));
            }
            byte[] bigTiff = Tiff(true); Put16(bigTiff, 2, 43, true);
            Unknown(Read(bigTiff, ".tif"));
        }

        private static void TiffIfdOffsets()
        {
            foreach (bool little in new[] { true, false })
                foreach (uint offset in new uint[] { 0, 1, 7, 63, 64, UInt32.MaxValue })
                {
                    byte[] bytes = Tiff(little); Put32(bytes, 4, offset, little);
                    Unknown(Read(bytes, ".nrw"));
                }
        }

        private static void TiffTruncation()
        {
            foreach (int length in new[] { 8, 9, 12, 21, 22, 25, 26, 27, 39, 40, 43, 63 })
                Unknown(Read(Prefix(Tiff(true), length), ".tiff"));
            byte[] bytes = Tiff(false); Put16(bytes, 8, 5, false);
            Unknown(Read(bytes, ".tiff"));
        }

        private static void TiffValueOffsets()
        {
            foreach (bool little in new[] { true, false })
                foreach (uint offset in new uint[] { 0, 7, 45, 64, UInt32.MaxValue })
                {
                    byte[] bytes = Tiff(little); Put32(bytes, 36, offset, little);
                    Unknown(Read(bytes, ".nef"));
                }
        }

        private static void TiffTypes()
        {
            foreach (bool little in new[] { true, false })
            {
                foreach (ushort type in new ushort[] { 0, 1, 3, 4, 7 })
                { byte[] bytes = Tiff(little); Put16(bytes, 30, type, little); Unknown(Read(bytes, ".tif")); }
                foreach (uint count in new uint[] { 0, 4, 19, 21, UInt32.MaxValue })
                { byte[] bytes = Tiff(little); Put32(bytes, 32, count, little); Unknown(Read(bytes, ".tif")); }
                byte[] badNull = Tiff(little); badNull[63] = (byte)'1'; Unknown(Read(badNull, ".tif"));
                byte[] embeddedNull = Tiff(little); embeddedNull[49] = 0; Unknown(Read(embeddedNull, ".tif"));
                byte[] nonAscii = Tiff(little); nonAscii[44] = 0xff; Unknown(Read(nonAscii, ".tif"));
            }
        }

        private static void TiffExifPointers()
        {
            foreach (bool little in new[] { true, false })
            {
                foreach (uint offset in new uint[] { 0, 7, 63, UInt32.MaxValue })
                { byte[] bytes = Tiff(little); Put32(bytes, 18, offset, little); Unknown(Read(bytes, ".nrw")); }
                byte[] type = Tiff(little); Put16(type, 12, 3, little); Unknown(Read(type, ".nrw"));
                byte[] count = Tiff(little); Put32(count, 14, 2, little); Unknown(Read(count, ".nrw"));
            }
        }

        private static void TiffCycles()
        {
            foreach (bool little in new[] { true, false })
            {
                byte[] next = Tiff(little); Put32(next, 22, 8, little); Unknown(Read(next, ".nef"));
                byte[] self = Tiff(little); Put32(self, 18, 8, little); Unknown(Read(self, ".nef"));
                byte[] cross = Tiff(little);
                Put16(cross, 28, 0x8769, little); Put16(cross, 30, 4, little);
                Put32(cross, 32, 1, little); Put32(cross, 36, 8, little);
                Unknown(Read(cross, ".nef"));
                byte[] exifNext = Tiff(little); Put32(exifNext, 40, 26, little); Unknown(Read(exifNext, ".nef"));
            }
        }

        private static void TiffDirectoryBudget()
        {
            byte[] bytes = new byte[8 + 33 * 6];
            TiffHeader(bytes, true, 8);
            for (int i = 0; i < 33; i++) Put32(bytes, 10 + i * 6, i == 32 ? 0U : (uint)(14 + i * 6), true);
            var stream = new FixtureStream(bytes);
            Unknown(CaptureDates.Read(stream, ".tif", false, null));
            Assert(stream.ReadCalls <= 65, "IFD traversal exceeded its directory limit.");
        }

        private static void TiffEntryBudget()
        {
            byte[] bytes = new byte[8 + 2 + 513 * 12 + 4];
            TiffHeader(bytes, false, 8); Put16(bytes, 8, 513, false);
            var stream = new FixtureStream(bytes);
            Unknown(CaptureDates.Read(stream, ".tif", false, null));
            Assert(stream.ReadCalls == 2, "Oversized directory was traversed.");
        }

        private static void TiffNextIfd()
        {
            foreach (bool little in new[] { true, false })
            {
                byte[] bytes = new byte[52]; TiffHeader(bytes, little, 8); Put32(bytes, 10, 14, little);
                Put16(bytes, 14, 1, little); Entry(bytes, 16, 0x9003, 2, 20, 32, little);
                CopyText(bytes, 32, Original);
                Taken(Read(bytes, ".tif"), Expected, "EXIF");
            }
        }

        private static void JpegLittle() { Taken(Read(Jpeg(Tiff(true)), ".jpg"), Expected, "EXIF"); }
        private static void JpegBig() { Taken(Read(Jpeg(Tiff(false)), ".JPEG"), Expected, "EXIF"); }

        private static void JpegSegments()
        {
            byte[] bytes = Join(new byte[] { 0xff, 0xd8, 0xff, 0x01 }, Segment(0xe0, new byte[40]),
                Segment(0xe1, Encoding.ASCII.GetBytes("http://fixture.invalid/xmp/")), Segment(0xe2, new byte[120]),
                new byte[] { 0xff, 0xff, 0xd0 }, Segment(0xe1, Join(Encoding.ASCII.GetBytes("Exif\0\0"), Tiff(true))),
                new byte[] { 0xff, 0xd9 });
            Taken(Read(bytes, ".jpg"), Expected, "EXIF");
        }

        private static void JpegLengths()
        {
            foreach (ushort length in new ushort[] { 0, 1, 2, 7, 65535 })
            {
                byte[] bytes = Jpeg(Tiff(true)); Put16(bytes, 4, length, false);
                Unknown(Read(bytes, ".jpg"));
            }
            Unknown(Read(new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0 }, ".jpg"));
            Unknown(Read(new byte[] { 0xff, 0xd8, 0xff, 0 }, ".jpg"));
        }

        private static void JpegExifBoundary()
        {
            byte[] bytes = Jpeg(Tiff(true));
            Put16(bytes, 4, 8 + 44, false); // The date physically follows APP1 but is outside its declared container.
            Unknown(Read(bytes, ".jpg"));
            byte[] offset = Tiff(false); Put32(offset, 36, 65, false);
            Unknown(Read(Join(Jpeg(offset), Encoding.ASCII.GetBytes(Original + "\0")), ".jpg"));
        }

        private static void JpegStops()
        {
            foreach (byte marker in new byte[] { 0xda, 0xd9 })
            {
                var stream = new FixtureStream(Join(new byte[] { 0xff, 0xd8, 0xff, marker }, Jpeg(Tiff(true))));
                Unknown(CaptureDates.Read(stream, ".jpg", false, null));
                Assert(stream.BytesRead == 4, "JPEG parser entered scan data or read beyond EOI.");
            }
        }

        private static void JpegBudget()
        {
            byte[] fill = new byte[7000]; fill[0] = 0xff; fill[1] = 0xd8;
            for (int i = 2; i < fill.Length; i++) fill[i] = 0xff;
            var stream = new FixtureStream(fill);
            Unknown(CaptureDates.Read(stream, ".jpg", false, null)); Bounded(stream);
            var segments = new List<byte[]> { new byte[] { 0xff, 0xd8 } };
            for (int i = 0; i < 3000; i++) segments.Add(Segment(0xe0, new byte[0]));
            segments.Add(Segment(0xe1, Join(Encoding.ASCII.GetBytes("Exif\0\0"), Tiff(true))));
            var many = new FixtureStream(Join(segments.ToArray()));
            Unknown(CaptureDates.Read(many, ".jpg", false, null)); Bounded(many);
        }

        private static void MovieV0()
        {
            foreach (string extension in new[] { ".mov", ".mp4", ".MOV", ".MP4" })
                Taken(Read(Movie(Expected, 0), extension), Expected, "mvhd");
        }

        private static void MovieV1()
        {
            DateTime date = new DateTime(2060, 6, 7, 8, 9, 10);
            Assert(Seconds(date) > UInt32.MaxValue, "Version one fixture does not exercise 64-bit seconds.");
            Taken(Read(Movie(date, 1), ".mp4"), date, "mvhd");
        }

        private static void VideoDefault()
        {
            foreach (string zone in new[] { null, "", "UTC", "China Standard Time", "invalid fixture zone" })
                Taken(Read(Movie(Expected, 0), ".mov", false, zone), Expected, "mvhd");
        }

        private static void VideoUtcForward()
        {
            DateTime utc = new DateTime(2024, 12, 31, 20, 30, 0);
            foreach (byte version in new byte[] { 0, 1 })
                Taken(Read(Movie(utc, version), ".mp4", true, "China Standard Time"), new DateTime(2025, 1, 1, 4, 30, 0), "mvhd");
            Taken(Read(Movie(utc, 0), ".mp4", true, "UTC"), utc, "mvhd");
        }

        private static void VideoUtcBackward()
        {
            DateTime utc = new DateTime(2024, 1, 1, 2, 30, 0);
            Taken(Read(Movie(utc, 1), ".mov", true, "Pacific Standard Time"), new DateTime(2023, 12, 31, 18, 30, 0), "mvhd");
            DateTime summer = new DateTime(2024, 7, 1, 2, 30, 0);
            Taken(Read(Movie(summer, 0), ".mov", true, "Pacific Standard Time"), new DateTime(2024, 6, 30, 19, 30, 0), "mvhd");
        }

        private static void VideoZoneErrors()
        {
            foreach (string zone in new[] { null, "", "invalid fixture zone" })
                Unknown(Read(Movie(Expected, 0), ".mp4", true, zone));
        }

        private static void MovieInvalidTimes()
        {
            Unknown(Read(Atom("moov", Header(0, 0, Seconds(Expected))), ".mov"));
            Unknown(Read(Atom("moov", Header(1, UInt64.MaxValue, 1)), ".mov"));
            Unknown(Read(Atom("moov", Header(2, Seconds(Expected), 1)), ".mov"));
            byte[] flags = Header(0, Seconds(Expected), 1); flags[9] = 1;
            Unknown(Read(Atom("moov", flags), ".mp4"));
            ulong maximum = (ulong)((DateTime.MaxValue.Ticks - Epoch.Ticks) / TimeSpan.TicksPerSecond);
            Unknown(Read(Atom("moov", Header(1, maximum + 1, 1)), ".mov"));
            DateTime lastSecond = Epoch.AddTicks((long)maximum * TimeSpan.TicksPerSecond);
            Taken(Read(Atom("moov", Header(1, maximum, 1)), ".mov"), lastSecond, "mvhd");
            Unknown(Read(Atom("moov", Header(1, maximum, 1)), ".mov", true, "China Standard Time"));
        }

        private static void MovieModified()
        {
            Unknown(Read(Atom("moov", Header(0, 0, Seconds(Expected))), ".mov"));
            Unknown(Read(Header(0, Seconds(Expected), 1), ".mov")); // mvhd outside moov is not a movie capture field.
            Unknown(Read(Atom("moov", Atom("trak", Join(Header(0, Seconds(Expected), 1),
                Atom("mdia", Atom("mdhd", new byte[40]))))), ".mov"));
            Taken(Read(Atom("moov", Join(Header(0, Seconds(Expected), Seconds(new DateTime(2025, 1, 1))),
                Atom("trak", Header(0, Seconds(new DateTime(2023, 1, 1)), 1)))), ".mov"), Expected, "mvhd");
        }

        private static void MovieSizes()
        {
            foreach (uint size in new uint[] { 2, 7, 9, UInt32.MaxValue })
            { byte[] bytes = Movie(Expected, 0); Put32(bytes, 0, size, false); Unknown(Read(bytes, ".mov")); }
            byte[] child = Movie(Expected, 0); Put32(child, 8, UInt32.MaxValue, false); Unknown(Read(child, ".mov"));
            byte[] nestedZero = Movie(Expected, 0); Put32(nestedZero, 8, 0, false); Unknown(Read(nestedZero, ".mov"));
            foreach (ulong size in new ulong[] { 0, 8, 15, UInt64.MaxValue, (ulong)Int64.MaxValue })
            {
                byte[] bytes = new byte[16]; Put32(bytes, 0, 1, false); Type(bytes, 4, "mdat"); Put64(bytes, 8, size);
                Unknown(Read(bytes, ".mp4"));
            }
            byte[] escaping = Movie(Expected, 0); Put32(escaping, 0, 8, false); Unknown(Read(escaping, ".mov"));
        }

        private static void MovieTruncation()
        {
            foreach (int length in new[] { 1, 4, 7, 8, 9, 15, 16, 19, 20, 30, 115 })
                Unknown(Read(Prefix(Movie(Expected, 0), length), ".mov"));
            Unknown(Read(Atom("moov", Atom("mvhd", new byte[] { 0, 0, 0, 0, 0xe0, 1, 2, 3 })), ".mov"));
            Unknown(Read(Atom("moov", Atom("mvhd", Prefix(Header(1, Seconds(Expected), 1), 111))), ".mp4"));
            Unknown(Read(new byte[] { 0, 0, 0, 1, (byte)'m', (byte)'d', (byte)'a', (byte)'t' }, ".mp4"));
        }

        private static void MovieExtended()
        {
            byte[] movie = Movie(Expected, 1);
            byte[] extended = new byte[movie.Length + 8];
            Put32(extended, 0, 1, false); Type(extended, 4, "moov"); Put64(extended, 8, (ulong)extended.Length);
            Buffer.BlockCopy(movie, 8, extended, 16, movie.Length - 8);
            Taken(Read(extended, ".mov"), Expected, "mvhd");
            Put32(movie, 0, 0, false); Taken(Read(movie, ".mp4"), Expected, "mvhd");
            Taken(Read(Join(Movie(Expected, 0), Atom("mdat", new byte[30], true)), ".mp4"), Expected, "mvhd");
            Unknown(Read(Atom("mdat", Movie(Expected, 0), true), ".mp4")); // Payload bytes are never searched for atoms.
        }

        private static void MovieHugeMdat()
        {
            long size = (1L << 34) + 16;
            byte[] header = new byte[16]; Put32(header, 0, 1, false); Type(header, 4, "mdat"); Put64(header, 8, (ulong)size);
            byte[] movie = Atom("moov", Join(Header(1, Seconds(Expected), 1), Metadata("2024-02-29T23:58:47+0800", false, false, "mdta", 1, "com.apple.quicktime.creationdate")));
            var stream = new FixtureStream(size + movie.Length, new Chunk(0, header), new Chunk(size, movie)) { RejectGaps = true };
            Taken(CaptureDates.Read(stream, ".mp4", true, "UTC"), Expected, "creationdate");
            Assert(stream.SeekCalls > 0 && stream.FarthestSeek >= size, "Huge mdat was not skipped by Seek.");
            Assert(stream.BytesRead < 1024, "Huge payload consumed read bytes."); Bounded(stream);
            long normalSize = UInt32.MaxValue;
            byte[] normal = new byte[8]; Put32(normal, 0, UInt32.MaxValue, false); Type(normal, 4, "mdat");
            var normalStream = new FixtureStream(normalSize + Movie(Expected, 0).Length,
                new Chunk(0, normal), new Chunk(normalSize, Movie(Expected, 0))) { RejectGaps = true };
            Taken(CaptureDates.Read(normalStream, ".mov", false, null), Expected, "mvhd"); Bounded(normalStream);
        }

        private static void MovieBudget()
        {
            var atoms = new List<byte[]>();
            for (int i = 0; i < 2050; i++) atoms.Add(Atom("free", new byte[0]));
            atoms.Add(Movie(Expected, 0));
            var stream = new FixtureStream(Join(atoms.ToArray()));
            Unknown(CaptureDates.Read(stream, ".mov", false, null));
            Assert(stream.ReadCalls <= 2048, "Atom traversal did not stop at its structural limit."); Bounded(stream);
            byte[] nested = Header(0, Seconds(Expected), 1);
            for (int i = 0; i < 100; i++) nested = Atom("trak", nested);
            Unknown(Read(Atom("moov", nested), ".mov"));
        }

        private static void MetadataPreferred()
        {
            byte[] explicitDate = Metadata("2025-01-02T03:04:05Z", false, false, "mdta", 1, "com.apple.quicktime.creationdate");
            foreach (bool first in new[] { true, false })
            {
                byte[] header = Header(0, Seconds(Expected), 1);
                Taken(Read(Atom("moov", first ? Join(explicitDate, header) : Join(header, explicitDate)), ".mov"),
                    new DateTime(2025, 1, 2, 3, 4, 5), "creationdate");
            }
            Taken(Read(Atom("moov", explicitDate), ".mov"), new DateTime(2025, 1, 2, 3, 4, 5), "creationdate");
        }

        private static void MetadataLayouts()
        {
            foreach (bool full in new[] { true, false })
                foreach (bool reversed in new[] { true, false })
                    foreach (bool userData in new[] { true, false })
                    {
                        byte[] meta = Metadata("2024-02-29T23:58:47", full, reversed, "mdta", 1, "com.apple.quicktime.creationdate");
                        Taken(Read(Atom("moov", userData ? Atom("udta", meta) : meta), ".mp4"), Expected, "creationdate");
                    }
        }

        private static void MetadataOffsets()
        {
            foreach (string text in new[] { "2024-02-29T23:58:47+08:00", "2024-02-29T23:58:47-0500",
                "2024-02-29T23:58:47Z", "2024-02-29 23:58:47", "2024-02-29T23:58:47+1400", "2024-02-29T23:58:47-14:00" })
                Taken(Read(ExplicitMovie(text), ".mp4"), Expected, "creationdate");
            Taken(Read(ExplicitMovie("2024-02-29T23:58:47.1234567+0800"), ".mov"), Expected.AddTicks(1234567), "creationdate");
            Taken(Read(ExplicitMovie("2024-02-29T23:58:47.5Z"), ".mov"), Expected.AddMilliseconds(500), "creationdate");
        }

        private static void MetadataUtcFlag()
        {
            foreach (string text in new[] { "2024-02-29T23:58:47+0800", "2024-02-29T23:58:47-05:00",
                "2024-02-29T23:58:47Z", "2024-02-29T23:58:47" })
                foreach (string zone in new[] { "UTC", "China Standard Time", "Pacific Standard Time", null, "invalid fixture zone" })
                {
                    Taken(Read(ExplicitMovie(text), ".mov", true, zone), Expected, "creationdate");
                    Taken(Read(ExplicitMovie(text), ".mov", false, zone), Expected, "creationdate");
                }
        }

        private static void MetadataInvalidDates()
        {
            foreach (string text in new[] { "", "4/21/2012", "2024-02-29", "2023-02-29T12:00:00Z", "2024-04-31T12:00:00Z",
                "2024-01-01T24:00:00Z", "2024-01-01T12:60:00Z", "2024-01-01T12:00:60Z", "2024-02-29T23:58:47+1460",
                "2024-02-29T23:58:47+1401", "2024-02-29T23:58:47+080", "2024-02-29T23:58:47.12345678Z",
                "2024-02-29T23:58:47.Z", "2024-02-29T23:58:47Zjunk", " 2024-02-29T23:58:47Z",
                "2024-02-29T23:58:47Z ", "2024-02-29T23:58:47\0", "0001-01-01T00:00:00+0100" })
            {
                Unknown(Read(ExplicitMovie(text), ".mov"));
                Taken(Read(Atom("moov", Join(Header(0, Seconds(Expected), 1),
                    Metadata(text, false, false, "mdta", 1, "com.apple.quicktime.creationdate"))), ".mov"), Expected, "mvhd");
            }
        }

        private static void MetadataTypes()
        {
            foreach (string handler in new[] { "mdir", "xxxx" })
                Unknown(Read(Atom("moov", Metadata("2024-02-29T23:58:47Z", false, false, handler, 1, "com.apple.quicktime.creationdate")), ".mov"));
            foreach (uint type in new uint[] { 0, 2, 13, 0x01000001 })
                Unknown(Read(Atom("moov", Metadata("2024-02-29T23:58:47Z", false, false, "mdta", type, "com.apple.quicktime.creationdate")), ".mov"));
            foreach (string key in new[] { "com.apple.quicktime.year", "com.apple.quicktime.modificationdate", "COM.APPLE.QUICKTIME.CREATIONDATE" })
                Unknown(Read(Atom("moov", Metadata("2024-02-29T23:58:47Z", false, false, "mdta", 1, key)), ".mov"));
            byte[] wrongNamespace = Metadata("2024-02-29T23:58:47Z", false, false, "mdta", 1, "com.apple.quicktime.creationdate");
            int namespaceOffset = Find(wrongNamespace, Encoding.ASCII.GetBytes("com.apple.quicktime.creationdate")) - 4;
            Type(wrongNamespace, namespaceOffset, "udta"); Unknown(Read(Atom("moov", wrongNamespace), ".mov"));
            byte[] wrongIndex = Metadata("2024-02-29T23:58:47Z", false, false, "mdta", 1, "com.apple.quicktime.creationdate");
            int ilst = Find(wrongIndex, Encoding.ASCII.GetBytes("ilst")); Put32(wrongIndex, ilst + 8, 0, false);
            Unknown(Read(Atom("moov", wrongIndex), ".mov"));
        }

        private static void MetadataLengths()
        {
            byte[] valid = ExplicitMovie("2024-02-29T23:58:47Z");
            foreach (uint length in new uint[] { 0, 7, UInt32.MaxValue })
            {
                byte[] bytes = (byte[])valid.Clone(); int key = Find(bytes, Encoding.ASCII.GetBytes("keys"));
                Put32(bytes, key + 12, length, false); Unknown(Read(bytes, ".mov"));
                bytes = (byte[])valid.Clone(); int data = Find(bytes, Encoding.ASCII.GetBytes("data"));
                Put32(bytes, data - 4, length, false); Unknown(Read(bytes, ".mov"));
            }
            byte[] count = (byte[])valid.Clone(); int keys = Find(count, Encoding.ASCII.GetBytes("keys"));
            Put32(count, keys + 8, 2, false); Unknown(Read(count, ".mov"));
            byte[] flags = (byte[])valid.Clone(); flags[keys + 4] = 1; Unknown(Read(flags, ".mov"));
        }

        private static void MetadataBudget()
        {
            Unknown(Read(ExplicitMovie("2024-02-29T23:58:47Z" + new string(' ', 60)), ".mov"));
            byte[] keys = new byte[8 + 513 * 8]; Put32(keys, 4, 513, false);
            for (int i = 0; i < 513; i++) { Put32(keys, 8 + i * 8, 8, false); Type(keys, 12 + i * 8, "mdta"); }
            var stream = new FixtureStream(Atom("moov", Atom("meta", Join(Handler("mdta"), Atom("keys", keys), Atom("ilst", new byte[0])))));
            Unknown(CaptureDates.Read(stream, ".mov", false, null));
            Assert(stream.BytesRead < 128, "Oversized keys table was read."); Bounded(stream);
        }

        private static void SlicedMemoryStream()
        {
            byte[][] values = { Tiff(true), Jpeg(Tiff(false)), Movie(Expected, 1) };
            string[] extensions = { ".nef", ".jpg", ".mov" };
            for (int i = 0; i < values.Length; i++)
            {
                byte[] padded = new byte[values[i].Length + 37]; Buffer.BlockCopy(values[i], 0, padded, 10, values[i].Length);
                using (var stream = new MemoryStream(padded, 10, values[i].Length, false))
                    Taken(CaptureDates.Read(stream, extensions[i], false, null), Expected, i == 2 ? "mvhd" : "EXIF");
            }
        }
        private static void StreamOwnership()
        {
            var stream = new FixtureStream(Join(new byte[11], Tiff(true))); stream.Position = 11;
            Taken(CaptureDates.Read(stream, ".nef", false, null), Expected, "EXIF");
            Assert(!stream.Disposed && stream.WriteCalls == 0 && stream.SetLengthCalls == 0, "Parser changed or closed the source.");
            Assert(stream.Position != 11, "Parser unexpectedly restored caller position.");
            stream.Position = 11;
            Taken(CaptureDates.Read(stream, ".nef", false, null), Expected, "EXIF");
            var movie = new FixtureStream(Movie(Expected, 0));
            Taken(CaptureDates.Read(movie, ".mov", false, null), Expected, "mvhd");
            Assert(!movie.Disposed && movie.WriteCalls == 0 && movie.SetLengthCalls == 0, "Movie parser changed source.");
        }

        private static void StreamPartialReads()
        {
            foreach (byte[] bytes in new[] { Tiff(true), Jpeg(Tiff(false)), Movie(Expected, 1), ExplicitMovie("2024-02-29T23:58:47Z") })
            {
                string extension = bytes[0] == 'I' ? ".tif" : bytes[0] == 0xff ? ".jpg" : ".mov";
                var stream = new FixtureStream(bytes) { MaximumChunk = 1 };
                Taken(CaptureDates.Read(stream, extension, false, null), Expected, extension == ".mov" ? "QuickTime" : "EXIF");
                Bounded(stream);
            }
            var eof = new FixtureStream(Tiff(true)) { PrematureEof = true };
            Unknown(CaptureDates.Read(eof, ".tif", false, null));
        }

        private static void StreamIoFailures()
        {
            IoFailure(new FixtureStream(Tiff(true)) { FailRead = true }, ".tif");
            IoFailure(new FixtureStream(Tiff(true)) { FailLength = true }, ".tif");
            byte[] bytes = Join(Atom("mdat", new byte[40]), Movie(Expected, 0));
            IoFailure(new FixtureStream(bytes) { FailSeek = true }, ".mov");
        }

        private static void CultureAndRepeat()
        {
            CultureInfo previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
                for (int i = 0; i < 3; i++)
                {
                    Taken(Read(Tiff(i % 2 == 0), ".TIFF"), Expected, "EXIF");
                    Taken(Read(ExplicitMovie("2024-02-29T23:58:47Z"), ".mp4", true, "invalid fixture zone"), Expected, "creationdate");
                    Unknown(Read(Tiff(true, "2023:02:29 23:58:47"), ".nef"));
                }
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
        }

        private static void TruncationPrefixes()
        {
            byte[][] fixtures = { Tiff(true), Tiff(false), Movie(Expected, 0), Movie(Expected, 1), ExplicitMovie("2024-02-29T23:58:47Z") };
            foreach (byte[] bytes in fixtures)
                for (int length = 0; length < bytes.Length; length++)
                {
                    var stream = new FixtureStream(Prefix(bytes, length));
                    Unknown(CaptureDates.Read(stream, bytes[0] == 'I' || bytes[0] == 'M' ? ".tif" : ".mov", false, null));
                    Bounded(stream);
                }
            byte[] jpeg = Jpeg(Tiff(true));
            for (int length = 0; length < jpeg.Length - 2; length++) Unknown(Read(Prefix(jpeg, length), ".jpg"));
        }

        private static void TiffConflictingDates()
        {
            foreach (bool little in new[] { true, false })
                Unknown(Read(TiffFields(little, true, new ushort[] { 0x9003, 0x9003 },
                    new[] { Original, "2025:01:01 01:02:03" }), ".tif"));
        }

        private static CaptureInfo Read(byte[] bytes, string extension, bool utc = false, string zone = null)
        {
            var stream = new FixtureStream(bytes);
            CaptureInfo result = CaptureDates.Read(stream, extension, utc, zone);
            Bounded(stream);
            Assert(!stream.Disposed && stream.WriteCalls == 0 && stream.SetLengthCalls == 0, "Read must retain and never write the caller's stream.");
            return result;
        }

        private static byte[] Tiff(bool little, string date = Original, bool indirect = true, ushort tag = 0x9003)
        { return TiffFields(little, indirect, new[] { tag }, new[] { date }); }

        private static byte[] TiffFields(bool little, bool indirect, ushort[] tags, string[] dates)
        {
            int directory = indirect ? 26 : 8;
            int values = directory + 2 + tags.Length * 12 + 4;
            byte[] bytes = new byte[values + tags.Length * 20]; TiffHeader(bytes, little, 8);
            if (indirect) { Put16(bytes, 8, 1, little); Entry(bytes, 10, 0x8769, 4, 1, 26, little); }
            Put16(bytes, directory, (ushort)tags.Length, little);
            for (int i = 0; i < tags.Length; i++)
            { Entry(bytes, directory + 2 + i * 12, tags[i], 2, 20, (uint)(values + i * 20), little); CopyText(bytes, values + i * 20, dates[i]); }
            return bytes;
        }

        private static void TiffHeader(byte[] bytes, bool little, uint first)
        { bytes[0] = bytes[1] = (byte)(little ? 'I' : 'M'); Put16(bytes, 2, 42, little); Put32(bytes, 4, first, little); }

        private static void Entry(byte[] bytes, int offset, ushort tag, ushort type, uint count, uint value, bool little)
        { Put16(bytes, offset, tag, little); Put16(bytes, offset + 2, type, little); Put32(bytes, offset + 4, count, little); Put32(bytes, offset + 8, value, little); }

        private static void CopyText(byte[] bytes, int offset, string text)
        { byte[] value = Encoding.ASCII.GetBytes(text); Buffer.BlockCopy(value, 0, bytes, offset, Math.Min(value.Length, 19)); }

        private static byte[] Jpeg(byte[] tiff)
        { return Join(new byte[] { 0xff, 0xd8 }, Segment(0xe1, Join(Encoding.ASCII.GetBytes("Exif\0\0"), tiff)), new byte[] { 0xff, 0xd9 }); }

        private static byte[] Segment(byte marker, byte[] payload)
        {
            byte[] bytes = new byte[payload.Length + 4]; bytes[0] = 0xff; bytes[1] = marker;
            Put16(bytes, 2, (ushort)(payload.Length + 2), false); Buffer.BlockCopy(payload, 0, bytes, 4, payload.Length); return bytes;
        }

        private static byte[] Movie(DateTime date, byte version) { return Atom("moov", Header(version, Seconds(date), 1)); }
        private static ulong Seconds(DateTime date) { return (ulong)((date.Ticks - Epoch.Ticks) / TimeSpan.TicksPerSecond); }

        private static byte[] Header(byte version, ulong creation, ulong modification)
        {
            byte[] payload = new byte[version == 1 ? 112 : 100]; payload[0] = version;
            if (version == 1) { Put64(payload, 4, creation); Put64(payload, 12, modification); Put32(payload, 20, 1000, false); }
            else { Put32(payload, 4, (uint)creation, false); Put32(payload, 8, (uint)modification, false); Put32(payload, 12, 1000, false); }
            return Atom("mvhd", payload);
        }

        private static byte[] ExplicitMovie(string text)
        { return Atom("moov", Metadata(text, false, false, "mdta", 1, "com.apple.quicktime.creationdate")); }

        private static byte[] Metadata(string text, bool full, bool reversed, string handler, uint dataType, string keyName)
        {
            byte[] key = Encoding.ASCII.GetBytes(keyName);
            byte[] keyPayload = new byte[16 + key.Length]; Put32(keyPayload, 4, 1, false);
            Put32(keyPayload, 8, (uint)(key.Length + 8), false); Type(keyPayload, 12, "mdta"); Buffer.BlockCopy(key, 0, keyPayload, 16, key.Length);
            byte[] value = Encoding.UTF8.GetBytes(text); byte[] data = new byte[value.Length + 8]; Put32(data, 0, dataType, false);
            Buffer.BlockCopy(value, 0, data, 8, value.Length);
            byte[] item = Atom("xxxx", Atom("data", data)); Put32(item, 4, 1, false);
            byte[] keys = Atom("keys", keyPayload), items = Atom("ilst", item), description = Handler(handler);
            byte[] children = reversed ? Join(items, keys, description) : Join(description, keys, items);
            return Atom("meta", full ? Join(new byte[4], children) : children);
        }

        private static byte[] Handler(string handler)
        { byte[] bytes = new byte[25]; Type(bytes, 8, handler); return Atom("hdlr", bytes); }

        private static byte[] Atom(string type, byte[] payload, bool toEnd = false)
        {
            byte[] bytes = new byte[payload.Length + 8]; Put32(bytes, 0, toEnd ? 0U : (uint)bytes.Length, false);
            Type(bytes, 4, type); Buffer.BlockCopy(payload, 0, bytes, 8, payload.Length); return bytes;
        }

        private static void Type(byte[] bytes, int offset, string type)
        { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)type[i]; }

        private static void Put16(byte[] bytes, int offset, ushort value, bool little)
        { bytes[offset + (little ? 0 : 1)] = (byte)value; bytes[offset + (little ? 1 : 0)] = (byte)(value >> 8); }

        private static void Put32(byte[] bytes, int offset, uint value, bool little)
        { for (int i = 0; i < 4; i++) bytes[offset + (little ? i : 3 - i)] = (byte)(value >> (i * 8)); }

        private static void Put64(byte[] bytes, int offset, ulong value)
        { Put32(bytes, offset, (uint)(value >> 32), false); Put32(bytes, offset + 4, (uint)value, false); }

        private static byte[] Join(params byte[][] chunks)
        {
            int length = 0; foreach (byte[] chunk in chunks) length += chunk.Length;
            byte[] result = new byte[length]; int offset = 0;
            foreach (byte[] chunk in chunks) { Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length); offset += chunk.Length; }
            return result;
        }

        private static byte[] Prefix(byte[] bytes, int length)
        { byte[] result = new byte[Math.Min(bytes.Length, length)]; Buffer.BlockCopy(bytes, 0, result, 0, result.Length); return result; }

        private static int Find(byte[] bytes, byte[] token)
        {
            for (int i = 0; i <= bytes.Length - token.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < token.Length; j++) if (bytes[i + j] != token[j]) { match = false; break; }
                if (match) return i;
            }
            throw new InvalidOperationException("Fixture token missing.");
        }

        private static void IoFailure(FixtureStream stream, string extension)
        {
            bool threw = false;
            try { CaptureDates.Read(stream, extension, false, null); }
            catch (IOException error) { threw = error.Message == "Synthetic disconnect."; }
            Assert(threw, "Real stream IOException was suppressed or replaced.");
            Assert(!stream.Disposed && stream.WriteCalls == 0, "Failing stream was changed or closed.");
        }

        private static void Bounded(FixtureStream stream)
        {
            Assert(stream.BytesRead <= 262144 && stream.ReadCalls <= 4096 && stream.SeekCalls <= 4096,
                "Parser exceeded the finite read/seek budget.");
        }

        private static void Taken(CaptureInfo info, DateTime expected, string source)
        {
            Assert(info != null && info.Taken.HasValue, "Expected known capture date.");
            Assert(info.Taken.Value == expected, "Unexpected capture value: " + info.Taken.Value.ToString("O", CultureInfo.InvariantCulture));
            Assert(info.Taken.Value.Kind == DateTimeKind.Unspecified, "Capture result is not an explicit wall clock.");
            Assert(info.Source != null && info.Source.IndexOf(source, StringComparison.Ordinal) >= 0, "Unexpected capture source.");
        }

        private static void Unknown(CaptureInfo info)
        { Assert(info != null && !info.Taken.HasValue && info.Source == null, "Unknown metadata must return an empty CaptureInfo."); }

        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private static void Test(List<string> passed, string name, Action test)
        {
            try { test(); passed.Add(name); }
            catch (Exception error) { throw new InvalidOperationException("Capture date test failed: " + name, error); }
        }

        private sealed class Chunk
        {
            public readonly long Offset;
            public readonly byte[] Bytes;
            public Chunk(long offset, byte[] bytes) { Offset = offset; Bytes = bytes; }
        }

        private sealed class FixtureStream : Stream
        {
            private readonly long length;
            private readonly Chunk[] chunks;
            private long position;
            public bool Readable = true, Seekable = true, RejectGaps, PrematureEof, FailRead, FailSeek, FailLength, Disposed;
            public int MaximumChunk = Int32.MaxValue, ReadCalls, SeekCalls, WriteCalls, SetLengthCalls;
            public long BytesRead, FarthestSeek;
            public FixtureStream(byte[] bytes) : this(bytes.Length, new Chunk(0, bytes)) { }
            public FixtureStream(long length, params Chunk[] chunks) { this.length = length; this.chunks = chunks; }
            public override bool CanRead { get { return Readable && !Disposed; } }
            public override bool CanSeek { get { return Seekable && !Disposed; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { if (FailLength) throw new IOException("Synthetic disconnect."); return length; } }
            public override long Position { get { return position; } set { position = value; } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (FailRead) throw new IOException("Synthetic disconnect.");
                ReadCalls++;
                if (PrematureEof || position >= length) return 0;
                int received = (int)Math.Min(Math.Min(count, MaximumChunk), length - position);
                foreach (Chunk chunk in chunks)
                {
                    if (position < chunk.Offset || position - chunk.Offset >= chunk.Bytes.Length) continue;
                    int index = (int)(position - chunk.Offset);
                    received = Math.Min(received, chunk.Bytes.Length - index);
                    Buffer.BlockCopy(chunk.Bytes, index, buffer, offset, received);
                    position += received; BytesRead += received; return received;
                }
                if (RejectGaps) throw new IOException("Parser attempted to read a virtual media payload.");
                Array.Clear(buffer, offset, received); position += received; BytesRead += received; return received;
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                if (FailSeek) throw new IOException("Synthetic disconnect.");
                SeekCalls++;
                position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : length + offset;
                FarthestSeek = Math.Max(FarthestSeek, position); return position;
            }
            public override void Write(byte[] buffer, int offset, int count) { WriteCalls++; throw new InvalidOperationException("Source write attempted."); }
            public override void SetLength(long value) { SetLengthCalls++; throw new InvalidOperationException("Source resize attempted."); }
            public override void Flush() { throw new InvalidOperationException("Source flush attempted."); }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }
    }
}
