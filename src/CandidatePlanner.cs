using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PhotoImportV2
{
    // Read-only planning. A match suppresses a candidate but never earns a cleanup receipt.
    public static class CandidatePlanner
    {
        sealed class Target
        {
            internal string Path;
            internal long Length, WriteTicks;
            internal string Hash;
            internal IRemoteStore Remote;
        }

        public static string Signature(DiskSnapshot disk, AppState state)
        {
            var text = new StringBuilder();
            Action<string> add = delegate(string value) { value = value ?? ""; text.Append(value.Length).Append(':').Append(value); };
            add(disk.Key); add(disk.MarkerId);
            foreach (var file in disk.Media.OrderBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase))
            { add(file.SourcePath); add(file.RelativePath); add(file.Length.ToString()); add(file.WriteTicks.ToString()); }
            foreach (var target in ImportSettings.Ordered(state)) { add(target.Id); add(StorageSettings.Key(target)); }
            foreach (string extension in MediaRules.NormalizeExcluded(state.ExcludedExtensions)) add(extension);
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        public static WorkResult Run(WorkRequest request, Action<CandidateProgress> progress = null)
        {
            try
            {
                if (request == null || request.CandidateFiles == null || request.ComparisonTargets == null)
                    throw new ArgumentException("A source list and comparison targets are required.");
                string root = TransferPaths.Full(request.SourceRoot);
                var files = request.CandidateFiles.Select(f => new MediaItem { SourcePath = f.SourcePath,
                    RelativePath = f.RelativePath, Length = f.Length, WriteTicks = f.WriteTicks }).ToList();
                var excluded = MediaRules.NormalizeExcluded(request.ExcludedExtensions);
                var targets = request.ComparisonTargets.Where(d => d.Enabled).Select(ImportSettings.Clone).ToList();
                var plan = new CandidatePlan();
                var eligible = files.Where(f => MediaRules.Includes(f.RelativePath ?? f.SourcePath, excluded)).ToList();
                plan.ExcludedCount = files.Count - eligible.Count;
                var lengths = new HashSet<long>(eligible.Select(f => f.Length));
                var inventory = new Dictionary<long, List<Target>>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var identity = new TransferIdentity(root, request.CardId, request.ExpectedSerial, request.ExpectedCapacity))
                {
                    foreach (var destination in targets)
                    {
                        if (StorageSettings.IsDirect(destination))
                        {
                            Report(progress, "读取目标目录 · " + destination.Name, 0, eligible.Count);
                            try
                            {
                                var remote = RemoteStores.Create(destination, request.IdleTimeoutSeconds);
                                foreach (var item in remote.List())
                                {
                                    if (!lengths.Contains(item.Length) || !MediaRules.IsSupported(item.Path) || TransferPaths.IsPartial(item.Path)) continue;
                                    StorageSettings.Relative(item.Path, false);
                                    if (!seen.Add(StorageSettings.Key(destination) + "|" + item.Path)) continue;
                                    if (seen.Count > 100000) throw new IOException("Target inventory exceeds 100,000 comparable files.");
                                    List<Target> list;
                                    if (!inventory.TryGetValue(item.Length, out list)) inventory[item.Length] = list = new List<Target>();
                                    list.Add(new Target { Path = item.Path, Length = item.Length, Remote = remote });
                                }
                            }
                            catch (Exception error) { plan.Warnings.Add(destination.Name + "：目标对比不完整（" + error.Message + "）"); }
                            continue;
                        }
                        string folder = StorageSettings.Normalize(destination).Path;
                        // Do not index the source card, or a folder enclosing it.
                        if (TransferPaths.Same(root, folder) || TransferPaths.Child(root, folder) || TransferPaths.Child(folder, root))
                            throw new IOException("Comparison target overlaps the source card.");
                        Report(progress, "读取目标目录 · " + destination.Name, 0, eligible.Count);
                        try { Enumerate(folder, lengths, inventory, seen, 0); }
                        catch (Exception error) { plan.Warnings.Add(destination.Name + "：目标对比不完整（" + error.Message + "）"); }
                    }
                    int processed = 0;
                    foreach (var file in eligible)
                    {
                        Report(progress, "对比内容 · " + file.RelativePath, processed, eligible.Count);
                        string source = TransferPaths.Full(file.SourcePath);
                        TransferEngine.Require(TransferPaths.Child(Path.Combine(root, "DCIM"), source) && !TransferPaths.IsPartial(source), "Unsafe comparison source.");
                        string relative = source.Substring(TransferPaths.Prefix(root).Length);
                        TransferEngine.Require(String.Equals(relative, file.RelativePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase), "Comparison relative path mismatch.");
                        bool match = false;
                        using (var directories = new TransferDirectories(Path.GetDirectoryName(source), false))
                        using (var input = TransferNative.OpenRead(source, false))
                        {
                            TransferEngine.CheckSnapshot(input, file.Length, file.WriteTicks);
                            List<Target> candidates;
                            if (inventory.TryGetValue(file.Length, out candidates) && candidates.Count > 0)
                            {
                                input.Position = 0;
                                string sourceHash = CompareHash(input, file.Length, file.RelativePath, processed, eligible.Count, progress);
                                // Prefer the usual filename; renamed/hash-suffixed copies are also indexed.
                                foreach (var target in candidates.OrderBy(t => Path.GetFileName(t.Path).Equals(Path.GetFileName(source), StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                                {
                                    try
                                    {
                                        // A cached non-match can only cause a conservative extra candidate.
                                        // Every positive match is freshly reopened and rehashed, even in this run.
                                        if (target.Hash != null && target.Hash != sourceHash) continue;
                                        if (target.Remote != null)
                                        {
                                            var before = target.Remote.Stat(target.Path);
                                            if (before == null || before.Length != file.Length) throw new IOException("Target changed during comparison.");
                                            using (var remoteInput = target.Remote.OpenRead(target.Path)) target.Hash = CompareHash(remoteInput, file.Length, file.RelativePath, processed, eligible.Count, progress);
                                            var after = target.Remote.Stat(target.Path);
                                            if (after == null || after.Length != before.Length || (before.ETag != null && before.ETag != after.ETag)) throw new IOException("Target changed during comparison.");
                                            if (target.Hash == sourceHash) { match = true; break; }
                                            continue;
                                        }
                                        using (var targetDirectories = new TransferDirectories(Path.GetDirectoryName(target.Path), false))
                                        using (var existing = TransferNative.OpenRead(target.Path, false))
                                        {
                                            TransferEngine.Require(!TransferNative.SameFile(input, existing), "Comparison target aliases the source.");
                                            TransferEngine.CheckSnapshot(existing, target.Length, target.WriteTicks);
                                            existing.Position = 0;
                                            target.Hash = CompareHash(existing, target.Length, file.RelativePath, processed, eligible.Count, progress);
                                            TransferEngine.CheckSnapshot(existing, target.Length, target.WriteTicks);
                                            if (target.Hash == sourceHash) { match = true; break; }
                                        }
                                    }
                                    catch (Exception) { unreadable.Add(target.Path); /* Unknown is never a duplicate. */ }
                                }
                            }
                            TransferEngine.CheckSnapshot(input, file.Length, file.WriteTicks);
                            identity.Recheck();
                        }
                        if (match) plan.ExistingCount++; else plan.Candidates.Add(file);
                        Report(progress, "对比内容 · " + file.RelativePath, ++processed, eligible.Count);
                    }
                    identity.Recheck();
                }
                if (unreadable.Count > 0) plan.Warnings.Add(unreadable.Count + " 个目标文件未能完成校验；未确认文件保留为候选。");
                return new WorkResult { Success = true, Plan = plan };
            }
            catch (Exception error) { return new WorkResult { Error = error.GetType().Name + ": " + error.Message }; }
        }

        static void Report(Action<CandidateProgress> callback, string message, int processed, int total)
        { if (callback != null) callback(new CandidateProgress { Message = message, Processed = processed, Total = total }); }
        static string CompareHash(Stream input, long length, string path, int processed, int total, Action<CandidateProgress> progress)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            return RemoteTransferEngine.Hash(input, length, "对比内容", delegate(TransferProgress p) {
                if (progress != null && (p.Bytes == 0 || p.Bytes == p.Total || watch.ElapsedMilliseconds >= 500))
                { progress(new CandidateProgress { Message = "对比内容 · " + path, Processed = processed, Total = total, Bytes = p.Bytes, TotalBytes = p.Total }); watch.Restart(); }
            });
        }

        static void Enumerate(string directory, HashSet<long> lengths, Dictionary<long, List<Target>> inventory,
            HashSet<string> seen, int depth)
        {
            if (depth > 16) throw new IOException("Target directory nesting exceeds the comparison limit.");
            if (lengths.Count == 0) return;
            using (var directories = new TransferDirectories(directory, false))
            {
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || TransferPaths.IsPartial(path) || !MediaRules.IsSupported(path) || !lengths.Contains(info.Length)) continue;
                    string full = TransferPaths.Full(path);
                    if (!seen.Add(full)) continue;
                    if (seen.Count > 100000) throw new IOException("Target inventory exceeds 100,000 comparable files.");
                    List<Target> list;
                    if (!inventory.TryGetValue(info.Length, out list)) inventory[info.Length] = list = new List<Target>();
                    list.Add(new Target { Path = full, Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks });
                }
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    Enumerate(child, lengths, inventory, seen, depth + 1);
                }
            }
        }
    }
}
