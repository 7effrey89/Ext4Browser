using DiscUtils.Ext;

namespace Ext4DiskFormatter.Services;

/// <summary>
/// Browses an ext volume through a mounted raw Windows volume handle using DiscUtils.
/// This path is useful when the current session can open \\.\D: but cannot open \\.\PhysicalDriveN.
/// </summary>
public sealed class DiscUtilsVolumeBrowser
{
    private readonly Action<string> _log;

    public DiscUtilsVolumeBrowser(Action<string>? log = null)
    {
        _log = log ?? Console.WriteLine;
    }

    public void ListContents(string volumeRoot, string path = "/", bool recursive = true)
    {
        string normalizedPath = NormalizePath(path);
        AppLogger.Info($"DiscUtils list requested for mounted volume '{volumeRoot}', Path='{normalizedPath}', Recursive={recursive}.");

        using var mountedVolume = OpenReadOnlyFileSystem(volumeRoot);
        var fs = mountedVolume.FileSystem;

        if (fs.FileExists(normalizedPath))
        {
            PrintFile(fs, normalizedPath);
            return;
        }

        if (!fs.DirectoryExists(normalizedPath))
            throw new IOException($"Path '{path}' was not found on mounted volume {volumeRoot}.");

        _log($"Contents of {DisplayPath(normalizedPath)} on mounted volume {volumeRoot}:");
        _log(string.Empty);

        SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] directories = fs
            .GetDirectories(normalizedPath, "*", searchOption)
            .Select(DisplayPath)
            .OrderBy(pathEntry => pathEntry, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] files = fs
            .GetFiles(normalizedPath, "*", searchOption)
            .Select(DisplayPath)
            .OrderBy(pathEntry => pathEntry, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _log("[Directories]");
        _log($"  {DisplayPath(normalizedPath)}");
        if (directories.Length == 0)
        {
            _log("  (none)");
        }
        else
        {
            foreach (string directory in directories)
            {
                _log($"  {directory} | modified {FormatTimestamp(fs.GetLastWriteTime(ToDiscUtilsPath(directory)))}");
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
                string discPath = ToDiscUtilsPath(file);
                _log($"  {file} | {FormatBytes((ulong)fs.GetFileLength(discPath))} | modified {FormatTimestamp(fs.GetLastWriteTime(discPath))}");
            }
        }

        _log(string.Empty);
        _log($"Summary: {directories.Length + 1} director{(directories.Length == 0 ? "y" : "ies")}, {files.Length} file{(files.Length == 1 ? string.Empty : "s")}.");
    }

    public void ExportFileToWindowsPath(string volumeRoot, string sourcePath, string destinationPath)
    {
        string normalizedSourcePath = NormalizePath(sourcePath);
        if (normalizedSourcePath == "\\")
            throw new IOException("Please provide a file path to export, for example '/copilot.txt'.");

        AppLogger.Info(
            $"DiscUtils export requested for mounted volume '{volumeRoot}', Source='{normalizedSourcePath}', Destination='{destinationPath}'.");

        using var mountedVolume = OpenReadOnlyFileSystem(volumeRoot);
        var fs = mountedVolume.FileSystem;

        if (!fs.FileExists(normalizedSourcePath))
            throw new IOException($"File '{sourcePath}' was not found on mounted volume {volumeRoot}.");

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        using var input = fs.OpenFile(normalizedSourcePath, FileMode.Open, FileAccess.Read);
        using var output = File.Create(destinationPath);
        input.CopyTo(output);

        _log($"Copied {DisplayPath(normalizedSourcePath)} to {destinationPath}.");
    }

    private void PrintFile(ExtFileSystem fs, string path)
    {
        _log($"File details for {DisplayPath(path)}:");
        _log($"  Size     : {FormatBytes((ulong)fs.GetFileLength(path))}");
        _log($"  Modified : {FormatTimestamp(fs.GetLastWriteTime(path))}");
        _log($"  Accessed : {FormatTimestamp(fs.GetLastAccessTime(path))}");
    }

    private static MountedDiscUtilsVolume OpenReadOnlyFileSystem(string volumeRoot)
    {
        string devicePath = NonAdminRawVolumeProbe.ToRawVolumeDevicePath(volumeRoot);
        var baseStream = File.Open(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var alignedStream = new SectorAlignedReadStream(baseStream, 512);

        try
        {
            var fileSystem = new ExtFileSystem(alignedStream);
            return new MountedDiscUtilsVolume(baseStream, alignedStream, fileSystem);
        }
        catch
        {
            alignedStream.Dispose();
            baseStream.Dispose();
            throw;
        }
    }

    private static string NormalizePath(string? path)
    {
        string normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
            return "\\";

        normalized = normalized.Replace('/', '\\');
        if (!normalized.StartsWith('\\'))
            normalized = "\\" + normalized;

        return normalized.Length > 1
            ? normalized.TrimEnd('\\')
            : normalized;
    }

    private static string DisplayPath(string path) =>
        path == "\\"
            ? "/"
            : path.Replace('\\', '/');

    private static string ToDiscUtilsPath(string displayPath) => NormalizePath(displayPath);

    private static string FormatTimestamp(DateTime value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

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

    private sealed class MountedDiscUtilsVolume : IDisposable
    {
        private readonly Stream _baseStream;
        private readonly SectorAlignedReadStream _alignedStream;

        public MountedDiscUtilsVolume(Stream baseStream, SectorAlignedReadStream alignedStream, ExtFileSystem fileSystem)
        {
            _baseStream = baseStream;
            _alignedStream = alignedStream;
            FileSystem = fileSystem;
        }

        public ExtFileSystem FileSystem { get; }

        public void Dispose()
        {
            FileSystem.Dispose();
            _alignedStream.Dispose();
            _baseStream.Dispose();
        }
    }

    private sealed class SectorAlignedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _sectorSize;

        public SectorAlignedReadStream(Stream inner, int sectorSize)
        {
            _inner = inner;
            _sectorSize = sectorSize;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            long start = _inner.Position;
            long alignedStart = start - (start % _sectorSize);
            int prefix = (int)(start - alignedStart);
            int alignedCount = AlignUp(prefix + count, _sectorSize);

            byte[] temp = new byte[alignedCount];
            _inner.Position = alignedStart;
            int read = _inner.Read(temp, 0, alignedCount);
            int available = Math.Max(0, Math.Min(count, read - prefix));

            Array.Copy(temp, prefix, buffer, offset, available);
            _inner.Position = start + available;
            return available;
        }

        public override int Read(Span<byte> buffer)
        {
            byte[] temp = new byte[buffer.Length];
            int read = Read(temp, 0, temp.Length);
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static int AlignUp(int value, int alignment) =>
            ((value + alignment - 1) / alignment) * alignment;
    }
}