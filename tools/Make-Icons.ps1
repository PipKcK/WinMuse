# Generates the app icon (AppIcon.ico + AppIcon.png) and the taskbar thumbnail-button icons.
#
#   .\Make-Icons.ps1                      # built-in design
#   .\Make-Icons.ps1 -FromPng my-logo.png # use your own square PNG (256x256 or larger recommended)
#
# The build runs this automatically when the AppIconPng property is set (see README).
param(
    [string]$FromPng,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\src\WinMuse\Assets")
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function New-Graphics($bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    return $g
}

# Built-in design: dark Fluent tile (near-black to deep blue) with a white note and a soft blue glow.
function New-DefaultIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = New-Graphics $bmp
    $pad = [Math]::Max(1, $size * 0.06)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $path = New-RoundRect $rect.X $rect.Y $rect.Width $rect.Height ($size * 0.22)

    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::Black), ([System.Drawing.Color]::Black), 60.0
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.Color]::FromArgb(255, 17, 24, 39), [System.Drawing.Color]::FromArgb(255, 15, 30, 70), [System.Drawing.Color]::FromArgb(255, 29, 58, 138))
    $blend.Positions = @(0.0, 0.55, 1.0)
    $bg.InterpolationColors = $blend
    $g.FillPath($bg, $path)

    # Soft blue glow behind the note
    $glowRect = New-Object System.Drawing.RectangleF ($size * 0.2), ($size * 0.18), ($size * 0.62), ($size * 0.62)
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse($glowRect)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(110, 59, 130, 246)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 59, 130, 246))
    $g.SetClip($path); $g.FillEllipse($glow, $glowRect); $g.ResetClip()

    # Subtle top highlight and hairline border (Fluent depth)
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(36, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
    $g.FillPath($hl, $path)
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 148, 163, 184)), ([Math]::Max(1, $size / 128))
        $g.DrawPath($pen, $path)
    }

    $font = New-Object System.Drawing.Font "Segoe Fluent Icons", ($size * 0.46), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char]0xEC4F, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($size * 0.02), $size, $size), $sf)
    $g.Dispose(); return $bmp
}

function Resize-Png([System.Drawing.Image]$src, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = New-Graphics $bmp
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose(); return $bmp
}

function New-GlyphBitmap([int]$size, [int]$glyph) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = New-Graphics $bmp
    $font = New-Object System.Drawing.Font "Segoe Fluent Icons", ($size * 0.7), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char]$glyph, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $size, $size), $sf)
    $g.Dispose(); return $bmp
}

# Writes a PNG-compressed multi-size .ico
function Save-Ico($bitmaps, [string]$file) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$bitmaps.Count)
    $pngs = @(); foreach ($b in $bitmaps) { $s = New-Object System.IO.MemoryStream; $b.Save($s, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += , $s.ToArray() }
    $offset = 6 + 16 * $bitmaps.Count
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $w = $bitmaps[$i].Width; $b8 = [byte]$(if ($w -ge 256) { 0 } else { $w })
        $bw.Write($b8); $bw.Write($b8); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset); $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    [System.IO.File]::WriteAllBytes($file, $ms.ToArray())
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
if ($FromPng) {
    $src = [System.Drawing.Image]::FromFile((Resolve-Path $FromPng))
    $frames = $sizes | ForEach-Object { Resize-Png $src $_ }
    $png = Resize-Png $src 256
    $src.Dispose()
} else {
    $frames = $sizes | ForEach-Object { New-DefaultIcon $_ }
    $png = New-DefaultIcon 256
}
Save-Ico $frames (Join-Path $OutDir "AppIcon.ico")
$png.Save((Join-Path $OutDir "AppIcon.png"), [System.Drawing.Imaging.ImageFormat]::Png)

@{ Previous = 0xE892; Play = 0xE768; Pause = 0xE769; Next = 0xE893 }.GetEnumerator() | ForEach-Object {
    Save-Ico @((New-GlyphBitmap 16 $_.Value), (New-GlyphBitmap 20 $_.Value), (New-GlyphBitmap 24 $_.Value), (New-GlyphBitmap 32 $_.Value)) (Join-Path $OutDir ("Thumb" + $_.Key + ".ico"))
}
Write-Host "Icons written to $OutDir"
