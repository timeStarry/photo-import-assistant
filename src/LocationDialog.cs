using System;
using System.Threading.Tasks;
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
        private readonly DestinationRecord original;
        private readonly Func<DestinationRecord, Task<WorkResult>> testConnection;
        private readonly ComboBox type;
        private readonly TextBox name, path, endpoint, bucket, region, prefix, username;
        private readonly PasswordBox password;
        private readonly StackPanel folderFields, remoteFields, s3Fields;
        private readonly Label usernameLabel, passwordLabel;
        private readonly TextBlock credentialHint, error, testStatus;
        private readonly Button test;
        private bool passwordEdited, testing, closed;
        public DestinationRecord Destination { get; private set; }
        internal Window PreviewWindow { get { return surface.Window; } }
        internal bool IsTesting { get { return testing; } }

        public LocationDialog(DestinationRecord existing, Func<DestinationRecord, Task<WorkResult>> testConnection = null)
        {
            original = existing == null ? new DestinationRecord { Id = Guid.NewGuid().ToString("D"), Type = "folder" } : ImportSettings.Clone(existing);
            this.testConnection = testConnection;
            surface = new DialogSurface(existing == null ? "添加位置" : "编辑位置", null, Forms.DialogResult.Cancel, 640);
            AutomationProperties.SetAutomationId(surface.Window, "Location.Dialog");
            surface.Window.Closed += delegate { closed = true; password.Clear(); };

            name = Input(original.Name, "位置名称", "Location.Name", 120);
            AddField(surface.Body, "位置名称(_N)", name, 0);
            type = new ComboBox { IsEditable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(type, "位置类型"); AutomationProperties.SetAutomationId(type, "Location.Type");
            foreach (string label in new[] { "Windows 文件夹", "网络共享 (SMB)", "WebDAV", "S3 兼容存储" }) type.Items.Add(label);
            string currentType = StorageSettings.TypeOf(original);
            type.SelectedIndex = currentType == "smb" ? 1 : currentType == "webdav" ? 2 : currentType == "s3" ? 3 : 0;
            AddField(surface.Body, "位置类型(_T)", type, 16);

            folderFields = new StackPanel(); surface.Body.Children.Add(folderFields);
            path = Input(original.Path, "目标文件夹", "Location.Path", 32760);
            path.TextWrapping = TextWrapping.Wrap; path.MaxHeight = 120;
            var pathRow = new Grid(); pathRow.ColumnDefinitions.Add(new ColumnDefinition());
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); pathRow.Children.Add(path);
            Button browse = DialogSurface.ActionButton("浏览…(_B)", "QuietButton", "Location.Browse");
            browse.MinWidth = 84; browse.Margin = new Thickness(12, 0, 0, 0); browse.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(browse, 1); pathRow.Children.Add(browse);
            folderFields.Children.Add(DialogSurface.Field("目标文件夹(_P)", path, 16)); folderFields.Children.Add(pathRow);
            folderFields.Children.Add(Hint(@"文件夹使用盘符路径；网络共享使用 \\服务器\共享名。", 8));
            browse.Click += delegate {
                if (testing || closed) return;
                using (var folder = new Forms.FolderBrowserDialog())
                {
                    folder.Description = "选择目标文件夹"; folder.ShowNewFolderButton = true;
                    if (System.IO.Directory.Exists(path.Text)) folder.SelectedPath = path.Text;
                    IntPtr handle = new WindowInteropHelper(surface.Window).Handle;
                    Forms.DialogResult choice = handle == IntPtr.Zero ? folder.ShowDialog() : folder.ShowDialog(new FolderOwner(handle));
                    if (choice == Forms.DialogResult.OK) path.Text = folder.SelectedPath;
                }
            };

            remoteFields = new StackPanel(); surface.Body.Children.Add(remoteFields);
            string address = original.Endpoint;
            if (String.IsNullOrWhiteSpace(address) && currentType == "webdav") address = ImportSettings.Address(original);
            endpoint = Input(address, "服务器地址", "Location.Endpoint", 32760);
            AddField(remoteFields, "服务器地址(_E)", endpoint, 16);
            remoteFields.Children.Add(Hint("HTTP(S) 地址，请勿在地址中填写用户名或密码。", 8));
            s3Fields = new StackPanel(); remoteFields.Children.Add(s3Fields);
            bucket = Input(original.Bucket, "Bucket", "Location.Bucket", 63);
            region = Input(String.IsNullOrWhiteSpace(original.Region) ? "us-east-1" : original.Region, "Region", "Location.Region", 64);
            var s3Row = new Grid(); s3Row.ColumnDefinitions.Add(new ColumnDefinition()); s3Row.ColumnDefinitions.Add(new ColumnDefinition());
            var bucketColumn = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var regionColumn = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            AddField(bucketColumn, "Bucket(_K)", bucket, 16); AddField(regionColumn, "Region(_R)", region, 16);
            s3Row.Children.Add(bucketColumn); Grid.SetColumn(regionColumn, 1); s3Row.Children.Add(regionColumn); s3Fields.Children.Add(s3Row);
            prefix = Input(original.Prefix, "对象前缀", "Location.Prefix", 4096);
            AddField(s3Fields, "对象前缀（可选）(_F)", prefix, 16);

            username = Input("", "用户名", "Location.Username", 512);
            password = new PasswordBox { MaxLength = 2560, Padding = new Thickness(11, 9, 11, 9), MinHeight = 40, FontSize = 14 };
            password.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            password.SetResourceReference(Control.ForegroundProperty, "TextBrush"); password.SetResourceReference(Control.BorderBrushProperty, "StrokeBrush");
            AutomationProperties.SetName(password, "密码"); AutomationProperties.SetAutomationId(password, "Location.Password");
            var credentialsRow = new Grid(); credentialsRow.ColumnDefinitions.Add(new ColumnDefinition()); credentialsRow.ColumnDefinitions.Add(new ColumnDefinition());
            var userColumn = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var passwordColumn = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            usernameLabel = DialogSurface.Field("用户名（可选）(_U)", username, 16);
            passwordLabel = DialogSurface.Field("密码（可选）(_W)", password, 16);
            userColumn.Children.Add(usernameLabel); userColumn.Children.Add(username); passwordColumn.Children.Add(passwordLabel); passwordColumn.Children.Add(password);
            credentialsRow.Children.Add(userColumn); Grid.SetColumn(passwordColumn, 1); credentialsRow.Children.Add(passwordColumn); remoteFields.Children.Add(credentialsRow);
            credentialHint = Hint("", 8); remoteFields.Children.Add(credentialHint);

            var testRow = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            test = DialogSurface.ActionButton("测试连接(_C)", "QuietButton", "Location.Test");
            test.HorizontalAlignment = HorizontalAlignment.Left; test.IsEnabled = testConnection != null; testRow.Children.Add(test);
            testStatus = Hint(testConnection == null ? "保存后可在位置列表测试连接。" : "使用已保存的凭据测试连接。", 6);
            AutomationProperties.SetAutomationId(testStatus, "Location.TestStatus"); testRow.Children.Add(testStatus); surface.Body.Children.Add(testRow);
            error = DialogSurface.Error("Location.Error"); surface.Body.Children.Add(error);
            foreach (TextBox input in new[] { name, path, endpoint, bucket, region, prefix, username }) input.TextChanged += delegate { ClearFeedback(); };
            password.PasswordChanged += delegate { passwordEdited = true; ClearFeedback(); };
            type.SelectionChanged += delegate { UpdateFields(); ClearFeedback(); };
            test.Click += async delegate { await TestConnection(); };
            surface.Footer("保存(_S)", Save, "取消", false, null);
            AutomationProperties.SetAutomationId(NativeDialogControls.Find<Button>(surface.Window, "Dialog.Confirm"), "Location.Save");
            UpdateFields();
        }

        private string SelectedType { get { return type.SelectedIndex == 1 ? "smb" : type.SelectedIndex == 2 ? "webdav" : type.SelectedIndex == 3 ? "s3" : "folder"; } }
        private bool NewPassword { get { return passwordEdited && password.Password.Length > 0; } }
        private void UpdateFields()
        {
            bool direct = type.SelectedIndex >= 2, s3 = type.SelectedIndex == 3;
            folderFields.Visibility = direct ? Visibility.Collapsed : Visibility.Visible; remoteFields.Visibility = direct ? Visibility.Visible : Visibility.Collapsed;
            s3Fields.Visibility = s3 ? Visibility.Visible : Visibility.Collapsed;
            usernameLabel.Content = new AccessText { Text = s3 ? "Access Key(_U)" : "用户名（可选）(_U)" };
            passwordLabel.Content = new AccessText { Text = s3 ? "Secret Key(_W)" : "密码（可选）(_W)" };
            AutomationProperties.SetName(username, s3 ? "Access Key" : "用户名"); AutomationProperties.SetName(password, s3 ? "Secret Key" : "密码");
            credentialHint.Text = String.IsNullOrWhiteSpace(original.CredentialTarget) ? "凭据仅在保存时写入 Windows 凭据管理器。" : "已保存凭据；留空保留。更换时请同时填写账号和密码。";
            AutomationProperties.SetHelpText(password, credentialHint.Text);
        }
        private DestinationRecord Candidate()
        {
            var draft = new DestinationRecord { Id = original.Id, Name = name.Text, Enabled = original.Enabled, Type = SelectedType,
                Path = type.SelectedIndex < 2 ? path.Text : "", Endpoint = type.SelectedIndex >= 2 ? endpoint.Text : null,
                Bucket = type.SelectedIndex == 3 ? bucket.Text : null, Region = type.SelectedIndex == 3 ? region.Text : null,
                Prefix = type.SelectedIndex == 3 ? prefix.Text : null, CredentialTarget = original.CredentialTarget };
            var candidate = StorageSettings.Normalize(draft);
            if (StorageSettings.IsDirect(candidate) && !String.IsNullOrWhiteSpace(original.CredentialTarget))
            {
                Uri before, after;
                bool sameOrigin = StorageSettings.IsDirect(original) && Uri.TryCreate(StorageSettings.Normalize(original).Endpoint, UriKind.Absolute, out before) &&
                    Uri.TryCreate(candidate.Endpoint, UriKind.Absolute, out after) && before.GetLeftPart(UriPartial.Authority) == after.GetLeftPart(UriPartial.Authority);
                if (StorageSettings.TypeOf(original) != candidate.Type || !sameOrigin) candidate.CredentialTarget = null;
            }
            return candidate;
        }
        private void Save()
        {
            if (testing || closed) return;
            try
            {
                DestinationRecord candidate = Candidate();
                if (StorageSettings.IsDirect(candidate) && !String.IsNullOrWhiteSpace(username.Text) && !NewPassword) throw new ArgumentException("更换账号时请同时填写密码或 Secret Key。");
                if (StorageSettings.IsDirect(candidate) && NewPassword)
                {
                    if (String.IsNullOrWhiteSpace(username.Text)) throw new ArgumentException(type.SelectedIndex == 3 ? "请填写 Access Key。" : "更换密码时请填写用户名。");
                    candidate.CredentialTarget = CredentialStore.Save(username.Text.Trim(), password.Password, original.CredentialTarget);
                }
                Destination = candidate; surface.Complete(Forms.DialogResult.OK);
            }
            catch (Exception failure) { ShowError(failure.Message); }
        }
        private async Task TestConnection()
        {
            if (testing || closed || testConnection == null) return;
            DestinationRecord candidate;
            try
            {
                candidate = Candidate();
                if (StorageSettings.IsDirect(candidate) && NewPassword) throw new ArgumentException("请先保存新凭据，再测试连接。");
            }
            catch (Exception failure) { ShowError(failure.Message); return; }
            testing = true; error.Visibility = Visibility.Collapsed; surface.Body.IsEnabled = false;
            Button save = NativeDialogControls.Find<Button>(surface.Window, "Location.Save"); save.IsEnabled = false;
            testStatus.Text = "正在测试连接…"; testStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            try
            {
                WorkResult result = await testConnection(candidate);
                if (closed) return;
                bool success = result != null && result.Success;
                testStatus.Text = success ? "连接成功" + (String.IsNullOrWhiteSpace(result.Detail) ? "" : " · " + result.Detail)
                    : "连接失败 · " + (result == null || String.IsNullOrWhiteSpace(result.Error) ? "请检查地址和凭据。" : result.Error);
                testStatus.SetResourceReference(TextBlock.ForegroundProperty, success ? "SuccessBrush" : "DangerBrush");
            }
            catch (Exception failure) { if (!closed) { testStatus.Text = "连接失败 · " + failure.Message; testStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); } }
            finally { testing = false; if (!closed) { surface.Body.IsEnabled = true; save.IsEnabled = true; } }
        }
        private void ClearFeedback() { if (closed || testing) return; error.Visibility = Visibility.Collapsed; testStatus.Text = testConnection == null ? "保存后可在位置列表测试连接。" : "使用已保存的凭据测试连接。"; testStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); }
        private void ShowError(string message) { error.Text = message; error.Visibility = Visibility.Visible; if (String.IsNullOrWhiteSpace(name.Text)) name.Focus(); }
        private static TextBox Input(string value, string label, string id, int limit)
        {
            var input = new TextBox { Text = value ?? "", MaxLength = limit, AcceptsReturn = false };
            AutomationProperties.SetName(input, label); AutomationProperties.SetAutomationId(input, id); return input;
        }
        private static void AddField(StackPanel panel, string label, Control input, double margin) { panel.Children.Add(DialogSurface.Field(label, input, margin)); panel.Children.Add(input); }
        private static TextBlock Hint(string value, double margin) { var hint = DialogSurface.Text(value, "Caption"); hint.Margin = new Thickness(0, margin, 0, 0); return hint; }
        private sealed class FolderOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            internal FolderOwner(IntPtr handle) { Handle = handle; }
        }
        public Forms.DialogResult ShowDialog(Forms.IWin32Window owner) { return surface.ShowDialog(owner); }
        public void Dispose() { surface.Dispose(); }
    }
}
