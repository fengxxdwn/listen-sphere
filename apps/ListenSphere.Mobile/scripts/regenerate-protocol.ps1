param(
    [string]$ProtocPath
)

$ErrorActionPreference = 'Stop'
$mobileRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Resolve-Path (Join-Path $mobileRoot '..\..')
$protoRoot = Join-Path $repositoryRoot 'protocol\protobuf'
$outputRoot = Join-Path $mobileRoot 'app\src\main\java'

if (-not $ProtocPath) {
    $gradleHome = if ($env:GRADLE_USER_HOME) { $env:GRADLE_USER_HOME } else { Join-Path $env:USERPROFILE '.gradle' }
    $ProtocPath = Get-ChildItem -Path $gradleHome -Filter 'protoc-*.exe' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ProtocPath -or -not (Test-Path -LiteralPath $ProtocPath)) {
    throw 'protoc.exe was not found. Pass -ProtocPath or run one Gradle dependency restore first.'
}

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ("ListenSphereMobileProto-" + [System.Guid]::NewGuid().ToString('N'))
$temporaryInput = Join-Path $temporaryRoot 'input'
$temporaryOutput = Join-Path $temporaryRoot 'output'
try {
    New-Item -ItemType Directory -Path $temporaryInput,$temporaryOutput -Force | Out-Null
    Copy-Item -Path (Join-Path $protoRoot '*') -Destination $temporaryInput -Recurse -Force

    & $ProtocPath "--proto_path=$temporaryInput" "--java_out=lite:$temporaryOutput" `
        (Join-Path $temporaryInput 'listensphere\v1\control.proto')
    if ($LASTEXITCODE -ne 0) {
        throw "protoc exited with code $LASTEXITCODE."
    }

    $generatedPackage = Join-Path $temporaryOutput 'io\listensphere\protocol\v1'
    $destinationPackage = Join-Path $outputRoot 'io\listensphere\protocol\v1'
    New-Item -ItemType Directory -Path $destinationPackage -Force | Out-Null
    Copy-Item -Path (Join-Path $generatedPackage '*.java') -Destination $destinationPackage -Force
    Write-Output "Generated ListenSphere Protocol Java Lite sources in $destinationPackage"
}
finally {
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTemporaryRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporaryRoot).StartsWith('ListenSphereMobileProto-')) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
