[CmdletBinding()]
param(
    [string] $IsccPath
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

function Assert-SafeArtifactPath {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ArtifactRoot
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
    $artifactPrefix = $resolvedArtifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside artifacts: $resolvedPath"
    }
}

function Assert-PublishedApplication {
    param(
        [Parameter(Mandatory)][string] $PublishDirectory,
        [Parameter(Mandatory)][string] $ExecutableName,
        [Parameter(Mandatory)][string] $ExpectedProductVersion,
        [Parameter(Mandatory)][string] $ExpectedFileVersion
    )

    if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
        throw "Publish input is missing: $PublishDirectory`nRun ./scripts/Publish-ListenSphereWindows.ps1 first."
    }

    $executable = Join-Path $PublishDirectory $ExecutableName
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable is missing: $executable`nRun ./scripts/Publish-ListenSphereWindows.ps1 first."
    }

    $versionInfo = (Get-Item -LiteralPath $executable).VersionInfo
    if ($versionInfo.ProductVersion -ne $ExpectedProductVersion) {
        throw "$ExecutableName ProductVersion is '$($versionInfo.ProductVersion)'; expected '$ExpectedProductVersion'."
    }
    if ($versionInfo.FileVersion -ne $ExpectedFileVersion) {
        throw "$ExecutableName FileVersion is '$($versionInfo.FileVersion)'; expected '$ExpectedFileVersion'."
    }

    foreach ($runtimeFile in @(
        [IO.Path]::ChangeExtension($ExecutableName, '.runtimeconfig.json'),
        'hostfxr.dll',
        'hostpolicy.dll',
        'coreclr.dll',
        'PresentationFramework.dll'
    )) {
        $runtimePath = Join-Path $PublishDirectory $runtimeFile
        if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
            throw "Self-contained runtime file is missing: $runtimePath`nRun ./scripts/Publish-ListenSphereWindows.ps1 first."
        }
    }
}

function Resolve-IsccPath {
    param([string] $ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = [IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "ISCC.exe was not found at the explicit path: $resolved"
        }
        return $resolved
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($candidate in @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    $registryPaths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($entry in Get-ItemProperty $registryPaths -ErrorAction SilentlyContinue |
        Where-Object {
            $null -ne $_.PSObject.Properties['DisplayName'] -and
            $null -ne $_.PSObject.Properties['InstallLocation'] -and
            $_.DisplayName -like 'Inno Setup version 6*'
        }) {
        $candidate = Join-Path $entry.InstallLocation 'ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'Inno Setup 6 was not found. Install it, add ISCC.exe to PATH, or pass -IsccPath.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $root 'artifacts'
$packageRoot = Join-Path $artifactRoot 'packages'
$versionPath = Join-Path $root 'eng/ListenSphere.Version.props'
$issPath = Join-Path $root 'packaging/windows/ListenSphere.iss'
$iconPath = Join-Path $root 'assets/branding/windows/ListenSphere.ico'
$controllerPublish = Join-Path $artifactRoot 'publish/Controller'
$senderPublish = Join-Path $artifactRoot 'publish/Sender'

if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
    throw "Version source is missing: $versionPath"
}
if (-not (Test-Path -LiteralPath $issPath -PathType Leaf)) {
    throw "Inno Setup script is missing: $issPath"
}
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "Approved Windows icon is missing: $iconPath"
}
$iconHeader = [IO.File]::ReadAllBytes($iconPath)
if ($iconHeader.Length -lt 6 -or $iconHeader[0] -ne 0 -or $iconHeader[1] -ne 0 -or
    $iconHeader[2] -ne 1 -or $iconHeader[3] -ne 0) {
    throw "Approved Windows icon is not a valid ICO file: $iconPath"
}

[xml] $versionDocument = Get-Content -LiteralPath $versionPath -Raw
$productVersion = Get-UniqueXmlValue $versionDocument 'ListenSphereProductVersion' $versionPath
$numericVersion = Get-UniqueXmlValue $versionDocument 'FileVersion' $versionPath
$versionPrefix = Get-UniqueXmlValue $versionDocument 'ListenSphereVersionPrefix' $versionPath
if ($productVersion.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "Product version contains invalid filename characters: $productVersion"
}
if ($numericVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "FileVersion must contain four numeric components: $numericVersion"
}
if ($versionPrefix -notmatch '^\d+\.\d+\.\d+$') {
    throw "ListenSphereVersionPrefix must contain three numeric components: $versionPrefix"
}

Assert-PublishedApplication $controllerPublish 'ListenSphere.Controller.exe' $productVersion $numericVersion
Assert-PublishedApplication $senderPublish 'ListenSphere.Sender.exe' $productVersion $numericVersion
$resolvedIscc = Resolve-IsccPath $IsccPath

Assert-SafeArtifactPath $packageRoot $artifactRoot
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
foreach ($oldInstaller in Get-ChildItem -LiteralPath $packageRoot -Filter 'ListenSphere-Setup-*-win-x64.exe' -File) {
    Assert-SafeArtifactPath $oldInstaller.FullName $artifactRoot
    Remove-Item -LiteralPath $oldInstaller.FullName -Force
}

$installerBaseName = "ListenSphere-Setup-$productVersion-win-x64"
$installerPath = Join-Path $packageRoot "$installerBaseName.exe"
$arguments = @(
    '/Qp',
    "/DProductVersion=$productVersion",
    "/DNumericVersion=$numericVersion",
    "/DSourceRoot=$root",
    "/DOutputDirectory=$packageRoot",
    "/DInstallerBaseName=$installerBaseName",
    $issPath
)
Write-Host "ISCC: $resolvedIscc"
Write-Host "ProductVersion: $productVersion"
Write-Host "VersionPrefix: $versionPrefix"
Write-Host "NumericVersion: $numericVersion"
& $resolvedIscc @arguments
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer output is missing: $installerPath"
}

$versionInfo = (Get-Item -LiteralPath $installerPath).VersionInfo
$expectedMetadata = [ordered]@{
    FileDescription = '聆界 ListenSphere Installer'
    ProductName = '聆界 ListenSphere'
    ProductVersion = $productVersion
    FileVersion = $numericVersion
    OriginalFilename = "$installerBaseName.exe"
}
foreach ($entry in $expectedMetadata.GetEnumerator()) {
    $actualValue = $versionInfo.($entry.Key).Trim()
    if ($actualValue -ne $entry.Value) {
        throw "Installer $($entry.Key) is '$actualValue'; expected '$($entry.Value)'."
    }
}

$packageNames = @(
    "ListenSphere-Controller-win-x64-$productVersion.zip",
    "ListenSphere-Sender-win-x64-$productVersion.zip",
    "$installerBaseName.exe"
)
$checksumLines = foreach ($name in $packageNames) {
    $path = Join-Path $packageRoot $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
}
$checksumPath = Join-Path $packageRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

$installer = Get-Item -LiteralPath $installerPath
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Installer: $($installer.FullName)"
Write-Output ("Size: {0:N0} bytes" -f $installer.Length)
Write-Output "SHA256: $installerHash"
Write-Output "Checksums: $checksumPath"
