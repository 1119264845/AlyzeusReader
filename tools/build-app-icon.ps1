[CmdletBinding()]
param(
    [string]$InputPng,
    [string]$OutputPng,
    [string]$OutputIco
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InputPng)) {
    $InputPng = Join-Path $projectRoot 'assets\app-icon-generated.png'
}
if ([string]::IsNullOrWhiteSpace($OutputPng)) {
    $OutputPng = Join-Path $projectRoot 'assets\app-icon.png'
}
if ([string]::IsNullOrWhiteSpace($OutputIco)) {
    $OutputIco = Join-Path $projectRoot 'assets\app-icon.ico'
}

Add-Type -AssemblyName System.Drawing

function Test-IconPixel {
    param([System.Drawing.Color]$Color)

    $brightness = ($Color.R + $Color.G + $Color.B) / 3.0
    return $brightness -lt 210
}

function New-ResizedPngBytes {
    param(
        [System.Drawing.Bitmap]$Source,
        [int]$Size
    )

    $result = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($result)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage($Source, (New-Object System.Drawing.Rectangle 0, 0, $Size, $Size))
    }
    finally {
        $graphics.Dispose()
    }

    $stream = New-Object System.IO.MemoryStream
    try {
        $result.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $result.Dispose()
    }
}

$source = [System.Drawing.Bitmap]::FromFile($InputPng)
try {
    $centerX = [Math]::Floor($source.Width / 2)
    $centerY = [Math]::Floor($source.Height / 2)

    $left = 0
    while ($left -lt $source.Width -and -not (Test-IconPixel $source.GetPixel($left, $centerY))) { $left++ }
    $right = $source.Width - 1
    while ($right -ge 0 -and -not (Test-IconPixel $source.GetPixel($right, $centerY))) { $right-- }
    $top = 0
    while ($top -lt $source.Height -and -not (Test-IconPixel $source.GetPixel($centerX, $top))) { $top++ }
    $bottom = $source.Height - 1
    while ($bottom -ge 0 -and -not (Test-IconPixel $source.GetPixel($centerX, $bottom))) { $bottom-- }

    if ($left -ge $right -or $top -ge $bottom) {
        throw '无法识别图标主体边界。'
    }

    $detectedWidth = $right - $left + 1
    $detectedHeight = $bottom - $top + 1
    $cropSize = [Math]::Min($detectedWidth, $detectedHeight)
    $cropX = $left + [Math]::Floor(($detectedWidth - $cropSize) / 2)
    $cropY = $top + [Math]::Floor(($detectedHeight - $cropSize) / 2)

    $masterSize = 1024
    $baseBitmap = New-Object System.Drawing.Bitmap $masterSize, $masterSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $baseGraphics = [System.Drawing.Graphics]::FromImage($baseBitmap)
    try {
        $baseGraphics.Clear([System.Drawing.Color]::Transparent)
        $baseGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $baseGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $baseGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $baseGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $sourceRect = New-Object System.Drawing.Rectangle $cropX, $cropY, $cropSize, $cropSize
        $targetRect = New-Object System.Drawing.Rectangle 0, 0, $masterSize, $masterSize
        $baseGraphics.DrawImage($source, $targetRect, $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $baseGraphics.Dispose()
    }

    $finalBitmap = New-Object System.Drawing.Bitmap $masterSize, $masterSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $finalGraphics = [System.Drawing.Graphics]::FromImage($finalBitmap)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $texture = New-Object System.Drawing.TextureBrush $baseBitmap, ([System.Drawing.Drawing2D.WrapMode]::Clamp)
    try {
        $finalGraphics.Clear([System.Drawing.Color]::Transparent)
        $finalGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $finalGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $finalGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $finalGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

        $radius = [Math]::Round($masterSize * 0.17)
        $diameter = $radius * 2
        $edge = $masterSize - 1
        $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
        $path.AddArc($edge - $diameter, 0, $diameter, $diameter, 270, 90)
        $path.AddArc($edge - $diameter, $edge - $diameter, $diameter, $diameter, 0, 90)
        $path.AddArc(0, $edge - $diameter, $diameter, $diameter, 90, 90)
        $path.CloseFigure()
        $finalGraphics.FillPath($texture, $path)
    }
    finally {
        $texture.Dispose()
        $path.Dispose()
        $finalGraphics.Dispose()
        $baseBitmap.Dispose()
    }

    try {
        $outputPngDirectory = Split-Path -Parent $OutputPng
        $outputIcoDirectory = Split-Path -Parent $OutputIco
        [System.IO.Directory]::CreateDirectory($outputPngDirectory) | Out-Null
        [System.IO.Directory]::CreateDirectory($outputIcoDirectory) | Out-Null
        $finalBitmap.Save($OutputPng, [System.Drawing.Imaging.ImageFormat]::Png)

        $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
        $pngEntries = @()
        foreach ($size in $sizes) {
            $pngEntries += [PSCustomObject]@{
                Size = $size
                Bytes = New-ResizedPngBytes -Source $finalBitmap -Size $size
            }
        }

        $fileStream = New-Object System.IO.FileStream $OutputIco, ([System.IO.FileMode]::Create), ([System.IO.FileAccess]::Write)
        $writer = New-Object System.IO.BinaryWriter $fileStream
        try {
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$pngEntries.Count)

            $offset = 6 + (16 * $pngEntries.Count)
            foreach ($entry in $pngEntries) {
                if ($entry.Size -eq 256) {
                    $dimension = [byte]0
                }
                else {
                    $dimension = [byte]$entry.Size
                }
                $writer.Write($dimension)
                $writer.Write($dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$entry.Bytes.Length)
                $writer.Write([UInt32]$offset)
                $offset += $entry.Bytes.Length
            }
            foreach ($entry in $pngEntries) {
                $writer.Write([byte[]]$entry.Bytes)
            }
        }
        finally {
            $writer.Dispose()
            $fileStream.Dispose()
        }
    }
    finally {
        $finalBitmap.Dispose()
    }

    Write-Host "图标已生成: $OutputPng"
    Write-Host "ICO 已生成: $OutputIco"
}
finally {
    $source.Dispose()
}
