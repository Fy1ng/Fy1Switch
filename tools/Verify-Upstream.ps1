$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendorRoot = [IO.Path]::GetFullPath((Join-Path $root 'vendor/powertoys'))
$manifest = Get-Content -LiteralPath (Join-Path $vendorRoot 'upstream.json') -Raw | ConvertFrom-Json
$prefix = $vendorRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($entry in $manifest.files) {
    $target = [IO.Path]::GetFullPath((Join-Path $vendorRoot $entry.path))
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source manifest path escapes vendor directory.' }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Missing upstream file: $($entry.path)" }
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if (-not $actual.Equals($entry.sha256, [StringComparison]::OrdinalIgnoreCase)) { throw "Modified upstream file: $($entry.path)" }
}
Write-Output "Verified $($manifest.files.Count) unmodified PowerToys files at $($manifest.commit)."
