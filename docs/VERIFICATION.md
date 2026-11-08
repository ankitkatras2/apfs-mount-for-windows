# Release verification

Version 1.0.1, Windows x64.

Version 1.0.1 adds the generated Apple-and-USB app icon. The Windows ICO has seven resolutions (16 through 256 pixels). The published executable's icon was extracted and visually checked; the tray and About window use that embedded icon. The package integrity and prerequisite-signature checks passed again. No filesystem behavior was changed by this update.

The underlying implementation previously passed a real USB test: directory enumeration, full photo/video reads, write rejection, and automatic remount after disconnection. The clean release rebuilds the same corrected parser and disk I/O adapters under this product name.

Packaging validation covers the renamed managed build, the native source rebuild, the 23 managed regression tests, the synthetic sparse-volume native regression, installer PowerShell parsing, and SHA-256/vendor-signature checks on the assembled offline package.

All of those checks passed for this release. The renamed WinFsp smoke test also mounted a temporary drive, enumerated 35,001 synthetic entries, verified capacity, rejected file creation, and unmounted. The renamed application's diagnostic mode successfully loaded the installed runtime. The previously installed app and its existing mount were left running.

The installer has not been run on a blank Windows virtual machine. Its machine-changing setup and disable paths are not executed during package validation. Existing installed software is preserved.

No personal drive contents, disk identifiers, log files, credentials, or previously captured physical-device header are included in the source ZIP. The native regression fixture is synthetic. Git ignores intermediate build files and local test output.
