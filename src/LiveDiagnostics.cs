using System;
using System.IO;
using System.Text;

namespace PhotoImportV2
{
    public static class LiveDiagnostics
    {
        // Only a new synthetic file, never an SD card or existing media.
        public static string RemoteProbe(string remoteFolder)
        {
            var configured = ImportSettings.ValidateDestination("Compatibility probe", remoteFolder);
            if (ImportSettings.Kind(configured) != "remote") throw new IOException("Specify a WebDAV URL or UNC network folder for the probe.");
            string token = Guid.NewGuid().ToString("N");
            string sourceRoot = Path.Combine(Path.GetTempPath(), "PhotoImport-Probe-" + token);
            string destinationRoot = Path.Combine(configured.Path, ".photoimport-probe-" + token);
            if (Directory.Exists(sourceRoot) || Directory.Exists(destinationRoot)) throw new IOException("Probe path already exists.");
            string sourceDir = Path.Combine(sourceRoot, "DCIM");
            string source = Path.Combine(sourceDir, "SYNTHETIC-TEST.txt");
            Directory.CreateDirectory(sourceDir);
            string id = Guid.NewGuid().ToString("D").ToUpperInvariant();
            JsonFile.Write(Path.Combine(sourceRoot, IdentityVerifier.MarkerFileName), new CardMarker { Version = 1, CardId = id, CreatedUtc = DateTime.UtcNow.ToString("o") });
            File.WriteAllText(source, "PhotoImport v2 synthetic compatibility probe. No personal media. " + token, Encoding.UTF8);
            try
            {
                var info = new FileInfo(source);
                var request = new WorkRequest { Operation = "copy", SourceRoot = sourceRoot, CardId = id, ExpectedCapacity = 0,
                    DestinationRoot = destinationRoot, DestinationKind = "remote",
                    File = new MediaItem { SourcePath = source, RelativePath = "DCIM\\SYNTHETIC-TEST.txt", Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks } };
                var result = TransferEngine.Run(request);
                if (!result.Success) return "FAIL remote copy: " + result.Error + "\r\nProbe directory (may contain partial): " + destinationRoot;
                bool writeBlocked = false;
                using (var locked = TransferNative.OpenRead(result.Receipt.DestinationPath, false))
                {
                    try { using (var writer = new FileStream(result.Receipt.DestinationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } }
                    catch (IOException ex) { writeBlocked = (ex.HResult & 0xffff) == 32; }
                }
                if (!writeBlocked) return "FAIL: remote read handle did not block a concurrent local writer. Probe: " + destinationRoot;
                var deletion = TransferEngine.Run(new WorkRequest { Operation = "delete", SourceRoot = sourceRoot, CardId = id, Receipt = result.Receipt });
                if (!deletion.Success || !deletion.Deleted || File.Exists(source)) return "FAIL: verified synthetic source cleanup: " + deletion.Error + " Probe: " + destinationRoot;
                // These exact paths were created by this probe. Do not recursively delete anything.
                File.Delete(result.Receipt.DestinationPath);
                string cleanup = "";
                try { Directory.Delete(Path.GetDirectoryName(result.Receipt.DestinationPath), false); Directory.Delete(destinationRoot, false); }
                catch (Exception ex) { cleanup = " Empty-directory cleanup unconfirmed: " + ex.Message + " Path: " + destinationRoot; }
                return "PASS: real WebDAV synthetic copy, close/reopen SHA-256 final readback, read-handle denies second local writer, and reverified deletion of the local synthetic source. Synthetic remote file removed." + cleanup;
            }
            finally
            {
                if (File.Exists(source)) File.Delete(source);
                File.Delete(Path.Combine(sourceRoot, IdentityVerifier.MarkerFileName));
                Directory.Delete(sourceDir, false);
                Directory.Delete(sourceRoot, false);
            }
        }
    }
}
