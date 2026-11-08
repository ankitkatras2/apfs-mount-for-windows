param([string]$VisualStudioDirectory = '', [string]$WindowsSdkVersion = '10.0.19041.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $VisualStudioDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $VisualStudioDirectory = & $vswhere -latest -products '*' -version '[16.0,17.0)' -property installationPath
}
if (-not $VisualStudioDirectory) { throw 'Install Visual Studio 2019 Build Tools with C++ tools (v142).' }
$msbuild = Join-Path $VisualStudioDirectory 'MSBuild\Current\Bin\MSBuild.exe'
$vcvars = Join-Path $VisualStudioDirectory 'VC\Auxiliary\Build\vcvars64.bat'
$native = Join-Path $root 'third_party\libfsapfs'
$build = Join-Path $root 'work\native'
$out = Join-Path $build 'lib'
New-Item -ItemType Directory -Force $out | Out-Null
& $msbuild (Join-Path $native 'vs2019\libfsapfs\libfsapfs.vcxproj') /m /nologo /verbosity:minimal /p:Configuration=Release /p:Platform=x64 "/p:WindowsTargetPlatformVersion=$WindowsSdkVersion" "/p:OutDir=$out\" "/p:SolutionDir=$native\vs2019\"
if ($LASTEXITCODE -ne 0) { throw 'Native library build failed.' }
$includes = Get-ChildItem $native -Directory | Where-Object { $_.Name -match '^lib|^common$|^include$' } | ForEach-Object { '/I"' + $_.FullName + '"' }
$defines = Get-ChildItem $native -Directory | Where-Object Name -Match '^lib' | ForEach-Object { '/DHAVE_LOCAL_' + $_.Name.ToUpperInvariant() }
$libraries = Get-ChildItem $out -Filter *.lib | Where-Object Name -ne 'apfsmountbridge.lib' | ForEach-Object { '"' + $_.FullName + '"' }
$bridge = Join-Path $root 'src\APFSMount.Core\native\raw_apfs_bridge.c'
$rsp = Join-Path $build 'bridge.rsp'
@('/nologo','/LD','/MD','/O2','/D_CRT_SECURE_NO_WARNINGS') + $includes + $defines + @(('/Fo"' + $build + '\bridge.obj"'),('"' + $bridge + '"'),'/link',('/OUT:"' + $out + '\apfsmountbridge.dll"'),('/IMPLIB:"' + $out + '\apfsmountbridge.lib"')) + $libraries | Set-Content $rsp
$command = Join-Path $build 'build-bridge.cmd'
@('@echo off',('call "' + $vcvars + '" >nul'),('cl @"' + $rsp + '"')) | Set-Content $command
& $env:ComSpec /c $command
if ($LASTEXITCODE -ne 0) { throw 'Raw-disk bridge build failed.' }
foreach ($name in @('libfsapfs.dll','apfsmountbridge.dll','zlib.dll')) {
    Copy-Item -LiteralPath (Join-Path $out $name) -Destination (Join-Path $root 'src\APFSMount.Core\native') -Force
}
Write-Host 'Native components rebuilt successfully.'
