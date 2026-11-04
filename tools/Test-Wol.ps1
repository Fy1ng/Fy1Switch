[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'tests/Fy1Switch.WolGateway.Tests/Fy1Switch.WolGateway.Tests.csproj'
& dotnet restore $project --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'WOL restore failed.' }
& dotnet test $project --configuration Release --no-restore --nologo --logger 'trx;LogFileName=wol-tests.trx'
if ($LASTEXITCODE -ne 0) { throw 'WOL tests failed.' }
Write-Output 'Gateway tests use temporary data, simulated SSH, and loopback HTTP only. No real WOL packet was requested.'
