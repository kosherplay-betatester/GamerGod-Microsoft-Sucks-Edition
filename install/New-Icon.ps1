<#
.SYNOPSIS
    Generates GamerGod's application icon.

.DESCRIPTION
    Drawn in code rather than committed as a binary, for the same reason the interface sounds
    are synthesised: the repository stays free of assets nobody can review or edit, and the
    mark is tuned by changing a number.

    The mark is a stacked GG - one G high and left, a second low and right, overlapping on the
    diagonal. Two letters carry more of the name than one and the offset gives the mark depth
    without any illustration: the rear G is hollow and dim so it reads as behind, the front G
    is solid amber on a gradient so it reads as lit. The enclosure stays a rounded near-black
    panel, because the visual argument of this product is instrument rather than gamer chrome.

    Everything scales off $Size, so the silhouette holds from 256 down to 16 where a detailed
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
$groundLit = [System.Drawing.Color]::FromArgb(255, 26, 31, 42)
$signal = [System.Drawing.Color]::FromArgb(255, 255, 180, 84)
$signalLit = [System.Drawing.Color]::FromArgb(255, 255, 201, 120)
$signalDeep = [System.Drawing.Color]::FromArgb(255, 224, 154, 60)
$edge = [System.Drawing.Color]::FromArgb(255, 138, 100, 40)
$ghost = [System.Drawing.Color]::FromArgb(255, 176, 116, 44)

function New-RoundedPath {
    param([System.Drawing.RectangleF] $Box, [float] $Radius)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($Box.X, $Box.Y, $d, $d, 180, 90)
    $path.AddArc(($Box.Right - $d), $Box.Y, $d, $d, 270, 90)
    $path.AddArc(($Box.Right - $d), ($Box.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($Box.X, ($Box.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    return $path
}

function New-Frame {
    param([int] $Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)

    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- enclosure -----------------------------------------------------
    $inset = [math]::Max(1.0, $Size * 0.055)
    $radius = [math]::Max(2.0, $Size * 0.22)
    $box = New-Object System.Drawing.RectangleF($inset, $inset, ($Size - ($inset * 2)), ($Size - ($inset * 2)))
    $shell = New-RoundedPath -Box $box -Radius $radius

    # Lit from the top left, which is where every other surface in this interface is lit from.
    $panel = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF($box.X, $box.Y)),
        (New-Object System.Drawing.PointF($box.Right, $box.Bottom)),
        $groundLit, $ground)
    $g.FillPath($panel, $shell)

    $penWidth = [math]::Max(1.0, $Size * 0.05)
    $pen = New-Object System.Drawing.Pen($edge, $penWidth)
    $g.DrawPath($pen, $shell)

    # --- the two G's ---------------------------------------------------
    # Bahnschrift is the display face used throughout; Segoe UI is the fallback on a machine
    # that somehow lacks it.
    $family = try { New-Object System.Drawing.FontFamily('Bahnschrift') }
              catch { New-Object System.Drawing.FontFamily('Segoe UI') }

    # Clipped to the enclosure. Without this the rear G's shoulder pushes through the top-left
    # corner at every size, and a mark that leaks out of its own frame looks like a mistake.
    $g.SetClip($shell)

    $bold = [int][System.Drawing.FontStyle]::Bold

    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'

    # Glyph bounding boxes sit low, so each centre is nudged up a fraction. Centring on the box
    # rather than on the letter looks off by a pixel at small sizes - exactly where it shows.
    $rise = $Size * 0.035

    # Below 40 pixels the pair has about six pixels of glyph each to work with, and two
    # overlapping G's at that scale are indistinguishable from a smudge. The taskbar and the
    # window chrome get the single G instead - the same simplification any mark this dense
    # needs, and the reason to draw the icon in code rather than scale one bitmap down.
    $stacked = $Size -ge 40
    $em = if ($stacked) { $Size * 0.47 } else { $Size * 0.62 }

    if ($stacked) {
        # Rear G: high and left, and SOLID.
        #
        # It was a thin hollow outline, which is why the mark did not read as two letters - at
        # any size below 128 the stroke thinned into noise behind the front glyph and what
        # survived looked like a single G with a smudge. Filled in a dimmer amber it stays
        # clearly a G while still sitting behind, because depth here comes from brightness and
        # overlap rather than from weight.
        $rear = New-Object System.Drawing.Drawing2D.GraphicsPath
        $rear.AddString('G', $family, $bold, $em,
            (New-Object System.Drawing.PointF(($Size * 0.395), (($Size * 0.400) - $rise))), $format)

        $rearFill = New-Object System.Drawing.SolidBrush($ghost)
        $g.FillPath($rearFill, $rear)
        $rearFill.Dispose()
        $rear.Dispose()
    }

    # Front G: low and right when stacked, dead centre when not. Solid, on its own vertical
    # gradient so it reads as lit rather than flat, and outlined in the ground colour first -
    # which is what keeps the two legible where they overlap.
    $frontX = if ($stacked) { $Size * 0.615 } else { $Size * 0.5 }
    $frontY = if ($stacked) { $Size * 0.620 } else { $Size * 0.5 }

    $front = New-Object System.Drawing.Drawing2D.GraphicsPath
    $front.AddString('G', $family, $bold, $em,
        (New-Object System.Drawing.PointF($frontX, ($frontY - $rise))), $format)

    $cutPen = New-Object System.Drawing.Pen($ground, [math]::Max(2.0, $Size * 0.085))
    $cutPen.LineJoin = 'Round'
    $g.DrawPath($cutPen, $front)

    $bounds = $front.GetBounds()
    if ($bounds.Height -gt 0) {
        $glyph = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF($bounds.X, ($bounds.Y - 1))),
            (New-Object System.Drawing.PointF($bounds.X, ($bounds.Bottom + 1))),
            $signalLit, $signalDeep)
        $g.FillPath($glyph, $front)
        $glyph.Dispose()
    }
    else {
        $flat = New-Object System.Drawing.SolidBrush($signal)
        $g.FillPath($flat, $front)
        $flat.Dispose()
    }

    $cutPen.Dispose(); $front.Dispose()
    $format.Dispose(); $family.Dispose()
    $pen.Dispose(); $panel.Dispose(); $shell.Dispose(); $g.Dispose()

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
