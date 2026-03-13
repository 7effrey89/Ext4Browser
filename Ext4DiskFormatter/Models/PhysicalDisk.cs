namespace Ext4DiskFormatter.Models;

/// <summary>
/// Represents a physical disk drive detected on the system.
/// </summary>
public class PhysicalDisk
{
    /// <summary>Windows physical disk index (e.g., 1 for \\.\PhysicalDrive1).</summary>
    public int DiskNumber { get; set; }

    /// <summary>Disk model/manufacturer name.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Total disk capacity in bytes.</summary>
    public ulong SizeBytes { get; set; }

    /// <summary>Current disk status as reported by WMI.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Media type (e.g., "Removable Media", "External hard disk media").</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Human-readable size string (e.g., "7.45 GB").</summary>
    public string FormattedSize => FormatBytes(SizeBytes);

    private static string FormatBytes(ulong bytes)
    {
        string[] suffixes = new[] { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:F2} {suffixes[i]}";
    }
}
