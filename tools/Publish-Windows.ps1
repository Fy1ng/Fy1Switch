param([string]$OutputDirectory, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts/windows/win-x64' }
$busy = @(Get-Process -Name 'Fy1Switch.Host','Fy1Switch.Worker','Fy1Switch.Cli' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
if ($busy.Count) { throw 'The output directory contains a running application. Exit it manually or choose -OutputDirectory; no process was stopped.' }
$applications = @(@{Name='Host';Sub=''}, @{Name='Worker';Sub='worker'}, @{Name='Cli';Sub='cli'})
foreach ($entry in $applications) {
    $destination = if ($entry.Sub) { Join-Path $output $entry.Sub } else { $output }
    $existingConfig = Join-Path $destination "Fy1Switch.$($entry.Name).runtimeconfig.json"
    if (Test-Path -LiteralPath $existingConfig) {
        $existingRuntime = (Get-Content -LiteralPath $existingConfig -Raw | ConvertFrom-Json).runtimeOptions
        if ([bool]$existingRuntime.includedFrameworks -ne [bool]$SelfContained) {
            throw 'The output contains a different deployment mode. Choose a new -OutputDirectory; existing files were not removed.'
        }
    }
}
if (-not $SelfContained -and (Test-Path -LiteralPath $output)) {
    if (@(Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.Name -in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll') }).Count) {
        throw 'The output contains a bundled .NET runtime. Choose a new -OutputDirectory; existing files were not removed.'
    }
}
& (Join-Path $PSScriptRoot 'Verify-Upstream.ps1')
foreach ($entry in $applications) {
    $destination = if ($entry.Sub) { Join-Path $output $entry.Sub } else { $output }
    $project = Join-Path $root "src/windows/Fy1Switch.$($entry.Name)/Fy1Switch.$($entry.Name).csproj"
    $publishArguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', ([bool]$SelfContained).ToString().ToLowerInvariant(), '-o', $destination, '--nologo', '-p:RestoreLockedMode=true')
    if (-not $SelfContained) { $publishArguments += @('-p:RollForward=LatestPatch', '-p:TargetLatestRuntimePatch=false') }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($entry.Name)" }
    $runtime = Get-Content -LiteralPath (Join-Path $destination "Fy1Switch.$($entry.Name).runtimeconfig.json") -Raw | ConvertFrom-Json
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
Copy-Item -LiteralPath (Join-Path $root 'README.en.md') -Destination $output
New-Item -ItemType Directory -Path (Join-Path $output 'assets/readme') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'assets/README.md') -Destination (Join-Path $output 'assets')
Get-ChildItem -LiteralPath (Join-Path $root 'assets/readme') -File | Copy-Item -Destination (Join-Path $output 'assets/readme')
New-Item -ItemType Directory -Path (Join-Path $output 'vendor/powertoys') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'vendor/powertoys/LICENSE') -Destination (Join-Path $output 'vendor/powertoys')
New-Item -ItemType Directory -Path (Join-Path $output 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'docs/Development-History.md') -Destination (Join-Path $output 'docs')
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
Copy-Item -LiteralPath (Join-Path $root 'tools/Check-WindowsRuntime.ps1') -Destination (Join-Path $output 'Check-Runtime.ps1')
if ($SelfContained) {
    'This package includes its .NET runtime. No separate .NET runtime installation is required.' | Set-Content -LiteralPath (Join-Path $output 'Runtime-Requirements.txt') -Encoding utf8
}
else {
    $runtimeGuide = @(
        'This win-x64 package uses installed system runtimes; it does not bundle .NET.',
        'Install both .NET Desktop Runtime 10.0 x64 and ASP.NET Core Runtime 10.0 x64.',
        '.NET Desktop Runtime also includes Microsoft.NETCore.App. The .NET SDK is not required.',
        'Use the latest stable 10.0 servicing patch. Other major versions, x86 and ARM64 runtimes do not satisfy this package.',
        'Official downloads: https://dotnet.microsoft.com/en-us/download/dotnet/10.0',
        'Check locally: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Check-Runtime.ps1',
        'Extract the complete package and run Fy1Switch.Host.exe. The app does not install runtimes automatically.'
    )
    $runtimeGuide | Set-Content -LiteralPath (Join-Path $output 'Runtime-Requirements.txt') -Encoding utf8
    & (Join-Path $output 'Check-Runtime.ps1') -PackageDirectory $output
}
Write-Output "Windows package: $output"
