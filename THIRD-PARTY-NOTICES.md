# Third-party components

The MIT license at the repository root applies only to application-specific code. Retain this file and `licenses/` when redistributing a release. The included prerequisite installers are vendor binaries, not application source.

| Component | License / source | Included material |
|---|---|---|
| WinFsp | GPLv3 with its FLOSS exception; https://github.com/winfsp/winfsp | Managed API, signed unmodified 2.2.26286 installer; license in licenses/WinFsp.txt |
| libfsapfs | LGPL-3.0-or-later; https://github.com/libyal/libfsapfs | Full vendored source, patched parser, rebuilt DLL |
| libyal support libraries | Their included libyal notices and LGPL terms | Vendored beneath third_party/libfsapfs |
| zlib | zlib license; https://zlib.net | Source, rebuilt DLL, license |
| SevenZipExtractor | MIT; https://github.com/adoconnection/SevenZipExtractor | Source with APFS format registration |
| 7-Zip | LGPL plus its bundled BSD/unRAR notices; https://www.7-zip.org | Native reader DLLs; full upstream notice in licenses/7-Zip.txt |
| System.Management | MIT; https://github.com/dotnet/runtime | NuGet runtime dependency |
| .NET Desktop Runtime | Microsoft distribution terms and upstream notices; https://dotnet.microsoft.com/download/dotnet/8.0 | Signed unmodified runtime installer in installation package |
| Visual C++ runtime | Microsoft redistribution terms; https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist | Signed unmodified prerequisite installer |

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos.
https://github.com/winfsp/winfsp

Local changes to libfsapfs: the parsed container volume list now compacts nonzero identifiers, avoiding invalid object-zero lookups for sparse volume tables. The application-specific raw bridge performs sector-aligned Windows reads and preserves native error traces. The corresponding changed sources and tests are included.

The release has been assembled for personal use and source delivery. Upstream license texts remain authoritative for further distribution. No commercial filesystem implementation, assets, or binaries are included.
