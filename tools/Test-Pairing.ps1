$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach ($name in @('Pairing','Discovery')) {
 & dotnet test (Join-Path $root "tests/Fy1Switch.$name.Tests/Fy1Switch.$name.Tests.csproj") -c Release --nologo -p:RestoreLockedMode=true
 if ($LASTEXITCODE -ne 0) { throw "Pairing validation failed: $name" }
}
& dotnet test (Join-Path $root 'tests/Fy1Switch.Remote.Tests/Fy1Switch.Remote.Tests.csproj') -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Remote compatibility tests failed.' }
