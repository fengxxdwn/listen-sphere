[CmdletBinding()]
param([string] $OutputPath = 'TestResults/R10/file-sizes.md')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$resolvedOutput = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
New-Item -ItemType Directory -Force -Path (Split-Path $resolvedOutput) | Out-Null
$files = foreach ($section in @('src', 'apps', 'tests')) {
    Get-ChildItem -LiteralPath (Join-Path $root $section) -Recurse -File |
        Where-Object {
            $_.Extension -in @('.cs', '.xaml', '.kt') -and
            $_.FullName -notmatch '[\\/](bin|obj|build|\.gradle|\.kotlin)[\\/]'
        } |
        ForEach-Object {
            [pscustomobject]@{
                Path = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                Bytes = $_.Length
            }
        }
}
$limit = 50KB
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Source file size report')
$lines.Add('')
$lines.Add("Generated (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))")
$lines.Add("Files scanned: $($files.Count); review threshold: $limit bytes. Exceeding the threshold is reported, not automatically rejected.")
$lines.Add('')
$lines.Add('| Bytes | File |')
$lines.Add('| ---: | --- |')
foreach ($file in ($files | Sort-Object Bytes -Descending | Select-Object -First 30)) {
    $lines.Add("| $($file.Bytes) | ``$($file.Path)`` |")
}
$lines.Add('')
$lines.Add("Files above threshold: $(($files | Where-Object Bytes -gt $limit).Count)")
[IO.File]::WriteAllLines($resolvedOutput, $lines)
Write-Output $resolvedOutput
