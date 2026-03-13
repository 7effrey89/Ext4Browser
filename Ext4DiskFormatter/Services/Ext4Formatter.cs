using System.Diagnostics;
using System.Text;

namespace Ext4DiskFormatter.Services;

/// <summary>
/// Formats a physical disk to the ext4 filesystem.
///
/// Strategy:
///   1. Use DiskPart to wipe the disk and create a single MBR partition (type 0x83 Linux).
///   2. Mount the raw disk to WSL 2 via "wsl --mount --bare".
///   3. Discover the new Linux block-device path by comparing device lists.
///   4. Run "mkfs.ext4" inside WSL to write the filesystem.
///   5. Unmount the disk from WSL.
///   6. Optionally verify the result with SharpExt4.
///
/// Requirements:
///   • Windows 10 Build 21364 (21H2) or later with WSL 2 enabled.
///   • A WSL 2 distribution with e2fsprogs (mkfs.ext4) installed.
///   • Administrator privileges.
/// </summary>
public class Ext4Formatter
{
    private readonly Action<string> _log;

    public Ext4Formatter(Action<string>? log = null)
    {
        _log = log ?? Console.WriteLine;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Public entry point
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats the specified physical disk as ext4.
    /// </summary>
    /// <param name="diskNumber">Windows physical disk index (0-based).</param>
    /// <param name="label">Optional ext4 volume label (max 16 characters).</param>
    /// <exception cref="InvalidOperationException">
    ///   Thrown when WSL is not available or no distribution is installed.
    /// </exception>
    /// <exception cref="IOException">
    ///   Thrown when DiskPart or mkfs.ext4 fails.
    /// </exception>
    public void Format(int diskNumber, string label = "")
    {
        if (label.Length > 16)
            label = label[..16];

        // ── 1. Pre-flight checks ──────────────────────────────────────────
        _log("Checking WSL availability...");
        EnsureWslReady();

        // ── 2. Prepare disk with DiskPart ─────────────────────────────────
        _log("Initializing disk with DiskPart (clean + MBR + primary partition)...");
        PrepareDiskWithDiskPart(diskNumber);

        // ── 3. Snapshot WSL block devices BEFORE mount ───────────────────
        var devicesBefore = GetWslBlockDevices();

        // ── 4. Mount disk (bare) into WSL ─────────────────────────────────
        _log($"Attaching disk {diskNumber} to WSL...");
        MountDiskToWsl(diskNumber);

        try
        {
            // ── 5. Find the new device ────────────────────────────────────
            var devicesAfter = GetWslBlockDevices();
            string newDev = FindNewDevice(devicesBefore, devicesAfter);
            if (string.IsNullOrEmpty(newDev))
                throw new IOException(
                    "Could not identify the disk inside WSL. " +
                    "Ensure WSL 2 is configured and the disk is not locked by Windows.");

            _log($"Disk is visible in WSL as /dev/{newDev}.");

            // Give the kernel a moment to detect the partition created by DiskPart
            _log("Refreshing partition table in WSL...");
            RunWslCommand($"sudo blockdev --rereadpt /dev/{newDev}", ignoreErrors: true);
            Thread.Sleep(1500);

            // ── 6. Format partition 1 as ext4 ─────────────────────────────
            string partition = $"/dev/{newDev}1";
            string mkfsArgs  = string.IsNullOrWhiteSpace(label)
                ? $"sudo mkfs.ext4 -F {partition}"
                : $"sudo mkfs.ext4 -F -L \"{EscapeShell(label)}\" {partition}";

            _log($"Running mkfs.ext4 on {partition}...");
            RunWslCommand(mkfsArgs);

            _log("ext4 filesystem created successfully.");
        }
        finally
        {
            // ── 7. Always unmount ─────────────────────────────────────────
            _log("Detaching disk from WSL...");
            UnmountDiskFromWsl(diskNumber);
        }

        // ── 8. Verify with SharpExt4 ──────────────────────────────────────
        _log("Verifying filesystem with SharpExt4...");
        VerifyWithSharpExt4(diskNumber);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Pre-flight: WSL check
    // ──────────────────────────────────────────────────────────────────────

    private void EnsureWslReady()
    {
        string wslExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

        if (!File.Exists(wslExe))
            throw new InvalidOperationException(
                "wsl.exe not found. Please install Windows Subsystem for Linux.\n" +
                "Run: wsl --install   (then restart and re-run this application)");

        // Check that at least one distribution is registered
        string distros = RunProcessGetOutput(wslExe, "--list --quiet", suppressErrors: true);
        if (string.IsNullOrWhiteSpace(distros))
            throw new InvalidOperationException(
                "No WSL distribution is installed.\n" +
                "Install one from the Microsoft Store (e.g., Ubuntu) or run: wsl --install");

        // Verify mkfs.ext4 is available in the default distribution
        string which = RunWslCommandGetOutput("which mkfs.ext4 2>/dev/null || echo ''");
        if (string.IsNullOrWhiteSpace(which))
            throw new InvalidOperationException(
                "mkfs.ext4 not found in your WSL distribution.\n" +
                "Install e2fsprogs: sudo apt-get install -y e2fsprogs");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Step 2: DiskPart – wipe + create Linux MBR partition
    // ──────────────────────────────────────────────────────────────────────

    private void PrepareDiskWithDiskPart(int diskNumber)
    {
        // DiskPart script:
        //   select disk N  – choose the target disk
        //   clean          – remove ALL partitions and data
        //   convert mbr    – create an MBR partition table
        //   create partition primary – create one full-disk partition
        //   set id=83      – mark it as Linux (type 0x83)
        //   exit
        string script =
            $"select disk {diskNumber}\r\n" +
            "clean\r\n" +
            "convert mbr\r\n" +
            "create partition primary\r\n" +
            "set id=83\r\n" +
            "exit\r\n";

        string scriptPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(scriptPath, script);
            string output = RunProcessGetOutput("diskpart.exe", $"/s \"{scriptPath}\"");

            // DiskPart writes success/failure messages to stdout; check for common errors
            if (output.Contains("Virtual Disk Service error", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("is not a valid", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"DiskPart reported an error:\n{output}");
            }
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Step 3/5: Detect WSL block devices
    // ──────────────────────────────────────────────────────────────────────

    private List<string> GetWslBlockDevices()
    {
        // lsblk lists block devices; -d skips children, -n skips header, -o NAME lists names only
        string output = RunWslCommandGetOutput("lsblk -dno NAME 2>/dev/null");
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string FindNewDevice(List<string> before, List<string> after)
    {
        return after.FirstOrDefault(d => !before.Contains(d)) ?? string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Step 4/7: Mount / unmount via wsl --mount
    // ──────────────────────────────────────────────────────────────────────

    private void MountDiskToWsl(int diskNumber)
    {
        // --bare: attach as a raw block device without auto-mounting filesystems
        RunProcess("wsl.exe", $"--mount \\\\.\\PhysicalDrive{diskNumber} --bare");
    }

    private void UnmountDiskFromWsl(int diskNumber)
    {
        try
        {
            RunProcess("wsl.exe", $"--unmount \\\\.\\PhysicalDrive{diskNumber}");
        }
        catch (Exception ex)
        {
            // Best-effort unmount – log but don't rethrow
            _log($"Warning: could not unmount disk from WSL: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Step 8: Verify with SharpExt4
    // ──────────────────────────────────────────────────────────────────────

    private void VerifyWithSharpExt4(int diskNumber)
    {
        try
        {
            var disk = SharpExt4.ExtDisk.Open(diskNumber);
            if (disk.Partitions == null || disk.Partitions.Count == 0)
            {
                _log("Warning: SharpExt4 found no partitions on the disk (the MBR may need a moment to refresh).");
                return;
            }

            using var fs = SharpExt4.ExtFileSystem.Open(disk, disk.Partitions[0]);
            _log($"Verification OK – ext4 volume mounted. Label: \"{fs.VolumeLabel}\"");
        }
        catch (Exception ex)
        {
            // Verification is a best-effort check; the format itself may still have succeeded
            _log($"Note: SharpExt4 verification skipped: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers: run WSL commands
    // ──────────────────────────────────────────────────────────────────────

    private void RunWslCommand(string shellCommand, bool ignoreErrors = false)
    {
        // Forward the shell command to WSL via "wsl -- sh -c '<cmd>'"
        RunProcess("wsl.exe", BuildWslArgs(shellCommand), ignoreErrors);
    }

    private string RunWslCommandGetOutput(string shellCommand)
    {
        return RunProcessGetOutput("wsl.exe", BuildWslArgs(shellCommand), suppressErrors: true);
    }

    private static string BuildWslArgs(string shellCommand)
    {
        // Wrap in 'sh -c "..."'; inner double-quotes must be escaped
        string escaped = shellCommand.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"-- sh -c \"{escaped}\"";
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers: process execution
    // ──────────────────────────────────────────────────────────────────────

    private void RunProcess(string exe, string args, bool ignoreErrors = false)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new IOException($"Failed to start process: {exe}");

        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000); // 2-minute timeout

        if (!ignoreErrors && proc.ExitCode != 0)
        {
            var message = new StringBuilder();
            message.AppendLine($"'{exe} {args}' exited with code {proc.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(stderr))  message.AppendLine(stderr.Trim());
            if (!string.IsNullOrWhiteSpace(stdout))  message.AppendLine(stdout.Trim());
            throw new IOException(message.ToString().Trim());
        }
    }

    private string RunProcessGetOutput(string exe, string args, bool suppressErrors = false)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new IOException($"Failed to start process: {exe}");

        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30_000);

        if (!suppressErrors && proc.ExitCode != 0)
        {
            string stderr = proc.StandardError.ReadToEnd();
            throw new IOException(
                $"'{exe} {args}' exited with code {proc.ExitCode}: {stderr.Trim()}");
        }

        return output;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers: string utilities
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Escapes a string for use inside a shell single-quoted argument.</summary>
    private static string EscapeShell(string value) =>
        value.Replace("'", "'\\''");
}
