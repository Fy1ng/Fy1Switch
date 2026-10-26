[CmdletBinding()]
param([string]$PackageDirectory, [string]$DotNetPath)

$ErrorActionPreference = 'Stop'

function Get-Fy1SwitchRuntimeRequirements {
    param([Parameter(Mandatory=$true)][string]$Directory)
    $requirements = @()
    $modes = @()
    foreach ($application in @('Fy1Switch.Host', 'worker/Fy1Switch.Worker', 'cli/Fy1Switch.Cli')) {
        $path = Join-Path $Directory ($application + '.runtimeconfig.json')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing runtime configuration: $application" }
        $options = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).runtimeOptions
        $bundled = @($options.includedFrameworks | Where-Object { $_ })
        $shared = @($options.frameworks | Where-Object { $_ }) + @($options.framework | Where-Object { $_ })
        if (($bundled.Count -eq 0) -eq ($shared.Count -eq 0)) { throw "Invalid or mixed runtime configuration: $application" }
        $modes += [bool]$bundled.Count
        $frameworks = if ($bundled.Count) { $bundled } else { $shared }
        $names = @($frameworks | ForEach-Object { $_.name })
        if ('Microsoft.NETCore.App' -notin $names) { throw "Missing .NET runtime requirement: $application" }
        if ($application -eq 'Fy1Switch.Host' -and ('Microsoft.WindowsDesktop.App' -notin $names -or 'Microsoft.AspNetCore.App' -notin $names)) {
            throw 'The Host runtime configuration must include Desktop and ASP.NET Core requirements.'
        }
        foreach ($framework in $frameworks) {
            if ($framework.name -notin @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App','Microsoft.AspNetCore.App') -or
                $framework.version -notmatch '^\d+\.\d+\.\d+$') { throw "Unsupported runtime requirement: $application" }
            $requirements += [pscustomobject]@{ Name = $framework.name; Version = [version]$framework.version }
        }
    }
    if (@($modes | Select-Object -Unique).Count -ne 1) { throw 'The Host, Worker and CLI use different deployment modes.' }
    [pscustomobject]@{ SelfContained = [bool]$modes[0]; Frameworks = @($requirements | Sort-Object Name, Version -Unique) }
}

function Test-Fy1SwitchRuntimeInventory {
    param([Parameter(Mandatory=$true)][object[]]$Requirements, [string[]]$RuntimeLines)
    $inventory = @($RuntimeLines | ForEach-Object {
        if ($_ -match '^(Microsoft\.(?:NETCore|WindowsDesktop|AspNetCore)\.App) (\d+\.\d+\.\d+) \[') {
            [pscustomobject]@{ Name = $Matches[1]; Version = [version]$Matches[2] }
        }
    })
    @($Requirements | Where-Object {
        $required = $_
        -not @($inventory | Where-Object {
            $_.Name -eq $required.Name -and $_.Version.Major -eq $required.Version.Major -and
            $_.Version.Minor -eq $required.Version.Minor -and $_.Version -ge $required.Version
        }).Count
    })
}

function Test-Fy1SwitchX64Executable {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $reader = $null
    try {
        $reader = New-Object IO.BinaryReader([IO.File]::OpenRead($Path))
        if ($reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $reader.BaseStream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 0x40 -or $offset -gt $reader.BaseStream.Length - 6) { return $false }
        $reader.BaseStream.Position = $offset
        return $reader.ReadUInt32() -eq 0x4550 -and $reader.ReadUInt16() -eq 0x8664
    }
    catch { return $false }
    finally { if ($reader) { $reader.Dispose() } }
}

# Dot-sourcing exposes the pure inventory functions for isolated regression tests.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $PackageDirectory) { $PackageDirectory = $PSScriptRoot }
$runtime = Get-Fy1SwitchRuntimeRequirements -Directory $PackageDirectory
if ($runtime.SelfContained) { Write-Output 'This package includes its .NET runtime.'; return }
$candidates = @()
if ($DotNetPath) { $candidates = @([IO.Path]::GetFullPath($DotNetPath)) }
else {
    if ($env:DOTNET_ROOT_X64) { $candidates += Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe' }
    if ([Environment]::Is64BitProcess -and $env:DOTNET_ROOT) { $candidates += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
    $baseKey = $null
    $runtimeKey = $null
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        $runtimeKey = $baseKey.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        if ($runtimeKey -and $runtimeKey.GetValue('InstallLocation')) { $candidates += Join-Path $runtimeKey.GetValue('InstallLocation') 'dotnet.exe' }
    }
    finally { if ($runtimeKey) { $runtimeKey.Dispose() }; if ($baseKey) { $baseKey.Dispose() } }
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    if (${env:ProgramW6432}) { $programFiles = ${env:ProgramW6432} }
    $candidates += Join-Path $programFiles 'dotnet/dotnet.exe'
    $candidates += Join-Path $programFiles 'dotnet/x64/dotnet.exe'
}
$selected = $candidates | Select-Object -Unique | Where-Object { Test-Fy1SwitchX64Executable -Path $_ } | Select-Object -First 1
$runtimeLines = @()
if ($selected) {
    $runtimeLines = @(& $selected --list-runtimes 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { $runtimeLines = @() }
}
$missing = @(Test-Fy1SwitchRuntimeInventory -Requirements $runtime.Frameworks -RuntimeLines $runtimeLines)
if ($missing.Count) {
    $details = ($missing | ForEach-Object { $_.Name + ' >= ' + $_.Version + ' (same major/minor, x64)' }) -join '; '
    throw "Required system runtimes are missing: $details. Install .NET Desktop Runtime 10.0 x64 and ASP.NET Core Runtime 10.0 x64 from https://dotnet.microsoft.com/en-us/download/dotnet/10.0 . Runtimes are not installed automatically."
}
Write-Output "System .NET x64 runtime requirements satisfied: $selected"
