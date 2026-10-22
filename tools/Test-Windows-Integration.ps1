param([switch]$Hardware, [string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package = if ($PackageDirectory) { [IO.Path]::GetFullPath($PackageDirectory) } else { Join-Path $root 'artifacts/windows/win-x64' }
$hostExe = Join-Path $package 'RemoteDisplaySwitch.Host.exe'
$cli = Join-Path $package 'cli/RemoteDisplaySwitch.Cli.exe'
$run = [Guid]::NewGuid().ToString('N')
$data = Join-Path $root ".local/integration-$run"
$pipe = "RemoteDisplaySwitch.Integration.$run"
$arguments = @('--background','--data-dir',('"'+$data+'"'),'--pipe',$pipe,'--stop-after-seconds','180')
if (-not $Hardware) { $arguments += '--simulate' }
function Invoke-Control([string[]]$Words) {
    $lines = & $cli @Words --pipe $pipe --host-path $hostExe
    $code = $LASTEXITCODE
    $reply = ($lines -join [Environment]::NewLine) | ConvertFrom-Json
    if ($code -ne 0 -or -not $reply.success) { throw "Control failed: $($Words[0]); exit=$code; error=$($reply.errorCode)" }
    return $reply
}
function Start-TestHost {
    $process = Start-Process -FilePath $hostExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 700
    return $process
}
$hostProcess = Start-TestHost
$targetId = $null
$offAttempted = $false
try {
    $initial = Invoke-Control @('status')
    if ($initial.snapshot.binding -or $initial.snapshot.observation.state -ne 'Unknown') { throw 'Expected an unbound, unknown initial state.' }
    $discovery = (Invoke-Control @('discover')).discovery
    if ($discovery.candidates.Count -ne 1) { throw 'Expected one explicitly supported candidate.' }
    $candidate = $discovery.candidates[0]
    $bound = (Invoke-Control @('bind',$candidate.targetKey,$discovery.topologyFingerprint,$candidate.name)).snapshot
    $targetId = $bound.binding.targetId
    $before = (Invoke-Control @('refresh')).snapshot
    if ($before.observation.state -ne 'On') {
        $recovery = (Invoke-Control @('on',$targetId,[Guid]::NewGuid().ToString())).operation
        if ($recovery.outcome -ne 'Confirmed' -or $recovery.observation.state -ne 'On') { throw 'ON recovery was not confirmed; no OFF will be sent.' }
    }
    $operationId = [Guid]::NewGuid().ToString()
    $offAttempted = $true
    $off = (Invoke-Control @('off',$targetId,$operationId)).operation
    if ($off.outcome -ne 'Confirmed' -or $off.observation.state -ne 'Off') { throw 'OFF was not confirmed by a readback.' }
    $duplicate = (Invoke-Control @('off',$targetId,$operationId)).operation
    if ($duplicate.updatedAt -ne $off.updatedAt) { throw 'Duplicate command was executed again.' }
    Start-Sleep -Seconds 5
    $null = Invoke-Control @('shutdown')
    if (-not $hostProcess.WaitForExit(15000)) { throw 'Host shutdown timed out.' }
    $restartedAt = [DateTimeOffset]::UtcNow
    $hostProcess = Start-TestHost
    $restored = (Invoke-Control @('status')).snapshot
    if ($restored.binding.targetId -ne $targetId -or $restored.binding.needsConfirmation) { throw 'Binding was not restored correctly.' }
    if (-not $Hardware) {
        $refreshDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
        while (($restored.busy -or $restored.observation.state -eq 'Unknown') -and [DateTimeOffset]::UtcNow -lt $refreshDeadline) {
            Start-Sleep -Milliseconds 200
            $restored = (Invoke-Control @('status')).snapshot
        }
        if ($restored.observation.state -ne 'On' -or $restored.observation.source -ne 'simulation' -or
            [DateTimeOffset]$restored.observation.observedAt -le $restartedAt) { throw 'Restart did not automatically obtain a fresh simulated observation.' }
    }
    $stored = (Invoke-Control @('operation',$operationId)).operation
    if ($stored.outcome -ne 'Confirmed') { throw 'Operation journal was not persisted.' }
    $on = (Invoke-Control @('on',$targetId,[Guid]::NewGuid().ToString())).operation
    if ($on.outcome -ne 'Confirmed' -or $on.observation.state -ne 'On') { throw 'Fresh-process ON was not confirmed.' }
    $offAttempted = $false
    if (-not $Hardware) {
        $startup = (Invoke-Control @('startup','on')).snapshot
        if (-not $startup.startupEnabled) { throw 'Simulated startup toggle failed.' }
        $null = Invoke-Control @('startup','off')
    }
    [pscustomobject]@{simulation=(-not $Hardware);offConfirmed=$true;duplicateDeduplicated=$true;bindingRestored=$true;automaticRefreshConfirmed=(-not $Hardware);freshProcessOnConfirmed=$true;dataDirectory=$data} | ConvertTo-Json
}
finally {
    if ($offAttempted -and $targetId) {
        if ($hostProcess.HasExited) { $hostProcess = Start-TestHost }
        try {
            $restoration = (Invoke-Control @('on',$targetId,[Guid]::NewGuid().ToString())).operation
            if ($restoration.outcome -ne 'Confirmed' -or $restoration.observation.state -ne 'On') { Write-Warning 'ON command was sent, but restoration was not confirmed.' }
        }
        catch { Write-Warning 'ON restoration could not be confirmed; inspect the physical display.' }
    }
    if (-not $hostProcess.HasExited) {
        try { $null = Invoke-Control @('shutdown') } catch { Write-Warning 'Could not request host shutdown.' }
        if (-not $hostProcess.WaitForExit(15000)) { Write-Warning 'The test host did not exit; its safety timer remains active.' }
    }
}
