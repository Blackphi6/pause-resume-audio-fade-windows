# アプリアイコンを作る: 大きなPNG(Mac版 AppIcon.icns の 1024px 層など)から、余白を切り詰めて
#   assets/icon-256.png, assets/icon-128.png, src/PauseResumeAudioFade/Assets/app.ico (16-256px) を生成する。
#   使い方: powershell -File tools\make-icon.ps1 -Source <1024px.png>
param([Parameter(Mandatory)][string]$Source)
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path "$PSScriptRoot\.."
$src  = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))

# 透明でない部分の外接矩形を求める(macOSアイコンは周囲に透明な余白がある)
$minX=$src.Width; $minY=$src.Height; $maxX=0; $maxY=0
for ($y=0; $y -lt $src.Height; $y+=2) { for ($x=0; $x -lt $src.Width; $x+=2) {
    if ($src.GetPixel($x,$y).A -gt 8) { if($x -lt $minX){$minX=$x}; if($x -gt $maxX){$maxX=$x}; if($y -lt $minY){$minY=$y}; if($y -gt $maxY){$maxY=$y} } } }
$side = [Math]::Max($maxX-$minX, $maxY-$minY) + 1
$cx = ($minX+$maxX)/2; $cy = ($minY+$maxY)/2
$crop = New-Object System.Drawing.Rectangle ([int]($cx-$side/2)), ([int]($cy-$side/2)), $side, $side
Write-Host "crop: $crop"

function Render([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0,0,$n,$n), $crop, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose(); $bmp
}
function PngBytes($bmp) { $m = New-Object IO.MemoryStream; $bmp.Save($m, [System.Drawing.Imaging.ImageFormat]::Png); return ,$m.ToArray() }

# 従来形式(BMP/DIB 32bit + ANDマスク)のICOエントリ。小さいサイズはこの形式にしないと、一部のローダが読めない
function DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0,0,$w,$h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $raw = New-Object byte[] ($w*$h*4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)
    $m = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $m
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h*2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32]($w*$h*4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $h-1; $y -ge 0; $y--) { $bw.Write($raw, $y*$w*4, $w*4) }          # 下の行から
    $maskRow = [int]([Math]::Ceiling($w/32.0))*4
    $bw.Write((New-Object byte[] ($maskRow*$h)))                                         # ANDマスク(全0=アルファを使用)
    $bw.Flush(); return ,$m.ToArray()
}
New-Item -ItemType Directory -Force "$root\assets" | Out-Null
foreach ($n in 256,128) { $b = Render $n; $b.Save("$root\assets\icon-$n.png", [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose() }

# ICO(PNG形式のエントリ。Vista以降で全サイズ対応)
$sizes = 16,20,24,32,40,48,64,128,256
$pngs  = New-Object System.Collections.Generic.List[byte[]]
foreach ($n in $sizes) { $b = Render $n; if ($n -ge 256) { $pngs.Add([byte[]](PngBytes $b)) } else { $pngs.Add([byte[]](DibBytes $b)) }; $b.Dispose() }
$ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $d = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
New-Item -ItemType Directory -Force "$root\src\PauseResumeAudioFade\Assets" | Out-Null
[IO.File]::WriteAllBytes("$root\src\PauseResumeAudioFade\Assets\app.ico", $ms.ToArray())
$src.Dispose()
Write-Host ("app.ico: {0:N0} bytes, sizes {1}" -f $ms.Length, ($sizes -join ','))
