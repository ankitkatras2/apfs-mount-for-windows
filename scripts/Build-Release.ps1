param([string]$OutputDirectory = '', [string]$PrerequisiteDirectory = '', [switch]$RebuildNative)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist\APFS-Mount-for-Windows-1.0.2-win-x64' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; release builds never delete or overwrite an existing folder.' }
if ($RebuildNative) { & (Join-Path $PSScriptRoot 'Build-Native.ps1') }
$app = Join-Path $OutputDirectory 'app'
New-Item -ItemType Directory -Force $app | Out-Null
& dotnet publish (Join-Path $root 'src\APFSMount.App\APFSMount.App.csproj') -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -Path (Join-Path $root 'installer\*') -Destination $OutputDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $OutputDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALLATION.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
$prerequisites = Join-Path $OutputDirectory 'prerequisites'
New-Item -ItemType Directory $prerequisites | Out-Null
foreach ($asset in (Get-Content (Join-Path $root 'package-assets.json') -Raw | ConvertFrom-Json)) {
    $target = Join-Path $prerequisites $asset.File
    if ($PrerequisiteDirectory -and (Test-Path -LiteralPath (Join-Path $PrerequisiteDirectory $asset.File))) {
        Copy-Item -LiteralPath (Join-Path $PrerequisiteDirectory $asset.File) -Destination $target
    } else { Invoke-WebRequest -Uri $asset.Url -OutFile $target -TimeoutSec 300 }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $asset.SHA256) { throw "Prerequisite checksum mismatch: $($asset.File). If its upstream download changed, review and update package-assets.json." }
}
$prefix = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
$manifest = @(Get-ChildItem $OutputDirectory -Recurse -File | ForEach-Object {
    [ordered]@{Path=$_.FullName.Substring($prefix.Length);SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$manifest | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.json')
& (Join-Path $OutputDirectory 'Install.ps1') -CheckOnly
Write-Host "Release ready: $OutputDirectory"
