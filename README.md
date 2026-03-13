# Ext4DiskFormatter

A Windows C# console application that formats a USB drive (or any removable disk) to the **Linux ext4 filesystem**.

Built on top of the [SharpExt4](https://github.com/nickdu088/SharpExt4) library by nickdu088, which provides full read/write access to Linux ext2/ext3/ext4 filesystems from Windows .NET applications.

---

## Features

- 🔍 **Automatically detects** all removable/USB drives via WMI
- ⚠️ **Safety confirmation** – requires you to type `YES` before erasing any data
- 🏷️ **Optional volume label** (up to 16 characters, per ext4 spec)
- 📄 **Optional dummy text file creation** in the root of the formatted ext4 volume
- ✅ **Post-format verification** using SharpExt4 to confirm the filesystem was created correctly
- 🖥️ Clean, colour-coded console UI

---

## Requirements

| Requirement | Details |
|---|---|
| **OS** | Windows 10 Build 21364 (21H2) or later / Windows 11 |
| **WSL 2** | Windows Subsystem for Linux version 2 must be enabled |
| **Linux distro** | At least one WSL 2 distribution installed (e.g., Ubuntu) |
| **e2fsprogs** | `mkfs.ext4` must be available in the WSL distribution |
| **Privileges** | Must be run as **Administrator** |
| **.NET runtime** | .NET 8 (x64) |

### Quick WSL setup (if not already done)

```powershell
# Install WSL 2 with Ubuntu (run in an elevated PowerShell)
wsl --install

# After restarting, install e2fsprogs inside Ubuntu:
wsl -- sudo apt-get update && sudo apt-get install -y e2fsprogs
```

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
       PhysicalDrive1 | 14.44 GB | OK

Enter the number of the drive to format (0 to cancel): 1

⚠  WARNING  ⚠
All data on the selected drive will be PERMANENTLY ERASED.

   Drive  : SanDisk Ultra USB 3.0
   Disk # : PhysicalDrive1
   Size   : 14.44 GB

Type  YES  (all caps) to confirm: YES

Volume label (max 16 chars, press Enter to skip): myusb
Create a dummy text file after formatting? (y/N): y
Dummy file name (default: dummy.txt): hello.txt
Dummy file will be created as: /hello.txt

Formatting PhysicalDrive1 as ext4...
──────────────────────────────────────────────────
  >> Checking WSL availability...
  >> Initializing disk with DiskPart (clean + MBR + primary partition)...
  >> Attaching disk 1 to WSL...
  >> Disk is visible in WSL as /dev/sdb.
  >> Refreshing partition table in WSL...
  >> Running mkfs.ext4 on /dev/sdb1...
  >> ext4 filesystem created successfully.
  >> Detaching disk from WSL...
  >> Verifying filesystem with SharpExt4...
  >> Created dummy text file at /hello.txt.
  >> Verification OK – ext4 volume mounted. Label: "myusb"
──────────────────────────────────────────────────

✔  Formatting completed successfully!
   PhysicalDrive1 (SanDisk Ultra USB 3.0) is now formatted as ext4.
   Dummy file created at /hello.txt.
```

---

## How It Works

1. **Disk detection** – Queries `Win32_DiskDrive` via WMI to find all drives with `MediaType = 'Removable Media'` or `'External hard disk media'`.

2. **DiskPart** – Runs a DiskPart script to:
   - `clean` – removes all existing partitions and data
   - `convert mbr` – creates an MBR partition table
   - `create partition primary` + `set id=83` – creates a single Linux (0x83) partition

3. **WSL mount** – Attaches the raw physical disk to WSL 2 using
   `wsl --mount \\.\PhysicalDriveN --bare`

4. **mkfs.ext4** – Runs `mkfs.ext4` inside WSL on the first partition
   (`/dev/sdXN`) to create the ext4 filesystem.

5. **WSL unmount** – Detaches the disk from WSL with
   `wsl --unmount \\.\PhysicalDriveN`

6. **Verification + dummy file creation** – Uses the [SharpExt4](https://github.com/nickdu088/SharpExt4) library to open and mount the newly created partition, confirm that the ext4 filesystem is readable, and optionally create a text file in the root of the formatted drive.

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
│   └── Ext4Formatter.cs           # Core formatting logic
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
| 10 | Formatting failed |

---

## Acknowledgements

- [SharpExt4](https://github.com/nickdu088/SharpExt4) by **nickdu088** – .NET wrapper around [lwext4](https://github.com/gkostka/lwext4) providing full ext2/3/4 filesystem access from Windows.
