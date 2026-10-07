using System;
using System.Collections.Generic;
using System.IO;

namespace PhotoImportV2
{
    /// <summary>Pure extension rules; no filesystem or application state is accessed.</summary>
    public static class MediaRules
    {
        private const int MaximumEntries = 32;
        // The leading dot is included in the canonical extension length.
        private const int MaximumExtensionLength = 16;
        private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n', '\v', '\f' };
        private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n', '\v', '\f' };
        private static readonly HashSet<string> Supported = new HashSet<string>(
            new[] { ".jpg", ".jpeg", ".nef", ".nrw", ".mov", ".mp4", ".avi", ".tif", ".tiff" },
            StringComparer.OrdinalIgnoreCase);

        public static List<string> DefaultExcluded()
        {
            return new List<string> { ".dat" };
        }

        /// <summary>Parses ASCII separators; an empty string means no exclusions.</summary>
        public static List<string> ParseExcluded(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            return NormalizeExcluded(text.Split(Separators, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Returns an independent list in first-seen order; each value is one extension.</summary>
        public static List<string> NormalizeExcluded(IEnumerable<string> values)
        {
            if (values == null) throw new ArgumentNullException("values");
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                string extension = NormalizeExtension(value);
                if (!seen.Add(extension)) continue;
                if (result.Count == MaximumEntries)
                    throw new ArgumentException("最多可排除 32 种后缀。", "values");
                result.Add(extension);
            }
            return result;
        }

        public static bool IsSupported(string path)
        {
            return !String.IsNullOrEmpty(path) && Supported.Contains(Path.GetExtension(path));
        }

        /// <summary>Compares the final extension with canonical dot-prefixed exclusions.</summary>
        public static bool IsExcluded(string path, IEnumerable<string> excluded)
        {
            if (String.IsNullOrEmpty(path) || excluded == null) return false;
            string extension = Path.GetExtension(path);
            if (String.IsNullOrEmpty(extension)) return false;
            foreach (string value in excluded)
                if (String.Equals(extension, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool Includes(string path, IEnumerable<string> excluded)
        {
            return IsSupported(path) && !IsExcluded(path, excluded);
        }

        private static string NormalizeExtension(string value)
        {
            if (value == null) throw new ArgumentException("排除后缀不能为空值。", "values");
            string token = value.Trim(Whitespace);
            int start = token.StartsWith(".", StringComparison.Ordinal) ? 1 : 0;
            int length = token.Length - start;
            if (length == 0 || length + 1 > MaximumExtensionLength)
                throw new ArgumentException("后缀须包含 1 至 15 个英文字母或数字，例如 .jpg。", "values");
            for (int i = start; i < token.Length; i++)
            {
                char character = token[i];
                if (!((character >= 'a' && character <= 'z') ||
                      (character >= 'A' && character <= 'Z') ||
                      (character >= '0' && character <= '9')))
                    throw new ArgumentException("请填写文件后缀，例如 .jpg；不支持路径或通配符。", "values");
            }
            return "." + token.Substring(start).ToLowerInvariant();
        }
    }
}
