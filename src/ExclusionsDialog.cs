using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace PhotoImportV2
{
    public sealed class ExclusionsDialog : IDisposable
    {
        readonly DialogSurface surface;
        public List<string> Excluded { get; private set; }
        internal Window PreviewWindow { get { return surface.Window; } }
        public ExclusionsDialog(IEnumerable<string> current)
        {
            surface = new DialogSurface("排除后缀", null, Forms.DialogResult.Cancel, 600);
            var input = new TextBox { Text = String.Join(", ", MediaRules.NormalizeExcluded(current)), MaxLength = 640,
                TextWrapping = TextWrapping.Wrap, MinHeight = 64, AcceptsReturn = true };
            AutomationProperties.SetName(input, "不导入的后缀");
            AutomationProperties.SetAutomationId(input, "Exclusions.Extensions");
            surface.Body.Children.Add(DialogSurface.Field("不导入的后缀(_E)", input, 0));
            surface.Body.Children.Add(input);
            var help = DialogSurface.Text("逗号或空格分隔。DAT 等非媒体文件始终忽略。", "Caption");
            help.Margin = new Thickness(0, 8, 0, 0); surface.Body.Children.Add(help);
            var error = DialogSurface.Error("Exclusions.Error"); surface.Body.Children.Add(error);
            input.TextChanged += delegate { error.Visibility = Visibility.Collapsed; };
            surface.Footer("保存", delegate {
                try { Excluded = MediaRules.ParseExcluded(input.Text); }
                catch (ArgumentException failure) { error.Text = failure.Message; error.Visibility = Visibility.Visible; input.Focus(); return; }
                surface.Complete(Forms.DialogResult.OK);
            }, "取消", false, null);
        }
        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }
}
