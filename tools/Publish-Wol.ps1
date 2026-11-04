[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts/wol/linux-x64' }
$project = Join-Path $root 'src/wol/Fy1Switch.WolGateway/Fy1Switch.WolGateway.csproj'
& dotnet publish $project -c Release -r linux-x64 --self-contained false -o $output -p:RuntimeIdentifiers= -p:NuGetLockFilePath=packages.linux-x64.lock.json -p:RestoreLockedMode=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'WOL Gateway publish failed.' }
Copy-Item -LiteralPath (Join-Path $root 'docs/Wake-on-LAN.md') -Destination (Join-Path $output 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'src/wol/deploy') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'src/wol/openwrt') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'src/wol/wol.config.example.json') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output
New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force | Out-Null
foreach ($license in @('Microsoft.Data.Sqlite.LICENSE', 'SQLitePCLRaw.LICENSE', 'SQLitePCLRaw.NOTICE', 'Microsoft.AspNetCore.LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $root "licenses/$license") -Destination (Join-Path $output 'licenses')
}
'Requires Linux x64, ASP.NET Core Runtime 10.0, OpenSSH client, and the existing WireGuard interface. This package does not install or alter WireGuard.' | Set-Content -LiteralPath (Join-Path $output 'Runtime-Requirements.txt') -Encoding utf8
Write-Output "WOL Gateway Linux x64 package: $output (framework-dependent; publishing does not deploy it)."
