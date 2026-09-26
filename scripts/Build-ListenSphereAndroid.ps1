[CmdletBinding()]
param(
    [switch] $SkipTests,
    [switch] $RequireSigned,
    [switch] $Offline
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

function Resolve-AndroidSdk {
    param([Parameter(Mandatory)][string] $AndroidProject)

    $candidates = [Collections.Generic.List[string]]::new()
    foreach ($environmentName in @('ANDROID_SDK_ROOT', 'ANDROID_HOME')) {
        $value = [Environment]::GetEnvironmentVariable($environmentName)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $candidates.Add($value)
        }
    }

    $localProperties = Join-Path $AndroidProject 'local.properties'
    if (Test-Path -LiteralPath $localProperties -PathType Leaf) {
        $sdkLine = Get-Content -LiteralPath $localProperties |
            Where-Object { $_ -match '^\s*sdk\.dir\s*=' } |
            Select-Object -First 1
        if ($null -ne $sdkLine) {
            $sdkValue = ($sdkLine -split '=', 2)[1].Trim()
            $sdkValue = $sdkValue.Replace('\:', ':').Replace('\\', '\')
            if (-not [string]::IsNullOrWhiteSpace($sdkValue)) {
                $candidates.Add($sdkValue)
            }
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $resolved = [IO.Path]::GetFullPath(
            [Environment]::ExpandEnvironmentVariables($candidate))
        if (Test-Path -LiteralPath $resolved -PathType Container) {
            return $resolved
        }
    }
    throw 'Android SDK was not found. Set ANDROID_SDK_ROOT or ANDROID_HOME, or configure sdk.dir in local.properties.'
}

function Assert-SafePackagePath {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $PackageRoot
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedPackageRoot = [IO.Path]::GetFullPath($PackageRoot)
    $prefix = $resolvedPackageRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the Android package output: $resolvedPath"
    }
}

function Invoke-Gradle {
    param(
        [Parameter(Mandatory)][string] $Wrapper,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    Write-Host "Gradle tasks: $($Arguments -join ' ')"
    & $Wrapper @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Gradle failed with exit code $LASTEXITCODE."
    }
}

function Get-ApkMetadata {
    param(
        [Parameter(Mandatory)][string] $Aapt2,
        [Parameter(Mandatory)][string] $ApkPath
    )

    $badging = @(& $Aapt2 dump badging $ApkPath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "aapt2 failed to read APK metadata: $ApkPath"
    }
    $text = $badging -join [Environment]::NewLine
    $packageMatch = [regex]::Match(
        $text,
        "package: name='([^']+)' versionCode='([^']+)' versionName='([^']+)'")
    $minSdkMatch = [regex]::Match($text, "sdkVersion:'([^']+)'")
    $targetSdkMatch = [regex]::Match($text, "targetSdkVersion:'([^']+)'")
    if (-not $packageMatch.Success -or -not $minSdkMatch.Success -or
        -not $targetSdkMatch.Success) {
        throw "Required APK metadata is missing: $ApkPath"
    }
    if ($text -notmatch "application-icon-\d+:'res/[^']+'") {
        throw "APK does not expose an application launcher icon: $ApkPath"
    }
    $resources = @(& $Aapt2 dump resources $ApkPath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "aapt2 failed to read APK resources: $ApkPath"
    }
    $resourceText = $resources -join [Environment]::NewLine
    foreach ($resourceName in @(
        'mipmap/ic_launcher',
        'mipmap/ic_launcher_round',
        'drawable/ic_launcher_adaptive_foreground',
        'color/ic_launcher_background'
    )) {
        if ($resourceText -notmatch [regex]::Escape($resourceName)) {
            throw "APK launcher resource is missing: $resourceName"
        }
    }

    return [pscustomobject]@{
        PackageName = $packageMatch.Groups[1].Value
        VersionCode = $packageMatch.Groups[2].Value
        VersionName = $packageMatch.Groups[3].Value
        MinSdk = $minSdkMatch.Groups[1].Value
        TargetSdk = $targetSdkMatch.Groups[1].Value
    }
}

function Assert-ApkMetadata {
    param(
        [Parameter(Mandatory)] $Metadata,
        [Parameter(Mandatory)][string] $ExpectedVersionName,
        [Parameter(Mandatory)][int] $ExpectedVersionCode
    )

    $expected = [ordered]@{
        PackageName = 'io.listensphere.mobile'
        VersionName = $ExpectedVersionName
        VersionCode = $ExpectedVersionCode.ToString([Globalization.CultureInfo]::InvariantCulture)
        MinSdk = '29'
        TargetSdk = '34'
    }
    foreach ($entry in $expected.GetEnumerator()) {
        if ($Metadata.($entry.Key) -ne $entry.Value) {
            throw "APK $($entry.Key) is '$($Metadata.($entry.Key))'; expected '$($entry.Value)'."
        }
    }
}

function Assert-ApkContents {
    param([Parameter(Mandatory)][string] $ApkPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ApkPath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($required in @('AndroidManifest.xml', 'resources.arsc')) {
            if ($entries -notcontains $required) {
                throw "APK entry is missing: $required"
            }
        }
        if (-not ($entries | Where-Object { $_ -match '^classes\d*\.dex$' })) {
            throw 'APK contains no classes.dex payload.'
        }
        $sensitive = $entries | Where-Object {
            $_ -match '(?i)(^|/)(keystore\.properties|key\.properties|.*\.(jks|keystore|pfx|p12|pem|key))$' -or
            $_ -match '(?i)private[-_ ]?key'
        }
        if ($sensitive) {
            throw "APK contains prohibited signing material: $($sensitive -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-ApkSignature {
    param(
        [Parameter(Mandatory)][string] $ApkSigner,
        [Parameter(Mandatory)][string] $ApkPath,
        [switch] $PrintCertificates
    )

    $arguments = @('verify', '--verbose')
    if ($PrintCertificates) {
        $arguments += '--print-certs'
    }
    $arguments += $ApkPath
    $output = @(& $ApkSigner @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        IsValid = $exitCode -eq 0
        Output = $output
    }
}

function Get-TestSummary {
    param([Parameter(Mandatory)][string] $ResultDirectory)

    $resultFiles = @(
        Get-ChildItem -LiteralPath $ResultDirectory -Filter 'TEST-*.xml' -File -ErrorAction Stop
    )
    $tests = 0
    $failures = 0
    $errors = 0
    $skipped = 0
    foreach ($file in $resultFiles) {
        [xml] $document = Get-Content -LiteralPath $file.FullName
        $tests += [int] $document.testsuite.tests
        $failures += [int] $document.testsuite.failures
        $errors += [int] $document.testsuite.errors
        $skipped += [int] $document.testsuite.skipped
    }
    if ($failures -ne 0 -or $errors -ne 0) {
        throw "Android unit tests failed: failures=$failures errors=$errors"
    }
    return [pscustomobject]@{
        Files = $resultFiles.Count
        Tests = $tests
        Skipped = $skipped
    }
}

$physicalRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$physicalAndroidProject = Join-Path $physicalRoot 'apps/ListenSphere.Mobile'
$versionPath = Join-Path $physicalRoot 'eng/ListenSphere.Version.props'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
    throw "Version source is missing: $versionPath"
}
[xml] $versionDocument = Get-Content -LiteralPath $versionPath -Raw
$versionName = Get-UniqueXmlValue $versionDocument 'ListenSphereProductVersion' $versionPath
$versionCodeText = Get-UniqueXmlValue $versionDocument 'ListenSphereAndroidVersionCode' $versionPath
$versionCode = 0
if (-not [int]::TryParse($versionCodeText, [ref] $versionCode) -or $versionCode -le 0) {
    throw "ListenSphereAndroidVersionCode must be a positive integer: $versionCodeText"
}
if ($versionName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "Product version contains invalid filename characters: $versionName"
}

$java = Get-Command java -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $java) {
    throw 'Java was not found. Install JDK 17 or newer and configure JAVA_HOME/PATH.'
}
$wrapperPhysical = Join-Path $physicalAndroidProject 'gradlew.bat'
if (-not (Test-Path -LiteralPath $wrapperPhysical -PathType Leaf)) {
    throw "Gradle wrapper is missing: $wrapperPhysical"
}

$androidSdk = Resolve-AndroidSdk $physicalAndroidProject
$buildTools = Join-Path $androidSdk 'build-tools/34.0.0'
$aapt2 = Join-Path $buildTools 'aapt2.exe'
$apkSigner = Join-Path $buildTools 'apksigner.bat'
foreach ($tool in @($aapt2, $apkSigner)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "Android SDK Build Tools 34.0.0 are incomplete. Install Android SDK Build Tools 34.0.0. Missing: $tool"
    }
}

$signingNames = @(
    'LISTENSPHERE_ANDROID_KEYSTORE_PATH',
    'LISTENSPHERE_ANDROID_KEYSTORE_PASSWORD',
    'LISTENSPHERE_ANDROID_KEY_ALIAS',
    'LISTENSPHERE_ANDROID_KEY_PASSWORD'
)
$providedSigningNames = @(
    $signingNames | Where-Object {
        -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
    }
)
if ($providedSigningNames.Count -ne 0 -and
    $providedSigningNames.Count -ne $signingNames.Count) {
    throw 'Incomplete ListenSphere Android release signing configuration. Either provide all four signing values or none of them.'
}
$releaseSigned = $providedSigningNames.Count -eq $signingNames.Count
if ($RequireSigned -and -not $releaseSigned) {
    throw 'Signed Android release required, but complete ListenSphere release signing configuration was not provided.'
}
if ($releaseSigned) {
    $keystorePath = [Environment]::GetEnvironmentVariable(
        'LISTENSPHERE_ANDROID_KEYSTORE_PATH')
    if (-not (Test-Path -LiteralPath $keystorePath -PathType Leaf)) {
        throw "ListenSphere Android release keystore does not exist: $keystorePath"
    }
    Write-Output 'Signing mode: release signed'
}
else {
    Write-Output 'Signing mode: unsigned'
}

$packageRootPhysical = Join-Path $physicalRoot 'artifacts/packages'
New-Item -ItemType Directory -Path $packageRootPhysical -Force | Out-Null
foreach ($oldPackage in Get-ChildItem -LiteralPath $packageRootPhysical -Filter 'ListenSphere-Mobile-*.apk' -File) {
    Assert-SafePackagePath $oldPackage.FullName $packageRootPhysical
    Remove-Item -LiteralPath $oldPackage.FullName -Force
}

$executionRoot = $physicalRoot
$substDrive = $null
$runningOnWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if ($runningOnWindows -and $physicalRoot -match '[^\x00-\x7F]') {
    foreach ($driveLetter in @('R', 'Q', 'P', 'O', 'N')) {
        $drive = [string]::Concat($driveLetter, ':')
        if (-not (Test-Path -LiteralPath "$drive\")) {
            & subst.exe $drive $physicalRoot
            if ($LASTEXITCODE -eq 0) {
                $substDrive = $drive
                $executionRoot = "$drive\"
                break
            }
        }
    }
    if ($null -eq $substDrive) {
        throw 'No free drive letter was available for Android SDK tools to access the non-ASCII repository path.'
    }
}

try {
    $androidProject = Join-Path $executionRoot 'apps/ListenSphere.Mobile'
    $wrapper = Join-Path $androidProject 'gradlew.bat'
    $packageRoot = Join-Path $executionRoot 'artifacts/packages'

    Push-Location $androidProject
    try {
        $gradleArguments = [Collections.Generic.List[string]]::new()
        if (-not $SkipTests) {
            $gradleArguments.Add('testDebugUnitTest')
            $gradleArguments.Add('testReleaseUnitTest')
        }
        $gradleArguments.Add('assembleDebug')
        $gradleArguments.Add('assembleRelease')
        $gradleArguments.Add('--no-daemon')
        if ($Offline) {
            $gradleArguments.Add('--offline')
        }
        Invoke-Gradle $wrapper $gradleArguments.ToArray()
    }
    finally {
        Pop-Location
    }

    if (-not $SkipTests) {
        $debugTestResults = Join-Path $androidProject 'app/build/test-results/testDebugUnitTest'
        $debugSummary = Get-TestSummary $debugTestResults
        Write-Output "Test files: $($debugSummary.Files)"
        Write-Output "Android unit tests: $($debugSummary.Tests)/$($debugSummary.Tests)"
        $releaseResultDirectory = Join-Path $androidProject 'app/build/test-results/testReleaseUnitTest'
        if (Test-Path -LiteralPath $releaseResultDirectory) {
            $releaseSummary = Get-TestSummary $releaseResultDirectory
            Write-Output "Android release unit tests: $($releaseSummary.Tests)/$($releaseSummary.Tests)"
        }
    }

    $debugApk = Join-Path $androidProject 'app/build/outputs/apk/debug/app-debug.apk'
    $releaseApk = if ($releaseSigned) {
        Join-Path $androidProject 'app/build/outputs/apk/release/app-release.apk'
    }
    else {
        Join-Path $androidProject 'app/build/outputs/apk/release/app-release-unsigned.apk'
    }
    foreach ($apk in @($debugApk, $releaseApk)) {
        if (-not (Test-Path -LiteralPath $apk -PathType Leaf)) {
            throw "Expected Gradle APK output is missing: $apk"
        }
        Assert-ApkContents $apk
        $metadata = Get-ApkMetadata $aapt2 $apk
        Assert-ApkMetadata $metadata $versionName $versionCode
    }

    $debugSignature = Test-ApkSignature $apkSigner $debugApk
    if (-not $debugSignature.IsValid) {
        throw 'Debug APK signature verification failed.'
    }

    $releaseSignature = Test-ApkSignature $apkSigner $releaseApk -PrintCertificates
    if ($releaseSigned -and -not $releaseSignature.IsValid) {
        throw 'Signed Release APK signature verification failed.'
    }
    if (-not $releaseSigned -and $releaseSignature.IsValid) {
        throw 'Release APK was expected to be unsigned but passed signature verification.'
    }

    $debugName = "ListenSphere-Mobile-debug-$versionName.apk"
    $releaseName = if ($releaseSigned) {
        "ListenSphere-Mobile-$versionName.apk"
    }
    else {
        "ListenSphere-Mobile-release-unsigned-$versionName.apk"
    }
    $debugPackage = Join-Path $packageRoot $debugName
    $releasePackage = Join-Path $packageRoot $releaseName
    Copy-Item -LiteralPath $debugApk -Destination $debugPackage
    Copy-Item -LiteralPath $releaseApk -Destination $releasePackage

    if ($releaseSigned) {
        $releaseSignature.Output | Where-Object {
            $_ -match 'certificate SHA-256 digest'
        } | Write-Output
    }
    else {
        Write-Output 'Release signature: expected unsigned'
    }

    $checksumFiles = @(
        Get-ChildItem -LiteralPath $packageRoot -File |
            Where-Object {
                $_.Name -like "ListenSphere-*$versionName*" -and
                $_.Extension -in @('.apk', '.exe', '.zip')
            } |
            Sort-Object Name
    )
    $checksumLines = foreach ($file in $checksumFiles) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }
    $checksumPath = Join-Path $packageRoot 'SHA256SUMS.txt'
    [IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

    Write-Output "Android package version: $versionName ($versionCode)"
    Write-Output 'Package: io.listensphere.mobile'
    foreach ($path in @($debugPackage, $releasePackage)) {
        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Output ("{0} ({1:N0} bytes)" -f $file.FullName, $file.Length)
        Write-Output "SHA256: $hash"
    }
    Write-Output "Checksums: $checksumPath"
}
finally {
    if ($null -ne $substDrive) {
        & subst.exe $substDrive /D
    }
}
