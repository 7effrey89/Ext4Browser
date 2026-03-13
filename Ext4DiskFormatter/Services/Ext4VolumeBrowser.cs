namespace Ext4DiskFormatter.Services;

/// <summary>
/// Lists files and directories from an ext2/ext3/ext4 volume using SharpExt4.
/// </summary>
public sealed class Ext4VolumeBrowser
{
    private readonly Action<string> _log;

    public Ext4VolumeBrowser(Action<string>? log = null)
    {
        _log = log ?? Console.WriteLine;
    }

    /// <summary>
    /// Lists the contents of a file or directory on the specified disk.
    /// </summary>
    public void ListContents(int diskNumber, string path = "/", bool recursive = true)
    {
        string normalizedPath = NormalizePath(path);
        AppLogger.Info($"List contents requested for PhysicalDrive{diskNumber}, Path='{normalizedPath}', Recursive={recursive}.");

        using var disk = SharpExt4DiskAccessor.OpenDiskWithRetry(diskNumber, _log);
        using var fs = OpenMountedFileSystem(disk, diskNumber);

        if (fs.FileExists(normalizedPath))
        {
            PrintFile(fs, normalizedPath);
            return;
        }

        if (!fs.DirectoryExists(normalizedPath))
            throw new IOException($"Path '{normalizedPath}' was not found on the selected volume.");

        _log($"Contents of {normalizedPath} on PhysicalDrive{diskNumber}:");
        _log(string.Empty);

        string[] directories = fs
            .GetDirectories(normalizedPath, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(pathEntry => pathEntry, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] files = fs
            .GetFiles(normalizedPath, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(pathEntry => pathEntry, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _log("[Directories]");
        _log($"  {normalizedPath}");
        if (directories.Length == 0)
        {
            _log("  (none)");
        }
        else
        {
            foreach (string directory in directories)
            {
                _log($"  {directory} | modified {FormatTimestamp(SafeGetLastWriteTime(fs, directory))}");
            }
        }

        _log(string.Empty);
        _log("[Files]");
        if (files.Length == 0)
        {
            _log("  (none)");
        }
        else
        {
            foreach (string file in files)
            {
                _log($"  {file} | {FormatBytes(fs.GetFileLength(file))} | modified {FormatTimestamp(SafeGetLastWriteTime(fs, file))}");
            }
        }

        _log(string.Empty);
        _log($"Summary: {directories.Length + 1} director{(directories.Length == 0 ? "y" : "ies")}, {files.Length} file{(files.Length == 1 ? string.Empty : "s")}.");
    }

    public void CreateTextFile(int diskNumber, string path, string contents)
    {
        string normalizedPath = NormalizeFilePath(path);
        AppLogger.Info($"Create text file requested for PhysicalDrive{diskNumber}, Path='{normalizedPath}'.");

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(contents);
        ulong expectedLength = (ulong)System.Text.Encoding.UTF8.GetByteCount(contents);
        DateTime timestamp = DateTime.Now;

        using var disk = SharpExt4DiskAccessor.OpenDiskWithRetry(diskNumber, _log);
        using var fs = OpenMountedFileSystem(disk, diskNumber);

        string parentPath = GetParentPath(normalizedPath);
        if (!fs.DirectoryExists(parentPath))
            throw new IOException($"Parent directory '{parentPath}' was not found on the selected volume.");

        if (fs.FileExists(normalizedPath))
        {
            fs.DeleteFile(normalizedPath);
            AppLogger.Info($"Deleted existing file '{normalizedPath}' before recreating it to avoid stale bytes on overwrite.");
        }

        using var file = fs.OpenFile(normalizedPath, FileMode.Create, FileAccess.Write);
        file.Write(bytes, 0, bytes.Length);
        file.Flush();

        TrySetTimestamps(fs, normalizedPath, timestamp);

        VerifyCreatedFile(diskNumber, normalizedPath, bytes, expectedLength);
        _log($"Created text file at {normalizedPath} ({FormatBytes((ulong)bytes.Length)}).");
    }

    public void ExportFileToWindowsPath(int diskNumber, string sourcePath, string destinationPath)
    {
        string normalizedSourcePath = NormalizeFilePath(sourcePath);
        AppLogger.Info(
            $"Export file requested for PhysicalDrive{diskNumber}, Source='{normalizedSourcePath}', Destination='{destinationPath}'.");

        using var disk = SharpExt4DiskAccessor.OpenDiskWithRetry(diskNumber, _log);
        using var fs = OpenMountedFileSystem(disk, diskNumber);

        if (!fs.FileExists(normalizedSourcePath))
            throw new IOException($"File '{normalizedSourcePath}' was not found on the selected volume.");

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        using var input = fs.OpenFile(normalizedSourcePath, FileMode.Open, FileAccess.Read);
        using var output = File.Create(destinationPath);
        input.CopyTo(output);

        _log($"Copied {normalizedSourcePath} to {destinationPath}.");
    }

    private void VerifyCreatedFile(int diskNumber, string path, byte[] expectedContents, ulong expectedLength)
    {
        using var verificationDisk = SharpExt4DiskAccessor.OpenDiskWithRetry(diskNumber, _log);
        using var verificationFs = OpenMountedFileSystem(verificationDisk, diskNumber);

        if (!verificationFs.FileExists(path))
            throw new IOException($"File '{path}' was written but could not be found when reopening the ext volume.");

        ulong actualLength = verificationFs.GetFileLength(path);
        if (actualLength != expectedLength)
        {
            throw new IOException(
                $"File '{path}' was written but reopened with length {actualLength} bytes instead of {expectedLength} bytes.");
        }

        using var verificationStream = verificationFs.OpenFile(path, FileMode.Open, FileAccess.Read);
        byte[] actualContents = new byte[expectedContents.Length];
        int totalRead = 0;
        while (totalRead < actualContents.Length)
        {
            int read = verificationStream.Read(actualContents, totalRead, actualContents.Length - totalRead);
            if (read == 0)
                break;

            totalRead += read;
        }

        if (totalRead != actualContents.Length || !actualContents.AsSpan().SequenceEqual(expectedContents))
            throw new IOException($"File '{path}' was written but reopened with different contents than expected.");
    }

    private void TrySetTimestamps(SharpExt4.ExtFileSystem fs, string path, DateTime timestamp)
    {
        try
        {
            fs.SetCreationTime(path, timestamp);
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Unable to set creation time for '{path}': {ex.Message}");
        }

        try
        {
            fs.SetLastAccessTime(path, timestamp);
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Unable to set last access time for '{path}': {ex.Message}");
        }

        try
        {
            fs.SetLastWriteTime(path, timestamp);
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Unable to set last write time for '{path}': {ex.Message}");
        }
    }

    private SharpExt4.ExtFileSystem OpenMountedFileSystem(SharpExt4.ExtDisk disk, int diskNumber)
    {
        if (disk.Partitions == null || disk.Partitions.Count == 0)
            throw new IOException("The selected drive does not expose any partitions that can be inspected.");

        AppLogger.Info(
            $"PhysicalDrive{diskNumber} exposed {disk.Partitions.Count} partition(s) to SharpExt4. " +
            $"Disk capacity: {FormatBytes(disk.Capacity)}.");

        var mountFailures = new List<string>();

        for (int index = 0; index < disk.Partitions.Count; index++)
        {
            SharpExt4.Partition partition = disk.Partitions[index];
            string partitionDescription = DescribePartition(index, partition);

            try
            {
                AppLogger.Info($"Attempting to mount PhysicalDrive{diskNumber} {partitionDescription}.");
                var fs = SharpExt4.ExtFileSystem.Open(disk, partition);
                AppLogger.Info($"Mounted PhysicalDrive{diskNumber} {partitionDescription}. Volume label='{SafeGetVolumeLabel(fs)}'.");
                return fs;
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"Failed to mount PhysicalDrive{diskNumber} {partitionDescription}: {ex.Message}");
                mountFailures.Add($"{partitionDescription}: {ex.Message}");
            }
        }

        throw new IOException(
            $"SharpExt4 could not mount any partition on PhysicalDrive{diskNumber}. " +
            "This usually means the volume is not ext2/ext3/ext4, the filesystem is damaged, or it uses ext4 features unsupported by SharpExt4. " +
            $"Partitions tried: {string.Join("; ", mountFailures)}");
    }

    private void PrintFile(SharpExt4.ExtFileSystem fs, string path)
    {
        _log($"File details for {path}:");
        _log($"  Size     : {FormatBytes(fs.GetFileLength(path))}");
        _log($"  Modified : {FormatTimestamp(SafeGetLastWriteTime(fs, path))}");
        _log($"  Accessed : {FormatTimestamp(SafeGetLastAccessTime(fs, path))}");
        _log($"  Mode     : 0{Convert.ToString(fs.GetMode(path), 8)}");
    }

    private static string NormalizePath(string? path)
    {
        string normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "/";

        normalized = normalized.Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        return normalized.Length > 1
            ? normalized.TrimEnd('/')
            : normalized;
    }

    private static string NormalizeFilePath(string? path)
    {
        string normalized = NormalizePath(path);
        if (normalized == "/")
            throw new IOException("Please provide a file path, for example '/notes.txt'.");

        return normalized;
    }

    private static string GetParentPath(string path)
    {
        int separatorIndex = path.LastIndexOf('/');
        return separatorIndex <= 0
            ? "/"
            : path[..separatorIndex];
    }

    private static DateTime? SafeGetLastWriteTime(SharpExt4.ExtFileSystem fs, string path)
    {
        try
        {
            return fs.GetLastWriteTime(path);
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? SafeGetLastAccessTime(SharpExt4.ExtFileSystem fs, string path)
    {
        try
        {
            return fs.GetLastAccessTime(path);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeGetVolumeLabel(SharpExt4.ExtFileSystem fs)
    {
        try
        {
            return string.IsNullOrWhiteSpace(fs.VolumeLabel)
                ? "(none)"
                : fs.VolumeLabel;
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static string DescribePartition(int index, SharpExt4.Partition partition) =>
        $"partition {index + 1} (offset {partition.Offset} bytes, size {FormatBytes(partition.Size)})";

    private static string FormatTimestamp(DateTime? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown";

    private static string FormatBytes(ulong bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int index = 0;

        while (size >= 1024 && index < suffixes.Length - 1)
        {
            size /= 1024;
            index++;
        }

        return $"{size:F2} {suffixes[index]}";
    }
}