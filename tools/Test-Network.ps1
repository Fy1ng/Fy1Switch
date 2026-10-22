$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Test-Remote.ps1')
& (Join-Path $PSScriptRoot 'Test-Relay.ps1')
& dotnet test (Join-Path $root 'tests/Fy1Switch.EndToEnd.Tests/Fy1Switch.EndToEnd.Tests.csproj') -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Cross-channel HTTPS/WSS integration failed.' }
