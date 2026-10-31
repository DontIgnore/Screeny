# Generates src/Screeny/Assets/screeny.ico: 16/32/48 frames (32bpp DIB) and 256 (PNG).
# Run once: powershell -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 52, 120, 246)), 26
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    # Four corners of the selection frame
    $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object Drawing.Point 36, 100), (New-Object Drawing.Point 36, 36), (New-Object Drawing.Point 100, 36)))
    $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object Drawing.Point 156, 36), (New-Object Drawing.Point 220, 36), (New-Object Drawing.Point 220, 100)))
    $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object Drawing.Point 220, 156), (New-Object Drawing.Point 220, 220), (New-Object Drawing.Point 156, 220)))
    $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object Drawing.Point 100, 220), (New-Object Drawing.Point 36, 220), (New-Object Drawing.Point 36, 156)))
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 59, 48))
    $g.FillEllipse($brush, 92, 92, 72, 72)
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER; height is doubled (XOR + AND masks)
    $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($size * $size * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    # AND mask: all zeros (alpha supplies transparency); rows are aligned to 4 bytes
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskStride * $size)))
    $bw.Flush()
    return $ms.ToArray()
}

$frames = @()
foreach ($s in 16, 32, 48) { $frames += , @{ Size = $s; Bytes = (Get-DibBytes (New-IconBitmap $s)) } }
$png = New-Object System.IO.MemoryStream
(New-IconBitmap 256).Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$frames += , @{ Size = 256; Bytes = $png.ToArray() }

$out = Join-Path $PSScriptRoot '..\src\Screeny\Assets\screeny.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$f.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f.Bytes) }
$bw.Close()
Write-Output "Written $out"
