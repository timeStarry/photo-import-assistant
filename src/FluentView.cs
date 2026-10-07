using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PhotoImportV2
{
    public sealed class FluentView : UserControl, IDisposable
    {
        readonly UserControl surface;
        bool refreshing, themeLoaded;
        string activePage = "import", lastLog, pickerSignature, cardsSignature, locationsSignature;
        Action primary;
        public Action Register, Import, Preferences, Rescan, Cancel, Pause, OpenLogs, ChromeChanged, AddLocation, EditExclusions;
        public Action<string> SelectCard, ThemeChanged, FailureChanged;
        public Action<bool> StartupChanged, NewCardPromptChanged;
        public Action<string, string> LocationAction, CardAction;
        public readonly Dictionary<string, string> LocationResults = new Dictionary<string, string>();
        public FluentView()
        {
            string xaml = ReadResource("FluentView.xaml");
            xaml = xaml.Insert(xaml.IndexOf('>') + 1, "<UserControl.Resources>" + ReadResource("FluentStyles.xaml") + "</UserControl.Resources>");
            surface = (UserControl)XamlReader.Parse(xaml); Content = surface;
            foreach (string page in new[] { "Import", "Cards", "Locations", "Activity", "Settings" })
            {
                string selected = page.ToLowerInvariant();
                Connect(page + "Nav", delegate { ShowPage(selected); });
            }
            Connect("ScanButton", delegate { if (Rescan != null) Rescan(); });
            Connect("RegisterButton", delegate { if (Register != null) Register(); });
            Connect("PrimaryAction", delegate { if (primary != null) primary(); });
            Connect("PreferencesButton", delegate { if (Preferences != null) Preferences(); });
            Connect("CancelButton", delegate { if (Cancel != null) Cancel(); });
            Connect("PauseButton", delegate { if (Pause != null) Pause(); });
            Connect("OpenLogsButton", delegate { if (OpenLogs != null) OpenLogs(); });
            Connect("ManageLocationsButton", delegate { ShowPage("locations"); });
            Connect("AddLocationButton", delegate { if (AddLocation != null) AddLocation(); });
            Connect("ExclusionsButton", delegate { if (EditExclusions != null) EditExclusions(); });
            Get<ComboBox>("CardPicker").SelectionChanged += delegate {
                if (!refreshing && SelectCard != null) { var item = Get<ComboBox>("CardPicker").SelectedItem as PickerItem; if (item != null) SelectCard(item.Root); }
            };
            Get<ComboBox>("ThemePicker").SelectionChanged += delegate {
                if (refreshing) return;
                string mode = Get<ComboBox>("ThemePicker").SelectedIndex == 1 ? "light" : Get<ComboBox>("ThemePicker").SelectedIndex == 2 ? "dark" : "system";
                FluentTheme.Mode = mode; ApplyTheme(); if (ThemeChanged != null) ThemeChanged(mode);
            };
            Get<ComboBox>("FailurePicker").SelectionChanged += delegate {
                if (!refreshing && FailureChanged != null) FailureChanged(Get<ComboBox>("FailurePicker").SelectedIndex == 1 ? "Stop" : "Next");
            };
            Get<CheckBox>("StartupCheck").Click += delegate { if (!refreshing && StartupChanged != null) StartupChanged(Get<CheckBox>("StartupCheck").IsChecked == true); };
            Get<CheckBox>("NewCardsCheck").Click += delegate { if (!refreshing && NewCardPromptChanged != null) NewCardPromptChanged(Get<CheckBox>("NewCardsCheck").IsChecked == true); };
            SystemEvents.UserPreferenceChanged += SystemThemeChanged;
            ApplyTheme(); ShowPage("import");
        }
        static string ReadResource(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PhotoImportV2." + name))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }
        T Get<T>(string name) where T : FrameworkElement { return (T)surface.FindName(name); }
        internal string PrimaryCaption { get { return (string)Get<Button>("PrimaryAction").Content; } }
        internal bool PrimaryEnabled { get { return Get<Button>("PrimaryAction").IsEnabled; } }
        internal string PhotoCaption { get { return Get<TextBlock>("PhotoCount").Text; } }
        void Connect(string name, Action action) { Get<Button>(name).Click += delegate { action(); }; }
        void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e) { Dispatcher.BeginInvoke(new Action(ApplyTheme)); }
        void ApplyTheme()
        {
            FluentTheme.SetPalette(surface.Resources, null); ShowPage(activePage);
            cardsSignature = locationsSignature = null;
            if (ChromeChanged != null) ChromeChanged();
        }
        Brush Brush(string key) { return (Brush)surface.Resources[key]; }
        public void ShowPage(string page)
        {
            activePage = page;
            foreach (string name in new[] { "Import", "Cards", "Locations", "Activity", "Settings" })
            {
                bool selected = name.Equals(page, StringComparison.OrdinalIgnoreCase);
                Get<Grid>(name + "Page").Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                var button = Get<Button>(name + "Nav");
                button.Background = selected ? Brush("AccentSoftBrush") : Brushes.Transparent;
                button.Foreground = Brush(selected ? "AccentBrush" : "MutedBrush");
                button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        public static bool Video(MediaItem item)
        {
            string ext = Path.GetExtension(item.RelativePath ?? item.SourcePath ?? "");
            return new[] { ".mp4", ".mov", ".avi" }.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }
        public static string SizeText(long bytes)
        {
            return bytes >= 1000000000 ? (bytes / 1000000000d).ToString("0.0") + " GB" :
                bytes >= 1000000 ? (bytes / 1000000d).ToString("0.0") + " MB" :
                bytes >= 1000 ? (bytes / 1000d).ToString("0.0") + " KB" : bytes + " B";
        }
        static CardRecord Find(AppState state, DiskSnapshot disk)
        {
            return disk == null || String.IsNullOrEmpty(disk.MarkerId) ? null : state.Cards.FirstOrDefault(c => !c.NeedsBinding && String.Equals(c.Id, disk.MarkerId, StringComparison.OrdinalIgnoreCase));
        }
        public void Render(IList<DiskSnapshot> disks, string selectedRoot, AppState state, bool busy, bool paused, string status, int progress, string activity, string outcome, bool fallback = false, string activeDestination = "", bool startupBusy = false)
        {
            refreshing = true;
            try
            {
                ImportSettings.Normalize(state);
                if (!themeLoaded)
                {
                    FluentTheme.Mode = state.Theme ?? "system";
                    Get<ComboBox>("ThemePicker").SelectedIndex = FluentTheme.Mode == "light" ? 1 : FluentTheme.Mode == "dark" ? 2 : 0;
                    ApplyTheme(); themeLoaded = true;
                }
                var disk = disks.FirstOrDefault(d => d.Root == selectedRoot) ?? disks.FirstOrDefault();
                var card = Find(state, disk);
                bool duplicate = disk != null && !String.IsNullOrEmpty(disk.MarkerId) && disks.Count(d => String.Equals(d.MarkerId, disk.MarkerId, StringComparison.OrdinalIgnoreCase)) > 1;
                string issue = disk == null ? null : disk.Error ?? disk.MarkerError ?? (duplicate ? "登记标识重复，请重新关联其中一张卡。" : null);
                bool unregistered = disk != null && card == null;
                var candidates = disk == null ? null : unregistered ? disk.Media.Where(f => MediaRules.Includes(f.RelativePath ?? f.SourcePath, state.ExcludedExtensions)).ToList() : disk.Plan == null ? null : disk.Plan.Candidates;
                string signature = String.Join("|", disks.Select(d => d.Key + (Find(state, d) == null ? d.Label : Find(state, d).Name)));
                var picker = Get<ComboBox>("CardPicker");
                if (pickerSignature != signature) { picker.ItemsSource = disks.Select(d => new PickerItem { Root = d.Root, Label = (Find(state, d) == null ? d.Label : Find(state, d).Name) + " · " + d.Root.TrimEnd('\\') }).ToArray(); pickerSignature = signature; }
                picker.SelectedValue = disk == null ? null : disk.Root; picker.Visibility = disks.Count > 1 ? Visibility.Visible : Visibility.Collapsed; picker.IsEnabled = !busy;
                Get<TextBlock>("CardTitle").Text = card != null ? card.Name : disk != null ? String.IsNullOrEmpty(disk.Label) ? "未命名存储卡" : disk.Label : "等待存储卡";
                Get<TextBlock>("CardSubtitle").Text = disk == null ? "插入 SD 卡" : disk.Root.TrimEnd('\\') + "  ·  " + MainWindow.Capacity(disk.Capacity) + (String.IsNullOrEmpty(disk.FileSystem) ? "" : "  ·  " + disk.FileSystem);
                Get<TextBlock>("CardBadgeText").Text = busy ? status.Contains("登记") ? "登记中" : status.Contains("测试") ? "测试中" : status.Contains("对比") || status.Contains("目标目录") ? "对比中" : "导入中" : issue != null || (disk != null && disk.ComparisonError != null) ? "需检查" : disk == null ? "未连接" : unregistered ? "未登记" : candidates == null ? "待对比" : candidates.Count == 0 ? "无待导入" : "就绪";
                string tone = busy ? "Accent" : issue != null || unregistered || (disk != null && disk.ComparisonError != null) ? "Warning" : disk == null ? "Muted" : "Success";
                Get<Border>("CardBadge").Background = Brush(tone == "Muted" ? "HoverBrush" : tone + "SoftBrush");
                Get<TextBlock>("CardBadgeText").Foreground = Brush(tone + "Brush");
                Get<Grid>("MediaSummary").Visibility = disk == null ? Visibility.Collapsed : Visibility.Visible;
                Get<TextBlock>("PhotoLabel").Text = unregistered ? "照片" : "待导入照片";
                Get<TextBlock>("VideoLabel").Text = unregistered ? "视频" : "待导入视频";
                Get<TextBlock>("PhotoCount").Text = candidates == null ? "—" : candidates.Count(m => !Video(m)).ToString("N0");
                Get<TextBlock>("VideoCount").Text = candidates == null ? "—" : candidates.Count(Video).ToString("N0");
                Get<TextBlock>("MediaSize").Text = candidates == null ? "—" : SizeText(candidates.Sum(m => m.Length));
                string comparisonSummary = disk == null || disk.Plan == null ? "" :
                    (disk.Plan.ExistingCount > 0 ? "已存在 " + disk.Plan.ExistingCount.ToString("N0") + " 个" : "") +
                    (disk.Plan.ExcludedCount > 0 ? (disk.Plan.ExistingCount > 0 ? " · " : "") + "已排除 " + disk.Plan.ExcludedCount.ToString("N0") + " 个" : "");
                if (disk != null && disk.Plan != null && disk.Plan.Warnings.Count > 0) comparisonSummary += (comparisonSummary.Length > 0 ? "\r\n" : "") + "目标对比不完整 · 未确认文件保留为候选";
                Get<TextBlock>("ComparisonSummary").Text = comparisonSummary;
                Get<TextBlock>("ComparisonSummary").Visibility = comparisonSummary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                Get<StackPanel>("StorageSummary").Visibility = disk != null && disk.FreeSpace.HasValue ? Visibility.Visible : Visibility.Collapsed;
                if (disk != null && disk.FreeSpace.HasValue)
                {
                    Get<ProgressBar>("StorageUsage").Value = disk.Capacity > 0 ? Math.Max(0, Math.Min(100, (disk.Capacity - disk.FreeSpace.Value) * 100d / disk.Capacity)) : 0;
                    Get<TextBlock>("StorageText").Text = "可用 " + SizeText(disk.FreeSpace.Value) + " / " + SizeText(disk.Capacity);
                }
                bool scanError = (status ?? "").Contains("扫描失败") || (status ?? "").Contains("等待上次工作进程");
                Get<TextBlock>("CardGuidance").Text = scanError ? "读取暂未完成，请刷新或查看活动记录。" : issue ?? (disk == null ? null : disk.ComparisonError) ?? (unregistered ? "登记后可使用此卡的导入偏好。" : "");
                Get<Border>("GuidanceBox").Visibility = !busy && (scanError || issue != null || unregistered || (disk != null && disk.ComparisonError != null)) ? Visibility.Visible : Visibility.Collapsed;
                Get<Grid>("CardActionRow").Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
                Get<Expander>("CardDetails").Visibility = disk == null || busy ? Visibility.Collapsed : Visibility.Visible;
                Get<TextBox>("IdentityText").Text = disk == null ? "" : "卷标  " + disk.Label + "\r\n序列号  " + disk.Serial + "\r\n登记 UUID  " + (disk.MarkerId ?? "未登记");
                var targets = ImportSettings.Ordered(state);
                var main = Get<Button>("PrimaryAction"); main.Content = disk == null ? "等待插卡" : issue != null ? "刷新" : unregistered ? "登记存储卡" : targets.Count == 0 ? "选择导入位置" : disk.ComparisonError != null ? "重新对比" : candidates == null ? "等待目标对比" : candidates.Count == 0 ? "没有新文件" : "导入 " + candidates.Count.ToString("N0") + " 个文件";
                primary = issue != null || (disk != null && disk.ComparisonError != null) ? Rescan : unregistered ? Register : targets.Count == 0 ? (Action)delegate { ShowPage("locations"); } : Import;
                main.IsEnabled = !busy && disk != null && (issue != null || unregistered || targets.Count == 0 || disk.ComparisonError != null || (candidates != null && candidates.Count > 0));
                Get<Button>("ScanButton").IsEnabled = !busy;
                Get<Button>("PreferencesButton").Visibility = card == null ? Visibility.Collapsed : Visibility.Visible; Get<Button>("PreferencesButton").IsEnabled = !busy;
                Get<Button>("RegisterButton").Visibility = disk == null || (unregistered && !duplicate) ? Visibility.Collapsed : Visibility.Visible;
                Get<Button>("RegisterButton").IsEnabled = !busy && disk != null && disk.Error == null && disk.MarkerError == null;
                Get<Border>("ProgressCard").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                Get<TextBlock>("ProgressTitle").Text = status.Contains("登记") ? "正在登记" : status.Contains("测试") ? "正在测试连接" : status.Contains("清理") ? "正在清理原件" : status.Contains("对比") || status.Contains("目标目录") ? "正在对比目标" : "正在导入";
                Get<TextBlock>("ProgressDescription").Text = status;
                Get<TextBlock>("ActiveDestinationText").Text = String.IsNullOrEmpty(activeDestination) ? "" : "保存到 " + activeDestination;
                Get<ProgressBar>("TransferProgress").Value = progress; Get<ProgressBar>("TransferProgress").IsIndeterminate = busy && (progress == 0 || status.Contains("登记") || status.Contains("测试") || status.Contains("清理"));
                Get<Border>("OutcomeCard").Visibility = String.IsNullOrEmpty(outcome) || outcome == "本次会话尚无导入任务" || busy ? Visibility.Collapsed : Visibility.Visible; Get<TextBlock>("OutcomeText").Text = outcome;
                Get<TextBlock>("DestinationName").Text = targets.Count == 0 ? "未设置位置" : targets[0].Name;
                Get<TextBlock>("DestinationPath").Text = targets.Count == 0 ? "" : ImportSettings.Address(targets[0]);
                string fallbackText = state.DestinationFailure == "Stop" ? "不可用时停止导入" : targets.Count > 1 ? "不可用时 → " + String.Join(" → ", targets.Skip(1).Select(d => d.Name)) : "";
                Get<TextBlock>("FallbackSummary").Text = fallbackText; Get<TextBlock>("FallbackSummary").Visibility = fallbackText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                Get<TextBlock>("ListenerLabel").Text = scanError ? "读取异常" : paused ? "提示已暂停" : "监听中"; Get<System.Windows.Shapes.Ellipse>("ListenerDot").Fill = Brush(paused || scanError ? "WarningBrush" : "SuccessBrush");
                Get<Button>("PauseButton").Content = paused ? "恢复" : "暂停"; Get<TextBlock>("PauseDescription").Text = paused ? "已暂停，可手动导入" : "运行中";
                Get<CheckBox>("StartupCheck").IsChecked = state.StartAtLogin == true; Get<CheckBox>("StartupCheck").IsEnabled = !startupBusy && state.StartAtLogin.HasValue;
                Get<TextBlock>("StartupStatus").Text = startupBusy ? "正在更新…" : !state.StartAtLogin.HasValue ? "正在读取…" : state.StartAtLogin == true ? "登录后在托盘运行" : "通过快捷方式启动";
                Get<CheckBox>("NewCardsCheck").IsChecked = state.PromptForNewCards != false;
                Get<ComboBox>("FailurePicker").SelectedIndex = state.DestinationFailure == "Stop" ? 1 : 0; Get<TextBlock>("NextBatchNote").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                Get<TextBlock>("ExclusionsSummary").Text = "照片和视频" + (state.ExcludedExtensions.Count > 0 ? " · 排除 " + String.Join(", ", state.ExcludedExtensions) : "");
                RenderLocations(state, busy); RenderRegisteredCards(state, disks, busy);
                if (lastLog != activity)
                {
                    lastLog = activity; var lines = (activity ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    Get<ListBox>("ActivityList").ItemsSource = lines.Reverse().Take(60).Select(line => new ActivityItem { Time = line.Length > 9 ? line.Substring(0, 8) : "", Message = line.Length > 9 ? line.Substring(9).Trim() : line, Dot = Brush(line.Contains("失败") || line.Contains("错误") ? "WarningBrush" : line.Contains("成功") || line.Contains("完成") ? "SuccessBrush" : "MutedBrush") }).ToArray();
                    Get<TextBlock>("NoActivities").Visibility = lines.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            finally { refreshing = false; }
        }
        TextBlock Text(string value, bool caption)
        {
            var block = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(StyleProperty, caption ? "Caption" : "Body"); return block;
        }
        Button Button(string title, Action action, bool enabled)
        {
            var button = new Button { Content = title, IsEnabled = enabled, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 6, 10, 6), MinHeight = 32 };
            button.SetResourceReference(StyleProperty, "QuietButton"); button.Click += delegate { action(); }; return button;
        }
        Border Card(UIElement body)
        {
            var card = new Border { Child = body, Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 12) }; card.SetResourceReference(StyleProperty, "Card"); return card;
        }
        void RenderLocations(AppState state, bool busy)
        {
            string sig = String.Join("|", state.Destinations.Select(d => d.Id + d.Name + d.Path + d.Enabled)) + String.Join("|", LocationResults.Select(p => p.Key + p.Value)) + busy;
            if (sig == locationsSignature) return; locationsSignature = sig;
            var panel = Get<StackPanel>("LocationsList"); panel.Children.Clear(); int priority = 0;
            for (int i = 0; i < state.Destinations.Count; i++)
            {
                var destination = state.Destinations[i]; string id = destination.Id; int index = i;
                var body = new StackPanel(); var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string rank = !destination.Enabled ? "停用" : ++priority == 1 ? "首选" : "备用 " + (priority - 1);
                var title = Text(destination.Name + "  ·  " + rank, false); title.FontWeight = FontWeights.SemiBold; header.Children.Add(title);
                var check = new CheckBox { Content = "启用", IsChecked = destination.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                Grid.SetColumn(check, 1); header.Children.Add(check); check.Click += delegate { if (LocationAction != null) LocationAction("toggle", id); };
                body.Children.Add(header); var path = Text(ImportSettings.Address(destination), true); path.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(path);
                string result; if (LocationResults.TryGetValue(id, out result)) { var note = Text(result, true); note.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(note); }
                var actions = new WrapPanel { Margin = new Thickness(-10, 14, 0, 0) };
                foreach (string verb in new[] { "edit", "test", "up", "down", "remove" })
                {
                    string action = verb; string label = verb == "edit" ? "编辑" : verb == "test" ? "测试连接" : verb == "up" ? "上移" : verb == "down" ? "下移" : "移除";
                    actions.Children.Add(Button(label, delegate { if (LocationAction != null) LocationAction(action, id); }, verb == "up" ? index > 0 : verb == "down" ? index < state.Destinations.Count - 1 : verb == "test" ? !busy : result != "正在测试…"));
                }
                body.Children.Add(actions); panel.Children.Add(Card(body));
            }
            Get<TextBlock>("LocationsEmpty").Visibility = state.Destinations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void RenderRegisteredCards(AppState state, IList<DiskSnapshot> disks, bool busy)
        {
            string sig = String.Join("|", state.Cards.Select(c => c.Id + c.Name + c.NeedsBinding + c.AutoImport + c.DeleteMode)) + String.Join("|", disks.Select(d => d.Key + d.MarkerId + d.Media.Count)) + busy;
            if (sig == cardsSignature) return; cardsSignature = sig;
            var panel = Get<StackPanel>("CardsList"); panel.Children.Clear();
            foreach (var disk in disks.Where(d => Find(state, d) == null))
            {
                string root = disk.Root;
                var body = new StackPanel(); var name = Text((disk.Label ?? "存储卡") + "  ·  未登记", false); name.FontWeight = FontWeights.SemiBold; body.Children.Add(name);
                body.Children.Add(Text(root.TrimEnd('\\') + " · " + MainWindow.Capacity(disk.Capacity) + " · " + disk.Media.Count + " 个文件", true));
                var button = Button("查看存储卡", delegate { if (SelectCard != null) SelectCard(root); ShowPage("import"); }, !busy); button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(-10, 10, 0, 0); body.Children.Add(button);
                panel.Children.Add(Card(body));
            }
            foreach (var record in state.Cards.OrderByDescending(c => disks.Any(d => String.Equals(c.Id, d.MarkerId, StringComparison.OrdinalIgnoreCase))))
            {
                string id = record.Id; var disk = disks.FirstOrDefault(d => String.Equals(id, d.MarkerId, StringComparison.OrdinalIgnoreCase) && !record.NeedsBinding);
                var body = new StackPanel(); var name = Text(record.Name, false); name.FontWeight = FontWeights.SemiBold; body.Children.Add(name);
                body.Children.Add(Text(MainWindow.Capacity(record.Capacity) + " · " + (disk != null ? disk.Root.TrimEnd('\\') + " · 已连接" : record.NeedsBinding ? "需重新关联" : "未连接"), true));
                var prefs = Text((record.AutoImport ? "自动导入" : "导入前询问") + " · " + (record.DeleteMode == "Auto" ? "自动清理原件" : record.DeleteMode == "Keep" ? "保留原件" : "清理前询问"), true); prefs.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(prefs);
                var row = new WrapPanel { Margin = new Thickness(-10, 12, 0, 0) };
                row.Children.Add(Button("卡片设置", delegate { if (CardAction != null) CardAction("preferences", id); }, !busy));
                if (disk != null) { string root = disk.Root; row.Children.Add(Button("查看", delegate { if (SelectCard != null) SelectCard(root); ShowPage("import"); }, !busy)); }
                body.Children.Add(row);
                var details = new Expander { Header = "标识", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }; details.SetResourceReference(ForegroundProperty, "MutedBrush");
                var identity = new TextBox { Text = "UUID  " + record.Id + "\r\n序列号  " + (record.LegacySerial ?? "未知"), IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 12, TextWrapping = TextWrapping.Wrap }; details.Content = identity; body.Children.Add(details); panel.Children.Add(Card(body));
            }
            Get<TextBlock>("CardsEmpty").Visibility = panel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        public void RenderToFile(string path, int width, int height, double scale = 1)
        {
            surface.Width = width; surface.Height = height; surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) encoder.Save(stream);
        }
        public void PreviewPage(string page) { ShowPage(page); }
        public void Dispose() { SystemEvents.UserPreferenceChanged -= SystemThemeChanged; }
        public sealed class PickerItem { public string Label { get; set; } public string Root { get; set; } }
        public sealed class ActivityItem { public string Time { get; set; } public string Message { get; set; } public Brush Dot { get; set; } }
    }
}
