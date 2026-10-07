using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoImportV2
{
    /// <summary>Only opaque references are stored in application settings.</summary>
    public static class CredentialStore
    {
        internal const string TargetPrefix = "PhotoImportV2/Remote/";
        private const uint Generic = 1;
        private const uint LocalMachine = 2; // Persists for this Windows user across logons.
        private const int MaxBlobBytes = 2560;
        private static readonly UnicodeEncoding BlobEncoding = new UnicodeEncoding(false, false, true);

        public static NetworkCredential Read(DestinationRecord destination)
        {
            if (destination == null) throw new ArgumentNullException("destination");
            if (!String.IsNullOrWhiteSpace(destination.CredentialTarget))
            {
                ValidateTarget(destination.CredentialTarget);
                // An explicit reference must not silently select a different account.
                return ReadTarget(destination.CredentialTarget);
            }
            if (StorageSettings.TypeOf(destination) != "webdav") return null;
            List<string> targets = LegacyTargets(destination);
            foreach (string target in targets)
            {
                NetworkCredential found = ReadTarget(target);
                if (found != null) return found;
            }

            // Enumerate narrowly by candidate prefix, then require exact target and
            // generic type before decoding. Never enumerate the entire vault.
            foreach (string target in targets)
            {
                if (target.IndexOf('*') >= 0) continue;
                foreach (string prefix in new[] { target, "LegacyGeneric:target=" + target })
                {
                    IntPtr buffer;
                    uint count;
                    if (!CredEnumerate(prefix + "*", 0, out count, out buffer))
                    {
                        CheckReadError(Marshal.GetLastWin32Error());
                        continue;
                    }
                    try
                    {
                        for (uint i = 0; i < count; i++)
                        {
                            IntPtr pointer = Marshal.ReadIntPtr(buffer, checked((int)i * IntPtr.Size));
                            var value = (NativeCredential)Marshal.PtrToStructure(pointer, typeof(NativeCredential));
                            if (value.Type != Generic || MatchingTarget(value.TargetName, new[] { target }) < 0) continue;
                            NetworkCredential match = Decode(value);
                            if (match != null) return match;
                        }
                    }
                    finally { CredFree(buffer); }
                }
            }
            return null;
        }

        public static string Save(string username, string secret, string existingTarget)
        {
            if (String.IsNullOrEmpty(username) || username.Length > 513 || HasControl(username))
                throw new ArgumentException("凭据用户名无效。", "username");
            if (secret == null || secret.IndexOf('\0') >= 0)
                throw new ArgumentException("凭据密码无效。", "secret");
            byte[] bytes;
            try { bytes = BlobEncoding.GetBytes(secret); }
            catch (EncoderFallbackException) { throw new ArgumentException("凭据密码编码无效。", "secret"); }
            if (bytes.Length > MaxBlobBytes)
            {
                Array.Clear(bytes, 0, bytes.Length);
                throw new ArgumentException("凭据密码过长。", "secret");
            }
            // Editing an imported Windows credential creates an app-owned reference;
            // it must not overwrite a credential used by Windows or another program.
            // References are immutable: a running batch must not suddenly resolve
            // an edited password to another account's namespace.
            string target = NewReference();
            IntPtr blob = IntPtr.Zero;
            try
            {
                if (bytes.Length != 0)
                {
                    blob = Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes, 0, blob, bytes.Length);
                }
                var value = new NativeCredential
                {
                    Type = Generic, TargetName = target, UserName = username,
                    CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = LocalMachine
                };
                if (!CredWrite(ref value, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Windows 凭据。");
                return target;
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                if (blob != IntPtr.Zero)
                {
                    Marshal.Copy(bytes, 0, blob, bytes.Length);
                    Marshal.FreeHGlobal(blob);
                }
            }
        }

        internal static bool OwnedTarget(string target)
        {
            if (target == null || !target.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            Guid id;
            return Guid.TryParseExact(target.Substring(TargetPrefix.Length), "N", out id);
        }
        internal static string NewReference() { return TargetPrefix + Guid.NewGuid().ToString("N"); }

        internal static List<string> LegacyTargets(DestinationRecord destination)
        {
            string endpoint = destination.Endpoint;
            if (String.IsNullOrWhiteSpace(endpoint)) endpoint = ImportSettings.LegacyAddress(destination);
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("WebDAV 凭据位置无效。");
            var values = new List<string>();
            AddTarget(values, uri.AbsoluteUri.TrimEnd('/'));
            AddTarget(values, uri.AbsoluteUri.TrimEnd('/') + "/");
            AddTarget(values, uri.GetLeftPart(UriPartial.Authority));
            AddTarget(values, uri.GetLeftPart(UriPartial.Authority) + "/");
            // Microsoft network targets commonly contain the WebClient authority.
            string authority = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
            AddTarget(values, authority);
            AddTarget(values, uri.Host);
            string webClientHost = uri.Host + (uri.Scheme == "https" ? "@SSL" : "") +
                (uri.IsDefaultPort ? "" : "@" + uri.Port);
            AddTarget(values, webClientHost);
            var targets = new List<string>();
            foreach (string value in values)
            {
                AddTarget(targets, value);
                AddTarget(targets, "Microsoft_Windows_Network:target=" + value);
            }
            return targets;
        }

        internal static int MatchingTarget(string target, IList<string> expected)
        {
            if (target == null) return -1;
            // Some enumeration providers expose the generic namespace explicitly.
            const string genericNamespace = "LegacyGeneric:target=";
            if (target.StartsWith(genericNamespace, StringComparison.OrdinalIgnoreCase))
                target = target.Substring(genericNamespace.Length);
            for (int i = 0; i < expected.Count; i++)
                if (String.Equals(target, expected[i], StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static void AddTarget(List<string> targets, string value)
        {
            if (!targets.Exists(delegate(string target) { return String.Equals(target, value, StringComparison.OrdinalIgnoreCase); }))
                targets.Add(value);
        }

        private static NetworkCredential ReadTarget(string target)
        {
            IntPtr pointer;
            if (!CredRead(target, Generic, 0, out pointer))
            {
                CheckReadError(Marshal.GetLastWin32Error());
                return null;
            }
            try { return Decode((NativeCredential)Marshal.PtrToStructure(pointer, typeof(NativeCredential))); }
            finally { CredFree(pointer); }
        }

        private static void CheckReadError(int error)
        {
            if (error != 1168 && error != 1312)
                throw new Win32Exception(error, "无法读取 Windows 凭据。");
        }

        private static NetworkCredential Decode(NativeCredential value)
        {
            if (value.Type != Generic || String.IsNullOrEmpty(value.UserName) || value.UserName.Length > 513 || HasControl(value.UserName)) return null;
            byte[] bytes = null;
            try
            {
                if (value.CredentialBlobSize > MaxBlobBytes || (value.CredentialBlobSize & 1) != 0 ||
                    (value.CredentialBlobSize != 0 && value.CredentialBlob == IntPtr.Zero)) return null;
                bytes = new byte[(int)value.CredentialBlobSize];
                if (bytes.Length != 0) Marshal.Copy(value.CredentialBlob, bytes, 0, bytes.Length);
                return DecodeBlob(value.UserName, bytes);
            }
            finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        }

        internal static NetworkCredential DecodeBlob(string username, byte[] bytes)
        {
            if (String.IsNullOrEmpty(username) || username.Length > 513 || HasControl(username) ||
                bytes == null || bytes.Length > MaxBlobBytes || (bytes.Length & 1) != 0) return null;
            string secret;
            try { secret = BlobEncoding.GetString(bytes); }
            catch (DecoderFallbackException) { return null; }
            // Accept one optional UTF-16 terminator, but never embedded NULs or
            // opaque binary blobs that are not usable password credentials.
            if (secret.EndsWith("\0", StringComparison.Ordinal)) secret = secret.Substring(0, secret.Length - 1);
            if (secret.IndexOf('\0') >= 0 || secret.IndexOf('\uFFFE') >= 0 || secret.IndexOf('\uFFFF') >= 0) return null;
            int slash = username.IndexOf('\\');
            return slash > 0 && slash < username.Length - 1
                ? new NetworkCredential(username.Substring(slash + 1), secret, username.Substring(0, slash))
                : new NetworkCredential(username, secret);
        }

        private static bool HasControl(string value)
        {
            foreach (char c in value) if (Char.IsControl(c)) return true;
            return false;
        }

        private static void ValidateTarget(string target)
        {
            if (target.Length > 512 || HasControl(target)) throw new ArgumentException("凭据引用无效。");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);
        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);
    }
}
