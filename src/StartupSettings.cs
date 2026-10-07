using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;

namespace PhotoImportV2
{
    /// <summary>Controls login triggers on the existing interactive-user task only.</summary>
    public static class StartupSettings
    {
        private const string TaskName = "PhotoImport-Agent";
        private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        private const int InteractiveToken = 3;
        // Update only; preserve the ACL; do not run registration triggers during the update.
        private const int UpdateFlags = 0x4 | 0x10 | 0x20;

        public static bool Read()
        {
            try
            {
                using (var session = new SchedulerSession())
                {
                    object task = session.FindTask();
                    if (task == null) return false;
                    if (!Convert.ToBoolean(Get(task, "Enabled"), CultureInfo.InvariantCulture)) return false;
                    object definition = session.Own(Get(task, "Definition"));
                    RequireCurrentUser(session, definition);
                    return ReadLogonTriggersInXml((string)Get(definition, "XmlText"));
                }
            }
            catch (COMException error) { throw SchedulerError("无法读取登录启动设置", error); }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (var session = new SchedulerSession())
                {
                    object task = session.FindTask();
                    if (task == null) throw new InvalidOperationException("尚未安装相机导入助手的启动任务。");
                    object definition = session.Own(Get(task, "Definition"));
                    string principalId = RequireCurrentUser(session, definition);
                    string original = (string)Get(definition, "XmlText");
                    string updated = SetLogonTriggersInXml(original, enabled);
                    if (original == updated && Convert.ToBoolean(Get(task, "Enabled"), CultureInfo.InvariantCulture)) return;
                    // Owner, group and DACL are carried through registration unchanged.
                    // SACL is not requested: it requires a privileged token and is not modified.
                    string security = (string)Call(task, "GetSecurityDescriptor", 0x1 | 0x2 | 0x4);
                    if (String.IsNullOrEmpty(security)) throw new InvalidOperationException("无法保留启动任务的访问权限。");
                    object latest = session.FindTask();
                    if (latest == null) throw new InvalidOperationException("启动任务已被移除，请重新打开设置。");
                    object latestDefinition = session.Own(Get(latest, "Definition"));
                    if (!String.Equals(original, (string)Get(latestDefinition, "XmlText"), StringComparison.Ordinal))
                        throw new InvalidOperationException("启动任务已被其他程序修改，请重试。");
                    Put(definition, "XmlText", updated);
                    // No password, no new task, no principal/run-level/action/restart changes.
                    object registered = session.Own(Call(session.Folder, "RegisterTaskDefinition",
                        TaskName, definition, UpdateFlags, principalId, null, InteractiveToken, security));
                    object savedDefinition = session.Own(Get(registered, "Definition"));
                    if (!Convert.ToBoolean(Get(registered, "Enabled"), CultureInfo.InvariantCulture) ||
                        ReadLogonTriggersInXml((string)Get(savedDefinition, "XmlText")) != enabled)
                        throw new InvalidOperationException("登录启动设置未能保存，请重新检查。");
                }
            }
            catch (COMException error) { throw SchedulerError("无法修改登录启动设置", error); }
        }

        // The same XML transformation used by Set is exercised offline by ConfigurationTests.
        // See Microsoft Learn: taskfolder-registertaskdefinition and trigger-enabled.
        internal static bool ReadLogonTriggersInXml(string xml)
        {
            XmlDocument document = Parse(xml);
            var ns = Namespaces(document);
            XmlElement settingsEnabled = document.SelectSingleNode("/t:Task/t:Settings/t:Enabled", ns) as XmlElement;
            if (!Enabled(settingsEnabled)) return false;
            foreach (XmlElement trigger in document.SelectNodes("/t:Task/t:Triggers/t:LogonTrigger", ns))
                if (Enabled(trigger.SelectSingleNode("t:Enabled", ns) as XmlElement)) return true;
            return false;
        }

        internal static string SetLogonTriggersInXml(string xml, bool enabled)
        {
            XmlDocument document = Parse(xml);
            var ns = Namespaces(document);
            XmlNodeList triggers = document.SelectNodes("/t:Task/t:Triggers/t:LogonTrigger", ns);
            if (triggers.Count == 0) throw new InvalidOperationException("启动任务缺少登录触发器，请重新安装助手。");
            foreach (XmlElement trigger in triggers) WriteEnabled(trigger, enabled, ns, true);
            XmlElement settings = document.SelectSingleNode("/t:Task/t:Settings", ns) as XmlElement;
            if (settings == null)
            {
                settings = document.CreateElement("Settings", TaskNamespace);
                XmlNode data = document.SelectSingleNode("/t:Task/t:Data", ns);
                document.DocumentElement.InsertBefore(settings, data);
            }
            // Keep the task enabled so double-click/manual starts still work with startup off.
            WriteEnabled(settings, true, ns, false);
            return document.OuterXml;
        }

        private static void WriteEnabled(XmlElement parent, bool value, XmlNamespaceManager ns, bool trigger)
        {
            XmlElement child = parent.SelectSingleNode("t:Enabled", ns) as XmlElement;
            if (child != null && Enabled(child) == value) return;
            if (child == null)
            {
                child = parent.OwnerDocument.CreateElement("Enabled", TaskNamespace);
                XmlNode before = trigger ? parent.SelectSingleNode("t:Repetition | t:ExecutionTimeLimit | t:UserId | t:Delay", ns) : null;
                parent.InsertBefore(child, before);
            }
            child.InnerText = value ? "true" : "false";
        }

        private static bool Enabled(XmlElement node)
        {
            if (node == null) return true;
            try { return XmlConvert.ToBoolean(node.InnerText.Trim()); }
            catch (FormatException) { throw new InvalidOperationException("启动任务包含无效的启用状态。"); }
        }

        private static XmlDocument Parse(string xml)
        {
            if (String.IsNullOrEmpty(xml) || xml.Length > 1024 * 1024) throw new InvalidOperationException("启动任务定义无效。");
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), settings)) document.Load(reader);
            }
            catch (XmlException) { throw new InvalidOperationException("启动任务定义无效。"); }
            if (document.DocumentElement == null || document.DocumentElement.LocalName != "Task" ||
                document.DocumentElement.NamespaceURI != TaskNamespace)
                throw new InvalidOperationException("启动任务格式不受支持。");
            return document;
        }
        private static XmlNamespaceManager Namespaces(XmlDocument document)
        {
            var ns = new XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("t", TaskNamespace);
            return ns;
        }

        private static string RequireCurrentUser(SchedulerSession session, object definition)
        {
            object principal = session.Own(Get(definition, "Principal"));
            string userId = (string)Get(principal, "UserId");
            if (Convert.ToInt32(Get(principal, "LogonType"), CultureInfo.InvariantCulture) != InteractiveToken || String.IsNullOrEmpty(userId))
                throw new InvalidOperationException("启动任务必须使用当前用户的交互登录方式。");
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null) throw new InvalidOperationException("无法确认当前登录用户。");
                bool same = String.Equals(userId, identity.User.Value, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(userId, identity.Name, StringComparison.OrdinalIgnoreCase);
                if (!same)
                {
                    try
                    {
                        var sid = (SecurityIdentifier)new NTAccount(userId).Translate(typeof(SecurityIdentifier));
                        same = sid.Equals(identity.User);
                    }
                    catch (IdentityNotMappedException) { same = false; }
                }
                if (!same) throw new InvalidOperationException("启动任务属于其他用户，不能从当前账户修改。");
            }
            return userId;
        }

        private static InvalidOperationException SchedulerError(string action, COMException error)
        {
            // Do not expose task XML, account details or scheduler diagnostic payloads.
            return new InvalidOperationException(action + "（HRESULT 0x" + error.ErrorCode.ToString("X8", CultureInfo.InvariantCulture) + "）。");
        }
        private static object Get(object value, string name) { return Dispatch(value, name, BindingFlags.GetProperty, new object[0]); }
        private static void Put(object value, string name, object data) { Dispatch(value, name, BindingFlags.SetProperty, new[] { data }); }
        private static object Call(object value, string name, params object[] args) { return Dispatch(value, name, BindingFlags.InvokeMethod, args); }
        private static object Dispatch(object value, string name, BindingFlags flags, object[] args)
        {
            try { return value.GetType().InvokeMember(name, flags | BindingFlags.Public | BindingFlags.Instance, null, value, args, CultureInfo.InvariantCulture); }
            catch (TargetInvocationException error)
            {
                if (error.InnerException != null) throw error.InnerException;
                throw;
            }
        }

        private sealed class SchedulerSession : IDisposable
        {
            private readonly List<object> owned = new List<object>();
            public object Folder { get; private set; }
            public SchedulerSession()
            {
                try
                {
                    Type type = Type.GetTypeFromProgID("Schedule.Service", true);
                    object service = Own(Activator.CreateInstance(type));
                    // Empty connection parameters use the current token and local scheduler.
                    Call(service, "Connect", null, null, null, null);
                    Folder = Own(Call(service, "GetFolder", @"\"));
                }
                catch { Dispose(); throw; }
            }
            public object Own(object value) { if (value != null && Marshal.IsComObject(value)) owned.Add(value); return value; }
            public object FindTask()
            {
                try { return Own(Call(Folder, "GetTask", TaskName)); }
                catch (COMException error)
                {
                    if (error.ErrorCode == unchecked((int)0x80070002)) return null;
                    throw;
                }
            }
            public void Dispose()
            {
                for (int i = owned.Count - 1; i >= 0; i--)
                    try { Marshal.ReleaseComObject(owned[i]); } catch (InvalidComObjectException) { }
                owned.Clear();
            }
        }
    }
}
