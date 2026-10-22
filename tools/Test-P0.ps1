$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Verify-Upstream.ps1')
foreach ($project in @('tests/Fy1Switch.PowerDisplay.Tests/Fy1Switch.PowerDisplay.Tests.csproj', 'tests/Fy1Switch.Probe.Tests/Fy1Switch.Probe.Tests.csproj')) {
    & dotnet test (Join-Path $root $project) --configuration Release --nologo -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
}
