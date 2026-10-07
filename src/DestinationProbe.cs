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
                var target = ImportSettings.ValidateDestination("测试位置", request.DestinationRoot);
                string root = Path.GetFullPath(target.Path);
                if (!root.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(Path.GetPathRoot(root)).DriveType == DriveType.Removable)
                    throw new IOException("请选择照片存储位置，不能在存储卡上测试。");
                Directory.CreateDirectory(root);
                probe = Path.Combine(root, ".photoimport-check-" + Guid.NewGuid().ToString("N") + ".tmp");
                byte[] expected = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"));
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                { owned = true; stream.Write(expected, 0, expected.Length); stream.Flush(true); }
                byte[] actual = File.ReadAllBytes(probe);
                if (BitConverter.ToString(expected) != BitConverter.ToString(actual)) throw new IOException("读取测试未通过。");
                File.Delete(probe); owned = false;
                return new WorkResult { Success = true };
            }
            catch (Exception ex) { return new WorkResult { Error = ex.Message }; }
            finally { if (owned && probe != null) { try { File.Delete(probe); } catch { } } }
        }
    }
}
