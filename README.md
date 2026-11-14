# APFS Mount for Windows

![APFS Mount for Windows icon](assets/logo-128.png)

A Windows notification-area app that automatically mounts supported APFS USB drives read-only. Browse and copy files using File Explorer. The application uses WinFsp and independent APFS readers; it does not require a commercial APFS application.

Free, MIT-licensed application code for read-only APFS USB access on Windows 10 and Windows 11 x64. Mount supported Apple File System drives automatically and copy files in Explorer. Independent project; not affiliated with Apple or Microsoft.

## Install

**[Download APFS Mount for Windows 1.0.2 — Windows x64](https://github.com/ankitkatras2/apfs-mount-for-windows/releases/download/v1.0.2/APFS-Mount-for-Windows-1.0.2-win-x64.zip)**

1. Download the installation ZIP above and extract the entire folder.
2. Run **Setup.cmd** and accept the Windows administrator prompt.
3. Connect a supported APFS USB drive. Browse and copy files in File Explorer; mounting is read-only.

The ZIP includes the app and prerequisite installers for offline setup on Windows 10/11 x64. No source build is needed. Keep the extracted files together: the application EXE alone is not a complete installation package.

See [fresh installation instructions](docs/INSTALLATION.md) or [release downloads and checksums](https://github.com/ankitkatras2/apfs-mount-for-windows/releases/latest). GitHub's **Code → Download ZIP** and release **Source code** archives contain source, not the ready-to-install app. A private repository requires sign-in and repository access to download releases.

## Build from this repository

Requirements: Windows x64, .NET 8 SDK, and internet access for the initial NuGet restore. Prebuilt native reader DLLs are checked in under `src/APFSMount.Core/native`; the corresponding sources are included.

```powershell
.\scripts\Test.ps1
.\scripts\Build-Release.ps1
```

The release appears in `dist/APFS-Mount-for-Windows-1.0.2-win-x64`. The build downloads the pinned prerequisites, checks their SHA-256 hashes, and validates vendor signatures. It refuses to overwrite an existing release folder. For a new build, supply a new `-OutputDirectory`.

For an offline build with previously downloaded installers:

```powershell
.\scripts\Build-Release.ps1 -PrerequisiteDirectory C:\path\to\prerequisites -OutputDirectory C:\path\to\new-release
```

To rebuild all native libraries, install Visual Studio 2019 Build Tools with the v142 C++ tools and Windows SDK 10.0.19041.0, then run:

```powershell
.\scripts\Build-Native.ps1
.\scripts\Test-Native.ps1
.\scripts\Build-Release.ps1 -OutputDirectory .\dist\new-native-build
```

No machine-specific source paths are required. The native build uses the vendored sources and writes intermediate files to ignored build folders.

## Tests and behavior

- 23 managed tests cover core parsing, safe copy paths, sector-aligned reads, and related safeguards.
- Native sparse-volume regression covers holes in the APFS volume table and empty-table reuse.
- The WinFsp smoke test is optional and requires WinFsp: `dotnet run --project tests/APFSMount.WinFsp.Smoke -c Release`.
- The working implementation was validated on a real USB disk, including real file reads, write rejection, removal, and automatic remounting. See [verification](docs/VERIFICATION.md) for the packaging checks and their limits.

Windows disk notifications complement periodic detection. Removal causes active USB mounts to be revalidated, so other APFS USB mounts can briefly remount too. Encrypted volumes and all APFS variants are not supported or validated comprehensively.

## Bitbucket

See [Bitbucket setup](docs/BITBUCKET.md). Build outputs, download caches, and installers are excluded from Git. Required native DLLs and source are included. No runtime logs, personal USB contents, credentials, or machine-specific test results are part of the intended Git payload. The sparse-volume fixture is synthetic.

## License

Application-specific code: [MIT](LICENSE). Third-party sources and binaries retain their own licenses; see [notices](THIRD-PARTY-NOTICES.md).

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos.
https://github.com/winfsp/winfsp
