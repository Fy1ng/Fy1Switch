[CmdletBinding()]
param([Parameter(Mandatory)][string]$MakeNsisPath, [string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler = [IO.Path]::GetFullPath($MakeNsisPath)
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/Fy1Switch.nsi') -Raw
$validation = [regex]::Match($source, '(?ms)^Function ValidateInstallDirectory\r?\n.*?^FunctionEnd')
if (-not $validation.Success) { throw 'Installer directory validation function was not found.' }
$run = Join-Path $root ('.local/packaging-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$harness = @'
Unicode true
RequestExecutionLevel user
SilentInstall silent
!include "FileFunc.nsh"
!define UNINSTALL_KEY "Software\Fy1Switch.PackagingTests.__RUN_ID__"
Name "Fy1Switch directory regression"
OutFile "directory-test.exe"
InstallDir "$LOCALAPPDATA\Programs\Fy1Switch"
Var InstallParent
__VALIDATION_FUNCTION__
Function .onInit
    ReadEnvStr $INSTDIR FY1SWITCH_TEST_INSTALL_DIR
    Call ValidateInstallDirectory
    FileOpen $9 "$EXEDIR\validated.txt" w
    FileWriteUTF16LE $9 "$INSTDIR$\r$\n$InstallParent"
    FileClose $9
FunctionEnd
Section
SectionEnd
'@
$harness = $harness.Replace('__RUN_ID__', [guid]::NewGuid().ToString('N')).Replace('__VALIDATION_FUNCTION__', $validation.Value)
$harnessPath = Join-Path $run 'directory-test.nsi'
Set-Content -LiteralPath $harnessPath -Value $harness -Encoding utf8
& $compiler /NOCONFIG /INPUTCHARSET UTF8 /V2 $harnessPath
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the production directory validation harness.' }
$existing = Join-Path $run 'existing/Fy1Switch'
New-Item -ItemType Directory -Path $existing -Force | Out-Null
$cases = @(
    @{ Name = 'Default per-user directory'; Path = Join-Path $env:LOCALAPPDATA 'Programs/Fy1Switch'; Valid = $true },
    @{ Name = 'Fresh install'; Path = Join-Path $run 'new/Fy1Switch'; Valid = $true },
    @{ Name = 'Existing install directory'; Path = $existing; Valid = $true },
    @{ Name = 'Spaces and Unicode'; Path = Join-Path $run '中文 path/new/Fy1Switch'; Valid = $true },
    @{ Name = 'Trailing separator'; Path = (Join-Path $run 'trailing/Fy1Switch') + '\'; Valid = $true },
    @{ Name = 'Normalized parent'; Path = (Join-Path $run 'nested/../Fy1Switch'); Valid = $true },
    @{ Name = 'Wrong directory name'; Path = Join-Path $run 'WrongName'; Valid = $false },
    @{ Name = 'Filesystem root'; Path = [IO.Path]::GetPathRoot($run); Valid = $false }
)
$resultPath = Join-Path $run 'validated.txt'
foreach ($case in $cases) {
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
    # Supply the path after NSIS initialization, which can replace an invalid /D with the default.
    $previousTestPath = $env:FY1SWITCH_TEST_INSTALL_DIR
    try {
        $env:FY1SWITCH_TEST_INSTALL_DIR = $case.Path
        $process = Start-Process -FilePath (Join-Path $run 'directory-test.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru
    }
    finally { $env:FY1SWITCH_TEST_INSTALL_DIR = $previousTestPath }
    if (-not $process.WaitForExit(15000)) { throw "Directory validation timed out: $($case.Name)" }
    if ($case.Valid) {
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) { throw "Rejected a valid path: $($case.Name)" }
        $actual = Get-Content -LiteralPath $resultPath -Encoding unicode
        $expected = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($case.Path))
        if ($actual[0] -ne $expected -or $actual[1] -ne [IO.Path]::GetDirectoryName($expected)) { throw "Incorrect normalized directory or parent: $($case.Name)" }
    }
    elseif ($process.ExitCode -ne 1 -or (Test-Path -LiteralPath $resultPath)) { throw "Accepted an invalid path: $($case.Name)" }
    Write-Output "Passed: $($case.Name)"
}
Write-Output "All $($cases.Count) installer directory regression cases passed. No installation or registry writes were performed."

. (Join-Path $PSScriptRoot 'Check-WindowsRuntime.ps1') -PackageDirectory $PackageDirectory
$required = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App') | ForEach-Object {
    [pscustomobject]@{ Name = $_; Version = [version]'10.0.0' }
}
$allRuntimes = @($required | ForEach-Object { "$($_.Name) 10.0.11 [C:\runtime-fixture]" })
$runtimeCases = @(
    @{ Name = 'Servicing patch accepted'; Lines = $allRuntimes; Missing = 0 },
    @{ Name = 'No runtimes installed'; Lines = @(); Missing = 3 },
    @{ Name = 'Desktop only'; Lines = $allRuntimes | Where-Object { $_ -notlike 'Microsoft.AspNetCore.App *' }; Missing = 1 },
    @{ Name = 'Previous major rejected'; Lines = $allRuntimes -replace '10\.0\.11', '9.0.11'; Missing = 3 },
    @{ Name = 'Next major rejected'; Lines = $allRuntimes -replace '10\.0\.11', '11.0.0'; Missing = 3 },
    @{ Name = 'Preview rejected'; Lines = $allRuntimes -replace '10\.0\.11', '10.0.0-preview.1'; Missing = 3 }
)
foreach ($case in $runtimeCases) {
    $missing = @(Test-Fy1SwitchRuntimeInventory -Requirements $required -RuntimeLines $case.Lines)
    if ($missing.Count -ne $case.Missing) { throw "Incorrect runtime inventory result: $($case.Name)" }
    Write-Output "Passed: $($case.Name)"
}

if ($PackageDirectory) {
    $package = [IO.Path]::GetFullPath($PackageDirectory)
    $fixture = Join-Path $run 'payload'
    New-Item -ItemType Directory -Path $fixture | Out-Null
    Get-ChildItem -LiteralPath $package -Force | Copy-Item -Destination $fixture -Recurse
    # Isolate external state: retain production install/uninstall logic, but use no shortcuts
    # and a unique process name so an unrelated running user copy is left alone.
    foreach ($scriptName in @('Install.ps1', 'Uninstall.ps1')) {
        $scriptPath = Join-Path $fixture $scriptName
        $script = Get-Content -LiteralPath $scriptPath -Raw
        $script = $script.Replace("Get-Process -Name 'Fy1Switch.Host','Fy1Switch.Worker','Fy1Switch.Cli'", "Get-Process -Name 'Fy1Switch.PackagingFixture.NoProcesses'")
        if ($scriptName -eq 'Install.ps1') { $script = $script.Replace('[switch]$NoShortcuts)', '[switch]$NoShortcuts = $true)') }
        Set-Content -LiteralPath $scriptPath -Value $script -Encoding utf8
    }
    $testRegistryKey = 'Software\Fy1Switch.PackagingTests.' + [guid]::NewGuid().ToString('N')
    $testRegistryPath = 'HKCU:\' + $testRegistryKey
    $testSource = $source.Replace('!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Fy1Switch"', ('!define UNINSTALL_KEY "' + $testRegistryKey + '"'))
    if ($testSource -eq $source) { throw 'Could not isolate the test installer registry key.' }
    $diagnostic = @'
DetailPrint "$1"
    FileOpen $9 "$EXEDIR\script-output.txt" a
    FileWrite $9 "$0$\r$\n$1$\r$\n"
    FileClose $9
'@
    $testSource = $testSource.Replace('DetailPrint "$1"', $diagnostic)
    $testSourcePath = Join-Path $run 'installer-test.nsi'
    Set-Content -LiteralPath $testSourcePath -Value $testSource -Encoding utf8
    $testInstaller = Join-Path $run 'installer-test.exe'
    & $compiler /NOCONFIG /INPUTCHARSET UTF8 /V2 "/DPAYLOAD_DIR=$fixture" "/DSETUP_OUT=$testInstaller" /DPRODUCT_VERSION=1.0.0 /DPRODUCT_VERSION_QUAD=1.0.0.0 /DPAYLOAD_SIZE_KB=1 "/DPRODUCT_ICON=$(Join-Path $root 'src/windows/Fy1Switch.Host/Assets/App.ico')" $testSourcePath
    if ($LASTEXITCODE -ne 0) { throw 'Full installer test compilation failed.' }
    $target = Join-Path $run 'installed path/Fy1Switch'
    foreach ($pass in @('Fresh NSIS installation', 'NSIS upgrade')) {
        $process = Start-Process -FilePath $testInstaller -ArgumentList "/S /D=$target" -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0) {
            $diagnosticPath = Join-Path $run 'script-output.txt'
            if (Test-Path -LiteralPath $diagnosticPath) { Get-Content -LiteralPath $diagnosticPath }
            throw "$pass failed."
        }
        $marker = Get-Content -LiteralPath (Join-Path $target '.installation.json') -Raw | ConvertFrom-Json
        $registration = Get-ItemProperty -LiteralPath $testRegistryPath
        if ($marker.path -ne $target -or $marker.shortcut -or $registration.InstallLocation -ne $target -or
            -not (Test-Path -LiteralPath (Join-Path $target 'Uninstall.exe'))) { throw 'Installer files, marker or isolated registration are incorrect.' }
        Write-Output "Passed: $pass"
    }
    if (-not $target.StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstall test target escaped its workspace.' }
    $process = Start-Process -FilePath (Join-Path $target 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw 'NSIS uninstaller timed out.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $testRegistryPath)) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $testRegistryPath)) { throw 'NSIS uninstall left program files or its isolated registration.' }
    Write-Output 'Passed: NSIS uninstall and isolated registration removal'
}
