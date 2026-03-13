using Ext4DiskFormatter.Models;
using Ext4DiskFormatter.Services;
using System.Security.Principal;

namespace Ext4DiskFormatter;

/// <summary>
/// Entry point for Ext4DiskFormatter – a Windows console application that formats
/// a USB drive (or any removable disk) to the Linux ext4 filesystem.
///
/// Prerequisites:
///   • Run as Administrator.
///   • WSL 2 enabled with a distribution that has e2fsprogs installed
///     (e.g., Ubuntu:  sudo apt-get install -y e2fsprogs).
///
/// Based on the SharpExt4 library:
///   https://github.com/nickdu088/SharpExt4
/// </summary>
internal class Program
{
    private const string AppTitle = "Ext4DiskFormatter";

    static int Main(string[] args)
    {
        Console.Title = AppTitle;
        PrintBanner();

        // ── Admin check ──────────────────────────────────────────────────
        if (!IsAdministrator())
        {
            WriteError(
                "This application requires administrator privileges.\n" +
                "Please right-click the executable and choose 'Run as administrator'.");
            return ExitCode.NotAdmin;
        }

        // ── Enumerate removable drives via WMI ───────────────────────────
        Console.WriteLine("Scanning for removable drives...\n");
        List<PhysicalDisk> disks;
        try
        {
            disks = DiskEnumerator.GetRemovableDisks();
        }
        catch (Exception ex)
        {
            WriteError($"Failed to enumerate disks: {ex.Message}");
            return ExitCode.EnumerationFailed;
        }

        if (disks.Count == 0)
        {
            Console.WriteLine("No removable drives found. Connect a USB drive and try again.");
            return ExitCode.NoDrivesFound;
        }

        // ── Present drive list ───────────────────────────────────────────
        PrintDriveList(disks);

        // ── Drive selection ──────────────────────────────────────────────
        Console.Write("Enter the number of the drive to format (0 to cancel): ");
        if (!int.TryParse(Console.ReadLine(), out int choice) ||
            choice < 0 || choice > disks.Count)
        {
            Console.WriteLine("\nInvalid selection. Exiting.");
            return ExitCode.InvalidSelection;
        }

        if (choice == 0)
        {
            Console.WriteLine("\nOperation cancelled.");
            return ExitCode.Cancelled;
        }

        PhysicalDisk selected = disks[choice - 1];

        // ── Confirmation ─────────────────────────────────────────────────
        Console.WriteLine();
        WriteWarning("⚠  WARNING  ⚠");
        WriteWarning("All data on the selected drive will be PERMANENTLY ERASED.");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"   Drive  : {selected.Model}");
        Console.WriteLine($"   Disk # : PhysicalDrive{selected.DiskNumber}");
        Console.WriteLine($"   Size   : {selected.FormattedSize}");
        Console.ResetColor();
        Console.WriteLine();
        Console.Write("Type  YES  (all caps) to confirm: ");

        string confirmation = Console.ReadLine() ?? string.Empty;
        if (confirmation != "YES")
        {
            Console.WriteLine("\nOperation cancelled.");
            return ExitCode.Cancelled;
        }

        // ── Optional volume label ────────────────────────────────────────
        Console.Write("\nVolume label (max 16 chars, press Enter to skip): ");
        string label = (Console.ReadLine() ?? string.Empty).Trim();
        if (label.Length > 16)
        {
            label = label[..16];
            Console.WriteLine($"Label truncated to: {label}");
        }

        // ── Optional dummy text file ─────────────────────────────────────
        Console.Write("Create a dummy text file after formatting? (y/N): ");
        bool createDummyFile = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

        string? dummyFileName = null;
        if (createDummyFile)
        {
            Console.Write("Dummy file name (default: dummy.txt): ");
            if (!TryNormalizeDummyFileName(Console.ReadLine(), out dummyFileName, out string? dummyFileError))
            {
                WriteError(dummyFileError ?? "Invalid dummy file name.");
                return ExitCode.InvalidSelection;
            }

            Console.WriteLine($"Dummy file will be created as: /{dummyFileName}");
        }

        // ── Format ───────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"Formatting PhysicalDrive{selected.DiskNumber} as ext4...");
        Console.WriteLine(new string('─', 50));

        var formatter = new Ext4Formatter(msg =>
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("  >> ");
            Console.ResetColor();
            Console.WriteLine(msg);
        });

        try
        {
            formatter.Format(selected.DiskNumber, label, dummyFileName);
        }
        catch (InvalidOperationException ex)
        {
            // Pre-flight failures (WSL missing, etc.)
            Console.WriteLine();
            WriteError(ex.Message);
            return ExitCode.FormatFailed;
        }
        catch (IOException ex)
        {
            Console.WriteLine();
            WriteError($"Format failed: {ex.Message}");
            return ExitCode.FormatFailed;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            WriteError($"Unexpected error: {ex}");
            return ExitCode.FormatFailed;
        }

        Console.WriteLine(new string('─', 50));
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✔  Formatting completed successfully!");
        Console.ResetColor();
        Console.WriteLine($"   PhysicalDrive{selected.DiskNumber} ({selected.Model}) is now formatted as ext4.");
        if (createDummyFile)
        {
            Console.WriteLine($"   Dummy file created at /{dummyFileName}.");
        }
        Console.WriteLine();

        return ExitCode.Success;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔════════════════════════════════════════════════════╗");
        Console.WriteLine("║          Ext4DiskFormatter  (USB → ext4)           ║");
        Console.WriteLine("║  Based on SharpExt4 by nickdu088                  ║");
        Console.WriteLine("║  https://github.com/nickdu088/SharpExt4            ║");
        Console.WriteLine("╚════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintDriveList(List<PhysicalDisk> disks)
    {
        Console.WriteLine($"Found {disks.Count} removable drive(s):\n");
        for (int i = 0; i < disks.Count; i++)
        {
            PhysicalDisk d = disks[i];
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  [{i + 1}] ");
            Console.ResetColor();
            Console.WriteLine(d.Model);
            Console.WriteLine($"       PhysicalDrive{d.DiskNumber} | {d.FormattedSize} | {d.Status}");
            Console.WriteLine();
        }
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {message}");
        Console.ResetColor();
    }

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static bool TryNormalizeDummyFileName(string? fileName, out string normalized, out string? error)
    {
        error = null;
        normalized = (fileName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "dummy.txt";
            return true;
        }

        if (normalized.Contains('/') || normalized.Contains('\\'))
        {
            error = "Dummy file name must not include folders or path separators.";
            return false;
        }

        if (normalized.Contains('\0'))
        {
            error = "Dummy file name contains an invalid null character.";
            return false;
        }

        if (!string.Equals(Path.GetExtension(normalized), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.HasExtension(normalized)
                ? Path.ChangeExtension(normalized, ".txt")
                : normalized + ".txt";
        }

        return true;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Exit codes
    // ──────────────────────────────────────────────────────────────────────
    private static class ExitCode
    {
        public const int Success          =  0;
        public const int NotAdmin         =  1;
        public const int EnumerationFailed =  2;
        public const int NoDrivesFound    =  3;
        public const int InvalidSelection =  4;
        public const int Cancelled        =  5;
        public const int FormatFailed     = 10;
    }
}
