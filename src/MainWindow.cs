using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhotoImportV2
{
    public sealed class MainWindow : Form
    {
        readonly StateStore store;
        readonly bool preview;
        readonly EventWaitHandle showEvent;
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer timer;
        readonly FluentView view;
        readonly UiText status = new UiText(), log = new UiText();
        readonly UiProgress progress = new UiProgress();
        string selectedRoot;
        readonly HashSet<string> handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> errorShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<DiskSnapshot> disks = new List<DiskSnapshot>();
        bool scanning, busy, paused, exiting, prompting;
        bool fallbackActive;
        bool startupBusy;
        string activeDestination = "";
        CancellationTokenSource cancellation;
        Task activeImport;
        Task activeScan;
        readonly List<Task> backgroundProbes = new List<Task>();
        string activeRoot;
        string lastOutcome = "本次会话尚无导入任务";
        DateTime lastScan = DateTime.MinValue;
        int identityGeneration;
        sealed class ComparisonEntry
        {
            internal string Signature, Error;
            internal CandidatePlan Plan;
        }
        readonly Dictionary<string, ComparisonEntry> comparisons = new Dictionary<string, ComparisonEntry>(StringComparer.OrdinalIgnoreCase);

        public MainWindow(StateStore stateStore, EventWaitHandle show, bool isPreview, bool startup)
        {
            store = stateStore; showEvent = show; preview = isPreview;
            Text = "相机导入助手" + (preview ? " · 界面预览（不读写存储卡）" : "");
            Font = new Font("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1020, 740); MinimumSize = new Size(880, 620);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(246, 248, 251);
            Icon = AppIcon.Create();
            view = new FluentView();
            Controls.Add(new System.Windows.Forms.Integration.ElementHost { Dock = DockStyle.Fill, Child = view });
            view.SelectCard = delegate(string root) { selectedRoot = root; UpdateDetails(); };
            view.Rescan = async delegate { if (!busy) comparisons.Clear(); lastScan = DateTime.MinValue; await ScanAsync(); };
            view.Register = async delegate { if (preview) PreviewRegistration(); else await RegisterSelected(); };
            view.Import = async delegate { if (preview) PreviewImport(); else await StartSelected(); };
            view.Preferences = delegate { EditPreferences(); };
            view.CardAction = delegate(string action, string id) { var card = store.State.Cards.FirstOrDefault(c => c.Id == id); if (card != null) EditCard(card); };
            view.AddLocation = delegate { EditLocation(null); };
            view.LocationAction = async delegate(string action, string id) { await LocationAction(action, id); };
            view.FailureChanged = delegate(string policy) { SaveLocations(delegate { store.State.DestinationFailure = policy; }); };
            view.NewCardPromptChanged = delegate(bool enabled) {
                bool? old = store.State.PromptForNewCards; store.State.PromptForNewCards = enabled;
                try { store.Save(); } catch (Exception ex) { store.State.PromptForNewCards = old; ShowSettingsError(ex); } UpdateDetails();
            };
            view.StartupChanged = async delegate(bool enabled) { await ChangeStartup(enabled); };
            view.EditExclusions = delegate { EditExclusions(); };
            view.EditTransferSettings = delegate { EditTransferSettings(); };
            view.Cancel = delegate { if (cancellation != null) cancellation.Cancel(); };
            view.Pause = delegate { TogglePause(); };
            view.OpenLogs = delegate { if (!preview) System.Diagnostics.Process.Start("explorer.exe", store.Root); };
            view.ThemeChanged = delegate(string theme) { store.State.Theme = theme; store.Save(); UpdateDetails(); };
            view.ChromeChanged = delegate { if (IsHandleCreated) FluentTheme.StyleChrome(Handle); };
            status.Changed = UpdateDetails; log.Changed = UpdateDetails; progress.Changed = UpdateDetails;
            HandleCreated += delegate { FluentTheme.StyleChrome(Handle); };
            FormClosed += delegate { view.Dispose(); Icon.Dispose(); };
            if (!preview)
            {
                try
                {
                    if (File.Exists(store.LogPath)) log.Text = String.Join(Environment.NewLine, File.ReadLines(store.LogPath).Reverse().Take(100).Reverse().Select(HistoryLine)) + Environment.NewLine;
                }
                catch { }
            }
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开相机导入助手", null, delegate { Reveal(); });
            menu.Items.Add("重新扫描存储卡", null, async delegate { Reveal(); if (!busy) comparisons.Clear(); await ScanAsync(); });
            menu.Items.Add("暂停 / 恢复自动提示", null, delegate { TogglePause(); });
            menu.Items.Add("打开日志目录", null, delegate { if (!preview) System.Diagnostics.Process.Start("explorer.exe", store.Root); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出助手", null, async delegate { await ExitAsync(); });
            tray = new NotifyIcon { Icon = Icon, Text = "相机导入助手 · 正在监听", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { Reveal(); }; tray.BalloonTipClicked += delegate { Reveal(); };
            timer = new System.Windows.Forms.Timer { Interval = 800 };
            timer.Tick += async delegate {
                if (showEvent.WaitOne(0)) Reveal();
                if ((DateTime.UtcNow - lastScan).TotalSeconds >= 4) await ScanAsync();
            };
            Shown += async delegate {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                if (Width > work.Width - 32 || Height > work.Height - 32)
                {
                    MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width - 32), Math.Min(MinimumSize.Height, work.Height - 32));
                    Size = new Size(Math.Min(Width, work.Width - 32), Math.Min(Height, work.Height - 32)); CenterToScreen();
                }
                WriteLog(preview ? "预览模式：不访问真实卡片，不执行传输。" : "助手已启动；设备事件 + 每 4 秒核对。");
                if (store.MigratedLegacy) WriteLog("旧版登记已保留，需重新关联卡内 UUID；旧版删除偏好重置为每次询问。");
                if (preview) { store.State.StartAtLogin = true; SeedPreview(); }
                else { if (startup) Hide(); await ReadStartup(); await ScanAsync(); }
                timer.Start(); UpdateDetails();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); Balloon("仍在后台监听", "双击托盘图标可打开状态窗口；退出请使用托盘菜单。"); }
                else { if (cancellation != null) cancellation.Cancel(); timer.Stop(); tray.Visible = false; tray.Dispose(); }
            };
        }
        public static string Capacity(long bytes) { return (bytes / 1000000000d).ToString("0.0") + " GB"; }
        static string HistoryLine(string line)
        {
            int separator = line.IndexOf(' '); DateTimeOffset time;
            return separator > 0 && DateTimeOffset.TryParse(line.Substring(0, separator), out time) ? time.ToLocalTime().ToString("HH:mm:ss") + "  " + line.Substring(separator + 1) : line;
        }
        static string Token(DiskSnapshot disk) { return disk.Key + "|" + disk.MarkerId; }
        bool Duplicate(DiskSnapshot disk) { return !String.IsNullOrEmpty(disk.MarkerId) && disks.Count(d => String.Equals(d.MarkerId, disk.MarkerId, StringComparison.OrdinalIgnoreCase)) > 1; }
        bool PresentInLastScan(DiskSnapshot disk, string id)
        {
            return disks.Any(d => d.Root == disk.Root && d.Serial == disk.Serial && d.Capacity == disk.Capacity && String.Equals(d.MarkerId, id, StringComparison.OrdinalIgnoreCase));
        }
        DiskSnapshot Selected { get { return disks.FirstOrDefault(d => d.Root == selectedRoot) ?? disks.FirstOrDefault(); } }
        string DiskState(DiskSnapshot disk)
        {
            if (disk.Error != null) return disk.Error;
            if (disk.MarkerError != null) return disk.MarkerError;
            if (Duplicate(disk)) return "UUID 重复：停止导入，请重新登记其中一张";
            if (store.Find(disk.MarkerId) == null) return String.IsNullOrEmpty(disk.MarkerId) ? "未登记 / 已格式化 → 点击登记或关联" : "卡内 UUID 未在本机登记 → 点击关联";
            return disk.Media.Count == 0 ? "已识别，DCIM 中没有支持的媒体" : "已识别，可以导入";
        }
        void RenderCards()
        {
            selectedRoot = Selected == null ? null : Selected.Root;
            UpdateDetails();
        }
        void UpdateDetails()
        {
            if (view != null && !IsDisposed) view.Render(disks, selectedRoot, store.State, busy, paused, status.Text, progress.Value, log.Text, lastOutcome, fallbackActive, activeDestination, startupBusy);
        }
        void SeedPreview()
        {
            disks = new List<DiskSnapshot> { new DiskSnapshot { Root = "F:\\", Label = "NIKON Z 30", Serial = "A1B2C3D4", Capacity = 63248007168, FreeSpace = 25248007168, FileSystem = "exFAT" } };
            for (int i = 0; i < 767; i++) disks[0].Media.Add(new MediaItem { RelativePath = i < 12 ? "DCIM\\CLIP.MP4" : "DCIM\\PHOTO.NEF", Length = i < 12 ? 700000000 : 38000000 });
            RenderCards(); status.Text = "监听中 · 发现 1 张卡片，等待登记";
        }
        void PreviewRegistration()
        {
            var disk = Selected; if (disk == null) return;
            using (var dialog = new RegistrationDialog(store, disk))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var card = new CardRecord { Id = Guid.NewGuid().ToString("D"), Name = dialog.CardName, Capacity = disk.Capacity, DeleteMode = "Ask" };
                store.State.Cards.Add(card); disk.MarkerId = card.Id;
                disk.Plan = new CandidatePlan { Candidates = disk.Media.Where(f => MediaRules.Includes(f.RelativePath, store.State.ExcludedExtensions)).ToList() }; UpdateDetails();
            }
        }
        void PreviewImport()
        {
            using (var first = new ChoiceDialog("是否导入这张卡？", "尼康 Z30 · F: · 63.2 GB\r\n共有 767 个媒体文件，将保存到照片存储 / Z30。\r\n当前为界面预览，不会读取或传输真实媒体。", "开始导入", "本次跳过", "以后插入此卡自动导入，不再询问"))
            {
                if (first.ShowDialog(this) != DialogResult.Yes) return;
            }
            using (var second = new ChoiceDialog("上传完成 · 清理原文件", "已完成 767 个文件的上传与完整校验。\r\n是否删除 SD 卡中的对应原文件？\r\n当前为界面预览，不会执行实际删除。", "删除已上传原件", "保留原文件", "记住本次选择，以后不再询问")) second.ShowDialog(this);
        }
        protected override void WndProc(ref Message m)
        {
            // Never do disk I/O in the device notification callback.
            if (m.Msg == 0x0219 && (m.WParam.ToInt64() == 0x8000 || m.WParam.ToInt64() == 0x8004))
            {
                lastScan = DateTime.MinValue;
                if (m.WParam.ToInt64() == 0x8004 && m.LParam != IntPtr.Zero && Marshal.ReadInt32(m.LParam, 4) == 2)
                {
                    identityGeneration++;
                    int mask = Marshal.ReadInt32(m.LParam, 12);
                    for (int bit = 0; bit < 26; bit++) if ((mask & (1 << bit)) != 0)
                    {
                        string root = ((char)('A' + bit)).ToString() + ":\\";
                        handled.RemoveWhere(token => token.StartsWith(root + "|", StringComparison.OrdinalIgnoreCase));
                        comparisons.Remove(root);
                        if (String.Equals(activeRoot, root, StringComparison.OrdinalIgnoreCase) && cancellation != null) cancellation.Cancel();
                    }
                }
            }
            base.WndProc(ref m);
        }
        async Task ScanAsync()
        {
            if (scanning || preview || exiting) return;
            scanning = true; lastScan = DateTime.UtcNow; int generation = identityGeneration;
            try
            {
                var scan = WorkerClient.Scan(store.Root); activeScan = scan;
                var found = await scan;
                if (IsDisposed || exiting || generation != identityGeneration) return;
                disks = found; handled.IntersectWith(disks.Select(Token));
                foreach (string root in comparisons.Keys.Where(r => !disks.Any(d => d.Root.Equals(r, StringComparison.OrdinalIgnoreCase))).ToList()) comparisons.Remove(root);
                BindPlans(); RenderCards();
                errorShown.IntersectWith(disks.Where(d => d.Error != null || d.MarkerError != null || Duplicate(d)).Select(Token));
                if (!busy) status.Text = (paused ? "已暂停自动提示" : "监听中") + " · " + disks.Count + " 张卡片 · 最近扫描 " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex) { WriteLog("扫描失败，将自动重试：" + ex.Message); status.Text = "监听仍在运行 · 扫描失败，可重新扫描"; }
            finally { scanning = false; activeScan = null; }
            if (busy || prompting || exiting) return;
            foreach (var disk in disks.ToList())
            {
                if (busy || prompting || exiting) break;
                if (store.Find(disk.MarkerId) != null && disk.Error == null && disk.MarkerError == null && !Duplicate(disk) && disk.Plan == null && disk.ComparisonError == null)
                    await PrepareCandidates(disk);
            }
            if (busy || paused || prompting || exiting) return;
            foreach (var disk in disks.ToList())
            {
                if (exiting) break;
                if (disk.Error != null || disk.MarkerError != null || Duplicate(disk))
                {
                    if (errorShown.Add(Token(disk))) { WriteLog(DiskState(disk)); Reveal(); Balloon("存储卡暂不可用", disk.Root + " " + DiskState(disk)); }
                    continue;
                }
                var known = store.Find(disk.MarkerId);
                if (known != null && disk.Plan == null && ImportSettings.Ordered(store.State).Count > 0) continue;
                if (!handled.Add(Token(disk))) continue;
                WriteLog("发现 " + disk.Root + " " + disk.Label + " · " + DiskState(disk));
                var card = store.Find(disk.MarkerId);
                if (disk.Error != null || disk.MarkerError != null || card == null || Duplicate(disk))
                { if (store.State.PromptForNewCards != false) { Reveal(); Balloon("检测到存储卡", disk.Root + " " + DiskState(disk)); } continue; }
                if (disk.Plan != null && disk.Plan.Candidates.Count == 0) { WriteLog(card.Name + " · 没有待导入文件，已存在 " + disk.Plan.ExistingCount + " 个，已排除 " + disk.Plan.ExcludedCount + " 个。"); continue; }
                await BeginImport(disk, card, false);
            }
        }
        void Reveal()
        {
            bool wasHidden = !Visible;
            Show(); WindowState = FormWindowState.Normal;
            TopMost = true; BringToFront(); Activate(); TopMost = false;
            if (wasHidden) WriteLog("已显示状态窗口。");
        }
        void Balloon(string title, string message) { tray.ShowBalloonTip(6000, title, message, ToolTipIcon.Info); }
        void WriteLog(string message)
        {
            store.Log(message);
            if (log.TextLength > 24000) log.Text = log.Text.Substring(log.TextLength - 12000);
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        void TogglePause() { paused = !paused; tray.Text = "相机导入助手 · " + (paused ? "自动提示已暂停" : "正在监听"); lastScan = DateTime.MinValue; UpdateDetails(); }
        void ShowSettingsError(Exception error)
        {
            WriteLog("设置未保存：" + error.Message);
            MessageBox.Show(this, error.Message, "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        async Task ReadStartup()
        {
            try { store.State.StartAtLogin = await Task.Run(() => StartupSettings.Read()); store.Save(); }
            catch (Exception ex) { store.State.StartAtLogin = null; WriteLog("无法读取启动设置：" + ex.Message); }
            UpdateDetails();
        }
        async Task ChangeStartup(bool enabled)
        {
            if (startupBusy) return;
            startupBusy = true; UpdateDetails(); Exception failure = null;
            try
            {
                if (!preview) await Task.Run(() => StartupSettings.Set(enabled));
                store.State.StartAtLogin = enabled; store.Save();
            }
            catch (Exception ex) { failure = ex; }
            finally { startupBusy = false; UpdateDetails(); }
            if (failure != null) { if (!preview) await ReadStartup(); ShowSettingsError(failure); UpdateDetails(); }
        }
        void SaveLocations(Action change)
        {
            var before = store.State.Destinations.Select(ImportSettings.Clone).ToList();
            string policy = store.State.DestinationFailure;
            try { change(); ImportSettings.ValidateConfig(store.State); store.Save(); comparisons.Clear(); lastScan = DateTime.MinValue; BindPlans(); }
            catch (Exception ex) { store.State.Destinations = before; store.State.DestinationFailure = policy; ShowSettingsError(ex); }
            UpdateDetails();
        }
        void EditExclusions()
        {
            if (prompting) return;
            prompting = true;
            try
            {
                using (var dialog = new ExclusionsDialog(store.State.ExcludedExtensions))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var old = store.State.ExcludedExtensions;
                    try { store.State.ExcludedExtensions = dialog.Excluded; store.Save(); comparisons.Clear(); BindPlans(); lastScan = DateTime.MinValue; }
                    catch (Exception error) { store.State.ExcludedExtensions = old; ShowSettingsError(error); }
                }
            }
            finally { prompting = false; UpdateDetails(); }
        }
        void EditTransferSettings()
        {
            if (prompting) return; prompting = true;
            try
            {
                using (var dialog = new TransferSettingsDialog(store.State))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var old = new[] { store.State.MaxParallel, store.State.IdleTimeoutSeconds };
                    bool? auto = store.State.AutoParallel; long? large = store.State.LargeFileBytes;
                    try
                    {
                        store.State.MaxParallel = dialog.MaxParallel; store.State.IdleTimeoutSeconds = dialog.IdleTimeoutSeconds;
                        store.State.AutoParallel = dialog.AutoParallel; store.State.LargeFileBytes = dialog.LargeFileBytes;
                        StorageSettings.ValidateTransfer(store.State); store.Save();
                    }
                    catch (Exception ex) { store.State.MaxParallel = old[0]; store.State.IdleTimeoutSeconds = old[1]; store.State.AutoParallel = auto; store.State.LargeFileBytes = large; ShowSettingsError(ex); }
                }
            }
            finally { prompting = false; UpdateDetails(); }
        }
        void BindPlans()
        {
            foreach (var disk in disks)
            {
                disk.Plan = null; disk.ComparisonError = null;
                if (store.Find(disk.MarkerId) == null || disk.Error != null || disk.MarkerError != null) continue;
                ComparisonEntry entry;
                if (comparisons.TryGetValue(disk.Root, out entry) && entry.Signature == CandidatePlanner.Signature(disk, store.State))
                { disk.Plan = entry.Plan; disk.ComparisonError = entry.Error; }
            }
        }
        async Task PrepareCandidates(DiskSnapshot disk)
        {
            var targets = ImportSettings.Ordered(store.State);
            if (targets.Count == 0) return;
            if (!paused && !handled.Contains(Token(disk))) Reveal();
            string signature = CandidatePlanner.Signature(disk, store.State);
            comparisons.Remove(disk.Root);
            foreach (var current in disks.Where(d => d.Root == disk.Root)) { current.Plan = null; current.ComparisonError = null; }
            disk.Plan = null; disk.ComparisonError = null;
            busy = true; activeRoot = disk.Root; cancellation = new CancellationTokenSource(); progress.Value = 0;
            status.Text = "正在对比目标内容…";
            var request = new WorkRequest { Operation = "plan", SourceRoot = disk.Root, CardId = disk.MarkerId,
                ExpectedSerial = disk.Serial, ExpectedCapacity = disk.Capacity, CandidateFiles = disk.Media, IdleTimeoutSeconds = StorageSettings.IdleSeconds(store.State),
                ComparisonTargets = targets, ExcludedExtensions = MediaRules.NormalizeExcluded(store.State.ExcludedExtensions) };
            activeImport = CompareAsync(disk, signature, identityGeneration, request, cancellation.Token);
            await activeImport;
        }
        async Task CompareAsync(DiskSnapshot disk, string signature, int generation, WorkRequest request, CancellationToken cancel)
        {
            var entry = new ComparisonEntry { Signature = signature };
            try
            {
                var result = await WorkerClient.Run(request, store.Root, cancel, delegate(CandidateProgress p) {
                    status.Text = p.Message + (p.Total > 0 ? " · " + p.Processed + " / " + p.Total : "");
                    if (p.TotalBytes > 0) status.Text += " · " + FluentView.SizeText(p.Bytes) + " / " + FluentView.SizeText(p.TotalBytes);
                    progress.Value = (int)(Math.Max(0, Math.Min(p.Processed, p.Total)) * 1000L / Math.Max(1, p.Total));
                });
                if (!result.Success || result.Plan == null) throw new IOException(result.Error ?? "目标对比没有返回结果。");
                entry.Plan = result.Plan;
                foreach (string warning in result.Plan.Warnings) WriteLog(warning);
            }
            catch (OperationCanceledException) { entry.Error = "目标对比已取消，请刷新重试。"; }
            catch (Exception error) { entry.Error = "目标对比未完成，请刷新重试。"; WriteLog(entry.Error + " " + error.Message); }
            finally
            {
                // Ignore stale results if the card, media list or settings changed during I/O.
                bool current = generation == identityGeneration && disks.Any(d => d.Root == disk.Root && CandidatePlanner.Signature(d, store.State) == signature);
                if (current) comparisons[disk.Root] = entry;
                BindPlans(); disk.Plan = current ? entry.Plan : null; disk.ComparisonError = current ? entry.Error : null;
                busy = false; activeRoot = null; cancellation.Dispose(); cancellation = null;
                status.Text = "监听中"; UpdateDetails(); lastScan = DateTime.MinValue;
            }
        }
        void EditLocation(DestinationRecord existing)
        {
            if (prompting || exiting) return;
            if (backgroundProbes.Any(t => !t.IsCompleted)) { status.Text = "等待上次连接测试退出…"; return; }
            prompting = true;
            var testCancellation = new CancellationTokenSource();
            try
            {
                using (var dialog = new LocationDialog(existing, async delegate(DestinationRecord d) {
                    if (exiting) throw new OperationCanceledException("应用正在退出。");
                    if (preview) return new WorkResult { Success = true, Detail = "界面预览 · 未连接真实位置" };
                    var pending = WorkerClient.Run(new WorkRequest { Operation = "probe", DestinationRoot = d.Path, Target = d }, store.Root, testCancellation.Token);
                    backgroundProbes.Add(pending);
                    try { return await pending; } finally { backgroundProbes.Remove(pending); }
                }))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (exiting) return;
                    SaveLocations(delegate {
                        if (existing == null) store.State.Destinations.Add(dialog.Destination);
                        else store.State.Destinations[store.State.Destinations.FindIndex(d => d.Id == existing.Id)] = dialog.Destination;
                    });
                    if (existing != null) view.LocationResults.Remove(existing.Id);
                }
            }
            finally { testCancellation.Cancel(); testCancellation.Dispose(); prompting = false; UpdateDetails(); }
        }
        async Task LocationAction(string action, string id)
        {
            if (exiting) return;
            var destination = store.State.Destinations.FirstOrDefault(d => d.Id == id);
            if (destination == null) return;
            if (action == "edit") { EditLocation(destination); return; }
            if (action == "test")
            {
                if (busy || prompting) return;
                if (preview) { view.LocationResults[id] = "预览 · 不测试真实连接"; UpdateDetails(); return; }
                busy = true; cancellation = new CancellationTokenSource(); activeDestination = destination.Name;
                view.LocationResults[id] = "正在测试…"; status.Text = "测试连接 · " + destination.Name; UpdateDetails();
                activeImport = TestLocation(ImportSettings.Clone(destination), cancellation.Token);
                await activeImport; return;
            }
            if (action == "remove")
            {
                if (prompting) return;
                prompting = true;
                try
                {
                    using (var dialog = new ChoiceDialog("移除此位置？", destination.Name + "\r\n已保存的文件不受影响。", "移除", "取消", null))
                        if (dialog.ShowDialog(this) != DialogResult.Yes) return;
                }
                finally { prompting = false; }
            }
            SaveLocations(delegate {
                int index = store.State.Destinations.FindIndex(d => d.Id == id);
                if (action == "toggle") destination.Enabled = !destination.Enabled;
                else if (action == "remove") store.State.Destinations.RemoveAt(index);
                else if (action == "up" || action == "down")
                {
                    int other = index + (action == "up" ? -1 : 1);
                    if (other >= 0 && other < store.State.Destinations.Count)
                    { store.State.Destinations[index] = store.State.Destinations[other]; store.State.Destinations[other] = destination; }
                }
            });
        }
        async Task TestLocation(DestinationRecord destination, CancellationToken cancel)
        {
            try
            {
                var result = await WorkerClient.Run(new WorkRequest { Operation = "probe", DestinationRoot = destination.Path, Target = destination }, store.Root, cancel);
                view.LocationResults[destination.Id] = result.Success ? (result.Detail ?? "读写校验通过") + " · " + DateTime.Now.ToString("HH:mm") : "连接失败 · " + result.Error;
            }
            catch (OperationCanceledException) { view.LocationResults[destination.Id] = "测试已取消"; }
            catch (Exception ex) { view.LocationResults[destination.Id] = "测试未完成 · " + ex.Message; }
            finally { busy = false; cancellation.Dispose(); cancellation = null; activeDestination = ""; status.Text = "监听中"; UpdateDetails(); lastScan = DateTime.MinValue; }
        }
        async Task RegisterSelected()
        {
            var disk = Selected; if (disk == null || busy || preview || exiting) return;
            prompting = true;
            try
            {
                using (var dialog = new RegistrationDialog(store, disk))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (exiting) return;
                    var card = dialog.SelectedRecord;
                    bool isNew = card == null;
                    string id = isNew ? Guid.NewGuid().ToString("D").ToUpperInvariant() : card.Id;
                    if (disks.Any(d => d.Root != disk.Root && String.Equals(d.MarkerId, id, StringComparison.OrdinalIgnoreCase))) throw new IOException("同一 UUID 的卡片仍连接着，请为另一张卡选择新建登记。");
                    if (isNew) card = new CardRecord { Id = id, AutoImport = false, DeleteMode = "Ask" };
                    busy = true; activeRoot = disk.Root; cancellation = new CancellationTokenSource(); UpdateDetails();
                    activeImport = CompleteRegistration(disk, card, isNew, dialog.CardName, cancellation.Token);
                    await activeImport;
                }
            }
            catch (Exception ex) { WriteLog("登记失败：" + ex.Message); MessageBox.Show(this, ex.Message, "无法完成登记", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { prompting = false; }
        }
        async Task CompleteRegistration(DiskSnapshot disk, CardRecord card, bool isNew, string name, CancellationToken cancel)
        {
            try
            {
                status.Text = "正在写入并核验卡片登记标识…";
                var result = await WorkerClient.Run(new WorkRequest { Operation = "register", SourceRoot = disk.Root, CardId = card.Id,
                    PreviousCardId = disk.MarkerId, ExpectedSerial = disk.Serial, ExpectedCapacity = disk.Capacity }, store.Root, cancel);
                if (!result.Success) throw new IOException(result.Error);
                card.Name = name; card.Label = disk.Label; card.Capacity = disk.Capacity;
                card.LegacySerial = disk.Serial; card.NeedsBinding = false;
                if (isNew) store.State.Cards.Add(card);
                store.Save(); identityGeneration++; disk.MarkerId = card.Id; lastOutcome = "登记成功：" + name;
                WriteLog(lastOutcome + " · " + disk.Root + " · UUID " + card.Id); handled.Remove(Token(disk));
            }
            catch (OperationCanceledException) { lastOutcome = "登记已取消，请重新扫描核对标识是否已写入"; WriteLog(lastOutcome); }
            catch (Exception ex) { lastOutcome = "登记失败，请重新扫描核对"; WriteLog(lastOutcome + "：" + ex.Message); if (!exiting) MessageBox.Show(this, ex.Message, "登记失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { busy = false; activeRoot = null; cancellation.Dispose(); cancellation = null; UpdateDetails(); lastScan = DateTime.MinValue; }
        }
        void EditPreferences()
        {
            var disk = Selected; var card = disk == null ? null : store.Find(disk.MarkerId); if (card == null) return;
            EditCard(card);
        }
        void EditCard(CardRecord card)
        {
            if (busy || prompting) return;
            prompting = true; try { using (var dialog = new PreferencesDialog(store, card)) dialog.ShowDialog(this); } finally { prompting = false; UpdateDetails(); }
        }
        async Task StartSelected()
        {
            var disk = Selected; var card = disk == null ? null : store.Find(disk.MarkerId);
            if (card != null && !busy && !preview) await BeginImport(disk, card, true);
        }
        async Task BeginImport(DiskSnapshot disk, CardRecord card, bool manual)
        {
            if (busy || prompting || exiting || Duplicate(disk) || disk.Error != null || disk.MarkerError != null) return;
            List<DestinationRecord> targets;
            try { ImportSettings.ValidateConfig(store.State); targets = ImportSettings.Ordered(store.State).ToList(); }
            catch (Exception ex) { ShowSettingsError(ex); view.ShowPage("locations"); return; }
            if (targets.Count == 0) { Reveal(); view.ShowPage("locations"); return; }
            ComparisonEntry comparison;
            if (manual || disk.Plan == null || !comparisons.TryGetValue(disk.Root, out comparison) || comparison.Signature != CandidatePlanner.Signature(disk, store.State))
            { if (manual) { comparisons.Remove(disk.Root); await PrepareCandidates(disk); } else return; }
            if (exiting || busy || !PresentInLastScan(disk, card.Id) || !comparisons.TryGetValue(disk.Root, out comparison) ||
                comparison.Signature != CandidatePlanner.Signature(disk, store.State) || comparison.Plan == null) return;
            targets = ImportSettings.Ordered(store.State);
            var files = disk.Plan == null ? null : disk.Plan.Candidates.ToList();
            if (files == null || files.Count == 0) { UpdateDetails(); return; }
            string confirmedSignature = comparison.Signature;
            int confirmedGeneration = identityGeneration;
            string failurePolicy = store.State.DestinationFailure;
            var availability = new List<LocationAvailability>();
            busy = true; activeRoot = disk.Root; cancellation = new CancellationTokenSource();
            status.Text = "检查本次导入位置…"; progress.Value = 0; UpdateDetails();
            try
            {
                activeImport = Preflight(targets, availability, cancellation.Token, files.Sum(f => f.Length));
                await activeImport;
            }
            catch (Exception ex) { WriteLog("位置检查已停止：" + ex.Message); return; }
            finally { busy = false; activeRoot = null; cancellation.Dispose(); cancellation = null; activeImport = null; UpdateDetails(); }
            targets = availability.Where(a => a.Available).Select(a => a.Destination).ToList();
            bool makeDefault = false;
            Reveal(); prompting = true;
            try
            {
                if (manual || !card.AutoImport || availability.Any(a => !a.Available))
                {
                    using (var dialog = new ImportDialog(card, disk, files, availability, failurePolicy))
                    {
                        if (dialog.ShowDialog(this) != DialogResult.Yes) return;
                        targets = dialog.SelectedTargets.Select(ImportSettings.Clone).ToList(); makeDefault = dialog.MakeDefault;
                        if (dialog.Remember) { card.AutoImport = true; store.Save(); }
                    }
                }
            }
            finally { prompting = false; }
            if (targets.Count == 0) return;
            if (exiting || !PresentInLastScan(disk, card.Id)) return;
            var latest = disks.FirstOrDefault(d => d.Root == disk.Root);
            if (confirmedGeneration != identityGeneration || latest == null || latest.Error != null || latest.MarkerError != null ||
                Duplicate(latest) || CandidatePlanner.Signature(latest, store.State) != confirmedSignature ||
                !comparisons.TryGetValue(disk.Root, out comparison) || comparison.Signature != confirmedSignature || comparison.Plan == null)
            {
                comparisons.Remove(disk.Root); BindPlans(); lastScan = DateTime.MinValue;
                WriteLog("待导入内容或位置已变化，本次确认作废，请重新确认。");
                Balloon("请重新确认导入", "待导入内容或位置已变化，尚未开始导入。"); UpdateDetails(); return;
            }
            if (makeDefault)
            {
                var order = targets.Select(t => t.Id).ToList();
                SaveLocations(delegate { store.State.Destinations = store.State.Destinations.OrderBy(d => order.Contains(d.Id) ? order.IndexOf(d.Id) : Int32.MaxValue).ToList(); });
            }
            busy = true; fallbackActive = false; activeDestination = targets[0].Name; progress.Value = 0; activeRoot = disk.Root; cancellation = new CancellationTokenSource(); UpdateDetails();
            activeImport = ImportAsync(disk, card, files, targets, failurePolicy, cancellation.Token);
            await activeImport;
        }
        async Task Preflight(List<DestinationRecord> targets, List<LocationAvailability> availability, CancellationToken cancel, long requiredBytes)
        {
            // A small bounded connection check, not a write-test on card insertion.
            for (int i = 0; i < targets.Count; i += 2)
            {
                var batch = targets.Skip(i).Take(2).Select(async delegate(DestinationRecord destination) {
                    var result = await WorkerClient.Run(new WorkRequest { Operation = "probe", Target = destination, DestinationRoot = destination.Path, ReadOnlyProbe = true }, store.Root, cancel);
                    if (result.Success && result.FreeBytes.HasValue && result.FreeBytes.Value < requiredBytes)
                    { result.Success = false; result.Error = "空间不足 · 需要 " + FluentView.SizeText(requiredBytes) + "，可用 " + FluentView.SizeText(result.FreeBytes.Value); }
                    return new LocationAvailability { Destination = destination, Available = result.Success, Detail = result.Success ? result.Detail ?? "可访问" : result.Error, FreeBytes = result.FreeBytes };
                });
                availability.AddRange(await Task.WhenAll(batch));
            }
        }
        async Task ImportAsync(DiskSnapshot disk, CardRecord card, List<MediaItem> files, List<DestinationRecord> targets, string failurePolicy, CancellationToken cancel)
        {
            var receipts = new List<TransferReceipt>();
            string journal = Path.Combine(store.Root, "imports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
            int failed = 0, deleted = 0, completed = 0, running = 0, queued = files.Count;
            long completedBytes = 0, totalBytes = files.Sum(f => f.Length);
            int batchIdle = StorageSettings.IdleSeconds(store.State);
            var current = new Dictionary<string, TransferProgress>(StringComparer.OrdinalIgnoreCase);
            var currentTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var samples = new Dictionary<string, Tuple<DateTime, long, string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                StorageSettings.ValidateTransfer(store.State);
                await ImportScheduler.Run(files, StorageSettings.Parallel(store.State), StorageSettings.LargeBytes(store.State), store.State.AutoParallel != false, async delegate(MediaItem file)
                {
                    cancel.ThrowIfCancellationRequested();
                    // This is only a quick UI hint; the isolated worker verifies and locks the real card.
                    if (!PresentInLastScan(disk, card.Id)) throw new IOException("存储卡已拔出或身份改变；本次传输已停止，监听继续运行。");
                    int destinationIndex = 0;
                    current[file.SourcePath] = new TransferProgress { Stage = "连接", Total = file.Length };
                    WorkResult result;
                    while (true)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var destination = targets[destinationIndex]; activeDestination = destination.Name; UpdateDetails();
                        currentTargets[file.SourcePath] = destination.Name;
                        var request = new WorkRequest { Operation = "copy", SourceRoot = disk.Root, CardId = card.Id, ExpectedSerial = disk.Serial, ExpectedCapacity = disk.Capacity,
                            File = file, DestinationRoot = destination.Path, DestinationKind = ImportSettings.Kind(destination), Target = destination, IdleTimeoutSeconds = batchIdle };
                        result = await WorkerClient.Run(request, store.Root, cancel, null, delegate(TransferProgress p) {
                            current[file.SourcePath] = p;
                            Tuple<DateTime, long, string> before;
                            double speed = 0; var now = DateTime.UtcNow;
                            if (samples.TryGetValue(file.SourcePath, out before) && before.Item3 == p.Stage && p.Bytes >= before.Item2)
                                speed = (p.Bytes - before.Item2) / Math.Max(.1, (now - before.Item1).TotalSeconds);
                            if (!samples.ContainsKey(file.SourcePath) || before.Item3 != p.Stage || (now - before.Item1).TotalSeconds >= 1) samples[file.SourcePath] = Tuple.Create(now, p.Bytes, p.Stage);
                            string rate = speed > 0 ? " · " + FluentView.SizeText((long)speed) + "/s · 约 " + Remaining((p.Total - p.Bytes) / speed) : "";
                            status.Text = "已完成 " + completed + " / " + files.Count + " · 运行 " + running + " · 排队 " + queued + "\r\n" + String.Join("\r\n", current.OrderBy(pair => pair.Key).Select(pair => pair.Value.Stage + " · " + Path.GetFileName(pair.Key) + " · " + FluentView.SizeText(pair.Value.Bytes) + " / " + FluentView.SizeText(pair.Value.Total) + (pair.Key == file.SourcePath ? rate : "")));
                            activeDestination = String.Join(" / ", currentTargets.Values.Distinct());
                            double inFlight = current.Sum(pair => pair.Value.Total > 0 ? pair.Value.Total * Phase(pair.Value) : 0);
                            progress.Value = (int)Math.Min(999, (completedBytes + inFlight) * 1000d / Math.Max(1, totalBytes));
                        });
                        if (result.Success) break;
                        WriteLog(destination.Name + " 导入失败：" + result.Error);
                        if (!PresentInLastScan(disk, card.Id)) throw new IOException("存储卡已断开。");
                        int next = ImportSettings.NextIndex(destinationIndex, targets.Count, failurePolicy);
                        if (next < 0) { failed++; WriteLog("保留原件：" + file.RelativePath + " · 所有已批准位置均未完成导入。"); current.Remove(file.SourcePath); currentTargets.Remove(file.SourcePath); return false; }
                        destinationIndex = next; fallbackActive = true; WriteLog("改用 " + targets[destinationIndex].Name);
                    }
                    if (result.Success && result.Receipt != null)
                    {
                        receipts.Add(result.Receipt); JsonFile.Write(journal, receipts);
                        WriteLog((result.ReusedExisting ? "已校验相同副本 " : "已复制并校验 ") + file.RelativePath + " → " + result.Receipt.DestinationPath);
                    }
                    else { failed++; WriteLog("保留原件，传输失败：" + file.RelativePath + " · " + result.Error); }
                    current.Remove(file.SourcePath); currentTargets.Remove(file.SourcePath); completedBytes += file.Length;
                    return result.Success;
                }, delegate(int done, int active, int waiting) { completed = done; running = active; queued = waiting; }, cancel, delegate { cancellation.Cancel(); }, true);
                cancel.ThrowIfCancellationRequested();
                var remote = receipts.Where(r => r.DestinationKind == "remote").ToList();
                int local = receipts.Count - remote.Count;
                string summary = "已导入 " + receipts.Count + " 个文件" + (failed > 0 ? "，失败 " + failed + " 个" : "") + "。";
                WriteLog(summary);
                bool remove = remote.Count > 0 && card.DeleteMode == "Auto";
                if (remote.Count > 0 && card.DeleteMode != "Keep" && card.DeleteMode != "Auto")
                {
                    using (var dialog = new ChoiceDialog("清理卡内原件？", summary + "\r\n可清理 " + remote.Count + " 个已上传原件。" + (local > 0 ? "\r\n" + local + " 个副本不满足锁定清理条件，原件保留。" : ""), "清理原件", "保留", "记住本次选择"))
                    {
                        var choice = dialog.ShowDialog(this); remove = choice == DialogResult.Yes;
                        if (dialog.Remember && (choice == DialogResult.Yes || choice == DialogResult.No)) { card.DeleteMode = remove ? "Auto" : "Keep"; store.Save(); }
                    }
                }
                if (remove)
                {
                    foreach (var receipt in remote)
                    {
                        cancel.ThrowIfCancellationRequested(); status.Text = "重新校验并清理 · " + Path.GetFileName(receipt.SourcePath);
                        var result = await WorkerClient.Run(new WorkRequest { Operation = "delete", SourceRoot = disk.Root, CardId = card.Id, ExpectedSerial = disk.Serial, ExpectedCapacity = disk.Capacity, Receipt = receipt }, store.Root, cancel);
                        if (result.Success && result.Deleted) { deleted++; WriteLog("已清理（再次校验成功）：" + receipt.SourcePath); }
                        else WriteLog("未确认清理成功，请核对卡内原件：" + receipt.SourcePath + " · " + result.Error);
                    }
                }
                lastOutcome = summary + (deleted > 0 ? " 已清理 " + deleted + " 个原件。" : " 原件已保留。");
                status.Text = "任务完成 · " + lastOutcome; WriteLog(status.Text);
                Balloon("导入任务完成", summary + " 已清理 " + deleted + " 个。");
            }
            catch (OperationCanceledException) { lastOutcome = "任务已取消 · 副本已保留；若取消时正在清理，请核对卡内文件"; status.Text = lastOutcome; WriteLog(status.Text); }
            catch (Exception ex) { lastOutcome = "导入停止 · 已完成 " + receipts.Count + " 个。" + ex.Message; status.Text = lastOutcome; WriteLog("任务错误：" + ex.Message); Balloon("导入停止", ex.Message); }
            finally { comparisons.Remove(disk.Root); BindPlans(); busy = false; activeRoot = null; cancellation.Dispose(); cancellation = null; UpdateDetails(); lastScan = DateTime.MinValue; }
        }
        static double Phase(TransferProgress p)
        {
            double ratio = p.Total > 0 ? Math.Min(1, p.Bytes / (double)p.Total) : 0;
            return p.Stage == "校验" ? .75 + .25 * ratio : p.Stage == "读取" ? .2 * ratio : p.Stage == "上传" ? .75 * ratio : p.Stage == "复制" ? .2 + .55 * ratio : 0;
        }
        static string Remaining(double seconds)
        { return seconds >= 3600 ? Math.Ceiling(seconds / 3600) + " 小时" : seconds >= 60 ? Math.Ceiling(seconds / 60) + " 分钟" : Math.Ceiling(Math.Max(0, seconds)) + " 秒"; }
        async Task ExitAsync()
        {
            if (exiting) return;
            if (prompting) { Balloon("请先关闭对话框", "关闭当前对话框后可退出；连接测试会随之取消。"); return; }
            exiting = true; timer.Stop();
            if (cancellation != null) cancellation.Cancel();
            try { if (activeImport != null) await activeImport; } catch (Exception ex) { WriteLog("退出时任务已停止：" + ex.Message); }
            try { await Task.WhenAll(backgroundProbes.ToArray()); } catch (Exception ex) { WriteLog("连接测试已停止：" + ex.Message); }
            try { if (activeScan != null) await activeScan; } catch (Exception ex) { WriteLog("退出时扫描已停止：" + ex.Message); }
            if (WorkerClient.HasUnconfirmedWorker)
            {
                exiting = false; timer.Start(); MessageBox.Show(this, "还有未确认退出的工作进程，请暂勿拔卡。等待进程结束后再退出。", "暂时无法退出", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
            }
            WriteLog("助手已退出。"); Close();
        }
        sealed class UiText
        {
            string text = "";
            public Action Changed;
            public string Text { get { return text; } set { text = value ?? ""; if (Changed != null) Changed(); } }
            public int TextLength { get { return text.Length; } }
            public void AppendText(string value) { Text = text + value; }
        }
        sealed class UiProgress
        {
            int value;
            public Action Changed;
            public int Value { get { return value; } set { this.value = value; if (Changed != null) Changed(); } }
        }
    }
}
