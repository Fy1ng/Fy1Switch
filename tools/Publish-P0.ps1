$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'artifacts/p0/win-x64'
& (Join-Path $PSScriptRoot 'Verify-Upstream.ps1')
& dotnet publish (Join-Path $root 'src/windows/RemoteDisplaySwitch.Probe/RemoteDisplaySwitch.Probe.csproj') --configuration Release --runtime win-x64 --self-contained true --output $output --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'P0 publish failed.' }
Copy-Item -LiteralPath (Join-Path $root 'vendor/powertoys/LICENSE') -Destination (Join-Path $output 'PowerToys.LICENSE')
Copy-Item -LiteralPath (Join-Path $root 'licenses/WmiLight.LICENSE') -Destination (Join-Path $output 'WmiLight.LICENSE')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $output 'README-P0.md')
Copy-Item -LiteralPath (Join-Path $root 'vendor/powertoys/upstream.json') -Destination (Join-Path $output 'powertoys-upstream.json')
$runtime = Get-Content -LiteralPath (Join-Path $output 'RemoteDisplaySwitch.Probe.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$assets = Get-Content -LiteralPath (Join-Path $root 'src/windows/RemoteDisplaySwitch.Probe/obj/project.assets.json') -Raw | ConvertFrom-Json
$runtimePackage = $null
foreach ($packageFolder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $packageFolder "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
    if (Test-Path -LiteralPath (Join-Path $candidate 'LICENSE.TXT')) { $runtimePackage = $candidate; break }
}
if (-not $runtimePackage) { throw 'Cannot locate .NET runtime licenses; do not distribute this output.' }
Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $output 'DOTNET-LICENSE.TXT')
Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $output 'DOTNET-THIRD-PARTY-NOTICES.TXT')
Write-Output "P0 package: $output"
