param([string]$Version = '0.1.0', [string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $repository 'artifacts/packages' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$output = Join-Path $OutputRoot "lumibelle-web-$Version"
dotnet publish (Join-Path $repository 'src/Lumibelle.Web/Lumibelle.Web.csproj') -c Release --self-contained false -p:UseAppHost=false "-p:Version=$Version" -o $output
if ($LASTEXITCODE) { throw 'Web publish failed.' }
Compress-Archive -Path "$output/*" -DestinationPath "$output.zip" -Force
Write-Output "$output.zip"
