# Renders app.ico (the app logo: blue rounded square with a white USB-stick symbol) as PNG-in-ICO at several sizes.
# The drawing matches Icons.RenderLogo in the app, so the exe icon, title bars and tray icon look the same.
# Output: ..\app.ico
param([string]$Out = "$PSScriptRoot\..\app.ico")
Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$rad) {
    $d = $rad * 2
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0, 102, 153))), (New-RoundRect 0 0 $s $s ($s * 0.24)))

    $cx = $s / 2.0; $top = $s * 0.12; $bottom = $s * 0.88; $lw = $s * 0.085
    $capW = $s * 0.34; $capH = $s * 0.19; $bodyW = $s * 0.54; $bodyTop = $top + $capH - $lw * 0.2
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), $lw
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, (New-RoundRect ($cx - $bodyW / 2 + $lw / 2) ($bodyTop + $lw / 2) ($bodyW - $lw) ($bottom - $bodyTop - $lw) ($s * 0.1)))
    $g.DrawRectangle($pen, $cx - $capW / 2 + $lw / 2, $top + $lw / 2, $capW - $lw, $capH + $lw * 0.2 - $lw / 2)

    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    ,@($s, $ms.ToArray())
}
$fs = [IO.File]::Create($Out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($e in $pngs) {
    $s = $e[0]; $data = $e[1]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($e in $pngs) { $bw.Write($e[1]) }
$bw.Close()
"wrote $Out"
