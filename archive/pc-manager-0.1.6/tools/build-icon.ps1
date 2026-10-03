# Purpose: generate the native app.ico matching assets/app.svg with Windows drawing APIs.
# Dependencies: Windows PowerShell and System.Drawing. No packages or external image tools.
# Output: assets/app.ico with 16-, 32- and 64-pixel PNG frames for tray, window and executable icons.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$frames = foreach ($size in @(16,32,64)) {
    $bitmap = New-Object Drawing.Bitmap $size,$size
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.ScaleTransform([single]($size/64),[single]($size/64))
    $pen = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#111111')),4
    foreach ($stroke in @(@{color='#111111';width=5},@{color='#ffffff';width=3})) {
        $pen.Color = [Drawing.ColorTranslator]::FromHtml($stroke.color); $pen.Width = $stroke.width
        foreach ($tube in @(@{y=10;knob=49},@{y=35;knob=15})) {
            $outline = New-Object Drawing.Drawing2D.GraphicsPath
            try {
                $outline.AddArc([single]4,[single]$tube.y,[single]19,[single]19,[single]90,[single]180)
                $outline.AddArc([single]41,[single]$tube.y,[single]19,[single]19,[single]270,[single]180)
                $outline.CloseFigure(); $graphics.DrawPath($pen,$outline)
                $graphics.DrawEllipse($pen,[single]($tube.knob-5),[single]($tube.y+4.5),[single]10,[single]10)
            } finally { $outline.Dispose() }
        }
    }
    $stream = New-Object IO.MemoryStream
    try { $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); @{size=$size;bytes=$stream.ToArray()} }
    finally { $stream.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
$output = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\app.ico'
$writer = New-Object IO.BinaryWriter ([IO.File]::Create($output))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $writer.Write([byte]$frame.size); $writer.Write([byte]$frame.size); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.bytes) }
} finally { $writer.Dispose() }
Write-Host "Created $output"
