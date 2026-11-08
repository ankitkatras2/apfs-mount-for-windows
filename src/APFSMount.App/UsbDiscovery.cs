using System.Management;
using APFSMount.Core.Models;

namespace APFSMount.App;

internal static class UsbDiscovery
{
    public static IReadOnlyList<StorageSource> Read()
    {
        // MSFT_Disk BusType recognizes USB-attached SCSI (UASP) bridges too.
        using var searcher = new ManagementObjectSearcher(
            @"\\.\root\Microsoft\Windows\Storage",
            "SELECT Number, FriendlyName, Size, UniqueId, SerialNumber FROM MSFT_Disk WHERE BusType = 7 AND IsBoot = FALSE AND IsSystem = FALSE");
        using var results = searcher.Get();
        var disks = new List<StorageSource>();
        foreach (ManagementObject disk in results)
        {
            using (disk)
            {
                var number = Convert.ToUInt32(disk["Number"]);
                var length = checked((long)Convert.ToUInt64(disk["Size"]));
                var path = @"\\.\PhysicalDrive" + number;
                var id = $"{path}|{disk["UniqueId"]}|{disk["SerialNumber"]}|{length}";
                disks.Add(new StorageSource(id, Convert.ToString(disk["FriendlyName"]) ?? path,
                    path, StorageSourceKind.PhysicalDisk, length, true, "USB"));
            }
        }
        return disks;
    }
}
