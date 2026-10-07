using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace PhotoImportV2
{
    public sealed class TransferSettingsDialog : IDisposable
    {
        private readonly DialogSurface surface;
        private readonly ComboBox parallel;
        private readonly CheckBox automatic;
        private readonly TextBox timeout, threshold;
        private readonly TextBlock error;
        public int MaxParallel { get; private set; }
        public int IdleTimeoutSeconds { get; private set; }
        public bool AutoParallel { get; private set; }
        public long LargeFileBytes { get; private set; }
        internal Window PreviewWindow { get { return surface.Window; } }

        public TransferSettingsDialog(AppState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            MaxParallel = state.MaxParallel ?? 2; IdleTimeoutSeconds = state.IdleTimeoutSeconds ?? 180;
            AutoParallel = state.AutoParallel != false; LargeFileBytes = state.LargeFileBytes ?? 256L * 1024 * 1024;
            surface = new DialogSurface("传输设置", "更改将在下次导入生效。", Forms.DialogResult.Cancel, 600);
            AutomationProperties.SetAutomationId(surface.Window, "TransferSettings.Dialog");
            parallel = new ComboBox { IsEditable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            for (int i = 1; i <= 4; i++) parallel.Items.Add(i + " 个文件");
            parallel.SelectedIndex = Math.Max(0, Math.Min(3, MaxParallel - 1));
            AutomationProperties.SetName(parallel, "最多同时传输的文件数"); AutomationProperties.SetAutomationId(parallel, "TransferSettings.MaxParallel");
            surface.Body.Children.Add(DialogSurface.Field("最大并发数(_P)", parallel, 0)); surface.Body.Children.Add(parallel);
            automatic = DialogSurface.Check("自动调整并发数(_A)", "TransferSettings.AutoParallel");
            automatic.IsChecked = AutoParallel; automatic.Margin = new Thickness(0, 16, 0, 0); surface.Body.Children.Add(automatic);
            AddHint("自动模式从 1 路开始，逐步探测到所选上限（最多 4 路）；固定模式按所选并发数传输。");
            timeout = Number(IdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture), "无进展超时（秒）", "TransferSettings.IdleTimeoutSeconds");
            surface.Body.Children.Add(DialogSurface.Field("无进展超时（秒）(_T)", timeout, 20)); surface.Body.Children.Add(timeout);
            AddHint("30–1800 秒，默认 180 秒；持续没有进展时结束当前传输。");
            threshold = Number((LargeFileBytes / (1024d * 1024)).ToString("0.##########", CultureInfo.InvariantCulture), "大文件阈值（MiB）", "TransferSettings.LargeFileBytes");
            surface.Body.Children.Add(DialogSurface.Field("大文件阈值（MiB）(_L)", threshold, 20)); surface.Body.Children.Add(threshold);
            AddHint("默认 256 MiB，范围 16–16384 MiB。小文件优先，每处理 3 个小文件后安排 1 个大文件。");
            error = DialogSurface.Error("TransferSettings.Error"); surface.Body.Children.Add(error);
            timeout.TextChanged += delegate { error.Visibility = Visibility.Collapsed; }; threshold.TextChanged += delegate { error.Visibility = Visibility.Collapsed; };
            surface.Footer("保存(_S)", Save, "取消", false, null);
            AutomationProperties.SetAutomationId(NativeDialogControls.Find<Button>(surface.Window, "Dialog.Confirm"), "TransferSettings.Save");
        }
        private void Save()
        {
            int seconds; decimal mib;
            if (!Int32.TryParse(timeout.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 30 || seconds > 1800)
            { Invalid("超时须为 30–1800 秒的整数。", timeout); return; }
            if (!Decimal.TryParse(threshold.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out mib) || mib < 16 || mib > 16384)
            { Invalid("大文件阈值须为 16–16384 MiB。", threshold); return; }
            MaxParallel = parallel.SelectedIndex + 1; IdleTimeoutSeconds = seconds;
            AutoParallel = automatic.IsChecked == true; LargeFileBytes = (long)Decimal.Round(mib * 1024 * 1024, 0, MidpointRounding.AwayFromZero);
            surface.Complete(Forms.DialogResult.OK);
        }
        private void Invalid(string message, TextBox field) { error.Text = message; error.Visibility = Visibility.Visible; field.Focus(); field.SelectAll(); }
        private void AddHint(string value) { var hint = DialogSurface.Text(value, "Caption"); hint.Margin = new Thickness(0, 8, 0, 0); surface.Body.Children.Add(hint); }
        private static TextBox Number(string value, string name, string id)
        {
            var input = new TextBox { Text = value, MaxLength = 24, AcceptsReturn = false };
            AutomationProperties.SetName(input, name); AutomationProperties.SetAutomationId(input, id); return input;
        }
        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }
}
