<#
.SYNOPSIS
    Generates Kroshka's application icon and PNG logo from code.

.DESCRIPTION
    The repository stores the generator, not just the binary, so the icon can be
    regenerated when the brand colour changes. Produces:

      src/DPT.Kroshka.Client/assets/kroshka.ico   multi-size icon for the exe and tray
      web/assets/img/logo-192.png                 notification / touch icon
      web/assets/img/logo-512.png                 store / readme art

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

function Get-PngBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Drawing.Bitmap]$Bitmap
    )

    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $result = [byte[]]$stream.ToArray()
    $stream.Dispose()

    # -NoEnumerate keeps a byte[] a byte[] instead of unrolling it into the pipeline.
    Write-Output -NoEnumerate $result
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

# PNG-compressed entries are valid on Windows Vista and newer, which is everything
# this app supports, and they keep a 256px icon from bloating the exe.
$entries = New-Object System.Collections.Generic.List[object]

foreach ($pixels in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $bitmap = New-KroshkaBitmap -Pixels $pixels
    $bytes = [byte[]](Get-PngBytes -Bitmap $bitmap)
    $bitmap.Dispose()

    $entries.Add([pscustomobject]@{ Pixels = $pixels; Bytes = $bytes })
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
Write-Host ("  ico  {0} ({1} KB, {2} sizes)" -f $icoPath, $sizeKb, $entries.Count)
Write-Host "done."
