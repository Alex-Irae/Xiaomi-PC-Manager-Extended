# Purpose: encode the supplied Xiaomi-inspired silver logo as a Windows multi-resolution icon.
# Dependencies: Windows PowerShell/.NET System.Drawing; no external packages.
# Output: assets/app.ico, used by apphost, tray, launcher and installer.
# Command: powershell -NoProfile -File native/build-icon.ps1 (also called by build.ps1).
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$images=@()
foreach($size in 16,24,32,48,64,128,256){
    $bitmap=New-Object Drawing.Bitmap($size,$size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $source=[Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'assets\xiaomi-silver.png'))
    try{$graphics.DrawImage($source,0,0,$size,$size)}finally{$source.Dispose()}
    $stream=New-Object IO.MemoryStream
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $images+=@{size=$size;bytes=$stream.ToArray()}
    $stream.Dispose();$graphics.Dispose();$bitmap.Dispose()
}
$output=[IO.File]::Create((Join-Path $PSScriptRoot 'assets\app.ico'))
$writer=New-Object IO.BinaryWriter($output)
try{
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
    $offset=6+16*$images.Count
    foreach($item in $images){
        $dimension=if($item.size -eq 256){0}else{$item.size}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$item.bytes.Length);$writer.Write([uint32]$offset)
        $offset+=$item.bytes.Length
    }
    foreach($item in $images){$writer.Write([byte[]]$item.bytes)}
}finally{$writer.Dispose();$output.Dispose()}
Write-Output 'PASS: Xiaomi silver icon encoded at 16 through 256 pixels.'
