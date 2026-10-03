# Purpose: render the XiControl tray-settings SVG as a high-DPI Windows tray/taskbar icon.
# Dependencies: PowerShell 7 on Windows, System.Drawing, Svg.dll from an existing PC Manager build.
# Outputs: assets/app.ico and a PNG preview in .test-environment/.
# Command: pwsh -NoProfile -File tools/render-app-icon.ps1
param(
    [string]$SourceSvg = (Join-Path $PSScriptRoot '..\assets\native-icon.svg'),
    [string]$OutputIcon = (Join-Path $PSScriptRoot '..\assets\app.ico')
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = (Resolve-Path -LiteralPath $SourceSvg).Path
$assembly = @((Join-Path $projectRoot 'app\Svg.dll'), (Join-Path $projectRoot 'bin\Release\net8.0-windows\Svg.dll')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$assembly) { throw 'Build PC Manager first so Svg.dll is available.' }
Add-Type -Path $assembly
Add-Type -AssemblyName System.Drawing
$xml = [xml](Get-Content -LiteralPath $source -Raw)
$xml.DocumentElement.SelectSingleNode('//*[local-name()="path"]').SetAttribute('fill', '#FFFFFF')
$xml.DocumentElement.SelectSingleNode('//*[local-name()="path"]').SetAttribute('stroke', '#25282d')
$xml.DocumentElement.SelectSingleNode('//*[local-name()="path"]').SetAttribute('stroke-width', '0.75')
$svg = [Svg.SvgDocument]::Open($xml)
$smallXml = [xml]$xml.OuterXml
$smallXml.DocumentElement.SelectSingleNode('//*[local-name()="path"]').SetAttribute('stroke-width', '1.15')
$smallSvg = [Svg.SvgDocument]::Open($smallXml)
$frames = [Collections.Generic.List[object]]::new()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.Clear([Drawing.Color]::Transparent)
        # Task Manager generally requests the tiny ICO frame. Give it a larger,
        # stronger silhouette while leaving the already legible tray sizes alone.
        $glyphSize = if ($size -le 24) { $size } else { [Math]::Max(11, [int][Math]::Round($size * 0.86)) }
        $glyph = if ($size -le 24) { $smallSvg.Draw($glyphSize, $glyphSize) } else { $svg.Draw($glyphSize, $glyphSize) }
        try {
            $offset = [int][Math]::Floor(($size - $glyphSize) / 2)
            $graphics.DrawImage($glyph, $offset, $offset, $glyphSize, $glyphSize)
        } finally { $glyph.Dispose() }
        $bytes = [IO.MemoryStream]::new()
        try { $bitmap.Save($bytes, [Drawing.Imaging.ImageFormat]::Png); $frames.Add([pscustomobject]@{ Size=$size; Bytes=$bytes.ToArray() }) }
        finally { $bytes.Dispose() }
        if ($size -eq 256) {
            $preview = Join-Path $projectRoot '.test-environment\app-icon-preview.png'
            $bitmap.Save($preview, [Drawing.Imaging.ImageFormat]::Png)
        }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$destination = [IO.Path]::GetFullPath($OutputIcon)
$file = [IO.File]::Create($destination)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $writer.Write([byte]($frame.Size % 256)); $writer.Write([byte]($frame.Size % 256))
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
$check = [Drawing.Icon]::new($destination)
try { if ($check.Width -lt 16) { throw 'The generated icon was not readable.' } }
finally { $check.Dispose() }
Write-Host "Rendered $destination from $source ($($frames.Count) sizes)."
