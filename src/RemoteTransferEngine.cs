using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace PhotoImportV2
{
    public static class RemoteTransferEngine
    {
        public static WorkResult Run(WorkRequest request, Action<TransferProgress> progress)
        {
            try { return Copy(request, RemoteStores.Create(request.Target, request.IdleTimeoutSeconds), progress); }
            catch (Exception ex) { return new WorkResult { Error = "直接上传未完成校验；原件保留。 " + ex.GetType().Name + ": " + ex.Message }; }
        }
        internal static WorkResult Copy(WorkRequest request, IRemoteStore store, Action<TransferProgress> progress)
        {
            var file = request.File;
            if (file == null) throw new ArgumentException("Missing source snapshot.");
            string root = TransferPaths.Full(request.SourceRoot), source = TransferPaths.Full(file.SourcePath);
            TransferEngine.Require(TransferPaths.Child(Path.Combine(root, "DCIM"), source) && !TransferPaths.IsPartial(source), "Unsafe source.");
            string relative = source.Substring(TransferPaths.Prefix(root).Length).Replace('\\', '/');
            TransferEngine.Require(String.Equals(relative, file.RelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), "Source relative path changed.");
            StorageSettings.Relative(relative, false);
            using (var identity = new TransferIdentity(root, request.CardId, request.ExpectedSerial, request.ExpectedCapacity))
            using (var directories = new TransferDirectories(Path.GetDirectoryName(source), false))
            using (var input = TransferNative.OpenRead(source, false))
            {
                TransferEngine.CheckSnapshot(input, file.Length, file.WriteTicks);
                string sourceHash = null; int raceCount = 0;
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    string path = attempt == 0 ? relative : CollisionName(relative, sourceHash, attempt);
                    var existing = store.Stat(path);
                    if (existing != null)
                    {
                        if (sourceHash == null) { input.Position = 0; sourceHash = Hash(input, file.Length, "读取", progress); }
                        if (existing.Length == file.Length)
                        {
                            using (var target = store.OpenRead(path))
                                if (sourceHash == Hash(target, file.Length, "校验", progress)) return Receipt(request, identity, input, path, sourceHash, true);
                        }
                        continue;
                    }
                    input.Position = 0;
                    bool uploaded;
                    using (var hashed = new HashingInput(input, file.Length))
                    {
                        Report(progress, "上传", 0, file.Length);
                        uploaded = store.UploadNew(path, hashed, file.Length, delegate(long bytes) { Report(progress, "上传", bytes, file.Length); });
                        // A collision may return without consuming the source. Hash only when needed.
                        if (hashed.Bytes == file.Length) sourceHash = hashed.Finish();
                    }
                    if (!uploaded)
                    {
                        if (sourceHash == null) { input.Position = 0; sourceHash = Hash(input, file.Length, "读取", progress); }
                        attempt--; // Next pass verifies the name occupied during CREATE_NEW.
                        if (++raceCount > 4) throw new IOException("目标竞争过多，请稍后重试。");
                        continue;
                    }
                    TransferEngine.Require(sourceHash != null, "Backend did not consume the complete source.");
                    TransferEngine.CheckSnapshot(input, file.Length, file.WriteTicks); identity.Recheck();
                    using (var verified = store.OpenRead(path))
                        TransferEngine.Require(Hash(verified, file.Length, "校验", progress) == sourceHash, "目标内容校验失败。");
                    return Receipt(request, identity, input, path, sourceHash, false);
                }
                throw new IOException("同名文件竞争超过上限，没有覆盖已有内容。");
            }
        }
        static WorkResult Receipt(WorkRequest work, TransferIdentity identity, FileStream input, string relative, string hash, bool reused)
        {
            identity.Recheck(); TransferEngine.CheckSnapshot(input, work.File.Length, work.File.WriteTicks);
            return new WorkResult { Success = true, ReusedExisting = reused, Receipt = new TransferReceipt {
                SourcePath = work.File.SourcePath, SourceRoot = identity.Root, CardId = identity.CardId, Serial = identity.Serial,
                Capacity = identity.Capacity, Length = work.File.Length, WriteTicks = work.File.WriteTicks,
                DestinationPath = ImportSettings.Address(work.Target).TrimEnd('/') + "/" + relative,
                // Generic HTTP/object copies cannot hold the remote file locked during SD deletion.
                // They NEVER authorize the legacy handle-based cleanup engine.
                DestinationKind = StorageSettings.TypeOf(work.Target), Sha256 = hash, VerifiedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } };
        }
        static string CollisionName(string path, string hash, int attempt)
        { int dot = path.LastIndexOf('.'); return path.Substring(0, dot) + "." + hash + (attempt == 1 ? "" : "." + attempt) + path.Substring(dot); }
        public static string Hash(Stream input, long length, string stage, Action<TransferProgress> progress)
        {
            using (var hashed = new HashingInput(input, length))
            {
                byte[] buffer = new byte[1024 * 1024]; Report(progress, stage, 0, length);
                while (hashed.Read(buffer, 0, buffer.Length) > 0) Report(progress, stage, hashed.Bytes, length);
                return hashed.Finish();
            }
        }
        public static void Report(Action<TransferProgress> progress, string stage, long bytes, long total)
        { if (progress != null) progress(new TransferProgress { Stage = stage, Bytes = bytes, Total = total, UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }); }
        sealed class HashingInput : Stream
        {
            readonly Stream input; readonly SHA256 sha = SHA256.Create(); readonly long length;
            internal long Bytes; bool finished;
            internal HashingInput(Stream source, long size) { input = source; length = size; }
            internal string Finish()
            {
                TransferEngine.Require(Bytes == length, "流长度与文件快照不一致。");
                if (!finished) { sha.TransformFinalBlock(new byte[0], 0, 0); finished = true; }
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = input.Read(buffer, offset, count);
                if (n == 0) { TransferEngine.Require(Bytes == length, "流提前结束。"); return 0; }
                TransferEngine.Require(Bytes + n <= length && !finished, "流超出文件快照。");
                sha.TransformBlock(buffer, offset, n, buffer, offset); Bytes += n; return n;
            }
            protected override void Dispose(bool disposing) { if (disposing) sha.Dispose(); base.Dispose(disposing); }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return length; } }
            public override long Position { get { return Bytes; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long l) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }
    }
}
