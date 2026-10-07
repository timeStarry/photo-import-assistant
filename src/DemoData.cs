using System.Collections.Generic;

namespace PhotoImportV2
{
    // Synthetic addresses, used only by previews and offline fixtures.
    internal static class DemoData
    {
        internal static List<DestinationRecord> Destinations()
        {
            return new List<DestinationRecord> {
                ImportSettings.ValidateDestination("NAS 照片库", "https://nas.example/Photos/Camera"),
                ImportSettings.ValidateDestination("本地备份", @"C:\Photos\Camera")
            };
        }
    }
}
