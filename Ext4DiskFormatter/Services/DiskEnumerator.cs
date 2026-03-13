using System.Management;
using Ext4DiskFormatter.Models;

namespace Ext4DiskFormatter.Services;

/// <summary>
/// Enumerates physical disk drives on the system using WMI (Win32_DiskDrive).
/// </summary>
public static class DiskEnumerator
{
    /// <summary>
    /// Returns all removable/external physical drives (i.e., USB drives).
    /// </summary>
    public static List<PhysicalDisk> GetRemovableDisks()
    {
        var disks = new List<PhysicalDisk>();

        // Query WMI for removable and external hard disks
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Model, Size, Status, MediaType " +
            "FROM Win32_DiskDrive " +
            "WHERE MediaType='Removable Media' OR MediaType='External hard disk media'");

        foreach (ManagementObject drive in searcher.Get())
        {
            string deviceId = drive["DeviceID"]?.ToString() ?? string.Empty;

            // DeviceID looks like "\\.\PHYSICALDRIVE1" – extract the number
            if (!TryParsePhysicalDriveNumber(deviceId, out int diskNumber))
                continue;

            var disk = new PhysicalDisk
            {
                DiskNumber = diskNumber,
                Model      = drive["Model"]?.ToString()?.Trim() ?? "Unknown",
                SizeBytes  = ulong.TryParse(drive["Size"]?.ToString(), out ulong sz) ? sz : 0,
                Status     = drive["Status"]?.ToString() ?? "Unknown",
                MediaType  = drive["MediaType"]?.ToString() ?? "Unknown",
            };
            disks.Add(disk);
        }

        disks.Sort((a, b) => a.DiskNumber.CompareTo(b.DiskNumber));
        return disks;
    }

    private static bool TryParsePhysicalDriveNumber(string deviceId, out int number)
    {
        number = -1;
        const string prefix = "\\\\.\\PHYSICALDRIVE";
        if (deviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return int.TryParse(deviceId[prefix.Length..], out number);
        return false;
    }
}
