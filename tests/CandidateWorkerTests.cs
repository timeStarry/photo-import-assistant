using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PhotoImportV2
{
    // Exercise the actual child-process dispatch and JSON contracts on owned local fixtures.
    public static class CandidateWorkerTests
    {
        public static string Run()
        {
            string parent = Path.GetFullPath(Path.GetTempPath());
            string root = Path.Combine(parent, "PhotoImport-candidate-worker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string card = Path.Combine(root, "card"), target = Path.Combine(root, "target");
                Directory.CreateDirectory(Path.Combine(card, "DCIM")); Directory.CreateDirectory(target);
                string id = Guid.NewGuid().ToString("D");
                JsonFile.Write(Path.Combine(card, IdentityVerifier.MarkerFileName), new CardMarker { Version = 1, CardId = id });
                string source = Path.Combine(card, "DCIM", "SOURCE.NEF");
                File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });
                string existing = Path.Combine(target, "RENAMED.NEF"); File.Copy(source, existing);
                var info = new FileInfo(source);
                var request = new WorkRequest { Operation = "plan", SourceRoot = card, CardId = id,
                    CandidateFiles = new List<MediaItem> { new MediaItem { SourcePath = source, RelativePath = @"DCIM\SOURCE.NEF", Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks } },
                    ComparisonTargets = new List<DestinationRecord> { ImportSettings.ValidateDestination("fixture", target) }, ExcludedExtensions = MediaRules.DefaultExcluded() };
                WorkResult result = WorkerClient.Run(request, root, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Success && result.Plan != null && result.Plan.ExistingCount == 1 && result.Plan.Candidates.Count == 0,
                    "The plan worker did not return the expected candidate JSON.");
                Assert(result.Receipt == null && !result.Deleted && File.Exists(source) && Directory.GetFiles(target).Length == 1,
                    "Comparison performed a transfer or gained cleanup authority.");
                Assert(File.Exists(request.ProgressPath), "The plan worker did not report progress.");
                CandidateProgress progress = JsonFile.Read<CandidateProgress>(request.ProgressPath);
                Assert(progress.Processed == 1 && progress.Total == 1, "Progress JSON disagrees with the plan.");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool observed = false;
                    try { WorkerClient.Run(request, root, canceled.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { observed = true; }
                    Assert(observed && !WorkerClient.HasUnconfirmedWorker, "Pre-canceled planning started an unconfirmed child.");
                }
                string job = Path.Combine(root, "invalid-progress"); Directory.CreateDirectory(job);
                string input = Path.Combine(job, "request.json"), output = Path.Combine(job, "result.json");
                request.ProgressPath = Path.Combine(root, "unexpected-progress.json"); JsonFile.Write(input, request);
                using (var process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--worker \"" + input + "\" \"" + output + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(3000); throw new InvalidOperationException("Invalid-progress child timed out."); }
                    Assert(process.ExitCode == 1, "Unsafe progress output was accepted.");
                }
                Assert(!File.Exists(request.ProgressPath) && !JsonFile.Read<WorkResult>(output).Success && File.Exists(source), "Invalid progress escaped the job or altered the source.");
                return "PASS: 3 candidate worker integration tests: child JSON/progress and read-only comparison; pre-launch cancellation; progress-path confinement.";
            }
            finally
            {
                string full = Path.GetFullPath(root);
                if (!TransferPaths.Child(parent, full) || !Path.GetFileName(full).StartsWith("PhotoImport-candidate-worker-", StringComparison.Ordinal))
                    throw new IOException("Refusing unsafe fixture cleanup.");
                Directory.Delete(full, true);
            }
        }
        static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
