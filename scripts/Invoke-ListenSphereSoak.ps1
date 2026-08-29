[CmdletBinding(DefaultParameterSetName = 'Launch')]
param(
    [Parameter(ParameterSetName = 'Launch')]
    [ValidateSet('Controller', 'Sender')]
    [string] $Application = 'Controller',

    [Parameter(Mandatory, ParameterSetName = 'Existing')]
    [ValidateRange(1, 2147483647)]
    [int] $ProcessId,

    [ValidateRange(0.05, 1440)]
    [double] $DurationMinutes = 30,

    [ValidateRange(1, 60)]
    [int] $SampleIntervalSeconds = 5,

    [string] $OutputDirectory = 'artifacts\stage4-soak',

    [Parameter(ParameterSetName = 'Launch')]
    [switch] $KeepRunning
)

$ErrorActionPreference = 'Stop'
$startedByScript = $PSCmdlet.ParameterSetName -eq 'Launch'
$startedAt = [DateTimeOffset]::Now
$resolvedOutput = [System.IO.Path]::GetFullPath(
    (Join-Path (Get-Location) $OutputDirectory))
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

if ($startedByScript) {
    $executable = Join-Path (Get-Location) (
        "apps\ListenSphere.$Application\bin\Release\net10.0-windows\ListenSphere.$Application.exe")
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "未找到 Release 程序：$executable。请先执行 dotnet build ListenSphere.sln -c Release。"
    }

    $process = Start-Process -FilePath $executable `
        -WorkingDirectory (Split-Path $executable) -PassThru
}
else {
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
}

$samples = [System.Collections.Generic.List[object]]::new()
$deadline = [DateTimeOffset]::Now.AddMinutes($DurationMinutes)
$failure = $null

try {
    while ([DateTimeOffset]::Now -lt $deadline) {
        Start-Sleep -Seconds $SampleIntervalSeconds
        try {
            $process.Refresh()
            if ($process.HasExited) {
                $failure = "进程提前退出，退出代码 $($process.ExitCode)。"
                break
            }

            $samples.Add([pscustomobject]@{
                Timestamp = [DateTimeOffset]::Now.ToString('O')
                Responding = $process.Responding
                WorkingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB, 2)
                PrivateMemoryMiB = [Math]::Round($process.PrivateMemorySize64 / 1MB, 2)
                HandleCount = $process.HandleCount
                ThreadCount = $process.Threads.Count
                TotalProcessorSeconds = [Math]::Round($process.TotalProcessorTime.TotalSeconds, 2)
            })

            if (-not $process.Responding) {
                $failure = '窗口停止响应。'
                break
            }
        }
        catch {
            $failure = "无法读取进程状态：$($_.Exception.Message)"
            break
        }
    }
}
finally {
    $finishedAt = [DateTimeOffset]::Now
    $stamp = $startedAt.ToString('yyyyMMdd-HHmmss')
    $csvPath = Join-Path $resolvedOutput "soak-$stamp.csv"
    $jsonPath = Join-Path $resolvedOutput "soak-$stamp.json"
    $samples | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

    $first = $samples | Select-Object -First 1
    $last = $samples | Select-Object -Last 1
    $report = [ordered]@{
        Application = if ($startedByScript) { $Application } else { $process.ProcessName }
        ProcessId = $process.Id
        StartedAt = $startedAt.ToString('O')
        FinishedAt = $finishedAt.ToString('O')
        RequestedDurationMinutes = $DurationMinutes
        SampleCount = $samples.Count
        Passed = $null -eq $failure
        Failure = $failure
        InitialPrivateMemoryMiB = $first.PrivateMemoryMiB
        FinalPrivateMemoryMiB = $last.PrivateMemoryMiB
        PrivateMemoryGrowthMiB = if ($null -ne $first -and $null -ne $last) {
            [Math]::Round($last.PrivateMemoryMiB - $first.PrivateMemoryMiB, 2)
        } else { $null }
        CsvPath = $csvPath
    }
    $report | ConvertTo-Json | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    if ($startedByScript -and -not $KeepRunning -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
    }
}

Get-Content -LiteralPath $jsonPath
if ($null -ne $failure) {
    exit 1
}
