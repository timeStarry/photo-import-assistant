using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotoImportV2
{
    public static class IdentityVerifier
    {
        public const string MarkerFileName = ".photoimport-id.json";

        // A false result includes inaccessible media, malformed markers and disconnects.
        public static bool Check(string root, string cardId, string expectedSerial, long expectedCapacity)
        {
            try
            {
                using (var identity = new TransferIdentity(root, cardId, expectedSerial, expectedCapacity))
                    return true;
            }
            catch (Exception) { return false; }
        }
    }

    public static class TransferEngine
    {
        private const int BufferSize = 65536;
        private const int CandidateLimit = 32;

        public static WorkResult Run(WorkRequest request)
        {
            return Run(request, TransferNative.RenameWithoutReplace, File.Move, false);
        }

        // Per-call injection permits deterministic local fixtures for the WebClient failure
        // and rename races. No request field or mutable global can enable this fixture mode.
        internal static WorkResult Run(WorkRequest request, Func<SafeFileHandle, string, bool> rename,
            Action<string, string> move, bool localWebDavFixture, bool directFixture = false,
            Action<string, FileStream> directStage = null)
        {
            try
            {
                // Do not consult mutable caller-owned objects after starting an operation.
                WorkRequest work = Freeze(request);
                if (String.Equals(work.Operation, "Copy", StringComparison.OrdinalIgnoreCase)) return Copy(work, rename, move, localWebDavFixture, directFixture, directStage);
                if (String.Equals(work.Operation, "Delete", StringComparison.OrdinalIgnoreCase)) return Delete(work);
                throw new InvalidOperationException("An explicit Copy or Delete operation is required.");
            }
            catch (Exception error)
            {
                return new WorkResult { Success = false, Error = error.GetType().Name + ": " + error.Message };
            }
        }

        private static WorkResult Copy(WorkRequest work, Func<SafeFileHandle, string, bool> rename,
            Action<string, string> move, bool localWebDavFixture, bool directFixture, Action<string, FileStream> directStage)
        {
            Require(work.File != null, "A source snapshot is required.");
            Require(String.Equals(work.DestinationKind, "remote", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(work.DestinationKind, "local", StringComparison.OrdinalIgnoreCase), "Copy destination kind must be remote or local.");
            work.DestinationKind = work.DestinationKind.ToLowerInvariant();
            string root = TransferPaths.Full(work.SourceRoot);
            string source = SourcePath(root, work.File.SourcePath);
            string relative = source.Substring(TransferPaths.Prefix(root).Length);
            ValidateRelative(work.File.RelativePath, relative);
            string destinationRoot = TransferPaths.Full(work.DestinationRoot);
            string destination = TransferPaths.Full(Path.Combine(destinationRoot, relative));
            ValidateDestination(root, source, destination);
            Require(TransferPaths.Child(destinationRoot, destination), "Destination escaped its root.");

            using (var identity = new TransferIdentity(root, work.CardId, work.ExpectedSerial, work.ExpectedCapacity))
            using (var sourceDirectories = new TransferDirectories(Path.GetDirectoryName(source), false))
            using (var input = TransferNative.OpenRead(source, false))
            {
                CheckSnapshot(input, work.File.Length, work.File.WriteTicks);
                string hash = Hash(input, work.File.Length);
                using (var destinationDirectories = new TransferDirectories(Path.GetDirectoryName(destination), true))
                {
                    OwnedPartial partial = null;
                    try
                    {
                        for (int attempt = 0; attempt < CandidateLimit; attempt++)
                        {
                            string candidate = Candidate(destination, hash, attempt);
                            using (FileStream existing = TransferNative.TryOpenRead(candidate))
                            {
                                if (existing != null)
                                {
                                    Require(!TransferNative.SameFile(input, existing), "Destination aliases the source.");
                                    if (existing.Length == work.File.Length && EqualHash(Hash(existing, work.File.Length), hash))
                                    {
                                        identity.Recheck();
                                        CheckSnapshot(input, work.File.Length, work.File.WriteTicks);
                                        return Copied(work, identity, source, candidate, hash, true);
                                    }
                                    // Length is only a cheap rejection; equality always requires SHA256.
                                    continue;
                                }
                            }

                            // WebClient cannot publish by either rename API on some servers.
                            // CREATE_NEW prevents overwrite; close commits, then readback verifies.
                            if (TransferPaths.IsWebDav(candidate) || directFixture)
                            {
                                WorkResult direct = CopyDirect(work, identity, input, source, candidate, hash, directStage);
                                if (direct != null) return direct;
                                continue; // A racing CREATE_NEW collision consumes one bounded attempt.
                            }

                            if (partial == null)
                            {
                                partial = new OwnedPartial(Path.GetDirectoryName(destination));
                                input.Position = 0;
                                CopyExactly(input, partial.Stream, work.File.Length);
                                partial.Stream.Flush(true);
                                Require(EqualHash(Hash(partial.Stream, work.File.Length), hash), "Destination readback hash mismatch.");
                            }
                            identity.Recheck();
                            CheckSnapshot(input, work.File.Length, work.File.WriteTicks);
                            if (partial.Publish(candidate, hash, work.File.Length, rename, move,
                                TransferPaths.IsWebDav(candidate) || localWebDavFixture))
                            {
                                if (partial.UsedClassicRename)
                                {
                                    // The pathname became mutable when the writer closed. Only a
                                    // securely reopened, locked, fully hashed final file earns a receipt.
                                    Require(!TransferNative.SameFile(input, partial.FinalStream), "Destination aliases the source.");
                                    Require(EqualHash(Hash(partial.FinalStream, work.File.Length), hash), "Final destination hash mismatch after classic rename.");
                                }
                                identity.Recheck();
                                CheckSnapshot(input, work.File.Length, work.File.WriteTicks);
                                return Copied(work, identity, source, candidate, hash, false);
                            }
                        }
                        throw new IOException("Destination collision limit reached; no file was overwritten.");
                    }
                    finally { if (partial != null) partial.Dispose(); }
                }
            }
        }

        private static WorkResult CopyDirect(WorkRequest work, TransferIdentity identity, FileStream input,
            string source, string candidate, string hash, Action<string, FileStream> stage)
        {
            try
            {
                identity.Recheck();
                CheckSnapshot(input, work.File.Length, work.File.WriteTicks);
                FileStream writer;
                try { writer = TransferNative.CreateFinal(candidate); }
                catch (Win32Exception error)
                {
                    if (error.NativeErrorCode == 80 || error.NativeErrorCode == 183) return null;
                    throw;
                }
                using (writer)
                {
                    if (stage != null) stage(candidate, writer); // Local fixture failure injection only.
                    input.Position = 0;
                    CopyExactly(input, writer, work.File.Length);
                    writer.Flush(true);
                }
                // WebClient commits on close. The final pathname is never cleaned up, even if
                // its contents change before we reopen it. Only fresh full verification earns a receipt.
                if (stage != null) stage(candidate, null);
                using (FileStream verified = TransferNative.OpenRead(candidate, false))
                {
                    Require(!TransferNative.SameFile(input, verified), "Destination aliases the source.");
                    Require(EqualHash(Hash(verified, work.File.Length), hash), "Final destination hash mismatch after direct copy.");
                    identity.Recheck();
                    CheckSnapshot(input, work.File.Length, work.File.WriteTicks);
                    return Copied(work, identity, source, candidate, hash, false);
                }
            }
            catch (Exception error)
            {
                throw new IOException("WebDAV direct copy was not verified; no receipt issued, source retained. An incomplete or unverified final file may remain at " +
                    candidate + " (HRESULT=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) + "): " + error.Message, error);
            }
        }

        private static WorkResult Delete(WorkRequest work)
        {
            TransferReceipt receipt = work.Receipt;
            Require(receipt != null, "A verified remote receipt is required.");
            Require(String.Equals(receipt.DestinationKind, "remote", StringComparison.OrdinalIgnoreCase), "Local/fallback receipts cannot authorize deletion.");
            Require(String.IsNullOrEmpty(work.DestinationKind) || String.Equals(work.DestinationKind, "remote", StringComparison.OrdinalIgnoreCase), "Delete destination kind disagrees with the receipt.");
            Require(IsHash(receipt.Sha256), "Receipt SHA256 is invalid.");
            string root = TransferPaths.Full(work.SourceRoot);
            Require(TransferPaths.Same(root, TransferPaths.Full(receipt.SourceRoot)), "Receipt source root mismatch.");
            Require(String.Equals(work.CardId, receipt.CardId, StringComparison.Ordinal), "Receipt card ID mismatch.");
            string source = SourcePath(root, receipt.SourcePath);
            string destination = TransferPaths.Full(receipt.DestinationPath);
            ValidateDestination(root, source, destination);
            if (!String.IsNullOrEmpty(work.DestinationRoot))
                Require(TransferPaths.Child(TransferPaths.Full(work.DestinationRoot), destination), "Receipt destination escaped the requested root.");
            if (work.File != null)
            {
                Require(TransferPaths.Same(source, TransferPaths.Full(work.File.SourcePath)) && work.File.Length == receipt.Length &&
                    work.File.WriteTicks == receipt.WriteTicks, "Source snapshot disagrees with the receipt.");
                ValidateRelative(work.File.RelativePath, source.Substring(TransferPaths.Prefix(root).Length));
            }

            using (var identity = new TransferIdentity(root, work.CardId, work.ExpectedSerial, work.ExpectedCapacity))
            using (var sourceDirectories = new TransferDirectories(Path.GetDirectoryName(source), false))
            using (var destinationDirectories = new TransferDirectories(Path.GetDirectoryName(destination), false))
            // Keep the destination locked until the source handle has closed and deletion completes.
            using (var target = TransferNative.OpenRead(destination, false))
            {
                Require(String.Equals(receipt.Serial, identity.Serial, StringComparison.OrdinalIgnoreCase) &&
                    receipt.Capacity == identity.Capacity, "Receipt volume identity mismatch.");
                using (var input = TransferNative.OpenRead(source, true))
                {
                    Require(!TransferNative.SameFile(input, target), "Destination aliases the source.");
                    CheckSnapshot(input, receipt.Length, receipt.WriteTicks);
                    Require(EqualHash(Hash(target, receipt.Length), receipt.Sha256), "Destination no longer matches the receipt.");
                    Require(EqualHash(Hash(input, receipt.Length), receipt.Sha256), "Source no longer matches the receipt.");
                    identity.Recheck();
                    CheckSnapshot(input, receipt.Length, receipt.WriteTicks);
                    // This is the only source deletion site. No path-based delete, reopen, or
                    // FILE_FLAG_DELETE_ON_CLOSE is used: failed validation cannot delete a source.
                    TransferNative.MarkDelete(input.SafeFileHandle);
                }
            }
            return new WorkResult { Success = true, Deleted = true, Receipt = receipt };
        }

        private static string SourcePath(string root, string path)
        {
            string source = TransferPaths.Full(path);
            Require(TransferPaths.Child(Path.Combine(root, "DCIM"), source), "Source must be strictly inside the card's DCIM directory.");
            Require(!TransferPaths.IsPartial(source), "Partial files cannot be transferred or deleted.");
            return source;
        }

        private static void ValidateRelative(string supplied, string actual)
        {
            if (String.IsNullOrEmpty(supplied)) return;
            Require(String.Equals(supplied.Replace('/', '\\'), actual, StringComparison.OrdinalIgnoreCase), "Source relative path does not match its absolute path.");
        }

        private static void ValidateDestination(string root, string source, string destination)
        {
            Require(!TransferPaths.Same(source, destination) && !TransferPaths.Same(root, destination) &&
                !TransferPaths.Child(root, destination), "Destination must be outside the source card root.");
            Require(!TransferPaths.IsPartial(destination), "A partial file cannot be a verified destination.");
        }

        private static WorkResult Copied(WorkRequest work, TransferIdentity identity, string source, string destination, string hash, bool reused)
        {
            return new WorkResult
            {
                Success = true, ReusedExisting = reused,
                Receipt = new TransferReceipt
                {
                    SourcePath = source, SourceRoot = identity.Root, CardId = identity.CardId,
                    Serial = identity.Serial, Capacity = identity.Capacity,
                    Length = work.File.Length, WriteTicks = work.File.WriteTicks,
                    DestinationPath = destination, DestinationKind = work.DestinationKind,
                    Sha256 = hash, VerifiedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                }
            };
        }

        private static string Candidate(string original, string hash, int attempt)
        {
            if (attempt == 0) return original;
            string suffix = "." + hash + (attempt == 1 ? "" : "." + (attempt - 1).ToString(CultureInfo.InvariantCulture));
            return Path.Combine(Path.GetDirectoryName(original), Path.GetFileNameWithoutExtension(original) + suffix + Path.GetExtension(original));
        }

        internal static void CheckSnapshot(FileStream stream, long length, long writeTicks)
        {
            Require(length >= 0 && writeTicks > 0, "Invalid source snapshot.");
            TransferNative.FileInformation information = TransferNative.Information(stream.SafeFileHandle, false);
            long observedLength = ((long)information.SizeHigh << 32) | information.SizeLow;
            long fileTime = ((long)information.WriteHigh << 32) | information.WriteLow;
            Require(observedLength == length && stream.Length == length && DateTime.FromFileTimeUtc(fileTime).Ticks == writeTicks,
                "Source length or last-write snapshot changed.");
        }

        private static void CopyExactly(FileStream input, FileStream output, long length)
        {
            byte[] buffer = new byte[BufferSize];
            long remaining = length;
            while (remaining > 0)
            {
                int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                Require(count > 0, "Source ended before its snapshot length.");
                output.Write(buffer, 0, count);
                remaining -= count;
            }
            Require(input.ReadByte() == -1, "Source grew beyond its snapshot length.");
        }

        internal static string Hash(FileStream stream, long length)
        {
            Require(length >= 0 && stream.Length == length, "File length does not match the verified snapshot.");
            stream.Position = 0;
            byte[] buffer = new byte[BufferSize];
            using (SHA256 sha = SHA256.Create())
            {
                long remaining = length;
                while (remaining > 0)
                {
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    Require(count > 0, "File disconnected or ended during hashing.");
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                    remaining -= count;
                }
                Require(stream.ReadByte() == -1, "File grew during hashing.");
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'))) return false;
            return true;
        }

        private static bool EqualHash(string left, string right) { return String.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        internal static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }

        private static WorkRequest Freeze(WorkRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            MediaItem file = request.File;
            TransferReceipt receipt = request.Receipt;
            return new WorkRequest
            {
                Operation = request.Operation, SourceRoot = request.SourceRoot, CardId = request.CardId,
                ExpectedSerial = request.ExpectedSerial, ExpectedCapacity = request.ExpectedCapacity,
                DestinationRoot = request.DestinationRoot, DestinationKind = request.DestinationKind,
                File = file == null ? null : new MediaItem { SourcePath = file.SourcePath, RelativePath = file.RelativePath, Length = file.Length, WriteTicks = file.WriteTicks },
                Receipt = receipt == null ? null : new TransferReceipt
                {
                    SourcePath = receipt.SourcePath, SourceRoot = receipt.SourceRoot, CardId = receipt.CardId,
                    Serial = receipt.Serial, Capacity = receipt.Capacity, Length = receipt.Length, WriteTicks = receipt.WriteTicks,
                    DestinationPath = receipt.DestinationPath, DestinationKind = receipt.DestinationKind,
                    Sha256 = receipt.Sha256, VerifiedUtc = receipt.VerifiedUtc
                }
            };
        }

        private sealed class OwnedPartial : IDisposable
        {
            internal readonly FileStream Stream;
            internal FileStream FinalStream { get; private set; }
            internal bool UsedClassicRename { get; private set; }
            private readonly string ownedPath;
            private bool published;
            private Win32Exception renameFailure;

            internal OwnedPartial(string directory)
            {
                ownedPath = Path.Combine(directory, ".photoimport-" + Guid.NewGuid().ToString("N") + ".partial");
                Stream = TransferNative.CreatePartial(ownedPath);
            }

            internal bool Publish(string path, string hash, long length, Func<SafeFileHandle, string, bool> rename,
                Action<string, string> move, bool allowClassic)
            {
                Require(TransferPaths.Same(Path.GetDirectoryName(ownedPath), Path.GetDirectoryName(path)), "Publication must remain in the partial's directory.");
                if (!UsedClassicRename)
                {
                    try
                    {
                        if (!rename(Stream.SafeFileHandle, path)) return false;
                        published = true;
                        return true;
                    }
                    catch (Win32Exception error)
                    {
                        if (!allowClassic) throw;
                        renameFailure = error;
                    }
                    // Redirectors can return generic native errors even for a collision. Never
                    // release the writer for a known occupied candidate, and rehash it while owned.
                    using (FileStream occupied = TransferNative.TryOpenRead(path))
                        if (occupied != null) return false;
                    Require(EqualHash(Hash(Stream, length), hash), "Partial hash changed before classic rename.");
                    UsedClassicRename = true; // Set before closing: cleanup authority ends here.
                    Stream.Dispose();
                }
                try
                {
                    // .NET Framework's two-argument File.Move never replaces an existing file.
                    // Directory ancestors stay pinned, and a racing final collision is retried.
                    move(ownedPath, path);
                }
                catch (IOException error)
                {
                    int code = error.HResult & 0xffff;
                    if (code == 80 || code == 183) return false;
                    throw ClassicFailure(error);
                }
                catch (Exception error) { throw ClassicFailure(error); }
                published = true;
                FinalStream = TransferNative.OpenRead(path, false);
                return true;
            }

            private IOException ClassicFailure(Exception error)
            {
                return new IOException("Classic no-overwrite publication failed (HRESULT=0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) +
                    "): " + error.Message + "; handle rename failed with NativeErrorCode=" +
                    renameFailure.NativeErrorCode.ToString(CultureInfo.InvariantCulture) + ": " + renameFailure.Message, error);
            }

            public void Dispose()
            {
                try
                {
                    if (!published && !UsedClassicRename)
                    {
                        // Only our CREATE_NEW handle can be cleaned up. On disconnect or process
                        // termination a partial may remain; never find/delete it later by pathname.
                        try { TransferNative.MarkDelete(Stream.SafeFileHandle); }
                        catch (Exception) { }
                    }
                }
                finally
                {
                    try { if (FinalStream != null) FinalStream.Dispose(); }
                    finally { Stream.Dispose(); }
                    // After classic close, leave any remaining partial alone. Its pathname may
                    // now identify someone else's file, including after failure or a collision.
                }
            }
        }
    }

    internal sealed class TransferIdentity : IDisposable
    {
        internal readonly string Root;
        internal readonly string CardId;
        internal string Serial { get; private set; }
        internal long Capacity { get; private set; }
        private TransferDirectories directories;
        private FileStream marker;

        internal TransferIdentity(string root, string cardId, string expectedSerial, long expectedCapacity)
        {
            Root = TransferPaths.Full(root);
            CardId = cardId;
            TransferEngine.Require(!String.IsNullOrWhiteSpace(cardId), "Card ID is required.");
            TransferEngine.Require(expectedCapacity >= 0, "Expected capacity cannot be negative.");
            try
            {
                directories = new TransferDirectories(Root, false);
                marker = TransferNative.OpenRead(Path.Combine(Root, IdentityVerifier.MarkerFileName), false);
                ReadMarker();
                Serial = TransferNative.VolumeSerial(Root);
                Capacity = new DriveInfo(Path.GetPathRoot(Root)).TotalSize;
                TransferEngine.Require(String.IsNullOrEmpty(expectedSerial) || String.Equals(expectedSerial, Serial, StringComparison.OrdinalIgnoreCase), "Card volume serial mismatch.");
                TransferEngine.Require(expectedCapacity == 0 || expectedCapacity == Capacity, "Card volume capacity mismatch.");
            }
            catch { Dispose(); throw; }
        }

        internal void Recheck()
        {
            ReadMarker();
            TransferEngine.Require(TransferNative.VolumeSerial(Root) == Serial && new DriveInfo(Path.GetPathRoot(Root)).TotalSize == Capacity,
                "Card disconnected, changed or was reformatted.");
        }

        private void ReadMarker()
        {
            TransferEngine.Require(marker.Length > 0 && marker.Length <= 65536, "Card marker has an invalid size.");
            // JsonFile is used while a no-write/no-delete marker handle and its directory
            // ancestors stay open, so the pathname cannot be swapped during deserialization.
            CardMarker value = JsonFile.Read<CardMarker>(Path.Combine(Root, IdentityVerifier.MarkerFileName));
            TransferEngine.Require(value != null && value.Version == 1 && String.Equals(value.CardId, CardId, StringComparison.Ordinal), "Card marker ID or version mismatch.");
        }

        public void Dispose()
        {
            try { if (marker != null) marker.Dispose(); }
            finally { if (directories != null) directories.Dispose(); }
        }
    }

    internal static class TransferPaths
    {
        internal static string Full(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new IOException("An absolute path is required.");
            string normalized = path.Replace('/', '\\');
            bool drive = normalized.Length >= 3 && Char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\';
            bool unc = normalized.StartsWith("\\\\", StringComparison.Ordinal);
            TransferEngine.Require((drive || unc) && !normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) &&
                !normalized.StartsWith("\\\\.\\", StringComparison.Ordinal), "Device and relative paths are not allowed.");
            string components = drive ? normalized.Substring(3) : normalized.Substring(2);
            foreach (string component in components.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                TransferEngine.Require(component != "." && component != ".." && component.TrimEnd(' ', '.') == component &&
                    component.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "Ambiguous path components or alternate streams are not allowed.");
                string stem = component.Split('.')[0].ToUpperInvariant();
                TransferEngine.Require(stem != "CON" && stem != "PRN" && stem != "AUX" && stem != "NUL" &&
                    !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '0' && stem[3] <= '9'), "Device filenames are not allowed.");
            }
            string full = Path.GetFullPath(normalized);
            string volume = Path.GetPathRoot(full);
            return full.Length > volume.Length ? full.TrimEnd('\\') : full;
        }

        internal static string Prefix(string root) { return root.TrimEnd('\\') + "\\"; }
        internal static bool Child(string root, string path) { return path.StartsWith(Prefix(root), StringComparison.OrdinalIgnoreCase); }
        internal static bool Same(string left, string right) { return String.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        internal static bool IsPartial(string path) { return Path.GetFileName(path).IndexOf(".partial", StringComparison.OrdinalIgnoreCase) >= 0; }
        internal static bool IsWebDav(string path)
        {
            if (!path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            string[] components = path.Substring(2).Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return components.Length >= 3 && String.Equals(components[1], "DavWWWRoot", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Pin every ancestor without FILE_SHARE_DELETE. Checking attributes alone leaves a
    // junction/rename race between checking the path and opening the leaf handle.
    internal sealed class TransferDirectories : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();

        internal TransferDirectories(string path, bool create)
        {
            try
            {
                string full = TransferPaths.Full(path);
                string current = Path.GetPathRoot(full);
                handles.Add(TransferNative.OpenDirectory(current));
                string tail = full.Substring(current.Length);
                foreach (string component in tail.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, component);
                    if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                    handles.Add(TransferNative.OpenDirectory(current));
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose() { for (int index = handles.Count - 1; index >= 0; index--) handles[index].Dispose(); }
    }

    internal static class TransferNative
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint BackupSemantics = 0x02000000;

        internal static Win32Exception Failure(int code, string context)
        {
            return new Win32Exception(code, context + " (NativeErrorCode=" + code.ToString(CultureInfo.InvariantCulture) + "): " + new Win32Exception(code).Message);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformation(string root, StringBuilder label, uint labelSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, uint fileSystemSize);

        internal static string VolumeSerial(string root)
        {
            uint serial, maxComponent, flags;
            if (!GetVolumeInformation(TransferPaths.Prefix(Path.GetPathRoot(root)), null, 0, out serial, out maxComponent, out flags, null, 0))
                throw Failure(Marshal.GetLastWin32Error(), "Cannot verify the card volume serial.");
            return serial.ToString("X8", CultureInfo.InvariantCulture);
        }

        internal static FileInformation Information(SafeFileHandle handle, bool directory)
        {
            FileInformation information;
            if (!GetFileInformationByHandle(handle, out information)) throw Failure(Marshal.GetLastWin32Error(), "Cannot inspect the open handle.");
            TransferEngine.Require((information.Attributes & (uint)FileAttributes.ReparsePoint) == 0, "Reparse paths are not allowed.");
            TransferEngine.Require(((information.Attributes & (uint)FileAttributes.Directory) != 0) == directory, "Unexpected file/directory type.");
            return information;
        }

        internal static SafeFileHandle OpenDirectory(string path)
        {
            // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES: attribute-only handles do not
            // participate in Windows sharing checks and therefore cannot pin a pathname.
            SafeFileHandle handle = CreateFile(path, 0x81, (uint)(FileShare.Read | FileShare.Write), IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid) throw Failure(Marshal.GetLastWin32Error(), "Cannot lock directory: " + path);
                Information(handle, true);
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        internal static FileStream OpenRead(string path, bool deleteAccess)
        {
            FileStream stream = OpenFile(path, GenericRead | (deleteAccess ? DeleteAccess : 0), 3, FileAccess.Read, false);
            return stream;
        }

        internal static FileStream TryOpenRead(string path) { return OpenFile(path, GenericRead, 3, FileAccess.Read, true); }
        internal static FileStream CreatePartial(string path) { return OpenFile(path, GenericRead | GenericWrite | DeleteAccess, 1, FileAccess.ReadWrite, false); }
        internal static FileStream CreateFinal(string path) { return OpenFile(path, GenericWrite, 1, FileAccess.Write, false); }

        private static FileStream OpenFile(string path, uint access, uint creation, FileAccess managedAccess, bool missingAllowed)
        {
            SafeFileHandle handle = CreateFile(path, access, (uint)FileShare.Read, IntPtr.Zero, creation, OpenReparsePoint, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (missingAllowed && error == 2) { handle.Dispose(); return null; }
                    throw Failure(error, "Cannot lock file: " + path);
                }
                Information(handle, false);
                return new FileStream(handle, managedAccess, 65536, false);
            }
            catch { handle.Dispose(); throw; }
        }

        internal static bool SameFile(FileStream left, FileStream right)
        {
            FileInformation a = Information(left.SafeFileHandle, false), b = Information(right.SafeFileHandle, false);
            return SameKnownFile(a, b);
        }

        internal static bool SameKnownFile(FileInformation a, FileInformation b)
        {
            // WebDAV and other redirectors may return zero for unavailable identifiers.
            // Unknown IDs prove nothing. Lexical containment and sharing locks still apply;
            // in delete, a destination alias cannot coexist with our DELETE-access source.
            return a.VolumeSerial != 0 && b.VolumeSerial != 0 && (a.IndexHigh != 0 || a.IndexLow != 0) &&
                (b.IndexHigh != 0 || b.IndexLow != 0) && a.VolumeSerial == b.VolumeSerial &&
                a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow;
        }

        internal static void MarkDelete(SafeFileHandle handle)
        {
            IntPtr information = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(information, 1);
                if (!SetFileInformationByHandle(handle, 4, information, 1)) throw Failure(Marshal.GetLastWin32Error(), "Handle deletion failed.");
            }
            finally { Marshal.FreeHGlobal(information); }
        }

        internal static bool RenameWithoutReplace(SafeFileHandle handle, string path)
        {
            byte[] name = Encoding.Unicode.GetBytes(path);
            // FILE_RENAME_INFO contains a BOOLEAN, an aligned HANDLE, a DWORD and WCHAR[].
            int lengthOffset = IntPtr.Size == 8 ? 16 : 8;
            int nameOffset = lengthOffset + 4;
            int size = checked(nameOffset + name.Length + 2);
            IntPtr information = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, information, size); // ReplaceIfExists = FALSE, RootDirectory = NULL.
                Marshal.WriteInt32(information, lengthOffset, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(information, nameOffset), name.Length);
                if (SetFileInformationByHandle(handle, 3, information, (uint)size)) return true;
                int error = Marshal.GetLastWin32Error();
                if (error == 80 || error == 183) return false;
                throw Failure(error, "Atomic destination publication failed.");
            }
            finally { Marshal.FreeHGlobal(information); }
        }
    }
}
