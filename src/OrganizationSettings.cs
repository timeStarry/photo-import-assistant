using System;
using System.Globalization;

namespace PhotoImportV2
{
    /// <summary>Pure organization policy and common local/remote destination path validation.</summary>
    public static class OrganizationSettings
    {
        private const string UnknownDate = "未知日期";
        private static readonly char[] InvalidComponentChars = { '<', '>', ':', '"', '|', '?', '*' };

        /// <summary>Returns an independent, validated snapshot, including the batch's time zone.</summary>
        public static OrganizationOptions Normalize(OrganizationOptions o)
        {
            var source = o ?? new OrganizationOptions();
            return new OrganizationOptions
            {
                Mode = Choice(source.Mode, "capture", "Mode", "capture", "import", "preserve", "flat"),
                DateLayout = Choice(source.DateLayout, "day", "DateLayout", "day", "year", "month"),
                MissingDate = Choice(source.MissingDate, "unknown", "MissingDate", "unknown", "modified"),
                SeparateMedia = source.SeparateMedia,
                VideoTimeMode = Choice(source.VideoTimeMode, "camera", "VideoTimeMode", "camera", "utc"),
                TimeZoneId = Zone(source.TimeZoneId).Id
            };
        }

        public static string Key(OrganizationOptions options)
        {
            OrganizationOptions o = Normalize(options);
            return String.Join("|", new[] { o.Mode, o.DateLayout, o.MissingDate,
                o.SeparateMedia ? "true" : "false", o.VideoTimeMode, o.TimeZoneId });
        }

        public static string Summary(OrganizationOptions options)
        {
            OrganizationOptions o = Normalize(options);
            string mode = o.Mode == "capture" ? "按拍摄日期" : o.Mode == "import" ? "按导入日期" :
                o.Mode == "preserve" ? "保留原始目录" : "全部放在同一目录";
            string layout = o.DateLayout == "year" ? "yyyy/yyyy-MM-dd" :
                o.DateLayout == "month" ? "yyyy/MM/yyyy-MM-dd" : "yyyy-MM-dd";
            string result = mode;
            if (o.Mode == "capture" || o.Mode == "import") result += " · " + layout;
            return result + " · " + (o.SeparateMedia ? "RAW/JPG分目录" : "混合保存");
        }

        public static void Apply(MediaItem file, OrganizationOptions options, string importDate, CaptureInfo capture)
        {
            if (file == null) throw new ArgumentNullException("file");
            OrganizationOptions o = Normalize(options);
            string original = Relative(file.RelativePath);
            string name = Basename(original);
            string date = UnknownDate, source = UnknownDate;
            if (capture != null && capture.Taken.HasValue)
            {
                // CaptureDates has already interpreted video time. Keep its wall date;
                // converting a DateTime.Kind here would shift or convert it twice.
                date = capture.Taken.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                source = String.IsNullOrWhiteSpace(capture.Source) || !SafeText(capture.Source) ?
                    "拍摄时间" : capture.Source.Trim();
            }
            else if (o.MissingDate == "modified" && file.WriteTicks > 0 && file.WriteTicks <= DateTime.MaxValue.Ticks)
            {
                DateTime utc = new DateTime(file.WriteTicks, DateTimeKind.Utc);
                date = TimeZoneInfo.ConvertTimeFromUtc(utc, Zone(o.TimeZoneId)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                source = "文件修改时间";
            }

            string parent;
            if (o.Mode == "capture") parent = DateParent(date, o.DateLayout);
            else if (o.Mode == "import")
            {
                DateTime day;
                if (importDate == null || !DateTime.TryParseExact(importDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                    throw new ArgumentException("导入日期须为有效的 yyyy-MM-dd 日期。", "importDate");
                parent = DateParent(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), o.DateLayout);
            }
            else parent = o.Mode == "preserve" ? Parent(original) : "";

            if (o.SeparateMedia)
            {
                string category = Category(name);
                if (category.Length != 0) parent = Join(parent, category);
            }
            string target = Relative(Join(parent, name));
            // Commit together only after all policy/date/path validation has succeeded.
            file.CaptureDate = date;
            file.CaptureDateSource = source;
            file.TargetRelativePath = target;
        }

        /// <summary>Raw, slash-normalized relative path; transports encode it themselves.</summary>
        public static string Target(MediaItem file)
        {
            if (file == null) throw new ArgumentNullException("file");
            string original = Relative(file.RelativePath);
            if (file.TargetRelativePath == null) return original;
            string target = Relative(file.TargetRelativePath);
            if (!String.Equals(Basename(original), Basename(target), StringComparison.Ordinal))
                throw new ArgumentException("目标文件名必须与原始文件名完全一致。", "file");
            return target;
        }

        private static string Choice(string value, string fallback, string name, params string[] allowed)
        {
            if (String.IsNullOrWhiteSpace(value)) return fallback;
            string normalized = value.Trim().ToLowerInvariant();
            foreach (string choice in allowed) if (normalized == choice) return normalized;
            throw new ArgumentException("文件整理设置无效：" + name + "。", name);
        }

        private static TimeZoneInfo Zone(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
            string value = id.Trim();
            if (!SafeText(value)) throw new ArgumentException("时区标识无效。", "TimeZoneId");
            try { return TimeZoneInfo.FindSystemTimeZoneById(value); }
            catch (TimeZoneNotFoundException error) { throw BadZone(error); }
            catch (InvalidTimeZoneException error) { throw BadZone(error); }
            catch (ArgumentException error) { throw BadZone(error); }
            catch (System.Security.SecurityException error) { throw BadZone(error); }
        }

        private static ArgumentException BadZone(Exception error)
        { return new ArgumentException("时区不存在或不可用。", "TimeZoneId", error); }

        private static string DateParent(string date, string layout)
        {
            if (date == UnknownDate || layout == "day") return date;
            string year = date.Substring(0, 4);
            return layout == "year" ? year + "/" + date : year + "/" + date.Substring(5, 2) + "/" + date;
        }

        private static string Category(string name)
        {
            int dot = name.LastIndexOf('.');
            string extension = dot < 0 ? "" : name.Substring(dot).ToLowerInvariant();
            if (extension == ".nef" || extension == ".nrw") return "RAW";
            if (extension == ".jpg" || extension == ".jpeg") return "JPG";
            if (extension == ".mov" || extension == ".mp4" || extension == ".avi") return "VIDEO";
            if (extension == ".tif" || extension == ".tiff") return "TIFF";
            return "";
        }

        private static string Relative(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 4096)
                throw new ArgumentException("相对路径为空或过长。", "file");
            string normalized = value.Replace('\\', '/');
            foreach (string part in normalized.Split('/'))
            {
                if (part.Length == 0 || part.Length > 255 || part == "." || part == ".." ||
                    part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.IndexOfAny(InvalidComponentChars) >= 0 || !SafeText(part))
                    throw new ArgumentException("相对路径包含不安全的 Windows 路径内容。", "file");
                string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    stem == "CONIN$" || stem == "CONOUT$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789\u00b9\u00b2\u00b3".IndexOf(stem[3]) >= 0))
                    throw new ArgumentException("相对路径不能包含设备名称。", "file");
                if (part.IndexOf(".partial", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new ArgumentException("临时传输路径不能作为目标。", "file");
            }
            return normalized;
        }

        private static bool SafeText(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (Char.IsControl(c)) return false;
                if (Char.IsHighSurrogate(c))
                {
                    if (++i == value.Length || !Char.IsLowSurrogate(value[i])) return false;
                }
                else if (Char.IsLowSurrogate(c)) return false;
            }
            return true;
        }

        private static string Basename(string path) { return path.Substring(path.LastIndexOf('/') + 1); }
        private static string Parent(string path) { int slash = path.LastIndexOf('/'); return slash < 0 ? "" : path.Substring(0, slash); }
        private static string Join(string parent, string name) { return parent.Length == 0 ? name : parent + "/" + name; }
    }
}
