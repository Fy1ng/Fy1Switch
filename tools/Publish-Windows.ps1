param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts/windows/win-x64' }
$busy = @(Get-Process -Name 'RemoteDisplaySwitch.Host','RemoteDisplaySwitch.Worker','RemoteDisplaySwitch.Cli' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
if ($busy.Count) { throw 'The output directory contains a running application. Exit it manually or choose -OutputDirectory; no process was stopped.' }
& (Join-Path $PSScriptRoot 'Verify-Upstream.ps1')
foreach ($entry in @(@{Name='Host';Sub=''}, @{Name='Worker';Sub='worker'}, @{Name='Cli';Sub='cli'})) {
    $destination = if ($entry.Sub) { Join-Path $output $entry.Sub } else { $output }
    $project = Join-Path $root "src/windows/RemoteDisplaySwitch.$($entry.Name)/RemoteDisplaySwitch.$($entry.Name).csproj"
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $destination --nologo -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($entry.Name)" }
    $runtime = Get-Content -LiteralPath (Join-Path $destination "RemoteDisplaySwitch.$($entry.Name).runtimeconfig.json") -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath (Join-Path (Split-Path $project) 'obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $packageName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
        $package = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder "$packageName/$($framework.version)"
            if ((Test-Path -LiteralPath (Join-Path $candidate 'LICENSE.TXT')) -or (Test-Path -LiteralPath (Join-Path $candidate 'LICENSE'))) { $package = $candidate; break }
        }
        if (-not $package) { throw "Missing runtime license: $packageName" }
        $license = Join-Path $package 'LICENSE.TXT'
        if (-not (Test-Path -LiteralPath $license)) { $license = Join-Path $package 'LICENSE' }
        Copy-Item -LiteralPath $license -Destination (Join-Path $destination "$($framework.name)-LICENSE.TXT")
        $notices = Join-Path $package 'THIRD-PARTY-NOTICES.TXT'
        if (Test-Path -LiteralPath $notices) { Copy-Item -LiteralPath $notices -Destination (Join-Path $destination "$($framework.name)-THIRD-PARTY-NOTICES.TXT") }
    }
}
Copy-Item -LiteralPath (Join-Path $root 'vendor/powertoys/LICENSE') -Destination (Join-Path $output 'PowerToys.LICENSE')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $output
New-Item -ItemType Directory -Path (Join-Path $output 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'docs/Windows-P1.md') -Destination (Join-Path $output 'docs')
Copy-Item -LiteralPath (Join-Path $root 'docs/P0-findings.md') -Destination (Join-Path $output 'docs')
Copy-Item -LiteralPath (Join-Path $root 'docs/Remote-Control.md') -Destination (Join-Path $output 'docs')
Copy-Item -LiteralPath (Join-Path $root 'docs/Android.md') -Destination (Join-Path $output 'docs')
Copy-Item -LiteralPath (Join-Path $root 'docs/Relay.md') -Destination (Join-Path $output 'docs')
Copy-Item -LiteralPath (Join-Path $root 'docs/Pairing-v1.md') -Destination (Join-Path $output 'docs')
Copy-Item -LiteralPath (Join-Path $root 'docs/UI-Terminology.md') -Destination (Join-Path $output 'docs')
New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'licenses') -File | Copy-Item -Destination (Join-Path $output 'licenses')
Copy-Item -LiteralPath (Join-Path $root 'tools/Install-Windows.ps1') -Destination (Join-Path $output 'Install.ps1')
Copy-Item -LiteralPath (Join-Path $root 'tools/Uninstall-Windows.ps1') -Destination (Join-Path $output 'Uninstall.ps1')
Write-Output "Windows package: $output"
