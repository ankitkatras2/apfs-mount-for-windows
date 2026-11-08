$ErrorActionPreference = 'Stop'
$product = 'APFS Mount for Windows'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"')
    exit
}
$destination = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles $product))
$marker = Join-Path $destination 'apfsmountwindows.install'
if (-not (Test-Path -LiteralPath $marker)) { throw 'No matching installation was found.' }
$installation = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
if ($installation.Product -ne $product) { throw 'Installation ownership check failed.' }
$task = Get-ScheduledTask -TaskName $installation.Task -ErrorAction SilentlyContinue
if ($task -and $task.Description -eq 'APFS Mount for Windows read-only USB watcher') {
    Stop-ScheduledTask -TaskName $task.TaskName
    Unregister-ScheduledTask -TaskName $task.TaskName -Confirm:$false
}
foreach ($process in Get-Process APFSMountWindows -ErrorAction SilentlyContinue) {
    if ($process.Path -eq (Join-Path $destination 'APFSMountWindows.exe')) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$product.lnk"
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
# Keep installed files, local caches, and shared prerequisites. This script only disables the app.
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.MessageBox]::Show('Startup and the Start menu shortcut have been removed. Program files, local caches, and shared prerequisites were preserved.', $product) | Out-Null
