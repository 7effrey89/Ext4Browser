namespace Ext4DiskFormatter.Services;

internal static class SharpExt4DiskAccessor
{
    public static SharpExt4.ExtDisk OpenDiskWithRetry(
        int diskNumber,
        Action<string>? log = null,
        int maxAttempts = 5,
        int delayMilliseconds = 1000)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                log?.Invoke($"Opening PhysicalDrive{diskNumber} with SharpExt4 (attempt {attempt}/{maxAttempts})...");
                SharpExt4.ExtDisk disk = SharpExt4.ExtDisk.Open(diskNumber);
                WindowsDiskMetadata.TryRepairSharpExt4Metadata(diskNumber, disk, log);
                return disk;
            }
            catch (Exception ex)
            {
                lastError = ex;
                AppLogger.Error($"SharpExt4 failed to open PhysicalDrive{diskNumber} on attempt {attempt}.", ex);

                if (attempt == maxAttempts)
                    break;

                log?.Invoke($"SharpExt4 open failed: {ex.Message}");
                RefreshWindowsStorageCache(log);
                Thread.Sleep(delayMilliseconds);
            }
        }

        throw new IOException(
            $"SharpExt4 could not open PhysicalDrive{diskNumber} after {maxAttempts} attempts. " +
            "See the application log for the detailed exception trail.",
            lastError);
    }

    public static void RefreshWindowsStorageCache(Action<string>? log = null)
    {
        string scriptPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(scriptPath, "rescan" + Environment.NewLine + "exit" + Environment.NewLine);
            log?.Invoke("Refreshing Windows storage cache...");

            var process = new System.Diagnostics.ProcessStartInfo("diskpart.exe", $"/s \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = System.Diagnostics.Process.Start(process)
                ?? throw new IOException("Failed to start diskpart.exe for storage rescan.");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);

            if (proc.ExitCode != 0)
            {
                throw new IOException(
                    $"diskpart rescan failed with exit code {proc.ExitCode}. " +
                    $"STDERR: {stderr.Trim()} STDOUT: {stdout.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(stdout))
                AppLogger.Info($"diskpart rescan output:{Environment.NewLine}{stdout.Trim()}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Refreshing Windows storage cache failed.", ex);
            log?.Invoke($"Warning: storage cache refresh failed: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch
            {
            }
        }
    }
}

internal static class WindowsDiskMetadata
{
    private static readonly System.Reflection.FieldInfo CapacityField =
        typeof(SharpExt4.ExtDisk).GetField("capacity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not locate SharpExt4.ExtDisk.capacity field.");

    private static readonly System.Reflection.FieldInfo PartitionsField =
        typeof(SharpExt4.ExtDisk).GetField("partitions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not locate SharpExt4.ExtDisk.partitions field.");

    private static readonly System.Reflection.FieldInfo PartitionOffsetField =
        typeof(SharpExt4.Partition).GetField("<backing_store>Offset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not locate SharpExt4.Partition offset field.");

    private static readonly System.Reflection.FieldInfo PartitionSizeField =
        typeof(SharpExt4.Partition).GetField("<backing_store>Size", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not locate SharpExt4.Partition size field.");

    public static void TryRepairSharpExt4Metadata(int diskNumber, SharpExt4.ExtDisk disk, Action<string>? log = null)
    {
        if (!NeedsRepair(disk))
            return;

        try
        {
            DiskMetadata? metadata = ReadDiskMetadata(diskNumber);
            if (metadata is null || metadata.Partitions.Count == 0)
                return;

            var repairedPartitions = new List<SharpExt4.Partition>(metadata.Partitions.Count);
            foreach (PartitionMetadata partition in metadata.Partitions)
            {
                var repairedPartition = new SharpExt4.Partition();
                PartitionOffsetField.SetValue(repairedPartition, partition.Offset);
                PartitionSizeField.SetValue(repairedPartition, partition.Size);
                repairedPartitions.Add(repairedPartition);
            }

            if (disk.Capacity == 0 && metadata.SizeBytes > 0)
                CapacityField.SetValue(disk, metadata.SizeBytes);

            PartitionsField.SetValue(disk, repairedPartitions);

            string summary = string.Join(
                ", ",
                metadata.Partitions.Select((partition, index) =>
                    $"p{index + 1}: offset {partition.Offset} size {partition.Size}"));

            AppLogger.Warning(
                $"SharpExt4 returned invalid disk metadata for PhysicalDrive{diskNumber}. " +
                $"Repaired using Windows partition data. Capacity={metadata.SizeBytes}. Partitions={summary}.");
            log?.Invoke("SharpExt4 returned invalid partition metadata; using Windows partition layout as a fallback.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to repair SharpExt4 metadata for PhysicalDrive{diskNumber}.", ex);
            log?.Invoke($"Warning: could not repair SharpExt4 metadata from Windows partition data: {ex.Message}");
        }
    }

    private static bool NeedsRepair(SharpExt4.ExtDisk disk)
    {
        return disk.Capacity == 0 ||
               disk.Partitions is null ||
               disk.Partitions.Count == 0 ||
               disk.Partitions.All(partition => partition.Offset == 0 && partition.Size == 0);
    }

    private static DiskMetadata? ReadDiskMetadata(int diskNumber)
    {
        using var diskSearcher = new System.Management.ManagementObjectSearcher(
            "root\\CIMV2",
            $"SELECT Size FROM Win32_DiskDrive WHERE Index = {diskNumber}");

        ulong sizeBytes = 0;
        foreach (System.Management.ManagementObject disk in diskSearcher.Get())
        {
            sizeBytes = ulong.TryParse(disk["Size"]?.ToString(), out ulong parsed) ? parsed : 0;
            break;
        }

        using var partitionSearcher = new System.Management.ManagementObjectSearcher(
            "root\\CIMV2",
            $"SELECT Index, Size, StartingOffset FROM Win32_DiskPartition WHERE DiskIndex = {diskNumber}");

        var partitions = new List<PartitionMetadata>();
        foreach (System.Management.ManagementObject partition in partitionSearcher.Get())
        {
            ulong offset = ulong.TryParse(partition["StartingOffset"]?.ToString(), out ulong parsedOffset) ? parsedOffset : 0;
            ulong size = ulong.TryParse(partition["Size"]?.ToString(), out ulong parsedSize) ? parsedSize : 0;
            uint index = uint.TryParse(partition["Index"]?.ToString(), out uint parsedIndex) ? parsedIndex : uint.MaxValue;

            if (offset == 0 || size == 0)
                continue;

            partitions.Add(new PartitionMetadata(index, offset, size));
        }

        partitions.Sort((left, right) => left.Index.CompareTo(right.Index));
        return new DiskMetadata(sizeBytes, partitions);
    }

    private sealed record DiskMetadata(ulong SizeBytes, List<PartitionMetadata> Partitions);

    private sealed record PartitionMetadata(uint Index, ulong Offset, ulong Size);
}