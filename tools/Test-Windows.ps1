$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Test-P0.ps1')
foreach ($name in @('Worker','Runtime')) {
    & dotnet test (Join-Path $root "tests/Fy1Switch.$name.Tests/Fy1Switch.$name.Tests.csproj") -c Release --nologo -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $name" }
}
& dotnet build (Join-Path $root 'src/windows/Fy1Switch.Host/Fy1Switch.Host.csproj') -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
