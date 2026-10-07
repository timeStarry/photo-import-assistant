using System;
using System.Collections.Generic;
using System.IO;
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
            string[] cases = { "inserted-light", "ready-light", "copying-light", "complete-light", "empty-light", "ready-dark", "activity-light", "settings-dark", "settings-light", "locations-light", "locations-dark", "cards-light", "cards-dark", "ready-small", "ready-large-scale" };
            foreach (string scenario in cases)
            {
                var state = new AppState { Theme = scenario.EndsWith("dark") ? "dark" : "light", StartAtLogin = true, Destinations = DemoData.Destinations() };
                var disk = new DiskSnapshot { Root = "F:\\", Label = "NIKON Z 30", Serial = "A1B2C3D4", Capacity = 63248007168, FreeSpace = 25248007168, FileSystem = "exFAT" };
                for (int i = 0; i < 767; i++) disk.Media.Add(new MediaItem { RelativePath = i < 12 ? "DCIM\\CLIP.MP4" : "DCIM\\PHOTO.NEF", Length = i < 12 ? 700000000 : 38000000 });
                if (!scenario.StartsWith("inserted"))
                {
                    disk.MarkerId = "11111111-2222-4333-8444-555555555555";
                    state.Cards.Add(new CardRecord { Id = disk.MarkerId, Name = "尼康 Z30 · 64GB", Capacity = disk.Capacity, DeleteMode = "Ask" });
                }
                if (scenario.StartsWith("cards")) state.Cards.Add(new CardRecord { Id = "BF38B514-78B4-4FDD-82A2-331C19FAE432", Name = "备用 SD 卡", Capacity = 31902400512, AutoImport = false, DeleteMode = "Keep", LegacySerial = "AB23E477" });
                var disks = scenario.StartsWith("empty") ? new List<DiskSnapshot>() : new List<DiskSnapshot> { disk };
                string activity = "12:28:02  已识别 尼康 Z30 · F: · 767 个媒体文件\r\n12:28:10  用户确认导入，正在连接照片存储\r\n12:28:12  已复制并校验 DCIM\\100NZ_30\\DSC_2395.NEF\r\n12:28:16  已复制并校验 DCIM\\100NZ_30\\DSC_2395.JPG\r\n12:28:45  任务完成：上传并校验 767 个文件，保留卡内原文件。";
                using (var view = new FluentView())
                {
                    view.Render(disks, "F:\\", state, scenario.StartsWith("copying"), false, "导入 335 / 767 · DCIM\\100NZ_30\\DSC_2395.NEF", 437, activity,
                        scenario.StartsWith("complete") ? "已导入 767 个文件。原件已保留。" : "本次会话尚无导入任务", false, "NAS 照片库");
                    if (scenario.StartsWith("activity")) view.PreviewPage("activity");
                    if (scenario.StartsWith("settings")) view.PreviewPage("settings");
                    if (scenario.StartsWith("locations")) view.PreviewPage("locations");
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
                state.State.Cards.Add(card);
                using (var dialog = new RegistrationDialog(state, disk)) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "register-" + mode + ".png"));
                using (var dialog = new PreferencesDialog(state, card)) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "preferences-" + mode + ".png"));
                using (var dialog = new ChoiceDialog("导入这些文件？", "尼康 Z30 · F:\r\n767 个文件 · 37.1 GB\r\n保存到 NAS 照片库\r\nhttps://nas.example/Photos/Camera\r\n不可用时 → 本地备份", "开始导入", "跳过", "以后自动导入此卡")) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "confirm-" + mode + ".png"));
                using (var dialog = new ChoiceDialog("清理卡内原件？", "已导入 767 个文件。\r\n可清理 767 个已上传原件。", "清理原件", "保留", "记住本次选择")) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "cleanup-" + mode + ".png"));
                using (var dialog = new LocationDialog(null)) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "location-add-" + mode + ".png"));
                using (var dialog = new LocationDialog(state.State.Destinations[0])) RenderDialog(dialog.PreviewWindow, Path.Combine(folder, "location-edit-" + mode + ".png"));
            }
            return "PASS: " + cases.Length + " native WPF view states and 12 dialog/theme states rendered without SD, NAS, preferences or task changes.";
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
