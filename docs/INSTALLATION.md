# APFS Mount for Windows — fresh installation

## Install

1. Extract the entire installation ZIP to a local folder. Do not run it inside the ZIP viewer.
2. Double-click **Setup.cmd**.
3. Accept the Windows administrator prompt. The application and setup script are not code-signed; the bundled prerequisite installers carry their vendors' signatures.
4. Setup installs missing prerequisites, copies the app to `C:\Program Files\APFS Mount for Windows`, adds a Start menu shortcut, and enables automatic startup when the installing user signs in.
5. Connect an APFS USB drive. Open File Explorer and look for **APFS Mount for Windows**. The drive letter is chosen automatically from the available letters.

The application runs in the notification area. Right-click its icon to open mounted drives, pause automatic mounting, retry detection, view the activity log, or unmount everything and exit. Close files before unplugging the drive.

This package installs fully offline: it includes .NET 8 Desktop Runtime x64, the Visual C++ x64 runtime, and the tested WinFsp runtime. Administrator access is needed for raw USB reads and mounting. Use an administrator account; the sign-in task belongs to the account that accepts elevation.

**Only run one APFS auto-mount app at a time.** If you already use a different build or filesystem tool, close it and disable its automatic startup before enabling this package. Setup does not delete or disable other products.

## Requirements and scope

- Windows 10/11 x64 on an Intel/AMD processor. ARM64 is not supported by this package.
- Supported, readable APFS USB disks. The app ignores Windows system/boot disks.
- Read-only access: browse files and copy them to a Windows disk. Writing back to APFS is intentionally rejected.
- Encrypted volumes, Fusion Drive, damaged filesystems, and every APFS variant have not been qualified.
- Local cache space is needed when reading files. A file can be cached in full. New mounts use separate caches to prevent stale reads.

## Options

Run these commands from the extracted installation folder in PowerShell:

```powershell
# Verify package hashes and vendor signatures without installing anything:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -CheckOnly

# Install without automatic sign-in startup:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NoStartup

# Install but do not start the app immediately:
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -NoLaunch
```

## Troubleshooting

- No drive appears: open the tray menu's activity log. Use **Retry detected drives** after checking the connection.
- Administrator prompt cancelled: run Setup.cmd again when ready.
- Windows requests a restart after a prerequisite installation: restart and sign in again.
- No tray icon: launch **APFS Mount for Windows** from the Start menu.
- Installation logs: `%LOCALAPPDATA%\APFSMountWindows\Setup`.
- App log: `%LOCALAPPDATA%\APFSMountWindows\activity.log`.
- The sparse-volume table and sector-alignment fixes are included in this build.

## Disable or remove startup

Close files on mounted drives first. Run `Uninstall.ps1` from the installation folder or `C:\Program Files\APFS Mount for Windows`. It stops this app, removes its sign-in task and shortcut, and **preserves all program files, caches, and shared prerequisites**. Nothing is deleted from USB drives. To re-enable it, run Setup.cmd again.

## Licenses

Application source is MIT-licensed; bundled dependencies retain their own terms. See THIRD-PARTY-NOTICES.md and licenses/.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos.
Source and license: https://github.com/winfsp/winfsp

Microsoft runtime information: https://dotnet.microsoft.com/download/dotnet/8.0

Visual C++ runtime information: https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist
