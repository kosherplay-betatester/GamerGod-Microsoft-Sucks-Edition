<#
.SYNOPSIS
    Generates GamerGod's application icon.

.DESCRIPTION
    Drawn in code rather than committed as a binary, for the same reason the interface sounds
    are synthesised: the repository stays free of assets nobody can review or edit, and the
    mark is tuned by changing a number.

    The mark is the same one in the title bar - an amber G inside a rounded enclosure on a
    near-black ground. It reads as an instrument panel rather than gamer chrome, which is the
    whole visual argument of the product, and it stays legible at 16 pixels where a detailed
    illustration would turn to mud.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot 'gamergod.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Windows picks the nearest size and scales; supplying each one avoids the blur that makes an
# application look unfinished in the taskbar.
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

$ground = [System.Drawing.Color]::FromArgb(255, 10, 12, 17)
$signal = [System.Drawing.Color]::FromArgb(255, 255, 180, 84)
$edge = [System.Drawing.Color]::FromArgb(255, 138, 100, 40)

function New-Frame {
    param([int] $Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)

    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded enclosure. The radius scales with the icon so the silhouette stays constant.
    $inset = [math]::Max(1, [int]($Size * 0.06))
    $radius = [math]::Max(2, [int]($Size * 0.22))
    $box = New-Object System.Drawing.Rectangle($inset, $inset, ($Size - ($inset * 2)), ($Size - ($inset * 2)))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($box.X, $box.Y, $d, $d, 180, 90)
    $path.AddArc(($box.Right - $d), $box.Y, $d, $d, 270, 90)
    $path.AddArc(($box.Right - $d), ($box.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($box.X, ($box.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $fill = New-Object System.Drawing.SolidBrush($ground)
    $g.FillPath($fill, $path)

    $penWidth = [math]::Max(1.0, $Size * 0.05)
    $pen = New-Object System.Drawing.Pen($edge, $penWidth)
    $g.DrawPath($pen, $path)

    # The G. Bahnschrift is the display face used throughout; Segoe UI is the fallback on a
    # machine that somehow lacks it.
    $family = try { New-Object System.Drawing.FontFamily('Bahnschrift') }
              catch { New-Object System.Drawing.FontFamily('Segoe UI') }

    $font = New-Object System.Drawing.Font($family, ($Size * 0.62), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)

    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'

    # Nudged up a fraction: glyph bounding boxes sit low, and centring on the box rather than
    # on the letter looks off by a pixel at small sizes, which is exactly where it shows.
    $textBox = New-Object System.Drawing.RectangleF(0, -($Size * 0.04), $Size, $Size)
    $text = New-Object System.Drawing.SolidBrush($signal)
    $g.DrawString('G', $font, $text, $textBox, $format)

    $text.Dispose(); $font.Dispose(); $family.Dispose()
    $pen.Dispose(); $fill.Dispose(); $path.Dispose(); $format.Dispose(); $g.Dispose()

    return $bitmap
}

Write-Host "  Drawing $($sizes.Count) sizes" -ForegroundColor Cyan

$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-Frame -Size $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bitmap.Dispose()
}

# ICO container. Every frame is stored as PNG, which the format has allowed since Vista and
# which keeps the 256px entry from bloating the file.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)

$writer.Write([uint16]0)               # reserved
$writer.Write([uint16]1)               # type: icon
$writer.Write([uint16]$frames.Count)

$offset = 6 + (16 * $frames.Count)

foreach ($frame in $frames) {
    # 256 is encoded as 0 in the directory entry.
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }

    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)             # palette
    $writer.Write([byte]0)             # reserved
    $writer.Write([uint16]1)           # colour planes
    $writer.Write([uint16]32)          # bits per pixel
    $writer.Write([uint32]$frame.Bytes.Length)
    $writer.Write([uint32]$offset)

    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $writer.Write($frame.Bytes)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($OutputPath, $out.ToArray())
$writer.Dispose(); $out.Dispose()

$kb = [math]::Round((Get-Item $OutputPath).Length / 1KB, 1)
Write-Host "  Written to $OutputPath ($kb KB)" -ForegroundColor Green
