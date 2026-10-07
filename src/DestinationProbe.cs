using System;
using System.IO;
using System.Text;

namespace PhotoImportV2
{
    public static class DestinationProbe
    {
        public static WorkResult Run(WorkRequest request)
        {
            string probe = null;
            bool owned = false;
            try
            {
                if (request.Target != null && StorageSettings.IsDirect(request.Target)) return RemoteStores.Create(request.Target).Probe(!request.ReadOnlyProbe);
                var target = ImportSettings.ValidateDestination("测试位置", request.DestinationRoot);
                string root = Path.GetFullPath(target.Path);
                if (!root.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(Path.GetPathRoot(root)).DriveType == DriveType.Removable)
                    throw new IOException("请选择照片存储位置，不能在存储卡上测试。");
                if (request.ReadOnlyProbe)
                {
                    // Automatic preflight is read-only. A previous write test remains distinct.
                    if (!Directory.Exists(root)) throw new IOException("目标文件夹不存在，请先创建或测试连接。");
                    using (var dirs = new TransferDirectories(root, false)) { }
                    long? free = null;
                    if (!root.StartsWith(@"\\", StringComparison.Ordinal)) free = new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace;
                    return new WorkResult { Success = true, Detail = "可访问 · 写入权限尚未验证", FreeBytes = free };
                }
                Directory.CreateDirectory(root);
                probe = Path.Combine(root, ".photoimport-check-" + Guid.NewGuid().ToString("N") + ".tmp");
                byte[] expected = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"));
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                { owned = true; stream.Write(expected, 0, expected.Length); stream.Flush(true); }
                byte[] actual = File.ReadAllBytes(probe);
                if (BitConverter.ToString(expected) != BitConverter.ToString(actual)) throw new IOException("读取测试未通过。");
                File.Delete(probe); owned = false;
                return new WorkResult { Success = true, Detail = "读写校验通过" };
            }
            catch (Exception ex) { return new WorkResult { Error = ex.Message }; }
            finally { if (owned && probe != null) { try { File.Delete(probe); } catch { } } }
        }
    }
}
