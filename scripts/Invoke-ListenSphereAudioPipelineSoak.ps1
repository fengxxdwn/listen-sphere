[CmdletBinding()]
param(
    [ValidateRange(2,120)] [double] $DurationMinutes = 30,
    [string] $OutputDirectory = 'TestResults/R8/soak'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$previousDuration = $env:LISTENSPHERE_SOAK_MINUTES
$previousCsv = $env:LISTENSPHERE_SOAK_CSV
try {
    $env:LISTENSPHERE_SOAK_MINUTES = $DurationMinutes.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:LISTENSPHERE_SOAK_CSV = Join-Path $outputPath 'pipeline.csv'
    & dotnet test (Join-Path $repositoryRoot 'tests/ListenSphere.Core.Tests') -c Release --no-build --no-restore `
        --filter 'FullyQualifiedName~AudioPipelineSoakTests' --logger 'trx;LogFileName=pipeline.trx' `
        --results-directory $outputPath
    if ($LASTEXITCODE -ne 0) { throw "Audio pipeline soak failed with exit code $LASTEXITCODE" }
} finally {
    $env:LISTENSPHERE_SOAK_MINUTES = $previousDuration
    $env:LISTENSPHERE_SOAK_CSV = $previousCsv
}
