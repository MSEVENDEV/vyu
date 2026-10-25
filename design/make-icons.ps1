# Vyu brand mark → multi-size .ico + .png, rendered with WPF so it matches the app exactly.
# Purple squircle (Amethyst → Lilac) with a white eye whose pupil is a play button.
# Usage: powershell -ExecutionPolicy Bypass -File design/make-icons.ps1 -OutDir src/Vyu/Assets
param([string]$OutDir = '.')
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$ColorA = '#6D28D9'; $ColorB = '#B07CFF'
$stroke = 'M2.8 12 C5.6 7 8.8 5 12 5 C15.2 5 18.4 7 21.2 12 C18.4 17 15.2 19 12 19 C8.8 19 5.6 17 2.8 12 Z'
$fill = 'M10.1 8.9 C10.1 8.4 10.6 8.1 11 8.4 L15.2 11.4 C15.6 11.7 15.6 12.3 15.2 12.6 L11 15.6 C10.6 15.9 10.1 15.6 10.1 15.1 Z'

function Render([int]$size) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = [double]$size
    $rect = New-Object System.Windows.Rect 0, 0, $s, $s
    $grad = New-Object System.Windows.Media.LinearGradientBrush (
        [System.Windows.Media.ColorConverter]::ConvertFromString($ColorA)), ([System.Windows.Media.ColorConverter]::ConvertFromString($ColorB)),
        (New-Object System.Windows.Point 0, 0), (New-Object System.Windows.Point 1, 1)
    $r = $s * 0.235
    $dc.DrawRoundedRectangle($grad, $null, $rect, $r, $r)
    $gloss = New-Object System.Windows.Media.LinearGradientBrush
    $gloss.StartPoint = New-Object System.Windows.Point 0, 0
    $gloss.EndPoint = New-Object System.Windows.Point 0, 1
    $gloss.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromArgb(46, 255, 255, 255)), 0))
    $gloss.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromArgb(0, 255, 255, 255)), 0.6))
    $dc.DrawRoundedRectangle($gloss, $null, $rect, $r, $r)
    $scale = $s / 24.0
    $tg = New-Object System.Windows.Media.ScaleTransform $scale, $scale
    $width = if ($size -le 20) { [Math]::Max(3.0 * $scale, 1.5) } else { [Math]::Max(2.6 * $scale, 1.6) }
    $pen = New-Object System.Windows.Media.Pen ([System.Windows.Media.Brushes]::White), $width
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    $g = [System.Windows.Media.Geometry]::Parse($stroke).Clone(); $g.Transform = $tg
    $dc.DrawGeometry($null, $pen, $g)
    $f = [System.Windows.Media.Geometry]::Parse($fill).Clone(); $f.Transform = $tg
    $dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $f)
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    return $rtb
}

function Png($bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

# classic 32-bit DIB entry (what GDI, WinForms and the tray expect below 256 px)
function Dib($bmp) {
    $s = $bmp.PixelWidth
    $conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bmp, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
    $stride = $s * 4
    $px = New-Object byte[] ($stride * $s)
    $conv.CopyPixels($px, $stride, 0)
    $maskRow = [int]([Math]::Floor(($s + 31) / 32) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($s * $s * 4 + $maskRow * $s)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($px, $y * $stride, $stride) }
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$blobs = @()
foreach ($sz in $sizes) { $bmp = Render $sz; if ($sz -ge 256) { $blobs += , (Png $bmp) } else { $blobs += , (Dib $bmp) } }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $blobs[$i].Length; $b = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($blob in $blobs) { $w.Write($blob) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'vyu.ico'), $ico.ToArray())
[IO.File]::WriteAllBytes((Join-Path $OutDir 'vyu-mark.png'), (Png (Render 512)))
"wrote vyu.ico and vyu-mark.png to $OutDir"
