# 生成 app.ico（16/32/48 三尺寸）与 app.png（64px）——DDL拯救器 应用图标
# 图案：白色时钟表盘 + 蓝色圆环指针 + 红色"死线"点，透明背景
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"
$outDir = "D:\DDLaunch\DDLSaver\Resources"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function New-ClockBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $blue  = [System.Drawing.Color]::FromArgb(255, 60, 110, 235)
    $blueDark = [System.Drawing.Color]::FromArgb(255, 40, 80, 180)
    $red   = [System.Drawing.Color]::FromArgb(255, 235, 80, 80)

    $cx = [double]$size / 2.0
    $cy = [double]$size / 2.0
    $cr = [double]$size * 0.46          # 外圈半径
    $inner = [double]$size * 0.38       # 表盘半径

    # 蓝色外圈（实心圆）
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($blue)), [single]($cx - $cr), [single]($cy - $cr), [single](2 * $cr), [single](2 * $cr))
    # 白色表盘
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($white)), [single]($cx - $inner), [single]($cy - $inner), [single](2 * $inner), [single](2 * $inner))

    # 指针
    $wPen = [single][Math]::Max(1.0, $size / 14.0)
    $penM = New-Object System.Drawing.Pen($blueDark, $wPen)
    $penM.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($penM, [single]$cx, [single]$cy, [single]$cx, [single]($cy - $inner * 0.60))
    $ang = [single](240.0 * [Math]::PI / 180.0)
    $g.DrawLine($penM, [single]$cx, [single]$cy, [single]($cx + $inner * 0.50 * [Math]::Cos($ang)), [single]($cy + $inner * 0.50 * [Math]::Sin($ang)))

    # 顶部红点（死线标记）
    $pd = [single][Math]::Max(1.5, $size / 10.0)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($red)), [single]($cx - $pd / 2.0), [single]($cy - $cr + $pd * 0.75), $pd, $pd)

    $g.Dispose()
    return $bmp
}

function Get-ArgbaPixels($bmp) {
    # 返回从下往上（BMP 行序）的 BGRA 字节数组
    $size = $bmp.Width
    $stride = $size * 4
    $data = New-Object byte[] ($stride * $size)
    for ($y = 0; $y -lt $size; $y++) {
        $srcY = $size - 1 - $y
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $srcY)
            $data[$y * $stride + $x * 4 + 0] = [byte]$c.B
            $data[$y * $stride + $x * 4 + 1] = [byte]$c.G
            $data[$y * $stride + $x * 4 + 2] = [byte]$c.R
            $data[$y * $stride + $x * 4 + 3] = [byte]$c.A
        }
    }
    return $data
}

$sizes = @(16, 32, 48)
$iconDatas = @()
foreach ($s in $sizes) {
    $bmp = New-ClockBitmap $s
    $pixels = Get-ArgbaPixels $bmp
    # AND 掩码：每行 ceil(s/8) 字节，且每行 4 字节对齐
    $andRowBytes = [int][Math]::Ceiling($s / 8.0)
    $andStride = $andRowBytes
    if (($andStride % 4) -ne 0) { $andStride = $andStride + (4 - ($andStride % 4)) }
    $andMask = New-Object byte[] ($andStride * $s)
    $bmi = New-Object byte[] 40
    [BitConverter]::GetBytes([int]40).CopyTo($bmi, 0)
    [BitConverter]::GetBytes([int]$s).CopyTo($bmi, 4)
    [BitConverter]::GetBytes([int]($s * 2)).CopyTo($bmi, 8)
    [BitConverter]::GetBytes([int]1).CopyTo($bmi, 12)
    [BitConverter]::GetBytes([int]32).CopyTo($bmi, 14)
    [BitConverter]::GetBytes([int]0).CopyTo($bmi, 16)
    $body = New-Object byte[] ($bmi.Length + $pixels.Length + $andMask.Length)
    $bmi.CopyTo($body, 0)
    $pixels.CopyTo($body, $bmi.Length)
    $andMask.CopyTo($body, $bmi.Length + $pixels.Length)
    $iconDatas += ,@{ Size = $s; Data = $body }
    $bmp.Dispose()
}

$count = $iconDatas.Count
$headerSize = 6 + 16 * $count
$totalSize = $headerSize
foreach ($d in $iconDatas) { $totalSize += $d.Data.Length }
Write-Output ("expected total: {0}" -f $totalSize)

$ico = New-Object byte[] $totalSize
[BitConverter]::GetBytes([System.UInt16]0).CopyTo($ico, 0)
[BitConverter]::GetBytes([System.UInt16]1).CopyTo($ico, 2)
[BitConverter]::GetBytes([System.UInt16]$count).CopyTo($ico, 4)
$offset = $headerSize
for ($i = 0; $i -lt $count; $i++) {
    $d = $iconDatas[$i]
    $e = 6 + 16 * $i
    [BitConverter]::GetBytes([byte]$d.Size).CopyTo($ico, $e)
    [BitConverter]::GetBytes([byte]$d.Size).CopyTo($ico, $e + 1)
    $ico[$e + 2] = 0
    $ico[$e + 3] = 0
    [BitConverter]::GetBytes([System.UInt16]1).CopyTo($ico, $e + 4)
    [BitConverter]::GetBytes([System.UInt16]32).CopyTo($ico, $e + 6)
    [BitConverter]::GetBytes([int]$d.Data.Length).CopyTo($ico, $e + 8)
    [BitConverter]::GetBytes([int]$offset).CopyTo($ico, $e + 12)
    $d.Data.CopyTo($ico, $offset)
    $offset += $d.Data.Length
}
[System.IO.File]::WriteAllBytes("$outDir\app.ico", $ico)

$bmp64 = New-ClockBitmap 64
$bmp64.Save("$outDir\app.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp64.Dispose()

Write-Output ("app.ico bytes: {0}, app.png bytes: {1}" -f $ico.Length, (Get-Item "$outDir\app.png").Length)
