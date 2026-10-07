using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace PhotoImportV2
{
    /// <summary>Offline fixtures only. Never registers a task or accesses an SD card/NAS.</summary>
    public static class ConfigurationTests
    {
        public static string Run()
        {
            var passed = new List<string>();
            Test(passed, "missing configuration migration", Migration);
            Test(passed, "migration is idempotent", Idempotence);
            Test(passed, "intentional empty list survives migration", EmptyList);
            Test(passed, "custom settings and card preferences survive", PreserveCustom);
            Test(passed, "enabled priority snapshots are independent", PrioritySnapshots);
            Test(passed, "Next Stop and final target use production retry policy", RetryTransitions);
            Test(passed, "production retry policy rejects invalid inputs", InvalidRetryInputs);
            Test(passed, "disabled reordered settings cannot change a running batch", FrozenBatch);
            Test(passed, "clone preserves identity and enabled state", Clone);
            Test(passed, "local absolute folder normalization", LocalPaths);
            Test(passed, "ordinary UNC normalization", UncPaths);
            Test(passed, "HTTP and HTTPS WebDAV mapping", UrlPaths);
            Test(passed, "safe escaped Unicode and spaces", UnicodeUrls);
            Test(passed, "WebDAV UNC aliases normalize consistently", WebDavUnc);
            Test(passed, "normalized destination paths are idempotent", PathIdempotence);
            Test(passed, "credentials query and fragment rejected", PrivateUrls);
            Test(passed, "original traversal rejected before URI normalization", Traversal);
            Test(passed, "encoded separators and nested encoding rejected", EncodedSeparators);
            Test(passed, "malformed encoding and invalid Unicode rejected", BadEncoding);
            Test(passed, "relative and device paths rejected", DevicePaths);
            Test(passed, "unsafe Windows components rejected", UnsafeComponents);
            Test(passed, "unsafe server and port rejected", UnsafeServers);
            Test(passed, "invalid destination names rejected", InvalidNames);
            Test(passed, "duplicate canonical destination paths rejected", DuplicatePaths);
            Test(passed, "invalid and duplicate IDs rejected", InvalidIds);
            Test(passed, "invalid entries and failure policies rejected", InvalidConfig);
            Test(passed, "no enabled destinations is valid", DisabledDestinations);
            Test(passed, "local result stays local at highest priority", LocalPriorityPolicy);
            Test(passed, "JSON roundtrip in owned temporary directory", Roundtrip);
            Test(passed, "production destination probe reads writes and cleans only its file", ProbeLocal);
            Test(passed, "production destination probe failure retains unrelated files", ProbeFailure);
            Test(passed, "login-off keeps task enabled and other definition unchanged", StartupOff);
            Test(passed, "login-on toggles all login triggers only", StartupOn);
            Test(passed, "missing trigger enabled element uses scheduler defaults", StartupDefaults);
            Test(passed, "login enabled element retains schema order", StartupOrder);
            Test(passed, "startup XML rejects missing logon trigger and unsafe XML", StartupInvalid);
            return "PASS: " + passed.Count.ToString(CultureInfo.InvariantCulture) +
                " configuration fixture tests: " + String.Join("; ", passed.ToArray()) + ".";
        }

        private static void Migration()
        {
            var state = new AppState();
            var card = new CardRecord { Id = Guid.NewGuid().ToString("D"), Name = "原卡", AutoImport = true, DeleteMode = "Always", NeedsBinding = false };
            state.Cards.Add(card);
            Assert(ImportSettings.Normalize(state), "Missing fields were not migrated.");
            Assert(state.Destinations.Count == 0, "A fresh or legacy configuration must not guess a destination.");
            Assert(state.Destinations.All(d => d.Enabled) && state.DestinationFailure == "Next" &&
                state.PromptForNewCards == true && !state.StartAtLogin.HasValue, "Default policies are wrong.");
            Assert(ReferenceEquals(card, state.Cards[0]) && card.AutoImport && card.DeleteMode == "Always" && !card.NeedsBinding, "Card preference was changed.");
            ImportSettings.ValidateConfig(state);
        }

        private static void Idempotence()
        {
            AppState state = Fresh();
            List<DestinationRecord> entries = state.Destinations;
            string[] ids = entries.Select(d => d.Id).ToArray();
            Assert(!ImportSettings.Normalize(state) && ReferenceEquals(entries, state.Destinations) &&
                ids.SequenceEqual(state.Destinations.Select(d => d.Id)), "Second normalize changed a configured state.");
        }

        private static void EmptyList()
        {
            var state = new AppState { Destinations = new List<DestinationRecord>() };
            Assert(ImportSettings.Normalize(state) && state.Destinations.Count == 0, "Empty list was overwritten.");
            Assert(!ImportSettings.Normalize(state), "Empty configuration keeps triggering migration.");
            ImportSettings.ValidateConfig(state);
            Assert(ImportSettings.Ordered(state).Count == 0, "Empty list produced defaults.");
        }

        private static void PreserveCustom()
        {
            var destination = ImportSettings.ValidateDestination("自定义", @"N:\摄影\导入");
            destination.Enabled = false;
            var state = new AppState { Destinations = new List<DestinationRecord> { destination }, DestinationFailure = "Stop", PromptForNewCards = false, StartAtLogin = false };
            var card = new CardRecord { Id = "card", AutoImport = true, DeleteMode = "Keep", NeedsBinding = true };
            state.Cards.Add(card);
            Assert(!ImportSettings.Normalize(state) && ReferenceEquals(destination, state.Destinations[0]) && !destination.Enabled &&
                state.DestinationFailure == "Stop" && state.PromptForNewCards == false && state.StartAtLogin == false &&
                ReferenceEquals(card, state.Cards[0]) && card.AutoImport && card.DeleteMode == "Keep" && card.NeedsBinding, "Custom configuration changed.");
        }

        private static void PrioritySnapshots()
        {
            AppState state = Fresh();
            DestinationRecord local = state.Destinations[1], remote = state.Destinations[0];
            var disabled = ImportSettings.ValidateDestination("停用", @"C:\Disabled");
            disabled.Enabled = false;
            state.Destinations = new List<DestinationRecord> { local, disabled, remote };
            List<DestinationRecord> snapshot = ImportSettings.Ordered(state);
            Assert(snapshot.Count == 2 && snapshot[0].Id == local.Id && snapshot[1].Id == remote.Id, "Enabled priority changed.");
            local.Name = "已编辑"; local.Path = @"C:\Changed"; state.Destinations.Reverse();
            Assert(snapshot[0].Name == "本地备份" && snapshot[0].Path == @"C:\Photos\Camera" && !ReferenceEquals(snapshot[1], remote), "Snapshot shares mutable entries.");
            snapshot[1].Enabled = false;
            Assert(remote.Enabled, "Snapshot mutated stored configuration.");
        }

        private static void Clone()
        {
            DestinationRecord source = ImportSettings.ValidateDestination("位置", @"C:\Photos");
            source.Enabled = false;
            DestinationRecord copy = ImportSettings.Clone(source);
            Assert(!ReferenceEquals(source, copy) && copy.Id == source.Id && copy.Name == source.Name &&
                copy.Path == source.Path && !copy.Enabled, "Clone changed a field.");
        }
        private static void RetryTransitions()
        {
            Assert(ImportSettings.NextIndex(0, 3, "Next") == 1 && ImportSettings.NextIndex(1, 3, "Next") == 2 &&
                ImportSettings.NextIndex(2, 3, "Next") == -1 && ImportSettings.NextIndex(0, 1, "Next") == -1,
                "Next did not follow the selected snapshot or stop at its end.");
            Assert(ImportSettings.NextIndex(0, 3, "Stop") == -1 && ImportSettings.NextIndex(2, 3, "Stop") == -1,
                "Stop fell through to another destination.");
        }
        private static void InvalidRetryInputs()
        {
            Reject(delegate { ImportSettings.NextIndex(-1, 2, "Next"); });
            Reject(delegate { ImportSettings.NextIndex(2, 2, "Next"); });
            Reject(delegate { ImportSettings.NextIndex(0, 0, "Next"); });
            Reject(delegate { ImportSettings.NextIndex(0, -1, "Stop"); });
            foreach (string policy in new[] { null, "", "next", "Fallback", "Stop " })
                Reject(delegate { ImportSettings.NextIndex(0, 2, policy); });
            Assert(ImportSettings.NextIndex(Int32.MaxValue - 1, Int32.MaxValue, "Next") == -1,
                "End-of-list calculation overflowed.");
        }
        private static void FrozenBatch()
        {
            AppState state = Fresh();
            DestinationRecord disabled = ImportSettings.ValidateDestination("停用位置", @"C:\Disabled");
            disabled.Enabled = false;
            DestinationRecord first = state.Destinations[1], second = state.Destinations[0];
            state.Destinations = new List<DestinationRecord> { first, disabled, second };
            List<DestinationRecord> batch = ImportSettings.Ordered(state);
            string batchPolicy = state.DestinationFailure;
            state.Destinations.Reverse(); first.Enabled = false; second.Path = @"C:\Edited";
            disabled.Enabled = true; state.DestinationFailure = "Stop";
            int next = ImportSettings.NextIndex(0, batch.Count, batchPolicy);
            Assert(batch.Count == 2 && batch[0].Id == first.Id && batch[0].Enabled && next == 1 &&
                batch[next].Id == second.Id && ImportSettings.Kind(batch[next]) == "remote" &&
                ImportSettings.NextIndex(next, batch.Count, batchPolicy) == -1,
                "Mid-batch edits changed priority, enabled state, path classification or retry policy.");
            Assert(ImportSettings.NextIndex(0, batch.Count, state.DestinationFailure) == -1,
                "New Stop selection was not honored when explicitly passed.");
        }
        private static void LocalPaths()
        {
            EqualPath("  d:/摄影/照片/  ", @"D:\摄影\照片");
            EqualPath(@"c:\", @"C:\");
            EqualPath(@"N:\相册\", @"N:\相册");
            EqualPath(@"C:\家庭 照片\2026.10", @"C:\家庭 照片\2026.10");
            EqualPath(@"C:\100%相册", @"C:\100%相册");
        }
        private static void UncPaths()
        {
            EqualPath(@"\\NAS-BOX\Photos\Z30\", @"\\nas-box\Photos\Z30");
            EqualPath(@"\\192.0.2.10\共享\摄影", @"\\192.0.2.10\共享\摄影");
            Assert(ImportSettings.Kind(Destination(@"\\NAS\Photos")) == "remote", "UNC was not remote.");
        }
        private static void UrlPaths()
        {
            EqualPath("http://192.0.2.10:5005/Photos/Z30/", @"\\192.0.2.10@5005\DavWWWRoot\Photos\Z30");
            EqualPath("https://NAS.Example:5443/Photos", @"\\nas.example@SSL@5443\DavWWWRoot\Photos");
            EqualPath("https://NAS.Example/", @"\\nas.example@SSL@443\DavWWWRoot");
            EqualPath("HTTP://NAS.Example", @"\\nas.example@80\DavWWWRoot");
            Assert(ImportSettings.Address(Destination("http://192.0.2.10:5005/Photos/Z30")) == "http://192.0.2.10:5005/Photos/Z30", "WebDAV display address changed.");
            Assert(ImportSettings.Address(Destination("https://nas.example/")) == "https://nas.example/", "HTTPS display root changed.");
        }
        private static void UnicodeUrls()
        {
            EqualPath("https://nas.example/%E7%85%A7%E7%89%87/%F0%9F%93%B7%20Z30", @"\\nas.example@SSL@443\DavWWWRoot\照片\📷 Z30");
            EqualPath("https://nas.example/中文/家庭%20照片", @"\\nas.example@SSL@443\DavWWWRoot\中文\家庭 照片");
            EqualPath("https://nas.example/100%25", @"\\nas.example@SSL@443\DavWWWRoot\100%");
            EqualPath("https://例子.测试/照片", @"\\xn--fsqu00a.xn--0zwm56d@SSL@443\DavWWWRoot\照片");
            var unicode = Destination("https://nas.example/中文/家庭%20照片");
            Assert(Destination(ImportSettings.Address(unicode)).Path == unicode.Path, "Displayed address does not roundtrip.");
        }
        private static void WebDavUnc()
        {
            EqualPath(@"\\NAS.EXAMPLE@ssl\davwwwroot\照片", @"\\nas.example@SSL@443\DavWWWRoot\照片");
            EqualPath(@"\\NAS.EXAMPLE\DavWWWRoot\照片", @"\\nas.example@80\DavWWWRoot\照片");
            EqualPath(@"\\NAS.EXAMPLE@SSL@05443\DavWWWRoot\%E7%85%A7%E7%89%87", @"\\nas.example@SSL@5443\DavWWWRoot\照片");
        }
        private static void PathIdempotence()
        {
            foreach (string input in new[] { @"C:\照片\", @"\\NAS\共享\照片", "https://nas.example/100%25", "https://nas.example/%E7%85%A7%E7%89%87", @"\\NAS@SSL\DavWWWRoot\照片" })
            {
                string path = Destination(input).Path;
                Assert(Destination(path).Path == path, "Normalized path changed on a second pass.");
            }
        }
        private static void PrivateUrls()
        {
            RejectPaths("http://user:password@nas.example/Photos", "https://@nas.example/Photos", "http://nas.example/Photos?", "http://nas.example/Photos?token=fixture", "https://nas.example/Photos#", "https://nas.example/Photos#fragment");
        }
        private static void Traversal()
        {
            RejectPaths("http://nas.example/Photos/../Other", "http://nas.example/Photos/./Other", "http://nas.example/%2e%2e/Other", "https://nas.example/%2E/Other", @"C:\Photos\..\Other", @"\\nas\share\..\Other", @"C:\Photos\%2e%2e\Other");
        }
        private static void EncodedSeparators()
        {
            RejectPaths("https://nas.example/Photos%2FZ30", "https://nas.example/Photos%5cZ30", "https://nas.example/%252f", "https://nas.example/%252e%252e", "https://nas.example/%2541", @"\\nas@SSL\DavWWWRoot\%2F", @"\\nas@SSL\DavWWWRoot\%2541", @"C:\Photos\%5Cbad", @"\\nas\share\%2fbad");
        }
        private static void BadEncoding()
        {
            RejectPaths("https://nas.example/%GG", "https://nas.example/%A", "https://nas.example/%", "https://nas.example/%C0%AF", "https://nas.example/%ED%A0%80", "https://nas.example/%F4%90%80%80", "https://nas.example/%00", "https://nas.example/%0A", "https://nas.example/%1f", "C:\\bad\uD800");
        }
        private static void DevicePaths()
        {
            RejectPaths("", "Photos", @"C:Photos", @"\Photos", "file:///C:/Photos", "ftp://nas.example/Photos", @"\\?\C:\Photos", @"\\.\PhysicalDrive0", @"\\?\UNC\nas\share", @"\??\C:\Photos", @"\\.\pipe\test");
        }
        private static void UnsafeComponents()
        {
            RejectPaths(@"C:\CON", @"C:\NUL.txt", @"C:\COM1", @"C:\LPT²", @"C:\CONIN$", @"C:\Photos.", @"C:\Photos \Z30", @"C:\Photos:stream", @"C:\Bad*Name", @"\\nas\share\Bad|Name", "https://nas.example/Photos%2e", "https://nas.example/Photos%20", "https://nas.example/Photos//Z30");
        }
        private static void UnsafeServers()
        {
            RejectPaths("https://nas.example:0/Photos", "https://nas.example:65536/Photos", "https://nas.example:/Photos", "https://nas.example:-1/Photos", "https://[::1]/Photos", "https://%6Eas.example/Photos", "https://nas..example/Photos", "https://nas.example./Photos", @"\\nas@user@5005\DavWWWRoot", @"\\nas@SSL@0\DavWWWRoot", @"\\.\share", @"\\nas", @"\\nas\\share", @"\\bad host\share");
        }
        private static void InvalidNames()
        {
            foreach (string name in new[] { null, "", " \t ", "bad\nname", new string('a', 121), "bad\uD800" })
                Reject(delegate { ImportSettings.ValidateDestination(name, @"C:\Photos"); });
            Assert(ImportSettings.ValidateDestination("  家庭相册  ", @"C:\Photos").Name == "家庭相册", "Name was not trimmed.");
        }
        private static void DuplicatePaths()
        {
            AssertDuplicate(@"C:\Photos\", "c:/photos");
            AssertDuplicate("https://NAS.example/Photos", @"\\nas.example@ssl\DavWWWRoot\photos\");
            AssertDuplicate("http://nas.example/Photos", @"\\NAS.EXAMPLE\davwwwroot\Photos");
            AssertDuplicate("https://nas.example/%E7%85%A7%E7%89%87", @"\\nas.example@SSL@443\DavWWWRoot\照片");
        }
        private static void InvalidIds()
        {
            AppState state = Fresh();
            foreach (string id in new[] { null, "bad", Guid.Empty.ToString("D") })
            {
                state.Destinations[0].Id = id;
                Reject(delegate { ImportSettings.ValidateConfig(state); });
            }
            state = Fresh();
            state.Destinations[1].Id = new Guid(state.Destinations[0].Id).ToString("B").ToUpperInvariant();
            Reject(delegate { ImportSettings.ValidateConfig(state); });
        }
        private static void InvalidConfig()
        {
            AppState state = Fresh();
            foreach (string policy in new[] { null, "", "next", "Fallback", " Next " })
            {
                state.DestinationFailure = policy;
                Reject(delegate { ImportSettings.ValidateConfig(state); });
            }
            state = Fresh(); state.Destinations.Add(null);
            Reject(delegate { ImportSettings.ValidateConfig(state); });
            state = Fresh(); state.Destinations[0].Enabled = false; state.Destinations[0].Path = "relative";
            Reject(delegate { ImportSettings.ValidateConfig(state); });
            Reject(delegate { ImportSettings.ValidateConfig(new AppState()); });
            Reject(delegate { ImportSettings.Normalize(null); });
        }
        private static void DisabledDestinations()
        {
            AppState state = Fresh();
            foreach (DestinationRecord record in state.Destinations) record.Enabled = false;
            ImportSettings.ValidateConfig(state);
            Assert(ImportSettings.Ordered(state).Count == 0, "Disabled destinations were returned.");
        }
        private static void LocalPriorityPolicy()
        {
            AppState state = Fresh();
            DestinationRecord mapped = Destination(@"N:\Z30");
            state.Destinations.Insert(0, mapped);
            DestinationRecord first = ImportSettings.Ordered(state)[0];
            Assert(first.Id == mapped.Id && ImportSettings.Kind(first) == "local" &&
                ImportSettings.Kind(state.Destinations[2]) == "local" && ImportSettings.Kind(state.Destinations[1]) == "remote",
                "Priority or drive mappings weakened local classification.");
        }
        private static void Roundtrip()
        {
            string root = Path.Combine(Path.GetTempPath(), "PhotoImport-configuration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "state.json");
            try
            {
                AppState state = Fresh();
                state.Destinations.Reverse(); state.Destinations[1].Enabled = false;
                state.DestinationFailure = "Stop"; state.PromptForNewCards = false; state.StartAtLogin = false;
                state.Cards.Add(new CardRecord { Id = Guid.NewGuid().ToString("D"), Name = "尼康卡", AutoImport = true, DeleteMode = "Keep" });
                JsonFile.Write(path, state);
                AppState saved = JsonFile.Read<AppState>(path);
                ImportSettings.ValidateConfig(saved);
                Assert(!ImportSettings.Normalize(saved) && state.Destinations.Select(d => d.Id).SequenceEqual(saved.Destinations.Select(d => d.Id)) &&
                    saved.Destinations[0].Path == state.Destinations[0].Path && !saved.Destinations[1].Enabled && saved.DestinationFailure == "Stop" &&
                    saved.PromptForNewCards == false && saved.StartAtLogin == false && saved.Cards[0].Name == "尼康卡" && saved.Cards[0].AutoImport && saved.Cards[0].DeleteMode == "Keep", "JSON roundtrip lost preferences.");
                saved.Destinations.Clear(); JsonFile.Write(path, saved);
                AppState empty = JsonFile.Read<AppState>(path);
                Assert(!ImportSettings.Normalize(empty) && empty.Destinations.Count == 0, "Persisted empty list was repopulated.");
            }
            finally
            {
                // Exact files created by this fixture only; no recursive deletion.
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
                Directory.Delete(root, false);
            }
        }

        private static void ProbeLocal()
        {
            string root = Path.Combine(Path.GetTempPath(), "PhotoImport-probe-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string unrelated = Path.Combine(root, "unrelated.txt");
            File.WriteAllText(unrelated, "fixture sentinel");
            try
            {
                WorkResult result = DestinationProbe.Run(new WorkRequest { Operation = "probe", DestinationRoot = root });
                Assert(result.Success && !result.Deleted && result.Receipt == null && String.IsNullOrEmpty(result.Error),
                    "Local probe failed or issued an import/deletion receipt.");
                Assert(File.ReadAllText(unrelated) == "fixture sentinel" &&
                    Directory.GetFiles(root).Length == 1 && Directory.GetDirectories(root).Length == 0,
                    "Probe touched unrelated content or left its test file behind.");
            }
            finally { File.Delete(unrelated); Directory.Delete(root, false); }
        }
        private static void ProbeFailure()
        {
            string root = Path.Combine(Path.GetTempPath(), "PhotoImport-probe-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string occupied = Path.Combine(root, "occupied.txt");
            File.WriteAllText(occupied, "do not replace");
            try
            {
                WorkResult result = DestinationProbe.Run(new WorkRequest { Operation = "probe", DestinationRoot = occupied });
                Assert(!result.Success && !result.Deleted && result.Receipt == null && !String.IsNullOrEmpty(result.Error) &&
                    File.ReadAllText(occupied) == "do not replace" && Directory.GetFiles(root).Length == 1,
                    "Failed probe lost a file or issued an import/deletion receipt.");
            }
            finally { File.Delete(occupied); Directory.Delete(root, false); }
        }

        private const string TaskXml = @"<Task version='1.2' xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'>
  <RegistrationInfo><Description>fixture only</Description><SecurityDescriptor>D:P(A;;FA;;;SY)</SecurityDescriptor></RegistrationInfo>
  <Triggers>
    <LogonTrigger id='login-a'><Enabled>true</Enabled><UserId>fixture-user</UserId><Delay>PT10S</Delay></LogonTrigger>
    <LogonTrigger id='login-b'><Enabled>false</Enabled><UserId>fixture-user</UserId></LogonTrigger>
    <CalendarTrigger id='daily'><StartBoundary>2026-10-07T12:00:00</StartBoundary><Enabled>false</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>
    <RegistrationTrigger><Enabled>true</Enabled></RegistrationTrigger>
  </Triggers>
  <Principals><Principal id='Author'><UserId>fixture-user</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure></Settings>
  <Actions Context='Author'><Exec><Command>C:\Fixture\PhotoImport.exe</Command><Arguments>--startup</Arguments><WorkingDirectory>C:\Fixture</WorkingDirectory></Exec></Actions>
</Task>";

        private static void StartupOff()
        {
            string updated = StartupSettings.SetLogonTriggersInXml(TaskXml, false);
            Assert(!StartupSettings.ReadLogonTriggersInXml(updated), "Login-off still reports enabled.");
            XmlDocument before = Document(TaskXml), after = Document(updated);
            Assert(XmlNodeAt(after, "/t:Task/t:Settings/t:Enabled").InnerText == "true", "Turning startup off disabled manual launch.");
            foreach (string xpath in new[] { "/t:Task/t:RegistrationInfo", "/t:Task/t:Principals", "/t:Task/t:Actions", "/t:Task/t:Settings", "/t:Task/t:Triggers/t:CalendarTrigger", "/t:Task/t:Triggers/t:RegistrationTrigger" })
                Assert(XmlNodeAt(before, xpath).OuterXml == XmlNodeAt(after, xpath).OuterXml, "Non-login definition changed: " + xpath);
            foreach (XmlElement trigger in Nodes(after, "/t:Task/t:Triggers/t:LogonTrigger"))
                Assert(trigger["Enabled"].InnerText == "false" && trigger["UserId"].InnerText == "fixture-user", "Login scope changed.");
        }
        private static void StartupOn()
        {
            string off = StartupSettings.SetLogonTriggersInXml(TaskXml, false);
            string on = StartupSettings.SetLogonTriggersInXml(off, true);
            Assert(StartupSettings.ReadLogonTriggersInXml(on), "Login-on reports disabled.");
            XmlDocument document = Document(on);
            foreach (XmlElement trigger in Nodes(document, "/t:Task/t:Triggers/t:LogonTrigger"))
                Assert(trigger["Enabled"].InnerText == "true", "One login trigger was not enabled.");
            Assert(XmlNodeAt(document, "/t:Task/t:Triggers/t:CalendarTrigger/t:Enabled").InnerText == "false", "Unrelated trigger changed.");
            Assert(StartupSettings.SetLogonTriggersInXml(on, true) == on, "Repeated startup-on rewrites definition.");
        }
        private static void StartupDefaults()
        {
            string xml = @"<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Triggers><LogonTrigger><UserId>fixture-user</UserId></LogonTrigger></Triggers></Task>";
            Assert(StartupSettings.ReadLogonTriggersInXml(xml), "Missing enabled nodes should default true.");
            string off = StartupSettings.SetLogonTriggersInXml(xml, false);
            Assert(!StartupSettings.ReadLogonTriggersInXml(off) && XmlNodeAt(Document(off), "/t:Task/t:Settings/t:Enabled").InnerText == "true", "Defaults not toggled safely.");
            string disabledTask = TaskXml.Replace("<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled>", "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>false</Enabled>");
            Assert(!StartupSettings.ReadLogonTriggersInXml(disabledTask), "Disabled task reported active startup.");
            string active = StartupSettings.SetLogonTriggersInXml(disabledTask, true);
            Assert(StartupSettings.ReadLogonTriggersInXml(active), "Task enabled flag was not restored.");
        }
        private static void StartupOrder()
        {
            string xml = @"<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Triggers><LogonTrigger><StartBoundary>2026-10-07T12:00:00</StartBoundary><Repetition><Interval>PT1H</Interval></Repetition><ExecutionTimeLimit>PT1H</ExecutionTimeLimit><UserId>fixture-user</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers></Task>";
            XmlElement trigger = XmlNodeAt(Document(StartupSettings.SetLogonTriggersInXml(xml, false)), "/t:Task/t:Triggers/t:LogonTrigger") as XmlElement;
            string[] order = trigger.ChildNodes.Cast<XmlNode>().Select(n => n.LocalName).ToArray();
            Assert(order.SequenceEqual(new[] { "StartBoundary", "Enabled", "Repetition", "ExecutionTimeLimit", "UserId", "Delay" }), "Inserted enabled node violates trigger schema order.");
        }
        private static void StartupInvalid()
        {
            string noLogin = @"<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Triggers><RegistrationTrigger /></Triggers></Task>";
            Assert(!StartupSettings.ReadLogonTriggersInXml(noLogin), "No login trigger reported enabled.");
            Reject(delegate { StartupSettings.SetLogonTriggersInXml(noLogin, false); });
            Reject(delegate { StartupSettings.ReadLogonTriggersInXml("<Task />"); });
            Reject(delegate { StartupSettings.ReadLogonTriggersInXml("<!DOCTYPE Task [<!ENTITY e SYSTEM 'file:///fixture-only'>]><Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'>&e;</Task>"); });
            Reject(delegate { StartupSettings.ReadLogonTriggersInXml(TaskXml.Replace("<Enabled>true</Enabled>", "<Enabled>maybe</Enabled>")); });
        }

        private static XmlDocument Document(string xml)
        {
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            document.LoadXml(xml);
            return document;
        }
        private static XmlNode XmlNodeAt(XmlDocument document, string xpath) { return document.SelectSingleNode(xpath, Ns(document)); }
        private static XmlNodeList Nodes(XmlDocument document, string xpath) { return document.SelectNodes(xpath, Ns(document)); }
        private static XmlNamespaceManager Ns(XmlDocument document)
        {
            var ns = new XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            return ns;
        }
        private static AppState Fresh() { var state = new AppState { Destinations = DemoData.Destinations() }; ImportSettings.Normalize(state); return state; }
        private static DestinationRecord Destination(string path) { return ImportSettings.ValidateDestination("位置", path); }
        private static void EqualPath(string input, string expected) { Assert(Destination(input).Path == expected, "Unexpected normalized path for fixture: " + input); }
        private static void RejectPaths(params string[] paths) { foreach (string path in paths) Reject(delegate { Destination(path); }); }
        private static void AssertDuplicate(string first, string second)
        {
            AppState state = Fresh();
            state.Destinations = new List<DestinationRecord> { Destination(first), Destination(second) };
            Reject(delegate { ImportSettings.ValidateConfig(state); });
        }
        private static void Reject(Action action)
        {
            bool rejected = false;
            try { action(); }
            catch (ArgumentException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Unsafe or invalid fixture was accepted.");
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Test(List<string> passed, string name, Action action)
        {
            try { action(); passed.Add(name); }
            catch (Exception error) { throw new InvalidOperationException("Configuration test failed: " + name, error); }
        }
    }
}
