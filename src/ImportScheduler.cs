using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoImportV2
{
    public static class ImportScheduler
    {
        sealed class Running { internal MediaItem File; internal Task<bool> Task; }
        // One large-file lane. Small files get three turns, then a waiting video
        // gets its turn. Parallelism is bounded even during failure/fallback.
        public static async Task Run(IList<MediaItem> files, int maximum, long largeBytes, bool automatic,
            Func<MediaItem, Task<bool>> copy, Action<int, int, int> state, CancellationToken cancel, Action cancelRemaining = null, bool stopOnFailure = false)
        {
            if (maximum < 1 || maximum > 4 || largeBytes < 1) throw new ArgumentException("Invalid scheduler limits.");
            var small = new Queue<MediaItem>(files.Where(f => f.Length < largeBytes));
            var large = new Queue<MediaItem>(files.Where(f => f.Length >= largeBytes));
            var active = new List<Running>();
            int completed = 0, smallTurns = 0, limit = automatic ? 1 : maximum, sampled = 0;
            long windowBytes = 0; double baseline = 0; bool probing = true;
            var window = Stopwatch.StartNew();
            Exception failure = null;
            try
            {
                while (small.Count + large.Count + active.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (automatic && maximum >= 2 && small.Count > 0 && active.Any(a => a.File.Length >= largeBytes)) limit = Math.Max(2, limit);
                    while (active.Count < limit && small.Count + large.Count > 0)
                    {
                        MediaItem file;
                        bool largeRunning = active.Any(a => a.File.Length >= largeBytes);
                        if (large.Count > 0 && !largeRunning && (smallTurns >= 3 || small.Count == 0))
                        {
                            file = large.Dequeue(); smallTurns = 0;
                            if (automatic && maximum >= 2 && small.Count > 0) limit = Math.Max(2, limit);
                        }
                        else if (small.Count > 0) { file = small.Dequeue(); smallTurns++; }
                        else break;
                        active.Add(new Running { File = file, Task = copy(file) });
                    }
                    if (state != null) state(completed, active.Count, small.Count + large.Count);
                    if (active.Count == 0) throw new InvalidOperationException("Scheduler cannot make progress.");
                    var done = await Task.WhenAny(active.Select(a => a.Task));
                    var entry = active.First(a => a.Task == done);
                    bool success = await done; active.Remove(entry); completed++;
                    if (!success && stopOnFailure) throw new InvalidOperationException("所有已批准位置均未完成导入；已停止排队任务，原件保留。");
                    if (!success) { limit = 1; probing = false; }
                    windowBytes += Math.Max(0, entry.File.Length); sampled++;
                    // Compare completed-byte throughput, not nominal NIC bandwidth. A
                    // failed request reduces pressure. Do not increase beyond user cap.
                    if (automatic && sampled >= 3 && window.Elapsed.TotalSeconds >= 2)
                    {
                        double rate = windowBytes / window.Elapsed.TotalSeconds;
                        if (baseline > 0 && rate < baseline * .85) { limit = Math.Max(1, limit - 1); probing = false; }
                        else if (probing && limit < maximum) limit++;
                        baseline = rate; sampled = 0; windowBytes = 0; window.Restart();
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                if (cancelRemaining != null) cancelRemaining();
            }
            // Observe every child before the controller clears busy or permits a
            // new batch. External cancellation propagates to all active workers.
            foreach (var running in active)
                try { await running.Task; } catch (Exception ex) { if (failure == null) failure = ex; }
            if (failure != null) throw failure;
            if (state != null) state(completed, 0, 0);
        }
    }
}
