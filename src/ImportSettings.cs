using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhotoImportV2
{
    /// <summary>Configuration validation only: never probes disks, mapped drives or servers.</summary>
    public static class ImportSettings
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <returns>True only when a missing setting was migrated.</returns>
        public static bool Normalize(AppState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            bool changed = false;
            if (state.ExcludedExtensions == null)
            {
                state.ExcludedExtensions = MediaRules.DefaultExcluded();
                changed = true;
            }
            else
            {
                var normalized = MediaRules.NormalizeExcluded(state.ExcludedExtensions);
                if (!String.Equals(String.Join("|", normalized), String.Join("|", state.ExcludedExtensions), StringComparison.Ordinal))
                { state.ExcludedExtensions = normalized; changed = true; }
            }
            // An intentionally empty list is a user setting, not a legacy configuration.
            if (state.Destinations == null)
            {
                state.Destinations = new List<DestinationRecord>();
                changed = true;
            }
            if (String.IsNullOrWhiteSpace(state.DestinationFailure))
            {
                state.DestinationFailure = "Next";
                changed = true;
            }
            if (!state.PromptForNewCards.HasValue)
            {
                state.PromptForNewCards = true;
                changed = true;
            }
            // StartAtLogin is read from Task Scheduler by the caller, never guessed here.
            // Cards and their import/delete preferences are deliberately not migrated here.
            return changed;
        }

        public static DestinationRecord ValidateDestination(string name, string path)
        {
            return new DestinationRecord
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = NormalizeName(name),
                Path = NormalizePath(path),
                Enabled = true
            };
        }

        public static string Address(DestinationRecord destination)
        {
            if (!String.IsNullOrWhiteSpace(destination.Type) && (destination.Type == "webdav" || destination.Type == "s3")) return StorageSettings.Normalize(destination).Path;
            return LegacyAddress(destination);
        }
        public static string LegacyAddress(DestinationRecord destination)
        {
            string path = destination.Path;
            if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return path;
            string[] parts = path.Substring(2).Split('\\');
            if (parts.Length < 2 || !parts[1].Equals("DavWWWRoot", StringComparison.OrdinalIgnoreCase)) return path;
            string[] server = parts[0].Split('@');
            bool ssl = server.Length > 1 && server[1].Equals("SSL", StringComparison.OrdinalIgnoreCase);
            string port = ssl ? server.Length > 2 ? server[2] : "443" : server.Length > 1 ? server[1] : "80";
            string address = (ssl ? "https://" : "http://") + server[0];
            if (port != (ssl ? "443" : "80")) address += ":" + port;
            for (int i = 2; i < parts.Length; i++) address += "/" + Uri.EscapeDataString(parts[i]);
            return address + (parts.Length == 2 ? "/" : "");
        }

        public static string Kind(DestinationRecord destination)
        {
            if (destination == null) throw new ArgumentNullException("destination");
            // Never resolve a drive letter to its backing share: even N: remains local.
            // The transfer engine rejects local receipts when authorizing source deletion.
            if (StorageSettings.IsDirect(destination)) return "remote";
            return NormalizePath(destination.Path).StartsWith(@"\\", StringComparison.Ordinal) ? "remote" : "local";
        }

        public static DestinationRecord Clone(DestinationRecord destination)
        {
            if (destination == null) throw new ArgumentNullException("destination");
            return new DestinationRecord
            {
                Id = destination.Id, Name = destination.Name,
                Path = destination.Path, Enabled = destination.Enabled, Type = destination.Type,
                Endpoint = destination.Endpoint, Bucket = destination.Bucket, Region = destination.Region,
                Prefix = destination.Prefix, CredentialTarget = destination.CredentialTarget
            };
        }

        /// <summary>Enabled, independent snapshots in user-selected priority order.</summary>
        public static List<DestinationRecord> Ordered(AppState state)
        {
            ValidateConfig(state);
            var result = new List<DestinationRecord>();
            foreach (DestinationRecord entry in state.Destinations)
            {
                if (!entry.Enabled) continue;
                DestinationRecord copy = StorageSettings.Normalize(entry);
                result.Add(copy);
            }
            return result;
        }

        /// <summary>Advance within the batch's frozen target list after a worker failure.</summary>
        public static int NextIndex(int current, int count, string policy)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException("count", "目标快照必须包含至少一个位置。");
            if (current < 0 || current >= count) throw new ArgumentOutOfRangeException("current", "当前位置不在目标快照中。");
            if (policy != "Next" && policy != "Stop") throw Invalid("位置不可用时的处理方式必须为 Next 或 Stop。");
            return policy == "Next" && current + 1 < count ? current + 1 : -1;
        }

        public static void ValidateConfig(AppState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (state.Destinations == null) throw Invalid("目标位置配置缺失，请先初始化设置。");
            if (state.DestinationFailure != "Next" && state.DestinationFailure != "Stop")
                throw Invalid("位置不可用时的处理方式必须为 Next 或 Stop。");
            var ids = new HashSet<Guid>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DestinationRecord entry in state.Destinations)
            {
                if (entry == null) throw Invalid("目标位置不能是空记录。");
                Guid id;
                if (!Guid.TryParse(entry.Id, out id) || id == Guid.Empty || !ids.Add(id))
                    throw Invalid("目标位置的 ID 无效或重复。");
                NormalizeName(entry.Name);
                string canonical = StorageSettings.Normalize(entry).Path;
                if (!paths.Add(canonical)) throw Invalid("目标位置重复，请合并相同的文件夹。");
            }
            // Zero enabled destinations is valid configuration; the import UI handles it.
        }

        private static string NormalizeName(string name)
        {
            if (name == null) throw Invalid("请输入位置名称。");
            string value = name.Trim();
            if (value.Length == 0 || value.Length > 120 || HasControl(value) || !ValidUnicode(value))
                throw Invalid("位置名称须为 1–120 个字符，且不能包含控制字符。");
            return value;
        }

        private static string NormalizePath(string path)
        {
            if (path == null) throw Invalid("请输入目标文件夹。");
            string value = path.Trim();
            if (value.Length == 0 || value.Length > 32760 || HasControl(value) || !ValidUnicode(value))
                throw Invalid("目标路径为空、过长或包含无效字符。");
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return FromWebDavUrl(value);
            value = value.Replace('/', '\\');
            if (value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                value.StartsWith(@"\??\", StringComparison.Ordinal))
                throw Invalid("请使用普通文件夹路径，不能使用设备或扩展命名空间路径。");
            if (value.StartsWith(@"\\", StringComparison.Ordinal)) return FromUnc(value);
            if (value.Length < 3 || !IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\')
                throw Invalid("请输入带盘符的绝对路径、UNC 文件夹或 HTTP(S) WebDAV 地址。");
            string suffix = value.Substring(3).TrimEnd('\\');
            var parts = ParseParts(suffix, '\\', false, false);
            return Char.ToUpperInvariant(value[0]) + @":\" + String.Join(@"\", parts);
        }

        private static string FromWebDavUrl(string value)
        {
            // Validate the original segments before System.Uri can erase ../ traversal.
            if (value.IndexOf('?') >= 0 || value.IndexOf('#') >= 0 || value.IndexOf('\\') >= 0)
                throw Invalid("WebDAV 地址不能包含查询参数、片段或反斜杠。");
            int separator = value.IndexOf("://", StringComparison.Ordinal);
            bool ssl = String.Equals(value.Substring(0, separator), "https", StringComparison.OrdinalIgnoreCase);
            int start = separator + 3;
            int slash = value.IndexOf('/', start);
            string authority = slash < 0 ? value.Substring(start) : value.Substring(start, slash - start);
            if (authority.Length == 0 || authority.IndexOf('@') >= 0 || authority.IndexOf('%') >= 0)
                throw Invalid("WebDAV 地址须使用普通服务器名称，不能嵌入凭据。");
            string host = authority;
            int port = ssl ? 443 : 80;
            int colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                if (colon != authority.LastIndexOf(':')) throw Invalid("当前 Windows WebDAV 路径不支持此服务器格式。");
                host = authority.Substring(0, colon);
                port = ParsePort(authority.Substring(colon + 1));
            }
            host = NormalizeHost(host);
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw Invalid("WebDAV 地址无效。");
            string rawPath = slash < 0 ? "" : value.Substring(slash + 1);
            if (rawPath.EndsWith("/", StringComparison.Ordinal)) rawPath = rawPath.Substring(0, rawPath.Length - 1);
            var parts = ParseParts(rawPath, '/', true, true);
            string server = host + (ssl ? "@SSL@" : "@") + port.ToString(CultureInfo.InvariantCulture);
            return @"\\" + server + @"\DavWWWRoot" + (parts.Count == 0 ? "" : @"\" + String.Join(@"\", parts));
        }

        private static string FromUnc(string value)
        {
            string[] raw = value.Substring(2).TrimEnd('\\').Split('\\');
            if (raw.Length < 2 || raw[0].Length == 0 || raw[1].Length == 0)
                throw Invalid("UNC 路径必须包含服务器和共享文件夹。");
            string[] serverParts = raw[0].Split('@');
            string host = NormalizeHost(serverParts[0]);
            bool ssl = false;
            bool hasWebDavServer = serverParts.Length > 1;
            int port = 80;
            if (hasWebDavServer)
            {
                if (String.Equals(serverParts[1], "SSL", StringComparison.OrdinalIgnoreCase))
                {
                    ssl = true;
                    port = serverParts.Length == 2 ? 443 : serverParts.Length == 3 ? ParsePort(serverParts[2]) : 0;
                }
                else port = serverParts.Length == 2 ? ParsePort(serverParts[1]) : 0;
                if (port == 0) throw Invalid("WebDAV 服务器格式无效。");
            }
            bool webDav = hasWebDavServer || String.Equals(raw[1], "DavWWWRoot", StringComparison.OrdinalIgnoreCase);
            var parts = new List<string>();
            for (int i = 1; i < raw.Length; i++)
            {
                string component = webDav && HasEscape(raw[i]) ? Decode(raw[i]) : raw[i];
                CheckComponent(component);
                if (webDav && HasEscape(component)) throw Invalid("路径不能包含多重 URL 编码。");
                CheckEscapedSafety(component);
                parts.Add(component);
            }
            if (String.Equals(parts[0], "DavWWWRoot", StringComparison.OrdinalIgnoreCase)) parts[0] = "DavWWWRoot";
            string server = host;
            if (webDav) server += (ssl ? "@SSL@" : "@") + port.ToString(CultureInfo.InvariantCulture);
            return @"\\" + server + @"\" + String.Join(@"\", parts);
        }

        private static List<string> ParseParts(string value, char separator, bool decode, bool rejectNestedEscapes)
        {
            var result = new List<string>();
            if (value.Length == 0) return result;
            foreach (string raw in value.Split(separator))
            {
                string component = decode ? Decode(raw) : raw;
                CheckComponent(component);
                if (rejectNestedEscapes && HasEscape(component)) throw Invalid("路径不能包含多重 URL 编码。");
                CheckEscapedSafety(component);
                result.Add(component);
            }
            return result;
        }

        private static void CheckComponent(string value)
        {
            if (value.Length == 0 || value.Length > 255 || value == "." || value == ".." ||
                value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal) ||
                value.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0 ||
                HasControl(value) || !ValidUnicode(value)) throw Invalid("文件夹名称包含不安全或无效的路径内容。");
            string stem = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                stem == "CONIN$" || stem == "CONOUT$" ||
                ((stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 stem.Length == 4 && ("123456789\u00b9\u00b2\u00b3".IndexOf(stem[3]) >= 0)))
                throw Invalid("文件夹名称不能使用 Windows 保留设备名称。");
        }

        private static void CheckEscapedSafety(string value)
        {
            if (!HasEscape(value)) return;
            string decoded = Decode(value);
            CheckComponent(decoded);
            if (HasEscape(decoded)) throw Invalid("路径不能包含多重 URL 编码。");
        }

        private static string Decode(string value)
        {
            var result = new StringBuilder();
            for (int i = 0; i < value.Length; )
            {
                if (value[i] != '%') { result.Append(value[i++]); continue; }
                var bytes = new List<byte>();
                while (i < value.Length && value[i] == '%')
                {
                    if (i + 2 >= value.Length || Hex(value[i + 1]) < 0 || Hex(value[i + 2]) < 0)
                        throw Invalid("URL 百分号编码无效。");
                    bytes.Add((byte)((Hex(value[i + 1]) << 4) | Hex(value[i + 2])));
                    i += 3;
                }
                try { result.Append(StrictUtf8.GetString(bytes.ToArray())); }
                catch (DecoderFallbackException) { throw Invalid("URL 必须使用有效的 UTF-8 编码。"); }
            }
            return result.ToString();
        }

        private static string NormalizeHost(string host)
        {
            if (String.IsNullOrEmpty(host) || host.Length > 253 || HasControl(host) || !ValidUnicode(host) ||
                host.IndexOfAny(new[] { '@', '%', ':', '/', '\\', '[', ']', '?', '#', ' ' }) >= 0 ||
                host.StartsWith(".", StringComparison.Ordinal) || host.EndsWith(".", StringComparison.Ordinal))
                throw Invalid("服务器名称无效。");
            string ascii;
            try { ascii = new IdnMapping().GetAscii(host).ToLowerInvariant(); }
            catch (ArgumentException) { throw Invalid("服务器名称无效。"); }
            if (ascii.Length > 253) throw Invalid("服务器名称过长。");
            foreach (string label in ascii.Split('.'))
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-')
                    throw Invalid("服务器名称无效。");
                foreach (char c in label)
                    if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9') && c != '-' && c != '_')
                        throw Invalid("服务器名称无效。");
            }
            IPAddress address;
            if (IPAddress.TryParse(ascii, out address))
            {
                string[] octets = ascii.Split('.');
                if (address.AddressFamily != AddressFamily.InterNetwork || octets.Length != 4)
                    throw Invalid("请使用完整的 IPv4 地址或服务器名称。");
                var normalized = new List<string>();
                foreach (string octet in octets)
                {
                    int number;
                    if (!Int32.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number > 255)
                        throw Invalid("IPv4 地址无效。");
                    normalized.Add(number.ToString(CultureInfo.InvariantCulture));
                }
                ascii = String.Join(".", normalized);
            }
            return ascii;
        }

        private static int ParsePort(string value)
        {
            int port;
            if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw Invalid("服务器端口须为 1–65535。");
            return port;
        }

        private static bool HasEscape(string value)
        {
            for (int i = 0; i + 2 < value.Length; i++)
                if (value[i] == '%' && Hex(value[i + 1]) >= 0 && Hex(value[i + 2]) >= 0) return true;
            return false;
        }
        private static int Hex(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }
        private static bool IsAsciiLetter(char value) { return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z'); }
        private static bool HasControl(string value)
        {
            foreach (char c in value) if (Char.IsControl(c)) return true;
            return false;
        }
        private static bool ValidUnicode(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (!Char.IsSurrogate(value[i])) continue;
                if (!Char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !Char.IsLowSurrogate(value[++i])) return false;
            }
            return true;
        }
        private static ArgumentException Invalid(string message) { return new ArgumentException(message); }
    }
}
