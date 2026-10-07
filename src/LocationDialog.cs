using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace PhotoImportV2
{
    public sealed class LocationDialog : IDisposable
    {
        private readonly DialogSurface surface;
        public DestinationRecord Destination { get; private set; }
        internal Window PreviewWindow { get { return surface.Window; } }

        public LocationDialog(DestinationRecord existing)
        {
            string existingId = existing == null ? null : existing.Id;
            bool existingEnabled = existing == null || existing.Enabled;
            surface = new DialogSurface(existing == null ? "添加位置" : "编辑位置", null, Forms.DialogResult.Cancel, 620);

            var name = new TextBox { Text = existing == null ? "" : existing.Name ?? "", MaxLength = 120, AcceptsReturn = false };
            AutomationProperties.SetName(name, "位置名称");
            AutomationProperties.SetAutomationId(name, "Location.Name");
            surface.Body.Children.Add(DialogSurface.Field("位置名称(_N)", name, 0));
            surface.Body.Children.Add(name);

            var path = new TextBox { Text = existing == null ? "" : ImportSettings.Address(existing), MaxLength = 32760,
                AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 160 };
            AutomationProperties.SetName(path, "目标路径");
            AutomationProperties.SetAutomationId(path, "Location.Path");
            var pathRow = new Grid();
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pathRow.Children.Add(path);
            Button browse = DialogSurface.ActionButton("浏览…", "QuietButton", "Location.Browse");
            browse.MinWidth = 76; browse.MaxWidth = 100; browse.Margin = new Thickness(12, 0, 0, 0);
            browse.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(browse, 1); pathRow.Children.Add(browse);
            surface.Body.Children.Add(DialogSurface.Field("目标路径(_P)", path, 20));
            surface.Body.Children.Add(pathRow);
            TextBlock help = DialogSurface.Text("本地文件夹、网络路径或 WebDAV URL", "Caption");
            help.Margin = new Thickness(0, 8, 0, 0); surface.Body.Children.Add(help);
            AutomationProperties.SetHelpText(path, help.Text);

            TextBlock error = DialogSurface.Error("Location.Error");
            surface.Body.Children.Add(error);
            name.TextChanged += delegate { error.Visibility = Visibility.Collapsed; };
            path.TextChanged += delegate { error.Visibility = Visibility.Collapsed; };
            browse.Click += delegate {
                using (var folder = new Forms.FolderBrowserDialog())
                {
                    folder.Description = "选择本地文件夹";
                    folder.ShowNewFolderButton = true;
                    IntPtr handle = new WindowInteropHelper(surface.Window).Handle;
                    Forms.DialogResult choice = handle == IntPtr.Zero ? folder.ShowDialog() : folder.ShowDialog(new FolderOwner(handle));
                    if (choice == Forms.DialogResult.OK) path.Text = folder.SelectedPath;
                }
            };
            surface.Footer("保存", delegate {
                try
                {
                    DestinationRecord candidate = ImportSettings.ValidateDestination(name.Text, path.Text);
                    if (existing != null) candidate.Id = existingId;
                    candidate.Enabled = existingEnabled;
                    Destination = candidate;
                }
                catch (Exception failure)
                {
                    error.Text = failure.Message; error.Visibility = Visibility.Visible;
                    if (String.IsNullOrWhiteSpace(name.Text)) name.Focus(); else path.Focus();
                    return;
                }
                surface.Complete(Forms.DialogResult.OK);
            }, "取消", false, null);
        }

        private sealed class FolderOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            internal FolderOwner(IntPtr handle) { Handle = handle; }
        }

        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }
}
