using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PhotoImportV2
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--export-icon")
            {
                using (var icon = AppIcon.Create()) using (var stream = new FileStream(args[1], FileMode.Create)) icon.Save(stream);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--render-previews")
            {
                Directory.CreateDirectory(args[1]);
                try { File.WriteAllText(Path.Combine(args[1], "render-check.txt"), FluentPreview.RenderAll(args[1])); return 0; }
                catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "render-check.txt"), "FAIL " + ex); return 1; }
            }
            if (args.Length == 3 && args[0] == "--remote-probe")
            {
                try { string result = LiveDiagnostics.RemoteProbe(args[1]); File.WriteAllText(args[2], result); return result.StartsWith("PASS:") ? 0 : 1; }
                catch (Exception ex) { File.WriteAllText(args[2], "FAIL " + ex); return 1; }
            }
            if (args.Length > 0 && args[0] == "--remote-probe") return 2;
            if (args.Length == 2 && args[0] == "--scan")
            {
                try { JsonFile.Write(args[1], Detector.Scan()); return 0; }
                catch { return 1; }
            }
            if (args.Length == 3 && args[0] == "--worker")
            {
                try { var request = JsonFile.Read<WorkRequest>(args[1]); JsonFile.Write(args[2], request.Operation == "register" ? RegistrationWorker.Run(request) : request.Operation == "probe" ? DestinationProbe.Run(request) : TransferEngine.Run(request)); return 0; }
                catch (Exception ex) { try { JsonFile.Write(args[2], new WorkResult { Error = ex.ToString() }); } catch { } return 1; }
            }
            if (args.Length > 0 && args[0] == "--self-test")
            {
                try
                {
                    string result = TransferEngineTests.Run() + Environment.NewLine + ConfigurationTests.Run();
                    if (args.Length > 1) File.WriteAllText(args[1], result);
                    return 0;
                }
                catch (Exception ex) { if (args.Length > 1) File.WriteAllText(args[1], "FAIL " + ex); return 1; }
            }
            bool preview = Array.IndexOf(args, "--preview") >= 0;
            string suffix = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value + (preview ? "-preview" : "");
            if (!preview && Array.IndexOf(args, "--startup") < 0)
            {
                // Shortcuts never create an unsupervised owner of the singleton mutex.
                try { using (var existing = EventWaitHandle.OpenExisting(@"Local\PhotoImportV2-Show-" + suffix)) existing.Set(); return 0; }
                catch (WaitHandleCannotBeOpenedException) { }
                try
                {
                    using (var task = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/Run /TN PhotoImport-Agent")
                        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    {
                        if (!task.WaitForExit(5000) || task.ExitCode != 0) throw new IOException("无法启动登录任务，请检查 PhotoImport-Agent 是否已安装。");
                    }
                    return 0;
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "相机导入助手", MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
            }
            bool created;
            using (var mutex = new Mutex(true, @"Local\PhotoImportV2-" + suffix, out created))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PhotoImportV2-Show-" + suffix))
            {
                if (!created) { show.Set(); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                StateStore store = null;
                try
                {
                    store = new StateStore(preview);
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                    {
                        e.Handled = true; store.Log("WPF_UI_ERROR " + e.Exception);
                        MessageBox.Show("界面操作未完成，监听仍在运行。\n" + e.Exception.Message, "相机导入助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    };
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                    {
                        store.Log("UI_ERROR " + e.Exception);
                        MessageBox.Show("本次操作发生错误，可在主窗口重试。\n" + e.Exception.Message, "相机导入助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    };
                    Application.Run(new MainWindow(store, show, preview, Array.IndexOf(args, "--startup") >= 0));
                    return 0;
                }
                catch (Exception ex)
                {
                    if (store != null) store.Log("FATAL " + ex);
                    MessageBox.Show(ex.Message, "相机导入助手启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
    }
}
