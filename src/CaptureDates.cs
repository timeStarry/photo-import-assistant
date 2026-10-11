using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PhotoImportV2
{
    public sealed class CaptureInfo
    {
        public DateTime? Taken { get; set; }
        public string Source { get; set; }
    }

    /// <summary>Reads bounded metadata from the caller's stream; never decodes media or owns the stream.</summary>
    public static class CaptureDates
    {
        private const string ExifSource = "EXIF DateTimeOriginal (0x9003)";
        private const string MovieSource = "QuickTime mvhd creation time";
        private const string ExplicitSource = "QuickTime com.apple.quicktime.creationdate";
        private const string CreationKey = "com.apple.quicktime.creationdate";
        private const uint Moov = 0x6d6f6f76, Mvhd = 0x6d766864, Udta = 0x75647461;
        private const uint Meta = 0x6d657461, Hdlr = 0x68646c72, Mdta = 0x6d647461;
        private const uint Keys = 0x6b657973, Ilst = 0x696c7374, Data = 0x64617461;
        private static readonly DateTime Epoch = new DateTime(1904, 1, 1);

        /// <summary>
        /// The current position is the media origin. The caller restores position and retains ownership.
        /// Unknown results have null Taken/Source. Actual stream I/O failures propagate to the caller.
        /// videoUtc converts only integer mvhd timestamps. Explicit date strings retain their written wall clock.
        /// </summary>
        public static CaptureInfo Read(Stream input, string extension, bool videoUtc, string timeZoneId)
        {
            if (input == null || String.IsNullOrEmpty(extension)) return Unknown();
            string kind = extension.ToLowerInvariant();
            bool jpeg = kind == ".jpg" || kind == ".jpeg";
            bool tiff = kind == ".tif" || kind == ".tiff" || kind == ".nef" || kind == ".nrw";
            bool movie = kind == ".mov" || kind == ".mp4";
            if ((!jpeg && !tiff && !movie) || !input.CanRead || !input.CanSeek) return Unknown();
            try
            {
                var reader = new MetadataReader(input);
                if (jpeg) return ReadJpeg(reader);
                if (tiff) return ReadTiff(reader, reader.Start, reader.End);
                var dates = new MovieDates();
                long position = reader.Start;
                while (position < reader.End)
                {
                    Atom atom = ReadAtom(reader, position, reader.End, true);
                    if (atom.Type == Moov) ReadMovie(reader, atom, dates);
                    position = atom.End;
                }
                VideoDate date = dates.Explicit ?? dates.Header;
                if (date == null) return Unknown();
                DateTime taken = date.Wall;
                if (videoUtc && dates.Explicit == null)
                {
                    TimeZoneInfo zone;
                    try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
                    catch (ArgumentException) { return Unknown(); }
                    catch (TimeZoneNotFoundException) { return Unknown(); }
                    catch (InvalidTimeZoneException) { return Unknown(); }
                    DateTime utc = DateTime.SpecifyKind(date.Wall, DateTimeKind.Utc);
                    DateTime converted = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
                    // ConvertTimeFromUtc clamps out-of-range results; do not report a fabricated boundary date.
                    TimeSpan offset = zone.GetUtcOffset(utc);
                    long ticks = utc.Ticks + offset.Ticks;
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return Unknown();
                    taken = DateTime.SpecifyKind(converted, DateTimeKind.Unspecified);
                }
                return new CaptureInfo { Taken = taken, Source = dates.Explicit != null ? ExplicitSource : MovieSource };
            }
            catch (MetadataException) { return Unknown(); }
        }

        private static CaptureInfo Unknown() { return new CaptureInfo(); }

        private static CaptureInfo ReadJpeg(MetadataReader reader)
        {
            byte[] signature = reader.ReadAt(reader.Start, 2, reader.End);
            if (signature[0] != 0xff || signature[1] != 0xd8) return Unknown();
            long position = reader.Start + 2;
            while (position < reader.End)
            {
                reader.Step();
                if (reader.ReadAt(position++, 1, reader.End)[0] != 0xff) throw new MetadataException();
                byte marker;
                do { marker = reader.ReadAt(position++, 1, reader.End)[0]; } while (marker == 0xff);
                if (marker == 0xda || marker == 0xd9) return Unknown(); // Never enter compressed scan data.
                if (marker == 0 || marker == 0xd8) throw new MetadataException();
                if (marker == 1 || (marker >= 0xd0 && marker <= 0xd7)) continue;
                byte[] lengthBytes = reader.ReadAt(position, 2, reader.End);
                int length = U16(lengthBytes, 0, false);
                if (length < 2 || length > reader.End - position) throw new MetadataException();
                long end = position + length;
                long payload = position + 2;
                if (marker == 0xe1 && length >= 8)
                {
                    byte[] prefix = reader.ReadAt(payload, 6, end);
                    if (prefix[0] == 'E' && prefix[1] == 'x' && prefix[2] == 'i' &&
                        prefix[3] == 'f' && prefix[4] == 0 && prefix[5] == 0)
                    {
                        CaptureInfo result = ReadTiff(reader, payload + 6, end);
                        if (result.Taken.HasValue) return result;
                    }
                }
                position = end;
            }
            return Unknown();
        }

        private static CaptureInfo ReadTiff(MetadataReader reader, long origin, long end)
        {
            byte[] header = reader.ReadAt(origin, 8, end);
            bool little = header[0] == 'I' && header[1] == 'I';
            if (!little && !(header[0] == 'M' && header[1] == 'M')) return Unknown();
            if (U16(header, 2, little) != 42) return Unknown(); // Classic TIFF only, not BigTIFF.
            uint first = U32(header, 4, little);
            if (first == 0) return Unknown();
            var pending = new Queue<uint>();
            var visited = new HashSet<uint>();
            pending.Enqueue(first);
            DateTime? date = null;
            while (pending.Count != 0)
            {
                uint offset = pending.Dequeue();
                if (offset < 8 || !visited.Add(offset) || visited.Count > 32) throw new MetadataException();
                reader.Step();
                long directory = origin + offset;
                byte[] countBytes = reader.ReadAt(directory, 2, end);
                int count = U16(countBytes, 0, little);
                if (count > 512 || 2L + count * 12L + 4L > end - directory) throw new MetadataException();
                for (int index = 0; index < count; index++)
                {
                    reader.Step();
                    byte[] entry = reader.ReadAt(directory + 2 + index * 12L, 12, end);
                    int tag = U16(entry, 0, little);
                    int type = U16(entry, 2, little);
                    uint elements = U32(entry, 4, little);
                    uint value = U32(entry, 8, little);
                    if (tag == 0x8769)
                    {
                        if (type != 4 || elements != 1 || value < 8) throw new MetadataException();
                        if (pending.Count >= 32) throw new MetadataException();
                        pending.Enqueue(value);
                    }
                    else if (tag == 0x9003 && type == 2 && elements == 20)
                    {
                        if (value < 8) throw new MetadataException();
                        byte[] bytes = reader.ReadAt(origin + value, 20, end);
                        DateTime parsed;
                        if (bytes[19] != 0 || !TryExifDate(bytes, out parsed)) continue;
                        if (date.HasValue && date.Value != parsed) throw new MetadataException();
                        date = parsed;
                    }
                }
                uint next = U32(reader.ReadAt(directory + 2 + count * 12L, 4, end), 0, little);
                if (next != 0)
                {
                    if (pending.Count >= 32) throw new MetadataException();
                    pending.Enqueue(next);
                }
            }
            return date.HasValue ? new CaptureInfo { Taken = date, Source = ExifSource } : Unknown();
        }

        private static bool TryExifDate(byte[] bytes, out DateTime date)
        {
            date = default(DateTime);
            for (int i = 0; i < 19; i++)
            {
                bool separator = i == 4 || i == 7 || i == 10 || i == 13 || i == 16;
                if (separator ? bytes[i] != (i == 10 ? ' ' : ':') : bytes[i] < '0' || bytes[i] > '9') return false;
            }
            return DateTime.TryParseExact(Encoding.ASCII.GetString(bytes, 0, 19), "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private static Atom ReadAtom(MetadataReader reader, long position, long end, bool topLevel)
        {
            reader.Step();
            byte[] header = reader.ReadAt(position, 8, end);
            ulong size = U32(header, 0, false);
            int headerSize = 8;
            if (size == 1)
            {
                size = U64(reader.ReadAt(position + 8, 8, end), 0);
                headerSize = 16;
            }
            else if (size == 0)
            {
                if (!topLevel) throw new MetadataException();
                size = (ulong)(end - position);
            }
            if (size < (ulong)headerSize || size > (ulong)(end - position)) throw new MetadataException();
            return new Atom { Type = U32(header, 4, false), Payload = position + headerSize, End = position + (long)size };
        }

        private static void ReadMovie(MetadataReader reader, Atom movie, MovieDates dates)
        {
            long position = movie.Payload;
            while (position < movie.End)
            {
                Atom child = ReadAtom(reader, position, movie.End, false);
                if (child.Type == Mvhd)
                {
                    VideoDate date = ReadMovieHeader(reader, child);
                    if (dates.Header == null) dates.Header = date;
                }
                else if (child.Type == Meta) ReadMetadata(reader, child, dates);
                else if (child.Type == Udta)
                {
                    long userPosition = child.Payload;
                    while (userPosition < child.End)
                    {
                        Atom userChild = ReadAtom(reader, userPosition, child.End, false);
                        if (userChild.Type == Meta) ReadMetadata(reader, userChild, dates);
                        userPosition = userChild.End;
                    }
                }
                // Track/media dates, filesystem dates, and generic modification fields are not capture fallbacks.
                position = child.End;
            }
        }

        private static VideoDate ReadMovieHeader(MetadataReader reader, Atom atom)
        {
            byte[] version = reader.ReadAt(atom.Payload, 4, atom.End);
            if (version[1] != 0 || version[2] != 0 || version[3] != 0) return null;
            int minimum = version[0] == 0 ? 100 : version[0] == 1 ? 112 : 0;
            if (minimum == 0 || atom.End - atom.Payload < minimum) return null;
            byte[] secondsBytes = reader.ReadAt(atom.Payload + 4, version[0] == 0 ? 4 : 8, atom.End);
            ulong seconds = version[0] == 0 ? U32(secondsBytes, 0, false) : U64(secondsBytes, 0);
            ulong maximum = (ulong)((DateTime.MaxValue.Ticks - Epoch.Ticks) / TimeSpan.TicksPerSecond);
            if (seconds == 0 || seconds > maximum) return null;
            return new VideoDate { Wall = Epoch.AddTicks((long)seconds * TimeSpan.TicksPerSecond) };
        }

        private static void ReadMetadata(MetadataReader reader, Atom meta, MovieDates dates)
        {
            long position = meta.Payload;
            // QuickTime has a plain meta container; ISO meta adds a zero version/flags prefix.
            if (U32(reader.ReadAt(position, 4, meta.End), 0, false) == 0) position += 4;
            Atom keys = null, items = null, handler = null;
            while (position < meta.End)
            {
                Atom child = ReadAtom(reader, position, meta.End, false);
                if (child.Type == Keys) { if (keys != null) throw new MetadataException(); keys = child; }
                else if (child.Type == Ilst) { if (items != null) throw new MetadataException(); items = child; }
                else if (child.Type == Hdlr) { if (handler != null) throw new MetadataException(); handler = child; }
                position = child.End;
            }
            if (handler == null || keys == null || items == null || handler.End - handler.Payload < 24) return;
            byte[] handlerBytes = reader.ReadAt(handler.Payload, 12, handler.End);
            if (U32(handlerBytes, 0, false) != 0 || U32(handlerBytes, 8, false) != Mdta) return;
            HashSet<uint> indices = ReadCreationKeys(reader, keys);
            position = items.Payload;
            while (position < items.End)
            {
                Atom item = ReadAtom(reader, position, items.End, false);
                if (indices.Contains(item.Type))
                {
                    long dataPosition = item.Payload;
                    while (dataPosition < item.End)
                    {
                        Atom data = ReadAtom(reader, dataPosition, item.End, false);
                        if (data.Type == Data && data.End - data.Payload >= 8 && data.End - data.Payload <= 72)
                        {
                            byte[] prefix = reader.ReadAt(data.Payload, 8, data.End);
                            if (U32(prefix, 0, false) == 1)
                            {
                                byte[] text = reader.ReadAt(data.Payload + 8, (int)(data.End - data.Payload - 8), data.End);
                                VideoDate parsed = ParseVideoDate(text);
                                if (dates.Explicit == null) dates.Explicit = parsed;
                            }
                        }
                        dataPosition = data.End;
                    }
                }
                position = item.End;
            }
        }

        private static HashSet<uint> ReadCreationKeys(MetadataReader reader, Atom keys)
        {
            byte[] prefix = reader.ReadAt(keys.Payload, 8, keys.End);
            if (U32(prefix, 0, false) != 0) throw new MetadataException();
            uint count = U32(prefix, 4, false);
            if (count > 512) throw new MetadataException();
            var indices = new HashSet<uint>();
            long position = keys.Payload + 8;
            for (uint index = 1; index <= count; index++)
            {
                reader.Step();
                byte[] entry = reader.ReadAt(position, 8, keys.End);
                uint size = U32(entry, 0, false);
                if (size < 8 || size > keys.End - position) throw new MetadataException();
                if (U32(entry, 4, false) == Mdta && size == CreationKey.Length + 8)
                {
                    byte[] name = reader.ReadAt(position + 8, CreationKey.Length, keys.End);
                    if (Encoding.ASCII.GetString(name) == CreationKey) indices.Add(index);
                }
                position += size;
            }
            if (position != keys.End) throw new MetadataException();
            return indices;
        }

        private static VideoDate ParseVideoDate(byte[] bytes)
        {
            if (bytes.Length < 19 || bytes.Length > 64) return null;
            for (int i = 0; i < bytes.Length; i++) if (bytes[i] < 32 || bytes[i] > 126) return null;
            string text = Encoding.ASCII.GetString(bytes);
            if (text[4] != '-' || text[7] != '-' || (text[10] != 'T' && text[10] != ' ') ||
                text[13] != ':' || text[16] != ':') return null;
            for (int i = 0; i < 19; i++)
                if (i != 4 && i != 7 && i != 10 && i != 13 && i != 16 && (text[i] < '0' || text[i] > '9')) return null;
            int end = 19;
            string format = text[10] == 'T' ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd HH:mm:ss";
            if (text.Length > end && text[end] == '.')
            {
                int fractionStart = ++end;
                while (end < text.Length && text[end] >= '0' && text[end] <= '9') end++;
                int digits = end - fractionStart;
                if (digits < 1 || digits > 7) return null;
                format += "." + new string('f', digits);
            }
            DateTime wall;
            if (!DateTime.TryParseExact(text.Substring(0, end), format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out wall)) return null;
            if (end < text.Length)
            {
                TimeSpan offset;
                string suffix = text.Substring(end);
                if (suffix == "Z") offset = TimeSpan.Zero;
                else
                {
                    if (suffix.Length == 6 && suffix[3] == ':') suffix = suffix.Remove(3, 1);
                    if (suffix.Length != 5 || (suffix[0] != '+' && suffix[0] != '-')) return null;
                    for (int i = 1; i < 5; i++) if (suffix[i] < '0' || suffix[i] > '9') return null;
                    int hours = (suffix[1] - '0') * 10 + suffix[2] - '0';
                    int minutes = (suffix[3] - '0') * 10 + suffix[4] - '0';
                    if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0)) return null;
                    offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (suffix[0] == '-' ? -1 : 1));
                }
                long utcTicks = wall.Ticks - offset.Ticks;
                if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks) return null;
            }
            return new VideoDate { Wall = wall };
        }

        private static ushort U16(byte[] bytes, int index, bool little)
        {
            return (ushort)(little ? bytes[index] | bytes[index + 1] << 8 : bytes[index] << 8 | bytes[index + 1]);
        }

        private static uint U32(byte[] bytes, int index, bool little)
        {
            if (little) return (uint)bytes[index] | (uint)bytes[index + 1] << 8 | (uint)bytes[index + 2] << 16 | (uint)bytes[index + 3] << 24;
            return (uint)bytes[index] << 24 | (uint)bytes[index + 1] << 16 | (uint)bytes[index + 2] << 8 | bytes[index + 3];
        }

        private static ulong U64(byte[] bytes, int index)
        {
            return (ulong)U32(bytes, index, false) << 32 | U32(bytes, index + 4, false);
        }

        private sealed class Atom { public uint Type; public long Payload; public long End; }
        private sealed class VideoDate { public DateTime Wall; }
        private sealed class MovieDates { public VideoDate Header; public VideoDate Explicit; }
        private sealed class MetadataException : Exception { }

        private sealed class MetadataReader
        {
            // Independent hard ceilings: requested bytes, actual Read calls, Seek calls, and parsed structures.
            private const int MaximumBytes = 262144, MaximumCalls = 4096, MaximumSeeks = 4096, MaximumNodes = 2048;
            private readonly Stream input;
            private int bytes, calls, seeks, nodes;
            public readonly long Start;
            public readonly long End;

            public MetadataReader(Stream input)
            {
                this.input = input;
                Start = input.Position;
                End = input.Length;
                if (Start < 0 || End < Start) throw new MetadataException();
            }

            public void Step() { if (++nodes > MaximumNodes) throw new MetadataException(); }

            public byte[] ReadAt(long position, int count, long end)
            {
                if (count < 0 || position < Start || end > End || position > end || count > end - position ||
                    count > MaximumBytes - bytes) throw new MetadataException();
                bytes += count;
                if (input.Position != position)
                {
                    if (++seeks > MaximumSeeks) throw new MetadataException();
                    input.Seek(position, SeekOrigin.Begin);
                    // MemoryStream slices return backing coordinates from Seek, but expose a relative Position.
                    if (input.Position != position) throw new MetadataException();
                }
                byte[] result = new byte[count];
                int read = 0;
                while (read < count)
                {
                    if (++calls > MaximumCalls) throw new MetadataException();
                    int received = input.Read(result, read, count - read);
                    if (received <= 0 || received > count - read) throw new MetadataException();
                    read += received;
                }
                return result;
            }
        }
    }
}
