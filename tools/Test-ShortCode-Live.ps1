$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& dotnet build (Join-Path $root 'tests/RemoteDisplaySwitch.LanFixture/RemoteDisplaySwitch.LanFixture.csproj') -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
$gradle = (Get-ChildItem "$env:USERPROFILE/.gradle/wrapper/dists/gradle-8.13-bin" -Recurse -Filter gradle.bat | Select-Object -First 1).FullName
if (-not $gradle) { throw 'Gradle 8.13 is required.' }
$oldAndroid = $env:ANDROID_HOME
$oldFixture = $env:RDS_SHORT_CODE_FIXTURE
$env:ANDROID_HOME = 'C:\android-sdk'
$exe = Join-Path $root 'tests/RemoteDisplaySwitch.LanFixture/bin/Release/net10.0-windows/RemoteDisplaySwitch.LanFixture.exe'
try {
 foreach ($mode in @('forward','reverse')) {
  $directory = Join-Path $root ('.local/shortcode-' + $mode + '-' + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $directory | Out-Null
  $process = Start-Process -FilePath $exe -ArgumentList @($mode,('"'+$directory+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory 'out.log') -RedirectStandardError (Join-Path $directory 'err.log')
  try {
   $env:RDS_SHORT_CODE_FIXTURE = Join-Path $directory 'fixture.json'
   for ($attempt=0; $attempt -lt 100 -and -not (Test-Path -LiteralPath $env:RDS_SHORT_CODE_FIXTURE); $attempt++) { Start-Sleep -Milliseconds 100; if ($process.HasExited) { throw 'Fixture failed before readiness.' } }
   if (-not (Test-Path -LiteralPath $env:RDS_SHORT_CODE_FIXTURE)) { throw 'Fixture readiness timeout.' }
   & $gradle -p (Join-Path $root 'src/android') --offline --no-daemon --console=plain :app:liveShortCodeTest
   if ($LASTEXITCODE -ne 0) { throw "Short-code $mode interoperability failed." }
   Write-Output "SHORT_CODE_DIRECTION=$mode PASSED"
  } finally {
   New-Item -ItemType File -Path (Join-Path $directory 'stop') -Force | Out-Null
   if (-not $process.WaitForExit(15000)) { Write-Warning 'Fixture awaits its bounded lifetime.' }
  }
 }
} finally { $env:ANDROID_HOME=$oldAndroid; $env:RDS_SHORT_CODE_FIXTURE=$oldFixture }
