# Genera el icono de la app (Assets\icono.ico) y las imágenes del instalador (Installer\Imagenes\*.bmp).
# Uso: powershell -ExecutionPolicy Bypass -File Tools\generar-graficos.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$raiz = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $raiz 'Assets'
$imgs = Join-Path $raiz 'Installer\Imagenes'
New-Item -ItemType Directory -Force $assets, $imgs | Out-Null

$naranja = [System.Drawing.Color]::FromArgb(255, 0xFF, 0x6A, 0x00)
$ambar = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xB0, 0x20)
$fondo1 = [System.Drawing.Color]::FromArgb(255, 0x22, 0x26, 0x30)
$fondo2 = [System.Drawing.Color]::FromArgb(255, 0x0D, 0x0E, 0x12)

function RutaRedondeada([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Indicador tipo velocímetro: arco gris, arco naranja-ámbar, aguja blanca y buje.
function DibujarIndicador($g, [float]$cx, [float]$cy, [float]$radio, [float]$grosor) {
    $rect = New-Object System.Drawing.RectangleF ($cx - $radio), ($cy - $radio), ($radio * 2), ($radio * 2)
    $base = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), $grosor
    $base.StartCap = 'Round'; $base.EndCap = 'Round'
    $g.DrawArc($base, $rect, 135, 270)

    $rg = New-Object System.Drawing.RectangleF ($rect.X - $grosor * 2), ($rect.Y - $grosor * 2), ($rect.Width + $grosor * 4), ($rect.Height + $grosor * 4)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rg, $naranja, $ambar, 0.0
    $activo = New-Object System.Drawing.Pen $grad, $grosor
    $activo.StartCap = 'Round'; $activo.EndCap = 'Round'
    $g.DrawArc($activo, $rect, 135, 200)

    $ang = (135 + 200) * [Math]::PI / 180
    $largo = $radio * 0.78
    $aguja = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.2, $grosor * 0.62))
    $aguja.StartCap = 'Round'; $aguja.EndCap = 'Round'
    $g.DrawLine($aguja, $cx, $cy, ($cx + [Math]::Cos($ang) * $largo), ($cy + [Math]::Sin($ang) * $largo))
    $b = $grosor * 0.95
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), ($cx - $b), ($cy - $b), ($b * 2), ($b * 2))
}

function Icono([int]$lado) {
    $bmp = New-Object System.Drawing.Bitmap $lado, $lado
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(0.5, $lado * 0.03)
    $ruta = RutaRedondeada $m $m ($lado - 2 * $m) ($lado - 2 * $m) ($lado * 0.22)
    $rf = New-Object System.Drawing.RectangleF 0, 0, $lado, $lado
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rf, $fondo1, $fondo2, 90.0), $ruta)
    if ($lado -ge 32) {
        $borde = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 0xFF, 0x6A, 0x00)), ([Math]::Max(1, $lado * 0.02))
        $g.DrawPath($borde, $ruta)
    }
    DibujarIndicador $g ($lado * 0.5) ($lado * 0.56) ($lado * 0.30) ([Math]::Max(1.6, $lado * 0.095))
    $g.Dispose()
    return $bmp
}

function ComoBmpIco($bmp) {
    $t = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([UInt32]40); $bw.Write([Int32]$t); $bw.Write([Int32]($t * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $t - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $t; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([Byte]$c.B); $bw.Write([Byte]$c.G); $bw.Write([Byte]$c.R); $bw.Write([Byte]$c.A)
        }
    }
    $fila = [Math]::Ceiling($t / 32) * 4
    $bw.Write((New-Object Byte[] ($fila * $t)))
    $bw.Flush()
    return , $ms.ToArray()
}

# ---- icono.ico
$tamanos = 16, 20, 24, 32, 40, 48, 64, 256
$datos = foreach ($t in $tamanos) {
    $bmp = Icono $t
    if ($t -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Save((Join-Path $assets 'icono-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $d = $ms.ToArray()
    } else { $d = ComoBmpIco $bmp }
    $bmp.Dispose()
    , $d
}
$salida = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $salida
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$tamanos.Count)
$off = 6 + 16 * $tamanos.Count
for ($n = 0; $n -lt $tamanos.Count; $n++) {
    $t = $tamanos[$n]; $l = if ($t -ge 256) { 0 } else { $t }
    $w.Write([Byte]$l); $w.Write([Byte]$l); $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$datos[$n].Length); $w.Write([UInt32]$off)
    $off += $datos[$n].Length
}
foreach ($d in $datos) { $w.Write($d) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icono.ico'), $salida.ToArray())

function Guardar24($bmp, $ruta) {
    $b24 = New-Object System.Drawing.Bitmap $bmp.Width, $bmp.Height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($b24); $g.DrawImage($bmp, 0, 0, $bmp.Width, $bmp.Height); $g.Dispose()
    $b24.Save($ruta, [System.Drawing.Imaging.ImageFormat]::Bmp); $b24.Dispose()
}

# ---- Imagen lateral del asistente (164x314 a 100 % y 410x785 a 250 %)
foreach ($e in @(@{W = 164; H = 314; N = 'lateral-100.bmp' }, @{W = 410; H = 785; N = 'lateral-250.bmp' })) {
    $W = $e.W; $H = $e.H; $k = $W / 164.0
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $rf = New-Object System.Drawing.RectangleF 0, 0, $W, $H
    $g.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rf, $fondo1, $fondo2, 90.0), $rf)

    # Resplandor naranja
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse((-0.4 * $W), (0.18 * $H), (1.8 * $W), (0.55 * $H))
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
    $pgb.CenterColor = [System.Drawing.Color]::FromArgb(85, 0xFF, 0x6A, 0x00)
    $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0xFF, 0x6A, 0x00))
    $g.FillPath($pgb, $glow)

    # Líneas diagonales sutiles
    $lin = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(14, 255, 255, 255)), (1 * $k)
    for ($i = -$H; $i -lt $W + $H; $i += [int](14 * $k)) { $g.DrawLine($lin, $i, $H, $i + $H * 0.6, 0) }

    DibujarIndicador $g ($W * 0.5) ($H * 0.42) ($W * 0.27) (8 * $k)

    $f1 = New-Object System.Drawing.Font 'Segoe UI', (19 * $k), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $f2 = New-Object System.Drawing.Font 'Segoe UI', (10 * $k), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $f3 = New-Object System.Drawing.Font 'Segoe UI', (9 * $k), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'
    $g.DrawString('AORUS', $f1, (New-Object System.Drawing.SolidBrush $naranja), (New-Object System.Drawing.RectangleF 0, ($H * 0.63), $W, (30 * $k)), $sf)
    $g.DrawString('M O N I T O R', $f2, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 233, 235, 241))), (New-Object System.Drawing.RectangleF 0, ($H * 0.63 + 25 * $k), $W, (20 * $k)), $sf)
    $g.DrawString("Temperaturas · Ventiladores`nIluminación", $f3, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x8B, 0x91, 0xA1))), (New-Object System.Drawing.RectangleF 0, ($H * 0.86), $W, (30 * $k)), $sf)

    $g.FillRectangle((New-Object System.Drawing.SolidBrush $naranja), 0, ($H - 4 * $k), $W, (4 * $k))
    $g.Dispose()
    Guardar24 $bmp (Join-Path $imgs $e.N)
    $bmp.Dispose()
}

# ---- Imagen pequeña de la cabecera (55x55 y 138x138)
foreach ($e in @(@{L = 55; N = 'pequena-100.bmp' }, @{L = 138; N = 'pequena-250.bmp' })) {
    $L = $e.L
    $bmp = New-Object System.Drawing.Bitmap $L, $L
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 0x12, 0x14, 0x19))
    $ico = Icono $L
    $g.DrawImage($ico, 0, 0, $L, $L)
    $g.Dispose(); $ico.Dispose()
    Guardar24 $bmp (Join-Path $imgs $e.N)
    $bmp.Dispose()
}

Write-Output "Gráficos generados en $assets y $imgs"
