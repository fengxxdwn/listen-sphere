[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long] $ExpectedPackagingRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-UniqueXmlValue {
    param([xml] $Document, [string] $ElementName, [string] $SourcePath)
    $nodes = @($Document.SelectNodes("//$ElementName"))
    if ($nodes.Count -ne 1) { throw "Expected exactly one $ElementName element in $SourcePath; found $($nodes.Count)." }
    $value = $nodes[0].InnerText.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { throw "$ElementName is empty in $SourcePath." }
    return $value
}

function Resolve-UniqueArtifact {
    param([string] $Root, [string] $Name)
    $matches = @(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object Name -eq $Name)
    if ($matches.Count -ne 1) { throw "Expected exactly one '$Name' below $Root; found $($matches.Count)." }
    if ($matches[0].Length -le 0) { throw "Artifact is empty: $Name" }
    return $matches[0]
}

function Resolve-AndroidSdk {
    param([string] $AndroidProject)
    $candidates = [Collections.Generic.List[string]]::new()
    foreach ($environmentName in @('ANDROID_SDK_ROOT', 'ANDROID_HOME')) {
        $value = [Environment]::GetEnvironmentVariable($environmentName)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $candidates.Add($value) }
    }
    $localProperties = Join-Path $AndroidProject 'local.properties'
    if (Test-Path -LiteralPath $localProperties -PathType Leaf) {
        $sdkLine = Get-Content -LiteralPath $localProperties | Where-Object { $_ -match '^\s*sdk\.dir\s*=' } | Select-Object -First 1
        if ($null -ne $sdkLine) {
            $sdkValue = ($sdkLine -split '=', 2)[1].Trim().Replace('\:', ':').Replace('\\', '\')
            if (-not [string]::IsNullOrWhiteSpace($sdkValue)) { $candidates.Add($sdkValue) }
        }
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        $resolved = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($candidate))
        if (Test-Path -LiteralPath $resolved -PathType Container) { return $resolved }
    }
    throw 'Android SDK was not found. Set ANDROID_SDK_ROOT or ANDROID_HOME, or configure sdk.dir in local.properties.'
}

function Assert-PortableArchive {
    param([string] $ArchivePath, [string] $ExecutableName, [string] $Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $files = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        $rootNames = @($files | Where-Object { $_.FullName -eq $_.Name } | ForEach-Object Name)
        foreach ($required in @($ExecutableName, 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll')) {
            if ($rootNames -notcontains $required) { throw "Portable archive '$ArchivePath' is missing root entry '$required'." }
        }
        if (@($rootNames | Where-Object { $_ -like '*.runtimeconfig.json' }).Count -eq 0) {
            throw "Portable archive '$ArchivePath' has no root runtimeconfig.json."
        }
    }
    finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $Destination)
}

function Assert-ExecutableMetadata {
    param([string] $Path, [string] $ExpectedProductVersion, [string] $ExpectedFileVersion)
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($metadata.ProductVersion -ne $ExpectedProductVersion) { throw "ProductVersion for '$Path' is '$($metadata.ProductVersion)'; expected '$ExpectedProductVersion'." }
    if ($metadata.FileVersion -ne $ExpectedFileVersion) { throw "FileVersion for '$Path' is '$($metadata.FileVersion)'; expected '$ExpectedFileVersion'." }
    Add-Type -AssemblyName System.Drawing
    $icon = [Drawing.Icon]::ExtractAssociatedIcon($Path)
    if ($null -eq $icon -or $icon.Width -lt 16 -or $icon.Height -lt 16) { throw "Executable icon is missing or invalid: $Path" }
    $icon.Dispose()
}

function Assert-InstallerMetadata {
    param([string] $Path, [string] $ExpectedProductVersion, [string] $ExpectedFileVersion, [string] $ExpectedOriginalFilename)
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $expected = [ordered]@{
        FileDescription = '聆界 ListenSphere Installer'
        ProductName = '聆界 ListenSphere'
        ProductVersion = $ExpectedProductVersion
        FileVersion = $ExpectedFileVersion
        OriginalFilename = $ExpectedOriginalFilename
    }
    foreach ($entry in $expected.GetEnumerator()) {
        $actual = ([string]$metadata.($entry.Key)).Trim()
        if ($actual -ne $entry.Value) { throw "Installer $($entry.Key) is '$actual'; expected '$($entry.Value)'." }
    }
    $values = $expected.Keys | ForEach-Object { ([string]$metadata.$_).Trim() }
    foreach ($placeholder in @('My Program', 'Setup1')) {
        if ($values -match [regex]::Escape($placeholder)) { throw "Installer contains placeholder metadata: $placeholder" }
    }
}

function Get-ApkEvidence {
    param([string] $Aapt2, [string] $ApkPath)
    $badging = @(& $Aapt2 dump badging $ApkPath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "aapt2 failed to read APK metadata: $ApkPath" }
    $text = $badging -join [Environment]::NewLine
    $packageMatch = [regex]::Match($text, "package: name='([^']+)' versionCode='([^']+)' versionName='([^']+)'")
    $minSdkMatch = [regex]::Match($text, "sdkVersion:'([^']+)'")
    $targetSdkMatch = [regex]::Match($text, "targetSdkVersion:'([^']+)'")
    if (-not $packageMatch.Success -or -not $minSdkMatch.Success -or -not $targetSdkMatch.Success) { throw "Required APK metadata is missing: $ApkPath" }
    return [pscustomobject]@{
        PackageName = $packageMatch.Groups[1].Value
        VersionCode = $packageMatch.Groups[2].Value
        VersionName = $packageMatch.Groups[3].Value
        MinSdk = $minSdkMatch.Groups[1].Value
        TargetSdk = $targetSdkMatch.Groups[1].Value
        Debuggable = $text -match 'application-debuggable'
    }
}

function Assert-ApkMetadata {
    param($Metadata, [string] $ExpectedVersion, [int] $ExpectedVersionCode, [bool] $ExpectedDebuggable)
    $expected = [ordered]@{
        PackageName = 'io.listensphere.mobile'
        VersionName = $ExpectedVersion
        VersionCode = $ExpectedVersionCode.ToString([Globalization.CultureInfo]::InvariantCulture)
        MinSdk = '29'
        TargetSdk = '34'
        Debuggable = $ExpectedDebuggable
    }
    foreach ($entry in $expected.GetEnumerator()) {
        if ($Metadata.($entry.Key) -ne $entry.Value) { throw "APK $($entry.Key) is '$($Metadata.($entry.Key))'; expected '$($entry.Value)'." }
    }
}

function Assert-ApkContents {
    param([string] $ApkPath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ApkPath)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        foreach ($required in @('AndroidManifest.xml', 'resources.arsc')) {
            if ($entries -notcontains $required) { throw "APK entry is missing: $required" }
        }
        if (-not ($entries | Where-Object { $_ -match '^classes\d*\.dex$' })) { throw "APK contains no classes.dex payload: $ApkPath" }
        $sensitive = @($entries | Where-Object {
            $_ -match '(?i)(^|/)(keystore\.properties|key\.properties|.*\.(jks|keystore|pfx|p12|pem|key))$' -or $_ -match '(?i)private[-_ ]?key'
        })
        if ($sensitive.Count -ne 0) { throw "APK contains prohibited signing material: $($sensitive -join ', ')" }
    }
    finally { $archive.Dispose() }
}

function Test-ApkSignature {
    param([string] $ApkSigner, [string] $ApkPath)
    $output = @(& $ApkSigner verify --verbose --print-certs $ApkPath 2>&1)
    $exitCode = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    return [pscustomobject]@{ IsValid = $exitCode -eq 0; Output = $output }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$versionPath = Join-Path $repositoryRoot 'eng/ListenSphere.Version.props'
[xml] $versionDocument = Get-Content -LiteralPath $versionPath -Raw
$version = Get-UniqueXmlValue $versionDocument 'ListenSphereProductVersion' $versionPath
$fileVersion = Get-UniqueXmlValue $versionDocument 'FileVersion' $versionPath
$versionCodeText = Get-UniqueXmlValue $versionDocument 'ListenSphereAndroidVersionCode' $versionPath
$versionCode = 0
if (-not [int]::TryParse($versionCodeText, [ref] $versionCode) -or $versionCode -le 0) { throw "ListenSphereAndroidVersionCode must be a positive integer: $versionCodeText" }

$expectedNames = @(
    "ListenSphere-Controller-win-x64-$version.zip",
    "ListenSphere-Sender-win-x64-$version.zip",
    "ListenSphere-Setup-$version-win-x64.exe",
    "ListenSphere-Mobile-debug-$version.apk",
    "ListenSphere-Mobile-release-unsigned-$version.apk"
)
$artifacts = @{}
foreach ($name in $expectedNames) { $artifacts[$name] = Resolve-UniqueArtifact $artifactRoot $name }
$allPackages = @(Get-ChildItem -LiteralPath $artifactRoot -File -Recurse | Where-Object { $_.Name -like 'ListenSphere-*' -and $_.Extension -in @('.zip', '.exe', '.apk') })
$unexpected = @($allPackages | Where-Object Name -notin $expectedNames)
if ($unexpected.Count -ne 0) { throw "Unexpected or stale ListenSphere packages exist: $($unexpected.Name -join ', ')" }
if ($allPackages.Count -ne $expectedNames.Count) { throw "Expected exactly $($expectedNames.Count) candidate packages; found $($allPackages.Count)." }

$checksum = Resolve-UniqueArtifact $artifactRoot 'SHA256SUMS.txt'
$manifest = @{}
foreach ($line in Get-Content -LiteralPath $checksum.FullName) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([0-9A-Fa-f]{64})  ([^\\/]+)$') { throw "Invalid SHA256SUMS entry: $line" }
    if ($Matches[2] -eq 'SHA256SUMS.txt' -or $manifest.ContainsKey($Matches[2])) { throw "Invalid or duplicate SHA256SUMS filename: $($Matches[2])" }
    $manifest[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
if ($manifest.Count -ne $expectedNames.Count) { throw "SHA256SUMS.txt must contain exactly $($expectedNames.Count) entries; found $($manifest.Count)." }
foreach ($name in $expectedNames) {
    if (-not $manifest.ContainsKey($name)) { throw "SHA256SUMS.txt is missing: $name" }
    $actual = (Get-FileHash -LiteralPath $artifacts[$name].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $manifest[$name]) { throw "SHA256 mismatch: $name" }
}

$androidSdk = Resolve-AndroidSdk (Join-Path $repositoryRoot 'apps/ListenSphere.Mobile')
$buildTools = Join-Path $androidSdk 'build-tools/34.0.0'
$aapt2 = Join-Path $buildTools 'aapt2.exe'
$apkSigner = Join-Path $buildTools 'apksigner.bat'
foreach ($tool in @($aapt2, $apkSigner)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Install Android SDK Build Tools 34.0.0. Missing: $tool" }
}

$temporaryRoot = Join-Path $artifactRoot (".validation-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $controllerRoot = Join-Path $temporaryRoot 'Controller'
    $senderRoot = Join-Path $temporaryRoot 'Sender'
    Assert-PortableArchive $artifacts[$expectedNames[0]].FullName 'ListenSphere.Controller.exe' $controllerRoot
    Assert-PortableArchive $artifacts[$expectedNames[1]].FullName 'ListenSphere.Sender.exe' $senderRoot
    Assert-ExecutableMetadata (Join-Path $controllerRoot 'ListenSphere.Controller.exe') $version $fileVersion
    Assert-ExecutableMetadata (Join-Path $senderRoot 'ListenSphere.Sender.exe') $version $fileVersion
    Assert-InstallerMetadata $artifacts[$expectedNames[2]].FullName $version $fileVersion $expectedNames[2]

    $debugApk = $artifacts[$expectedNames[3]].FullName
    $unsignedApk = $artifacts[$expectedNames[4]].FullName
    Assert-ApkContents $debugApk
    Assert-ApkContents $unsignedApk
    Assert-ApkMetadata (Get-ApkEvidence $aapt2 $debugApk) $version $versionCode $true
    Assert-ApkMetadata (Get-ApkEvidence $aapt2 $unsignedApk) $version $versionCode $false
    if (-not (Test-ApkSignature $apkSigner $debugApk).IsValid) { throw 'Debug APK signature verification failed.' }
    if ((Test-ApkSignature $apkSigner $unsignedApk).IsValid) { throw 'Unsigned Release APK unexpectedly passed signature verification.' }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}

Write-Output 'AUTOMATED RESULT: PASS'
Write-Output "Product version: $version"
Write-Output "Android versionCode: $versionCode"
Write-Output "Expected source commit: $($ExpectedCommit.ToLowerInvariant())"
Write-Output "Expected packaging run: $ExpectedPackagingRun"
Write-Output "Candidate packages: $($expectedNames.Count)"
foreach ($name in $expectedNames) { Write-Output "$($manifest[$name])  $name" }
Write-Output 'Windows portable structure and metadata: PASS'
Write-Output 'Installer metadata: PASS'
Write-Output 'Android Debug metadata/signature: PASS'
Write-Output 'Android unsigned Release metadata/expected unsigned state: PASS'
Write-Output 'MANUAL RESULT: NOT TESTED BY THIS SCRIPT'
