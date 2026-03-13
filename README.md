# Ext4DiskFormatter

A Windows C# console application that inspects **Linux ext2/ext3/ext4 volumes** from Windows.

It is built on top of the [SharpExt4](https://github.com/nickdu088/SharpExt4) library by nickdu088, which provides read/write access to Linux ext filesystems from Windows .NET applications.

---

## Features

- 🔍 **Automatically detects** all removable/USB drives via WMI
- 📂 **Lists folders and files** on an ext2/ext3/ext4 drive
- ✍️ **Creates text files** on an ext2/ext3/ext4 drive
- 📄 **Shows file details** including size, timestamps, and permissions
- ✅ **Persistent diagnostic logging** for browse failures and SharpExt4 reopen problems
- 🖥️ Clean, colour-coded console UI

---

## Requirements

| Requirement | Details |
|---|---|
| **OS** | Windows 10 / Windows 11 |
| **Drive type** | A removable or external physical disk that SharpExt4 can open directly from Windows. |
| **Privileges** | Must be run as **Administrator** |
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
