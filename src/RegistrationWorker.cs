using System;
using System.IO;

namespace PhotoImportV2
{
    public static class RegistrationWorker
    {
        public static WorkResult Run(WorkRequest request)
        {
            try
            {
                Guid id;
                if (!Guid.TryParse(request.CardId, out id) || request.ExpectedCapacity <= 0 || String.IsNullOrEmpty(request.ExpectedSerial))
                    throw new IOException("登记请求缺少有效的卡片 UUID、容量或序列号。");
                var disk = Detector.ReadIdentity(request.SourceRoot);
                if (new DriveInfo(disk.Root).DriveType != DriveType.Removable || disk.Serial != request.ExpectedSerial ||
                    disk.Capacity != request.ExpectedCapacity || disk.MarkerId != request.PreviousCardId || disk.MarkerError != null)
                    throw new IOException("卡片状态已改变或登记文件损坏，请重新扫描后检查。");
                JsonFile.Write(Path.Combine(disk.Root, IdentityVerifier.MarkerFileName), new CardMarker {
                    Version = 1, CardId = id.ToString("D").ToUpperInvariant(), CreatedUtc = DateTime.UtcNow.ToString("o") });
                var check = Detector.ReadIdentity(disk.Root);
                if (check.MarkerId != id.ToString("D").ToUpperInvariant() || check.Serial != disk.Serial || check.Capacity != disk.Capacity)
                    throw new IOException("登记文件写入后未通过复核，请重新扫描。");
                return new WorkResult { Success = true };
            }
            catch (Exception ex) { return new WorkResult { Error = ex.Message }; }
        }
    }
}
