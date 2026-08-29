[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'All')]
    [string] $Configuration = 'All',

    [string] $JavaHome = 'C:\Program Files\Java\jdk-17',

    [string] $AndroidSdk = "$env:LOCALAPPDATA\Android\Sdk",

    [string] $GradleUserHome = $env:GRADLE_USER_HOME,

    [string] $GradleExecutable
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$mappedDrive = $null
$workingRoot = $repositoryRoot
$gradleExitCode = 0

if ($repositoryRoot -match '[^\x00-\x7F]') {
    $mappedDrive = 'L'
    if (Get-PSDrive -Name $mappedDrive -ErrorAction SilentlyContinue) {
        throw "临时盘符 ${mappedDrive}: 已被占用。请释放该盘符后重试。"
    }

    cmd.exe /c "subst ${mappedDrive}: `"$repositoryRoot`""
    if ($LASTEXITCODE -ne 0) {
        throw '无法创建 Android 测试所需的临时英文路径。'
    }

    $workingRoot = "${mappedDrive}:\"
}

try {
    if (-not (Test-Path -LiteralPath (Join-Path $JavaHome 'bin\java.exe'))) {
        throw "未找到 JDK 17：$JavaHome"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $AndroidSdk 'platforms'))) {
        throw "未找到 Android SDK：$AndroidSdk"
    }

    $env:JAVA_HOME = $JavaHome
    $env:Path = "$JavaHome\bin;$env:Path"
    $env:ANDROID_HOME = $AndroidSdk
    $env:ANDROID_SDK_ROOT = $AndroidSdk
    if (-not [string]::IsNullOrWhiteSpace($GradleUserHome)) {
        $env:GRADLE_USER_HOME = $GradleUserHome
    }

    Set-Location (Join-Path $workingRoot 'apps\ListenSphere.Mobile')
    $task = switch ($Configuration) {
        'Debug' { 'testDebugUnitTest' }
        'Release' { 'testReleaseUnitTest' }
        default { 'test' }
    }
    $runner = if ([string]::IsNullOrWhiteSpace($GradleExecutable)) {
        '.\gradlew.bat'
    } else {
        if (-not (Test-Path -LiteralPath $GradleExecutable)) {
            throw "未找到 Gradle：$GradleExecutable"
        }
        (Resolve-Path -LiteralPath $GradleExecutable).Path
    }
    & $runner $task '--no-daemon'
    $gradleExitCode = $LASTEXITCODE
}
finally {
    Set-Location $repositoryRoot
    if ($null -ne $mappedDrive) {
        cmd.exe /c "subst ${mappedDrive}: /d"
    }
}

if ($gradleExitCode -ne 0) {
    exit $gradleExitCode
}
