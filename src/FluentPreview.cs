using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoImportV2
{
    public static class FluentPreview
    {
        public static string RenderAll(string folder)
        {
            Directory.CreateDirectory(folder);
            string[] cases = { "inserted-light", "ready-light", "copying-light", "copying-detail-light", "copying-detail-dark", "complete-light", "empty-light", "ready-dark", "activity-light", "settings-dark", "settings-light", "locations-light", "locations-dark", "providers-light", "providers-dark", "offline-light", "offline-dark", "noavailable-light", "cards-light", "cards-dark", "ready-small", "ready-large-scale", "compared-light", "synchronized-light", "comparison-error-light", "comparing-light", "excluded-light" };
            VerifyDialogBehavior();
            foreach (string scenario in cases)
            {
                var state = new AppState { Theme = scenario.EndsWith("dark") ? "dark" : "light", StartAtLogin = true, Destinations = DemoData.Destinations() };
                if (scenario.StartsWith("providers") || scenario.StartsWith("offline")) state.Destinations = ProviderDestinations();
                if (scenario.StartsWith("noavailable")) foreach (DestinationRecord destination in state.Destinations) destination.Enabled = false;
                var disk = new DiskSnapshot { Root = "F:\\", Label = "NIKON Z 30", Serial = "A1B2C3D4", Capacity = 63248007168, FreeSpace = 25248007168, FileSystem = "exFAT" };
                for (int i = 0; i < 767; i++) disk.Media.Add(new MediaItem { RelativePath = i < 12 ? "DCIM\\CLIP.MP4" : "DCIM\\PHOTO.NEF", Length = i < 12 ? 700000000 : 38000000 });
                if (!scenario.StartsWith("inserted"))
                {
                    disk.MarkerId = "11111111-2222-4333-8444-555555555555";
                    state.Cards.Add(new CardRecord { Id = disk.MarkerId, Name = "尼康 Z30 · 64GB", Capacity = disk.Capacity, DeleteMode = "Ask" });
                    disk.Plan = new CandidatePlan { Candidates = new List<MediaItem>(disk.Media) };
                }
                if (scenario.StartsWith("compared")) { disk.Plan.ExistingCount = 667; disk.Plan.Candidates = disk.Media.GetRange(0, 100); }
                if (scenario.StartsWith("synchronized")) { disk.Plan.ExistingCount = disk.Media.Count; disk.Plan.Candidates.Clear(); }
                if (scenario.StartsWith("comparison-error")) { disk.Plan = null; disk.ComparisonError = "目标对比未完成，请刷新重试。"; }
                if (scenario.StartsWith("comparing")) disk.Plan = null;
                if (scenario.StartsWith("excluded")) { state.ExcludedExtensions = new List<string> { ".dat", ".nef" }; disk.Plan.ExcludedCount = 755; disk.Plan.Candidates = disk.Media.GetRange(0, 12); }
                if (scenario.StartsWith("cards")) state.Cards.Add(new CardRecord { Id = "BF38B514-78B4-4FDD-82A2-331C19FAE432", Name = "备用 SD 卡", Capacity = 31902400512, AutoImport = false, DeleteMode = "Keep", LegacySerial = "AB23E477" });
                var disks = scenario.StartsWith("empty") ? new List<DiskSnapshot>() : new List<DiskSnapshot> { disk };
                string activity = "12:28:02  已识别 尼康 Z30 · F: · 767 个媒体文件\r\n12:28:10  用户确认导入，正在连接照片存储\r\n12:28:12  已复制并校验 DCIM\\100NZ_30\\DSC_2395.NEF\r\n12:28:16  已复制并校验 DCIM\\100NZ_30\\DSC_2395.JPG\r\n12:28:45  任务完成：上传并校验 767 个文件，保留卡内原文件。";
                using (var view = new FluentView())
                {
                    if (scenario.StartsWith("offline")) foreach (DestinationRecord destination in state.Destinations) view.LocationResults[destination.Id] = "不可用 · 服务器未连接，请检查网络。";
                    if (scenario.StartsWith("providers")) view.LocationResults[state.Destinations[0].Id] = "只读预检通过 · 可访问（尚未验证写入）";
                    string status = scenario.StartsWith("comparing") ? "对比内容 · 335 / 767" : scenario.StartsWith("copying-detail")
                        ? "已完成 24 / 401 · 运行 2 · 排队 375\n上传 · DSC_2395.NEF · 16.1 GB / 17.9 GB · 12 MB/s · 约 3 分钟\n校验 · DSC_2396.JPG · 4.1 MB / 8.5 MB"
                        : "导入 335 / 767 · DCIM\\100NZ_30\\DSC_2395.NEF";
                    view.Render(disks, "F:\\", state, scenario.StartsWith("copying") || scenario.StartsWith("comparing"), false, status, 437, activity,
                        scenario.StartsWith("complete") ? "已导入 767 个文件。原件已保留。" : "本次会话尚无导入任务", false, "NAS 照片库");
                    if (scenario.StartsWith("compared") && (view.PrimaryCaption != "导入 100 个文件" || view.PhotoCaption != "88")) throw new InvalidOperationException("UI counted already-uploaded media as candidates.");
                    if (scenario.StartsWith("synchronized") && view.PrimaryEnabled) throw new InvalidOperationException("UI permits an empty import batch.");
                    if (scenario.StartsWith("comparing") && (view.PrimaryEnabled || view.PhotoCaption != "—")) throw new InvalidOperationException("UI presents unverified raw files as candidates.");
                    if (scenario.StartsWith("excluded") && (view.PrimaryCaption != "导入 12 个文件" || view.PhotoCaption != "0")) throw new InvalidOperationException("UI counted excluded media as candidates.");
                    if (scenario.StartsWith("noavailable") && view.PrimaryCaption != "选择导入位置") throw new InvalidOperationException("UI presents disabled destinations as import targets.");
                    if (scenario.StartsWith("settings") && !view.ConcurrencyCaption.Contains("从 1 路开始")) throw new InvalidOperationException("UI misstates adaptive concurrency.");
                    if (scenario.StartsWith("activity")) view.PreviewPage("activity");
                    if (scenario.StartsWith("settings")) view.PreviewPage("settings");
                    if (scenario.StartsWith("locations") || scenario.StartsWith("providers") || scenario.StartsWith("offline")) view.PreviewPage("locations");
                    if (scenario.StartsWith("cards")) view.PreviewPage("cards");
                    view.RenderToFile(Path.Combine(folder, scenario + ".png"), scenario.EndsWith("small") ? 860 : 1020, scenario.EndsWith("small") ? 580 : 740, scenario.EndsWith("large-scale") ? 1.25 : 1);
                }
            }
            foreach (string mode in new[] { "light", "dark" })
            {
                FluentTheme.Mode = mode;
                var state = new StateStore(true);
                var disk = new DiskSnapshot { Root = "F:\\", Label = "NIKON Z 30", Serial = "A1B2C3D4", Capacity = 63248007168 };
                var card = new CardRecord { Id = "11111111-2222-4333-8444-555555555555", Name = "尼康 Z30 · 64GB", Capacity = disk.Capacity, LegacySerial = disk.Serial, NeedsBinding = true, DeleteMode = "Ask" };
                var files = PreviewFiles();
                var available = ProviderAvailability();
                state.State.Cards.Add(card);
                using (var dialog = new RegistrationDialog(state, disk)) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "register-" + mode + ".png"));
                using (var dialog = new PreferencesDialog(state, card)) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "preferences-" + mode + ".png"));
                using (var dialog = new ImportDialog(card, disk, files, available, "Next")) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "confirm-" + mode + ".png"));
                using (var dialog = new ImportDialog(card, disk, files, available, "Stop")) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "confirm-stop-" + mode + ".png"));
                using (var dialog = new ImportDialog(card, disk, files, available.Select(a => new LocationAvailability { Destination = a.Destination, Available = false, Detail = "服务器未连接，请检查网络。" }).ToList(), "Next")) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "confirm-noavailable-" + mode + ".png"));
                using (var dialog = new ChoiceDialog("清理卡内原件？", "已导入 767 个文件。\r\n可清理 767 个已上传原件。", "清理原件", "保留", "记住本次选择")) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "cleanup-" + mode + ".png"));
                using (var dialog = new LocationDialog(null)) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "location-add-" + mode + ".png"));
                using (var dialog = new LocationDialog(state.State.Destinations[0])) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "location-edit-" + mode + ".png"));
                foreach (DestinationRecord destination in ProviderDestinations())
                    using (var dialog = new LocationDialog(destination)) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "location-" + destination.Type + "-" + mode + ".png"));
                using (var dialog = new LocationDialog(ProviderDestinations()[0], delegate(DestinationRecord destination) { return Task.FromResult(new WorkResult { Success = true, Detail = "写入测试已通过" }); }))
                {
                    Click(dialog.PreviewWindow, "Location.Test");
                    RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "location-tested-" + mode + ".png"));
                }
                using (var dialog = new TransferSettingsDialog(state.State)) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "transfer-settings-" + mode + ".png"));
                using (var dialog = new ExclusionsDialog(MediaRules.DefaultExcluded())) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "exclusions-" + mode + ".png"));
            }
            return "PASS: " + cases.Length + " native WPF view states and 30 dialog/theme states rendered without SD, NAS, credential, preferences or task changes; candidate-count, selection, settings, async cancellation and action-gating assertions passed.";
        }
        static List<DestinationRecord> ProviderDestinations()
        {
            return new List<DestinationRecord> {
                StorageSettings.Normalize(new DestinationRecord { Id = "10000000-0000-4000-8000-000000000001", Name = "照片云盘", Type = "webdav", Endpoint = "https://dav.example/Photos/Camera", CredentialTarget = "PhotoImportV2/Remote/00000000000000000000000000000000" }),
                StorageSettings.Normalize(new DestinationRecord { Id = "10000000-0000-4000-8000-000000000002", Name = "对象存储归档", Type = "s3", Endpoint = "https://s3.example", Bucket = "camera-archive", Region = "us-east-1", Prefix = "photos/2026" }),
                StorageSettings.Normalize(new DestinationRecord { Id = "10000000-0000-4000-8000-000000000003", Name = "本地照片备份", Type = "folder", Path = @"C:\Photos\Camera" }),
                StorageSettings.Normalize(new DestinationRecord { Id = "10000000-0000-4000-8000-000000000004", Name = "网络共享", Type = "smb", Path = @"\\nas.example\Photos\Camera" })
            };
        }
        static List<LocationAvailability> ProviderAvailability()
        {
            return ProviderDestinations().Select(d => new LocationAvailability { Destination = d, Available = d.Type != "smb",
                Detail = d.Type == "smb" ? "网络共享未连接，无法访问。" : "只读预检通过 · 尚未验证写入", FreeBytes = d.Type == "folder" ? (long?)512000000000 : null }).ToList();
        }
        static List<MediaItem> PreviewFiles()
        {
            return new List<MediaItem> {
                new MediaItem { RelativePath = @"DCIM\100NZ_30\DSC_2395.NEF", Length = 38000000 },
                new MediaItem { RelativePath = @"DCIM\100NZ_30\DSC_2395.JPG", Length = 12000000 },
                new MediaItem { RelativePath = @"DCIM\100NZ_30\CLIP_001.MP4", Length = 700000000 }
            };
        }
        static void Click(Window window, string id)
        {
            Button button = NativeDialogControls.Find<Button>(window, id);
            if (button == null) throw new InvalidOperationException("Missing automation control: " + id);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        static void VerifyDialogBehavior()
        {
            var disk = new DiskSnapshot { Root = @"F:\", Label = "合成存储卡", Capacity = 64000000000 };
            var card = new CardRecord { Id = "11111111-2222-4333-8444-555555555555", Name = "合成存储卡" };
            var available = ProviderAvailability();
            using (var dialog = new ImportDialog(card, disk, PreviewFiles(), available, "Next"))
            {
                if (!dialog.StartEnabled || dialog.SelectedTargets.Count != 3 || dialog.SelectedTargets.Any(d => d.Type == "smb")) throw new InvalidOperationException("Import selection includes an unavailable location.");
                if (!NativeDialogControls.Find<Button>(dialog.PreviewWindow, "Dialog.Cancel").IsDefault) throw new InvalidOperationException("Import confirmation defaults to starting.");
                var picker = NativeDialogControls.Find<ComboBox>(dialog.PreviewWindow, "Import.Target");
                if (((ComboBoxItem)picker.Items[3]).IsEnabled) throw new InvalidOperationException("Unavailable location remains selectable.");
                picker.SelectedIndex = 1;
                if (dialog.SelectedTargets[0].Type != "s3" || dialog.SelectedTargets[1].Type != "webdav") throw new InvalidOperationException("Approved fallback order differs from the displayed order.");
                var fallback = NativeDialogControls.Find<CheckBox>(dialog.PreviewWindow, "Import.Fallback." + available[0].Destination.Id);
                fallback.IsChecked = false; fallback.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                if (dialog.SelectedTargets.Count != 2) throw new InvalidOperationException("Unapproved fallback remains selected.");
                dialog.SelectedTargets[0].Name = "mutated snapshot";
                if (dialog.SelectedTargets[0].Name == "mutated snapshot") throw new InvalidOperationException("Confirmation leaks mutable target snapshots.");
                if (NativeDialogControls.Find<TextBlock>(dialog.PreviewWindow, "Import.TargetStatus").Text.Contains("可写")) throw new InvalidOperationException("Read-only preflight claims write permission.");
                NativeDialogControls.Find<CheckBox>(dialog.PreviewWindow, "Import.Remember").IsChecked = true;
                NativeDialogControls.Find<CheckBox>(dialog.PreviewWindow, "Import.MakeDefault").IsChecked = true;
                Click(dialog.PreviewWindow, "Dialog.Cancel");
                if (dialog.Remember || dialog.MakeDefault || card.AutoImport) throw new InvalidOperationException("Canceled import changes preferences.");
            }
            using (var dialog = new ImportDialog(card, disk, PreviewFiles(), available, "Stop"))
                if (!dialog.StartEnabled || dialog.SelectedTargets.Count != 1) throw new InvalidOperationException("Stop policy approves fallback locations.");
            using (var dialog = new ImportDialog(card, disk, PreviewFiles(), available.Select(a => new LocationAvailability { Destination = a.Destination, Available = false, Detail = "offline" }).ToList(), "Next"))
                if (dialog.StartEnabled || dialog.SelectedTargets.Count != 0) throw new InvalidOperationException("No-available-location import can start.");
            using (var dialog = new ImportDialog(card, disk, new List<MediaItem>(), available, "Next"))
                if (dialog.StartEnabled) throw new InvalidOperationException("Empty import can start.");

            var settings = new AppState();
            using (var dialog = new TransferSettingsDialog(settings))
            {
                if (dialog.MaxParallel != 2 || dialog.IdleTimeoutSeconds != 180 || !dialog.AutoParallel || dialog.LargeFileBytes != 256L * 1024 * 1024) throw new InvalidOperationException("Transfer defaults differ from the controller.");
                NativeDialogControls.Find<TextBox>(dialog.PreviewWindow, "TransferSettings.IdleTimeoutSeconds").Text = "29";
                Click(dialog.PreviewWindow, "TransferSettings.Save");
                if (NativeDialogControls.Find<TextBlock>(dialog.PreviewWindow, "TransferSettings.Error").Visibility != Visibility.Visible) throw new InvalidOperationException("Invalid timeout is accepted.");
                NativeDialogControls.Find<TextBox>(dialog.PreviewWindow, "TransferSettings.IdleTimeoutSeconds").Text = "1800";
                NativeDialogControls.Find<ComboBox>(dialog.PreviewWindow, "TransferSettings.MaxParallel").SelectedIndex = 3;
                Click(dialog.PreviewWindow, "TransferSettings.Save");
                if (dialog.MaxParallel != 4 || dialog.IdleTimeoutSeconds != 1800 || settings.MaxParallel.HasValue) throw new InvalidOperationException("Transfer dialog mutates application state or loses settings.");
            }
            DestinationRecord saved = ProviderDestinations()[0];
            using (var dialog = new LocationDialog(saved))
            {
                if (NativeDialogControls.Find<Button>(dialog.PreviewWindow, "Location.Test").IsEnabled) throw new InvalidOperationException("Headless preview can invoke a missing callback.");
                Click(dialog.PreviewWindow, "Location.Save");
                if (dialog.Destination == null || dialog.Destination.CredentialTarget != saved.CredentialTarget || Object.ReferenceEquals(dialog.Destination, saved)) throw new InvalidOperationException("Editing clears saved credentials or mutates the original.");
            }
            int calls = 0; var pending = new TaskCompletionSource<WorkResult>();
            using (var dialog = new LocationDialog(saved, delegate(DestinationRecord destination) { calls++; return pending.Task; }))
            {
                Click(dialog.PreviewWindow, "Location.Test"); Click(dialog.PreviewWindow, "Location.Test");
                if (calls != 1 || !dialog.IsTesting || NativeDialogControls.Find<Button>(dialog.PreviewWindow, "Location.Save").IsEnabled || !NativeDialogControls.Find<Button>(dialog.PreviewWindow, "Dialog.Cancel").IsEnabled) throw new InvalidOperationException("Connection test is reentrant or blocks cancel.");
                Click(dialog.PreviewWindow, "Dialog.Cancel"); pending.SetResult(new WorkResult { Success = true });
                if (dialog.Destination != null) throw new InvalidOperationException("Canceled connection test saves a destination.");
            }
            using (var dialog = new LocationDialog(saved, delegate(DestinationRecord destination) { calls++; return Task.FromResult(new WorkResult { Success = true }); }))
            {
                NativeDialogControls.Find<PasswordBox>(dialog.PreviewWindow, "Location.Password").Password = "synthetic-unsaved-secret";
                Click(dialog.PreviewWindow, "Location.Test");
                if (calls != 1 || NativeDialogControls.Find<TextBlock>(dialog.PreviewWindow, "Location.Error").Visibility != Visibility.Visible) throw new InvalidOperationException("Unsaved credentials are persisted or silently ignored by connection testing.");
            }
        }
        static void RenderDialog(Window window, string path)
        {
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var root = new Border { Child = content, Width = window.Width };
            root.Resources.MergedDictionaries.Add(window.Resources);
            root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            TextElementSet(root, window);
            root.Measure(new Size(window.Width, Double.PositiveInfinity));
            int height = (int)Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, window.Width, height)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.Width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) encoder.Save(stream);
        }
        static void TextElementSet(Border root, Window window)
        {
            System.Windows.Documents.TextElement.SetFontFamily(root, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(root, window.FontSize);
            System.Windows.Documents.TextElement.SetForeground(root, window.Foreground);
        }
    }
}
