[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts/relay/linux-x64' }
& dotnet publish (Join-Path $root 'src/relay/Fy1Switch.Relay/Fy1Switch.Relay.csproj') -c Release -r linux-x64 --self-contained true -o $output -p:RuntimeIdentifiers= -p:NuGetLockFilePath=packages.linux-x64.lock.json -p:RestoreLockedMode=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Relay publish failed.' }
$assets = Get-Content -LiteralPath (Join-Path $root 'src/relay/Fy1Switch.Relay/obj/project.assets.json') -Raw | ConvertFrom-Json
$runtime = Get-Content -LiteralPath (Join-Path $output 'Fy1Switch.Relay.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $packageName = $framework.name.ToLowerInvariant() + '.runtime.linux-x64'
    $package = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder "$packageName/$($framework.version)"
        if (Test-Path -LiteralPath (Join-Path $candidate 'LICENSE.TXT')) { $package = $candidate; break }
    }
    if (-not $package) { throw "Missing runtime license: $packageName" }
    Copy-Item -LiteralPath (Join-Path $package 'LICENSE.TXT') -Destination (Join-Path $output "$($framework.name)-LICENSE.TXT")
    $notices = Join-Path $package 'THIRD-PARTY-NOTICES.TXT'
    if (Test-Path -LiteralPath $notices) { Copy-Item -LiteralPath $notices -Destination (Join-Path $output "$($framework.name)-THIRD-PARTY-NOTICES.TXT") }
}
Copy-Item -LiteralPath (Join-Path $root 'docs/Relay.md') -Destination (Join-Path $output 'README.md')
Write-Output "Relay Linux x64 package: $output (built, not executed on Linux or deployed)."
