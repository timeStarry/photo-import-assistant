using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace PhotoImportV2
{
    public sealed class ImportDialog : IDisposable
    {
        private readonly DialogSurface surface;
        private readonly ComboBox target;
        private readonly StackPanel selectedDetails, fallbacks;
        private readonly TextBlock order;
        private readonly CheckBox remember, makeDefault;
        private readonly Button start;
        private readonly List<LocationAvailability> locations;
        private readonly Dictionary<LocationAvailability, CheckBox> fallbackChecks = new Dictionary<LocationAvailability, CheckBox>();
        private readonly bool allowFallback;
        private readonly bool hasCard;
        private readonly int fileCount;
        public IList<DestinationRecord> SelectedTargets { get { return Approved().Select(a => ImportSettings.Clone(a.Destination)).ToList(); } }
        public bool Remember { get { return surface.Result == Forms.DialogResult.Yes && remember.IsChecked == true; } }
        public bool MakeDefault { get { return surface.Result == Forms.DialogResult.Yes && makeDefault.IsChecked == true; } }
        internal Window PreviewWindow { get { return surface.Window; } }
        internal bool StartEnabled { get { return start.IsEnabled; } }

        public ImportDialog(CardRecord card, DiskSnapshot disk, IList<MediaItem> files, IList<LocationAvailability> availability, string failurePolicy)
        {
            if (disk == null) throw new ArgumentNullException("disk");
            if (files == null) throw new ArgumentNullException("files");
            if (availability == null) throw new ArgumentNullException("availability");
            if (failurePolicy != "Next" && failurePolicy != "Stop") throw new ArgumentException("位置不可用时的处理方式无效。", "failurePolicy");
            allowFallback = failurePolicy == "Next";
            hasCard = card != null;
            fileCount = files.Count(f => f != null);
            locations = availability.Where(a => a != null && a.Destination != null).Select(a => new LocationAvailability {
                Destination = ImportSettings.Clone(a.Destination), Available = a.Available && a.Destination.Enabled,
                Detail = !a.Destination.Enabled ? "位置已停用" : a.Detail, FreeBytes = a.FreeBytes }).ToList();
            surface = new DialogSurface("确认导入", "确认本次保存位置与备用顺序。", Forms.DialogResult.Cancel, 680);
            AutomationProperties.SetAutomationId(surface.Window, "Import.Dialog");
            var source = new StackPanel();
            var sourceName = DialogSurface.Text(card == null ? DialogSurface.Known(disk.Label) : DialogSurface.Known(card.Name), "Body");
            sourceName.FontWeight = FontWeights.SemiBold; source.Children.Add(sourceName);
            source.Children.Add(DialogSurface.Text((disk.Root ?? "未知盘符").TrimEnd('\\') + " · " + DialogSurface.Capacity(disk.Capacity), "Caption"));
            var counts = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            for (int i = 0; i < 3; i++) counts.ColumnDefinitions.Add(new ColumnDefinition());
            int videos = files.Count(f => f != null && FluentView.Video(f));
            AddCount(counts, 0, "照片", (fileCount - videos).ToString("N0"), "Import.PhotoCount");
            AddCount(counts, 1, "视频", videos.ToString("N0"), "Import.VideoCount");
            AddCount(counts, 2, "共 " + fileCount.ToString("N0") + " 个文件", FluentView.SizeText(files.Where(f => f != null).Sum(f => Math.Max(0, f.Length))), "Import.SourceBytes");
            source.Children.Add(counts); surface.Body.Children.Add(DialogSurface.Card(source));

            target = new ComboBox { IsEditable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 280 };
            AutomationProperties.SetName(target, "本次首选位置"); AutomationProperties.SetAutomationId(target, "Import.Target");
            surface.Body.Children.Add(DialogSurface.Field("本次首选位置(_T)", target, 20)); surface.Body.Children.Add(target);
            foreach (LocationAvailability location in locations)
            {
                var item = new ComboBoxItem { Content = location.Destination.Name + " · " + StorageSettings.TypeName(location.Destination) + (location.Available ? "" : " · 不可用"),
                    Tag = location, IsEnabled = location.Available, Opacity = location.Available ? 1 : .55 };
                AutomationProperties.SetName(item, location.Destination.Name + (location.Available ? "，可访问" : "，不可用，" + Reason(location)));
                AutomationProperties.SetAutomationId(item, "Import.Target." + location.Destination.Id);
                item.ToolTip = location.Available ? ImportSettings.Address(location.Destination) : Reason(location); target.Items.Add(item);
            }
            selectedDetails = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; surface.Body.Children.Add(selectedDetails);
            fallbacks = new StackPanel { Margin = new Thickness(0, 16, 0, 0) }; surface.Body.Children.Add(fallbacks);
            order = DialogSurface.Text("", "Caption"); order.Margin = new Thickness(0, 12, 0, 0);
            AutomationProperties.SetAutomationId(order, "Import.ApprovedOrder"); surface.Body.Children.Add(order);
            var preflightNote = DialogSurface.Text("预检仅确认可访问；写入能力以实际传输为准。", "Caption");
            preflightNote.Margin = new Thickness(0, 8, 0, 0); surface.Body.Children.Add(preflightNote);
            var retainedNote = DialogSurface.Text("本地、WebDAV 和 S3 副本保留卡内原件", "Caption");
            retainedNote.Margin = new Thickness(0, 8, 0, 0); surface.Body.Children.Add(retainedNote);
            var unavailable = locations.Where(a => !a.Available).ToList();
            if (unavailable.Count > 0)
            {
                var panel = new StackPanel(); var heading = DialogSurface.Text("不可用的位置", "Body"); heading.FontWeight = FontWeights.SemiBold; panel.Children.Add(heading);
                foreach (LocationAvailability location in unavailable)
                {
                    var disabled = DialogSurface.Check(location.Destination.Name + " · " + StorageSettings.TypeName(location.Destination), "Import.Unavailable." + location.Destination.Id);
                    disabled.IsEnabled = false; disabled.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(disabled);
                    var reason = DialogSurface.Text(Reason(location), "Caption"); reason.Margin = new Thickness(30, 4, 0, 0);
                    reason.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush"); panel.Children.Add(reason);
                }
                var unavailableCard = DialogSurface.Card(panel); unavailableCard.Margin = new Thickness(0, 16, 0, 0); surface.Body.Children.Add(unavailableCard);
            }
            remember = DialogSurface.Check("以后自动导入此卡(_A)", "Import.Remember"); remember.Margin = new Thickness(0, 20, 0, 0);
            remember.IsChecked = card != null && card.AutoImport; remember.IsEnabled = card != null; surface.Body.Children.Add(remember);
            makeDefault = DialogSurface.Check("将本次位置顺序设为默认(_D)", "Import.MakeDefault"); makeDefault.Margin = new Thickness(0, 12, 0, 0); surface.Body.Children.Add(makeDefault);
            surface.Footer("开始导入(_I)", delegate { if (start.IsEnabled && Approved().Count > 0) surface.Complete(Forms.DialogResult.Yes); }, "取消", false, null);
            start = NativeDialogControls.Find<Button>(surface.Window, "Dialog.Confirm"); AutomationProperties.SetAutomationId(start, "Import.Start");
            target.SelectionChanged += delegate { UpdateSelection(); };
            for (int i = 0; i < locations.Count; i++) if (locations[i].Available) { target.SelectedIndex = i; break; }
            if (!locations.Any(a => a.Available))
            {
                target.Items.Add(new ComboBoxItem { Content = "没有可用位置", IsEnabled = false }); target.SelectedIndex = target.Items.Count - 1;
            }
            UpdateSelection();
        }

        private LocationAvailability Primary { get { var item = target.SelectedItem as ComboBoxItem; return item == null || !item.IsEnabled ? null : item.Tag as LocationAvailability; } }
        private List<LocationAvailability> Approved()
        {
            var approved = new List<LocationAvailability>(); LocationAvailability primary = Primary;
            if (primary == null || !primary.Available) return approved;
            approved.Add(primary);
            if (allowFallback) foreach (LocationAvailability location in locations)
            {
                CheckBox check;
                if (location != primary && location.Available && fallbackChecks.TryGetValue(location, out check) && check.IsChecked == true) approved.Add(location);
            }
            return approved;
        }
        private void UpdateSelection()
        {
            LocationAvailability primary = Primary;
            selectedDetails.Children.Clear(); fallbacks.Children.Clear();
            if (primary == null)
            {
                var hint = DialogSurface.Text("没有可用的导入位置。请取消后检查连接或添加位置。", "Body");
                hint.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush"); selectedDetails.Children.Add(hint);
            }
            else
            {
                var details = new StackPanel(); var name = DialogSurface.Text(primary.Destination.Name, "Body"); name.FontWeight = FontWeights.SemiBold; details.Children.Add(name);
                details.Children.Add(DialogSurface.Text(StorageSettings.TypeName(primary.Destination) + " · " + ImportSettings.Address(primary.Destination), "Caption"));
                var status = DialogSurface.Text("可访问 · 本次未测试写入" + (primary.FreeBytes.HasValue ? " · 剩余 " + FluentView.SizeText(Math.Max(0, primary.FreeBytes.Value)) : ""), "Caption");
                status.ToolTip = primary.Detail;
                AutomationProperties.SetAutomationId(status, "Import.TargetStatus");
                status.Margin = new Thickness(0, 6, 0, 0); status.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush"); details.Children.Add(status); selectedDetails.Children.Add(DialogSurface.Card(details));
                var backupLocations = locations.Where(a => a.Available && a != primary).ToList();
                if (allowFallback && backupLocations.Count > 0)
                {
                    var heading = DialogSurface.Text("允许使用的备用位置（按以下顺序）", "Body"); heading.FontWeight = FontWeights.SemiBold; fallbacks.Children.Add(heading);
                    foreach (LocationAvailability location in backupLocations)
                    {
                        CheckBox check;
                        if (!fallbackChecks.TryGetValue(location, out check))
                        {
                            check = DialogSurface.Check(location.Destination.Name + " · " + StorageSettings.TypeName(location.Destination), "Import.Fallback." + location.Destination.Id);
                            check.IsChecked = true; check.Click += delegate { UpdateOrder(); }; fallbackChecks.Add(location, check);
                        }
                        check.Margin = new Thickness(0, 12, 0, 0); fallbacks.Children.Add(check);
                        var facts = DialogSurface.Text(ImportSettings.Address(location.Destination) + "\r\n可访问 · 本次未测试写入", "Caption");
                        facts.ToolTip = location.Detail;
                        facts.Margin = new Thickness(30, 4, 0, 0); fallbacks.Children.Add(facts);
                    }
                }
            }
            target.IsEnabled = locations.Any(a => a.Available); remember.IsEnabled = hasCard && primary != null;
            makeDefault.IsEnabled = primary != null; start.IsEnabled = primary != null && fileCount > 0;
            UpdateOrder();
        }
        private void UpdateOrder()
        {
            List<LocationAvailability> approved = Approved();
            order.Text = approved.Count == 0 ? "未批准任何位置。" : "本次顺序：" + String.Join(" → ", approved.Select(a => a.Destination.Name)) +
                (allowFallback && approved.Count > 1 ? "。仅在前一位置失败时尝试下一位置。" : "。不可用时停止导入。");
        }
        private static string Reason(LocationAvailability location) { return String.IsNullOrWhiteSpace(location.Detail) ? "连接未通过，请检查地址、权限或网络。" : location.Detail; }
        private static void AddCount(Grid grid, int column, string label, string value, string id)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; panel.Children.Add(DialogSurface.Text(label, "Caption"));
            var count = DialogSurface.Text(value, "Body"); count.FontSize = 22; count.FontWeight = FontWeights.SemiBold;
            AutomationProperties.SetAutomationId(count, id); panel.Children.Add(count); Grid.SetColumn(panel, column); grid.Children.Add(panel);
        }
        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }

    internal static class NativeDialogControls
    {
        internal static T Find<T>(DependencyObject root, string id) where T : DependencyObject
        {
            if (root is T && AutomationProperties.GetAutomationId(root) == id) return (T)root;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var element = child as DependencyObject;
                if (element == null) continue;
                T found = Find<T>(element, id); if (found != null) return found;
            }
            return null;
        }
    }
}
