using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace PhotoImportV2
{
    public sealed class OrganizationDialog : IDisposable
    {
        private readonly DialogSurface surface;
        private readonly ComboBox mode, layout, missing, timeZone;
        private readonly CheckBox separate, videoUtc;
        private readonly StackPanel dateFields, missingFields;
        private readonly Expander advanced;
        private readonly TextBlock summary, examples, missingExample, error;
        private OrganizationOptions selected;
        public OrganizationOptions SelectedOptions { get { return selected == null ? null : Snapshot(selected); } }
        public Window PreviewWindow { get { return surface.Window; } }
        internal Forms.DialogResult PreviewResult { get { return surface.Result; } }

        public OrganizationDialog(OrganizationOptions current)
        {
            OrganizationOptions initial = Snapshot(current);
            surface = new DialogSurface("文件整理", "更改将在下次导入生效。", Forms.DialogResult.Cancel, 620);
            AutomationProperties.SetAutomationId(surface.Window, "Organization.Dialog");
            mode = Picker("整理方式", "Organization.Mode",
                new[] { "capture", "import", "preserve", "flat" },
                new[] { "按拍摄日期", "按导入日期", "保留卡内目录", "直接存入目标" }, initial.Mode);
            AddField(surface.Body, "整理方式(_M)", mode, 0);
            dateFields = new StackPanel();
            layout = Picker("日期目录", "Organization.DateLayout", new[] { "day", "year", "month" },
                new[] { "按天 · yyyy-MM-dd", "按年 · yyyy/yyyy-MM-dd", "年 / 月 · yyyy/MM/yyyy-MM-dd" }, initial.DateLayout);
            AddField(dateFields, "日期目录(_D)", layout, 16); surface.Body.Children.Add(dateFields);
            separate = DialogSurface.Check("RAW 与 JPG 分目录(_R)", "Organization.SeparateMedia");
            separate.IsChecked = initial.SeparateMedia; separate.Margin = new Thickness(0, 18, 0, 0);
            surface.Body.Children.Add(separate);
            var categories = DialogSurface.Text("按类型分入 RAW、JPG、VIDEO、TIFF；保留原文件名。", "Caption");
            categories.Margin = new Thickness(30, 6, 0, 0); surface.Body.Children.Add(categories);
            missingFields = new StackPanel();
            missing = Picker("无拍摄时间时", "Organization.MissingDate", new[] { "unknown", "modified" },
                new[] { "放入未知日期（默认）", "使用文件修改日期" }, initial.MissingDate);
            AddField(missingFields, "无拍摄时间时(_F)", missing, 16); surface.Body.Children.Add(missingFields);
            var advancedBody = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            videoUtc = DialogSurface.Check("视频整数时间戳按 UTC 解读(_U)", "Organization.VideoUtc");
            videoUtc.IsChecked = initial.VideoTimeMode == "utc"; advancedBody.Children.Add(videoUtc);
            timeZone = new ComboBox { IsEditable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                SelectedValuePath = "Id", MaxDropDownHeight = 280 };
            foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones()) timeZone.Items.Add(zone);
            timeZone.SelectedValue = initial.TimeZoneId;
            if (timeZone.SelectedItem == null) { timeZone.Items.Add(TimeZoneInfo.FindSystemTimeZoneById(initial.TimeZoneId)); timeZone.SelectedValue = initial.TimeZoneId; }
            AutomationProperties.SetName(timeZone, "视频 UTC 与修改日期的时区"); AutomationProperties.SetAutomationId(timeZone, "Organization.TimeZone");
            DialogSurface.WrapItems(timeZone, "DisplayName", surface.Window.Width - 128);
            AddField(advancedBody, "时区(_Z)", timeZone, 14);
            advanced = new Expander { Header = "高级", Content = advancedBody, Margin = new Thickness(0, 14, 0, 0) };
            advanced.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            AutomationProperties.SetAutomationId(advanced, "Organization.Advanced"); surface.Body.Children.Add(advanced);
            var preview = new StackPanel();
            var heading = DialogSurface.Text("路径示例 · 保留原文件名", "Body"); heading.FontWeight = FontWeights.SemiBold; preview.Children.Add(heading);
            summary = DialogSurface.Text("", "Caption"); summary.Margin = new Thickness(0, 6, 0, 0);
            AutomationProperties.SetAutomationId(summary, "Organization.Summary"); preview.Children.Add(summary);
            examples = DialogSurface.Text("", "Body"); examples.Margin = new Thickness(0, 12, 0, 0);
            AutomationProperties.SetAutomationId(examples, "Organization.Examples"); preview.Children.Add(examples);
            missingExample = DialogSurface.Text("", "Caption"); missingExample.Margin = new Thickness(0, 8, 0, 0);
            AutomationProperties.SetAutomationId(missingExample, "Organization.MissingExample"); preview.Children.Add(missingExample);
            var previewCard = DialogSurface.Card(preview); previewCard.Margin = new Thickness(0, 18, 0, 0); surface.Body.Children.Add(previewCard);
            error = DialogSurface.Error("Organization.Error"); surface.Body.Children.Add(error);
            surface.Footer("保存(_S)", Save, "取消", false, null);
            AutomationProperties.SetAutomationId(NativeDialogControls.Find<Button>(surface.Window, "Dialog.Confirm"), "Organization.Save");
            mode.SelectionChanged += delegate { UpdatePreview(); }; layout.SelectionChanged += delegate { UpdatePreview(); };
            missing.SelectionChanged += delegate { UpdatePreview(); };
            timeZone.SelectionChanged += delegate { UpdatePreview(); };
            separate.Checked += delegate { UpdatePreview(); }; separate.Unchecked += delegate { UpdatePreview(); };
            videoUtc.Checked += delegate { UpdatePreview(); }; videoUtc.Unchecked += delegate { UpdatePreview(); };
            UpdatePreview();
        }

        private OrganizationOptions ReadOptions()
        {
            var zone = timeZone.SelectedItem as TimeZoneInfo;
            if (zone == null) throw new ArgumentException("请选择有效的时区。");
            return OrganizationSettings.Normalize(new OrganizationOptions {
                Mode = Choice(mode), DateLayout = Choice(layout), MissingDate = Choice(missing),
                SeparateMedia = separate.IsChecked == true, VideoTimeMode = videoUtc.IsChecked == true ? "utc" : "camera", TimeZoneId = zone.Id });
        }
        private void UpdatePreview()
        {
            string value = mode.SelectedValue as string;
            dateFields.Visibility = value == "capture" || value == "import" ? Visibility.Visible : Visibility.Collapsed;
            missingFields.Visibility = advanced.Visibility = value == "capture" ? Visibility.Visible : Visibility.Collapsed;
            try
            {
                OrganizationOptions options = ReadOptions();
                summary.Text = OrganizationSettings.Summary(options);
                examples.Text = Example("DSC_2395.NEF", options, true) + "\r\n" + Example("DSC_2395.JPG", options, true);
                missingExample.Text = "无拍摄时间：" + Example("CLIP_001.MP4", options, false);
                missingExample.Visibility = options.Mode == "capture" ? Visibility.Visible : Visibility.Collapsed;
                error.Visibility = Visibility.Collapsed;
            }
            catch (ArgumentException problem) { Invalid(problem.Message); }
        }
        private static string Example(string name, OrganizationOptions options, bool hasCapture)
        {
            var file = new MediaItem { RelativePath = "DCIM/100NZ_30/" + name,
                WriteTicks = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc).Ticks };
            OrganizationSettings.Apply(file, options, "2026-10-11",
                new CaptureInfo { Taken = hasCapture ? (DateTime?)new DateTime(2026, 9, 21, 14, 30, 0) : null, Source = hasCapture ? "示例拍摄时间" : "未知" });
            return "目标 / " + OrganizationSettings.Target(file).Replace('\\', '/');
        }
        private void Save()
        {
            try { selected = ReadOptions(); surface.Complete(Forms.DialogResult.OK); }
            catch (ArgumentException problem) { Invalid(problem.Message); }
        }
        private void Invalid(string message)
        {
            error.Text = message; error.Visibility = Visibility.Visible;
            examples.Text = "请选择有效的整理方式与日期选项。"; missingExample.Visibility = Visibility.Collapsed;
        }
        private static string Choice(ComboBox picker)
        {
            var item = picker.SelectedItem as ComboBoxItem;
            if (item == null || !(item.Tag is string)) throw new ArgumentException("请选择有效的整理选项。");
            return (string)item.Tag;
        }
        private static ComboBox Picker(string name, string id, string[] values, string[] labels, string selectedValue)
        {
            var picker = new ComboBox { IsEditable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, SelectedValuePath = "Tag" };
            for (int i = 0; i < values.Length; i++) picker.Items.Add(new ComboBoxItem { Content = labels[i], Tag = values[i] });
            picker.SelectedValue = selectedValue;
            AutomationProperties.SetName(picker, name); AutomationProperties.SetAutomationId(picker, id); return picker;
        }
        private static void AddField(StackPanel panel, string label, Control input, double margin)
        { panel.Children.Add(DialogSurface.Field(label, input, margin)); panel.Children.Add(input); }
        private static OrganizationOptions Snapshot(OrganizationOptions options)
        {
            return OrganizationSettings.Normalize(options == null ? null : new OrganizationOptions {
                Mode = options.Mode, DateLayout = options.DateLayout, MissingDate = options.MissingDate,
                SeparateMedia = options.SeparateMedia, VideoTimeMode = options.VideoTimeMode, TimeZoneId = options.TimeZoneId });
        }
        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }
}
