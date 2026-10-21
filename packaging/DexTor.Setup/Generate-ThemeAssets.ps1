$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# Images for the WixStdBA theme, drawn to the installer design (2026-10): window #0E0F13,
# the "go" button a dark face #1C2320 whose edge #296B4E fades inwards over ~10 px,
# secondary buttons #1B1C21 with a hairline edge, outlined caps badges, a thin light
# progress bar. thmutil cannot draw any of this itself, so it is baked into PNGs.
$assetDir = Join-Path $PSScriptRoot 'Assets'
[IO.Directory]::CreateDirectory($assetDir) | Out-Null
$window = [Drawing.ColorTranslator]::FromHtml('#0E0F13')

function New-Canvas([int]$width, [int]$height) {
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear($window)
    return @($bitmap, $g)
}

function New-Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Blend([Drawing.Color]$a, [Drawing.Color]$b, [double]$t) {
    $t = [Math]::Max(0.0, [Math]::Min(1.0, $t))
    return [Drawing.Color]::FromArgb(
        [int]($a.R + ($b.R - $a.R) * $t), [int]($a.G + ($b.G - $a.G) * $t), [int]($a.B + ($b.B - $a.B) * $t))
}

function Save($bitmap, $g, [string]$name) {
    $bitmap.Save((Join-Path $assetDir $name), [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bitmap.Dispose()
}

# Inner glow along a rounded rectangle: inset 0 0 <blur> <colour> at <alpha>, as CSS draws it.
function Add-InnerGlow($g, [double]$w, [double]$h, [double]$r, [Drawing.Color]$colour, [int]$blur, [double]$alpha) {
    for ($step = 0; $step -lt $blur; $step++) {
        $t = 1 - $step / [double]$blur
        $a = [int][Math]::Round(255 * $alpha * $t * $t)
        if ($a -le 0) { continue }
        $inset = $step + 0.5
        $ring = New-Rounded $inset $inset ($w - 1 - 2 * $inset) ($h - 1 - 2 * $inset) ([Math]::Max(1, $r - $inset))
        $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb($a, $colour), 1.2)
        $g.DrawPath($pen, $ring); $pen.Dispose(); $ring.Dispose()
    }
}

# The keyboard focus. thmutil shows a focused button with its focus image instead of the
# normal one, but hover still wins over focus. The focus image used to be the hover one, so
# the default button of every page already looked hovered and the pointer changed nothing.
# Now it is the normal button with a faint ring inside the edge.
function Add-FocusRing($g, [int]$w, [int]$h) {
    $ring = New-Rounded 4.5 4.5 ($w - 9) ($h - 9) 5
    $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(40, 255, 255, 255), 1); $g.DrawPath($pen, $ring); $pen.Dispose(); $ring.Dispose()
}

# The primary ("go") button of the kit: a #12141A face, a 2 px border running from #1E9A62 to
# #7AF0BE, and a green inner glow. thmutil cannot draw outside a control, so the outer glow
# of the design is left out - which is why hover is made plain inside the button: the face
# turns green, the glow doubles and the border lights up along its whole length.
$green = [Drawing.ColorTranslator]::FromHtml('#3DDC97')
# name, face, inner glow blur, alpha, border from, border to, border width
foreach ($state in @(
        @('normal', '#12141A', 10, 0.15, '#1E9A62', '#7AF0BE', 1.6),
        @('focus', '#12141A', 10, 0.15, '#1E9A62', '#7AF0BE', 1.6),
        @('hover', '#17271F', 18, 0.42, '#3DDC97', '#B4FADB', 2.0),
        @('pressed', '#0F1915', 8, 0.20, '#1E9A62', '#7AF0BE', 1.6))) {
    $w = 200; $h = 40
    $bitmap, $g = New-Canvas $w $h
    $path = New-Rounded 0.5 0.5 ($w - 1) ($h - 1) 8
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($state[1])); $g.FillPath($brush, $path); $brush.Dispose()
    Add-InnerGlow $g $w $h 8 $green $state[2] $state[3]
    $edge = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.RectangleF]::new(0, 0, $w, $h),
        [Drawing.ColorTranslator]::FromHtml($state[4]), [Drawing.ColorTranslator]::FromHtml($state[5]), [Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $width = [single]$state[6]
    $pen = [Drawing.Pen]::new($edge, $width)
    $half = 0.5 + $width / 2
    $outline = New-Rounded $half $half ($w - 2 * $half) ($h - 2 * $half) (8.5 - $half)
    $g.DrawPath($pen, $outline)
    $pen.Dispose(); $edge.Dispose(); $outline.Dispose(); $path.Dispose()
    if ($state[0] -eq 'focus') { Add-FocusRing $g $w $h }
    Save $bitmap $g "primary-$($state[0]).png"
}

# The secondary button of the kit: #1C1A23, a dark 1 px edge, a faint violet line inside it and
# a faint violet inner light. Hover is lighter than the kit's #23202B, which could hardly be
# told from the normal face on many screens: #2C2938 with a brighter violet line.
$violet = [Drawing.Color]::FromArgb(0xD6, 0xC4, 0xFF)
# name, face, inner light, violet line alpha
foreach ($state in @(@('normal', '#1C1A23', $true, 26), @('focus', '#1C1A23', $true, 26), @('hover', '#2C2938', $true, 64), @('pressed', '#18161E', $false, 20))) {
    $w = 120; $h = 40
    $bitmap, $g = New-Canvas $w $h
    $path = New-Rounded 0.5 0.5 ($w - 1) ($h - 1) 8
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($state[1])); $g.FillPath($brush, $path); $brush.Dispose()
    if ($state[2]) { Add-InnerGlow $g $w $h 8 $violet 12 0.06 }
    $inset = New-Rounded 1.5 1.5 ($w - 3) ($h - 3) 7
    $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb($state[3], $violet), 1); $g.DrawPath($pen, $inset); $pen.Dispose(); $inset.Dispose()
    $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(89, 0, 0, 0), 1); $g.DrawPath($pen, $path); $pen.Dispose(); $path.Dispose()
    if ($state[0] -eq 'focus') { Add-FocusRing $g $w $h }
    Save $bitmap $g "secondary-$($state[0]).png"
}

# Every button that cannot be pressed: #1D1B28 with the #2C2A3A hairline.
$bitmap, $g = New-Canvas 200 40
$path = New-Rounded 0.5 0.5 199 39 8
$brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#1D1B28')); $g.FillPath($brush, $path); $brush.Dispose()
$pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#2C2A3A'), 1); $g.DrawPath($pen, $path); $pen.Dispose(); $path.Dispose()
Save $bitmap $g 'disabled.png'

# "All set": the design's green block - a dark green face, the green border and a lit
# medallion with a tick. Its text is a label on top, on the same flat face colour.
$bitmap, $g = New-Canvas 556 52
$path = New-Rounded 0.5 0.5 555 51 10
$brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#13261F')); $g.FillPath($brush, $path); $brush.Dispose()
$pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(115, $green), 1); $g.DrawPath($pen, $path); $pen.Dispose(); $path.Dispose()
$disc = [Drawing.RectangleF]::new(16, 14, 24, 24)
for ($i = 6; $i -ge 1; $i--) {
    $halo = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb([int](30 * (1 - $i / 7.0)), $green))
    $g.FillEllipse($halo, [Drawing.RectangleF]::Inflate($disc, $i, $i)); $halo.Dispose()
}
$medallion = [Drawing.Drawing2D.GraphicsPath]::new(); $medallion.AddEllipse($disc)
$light = [Drawing.Drawing2D.PathGradientBrush]::new($medallion)
$light.CenterColor = [Drawing.ColorTranslator]::FromHtml('#5BE6A8'); $light.SurroundColors = @([Drawing.ColorTranslator]::FromHtml('#2FBF80'))
$light.CenterPoint = [Drawing.PointF]::new(24.4, 20)
$g.FillPath($light, $medallion); $light.Dispose(); $medallion.Dispose()
$tick = [Drawing.Pen]::new([Drawing.Color]::White, 2.4)
$tick.StartCap = [Drawing.Drawing2D.LineCap]::Round; $tick.EndCap = [Drawing.Drawing2D.LineCap]::Round; $tick.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
$g.DrawLines($tick, [Drawing.PointF[]]@([Drawing.PointF]::new(22.7, 26.2), [Drawing.PointF]::new(26.4, 29.8), [Drawing.PointF]::new(33.5, 22.2)))
$tick.Dispose()
Save $bitmap $g 'success-block.png'

# The window's own close button (the design has no system caption).
foreach ($state in @(@('normal', '#0E0F13'), @('hover', '#C42B1C'))) {
    $bitmap, $g = New-Canvas 46 32
    $g.Clear([Drawing.ColorTranslator]::FromHtml($state[1]))
    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($(if ($state[0] -eq 'hover') { '#FFFFFF' } else { '#A6A6BC' })), 1)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::None
    $g.DrawLine($pen, 18, 11, 27, 20); $g.DrawLine($pen, 27, 11, 18, 20)
    $pen.Dispose()
    Save $bitmap $g "close-$($state[0]).png"
}

# The wordmark as the design sets it: the logo's three parts - the shield, "DexTor" and
# "OS" - taken from docs\logo.png and placed in the design's proportions, which give the
# words more weight next to the shield than the logo file does. Boxes are design pixels of
# the 149.6 x 34.4 wordmark, measured on the mock-ups; "OS" takes the brand violet.
function New-Wordmark([double]$scale, [bool]$transparent) {
    $logo = [Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot '..\..\docs\logo.png'))
    $w = [int][Math]::Ceiling(149.6 * $scale); $h = [int][Math]::Ceiling(34.4 * $scale)
    $bitmap, $g = New-Canvas $w $h
    if ($transparent) { $g.Clear([Drawing.Color]::Transparent) }
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $violet = [Drawing.Imaging.ColorMatrix]::new()
    $violet.Matrix00 = 0; $violet.Matrix11 = 0; $violet.Matrix22 = 0
    $violet.Matrix40 = 0x94 / 255.0; $violet.Matrix41 = 0x55 / 255.0; $violet.Matrix42 = 0xF2 / 255.0
    # The design's shield is lit brighter than the logo file's (#9247DC against #5D2A80).
    $shield = [Drawing.Imaging.ColorMatrix]::new()
    $shield.Matrix00 = 1.55; $shield.Matrix11 = 1.55; $shield.Matrix22 = 1.6
    $words = [Drawing.Imaging.ColorMatrix]::new()
    $words.Matrix00 = 1.08; $words.Matrix11 = 1.08; $words.Matrix22 = 1.08
    $parts = @(
        @(0, 0, 122, 206, 0.0, 0.0, 20.8, 34.4, $shield),
        @(169, 57, 460, 102, 30.4, 9.6, 83.2, 19.2, $words),
        @(648, 72, 173, 89, 117.6, 10.4, 32.0, 17.6, $violet))
    foreach ($p in $parts) {
        $target = [Drawing.RectangleF]::new($p[4] * $scale, $p[5] * $scale, $p[6] * $scale, $p[7] * $scale)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        if ($p[8]) { $attributes.SetColorMatrix($p[8]) }
        # The design's "OS" has a heavier stroke than the logo file's hairline: it is drawn a
        # few times around a small circle, which thickens it without changing its shape.
        $offsets = if ($p[8] -eq $violet) { 0..7 | ForEach-Object { $a = $_ * [Math]::PI / 4; [Drawing.PointF]::new(0.3 * $scale * [Math]::Cos($a), 0.3 * $scale * [Math]::Sin($a)) } } else { @([Drawing.PointF]::new(0, 0)) }
        foreach ($o in $offsets) {
            $points = [Drawing.PointF[]]@(
                [Drawing.PointF]::new($target.Left + $o.X, $target.Top + $o.Y),
                [Drawing.PointF]::new($target.Right + $o.X, $target.Top + $o.Y),
                [Drawing.PointF]::new($target.Left + $o.X, $target.Bottom + $o.Y))
            $g.DrawImage($logo, $points, [Drawing.RectangleF]::new($p[0], $p[1], $p[2], $p[3]), [Drawing.GraphicsUnit]::Pixel, $attributes)
        }
        $attributes.Dispose()
    }
    $logo.Dispose()
    return @($bitmap, $g)
}

# The installer's, on the window colour at thmutil's 1:1 scale, and the launcher's, transparent
# and at four times the size so it stays sharp on any display.
$bitmap, $g = New-Wordmark 1.0 $false
Save $bitmap $g 'wordmark.png'
$bitmap, $g = New-Wordmark 4.0 $true
$bitmap.Save((Join-Path $PSScriptRoot '..\..\src\DexTor.Launcher\Assets\wordmark.png'), [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bitmap.Dispose()

# Badge outline in three slices - caps and a stretchable middle - so a badge can take
# the width its text needs in each language.
$border = [Drawing.ColorTranslator]::FromHtml('#2C2A3A')
$bitmap, $g = New-Canvas 24 24
$pen = [Drawing.Pen]::new($border, 1)
$path = New-Rounded 0.5 0.5 23 23 6
$g.DrawPath($pen, $path)
$pen.Dispose(); $path.Dispose()
foreach ($slice in @(@('badge-left.png', 0, 6), @('badge-right.png', 18, 6))) {
    $part = $bitmap.Clone([Drawing.Rectangle]::new($slice[1], 0, $slice[2], 24), $bitmap.PixelFormat)
    $part.Save((Join-Path $assetDir $slice[0]), [Drawing.Imaging.ImageFormat]::Png)
    $part.Dispose()
}
$g.Dispose(); $bitmap.Dispose()

# Progress bar strip for thmutil: four columns, each the bar's full height - left edge,
# filled middle, unfilled middle, right edge. thmutil stretches the two middle columns.
$bitmap = [Drawing.Bitmap]::new(4, 8)
$columns = @('#272534', '#E7E7EC', '#272534', '#272534')
for ($x = 0; $x -lt 4; $x++) {
    for ($y = 0; $y -lt 8; $y++) {
        $bitmap.SetPixel($x, $y, [Drawing.ColorTranslator]::FromHtml($columns[$x]))
    }
}
$bitmap.Save((Join-Path $assetDir 'progress.png'), [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

# The badge's top and bottom edges: one pixel, stretched between the caps.
$bitmap = [Drawing.Bitmap]::new(1, 1)
$bitmap.SetPixel(0, 0, [Drawing.ColorTranslator]::FromHtml('#2C2A3A'))
$bitmap.Save((Join-Path $assetDir 'badge-line.png'), [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
