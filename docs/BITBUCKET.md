# Push to Bitbucket

This directory is the repository root. A local Git repository on branch `main` is included in the folder. Nothing has been uploaded and no remote credentials are stored.

1. In Bitbucket, create an empty repository named `apfs-mount-for-windows`. Do not initialize it with a README or .gitignore.
2. Open PowerShell in this repository folder.
3. Check the initial commit with `git log -1` and `git status`.
4. Add your own repository URL and push:

```powershell
git remote add origin https://bitbucket.org/YOUR_WORKSPACE/apfs-mount-for-windows.git
git push -u origin main
```

Use Bitbucket's supported authentication or your SSH remote. Never place passwords or tokens in scripts, remote URLs, or committed files.

If using the source ZIP, it does not contain the `.git` history. Initialize it first:

```powershell
git init -b main
git add .
git commit -m "Initial APFS Mount for Windows release"
```

The repository includes native dependency source and the small native runtime DLLs required by the application. Large prerequisite installers and release ZIPs stay outside Git. Upload installation ZIPs through your release/download distribution process, rather than committing them.

For a Windows Bitbucket runner, run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test.ps1`. No cloud pipeline is enabled by default: native mounting tests need a Windows machine with WinFsp and must not run on ordinary Linux containers.
