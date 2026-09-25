[CmdletBinding()]
param(
    [string] $SourcePath = 'assets/branding/source/listensphere-icon.png'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RenderedPng {
    param(
        [Parameter(Mandatory)][System.Drawing.Image] $Source,
        [Parameter(Mandatory)][int] $Size,
        [double] $Scale = 1.0,
        [switch] $Circular
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            if ($Circular) {
                $clip = [System.Drawing.Drawing2D.GraphicsPath]::new()
                try {
                    $clip.AddEllipse(0, 0, $Size, $Size)
                    $graphics.SetClip($clip)
                }
                finally {
                    $clip.Dispose()
                }
            }

            $renderSize = [int][Math]::Round($Size * $Scale)
            $offset = [int][Math]::Round(($Size - $renderSize) / 2.0)
            $graphics.DrawImage($Source, $offset, $offset, $renderSize, $renderSize)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Write-Png {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][byte[]] $Bytes
    )

    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$resolvedSource = [IO.Path]::GetFullPath((Join-Path $root $SourcePath))
if (-not (Test-Path -LiteralPath $resolvedSource -PathType Leaf)) {
    throw "Brand source image is missing: $resolvedSource"
}

$source = [System.Drawing.Bitmap]::FromFile($resolvedSource)
try {
    if ($source.Width -ne $source.Height -or $source.Width -lt 1024) {
        throw "Brand source must be square and at least 1024 px; found $($source.Width)x$($source.Height)."
    }

    $source1024 = Join-Path $root 'assets/branding/source/listensphere-icon-1024.png'
    Write-Png $source1024 (New-RenderedPng $source 1024)

    $windowsDirectory = Join-Path $root 'assets/branding/windows'
    New-Item -ItemType Directory -Force -Path $windowsDirectory | Out-Null
    $icoPath = Join-Path $windowsDirectory 'ListenSphere.ico'
    $frames = foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        [pscustomobject]@{
            Size = $size
            Bytes = [byte[]](New-RenderedPng $source $size)
        }
    }
    $icoStream = [IO.File]::Create($icoPath)
    try {
        $writer = [IO.BinaryWriter]::new($icoStream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$frames.Count)
            $offset = 6 + (16 * $frames.Count)
            foreach ($frame in $frames) {
                $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$frame.Bytes.Length)
                $writer.Write([uint32]$offset)
                $offset += $frame.Bytes.Length
            }
            foreach ($frame in $frames) {
                $writer.Write($frame.Bytes)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $icoStream.Dispose()
    }

    $androidBrandDirectory = Join-Path $root 'assets/branding/android'
    $adaptiveForeground = New-RenderedPng $source 432 0.6666667
    Write-Png (Join-Path $androidBrandDirectory 'icon-foreground.png') $adaptiveForeground

    $background = [System.Drawing.Bitmap]::new(432, 432)
    try {
        $backgroundGraphics = [System.Drawing.Graphics]::FromImage($background)
        try {
            $backgroundGraphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#05112B'))
        }
        finally {
            $backgroundGraphics.Dispose()
        }
        $backgroundStream = [IO.MemoryStream]::new()
        try {
            $background.Save($backgroundStream, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Png (Join-Path $androidBrandDirectory 'icon-background.png') $backgroundStream.ToArray()
        }
        finally {
            $backgroundStream.Dispose()
        }
    }
    finally {
        $background.Dispose()
    }

    $androidResources = Join-Path $root 'apps/ListenSphere.Mobile/app/src/main/res'
    Write-Png (
        Join-Path $androidResources 'drawable-nodpi/ic_launcher_adaptive_foreground.png'
    ) $adaptiveForeground
    $densitySizes = [ordered]@{
        'mipmap-mdpi' = 48
        'mipmap-hdpi' = 72
        'mipmap-xhdpi' = 96
        'mipmap-xxhdpi' = 144
        'mipmap-xxxhdpi' = 192
    }
    foreach ($entry in $densitySizes.GetEnumerator()) {
        $directory = Join-Path $androidResources $entry.Key
        Write-Png (Join-Path $directory 'ic_launcher.png') (
            New-RenderedPng $source $entry.Value)
        Write-Png (Join-Path $directory 'ic_launcher_round.png') (
            New-RenderedPng $source $entry.Value 1.0 -Circular)
    }

    Write-Output "Generated Windows and Android icons from $resolvedSource"
    Write-Output $icoPath
}
finally {
    $source.Dispose()
}
