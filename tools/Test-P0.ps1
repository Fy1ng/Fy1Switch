$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Verify-Upstream.ps1')
foreach ($project in @('tests/RemoteDisplaySwitch.PowerDisplay.Tests/RemoteDisplaySwitch.PowerDisplay.Tests.csproj', 'tests/RemoteDisplaySwitch.Probe.Tests/RemoteDisplaySwitch.Probe.Tests.csproj')) {
    & dotnet test (Join-Path $root $project) --configuration Release --nologo -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
}
