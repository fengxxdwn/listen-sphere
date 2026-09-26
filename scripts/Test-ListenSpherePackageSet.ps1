[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('unsigned', 'signed')]
    [string] $AndroidSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-UniqueXmlValue {
    param(
        [Parameter(Mandatory)][xml] $Document,
        [Parameter(Mandatory)][string] $ElementName,
        [Parameter(Mandatory)][string] $SourcePath
    )

    $nodes = @($Document.SelectNodes("//$ElementName"))
    if ($nodes.Count -ne 1) {
        throw "Expected exactly one $ElementName element in $SourcePath; found $($nodes.Count)."
    }
    $value = $nodes[0].InnerText.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$ElementName is empty in $SourcePath."
    }
    return $value
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$versionPath = Join-Path $repositoryRoot 'eng/ListenSphere.Version.props'
[xml] $versionDocument = Get-Content -LiteralPath $versionPath -Raw
$version = Get-UniqueXmlValue $versionDocument 'ListenSphereProductVersion' $versionPath
$versionCodeText = Get-UniqueXmlValue $versionDocument 'ListenSphereAndroidVersionCode' $versionPath
$versionCode = 0
if (-not [int]::TryParse($versionCodeText, [ref] $versionCode) -or $versionCode -le 0) {
    throw "ListenSphereAndroidVersionCode must be a positive integer: $versionCodeText"
}

$packageRoot = Join-Path $repositoryRoot 'artifacts/packages'
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "Package directory is missing: $packageRoot"
}

$androidReleaseName = if ($AndroidSigning -eq 'signed') {
    "ListenSphere-Mobile-$version.apk"
}
else {
    "ListenSphere-Mobile-release-unsigned-$version.apk"
}
$oppositeAndroidReleaseName = if ($AndroidSigning -eq 'signed') {
    "ListenSphere-Mobile-release-unsigned-$version.apk"
}
else {
    "ListenSphere-Mobile-$version.apk"
}
$expectedNames = @(
    "ListenSphere-Controller-win-x64-$version.zip",
    "ListenSphere-Sender-win-x64-$version.zip",
    "ListenSphere-Setup-$version-win-x64.exe",
    "ListenSphere-Mobile-debug-$version.apk",
    $androidReleaseName
)

foreach ($name in $expectedNames) {
    $path = Join-Path $packageRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected package is missing: $name"
    }
    if ((Get-Item -LiteralPath $path).Length -le 0) {
        throw "Package is empty: $name"
    }
}
if (Test-Path -LiteralPath (Join-Path $packageRoot $oppositeAndroidReleaseName)) {
    throw "Unexpected Android release mode package exists: $oppositeAndroidReleaseName"
}

$actualPackageNames = @(
    Get-ChildItem -LiteralPath $packageRoot -File -Filter 'ListenSphere-*' |
        Where-Object { $_.Extension -in @('.zip', '.exe', '.apk') } |
        Select-Object -ExpandProperty Name |
        Sort-Object
)
$unexpectedNames = @($actualPackageNames | Where-Object { $_ -notin $expectedNames })
if ($unexpectedNames.Count -ne 0) {
    throw "Unexpected or stale ListenSphere packages exist: $($unexpectedNames -join ', ')"
}
if ($actualPackageNames.Count -ne $expectedNames.Count) {
    throw "Expected exactly $($expectedNames.Count) ListenSphere packages; found $($actualPackageNames.Count)."
}

$checksumPath = Join-Path $packageRoot 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw 'SHA256SUMS.txt is missing.'
}
$checksumLines = @(
    Get-Content -LiteralPath $checksumPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if ($checksumLines.Count -ne $expectedNames.Count) {
    throw "SHA256SUMS.txt must contain $($expectedNames.Count) entries; found $($checksumLines.Count)."
}

$manifest = @{}
foreach ($line in $checksumLines) {
    if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
        throw "Invalid SHA256SUMS entry: $line"
    }
    $hash = $Matches[1].ToLowerInvariant()
    $name = $Matches[2]
    if ([IO.Path]::IsPathRooted($name) -or $name -ne [IO.Path]::GetFileName($name)) {
        throw "SHA256SUMS must use relative file names only: $name"
    }
    if ($name -eq 'SHA256SUMS.txt') {
        throw 'SHA256SUMS.txt must not hash itself.'
    }
    if ($manifest.ContainsKey($name)) {
        throw "SHA256SUMS contains a duplicate file name: $name"
    }
    $manifest[$name] = $hash
}

foreach ($name in $expectedNames) {
    if (-not $manifest.ContainsKey($name)) {
        throw "SHA256SUMS is missing an entry for: $name"
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest[$name] -ne $actualHash) {
        throw "SHA256 mismatch for: $name"
    }
}

Write-Output "Package set valid: $version (Android versionCode $versionCode, signing $AndroidSigning)"
Write-Output "Packages: $($expectedNames.Count)"
Write-Output "Checksums: $checksumPath"
