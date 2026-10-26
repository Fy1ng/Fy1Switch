[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$MakeNsisPath,
    [string]$Version,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payload = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $root 'artifacts/windows/win-x64' }
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts/windows/packages' }
$payload = [IO.Path]::TrimEndingDirectorySeparator($payload)
$output = [IO.Path]::TrimEndingDirectorySeparator($output)
$payloadPrefix = $payload.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($output.Equals($payload, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The package output must be outside the published directory.'
}

foreach ($checkedPath in @($payload, $output)) {
    $cursor = $checkedPath
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing a reparse-point package path.'
        }
        $next = Split-Path -Parent $cursor
        if ($next -eq $cursor) { break }
        $cursor = $next
    }
}
foreach ($relativePath in @('Fy1Switch.Host.exe', 'Fy1Switch.Host.runtimeconfig.json', 'coreclr.dll',
        'worker/Fy1Switch.Worker.exe', 'worker/coreclr.dll', 'cli/Fy1Switch.Cli.exe', 'cli/coreclr.dll',
        'Install.ps1', 'Uninstall.ps1', 'THIRD-PARTY-NOTICES.md', 'PowerToys.LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $relativePath) -PathType Leaf)) {
        throw "The complete self-contained published package is required. Missing: $relativePath"
    }
}
$entries = @(Get-ChildItem -LiteralPath $payload -Recurse -Force)
foreach ($entry in $entries) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a package containing reparse points.' }
    if ($entry.Name -match '^(\.git|\.local|\.installation\.json|\.env(?:\..*)?|diagnostics|logs)$' -or
        (-not $entry.PSIsContainer -and $entry.Extension -match '^\.(pfx|p12|key|pem|db(?:-.*)?|sqlite(?:3)?(?:-.*)?|dmp|log)$')) {
        throw "Refusing possible local data or credentials in the package: $($entry.Name)"
    }
}
$files = @($entries | Where-Object { -not $_.PSIsContainer })

if (-not $Version) {
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'Fy1Switch.Host.exe'))
    $Version = '{0}.{1}.{2}' -f $fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart
    if ($fileVersion.FilePrivatePart -ne 0) { $Version += '.' + $fileVersion.FilePrivatePart }
}
if ($Version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}(?:\.\d{1,5})?$' -or
    @($Version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count) {
    throw 'Version must contain three or four numeric components, each from 0 to 65535.'
}
$versionParts = $Version.Split('.')
$versionQuad = if ($versionParts.Count -eq 3) { $Version + '.0' } else { $Version }

if (-not $MakeNsisPath) {
    $compiler = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($compiler) { $MakeNsisPath = $compiler.Source }
    else {
        foreach ($candidate in @((Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe'), (Join-Path $env:ProgramFiles 'NSIS/makensis.exe'))) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $MakeNsisPath = $candidate; break }
        }
    }
}
if (-not $MakeNsisPath -or -not (Test-Path -LiteralPath $MakeNsisPath -PathType Leaf)) {
    throw 'NSIS 3 is required. Supply its makensis.exe using -MakeNsisPath.'
}
$MakeNsisPath = [IO.Path]::GetFullPath($MakeNsisPath)
$setupName = "Fy1Switch-$Version-win-x64-setup.exe"
$portableName = "Fy1Switch-$Version-win-x64-portable.zip"
$setupPath = Join-Path $output $setupName
$portablePath = Join-Path $output $portableName
foreach ($destination in @($setupPath, $portablePath)) {
    if ((Test-Path -LiteralPath $destination) -and -not $Force) { throw "Package already exists. Use -Force to replace it: $destination" }
    if ((Test-Path -LiteralPath $destination) -and ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing a reparse-point output file.'
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$staging = Join-Path $output ('.packaging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
$temporarySetup = Join-Path $staging $setupName
$temporaryPortable = Join-Path $staging $portableName
try {
    $sizeKb = [long][Math]::Ceiling(($files | Measure-Object -Property Length -Sum).Sum / 1KB)
    $arguments = @('/NOCONFIG', '/INPUTCHARSET', 'UTF8', '/V3', "/DPAYLOAD_DIR=$payload", "/DSETUP_OUT=$temporarySetup",
        "/DPRODUCT_VERSION=$Version", "/DPRODUCT_VERSION_QUAD=$versionQuad", "/DPAYLOAD_SIZE_KB=$sizeKb",
        "/DPRODUCT_ICON=$(Join-Path $root 'src/windows/Fy1Switch.Host/Assets/App.ico')",
        (Join-Path $PSScriptRoot 'installer/Fy1Switch.nsi'))
    & $MakeNsisPath @arguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $temporarySetup -PathType Leaf)) { throw 'NSIS compilation failed.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($temporaryPortable, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "Fy1Switch/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }

    [IO.File]::Move($temporarySetup, $setupPath, [bool]$Force)
    [IO.File]::Move($temporaryPortable, $portablePath, [bool]$Force)
    Write-Output "NSIS installer: $setupPath"
    Write-Output "Portable ZIP: $portablePath"
    Get-FileHash -LiteralPath $setupPath, $portablePath -Algorithm SHA256 | Select-Object Path, Hash
}
finally {
    # Only the two known temporary files are removed; never recursively delete an output directory.
    foreach ($temporaryFile in @($temporarySetup, $temporaryPortable)) {
        if (Test-Path -LiteralPath $temporaryFile -PathType Leaf) { Remove-Item -LiteralPath $temporaryFile -Force }
    }
    [IO.Directory]::Delete($staging, $false)
}
