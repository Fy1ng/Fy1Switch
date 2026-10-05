param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs'), [switch]$NoShortcuts)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $source 'RemoteDisplaySwitch.Host.exe'))) { throw 'Run Install.ps1 from the complete published Windows package.' }
$parent = [IO.Path]::GetFullPath($InstallRoot)
$target = [IO.Path]::GetFullPath((Join-Path $parent 'RemoteDisplaySwitch'))
if (-not $target.StartsWith($parent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid install target.' }
if ($target.Equals($source, [StringComparison]::OrdinalIgnoreCase)) { throw 'The published package and install directory must differ.' }
foreach ($checkedPath in @($source, $parent)) {
    $cursor = $checkedPath
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing a reparse-point installation path.' }
        $next = Split-Path -Parent $cursor
        if ($next -eq $cursor) { break }
        $cursor = $next
    }
}
if (@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Refusing a package containing reparse points.' }
if (@(Get-Process -Name 'RemoteDisplaySwitch.Host','RemoteDisplaySwitch.Worker','RemoteDisplaySwitch.Cli' -ErrorAction SilentlyContinue).Count) { throw 'Exit RemoteDisplaySwitch before installation or upgrade.' }
if (Test-Path -LiteralPath $target) {
    $markerPath = Join-Path $target '.installation.json'
    if (-not (Test-Path -LiteralPath $markerPath)) { throw 'Refusing to overwrite an unrecognized directory.' }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.product -ne 'RemoteDisplaySwitch' -or $marker.path -ne $target) { throw 'Invalid installation marker.' }
    if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a reparse-point target.' }
    if (@(Get-ChildItem -LiteralPath $target -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Refusing an installation containing reparse points.' }
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse -Force
$shortcutPath = $null
if (-not $NoShortcuts) {
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'RemoteDisplaySwitch.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if ((Test-Path -LiteralPath $shortcutPath) -and $shortcut.TargetPath -ne (Join-Path $target 'RemoteDisplaySwitch.Host.exe')) { throw 'The Start Menu shortcut belongs to another installation.' }
    $shortcut.TargetPath = Join-Path $target 'RemoteDisplaySwitch.Host.exe'
    $shortcut.WorkingDirectory = $target
    $shortcut.Save()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}
@{product='RemoteDisplaySwitch';path=$target;shortcut=$shortcutPath} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target '.installation.json') -Encoding utf8
Write-Output "Installed: $target"
Write-Output 'Startup is not enabled automatically. Open the tray settings to choose it. User data is stored separately and is preserved on uninstall.'
