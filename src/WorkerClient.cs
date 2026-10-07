using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhotoImportV2
{
    // Network I/O lives in a disposable child, never on the tray message loop.
    public static class WorkerClient
    {
        static readonly HashSet<Process> Children = new HashSet<Process>();
        static readonly HashSet<Process> Unconfirmed = new HashSet<Process>();
        public static bool HasUnconfirmedWorker
        {
            get
            {
                lock (Children)
                {
                    foreach (var process in new List<Process>(Unconfirmed))
                    {
                        try { if (process.HasExited) { Unconfirmed.Remove(process); Children.Remove(process); process.Dispose(); } } catch { }
                    }
                    return Unconfirmed.Count > 0;
                }
            }
        }
        static void StopChild(Process process)
        {
            bool stopped = false;
            try { if (!process.HasExited) process.Kill(); stopped = process.WaitForExit(3000); } catch { }
            lock (Children)
            {
                if (stopped) { Children.Remove(process); Unconfirmed.Remove(process); process.Dispose(); }
                else Unconfirmed.Add(process);
            }
            if (!stopped) throw new IOException("无法确认工作进程已退出，已暂停新操作。请暂勿拔卡，等待当前进程结束后重试。");
        }
        static WorkerClient()
        {
            AppDomain.CurrentDomain.ProcessExit += delegate {
                lock (Children) foreach (var process in Children) { try { if (!process.HasExited) process.Kill(); } catch { } }
            };
        }
        public static async Task<List<DiskSnapshot>> Scan(string stateRoot)
        {
            if (HasUnconfirmedWorker) throw new IOException("等待上次工作进程退出，暂不启动新操作。");
            string output = Path.Combine(stateRoot, "scan-" + Guid.NewGuid().ToString("N") + ".json");
            Process process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--scan \"" + output + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                lock (Children) Children.Add(process);
                var watch = Stopwatch.StartNew();
                while (!process.HasExited)
                {
                    if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("存储卡扫描超时；下一轮自动重试。");
                    await Task.Delay(150);
                }
                if (process.ExitCode != 0 || !File.Exists(output)) throw new IOException("扫描进程异常结束。");
                return JsonFile.Read<List<DiskSnapshot>>(output);
            }
            finally
            {
                if (process != null) StopChild(process);
                try { if (File.Exists(output)) File.Delete(output); } catch { }
            }
        }
        public static async Task<WorkResult> Run(WorkRequest request, string stateRoot, CancellationToken cancel, Action<CandidateProgress> progress = null, Action<TransferProgress> transferProgress = null)
        {
            if (HasUnconfirmedWorker) throw new IOException("等待上次工作进程退出，暂不启动新操作。");
            string folder = Path.Combine(stateRoot, "jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string input = Path.Combine(folder, "request.json"), output = Path.Combine(folder, "result.json");
            if (request.Operation == "plan" || request.Operation == "copy" || request.Operation == "delete") request.ProgressPath = Path.Combine(folder, "progress.json");
            JsonFile.Write(input, request);
            int seconds = request.Operation == "register" || request.Operation == "probe" ? 30 : request.IdleTimeoutSeconds > 0 ? Math.Max(30, Math.Min(1800, request.IdleTimeoutSeconds)) : 180;
            Process process = null;
            try
            {
                cancel.ThrowIfCancellationRequested();
                process = Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                    "--worker \"" + input + "\" \"" + output + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                lock (Children) Children.Add(process);
                var watch = Stopwatch.StartNew();
                string lastSignal = null;
                while (!process.HasExited)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("持续 " + seconds + " 秒没有传输进展；已停止本次操作，原件保留。");
                    if (request.ProgressPath != null && File.Exists(request.ProgressPath))
                    {
                        try
                        {
                            string signal;
                            if (request.Operation == "plan")
                            {
                                var value = JsonFile.Read<CandidateProgress>(request.ProgressPath);
                                signal = value.Message + "|" + value.Processed + "|" + value.Bytes;
                                if (progress != null) progress(value);
                            }
                            else
                            {
                                var value = JsonFile.Read<TransferProgress>(request.ProgressPath);
                                signal = value.Stage + "|" + value.Bytes;
                                if (transferProgress != null) transferProgress(value);
                            }
                            if (lastSignal != signal) { lastSignal = signal; watch.Restart(); }
                        }
                        catch (IOException) { }
                    }
                    await Task.Delay(150, cancel);
                }
                cancel.ThrowIfCancellationRequested();
                if (!File.Exists(output)) return new WorkResult { Error = "工作进程没有返回结果；若操作为清理，请核对日志与卡内原件。" };
                return JsonFile.Read<WorkResult>(output);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new WorkResult { Error = ex.Message }; }
            finally
            {
                if (process != null) StopChild(process);
            }
        }
    }
}
