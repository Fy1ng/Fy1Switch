$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'tests/RemoteDisplaySwitch.Remote.Tests/RemoteDisplaySwitch.Remote.Tests.csproj'
& dotnet test $project --configuration Release --nologo -p:RestoreLockedMode=true --logger 'trx;LogFileName=remote-tests.trx'
if ($LASTEXITCODE -ne 0) { throw 'Remote tests failed. No hardware, ADB, or existing user configuration is used by this suite.' }
