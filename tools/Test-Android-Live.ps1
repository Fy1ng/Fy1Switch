$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet build (Join-Path $root 'tests/RemoteDisplaySwitch.AndroidFixture/RemoteDisplaySwitch.AndroidFixture.csproj') -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'HTTPS fixture build failed.' }
& dotnet build (Join-Path $root 'src/windows/RemoteDisplaySwitch.Worker/RemoteDisplaySwitch.Worker.csproj') -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Simulation worker build failed.' }
$fixtureExe = Join-Path $root 'tests/RemoteDisplaySwitch.AndroidFixture/bin/Release/net10.0-windows/RemoteDisplaySwitch.AndroidFixture.exe'
$workerExe = Join-Path $root 'src/windows/RemoteDisplaySwitch.Worker/bin/Release/net10.0-windows/RemoteDisplaySwitch.Worker.exe'
$directory = Join-Path $root ('.local/android-live-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$fixture = Start-Process -FilePath $fixtureExe -ArgumentList @(('"'+$directory+'"'),('"'+$workerExe+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory 'fixture.log') -RedirectStandardError (Join-Path $directory 'fixture-error.log')
$previous = $env:RDS_ANDROID_FIXTURE
try {
    $env:RDS_ANDROID_FIXTURE = Join-Path $directory 'fixture.json'
    for ($attempt=0; $attempt -lt 100 -and -not (Test-Path -LiteralPath $env:RDS_ANDROID_FIXTURE); $attempt++) {
        Start-Sleep -Milliseconds 100
        if ($fixture.HasExited) { throw 'Fixture exited before readiness.' }
    }
    if (-not (Test-Path -LiteralPath $env:RDS_ANDROID_FIXTURE)) { throw 'Fixture did not become ready.' }
    & (Join-Path $PSScriptRoot 'Build-Android.ps1') -LiveWindowsTest -Offline
}
finally {
    New-Item -ItemType File -Path (Join-Path $directory 'stop') -Force | Out-Null
    $env:RDS_ANDROID_FIXTURE = $previous
    if (-not $fixture.WaitForExit(15000)) { Write-Warning 'Fixture has not exited; its bounded lifetime remains active.' }
}
