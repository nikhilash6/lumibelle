param(
    [Parameter(Mandatory=$true)][ValidateSet('standard','turbo8','comparison','guided','progressive','long','preview-app','acceleration')][string]$Case,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-z0-9-]+$')][string]$Name,
    [string]$Container = 'lumibelle-h3-benchmark-20260910',
    [string]$ProductionUrl = 'http://127.0.0.1:8188',
    [double]$SourceScale = 0.8,
    [long]$Seed = 20260910,
    [ValidateSet(73,141,243)][int]$Frames = 73,
    [ValidateRange(60,43200)][int]$MaxRunSeconds = 7200,
    [string]$ExperimentDirectory,
    [ValidateSet('standard','turbo4','turbo8','sol','spectrum','cache','pdd','larry')][string]$Configuration = 'standard',
    [ValidateSet('dialogue','motion')][string]$Scene = 'dialogue',
    [switch]$Native,
    [switch]$Warm
)
$ErrorActionPreference = 'Stop'
$benchRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/h3-benchmark'))
if($ExperimentDirectory) { $benchRoot = [IO.Path]::GetFullPath($ExperimentDirectory) }
if($Case -eq 'acceleration' -and ($Frames -notin @(141,243) -or $Seed -notin @(20260911,20260912))) { throw 'Choose a frozen acceleration scenario.' }
$appQueueFile = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../App_Data/ai-jobs/queue.json'))
function Test-LumibelleComfyWork {
    # Read the durable index only, never snapshots/prompts or mutable queue APIs.
    # A batch can be preparing/saving between otherwise idle ComfyUI requests.
    $appQueue = Get-Content -LiteralPath $appQueueFile -Raw | ConvertFrom-Json
    if($appQueue.schemaVersion -ne 1 -or $null -eq $appQueue.jobs) { throw 'Cannot verify the Lumibelle queue.' }
    return @($appQueue.jobs | Where-Object {
        $_.backend -eq 'ComfyUI' -and ($_.state -eq 'Running' -or $_.remoteUnconfirmed -or
            ($_.state -eq 'Waiting' -and -not $_.cancelRequested))
    }).Count -gt 0
}
$budgetFile = Join-Path $benchRoot 'budget.json'
$budget = if(Test-Path $budgetFile) { Get-Content $budgetFile -Raw | ConvertFrom-Json } else {
    [pscustomobject]@{ TargetSeconds=7200; ConsumedSeconds=0; Runs=@() }
}
if(-not $budget.PSObject.Properties['TargetSeconds']) { $budget | Add-Member -NotePropertyName TargetSeconds -NotePropertyValue 7200 }
if(@($budget.Runs | Where-Object { $_.Status -eq 'running' }).Count) { throw 'Resolve the recorded running benchmark before starting another.' }
# Two hours is a planning target, per the user's September 10 correction.
# Keep a per-case watchdog for a hung worker, not an aggregate budget cutoff.
if(Test-Path (Join-Path $benchRoot "results/$Name")) { throw 'This result already exists; choose a new run name.' }
$production = Invoke-RestMethod "$ProductionUrl/queue" -TimeoutSec 10
if(@($production.queue_running).Count -or @($production.queue_pending).Count -or (Test-LumibelleComfyWork)) { throw 'Production has work. No benchmark was started.' }
if(-not (Test-Path (Join-Path $benchRoot 'preflight.json'))) { throw 'Complete checkpoint hashing first.' }
$inspected = (docker inspect $Container | ConvertFrom-Json)[0]
if($LASTEXITCODE -ne 0 -or $inspected.Name -ne "/$Container" -or $inspected.HostConfig.NetworkMode -ne 'none') { throw 'Unexpected benchmark container.' }
if($inspected.Image -ne 'sha256:b3ae356d86c36d82e9900f30ec8891e28ebb1a9e27dbe2bd3f1debe78c33789f') { throw 'The benchmark must use the pinned ComfyUI image.' }
$modelMount = @($inspected.Mounts | Where-Object { $_.Destination -eq '/comfyui/models' })
if($modelMount.Count -ne 1 -or $modelMount[0].RW) { throw 'The model mount must be read-only.' }
if(@($inspected.Mounts | Where-Object { $_.Destination -in @('/comfyui/output','/comfyui/input','/comfyui/user','/comfyui/custom_nodes') }).Count) { throw 'Production state must not be mounted into the benchmark.' }
$expectedBenchMount = '/mnt/' + $benchRoot.Substring(0,1).ToLowerInvariant() + $benchRoot.Substring(2).Replace('\','/')
$benchMount = @($inspected.Mounts | Where-Object { $_.Destination -eq '/bench' })
if($benchMount.Count -ne 1 -or $benchMount[0].Source -ne $expectedBenchMount) { throw 'The container artifact mount does not match this experiment.' }
$gpuStatus = docker exec $Container nvidia-smi --id=0 --query-gpu=utilization.gpu,memory.free --format=csv,noheader,nounits
if($LASTEXITCODE -ne 0) { throw 'Cannot verify GPU availability.' }
$gpuFields = $gpuStatus.Trim().Split(',')
if([int]$gpuFields[0] -gt 10 -or [int]$gpuFields[1] -lt 2048) { throw 'The benchmark GPU is busy or has insufficient free headroom.' }
$run = [pscustomobject]@{Name=$Name;Case=$Case;StartedUtc=[DateTimeOffset]::UtcNow.ToString('o');Status='running';Seconds=0;ExitCode=$null}
$budget.Runs = @($budget.Runs) + $run
$budget | ConvertTo-Json -Depth 5 | Set-Content $budgetFile -Encoding utf8
$timer = [Diagnostics.Stopwatch]::StartNew()
$reason = $null
try {
    $workerArguments = @('exec',$Container,
        '/opt/venv/bin/python','-u','/harness/runner.py','--case',$Case,'--name',$Name,
        '--seconds-remaining',$MaxRunSeconds,'--source-scale',$SourceScale.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--seed',$Seed,'--frames',$(if($Case -eq 'long') {141} else {$Frames}))
    if($Case -eq 'acceleration') {
        $workerArguments = @('exec',$Container,'/opt/venv/bin/python','-u','/harness/acceleration_worker.py',
            '--name',$Name,'--configuration',$Configuration,'--scene',$Scene,'--frames',$Frames,'--seed',$Seed,'--seconds-remaining',$MaxRunSeconds)
        if($Native) { $workerArguments += '--native' }
        if($Warm) { $workerArguments += '--warm' }
    }
    $worker = Start-Process -FilePath (Get-Command docker).Source -ArgumentList $workerArguments `
        -WindowStyle Hidden -RedirectStandardOutput (Join-Path $benchRoot "$Name.stdout.log") `
        -RedirectStandardError (Join-Path $benchRoot "$Name.stderr.log") -PassThru
    while(-not $worker.HasExited) {
        Start-Sleep -Seconds 2
        $worker.Refresh()
        if($worker.HasExited) { break }
        try {
            $production = Invoke-RestMethod "$ProductionUrl/queue" -TimeoutSec 5
            if(@($production.queue_running).Count -or @($production.queue_pending).Count -or (Test-LumibelleComfyWork)) { $reason='production_work_arrived' }
        } catch { $reason='production_status_unavailable' }
        if($timer.Elapsed.TotalSeconds -ge $MaxRunSeconds) { $reason='case_timeout' }
        if($reason) {
            # Kill only the script PID published by this isolated worker, never production.
            $workerFile = Join-Path $benchRoot "results/$Name/worker.json"
            if(Test-Path $workerFile) {
                $workerInfo = Get-Content $workerFile -Raw | ConvertFrom-Json
                if($workerInfo.pid -gt 1 -and $workerInfo.name -eq $Name) {
                    docker exec $Container kill -TERM $workerInfo.pid
                }
            }
            $worker.WaitForExit(10000) | Out-Null
            if(-not $worker.HasExited) {
                # A stuck CUDA operation may delay TERM handling. Force only our
                # verified isolated worker to exit; production is never targeted.
                if($workerInfo -and $workerInfo.pid -gt 1 -and $workerInfo.name -eq $Name) {
                    docker exec $Container kill -KILL $workerInfo.pid
                    $worker.WaitForExit(10000) | Out-Null
                }
                if(-not $worker.HasExited) { throw 'Benchmark worker did not stop; do not start another case.' }
            }
            break
        }
    }
    $worker.WaitForExit()
    $run.ExitCode = $worker.ExitCode
    $run.Status = if($reason) { $reason } elseif($worker.ExitCode -eq 0) { 'complete' } else { 'failed' }
} finally {
    $run.Seconds = $timer.Elapsed.TotalSeconds
    $budget.ConsumedSeconds += $run.Seconds
    $budget | ConvertTo-Json -Depth 5 | Set-Content $budgetFile -Encoding utf8
}
$run | ConvertTo-Json
Write-Output ("Benchmark worker time: {0:N1} minutes (planning target: {1:N0} minutes)" -f ($budget.ConsumedSeconds/60),($budget.TargetSeconds/60))
