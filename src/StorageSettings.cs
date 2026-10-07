using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotoImportV2
{
    public static class StorageSettings
    {
        public static string TypeOf(DestinationRecord d)
        {
            if (d == null) throw new ArgumentNullException("d");
            if (!String.IsNullOrWhiteSpace(d.Type)) return d.Type.ToLowerInvariant();
            string p = d.Path ?? "";
            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || p.IndexOf("\\DavWWWRoot", StringComparison.OrdinalIgnoreCase) >= 0) return "webdav";
            return p.StartsWith(@"\\", StringComparison.Ordinal) ? "smb" : "folder";
        }
        public static bool IsDirect(DestinationRecord d) { string t = TypeOf(d); return t == "webdav" || t == "s3"; }
        public static string TypeName(DestinationRecord d)
        { string t = TypeOf(d); return t == "folder" ? "Windows 文件夹" : t == "smb" ? "网络共享" : t == "webdav" ? "WebDAV" : "S3"; }
        public static DestinationRecord Normalize(DestinationRecord d)
        {
            if (d == null) throw new ArgumentNullException("d");
            var copy = ImportSettings.Clone(d); copy.Type = TypeOf(d);
            if (copy.Type == "folder" || copy.Type == "smb")
            {
                var validated = ImportSettings.ValidateDestination(copy.Name, copy.Path);
                if (TypeOf(validated) != copy.Type) throw new ArgumentException("位置类型与路径不一致。");
                copy.Name = validated.Name; copy.Path = validated.Path;
            }
            else if (copy.Type == "webdav")
            {
                string url = String.IsNullOrWhiteSpace(copy.Endpoint) ? ImportSettings.LegacyAddress(copy) : copy.Endpoint;
                var validated = ImportSettings.ValidateDestination(copy.Name, url);
                if (TypeOf(validated) != "webdav") throw new ArgumentException("请输入 HTTP(S) WebDAV 地址。");
                copy.Name = validated.Name;
                copy.Endpoint = ImportSettings.LegacyAddress(validated).TrimEnd('/');
                copy.Path = copy.Endpoint;
            }
            else if (copy.Type == "s3")
            {
                Uri endpoint;
                string value = (copy.Endpoint ?? "").Trim();
                // Reuse strict URL safety checks, but retain the URL for direct HTTP.
                var validated = ImportSettings.ValidateDestination(copy.Name, value);
                if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint) || (endpoint.Scheme != "http" && endpoint.Scheme != "https")) throw new ArgumentException("S3 Endpoint 须为 HTTP(S) 地址。");
                copy.Name = validated.Name; copy.Endpoint = ImportSettings.LegacyAddress(validated).TrimEnd('/');
                copy.Bucket = (copy.Bucket ?? "").Trim();
                if (copy.Bucket.Length < 3 || copy.Bucket.Length > 63 || !copy.Bucket.All(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '.') || copy.Bucket[0] == '.' || copy.Bucket[copy.Bucket.Length - 1] == '.') throw new ArgumentException("Bucket 名称无效。");
                copy.Region = String.IsNullOrWhiteSpace(copy.Region) ? "us-east-1" : copy.Region.Trim();
                if (copy.Region.Length > 64 || !copy.Region.All(c => Char.IsLetterOrDigit(c) || c == '-')) throw new ArgumentException("S3 区域无效。");
                copy.Prefix = Relative((copy.Prefix ?? "").Trim().Trim('/'), true);
                copy.Path = copy.Endpoint + "/" + copy.Bucket + (copy.Prefix.Length == 0 ? "" : "/" + copy.Prefix);
            }
            else throw new ArgumentException("不支持的位置类型。");
            if (copy.CredentialTarget != null && (copy.CredentialTarget.Length > 512 || copy.CredentialTarget.Any(Char.IsControl))) throw new ArgumentException("凭据引用无效。");
            return copy;
        }
        public static string Relative(string value, bool emptyAllowed)
        {
            if (value == null || (!emptyAllowed && value.Length == 0) || value.Length > 4096 || value.StartsWith("/", StringComparison.Ordinal) || value.IndexOf('\\') >= 0 || value.Any(Char.IsControl)) throw new ArgumentException("远端相对路径无效。");
            if (value.Length == 0) return value;
            foreach (string part in value.Split('/')) if (part.Length == 0 || part == "." || part == ".." || part.IndexOf(':') >= 0) throw new ArgumentException("远端路径不能越过保存位置。");
            return value;
        }
        public static string Key(DestinationRecord d)
        { var n = Normalize(d); return n.Type + "|" + n.Path + "|" + n.Region + "|" + n.CredentialTarget; }
        public static int Parallel(AppState s) { return s.MaxParallel.HasValue ? s.MaxParallel.Value : 2; }
        public static int IdleSeconds(AppState s) { return s.IdleTimeoutSeconds.HasValue ? s.IdleTimeoutSeconds.Value : 180; }
        public static long LargeBytes(AppState s) { return s.LargeFileBytes.HasValue ? s.LargeFileBytes.Value : 256L * 1024 * 1024; }
        public static void ValidateTransfer(AppState s)
        {
            if (Parallel(s) < 1 || Parallel(s) > 4 || IdleSeconds(s) < 30 || IdleSeconds(s) > 1800 || LargeBytes(s) < 16L * 1024 * 1024 || LargeBytes(s) > 16L * 1024 * 1024 * 1024) throw new ArgumentException("传输设置超出允许范围。");
        }
    }
}
