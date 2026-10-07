using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace PhotoImportV2
{
    // WPF presentation with the controller's existing WinForms modal-dialog contract.
    public sealed class ChoiceDialog : IDisposable
    {
        private readonly DialogSurface surface;
        private readonly CheckBox remember;
        public bool Remember
        {
            get { return (surface.Result == Forms.DialogResult.Yes || surface.Result == Forms.DialogResult.No) && remember.IsChecked == true; }
        }
        internal Window PreviewWindow { get { return surface.Window; } }

        public ChoiceDialog(string title, string message, string yes, string no, string check)
        {
            bool danger = Destructive(title) || Destructive(yes);
            surface = new DialogSurface(title, null, Forms.DialogResult.No, 600);
            if (danger)
            {
                var panel = new StackPanel();
                panel.Children.Add(DialogSurface.Text(message, "Body"));
                TextBlock consequence = DialogSurface.Text("删除卡内原文件后无法撤销。", "Caption");
                consequence.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                consequence.Margin = new Thickness(0, 12, 0, 0);
                panel.Children.Add(consequence);
                Border warning = DialogSurface.Card(panel);
                warning.SetResourceReference(Border.BorderBrushProperty, "DangerBrush");
                warning.SetResourceReference(Border.BackgroundProperty, "SubtleBrush");
                surface.Body.Children.Add(warning);
            }
            else
            {
                TextBlock body = DialogSurface.Text(message, "Body");
                body.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                surface.Body.Children.Add(DialogSurface.Card(body));
            }
            remember = DialogSurface.Check(check, "Choice.Remember");
            remember.Margin = new Thickness(0, 20, 0, 0);
            remember.Visibility = String.IsNullOrWhiteSpace(check) ? Visibility.Collapsed : Visibility.Visible;
            surface.Body.Children.Add(remember);
            surface.Footer(yes, delegate { surface.Complete(Forms.DialogResult.Yes); }, no, danger, null);
        }

        private static bool Destructive(string text)
        {
            return !String.IsNullOrEmpty(text) && (text.Contains("删除") || text.Contains("清理") ||
                text.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("remove", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }

    public sealed class RegistrationDialog : IDisposable
    {
        private readonly DialogSurface surface;
        private readonly ComboBox existing;
        private readonly TextBox name;
        internal Window PreviewWindow { get { return surface.Window; } }
        public CardRecord SelectedRecord
        {
            get { var option = existing.SelectedItem as RegistrationOption; return option == null ? null : option.Record; }
        }
        public string CardName { get { return name.Text.Trim(); } }

        public RegistrationDialog(StateStore store, DiskSnapshot disk)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (disk == null) throw new ArgumentNullException("disk");
            surface = new DialogSurface("登记存储卡", null, Forms.DialogResult.Cancel, 620);
            string defaultName = String.IsNullOrWhiteSpace(disk.Label) ? "相机存储卡" : disk.Label.Trim();
            surface.Body.Children.Add(DialogSurface.Identity(defaultName,
                (disk.Root ?? "未知盘符") + "  ·  " + DialogSurface.Capacity(disk.Capacity),
                "序列号 " + DialogSurface.Known(disk.Serial) + "  ·  标识 " + DialogSurface.ShortId(disk.MarkerId), null));

            existing = new ComboBox { IsEditable = false, IsTextSearchEnabled = true, MaxDropDownHeight = 280, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(existing, "选择登记记录");
            AutomationProperties.SetAutomationId(existing, "Registration.Record");
            AutomationProperties.SetHelpText(existing, "匹配仅为建议，确认后关联；不确定时请选择新建。");
            TextSearch.SetTextPath(existing, "DisplayName");
            DialogSurface.WrapItems(existing, "DisplayName", surface.Window.Width - 128);
            existing.Items.Add(new RegistrationOption(null, "新建独立登记"));
            foreach (CardRecord record in store.State.Cards.Where(c => c != null))
            {
                string label = DialogSurface.Known(record.Name) + " · " + DialogSurface.Capacity(record.Capacity) +
                    " · " + DialogSurface.ShortId(record.Id);
                existing.Items.Add(new RegistrationOption(record, label));
            }
            surface.Body.Children.Add(DialogSurface.Field("登记记录(_R)", existing, 20));
            surface.Body.Children.Add(existing);
            TextBlock suggestion = DialogSurface.Text("不确定时请选择新建。", "Caption");
            suggestion.Margin = new Thickness(0, 8, 0, 0);
            surface.Body.Children.Add(suggestion);

            name = new TextBox { Text = defaultName, MaxLength = 80, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(name, "卡片名称");
            AutomationProperties.SetAutomationId(name, "Registration.Name");
            surface.Body.Children.Add(DialogSurface.Field("卡片名称(_N)", name, 20));
            surface.Body.Children.Add(name);
            TextBlock error = DialogSurface.Error("Registration.Error");
            surface.Body.Children.Add(error);
            name.TextChanged += delegate {
                if (CardName.Length > 0) { error.Visibility = Visibility.Collapsed; name.ClearValue(Control.BorderBrushProperty); }
            };
            CardRecord suggested = store.State.Cards.LastOrDefault(c => c != null && disk.Capacity > 0 && c.Capacity == disk.Capacity &&
                !String.IsNullOrWhiteSpace(disk.Serial) && String.Equals(c.LegacySerial, disk.Serial, StringComparison.OrdinalIgnoreCase));
            existing.SelectionChanged += delegate {
                CardRecord selected = SelectedRecord;
                name.Text = selected == null || String.IsNullOrWhiteSpace(selected.Name) ? defaultName : selected.Name;
                suggestion.Text = suggested != null && Object.ReferenceEquals(selected, suggested)
                    ? "匹配建议，请确认是同一张卡后关联。"
                    : selected == null ? "不确定时请选择新建。" : "请确认是同一张卡后关联。";
            };
            existing.SelectedIndex = 0;
            if (suggested != null)
                foreach (RegistrationOption option in existing.Items)
                    if (Object.ReferenceEquals(option.Record, suggested)) { existing.SelectedItem = option; break; }

            surface.Footer("确认登记", delegate {
                if (CardName.Length == 0)
                {
                    error.Text = "请填写卡片名称。"; error.Visibility = Visibility.Visible;
                    name.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
                    name.Focus(); return;
                }
                surface.Complete(Forms.DialogResult.OK);
            }, "取消", false, null);
        }

        private sealed class RegistrationOption
        {
            public CardRecord Record { get; private set; }
            public string DisplayName { get; private set; }
            internal RegistrationOption(CardRecord record, string displayName) { Record = record; DisplayName = displayName; }
        }

        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }

    public sealed class PreferencesDialog : IDisposable
    {
        private readonly DialogSurface surface;
        internal Window PreviewWindow { get { return surface.Window; } }

        public PreferencesDialog(StateStore store, CardRecord card)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (card == null) throw new ArgumentNullException("card");
            surface = new DialogSurface("卡片设置", null, Forms.DialogResult.Cancel, 600);
            surface.Body.Children.Add(DialogSurface.Identity(DialogSurface.Known(card.Name), DialogSurface.Capacity(card.Capacity),
                "序列号 " + DialogSurface.Known(card.LegacySerial) + "  ·  标识 " + DialogSurface.ShortId(card.Id), null));
            var name = new TextBox { Text = card.Name ?? "", MaxLength = 80, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(name, "卡片名称");
            AutomationProperties.SetAutomationId(name, "Preferences.Name");
            surface.Body.Children.Add(DialogSurface.Field("卡片名称(_N)", name, 20));
            surface.Body.Children.Add(name);
            CheckBox auto = DialogSurface.Check("插入此卡时自动导入", "Preferences.AutoImport");
            auto.IsChecked = card.AutoImport; auto.Margin = new Thickness(0, 20, 0, 0);
            surface.Body.Children.Add(auto);
            var mode = new ComboBox { IsEditable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(mode, "原件处理");
            AutomationProperties.SetAutomationId(mode, "Preferences.DeleteMode");
            DialogSurface.WrapItems(mode, null, surface.Window.Width - 128);
            mode.Items.Add("每次询问");
            mode.Items.Add("保留原文件");
            mode.Items.Add("自动删除已校验的原文件");
            mode.SelectedIndex = card.DeleteMode == "Keep" ? 1 : card.DeleteMode == "Auto" ? 2 : 0;
            surface.Body.Children.Add(DialogSurface.Field("原件处理(_D)", mode, 20));
            surface.Body.Children.Add(mode);
            TextBlock retention = DialogSurface.Text("本地、WebDAV 和 S3 副本保留卡内原件；清理设置仅适用于可锁定校验的 SMB 副本。", "Caption");
            retention.Margin = new Thickness(0, 10, 0, 0); surface.Body.Children.Add(retention);
            TextBlock warning = DialogSurface.Text("将自动删除已校验的卡内原件，删除后无法撤销。", "Caption");
            warning.Margin = new Thickness(0, 10, 0, 0);
            warning.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            warning.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            mode.SelectionChanged += delegate { warning.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; };
            surface.Body.Children.Add(warning);
            TextBlock error = DialogSurface.Error("Preferences.Error"); surface.Body.Children.Add(error);
            name.TextChanged += delegate { error.Visibility = Visibility.Collapsed; name.ClearValue(Control.BorderBrushProperty); };
            Action explicitSave = delegate {
                string cardName = name.Text.Trim();
                if (cardName.Length == 0)
                {
                    error.Text = "请填写卡片名称。"; error.Visibility = Visibility.Visible;
                    name.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
                    name.Focus(); return;
                }
                string originalName = card.Name;
                bool originalAuto = card.AutoImport;
                string originalMode = card.DeleteMode;
                try
                {
                    card.Name = cardName;
                    card.AutoImport = auto.IsChecked == true;
                    card.DeleteMode = mode.SelectedIndex == 1 ? "Keep" : mode.SelectedIndex == 2 ? "Auto" : "Ask";
                    store.Save();
                }
                catch (Exception failure)
                {
                    card.Name = originalName; card.AutoImport = originalAuto; card.DeleteMode = originalMode;
                    error.Text = "保存失败：" + failure.Message;
                    error.Visibility = Visibility.Visible; return;
                }
                surface.Complete(Forms.DialogResult.OK);
            };
            surface.Footer("保存", explicitSave, "取消", false, null);
        }

        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }

    internal sealed class DialogSurface : IDisposable
    {
        internal readonly Window Window;
        internal readonly StackPanel Body;
        private readonly Grid footer;
        private readonly Forms.DialogResult safeResult;
        private Forms.DialogResult result;
        internal Forms.DialogResult Result { get { return result; } }
        private Button safeButton;
        private bool closed, disposed;

        internal DialogSurface(string title, string subtitle, Forms.DialogResult safe, double width)
        {
            safeResult = safe;
            result = Forms.DialogResult.Cancel;
            Window = new Window { Title = title, Width = Math.Min(width, Math.Max(320, SystemParameters.WorkArea.Width - 48)),
                SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 48),
                UseLayoutRounding = true, SnapsToDevicePixels = true };
            FluentTheme.StyleWindow(Window);
            AutomationProperties.SetName(Window, title);
            var root = new DockPanel { LastChildFill = true };
            KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);
            var header = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
            var heading = new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            header.Children.Add(heading);
            if (!String.IsNullOrWhiteSpace(subtitle))
            {
                TextBlock description = Text(subtitle, "Body");
                description.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                description.Margin = new Thickness(0, 8, 0, 0); header.Children.Add(description);
            }
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var footerBorder = new Border { Child = footer, Padding = new Thickness(28, 16, 28, 20), BorderThickness = new Thickness(0, 1, 0, 0) };
            footerBorder.SetResourceReference(Border.BackgroundProperty, "SubtleBrush");
            footerBorder.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            KeyboardNavigation.SetTabIndex(footerBorder, 100);
            DockPanel.SetDock(footerBorder, Dock.Bottom); root.Children.Add(footerBorder);
            Body = new StackPanel { Margin = new Thickness(28, 0, 28, 24) };
            var scroll = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height - 280), Focusable = false };
            root.Children.Add(scroll); Window.Content = root;
            Window.ContentRendered += delegate { if (safeButton != null) { safeButton.Focus(); Keyboard.Focus(safeButton); } };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs args) {
                if (args.Key == Key.Escape)
                { args.Handled = true; Complete(Forms.DialogResult.Cancel); }
            };
            Window.Closed += delegate { closed = true; };
        }

        internal void Footer(string primaryText, Action confirm, string safeText, bool danger, string note)
        {
            if (!String.IsNullOrWhiteSpace(note))
            {
                TextBlock caption = Text(note, "Caption"); caption.VerticalAlignment = VerticalAlignment.Center;
                caption.Margin = new Thickness(0, 0, 16, 0); footer.Children.Add(caption);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            safeButton = ActionButton(safeText, "QuietButton", "Dialog.Cancel");
            safeButton.IsDefault = true; // Enter defaults to No/Cancel; confirmation is never the default.
            safeButton.Click += delegate { Complete(safeResult); };
            buttons.Children.Add(safeButton);
            Button primary = ActionButton(primaryText, danger ? "DangerButton" : "PrimaryButton", "Dialog.Confirm");
            primary.Margin = new Thickness(12, 0, 0, 0);
            primary.Click += delegate { confirm(); };
            buttons.Children.Add(primary);
            Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
        }

        internal static Button ActionButton(string text, string style, string id)
        {
            var button = new Button { Content = new AccessText { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }, MinWidth = 108, MaxWidth = 220 };
            button.SetResourceReference(FrameworkElement.StyleProperty, style);
            AutomationProperties.SetName(button, text); AutomationProperties.SetAutomationId(button, id);
            return button;
        }

        internal Forms.DialogResult ShowDialog(Forms.IWin32Window owner)
        {
            if (disposed || closed) throw new ObjectDisposedException("dialog");
            if (owner != null && owner.Handle != IntPtr.Zero) new WindowInteropHelper(Window).Owner = owner.Handle;
            else Window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Window.ShowDialog();
            return result;
        }

        internal void Complete(Forms.DialogResult value) { if (!closed) { result = value; Window.Close(); } }
        public void Dispose() { if (disposed) return; disposed = true; if (!closed) Window.Close(); }

        internal static TextBlock Text(string value, string style)
        {
            var text = new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(FrameworkElement.StyleProperty, style); return text;
        }

        internal static Border Card(UIElement content)
        {
            var card = new Border { Child = content, Padding = new Thickness(20) };
            card.SetResourceReference(FrameworkElement.StyleProperty, "Card"); return card;
        }

        internal static Border Identity(string title, string facts, string identity, string description)
        {
            var panel = new StackPanel();
            TextBlock name = Text(title, "Body"); name.FontSize = 16; name.FontWeight = FontWeights.SemiBold; panel.Children.Add(name);
            TextBlock metadata = Text(facts, "Caption");
            metadata.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(metadata);
            if (!String.IsNullOrWhiteSpace(identity) || !String.IsNullOrWhiteSpace(description))
            {
                var details = new StackPanel();
                if (!String.IsNullOrWhiteSpace(identity)) details.Children.Add(Text(identity, "Caption"));
                if (!String.IsNullOrWhiteSpace(description)) details.Children.Add(Text(description, "Caption"));
                var expander = new Expander { Header = Text("详细信息", "Caption"), Content = details,
                    IsExpanded = false, Margin = new Thickness(0, 8, 0, 0) };
                expander.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
                panel.Children.Add(expander);
            }
            return Card(panel);
        }

        internal static CheckBox Check(string text, string id)
        {
            var check = new CheckBox { Content = Text(text, "Body"), HorizontalContentAlignment = HorizontalAlignment.Stretch, IsThreeState = false };
            AutomationProperties.SetName(check, text ?? ""); AutomationProperties.SetAutomationId(check, id); return check;
        }

        internal static void WrapItems(ComboBox combo, string path, double maxWidth)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, path == null ? new Binding() : new Binding(path));
            text.SetBinding(FrameworkElement.ToolTipProperty, path == null ? new Binding() : new Binding(path));
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            text.SetValue(FrameworkElement.MaxWidthProperty, Math.Max(160, maxWidth));
            combo.ItemTemplate = new DataTemplate { VisualTree = text };
        }

        internal static Label Field(string text, Control control, double space)
        {
            var label = new Label { Content = new AccessText { Text = text, TextWrapping = TextWrapping.Wrap }, Target = control,
                Padding = new Thickness(0), Margin = new Thickness(0, space, 0, 8), FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            AutomationProperties.SetLabeledBy(control, label); return label;
        }

        internal static TextBlock Error(string id)
        {
            TextBlock error = Text("", "Caption"); error.Margin = new Thickness(0, 8, 0, 0); error.Visibility = Visibility.Collapsed;
            error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); AutomationProperties.SetAutomationId(error, id); return error;
        }

        internal static string Known(string value) { return String.IsNullOrWhiteSpace(value) ? "未知" : value.Trim(); }
        internal static string ShortId(string value) { return String.IsNullOrWhiteSpace(value) ? "未登记" : value.Substring(0, Math.Min(8, value.Length)); }
        internal static string Capacity(long value) { return value <= 0 ? "未知容量" : (value / 1000000000d).ToString("0.0", CultureInfo.CurrentCulture) + " GB"; }
    }
}
