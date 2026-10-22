[CmdletBinding()]
param(
    [switch]$CreateLocalConfig,
    [Guid]$DeviceId = [Guid]::Empty,
    [switch]$DockerBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repository 'tests/Fy1Switch.Relay.Tests/Fy1Switch.Relay.Tests.csproj'

if ($CreateLocalConfig) {
    if ($DeviceId -eq [Guid]::Empty) { throw 'Specify the Windows device UUID with -DeviceId.' }
    if ($DockerBuild) { throw 'Generate credentials separately from a Docker build.' }
    $path = Join-Path $repository 'src/relay/relay.config.local.json'
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $windowsBytes = New-Object byte[] 32
        $controllerBytes = New-Object byte[] 32
        $random.GetBytes($windowsBytes)
        $random.GetBytes($controllerBytes)
        $config = @{
            devices = @(@{
                deviceId = $DeviceId.ToString()
                windowsToken = [Convert]::ToBase64String($windowsBytes)
                controllerToken = [Convert]::ToBase64String($controllerBytes)
            })
            requestTimeoutSeconds = 30
            requestsPerMinute = 60
        } | ConvertTo-Json -Depth 4
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($config)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $stream.Dispose() }
    }
    finally { $random.Dispose() }
    Write-Host "Created ignored local configuration: $path"
    Write-Host 'Protect its file permissions. Tokens were not printed; never commit this file.'
    return
}

Push-Location $repository
try {
    & dotnet restore $testProject --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Relay restore failed.' }
    & dotnet build $testProject --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Relay build failed.' }
    & dotnet test $testProject --configuration Release --no-restore --no-build --nologo --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Relay fixture tests failed.' }
    if ($DockerBuild) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker is not installed. No container validation was performed.' }
        & docker build --file src/relay/Dockerfile --tag fy1switch-relay:local .
        if ($LASTEXITCODE -ne 0) { throw 'Relay Docker build failed.' }
    }
}
finally { Pop-Location }
