$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& dotnet test (Join-Path $root 'tests\APFSMount.Core.Tests\APFSMount.Core.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
Write-Host 'Core tests passed. Optional live WinFsp test: dotnet run --project tests/APFSMount.WinFsp.Smoke -c Release'
