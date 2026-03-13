# Ext4DiskFormatter

A Windows C# console application that inspects **Linux ext2/ext3/ext4 volumes** from Windows.

It uses [DiscUtils.Ext](https://www.nuget.org/packages/DiscUtils.Ext) for mounted-volume browsing and [SharpExt4](https://github.com/nickdu088/SharpExt4) as the raw physical-drive write path plus the preferred browse path when administrator raw-disk access is available.

---

## Features

- 🔍 **Automatically detects** all removable/USB drives via WMI
- 📂 **Lists folders and files** on an ext2/ext3/ext4 drive
- ✍️ **Creates text files** on an ext2/ext3/ext4 drive when raw disk access is available
- 📤 **Copies files back to `C:\temp`** from the ext volume
- 📄 **Shows file details** including size, timestamps, and permissions
- ✅ **Persistent diagnostic logging** for browse failures and SharpExt4 reopen problems
- 🔓 **Read-only browsing without admin** when Windows exposes the ext volume as a mounted raw device like `\\.\\D:`
- 🖥️ Clean, colour-coded console UI

---

## Requirements

| Requirement | Details |
|---|---|
| **OS** | Windows 10 / Windows 11 |
| **Drive type** | A removable or external physical disk that SharpExt4 can open directly from Windows. |
| **Privileges** | Read-only browsing can work without admin when Windows exposes a mounted raw volume handle. Administrator is still required for writes, because the DiscUtils ext backend is read-only and file creation uses raw `PhysicalDriveN` access through SharpExt4. |
| **.NET runtime** | .NET 8 (x64) |

---

## Getting Started

### 1. Clone the repository

```bash
git clone https://github.com/7effrey89/Ext4DiskFormatter.git
cd Ext4DiskFormatter
```

### 2. Restore dependencies

```bash
cd Ext4DiskFormatter
dotnet restore
```

### 3. Build

```bash
dotnet build -c Release
```

### 4. Run (as Administrator)

Right-click `Ext4DiskFormatter.exe` in the output folder and choose **Run as administrator**, or from an elevated terminal:

```powershell
.\bin\Release\net8.0-windows\Ext4DiskFormatter.exe
```

---

## Usage

```
╔════════════════════════════════════════════════════╗
║          Ext4DiskFormatter  (USB → ext4)           ║
║  Based on SharpExt4 by nickdu088                  ║
║  https://github.com/nickdu088/SharpExt4            ║
╚════════════════════════════════════════════════════╝

Scanning for removable drives...

Found 1 removable drive(s):

  [1] SanDisk Ultra USB 3.0
   PhysicalDrive1 | 14.44 GB | OK | Removable Media
   Format unavailable: WSL does not support wsl --mount for USB flash drives, removable media, or SD card readers.

Enter the number of the drive to inspect (0 to cancel): 1

Choose an action for the selected drive:
   [L] List folders/files
   [C] Create text file
   [E] Export file to C:\temp
   [0] Back to drive list
Enter choice: C

File path to create (default: /copilot.txt): /notes.txt
File contents (press Enter for default): hello from windows

Created text file at /notes.txt (18.00 B).

Choose an action for the selected drive:
   [L] List folders/files
   [C] Create text file
   [0] Back to drive list
Enter choice: L

Path to inspect (default: /): /
List recursively? (Y/n): Y

Contents of / on PhysicalDrive1:

[Directories]
   /
   /lost+found | modified 2026-03-13 17:35:12

[Files]
   /hello.txt | 118.00 B | modified 2026-03-13 17:35:42

Summary: 2 directories, 1 file.

Choose an action for the selected drive:
   [L] List folders/files
   [C] Create text file
   [E] Export file to C:\temp
   [0] Back to drive list
Enter choice: E

File path to copy from ext volume (default: /copilot.txt): /copilot.txt
Destination on Windows (default: C:\temp\copilot.txt):

Copied /copilot.txt to C:\temp\copilot.txt.
```

---

## How It Works

1. **Disk detection** – Queries `Win32_DiskDrive` via WMI to find all drives with `MediaType = 'Removable Media'` or `'External hard disk media'`.

2. **Volume open** – Uses [SharpExt4](https://github.com/nickdu088/SharpExt4) to probe every partition exposed on the selected disk until one mounts successfully.

3. **Directory and file listing** – Reads directories and files from the ext volume, including file length and timestamps.

4. **Diagnostics** – Writes browse and SharpExt4 errors to a log file under `%LocalAppData%\Ext4DiskFormatter\Logs`, including the partition offsets and sizes that were tried.

---

## Integration Testing

The solution includes an opt-in test project for exercising a real removable ext volume.

```powershell
$env:EXT4_TEST_DISK_NUMBER = 1
dotnet test Ext4DiskFormatter.sln
```

The physical-drive tests are skipped unless both conditions are true:

- the test host is running as Administrator
- `EXT4_TEST_DISK_NUMBER` points at a currently attached removable disk

If every partition probe still fails with `Could not mount partition`, the most likely causes are:

- the selected partition is not actually ext2/ext3/ext4
- the filesystem is damaged
- the filesystem uses ext4 features that the bundled SharpExt4 build does not support

## Notes On Console Messages

During file creation you may see messages like these more than once:

```text
Opening PhysicalDrive1 with SharpExt4 (attempt 1/5)...
SharpExt4 returned invalid partition metadata; using Windows partition layout as a fallback.
```

This is expected for some drives and does not mean the write failed.

- `Opening PhysicalDrive... (attempt 1/5)` means the app is using the retry-capable disk-open helper. It is just the first allowed attempt, not an error by itself.
- `using Windows partition layout as a fallback` means SharpExt4 opened the disk, but reported invalid partition metadata for that device. The app repairs that by reading the partition layout from Windows/WMI and continuing.
- These lines can appear twice during file creation because the app opens the disk once to write the file and a second time to reopen the filesystem and verify that the file contents were written correctly.

If the operation succeeds afterward with output like `Created text file at /copilot.txt (...)`, the repeated SharpExt4 messages were informational only.

---

## Project Structure

```
Ext4DiskFormatter/
├── Ext4DiskFormatter.csproj       # Project file (.NET 8, x64, Windows)
├── Program.cs                     # CLI entry point
├── Models/
│   └── PhysicalDisk.cs            # Drive information model
├── Services/
│   ├── DiskEnumerator.cs          # WMI-based drive detection
│   ├── Ext4VolumeBrowser.cs       # ext volume browsing logic
│   ├── SharpExt4DiskAccessor.cs   # Retried SharpExt4 disk opening
│   └── AppLogger.cs               # Persistent diagnostic logging
└── lib/                           # Pre-built SharpExt4 binaries (x64)
    ├── SharpExt4.dll              # SharpExt4 managed/native assembly
    ├── DiskPartitionInfo.dll      # Partition table reader
    └── Ijwhost.dll                # C++/CLI IJW hosting runtime
```

The binaries in `lib/` are taken from the [SharpExt4 v0.0.3](https://github.com/nickdu088/SharpExt4/releases/tag/v0.0.3) release (x64).

---

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Not running as Administrator |
| 2 | WMI disk enumeration failed |
| 3 | No removable drives found |
| 4 | Invalid drive selection |
| 5 | User cancelled |

---

## Acknowledgements

- [SharpExt4](https://github.com/nickdu088/SharpExt4) by **nickdu088** – .NET wrapper around [lwext4](https://github.com/gkostka/lwext4) providing full ext2/3/4 filesystem access from Windows.
