using Ext4DiskFormatter.Models;
using Ext4DiskFormatter.Services;
using System.Security.Principal;
using Xunit;
using Xunit.Abstractions;

namespace Ext4DiskFormatter.Tests;

public sealed class PhysicalDriveIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public PhysicalDriveIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RequiresRemovableDiskFact]
    public void RawPhysicalDriveReadRequiresElevationForCurrentHost()
    {
        PhysicalDisk disk = GetAnyRemovableDisk();
        string physicalDrivePath = $"\\\\.\\PhysicalDrive{disk.DiskNumber}";

        _output.WriteLine($"Testing raw read access against {physicalDrivePath} ({disk.Model}).");

        Exception? exception = Record.Exception(() =>
        {
            using var stream = new FileStream(
                physicalDrivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            Assert.True(stream.CanRead);
        });

        bool isAdministrator = PhysicalDriveTestConfiguration.IsAdministrator();
        _output.WriteLine($"Current host elevated: {isAdministrator}.");

        if (isAdministrator)
        {
            Assert.Null(exception);
            return;
        }

        Assert.NotNull(exception);
        Assert.True(
            exception is UnauthorizedAccessException || IsAccessDeniedIOException(exception),
            $"Expected an access denied failure for non-elevated raw drive reads, but got: {exception}");
    }

    [RequiresPhysicalDriveFact]
    public void CanListConfiguredDriveRoot()
    {
        int diskNumber = PhysicalDriveTestConfiguration.GetDiskNumber();
        List<string> logLines = [];
        var browser = new Ext4VolumeBrowser(message => Capture(logLines, message));

        browser.ListContents(diskNumber, "/", recursive: false);

        Assert.Contains(logLines, line => line.Contains("Contents of / on PhysicalDrive", StringComparison.Ordinal));
        Assert.Contains(logLines, line => line.Contains("[Directories]", StringComparison.Ordinal));
        Assert.Contains(logLines, line => line.Contains("Summary:", StringComparison.Ordinal));
    }

    [RequiresPhysicalDriveFact]
    public void CanCreateAndObserveTextFileOnConfiguredDrive()
    {
        int diskNumber = PhysicalDriveTestConfiguration.GetDiskNumber();
        string filePath = $"/copilot-test-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt";
        string fileContents =
            "Created by the Ext4DiskFormatter integration test." + Environment.NewLine +
            $"Created: {DateTimeOffset.UtcNow:O}" + Environment.NewLine;

        List<string> createLogLines = [];
        var browser = new Ext4VolumeBrowser(message => Capture(createLogLines, message));

        browser.CreateTextFile(diskNumber, filePath, fileContents);

        Assert.Contains(createLogLines, line => line.Contains(filePath, StringComparison.Ordinal));

        List<string> listLogLines = [];
        browser = new Ext4VolumeBrowser(message => Capture(listLogLines, message));

        browser.ListContents(diskNumber, "/", recursive: false);

        Assert.Contains(listLogLines, line => line.Contains(filePath, StringComparison.Ordinal));
    }

    private void Capture(List<string> lines, string message)
    {
        lines.Add(message);
        _output.WriteLine(message);
    }

    private static PhysicalDisk GetAnyRemovableDisk()
    {
        IReadOnlyList<PhysicalDisk> disks = DiskEnumerator.GetRemovableDisks();
        return disks[0];
    }

    private static bool IsAccessDeniedIOException(Exception exception) =>
        exception is IOException ioException && ioException.HResult == unchecked((int)0x80070005);
}

internal sealed class RequiresPhysicalDriveFactAttribute : FactAttribute
{
    public RequiresPhysicalDriveFactAttribute()
    {
        if (!PhysicalDriveTestConfiguration.CanRun(out string reason))
            Skip = reason ?? "Physical drive integration test skipped.";
    }
}

internal sealed class RequiresRemovableDiskFactAttribute : FactAttribute
{
    public RequiresRemovableDiskFactAttribute()
    {
        if (!PhysicalDriveTestConfiguration.HasAnyRemovableDisk(out string reason))
            Skip = reason ?? "No removable disk is available for raw-drive access testing.";
    }
}

internal static class PhysicalDriveTestConfiguration
{
    private const string DiskNumberEnvironmentVariable = "EXT4_TEST_DISK_NUMBER";

    public static bool CanRun(out string reason)
    {
        if (!IsAdministrator())
        {
            reason = "Physical drive integration tests require an elevated test host.";
            return false;
        }

        if (!TryGetDiskNumber(out int diskNumber, out string? diskReason))
        {
            reason = diskReason ?? "No physical drive test disk was configured.";
            return false;
        }

        IReadOnlyList<PhysicalDisk> disks;
        try
        {
            disks = DiskEnumerator.GetRemovableDisks();
        }
        catch (Exception ex)
        {
            reason = $"Failed to enumerate removable disks: {ex.Message}";
            return false;
        }

        if (!disks.Any(disk => disk.DiskNumber == diskNumber))
        {
            reason = $"PhysicalDrive{diskNumber} was not found among removable disks. Set {DiskNumberEnvironmentVariable} to a currently attached removable disk.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool HasAnyRemovableDisk(out string reason)
    {
        try
        {
            if (DiskEnumerator.GetRemovableDisks().Count > 0)
            {
                reason = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = $"Failed to enumerate removable disks: {ex.Message}";
            return false;
        }

        reason = "No removable disks are attached, so raw-drive access cannot be tested.";
        return false;
    }

    public static int GetDiskNumber()
    {
        if (!TryGetDiskNumber(out int diskNumber, out string? reason))
            throw new InvalidOperationException(reason ?? "No physical drive test disk was configured.");

        return diskNumber;
    }

    private static bool TryGetDiskNumber(out int diskNumber, out string? reason)
    {
        string? value = Environment.GetEnvironmentVariable(DiskNumberEnvironmentVariable);
        if (!int.TryParse(value, out diskNumber) || diskNumber < 0)
        {
            reason = $"Set {DiskNumberEnvironmentVariable} to the target PhysicalDrive number before running physical drive integration tests.";
            return false;
        }

        reason = null;
        return true;
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}