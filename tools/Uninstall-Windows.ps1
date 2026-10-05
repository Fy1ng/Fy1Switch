$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ([IO.Path]::GetFileName($target) -ne 'RemoteDisplaySwitch') { throw 'Uninstall must run inside a recognized installation directory.' }
$markerPath = Join-Path $target '.installation.json'
if (-not (Test-Path -LiteralPath $markerPath)) { throw 'Installation marker missing.' }
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($marker.product -ne 'RemoteDisplaySwitch' -or $marker.path -ne $target) { throw 'Installation path does not match its marker.' }
$cursor = $target
while ($cursor) {
    if ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a reparse-point uninstall path.' }
    $next = Split-Path -Parent $cursor
    if ($next -eq $cursor) { break }
    $cursor = $next
}
if (@(Get-Process -Name 'RemoteDisplaySwitch.Host','RemoteDisplaySwitch.Worker','RemoteDisplaySwitch.Cli' -ErrorAction SilentlyContinue).Count) { throw 'Exit RemoteDisplaySwitch before uninstalling.' }
if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a reparse-point target.' }
if (@(Get-ChildItem -LiteralPath $target -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Refusing an installation containing reparse points.' }
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$expected = '"' + (Join-Path $target 'RemoteDisplaySwitch.Host.exe') + '" --background'
$current = (Get-ItemProperty -LiteralPath $registry -Name RemoteDisplaySwitch -ErrorAction SilentlyContinue).RemoteDisplaySwitch
if ($current -eq $expected) { Remove-ItemProperty -LiteralPath $registry -Name RemoteDisplaySwitch }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'RemoteDisplaySwitch.lnk'
if ($marker.shortcut -eq $shortcut -and (Test-Path -LiteralPath $shortcut)) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $owned = $link.TargetPath -eq (Join-Path $target 'RemoteDisplaySwitch.Host.exe')
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    if ($owned) { Remove-Item -LiteralPath $shortcut }
}
Remove-Item -LiteralPath $target -Recurse -Force
Write-Output 'Program files removed. Bindings, operation records and diagnostics were preserved in local application data.'
