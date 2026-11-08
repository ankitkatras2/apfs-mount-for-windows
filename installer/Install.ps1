[CmdletBinding()]
param([switch]$CheckOnly, [switch]$NoStartup, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
$product = 'APFS Mount for Windows'

function Test-Package {
    $manifestPath = Join-Path $package 'SHA256SUMS.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Extract the complete installation ZIP before running Setup.cmd.' }
    foreach ($item in (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)) {
        $file = [IO.Path]::GetFullPath((Join-Path $package $item.Path))
        $prefix = [IO.Path]::GetFullPath($package).TrimEnd('\') + '\'
        if (-not $file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package manifest path.' }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package file: $($item.Path)" }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.SHA256) { throw "Package checksum failed: $($item.Path)" }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $package 'prerequisites') -File) {
        if ($file.Extension -in '.exe','.msi' -and (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'Valid') {
            throw "Prerequisite signature validation failed: $($file.Name)"
        }
    }
}

Test-Package
if ($CheckOnly) { Write-Host 'PASS: all package hashes and prerequisite signatures verified. No changes made.'; exit 0 }
if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { throw 'This package requires Windows x64 (Intel/AMD).' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    if ($NoStartup) { $arguments += ' -NoStartup' }; if ($NoLaunch) { $arguments += ' -NoLaunch' }
    $process = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -PassThru -Wait
    exit $process.ExitCode
}
$logFolder = Join-Path $env:LOCALAPPDATA 'APFSMountWindows\Setup'
New-Item -ItemType Directory -Force $logFolder | Out-Null
Start-Transcript -Path (Join-Path $logFolder ('install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')) | Out-Null
try {
    $restartRequired = $false
    $requirements = Get-Content (Join-Path $package 'prerequisites.json') -Raw | ConvertFrom-Json
    $vc = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue
    if (-not $vc -or $vc.Installed -ne 1 -or [version]($vc.Version.TrimStart('v')) -lt [version]'14.29.30133.0') {
        $setup = Join-Path $package ('prerequisites\' + $requirements.VisualCppFile)
        $process = Start-Process $setup -ArgumentList '/install /quiet /norestart' -PassThru -Wait -WindowStyle Hidden
        if ($process.ExitCode -notin 0,3010,1638) { throw "Visual C++ runtime installation failed: $($process.ExitCode)" }
        $restartRequired = $restartRequired -or $process.ExitCode -eq 3010
    }
    $runtimeFolder = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
    $runtime = @(Get-ChildItem $runtimeFolder -Directory -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '^8\.0\.\d+$' -and [version]$_.Name -ge [version]$requirements.DesktopRuntimeVersion
    })
    if ($runtime.Count -eq 0) {
        $setup = Join-Path $package ('prerequisites\' + $requirements.DesktopRuntimeFile)
        $process = Start-Process $setup -ArgumentList '/install /quiet /norestart' -PassThru -Wait -WindowStyle Hidden
        if ($process.ExitCode -notin 0,3010) { throw "Desktop runtime installation failed: $($process.ExitCode)" }
        $restartRequired = $restartRequired -or $process.ExitCode -eq 3010
    }
    $winfspDll = Join-Path ${env:ProgramFiles(x86)} 'WinFsp\bin\winfsp-x64.dll'
    $winfspVersion = [version]'0.0.0'
    if (Test-Path -LiteralPath $winfspDll) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($winfspDll)
        $winfspVersion = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart)
    }
    if ($winfspVersion -lt [version]'2.2.26286') {
        $msi = Join-Path $package ('prerequisites\' + $requirements.WinFspFile)
        $process = Start-Process msiexec.exe -ArgumentList ('/i "' + $msi + '" /qn /norestart ADDLOCAL=ALL') -PassThru -Wait -WindowStyle Hidden
        if ($process.ExitCode -notin 0,3010) { throw "WinFsp installation failed: $($process.ExitCode)" }
        $restartRequired = $restartRequired -or $process.ExitCode -eq 3010
    }
    $destination = Join-Path $env:ProgramFiles $product
    $marker = Join-Path $destination 'apfsmountwindows.install'
    if ((Test-Path $destination) -and -not (Test-Path $marker)) { throw 'The destination belongs to another installation; choose a different installation location manually.' }
    $taskName = 'APFSMountWindows-' + $identity.User.Value
    $description = 'APFS Mount for Windows read-only USB watcher'
    $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Description -ne $description) { throw 'An unrelated scheduled task uses this name.' }
    if ($existing) { Stop-ScheduledTask -TaskName $taskName }
    $executable = Join-Path $destination 'APFSMountWindows.exe'
    foreach ($process in Get-Process APFSMountWindows -ErrorAction SilentlyContinue) {
        if ($process.Path -eq $executable) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    }
    New-Item -ItemType Directory -Force $destination | Out-Null
    Copy-Item -Path (Join-Path $package 'app\*') -Destination $destination -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $package 'Uninstall.ps1') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $package 'INSTALLATION.md') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $package 'licenses') -Destination $destination -Recurse -Force
    @{ Product=$product; Version='1.0.1'; Task=$taskName; User=$identity.Name } | ConvertTo-Json | Set-Content -LiteralPath $marker
    if (-not $NoStartup) {
        $action = New-ScheduledTaskAction -Execute $executable -WorkingDirectory $destination
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
        $principal = New-ScheduledTaskPrincipal -UserId $identity.User.Value -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description $description -Force | Out-Null
    } elseif ($existing) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) "$product.lnk"))
    $shortcut.TargetPath = $executable; $shortcut.WorkingDirectory = $destination; $shortcut.Save()
    if (-not $NoLaunch -and -not $restartRequired) {
        if ($NoStartup) { Start-Process $executable -WindowStyle Hidden } else { Start-ScheduledTask -TaskName $taskName }
    }
    Write-Host "$product installed successfully. Open File Explorer after connecting your APFS USB drive."
    Add-Type -AssemblyName System.Windows.Forms
    $message = if ($restartRequired) { 'Installation complete. Restart Windows before using the app.' } else { 'Installation complete. The app runs in the notification area. Connect an APFS USB drive to browse it read-only.' }
    [System.Windows.Forms.MessageBox]::Show($message, $product) | Out-Null
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message + "`nSetup logs: $logFolder", "$product setup failed") | Out-Null
    throw
} finally { Stop-Transcript | Out-Null }
