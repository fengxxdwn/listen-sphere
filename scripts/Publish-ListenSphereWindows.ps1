[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Runtime = 'win-x64',

    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Assert-UniqueXmlValue {
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
        throw "Refusing to clean a path outside artifacts: $resolvedPath"
    }
}

function Assert-PublishedApplication {
    param(
        [Parameter(Mandatory)][string] $PublishDirectory,
        [Parameter(Mandatory)][string] $ExecutableName,
        [Parameter(Mandatory)][string] $ExpectedDescription,
        [Parameter(Mandatory)][string] $ExpectedProductVersion,
        [Parameter(Mandatory)][string] $ExpectedFileVersion
    )

    $executable = Join-Path $PublishDirectory $ExecutableName
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable is missing: $executable"
    }

    $versionInfo = (Get-Item -LiteralPath $executable).VersionInfo
    $metadata = @{
        FileDescription = @($versionInfo.FileDescription, $ExpectedDescription)
        ProductVersion = @($versionInfo.ProductVersion, $ExpectedProductVersion)
        FileVersion = @($versionInfo.FileVersion, $ExpectedFileVersion)
    }
    foreach ($entry in $metadata.GetEnumerator()) {
        if ($entry.Value[0] -ne $entry.Value[1]) {
            throw "$ExecutableName $($entry.Key) is '$($entry.Value[0])'; expected '$($entry.Value[1])'."
        }
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
            throw "Self-contained runtime file is missing: $runtimePath"
        }
    }

    return $executable
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'ListenSphere.sln'
$versionPath = Join-Path $root 'eng/ListenSphere.Version.props'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
    throw "Version source is missing: $versionPath"
}

[xml] $versionDocument = Get-Content -LiteralPath $versionPath -Raw
$version = Assert-UniqueXmlValue $versionDocument 'ListenSphereProductVersion' $versionPath
$fileVersion = Assert-UniqueXmlValue $versionDocument 'FileVersion' $versionPath
if ($version.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "Product version contains characters that are invalid in a package name: $version"
}

$artifactRoot = Join-Path $root 'artifacts'
$publishRoot = Join-Path $artifactRoot 'publish'
$packageRoot = Join-Path $artifactRoot 'packages'
$controllerPublish = Join-Path $publishRoot 'Controller'
$senderPublish = Join-Path $publishRoot 'Sender'
foreach ($outputDirectory in @($publishRoot, $packageRoot)) {
    Assert-SafeArtifactPath $outputDirectory $artifactRoot
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Push-Location $root
try {
    Invoke-DotNet -Arguments @('restore', $solution)
    Invoke-DotNet -Arguments @(
        'build', $solution,
        '-c', $Configuration,
        '--no-restore',
        '-m:1',
        '-warnaserror'
    )
    if (-not $SkipTests) {
        Invoke-DotNet -Arguments @(
            'test', $solution,
            '-c', $Configuration,
            '--no-build',
            '--no-restore'
        )
    }

    $publishOptions = @(
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', 'true',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false'
    )
    Invoke-DotNet -Arguments (@(
        'publish', 'apps/ListenSphere.Controller/ListenSphere.Controller.csproj'
    ) + $publishOptions + @('-o', $controllerPublish))
    Invoke-DotNet -Arguments (@(
        'publish', 'apps/ListenSphere.Sender/ListenSphere.Sender.csproj'
    ) + $publishOptions + @('-o', $senderPublish))
}
finally {
    Pop-Location
}

$controllerExecutable = Assert-PublishedApplication `
    $controllerPublish `
    'ListenSphere.Controller.exe' `
    '聆界 / ListenSphere Controller' `
    $version `
    $fileVersion
$senderExecutable = Assert-PublishedApplication `
    $senderPublish `
    'ListenSphere.Sender.exe' `
    '聆界发送端 / ListenSphere Sender' `
    $version `
    $fileVersion

Add-Type -AssemblyName System.IO.Compression.FileSystem
$controllerZipName = "ListenSphere-Controller-$Runtime-$version.zip"
$senderZipName = "ListenSphere-Sender-$Runtime-$version.zip"
$controllerZip = Join-Path $packageRoot $controllerZipName
$senderZip = Join-Path $packageRoot $senderZipName
[IO.Compression.ZipFile]::CreateFromDirectory(
    $controllerPublish,
    $controllerZip,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)
[IO.Compression.ZipFile]::CreateFromDirectory(
    $senderPublish,
    $senderZip,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

$checksums = foreach ($package in @($controllerZip, $senderZip)) {
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($package))"
}
$checksumPath = Join-Path $packageRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksumPath, $checksums, [Text.UTF8Encoding]::new($false))

Write-Output "ListenSphere Windows portable packages $version"
Write-Output "Controller executable: $controllerExecutable"
Write-Output "Sender executable: $senderExecutable"
foreach ($path in @($controllerZip, $senderZip, $checksumPath)) {
    $file = Get-Item -LiteralPath $path
    Write-Output ("{0} ({1:N0} bytes)" -f $file.FullName, $file.Length)
}
