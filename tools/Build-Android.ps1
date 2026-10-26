[CmdletBinding()]
param(
    [string]$SdkPath = 'C:\android-sdk',
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$GradlePath,
    [ValidateSet('Debug', 'Optimized')]
    [string]$Configuration = 'Debug',
    [switch]$Clean,
    [switch]$Offline,
    [switch]$LiveWindowsTest
)

$ErrorActionPreference = 'Stop'
if ($LiveWindowsTest -and [string]::IsNullOrWhiteSpace($env:FY1SWITCH_ANDROID_FIXTURE)) {
    throw 'FY1SWITCH_ANDROID_FIXTURE must point to a temporary Windows HTTPS fixture JSON.'
}
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\android'))
if (-not (Test-Path -LiteralPath (Join-Path $SdkPath 'platforms\android-36\android.jar'))) {
    throw 'Android SDK 36 is required. Supply -SdkPath.'
}
if (-not $JavaHome) {
    $javaCommand = Get-Command java -ErrorAction SilentlyContinue
    if ($javaCommand) {
        $settings = & $javaCommand.Source -XshowSettings:properties -version 2>&1
        $homeLine = $settings | Select-String '^\s*java.home\s*=\s*(.+)$' | Select-Object -First 1
        if ($homeLine) { $JavaHome = $homeLine.Matches[0].Groups[1].Value.Trim() }
    }
}
if (-not $JavaHome -or -not (Test-Path -LiteralPath (Join-Path $JavaHome 'bin\javac.exe'))) {
    throw 'A JDK 17 or newer is required. Supply -JavaHome.'
}
if (-not $GradlePath) {
    $gradleHome = if ($env:GRADLE_USER_HOME) { $env:GRADLE_USER_HOME } else { Join-Path $env:USERPROFILE '.gradle' }
    $cached = Get-ChildItem -LiteralPath (Join-Path $gradleHome 'wrapper\dists\gradle-8.13-bin') -Filter gradle.bat -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\gradle-8.13\bin\gradle.bat' } | Select-Object -First 1
    if ($cached) { $GradlePath = $cached.FullName }
}
if (-not $GradlePath -or -not (Test-Path -LiteralPath $GradlePath)) {
    throw 'Gradle 8.13 is required. Supply its bin\gradle.bat using -GradlePath.'
}
$previousJava = $env:JAVA_HOME
$previousAndroid = $env:ANDROID_HOME
$previousSdk = $env:ANDROID_SDK_ROOT
try {
    $env:JAVA_HOME = $JavaHome
    $env:ANDROID_HOME = $SdkPath
    $env:ANDROID_SDK_ROOT = $SdkPath
    $version = & $GradlePath --version
    if ($LASTEXITCODE -ne 0 -or -not ($version -match '^Gradle 8\.13$')) { throw 'Use stable Gradle 8.13 for this project.' }
    $tasks = if ($LiveWindowsTest) { @(':app:liveWindowsTest') } else {
        @(
            ":app:test${Configuration}UnitTest"
            ":app:lint${Configuration}"
            ":app:assemble${Configuration}"
        )
    }
    if ($Clean) { $tasks = @('clean') + $tasks }
    $arguments = @('-p', $project, '--no-daemon', '--console=plain') + $tasks
    if ($Offline) { $arguments += '--offline' }
    & $GradlePath @arguments
    if ($LASTEXITCODE -ne 0) { throw "Android validation/build failed (exit $LASTEXITCODE)." }
    if ($LiveWindowsTest) {
        Write-Output "Live Windows tests: $(Join-Path $project 'app\build\reports\tests\liveWindowsTest\index.html')"
    }
    else {
        $variant = $Configuration.ToLowerInvariant()
        Write-Output "APK: $(Join-Path $project "app\build\outputs\apk\$variant\app-$variant.apk")"
        Write-Output "Tests: $(Join-Path $project "app\build\reports\tests\test${Configuration}UnitTest\index.html")"
        Write-Output "Lint: $(Join-Path $project "app\build\reports\lint-results-$variant.html")"
        if ($Configuration -eq 'Optimized') {
            Write-Output 'Optimized APK: R8 code/resource shrinking enabled; signed with the local debug certificate for testing, not a production signing key.'
        }
    }
}
finally {
    $env:JAVA_HOME = $previousJava
    $env:ANDROID_HOME = $previousAndroid
    $env:ANDROID_SDK_ROOT = $previousSdk
}
