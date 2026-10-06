$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach ($name in @('Pairing','Discovery')) {
 & dotnet test (Join-Path $root "tests/RemoteDisplaySwitch.$name.Tests/RemoteDisplaySwitch.$name.Tests.csproj") -c Release --nologo -p:RestoreLockedMode=true
 if ($LASTEXITCODE -ne 0) { throw "Pairing validation failed: $name" }
}
& dotnet test (Join-Path $root 'tests/RemoteDisplaySwitch.Remote.Tests/RemoteDisplaySwitch.Remote.Tests.csproj') -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Remote compatibility tests failed.' }
