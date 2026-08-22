param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\PathEcho\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

function New-LogoBitmap([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 32.0

    $background = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $radius = 7 * $scale
    $diameter = 2 * $radius
    $bounds = [System.Drawing.RectangleF]::new(0, 0, $size, $size)
    $background.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
    $background.AddArc($bounds.Right - $diameter, $bounds.Top, $diameter, $diameter, 270, 90)
    $background.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $background.AddArc($bounds.Left, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $background.CloseFigure()
    $backgroundBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 23, 32, 29))
    $graphics.FillPath($backgroundBrush, $background)

    $greenPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 43, 181, 139), 3 * $scale)
    $whitePen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 3 * $scale)
    foreach ($pen in @($greenPen, $whitePen)) {
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    }

    $graphics.DrawLine($greenPen, 7 * $scale, 11.5 * $scale, 22 * $scale, 11.5 * $scale)
    $graphics.DrawLines($greenPen, @(
        [System.Drawing.PointF]::new(20 * $scale, 7.5 * $scale),
        [System.Drawing.PointF]::new(24 * $scale, 11.5 * $scale),
        [System.Drawing.PointF]::new(20 * $scale, 15.5 * $scale)))
    $graphics.DrawLine($whitePen, 25 * $scale, 20.5 * $scale, 10 * $scale, 20.5 * $scale)
    $graphics.DrawLines($whitePen, @(
        [System.Drawing.PointF]::new(12 * $scale, 16.5 * $scale),
        [System.Drawing.PointF]::new(8 * $scale, 20.5 * $scale),
        [System.Drawing.PointF]::new(12 * $scale, 24.5 * $scale)))

    $greenPen.Dispose()
    $whitePen.Dispose()
    $backgroundBrush.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$pngPath = Join-Path $resolvedOutput 'PathEchoLogo.png'
$preview = New-LogoBitmap 512
$preview.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = New-LogoBitmap $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    ,$stream.ToArray()
    $stream.Dispose()
}

$iconPath = Join-Path $resolvedOutput 'PathEcho.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $sizeByte = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
    $writer.Write([byte]$sizeByte)
    $writer.Write([byte]$sizeByte)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$index].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$index].Length
}

foreach ($image in $images) {
    $writer.Write($image)
}

$writer.Dispose()
$file.Dispose()
