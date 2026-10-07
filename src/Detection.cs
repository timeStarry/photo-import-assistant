using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoImportV2
{
    public static class Detector
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeInformation(string root, StringBuilder name, int nameSize, out uint serial, out uint max, out uint flags, StringBuilder fs, int fsSize);
        static readonly HashSet<string> Extensions = new HashSet<string>(new[] { ".jpg", ".jpeg", ".nef", ".nrw", ".mov", ".mp4", ".avi", ".tif", ".tiff" }, StringComparer.OrdinalIgnoreCase);
        public static DiskSnapshot ReadIdentity(string root)
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady) throw new IOException("存储卡尚未就绪或已拔出");
            var disk = new DiskSnapshot { Root = drive.RootDirectory.FullName, Capacity = drive.TotalSize, FreeSpace = drive.AvailableFreeSpace, Label = drive.VolumeLabel.Trim() };
            uint serial, max, flags;
            var fileSystem = new StringBuilder(256);
            if (!GetVolumeInformation(disk.Root, new StringBuilder(256), 256, out serial, out max, out flags, fileSystem, 256))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            disk.Serial = serial.ToString("X8");
            disk.FileSystem = fileSystem.ToString();
            string path = Path.Combine(root, IdentityVerifier.MarkerFileName);
            if (File.Exists(path))
            {
                try
                {
                    if (new FileInfo(path).Length > 16384 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("无效标识文件");
                    var marker = JsonFile.Read<CardMarker>(path);
                    Guid id;
                    if (marker == null || marker.Version != 1 || !Guid.TryParse(marker.CardId, out id)) throw new IOException("标识内容无效");
                    disk.MarkerId = id.ToString("D").ToUpperInvariant();
                }
                catch (Exception ex) { disk.MarkerError = "登记标识无法读取：" + ex.Message; }
            }
            return disk;
        }
        public static List<DiskSnapshot> Scan()
        {
            var result = new List<DiskSnapshot>();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Removable) continue;
                    if (!drive.IsReady) continue;
                    DiskSnapshot disk = ReadIdentity(drive.Name);
                    try
                    {
                        string dcim = Path.Combine(drive.Name, "DCIM");
                        if (Directory.Exists(dcim)) Enumerate(dcim, disk, 0);
                    }
                    catch (Exception ex) { disk.Error = "读取媒体列表失败：" + ex.Message; }
                    result.Add(disk);
                }
                catch (Exception ex) { result.Add(new DiskSnapshot { Root = drive.Name, Error = "读取卡片信息失败：" + ex.Message }); }
            }
            return result;
        }
        static void Enumerate(string directory, DiskSnapshot disk, int depth)
        {
            if (depth > 8) throw new IOException("媒体目录层级异常");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || !Extensions.Contains(info.Extension)) continue;
                disk.Media.Add(new MediaItem { SourcePath = info.FullName, RelativePath = info.FullName.Substring(disk.Root.Length), Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks });
            }
            foreach (string child in Directory.EnumerateDirectories(directory)) Enumerate(child, disk, depth + 1);
        }
        public static bool StillSame(DiskSnapshot expected, string cardId)
        {
            try
            {
                var now = ReadIdentity(expected.Root);
                return now.Serial == expected.Serial && now.Capacity == expected.Capacity && String.Equals(now.MarkerId, cardId, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
    public sealed class StateStore
    {
        public string Root { get; private set; }
        public AppState State { get; private set; }
        public bool MigratedLegacy { get; private set; }
        public string LogPath { get { return Path.Combine(Root, "agent.log"); } }
        readonly bool preview;
        readonly object logLock = new object();
        public StateStore(bool isPreview)
        {
            preview = isPreview;
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoImport");
            if (preview) { State = new AppState { Destinations = DemoData.Destinations() }; ImportSettings.Normalize(State); return; }
            Directory.CreateDirectory(Root);
            string path = Path.Combine(Root, "state.json");
            if (File.Exists(path))
            {
                State = JsonFile.Read<AppState>(path);
                if (State == null || State.Version != 2 || State.Cards == null) throw new IOException("登记配置损坏，请检查 " + path);
            }
            else
            {
                State = new AppState { Destinations = new List<DestinationRecord>() };
                string old = @"C:\ProgramData\PhotoImport\registered-cards.json";
                if (File.Exists(old))
                {
                    MigratedLegacy = true;
                    string json = File.ReadAllText(old).Trim();
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var entries = json.StartsWith("[") ? serializer.Deserialize<LegacyCard[]>(json) : new[] { serializer.Deserialize<LegacyCard>(json) };
                    foreach (var entry in entries)
                    {
                        Guid id;
                        State.Cards.Add(new CardRecord { Id = Guid.TryParse(entry.Uuid, out id) ? id.ToString("D").ToUpperInvariant() : Guid.NewGuid().ToString("D").ToUpperInvariant(), Name = entry.Name ?? entry.Label ?? "旧版存储卡", Capacity = entry.CapacityBytes, Label = entry.Label, LegacySerial = entry.VolumeSerialNumber, LegacyGuid = entry.VolumeGuid ?? entry.Uuid, NeedsBinding = true, AutoImport = false, DeleteMode = "Ask" });
                    }
                }
                ImportSettings.Normalize(State); Save();
            }
            if (ImportSettings.Normalize(State)) Save();
        }
        public void Save() { if (!preview) JsonFile.Write(Path.Combine(Root, "state.json"), State); }
        public CardRecord Find(string id) { return String.IsNullOrEmpty(id) ? null : State.Cards.FirstOrDefault(c => !c.NeedsBinding && String.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)); }
        public void Log(string message)
        {
            if (preview) return;
            try
            {
                lock (logLock)
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 4 * 1024 * 1024)
                    {
                        string archive = Path.Combine(Root, "agent.previous.log");
                        File.Copy(LogPath, archive, true);
                        File.WriteAllText(LogPath, "", Encoding.UTF8);
                    }
                    File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
        public class LegacyCard
        {
            public string Uuid { get; set; }
            public string Name { get; set; }
            public string Label { get; set; }
            public string VolumeGuid { get; set; }
            public string VolumeSerialNumber { get; set; }
            public long CapacityBytes { get; set; }
        }
    }
}
