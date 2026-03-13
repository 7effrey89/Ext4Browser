using Ext4DiskFormatter.Models;
using Ext4DiskFormatter.Services;
using System.Security.Principal;

namespace Ext4DiskFormatter;

/// <summary>
/// Entry point for Ext4DiskFormatter – a Windows console application that
/// inspects ext2/ext3/ext4 filesystems on removable or external disks.
/// </summary>
internal class Program
{
    private const string AppTitle = "Ext4DiskFormatter";

    static int Main(string[] args)
    {
        Console.Title = AppTitle;
        string logFilePath = AppLogger.Initialize();
        RegisterGlobalExceptionLogging();
        PrintBanner();
        Console.WriteLine($"Log file: {logFilePath}");
        Console.WriteLine();

        bool isAdministrator = IsAdministrator();
        if (!isAdministrator)
        {
            WriteWarning(
                "Running without administrator privileges. The app will use DiscUtils on mounted raw volumes for read-only browsing where possible, " +
                "but file creation still requires elevation because the DiscUtils ext backend is read-only.");
            Console.WriteLine();
        }

        while (true)
        {
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
                Console.WriteLine("No removable drives found. Connect a drive and try again.");
                return ExitCode.NoDrivesFound;
            }

            PrintDriveList(disks);

            Console.Write("Enter the number of the drive to inspect (0 to cancel): ");
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

            while (true)
            {
                switch (PromptForDriveAction())
                {
                    case DriveAction.ListContents:
                        TryListDriveContents(selected, isAdministrator);
                        break;

                    case DriveAction.CreateTextFile:
                        TryCreateTextFile(selected, isAdministrator);
                        break;

                    case DriveAction.ExportFile:
                        TryExportFileToTemp(selected, isAdministrator);
                        break;

                    case DriveAction.Cancel:
                        if (!PromptToReturnToDriveList())
                            return ExitCode.Success;

                        goto ContinueMainMenu;

                    default:
                        Console.WriteLine("\nInvalid selection. Exiting.");
                        return ExitCode.InvalidSelection;
                }
            }

        ContinueMainMenu:
            Console.WriteLine();
        }
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔════════════════════════════════════════════════════╗");
        Console.WriteLine("║      Ext4DiskFormatter  (ext volume browser)      ║");
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
            PhysicalDisk disk = disks[i];
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  [{i + 1}] ");
            Console.ResetColor();
            Console.WriteLine(disk.Model);
            Console.WriteLine($"       PhysicalDrive{disk.DiskNumber} | {disk.FormattedSize} | {disk.Status} | {disk.MediaType}");
            Console.WriteLine($"       Mounted volumes: {disk.MountedVolumeSummary}");
            Console.WriteLine();
        }
    }

    private static void TryListDriveContents(PhysicalDisk disk, bool isAdministrator)
    {
        Console.Write("\nPath to inspect (default: /): ");
        string path = Console.ReadLine() ?? string.Empty;

        Console.Write("List recursively? (Y/n): ");
        string recursiveChoice = (Console.ReadLine() ?? string.Empty).Trim();
        bool recursive = !string.Equals(recursiveChoice, "n", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine();

        if (!isAdministrator)
        {
            if (TryListMountedVolumeContents(disk, path, recursive))
                return;

            string message = NonAdminRawVolumeProbe.ExplainBrowseLimitation(disk);
            WriteError($"Unable to list contents without administrator privileges: {message}");
            return;
        }

        var browser = new Ext4VolumeBrowser();
        try
        {
            browser.ListContents(disk.DiskNumber, path, recursive);
            return;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Listing failed for PhysicalDrive{disk.DiskNumber}.", ex);

            if (TryListMountedVolumeContents(disk, path, recursive))
            {
                WriteWarning("SharpExt4 listing failed; showing mounted-volume contents via DiscUtils instead.");
                return;
            }

            WriteError($"Unable to list contents: {ex.Message}");
            PrintLogFileHint();
        }
    }

    private static void TryCreateTextFile(PhysicalDisk disk, bool isAdministrator)
    {
        Console.Write("\nFile path to create (default: /copilot.txt): ");
        string path = Console.ReadLine() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            path = "/copilot.txt";

        Console.Write("File contents (press Enter for default): ");
        string contents = Console.ReadLine() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contents))
        {
            contents =
                "Created by Ext4DiskFormatter." + Environment.NewLine +
                $"Disk: PhysicalDrive{disk.DiskNumber}" + Environment.NewLine +
                $"Created: {DateTimeOffset.UtcNow:O}" + Environment.NewLine;
        }

        Console.WriteLine();

        if (!isAdministrator)
        {
            WriteError(
                "Unable to create files without administrator privileges. " +
                "Browsing can use DiscUtils on the mounted raw volume, but DiscUtils.Ext is read-only and file creation still requires SharpExt4 raw disk access.");
            return;
        }

        var browser = new Ext4VolumeBrowser();
        try
        {
            browser.CreateTextFile(disk.DiskNumber, path, contents);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Create text file failed for PhysicalDrive{disk.DiskNumber}.", ex);
            WriteError($"Unable to create file: {ex.Message}");
            PrintLogFileHint();
        }
    }

    private static void TryExportFileToTemp(PhysicalDisk disk, bool isAdministrator)
    {
        Console.Write("\nFile path to copy from ext volume (default: /copilot.txt): ");
        string sourcePath = Console.ReadLine() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePath))
            sourcePath = "/copilot.txt";

        string defaultDestination = BuildDefaultTempDestinationPath(sourcePath);
        Console.Write($"Destination on Windows (default: {defaultDestination}): ");
        string destinationPath = Console.ReadLine() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(destinationPath))
            destinationPath = defaultDestination;

        Console.WriteLine();

        try
        {
            if (!isAdministrator && TryExportMountedVolumeFile(disk, sourcePath, destinationPath))
                return;

            var browser = new Ext4VolumeBrowser();
            browser.ExportFileToWindowsPath(disk.DiskNumber, sourcePath, destinationPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Export file failed for PhysicalDrive{disk.DiskNumber}.", ex);
            WriteError($"Unable to export file: {ex.Message}");
            PrintLogFileHint();
        }
    }

    private static bool TryListMountedVolumeContents(PhysicalDisk disk, string path, bool recursive)
    {
        if (!NonAdminRawVolumeProbe.TryGetBrowsableMountedVolumeRoot(disk, out string? volumeRoot, out string? reason))
            return false;

        var browser = new DiscUtilsVolumeBrowser();
        try
        {
            browser.ListContents(volumeRoot!, path, recursive);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"DiscUtils listing failed for mounted volume {volumeRoot}.", ex);
            if (!string.IsNullOrWhiteSpace(reason))
                AppLogger.Warning($"Mounted volume probe context for PhysicalDrive{disk.DiskNumber}: {reason}");

            if (IsAdministrator())
                AppLogger.Warning($"Falling back to SharpExt4 raw physical drive access for PhysicalDrive{disk.DiskNumber} after DiscUtils failure.");
            else
            {
                WriteError($"Unable to list contents via mounted volume {volumeRoot}: {ex.Message}");
                PrintLogFileHint();
                return true;
            }
        }

        return false;
    }

    private static bool TryExportMountedVolumeFile(PhysicalDisk disk, string sourcePath, string destinationPath)
    {
        if (!NonAdminRawVolumeProbe.TryGetBrowsableMountedVolumeRoot(disk, out string? volumeRoot, out string? reason))
        {
            WriteError($"Unable to export file without administrator privileges: {reason}");
            return true;
        }

        var browser = new DiscUtilsVolumeBrowser();
        try
        {
            browser.ExportFileToWindowsPath(volumeRoot!, sourcePath, destinationPath);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"DiscUtils export failed for mounted volume {volumeRoot}.", ex);
            WriteError($"Unable to export file via mounted volume {volumeRoot}: {ex.Message}");
            PrintLogFileHint();
            return true;
        }
    }

    private static string BuildDefaultTempDestinationPath(string sourcePath)
    {
        string normalizedSourcePath = sourcePath.Replace('/', Path.DirectorySeparatorChar).Trim();
        string fileName = Path.GetFileName(normalizedSourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "copied-from-ext.bin";

        return Path.Combine(@"C:\temp", fileName);
    }

    private static DriveAction PromptForDriveAction()
    {
        Console.WriteLine();
        Console.WriteLine("Choose an action for the selected drive:");
        Console.WriteLine("  [L] List folders/files");
        Console.WriteLine("  [C] Create text file");
        Console.WriteLine("  [E] Export file to C:\\temp");
        Console.WriteLine("  [0] Back to drive list");
        Console.Write("Enter choice: ");

        string action = (Console.ReadLine() ?? string.Empty).Trim();
        return action.ToUpperInvariant() switch
        {
            "L" => DriveAction.ListContents,
            "C" => DriveAction.CreateTextFile,
            "E" => DriveAction.ExportFile,
            "0" => DriveAction.Cancel,
            _ => DriveAction.Invalid,
        };
    }

    private static bool PromptToReturnToDriveList()
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to return to the drive list, or type X to exit.");
        string response = (Console.ReadLine() ?? string.Empty).Trim();
        return !string.Equals(response, "x", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintLogFileHint()
    {
        Console.WriteLine($"See log for details: {AppLogger.GetLogFilePath()}");
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {message}");
        Console.ResetColor();
        AppLogger.Error(message);
    }

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
        AppLogger.Warning(message);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RegisterGlobalExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
            {
                AppLogger.Error("Unhandled application exception.", ex);
            }
            else
            {
                AppLogger.Error($"Unhandled non-exception object: {eventArgs.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLogger.Error("Unobserved task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }

    private static class ExitCode
    {
        public const int Success = 0;
        public const int NotAdmin = 1;
        public const int EnumerationFailed = 2;
        public const int NoDrivesFound = 3;
        public const int InvalidSelection = 4;
        public const int Cancelled = 5;
    }

    private enum DriveAction
    {
        Invalid,
        ListContents,
        CreateTextFile,
        ExportFile,
        Cancel,
    }
}
