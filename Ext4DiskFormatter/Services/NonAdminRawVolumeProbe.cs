using Ext4DiskFormatter.Models;

namespace Ext4DiskFormatter.Services;

internal static class NonAdminRawVolumeProbe
{
    public static bool TryGetBrowsableMountedVolumeRoot(PhysicalDisk disk, out string? volumeRoot, out string? reason)
    {
        foreach (string root in disk.MountedVolumeRoots)
        {
            string devicePath = ToRawVolumeDevicePath(root);

            try
            {
                using var stream = File.Open(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[512];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    volumeRoot = root;
                    reason = null;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = $"{devicePath}: {ex.Message}";
            }
        }

        volumeRoot = null;
        reason = disk.MountedVolumeRoots.Count == 0
            ? $"PhysicalDrive{disk.DiskNumber} has no mounted Windows volume path."
            : "No mounted raw volume could be opened for read access in this session.";
        return false;
    }

    public static string ExplainBrowseLimitation(PhysicalDisk disk)
    {
        if (disk.MountedVolumeRoots.Count == 0)
        {
            return $"PhysicalDrive{disk.DiskNumber} has no mounted Windows volume path. " +
                   "Without administrator privileges the app cannot open the raw physical drive, so ext4 browsing is unavailable in this session.";
        }

        var diagnostics = new List<string>();
        foreach (string root in disk.MountedVolumeRoots)
        {
            string devicePath = ToRawVolumeDevicePath(root);

            try
            {
                using var stream = File.Open(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[512];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                diagnostics.Add($"{devicePath} is readable without elevation ({bytesRead} bytes). ");
                return $"Windows allows read-only access to mounted raw volume {devicePath} without elevation, " +
                       "but the bundled SharpExt4 API cannot browse from a device handle or volume GUID path. " +
                       "Its public API only opens `PhysicalDriveN` or image-file style paths, so non-admin ext4 browsing is still unavailable with the current backend.";
            }
            catch (UnauthorizedAccessException)
            {
                diagnostics.Add($"{devicePath}: access denied");
            }
            catch (IOException ex)
            {
                diagnostics.Add($"{devicePath}: {ex.Message}");
            }
        }

        return $"Mounted Windows volume paths were found for PhysicalDrive{disk.DiskNumber}, " +
               $"but none could be opened for raw reads in this session. Details: {string.Join("; ", diagnostics)}";
    }

    public static string ToRawVolumeDevicePath(string root)
    {
        string trimmed = root.TrimEnd('\\');
        return trimmed.EndsWith(":", StringComparison.Ordinal)
            ? $"\\\\.\\{trimmed}"
            : trimmed;
    }
}