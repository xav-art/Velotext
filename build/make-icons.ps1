# Velotext: draws the Velotext app and tray icons (a "V" and a text caret on a warm tile) and writes .ico files.
# Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.
#
# Run from any folder: powershell -ExecutionPolicy Bypass -File build\make-icons.ps1
# Writes src\Velotext.App\Assets\velotext.ico, tray.ico, tray-paused.ico and velotext-256.png.

[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function New-IconPng {
    param([int]$Size, [string]$TileColor, [bool]$WithCaret)

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $s = [double]$Size
    $tile = [System.Windows.Media.BrushConverter]::new().ConvertFromString($TileColor)
    $radius = $s * 0.22
    $dc.DrawRoundedRectangle($tile, $null, (New-Object System.Windows.Rect 0, 0, $s, $s), $radius, $radius)

    $pen = New-Object System.Windows.Media.Pen ([System.Windows.Media.Brushes]::White), ($s * 0.13)
    $pen.StartLineCap = 'Round'
    $pen.EndLineCap = 'Round'
    $pen.LineJoin = 'Round'
    $shift = if ($WithCaret) { -0.06 } else { 0 }
    $figure = New-Object System.Windows.Media.PathFigure
    $figure.StartPoint = New-Object System.Windows.Point ($s * (0.27 + $shift)), ($s * 0.30)
    $figure.Segments.Add((New-Object System.Windows.Media.LineSegment (New-Object System.Windows.Point ($s * (0.50 + $shift)), ($s * 0.72)), $true))
    $figure.Segments.Add((New-Object System.Windows.Media.LineSegment (New-Object System.Windows.Point ($s * (0.73 + $shift)), ($s * 0.30)), $true))
    $geometry = New-Object System.Windows.Media.PathGeometry
    $geometry.Figures.Add($figure)
    $dc.DrawGeometry($null, $pen, $geometry)

    if ($WithCaret) {
        $caret = New-Object System.Windows.Media.Pen ([System.Windows.Media.Brushes]::White), ($s * 0.07)
        $caret.StartLineCap = 'Round'
        $caret.EndLineCap = 'Round'
        $dc.DrawLine($caret, (New-Object System.Windows.Point ($s * 0.82), ($s * 0.28)), (New-Object System.Windows.Point ($s * 0.82), ($s * 0.74)))
    }

    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $Size, $Size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

function Write-Ico {
    param([string]$Path, [int[]]$Sizes, [string]$TileColor)

    $images = foreach ($size in $Sizes) { , (New-IconPng -Size $size -TileColor $TileColor -WithCaret ($size -ge 24)) }
    $writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($Path))
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Sizes.Count)
        $offset = 6 + 16 * $Sizes.Count
        for ($i = 0; $i -lt $Sizes.Count; $i++) {
            $dimension = if ($Sizes[$i] -ge 256) { 0 } else { $Sizes[$i] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }

        foreach ($image in $images) { $writer.Write($image) }
    }
    finally {
        $writer.Dispose()
    }
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\Velotext.App\Assets' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$active = '#C8553D'
$paused = '#8A817A'
Write-Ico -Path (Join-Path $OutputDirectory 'velotext.ico') -Sizes 16, 20, 24, 32, 40, 48, 64, 128, 256 -TileColor $active
Write-Ico -Path (Join-Path $OutputDirectory 'tray.ico') -Sizes 16, 20, 24, 32, 40, 48 -TileColor $active
Write-Ico -Path (Join-Path $OutputDirectory 'tray-paused.ico') -Sizes 16, 20, 24, 32, 40, 48 -TileColor $paused
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'velotext-256.png'), (New-IconPng -Size 256 -TileColor $active -WithCaret $true))
Write-Output "Icons written to $OutputDirectory"
