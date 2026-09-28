param(
    [Parameter(Mandatory=$true)][string]$ExperimentDirectory,
    [Parameter(Mandatory=$true)][string]$Matrix,
    [string]$Container = 'lumibelle-h3-acceleration-20260911',
    [string]$ProductionUrl = 'http://127.0.0.1:8188'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ExperimentDirectory)
$cases = Get-Content -LiteralPath $Matrix -Raw | ConvertFrom-Json
foreach($test in $cases) {
    if(Test-Path (Join-Path $root "results/$($test.name)")) { throw "Run already exists: $($test.name). Choose an explicit new attempt name." }
    $arguments = @{ Case='acceleration'; Name=$test.name; Container=$Container; ExperimentDirectory=$root;
        ProductionUrl=$ProductionUrl; Configuration=$test.configuration; Scene=$test.scene; Frames=$test.frames; Seed=$test.seed }
    if($test.native) { $arguments.Native=$true }
    if($test.warm) { $arguments.Warm=$true }
    & "$PSScriptRoot/run-case.ps1" @arguments
    $budget = Get-Content -LiteralPath (Join-Path $root 'budget.json') -Raw | ConvertFrom-Json
    $run = @($budget.Runs | Where-Object { $_.Name -eq $test.name })[-1]
    & python "$PSScriptRoot/acceleration_report.py" $root
    if($run.Status -notin @('complete','failed')) { throw "Matrix stopped: $($run.Status). No automatic retries." }
    if($run.Status -eq 'failed') { Write-Output "Failed attempt retained: $($test.name). Continuing with the next independent recipe." }
}
