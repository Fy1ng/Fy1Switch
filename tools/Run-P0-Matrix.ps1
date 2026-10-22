param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$TargetKey,
    [ValidateRange(1, 30)]
    [int]$Cycles = 30,
    [ValidateRange(1, 300)]
    [int]$OffSeconds = 5,
    [ValidateRange(2, 30)]
    [int]$OnSeconds = 3,
    [switch]$AllowPowerWrite
)
$ErrorActionPreference = 'Stop'
if (-not $AllowPowerWrite) { throw 'This hardware test requires -AllowPowerWrite and a person available to recover the display.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probe = Join-Path $root 'artifacts/p0/win-x64/Fy1Switch.Probe.exe'
if (-not (Test-Path -LiteralPath $probe)) { throw 'Publish the current P0 probe before running the matrix.' }
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$reportDirectory = Join-Path $root "diagnostics/matrix-$stamp"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
for ($iteration = 1; $iteration -le $Cycles; $iteration++) {
    $stdout = Join-Path $reportDirectory "$iteration.jsonl"
    $stderr = Join-Path $reportDirectory "$iteration.log"
    $started = [DateTimeOffset]::UtcNow
    & $probe cycle $TargetKey --gdi-order-experiment --allow-power-write --off-seconds $OffSeconds 1> $stdout 2> $stderr
    $exitCode = $LASTEXITCODE
    $entries = @(Get-Content -LiteralPath $stdout | ForEach-Object { $_ | ConvertFrom-Json })
    $summary = $entries | Where-Object kind -eq 'power_test_summary' | Select-Object -Last 1
    $success = $exitCode -eq 0 -and $null -ne $summary -and $summary.onReadBack -eq $true
    $results.Add([pscustomobject]@{
        iteration = $iteration
        startedUtc = $started.ToString('O')
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        offSeconds = $OffSeconds
        exitCode = $exitCode
        softwareCyclePassed = $success
        physicallyObserved = $false
    })
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory 'summary.json') -Encoding utf8
    Write-Output "Cycle $iteration/$Cycles : softwareCyclePassed=$success; exit=$exitCode"
    if (-not $success) { throw "Hardware matrix stopped after the first uncertain or failed cycle. Inspect $reportDirectory; do not assume that ON recovery succeeded." }
    if ($iteration -lt $Cycles) { Start-Sleep -Seconds $OnSeconds }
}
Write-Output "Completed $Cycles software cycles. Physical acceptance remains separate. Reports: $reportDirectory"
