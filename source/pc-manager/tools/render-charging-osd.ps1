# Purpose: compose Xiaomi's bundled battery SVGs into dark charging and unplugged OSD cards.
# Dependencies: PowerShell 7 on Windows, Svg.dll from an existing PC Manager build.
# Outputs: www/osd/Charging*_Dark.png and OnBattery*_Dark.png.
# Command: pwsh -NoProfile -File tools/render-charging-osd.ps1
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly = @((Join-Path $projectRoot 'app\Svg.dll'), (Join-Path $projectRoot 'bin\Release\net8.0-windows\Svg.dll')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$assembly) { throw 'Build PC Manager first so Svg.dll is available.' }
Add-Type -Path $assembly
Add-Type -AssemblyName System.Drawing
foreach ($state in 'charging','normal') { foreach ($level in 10,20,30,40,50,60,70,80,90,100) {
    $source = Join-Path $projectRoot "www\osd\battery\battery${level}_${state}.svg"
    if (!(Test-Path -LiteralPath $source)) { throw "Missing Xiaomi charging artwork: $source" }
    # Svg.dll paints the OEM alpha mask as a visible gray rectangle. Its paths
    # already carry their own colors, so omit only that mask for this render.
    $artwork = Get-Content -LiteralPath $source -Raw
    if ($state -eq 'normal') { $artwork = $artwork.Replace('#666666', '#CBD2DD').Replace('#333333', '#F5F7FA') }
    $xml = [xml]$artwork
    $mask = $xml.DocumentElement.SelectSingleNode('//*[local-name()="mask"]')
    if ($mask) { [void]$mask.ParentNode.RemoveChild($mask) }
    $group = $xml.DocumentElement.SelectSingleNode('//*[local-name()="g"]')
    if ($group) { $group.RemoveAttribute('mask') }
    $svg = [Svg.SvgDocument]::Open($xml)
    $glyph = $svg.Draw(280, 151)
    $bitmap = [Drawing.Bitmap]::new(400, 400, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $labelFont = [Drawing.Font]::new('Segoe UI Semibold', 33, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $center = [Drawing.StringFormat]::new()
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([Drawing.Color]::Transparent)
        $shape.AddArc(0, 0, 72, 72, 180, 90)
        $shape.AddArc(328, 0, 72, 72, 270, 90)
        $shape.AddArc(328, 328, 72, 72, 0, 90)
        $shape.AddArc(0, 328, 72, 72, 90, 90)
        $shape.CloseFigure()
        $background = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(31, 34, 39))
        try { $graphics.FillPath($background, $shape) } finally { $background.Dispose() }
        $graphics.DrawImage($glyph, 60, 88, 280, 151)
        $center.Alignment = [Drawing.StringAlignment]::Center
        $center.LineAlignment = [Drawing.StringAlignment]::Center
        $label = if ($state -eq 'charging') { 'Charging' } else { 'On battery' }
        $graphics.DrawString($label, $labelFont, [Drawing.Brushes]::White, [Drawing.RectangleF]::new(0, 264, 400, 64), $center)
        $prefix = if ($state -eq 'charging') { 'Charging' } else { 'OnBattery' }
        $output = Join-Path $projectRoot "www\osd\${prefix}${level}_Dark.png"
        $bitmap.Save($output, [Drawing.Imaging.ImageFormat]::Png)
        Write-Host "Rendered $output"
    } finally { $center.Dispose(); $labelFont.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $glyph.Dispose() }
} }
