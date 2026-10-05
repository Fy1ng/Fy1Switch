$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Test-P0.ps1')
foreach ($name in @('Worker','Runtime')) {
    & dotnet test (Join-Path $root "tests/RemoteDisplaySwitch.$name.Tests/RemoteDisplaySwitch.$name.Tests.csproj") -c Release --nologo -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $name" }
}
& dotnet build (Join-Path $root 'src/windows/RemoteDisplaySwitch.Host/RemoteDisplaySwitch.Host.csproj') -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
