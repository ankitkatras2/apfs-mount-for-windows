$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$native = Join-Path $root 'third_party\libfsapfs'
$build = Join-Path $root 'work\native'
$lib = Join-Path $build 'lib'
if (-not (Test-Path (Join-Path $lib 'libfsapfs.dll'))) { & (Join-Path $PSScriptRoot 'Build-Native.ps1') }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -version '[16.0,17.0)' -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$includes = Get-ChildItem $native -Directory | Where-Object { $_.Name -match '^lib|^common$|^include$' } | ForEach-Object { '/I"' + $_.FullName + '"' }
$defines = Get-ChildItem $native -Directory | Where-Object Name -Match '^lib' | ForEach-Object { '/DHAVE_LOCAL_' + $_.Name.ToUpperInvariant() }
$object = Join-Path $build 'sparse_volumes_test.obj'
$compile = Join-Path $build 'test-compile.rsp'
@('/nologo','/c','/MD','/O2','/D_CRT_SECURE_NO_WARNINGS') + $includes + $defines + @(('/Fo"' + $object + '"'),('"' + (Join-Path $root 'tests\native\sparse_volumes_test.c') + '"')) | Set-Content $compile
$objects = Get-ChildItem (Join-Path $native 'vs2019\libfsapfs\Release\x64') -Filter *.obj | ForEach-Object { '"' + $_.FullName + '"' }
$libraries = Get-ChildItem $lib -Filter *.lib | Where-Object { $_.Name -notin 'libfsapfs.lib','apfsmountbridge.lib' } | ForEach-Object { '"' + $_.FullName + '"' }
$exe = Join-Path $build 'sparse_volumes_test.exe'
$link = Join-Path $build 'test-link.rsp'
@('/NOLOGO',('/OUT:"' + $exe + '"'),('"' + $object + '"'),'advapi32.lib','user32.lib') + $objects + $libraries | Set-Content $link
$command = Join-Path $build 'test-native.cmd'
@('@echo off',('call "' + $vcvars + '" >nul'),('cl @"' + $compile + '"'),'if errorlevel 1 exit /b 1',('link @"' + $link + '"')) | Set-Content $command
& $env:ComSpec /c $command
if ($LASTEXITCODE -ne 0) { throw 'Native test build failed.' }
Copy-Item -LiteralPath (Join-Path $lib 'zlib.dll') -Destination $build -Force
& $exe (Join-Path $root 'tests\native\sparse-superblock.bin')
if ($LASTEXITCODE -ne 0) { throw "Sparse-volume regression failed with exit code $LASTEXITCODE." }
