<#
.SYNOPSIS
    Generates Kroshka's application icon and PNG logo from code.

.DESCRIPTION
    The repository stores the generator, not just the binary, so the icon can be
    regenerated when the brand colour changes. Produces:

      src/DPT.Kroshka.Client/assets/kroshka.ico   multi-size icon for the exe and tray
      web/assets/img/logo-192.png                 notification / touch icon
      web/assets/img/logo-512.png                 readme / store art

    Encoding matters. Windows renders ICO entries two ways:

      * BMP (DIB) entries - the classic format, honoured everywhere: Explorer,
        taskbar, Alt-Tab, the small 16px tray, the file property sheet.
      * PNG entries - only officially supported at 256x256. A PNG-compressed
        16px or 32px entry is silently skipped by several shell surfaces, which
        shows up as a blank or generic icon even though the file "has" an icon.

    So: BMP for 16-64, PNG for 128 and 256. That combination renders correctly
    in every context and still keeps the file small.

    Requires nothing but Windows PowerShell and .NET Framework (System.Drawing).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-ico.ps1
#>

[CmdletBinding()]
param(
    [string]$AccentTop = '#0080FF',
    [string]$AccentBottom = '#0066CC'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$iconDir = Join-Path $root 'src\DPT.Kroshka.Client\assets'
$imgDir = Join-Path $root 'web\assets\img'

New-Item -ItemType Directory -Force -Path $iconDir, $imgDir | Out-Null

# ------------------------------------------------------------------- drawing

function New-KroshkaBitmap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][int]$Pixels
    )

    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $Pixels, $Pixels, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = [double]$Pixels / 64.0
    $radius = [int][Math]::Max(2, 18 * $scale)
    $diameter = $radius * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc(($Pixels - $diameter), 0, $diameter, $diameter, 270, 90)
    $path.AddArc(($Pixels - $diameter), ($Pixels - $diameter), $diameter, $diameter, 0, 90)
    $path.AddArc(0, ($Pixels - $diameter), $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $Pixels, $Pixels
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList `
        $rect,
        ([System.Drawing.ColorTranslator]::FromHtml($AccentTop)),
        ([System.Drawing.ColorTranslator]::FromHtml($AccentBottom)),
        ([System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)

    $g.FillPath($brush, $path)

    $glossHeight = [int][Math]::Max(1, $Pixels * 0.55)
    $glossRect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $Pixels, $glossHeight
    $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList `
        $glossRect,
        ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)),
        ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)),
        ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)

    $g.SetClip($path)
    $g.FillRectangle($gloss, $glossRect)
    $g.ResetClip()

    $strokeWidth = [single][Math]::Max(1.6, 6 * $scale)
    $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::White), $strokeWidth
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $g.DrawLine($pen, [single](20 * $scale), [single](16 * $scale), [single](20 * $scale), [single](48 * $scale))

    $points = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF -ArgumentList ([single](43 * $scale)), ([single](15 * $scale))),
        (New-Object System.Drawing.PointF -ArgumentList ([single](24 * $scale)), ([single](32 * $scale))),
        (New-Object System.Drawing.PointF -ArgumentList ([single](44 * $scale)), ([single](49 * $scale)))
    )
    $g.DrawLines($pen, $points)

    $pen.Dispose()
    $brush.Dispose()
    $gloss.Dispose()
    $path.Dispose()
    $g.Dispose()

    return $bmp
}

# ------------------------------------------------------------- ICO encoders
#
# Both encoders append into a caller-supplied stream instead of returning a
# byte[]. PowerShell's output stream turns arrays into enumerated pipelines in
# surprising ways (a returned byte[] can arrive as N separate bytes), and an
# accumulator removes that class of bug entirely.

function Add-PngBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Drawing.Bitmap]$Bitmap,
        [Parameter(Mandatory = $true)][System.IO.MemoryStream]$Target
    )

    $temp = New-Object System.IO.MemoryStream
    $Bitmap.Save($temp, [System.Drawing.Imaging.ImageFormat]::Png)
    $Target.Write($temp.GetBuffer(), 0, [int]$temp.Length)
    $temp.Dispose()
}

<#
    A BMP entry inside an ICO is a BITMAPINFOHEADER followed by two bitmaps:

      XOR  - 32bpp BGRA, bottom-up, width*height*4 bytes
      AND  - 1bpp transparency mask, bottom-up, rows padded to 4 bytes

    The header height is doubled because it covers both. With a real alpha
    channel Windows mostly ignores AND, but a fully transparent pixel must
    still be marked in it or some older shell paths paint it black.
#>
function Add-DibBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Drawing.Bitmap]$Bitmap,
        [Parameter(Mandatory = $true)][int]$Pixels,
        [Parameter(Mandatory = $true)][System.IO.MemoryStream]$Target
    )

    $rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $Pixels, $Pixels
    $locked = $Bitmap.LockBits(
        $rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $stride = $locked.Stride

    # Named $rgba, not $pixels: PowerShell variable names are case-insensitive, so
    # $pixels would overwrite the $Pixels parameter and the arithmetic below would
    # suddenly be operating on a byte array.
    $rgba = New-Object byte[] ($stride * $Pixels)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $rgba, 0, $rgba.Length)
    $Bitmap.UnlockBits($locked)

    $maskStride = [int]([Math]::Floor(($Pixels + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskStride * $Pixels)

    # Build the AND mask from the alpha channel: bit set = transparent.
    for ($y = 0; $y -lt $Pixels; $y++) {
        $rowStart = $y * $stride
        $maskRow = ($Pixels - 1 - $y) * $maskStride
        for ($x = 0; $x -lt $Pixels; $x++) {
            $alpha = $rgba[$rowStart + ($x * 4) + 3]
            if ($alpha -lt 128) {
                $byteIndex = $maskRow + [int][Math]::Floor($x / 8)
                $bit = 7 - ($x % 8)
                $mask[$byteIndex] = $mask[$byteIndex] -bor ([byte](1 -shl $bit))
            }
        }
    }

    $bw = New-Object System.IO.BinaryWriter -ArgumentList $Target, ([System.Text.Encoding]::UTF8), $true

    # BITMAPINFOHEADER
    $bw.Write([UInt32]40)                      # biSize
    $bw.Write([Int32]$Pixels)                  # biWidth
    $bw.Write([Int32]($Pixels * 2))            # biHeight (XOR + AND)
    $bw.Write([UInt16]1)                       # biPlanes
    $bw.Write([UInt16]32)                      # biBitCount
    $bw.Write([UInt32]0)                       # biCompression = BI_RGB
    $bw.Write([UInt32]($Pixels * $Pixels * 4)) # biSizeImage
    $bw.Write([Int32]0)                        # biXPelsPerMeter
    $bw.Write([Int32]0)                        # biYPelsPerMeter
    $bw.Write([UInt32]0)                       # biClrUsed
    $bw.Write([UInt32]0)                       # biClrImportant

    # XOR bitmap, bottom-up
    for ($y = $Pixels - 1; $y -ge 0; $y--) {
        $bw.Write($rgba, ($y * $stride), ($Pixels * 4))
    }

    # AND mask, already ordered bottom-up
    $bw.Write($mask, 0, $mask.Length)
    $bw.Flush()
    $bw.Dispose()
}

# ---------------------------------------------------------------- PNG exports

foreach ($pixels in @(192, 512)) {
    $bitmap = New-KroshkaBitmap -Pixels $pixels
    $target = Join-Path $imgDir ("logo-{0}.png" -f $pixels)
    $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host ("  png  {0}" -f $target)
}

# ------------------------------------------------------------------- ICO file

# Every entry is BMP. PNG entries are legal in an ICO and Explorer renders them,
# but .NET's own Icon class refuses to decode a PNG entry ("input data size
# overflow"), which breaks ToBitmap() and any consumer built on System.Drawing.
# The cost of BMP at 256px is a few hundred kilobytes; the cost of a mystery
# blank icon is a support ticket.
$plan = @(
    @{ Pixels = 16 },
    @{ Pixels = 20 },
    @{ Pixels = 24 },
    @{ Pixels = 32 },
    @{ Pixels = 40 },
    @{ Pixels = 48 },
    @{ Pixels = 64 },
    @{ Pixels = 128 },
    @{ Pixels = 256 }
)

$entries = New-Object System.Collections.Generic.List[object]

foreach ($item in $plan) {
    $bitmap = New-KroshkaBitmap -Pixels $item.Pixels
    $buffer = New-Object System.IO.MemoryStream

    Add-DibBytes -Bitmap $bitmap -Pixels $item.Pixels -Target $buffer
    $bitmap.Dispose()

    $entries.Add([pscustomobject]@{
        Pixels = $item.Pixels
        Format = 'bmp'
        Bytes  = $buffer.ToArray()
    })

    $buffer.Dispose()
}

$icoPath = Join-Path $iconDir 'kroshka.ico'
$file = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter -ArgumentList $file

$writer.Write([UInt16]0)                 # reserved
$writer.Write([UInt16]1)                 # type: icon
$writer.Write([UInt16]$entries.Count)    # image count

$offset = [int](6 + (16 * $entries.Count))

foreach ($entry in $entries) {
    $dimension = if ($entry.Pixels -ge 256) { 0 } else { $entry.Pixels }

    $writer.Write([Byte]$dimension)                # width
    $writer.Write([Byte]$dimension)                # height
    $writer.Write([Byte]0)                         # palette colours
    $writer.Write([Byte]0)                         # reserved
    $writer.Write([UInt16]1)                       # colour planes
    $writer.Write([UInt16]32)                      # bits per pixel
    $writer.Write([UInt32]$entry.Bytes.Length)     # size of image data
    $writer.Write([UInt32]$offset)                 # offset

    $offset += $entry.Bytes.Length
}

foreach ($entry in $entries) {
    $writer.Write($entry.Bytes)
}

$writer.Flush()
$writer.Dispose()
$file.Dispose()

$sizeKb = [Math]::Round((Get-Item $icoPath).Length / 1KB, 1)
Write-Host ("  ico  {0} ({1} KB)" -f $icoPath, $sizeKb)

foreach ($entry in $entries) {
    Write-Host ("         {0,3}px {1,3} KB  {2}" -f $entry.Pixels, [Math]::Round($entry.Bytes.Length / 1KB, 1), $entry.Format)
}

Write-Host "done."
