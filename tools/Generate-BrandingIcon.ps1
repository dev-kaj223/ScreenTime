<#
.SYNOPSIS
    Regenerates ScreenTime.ico from the single WPF branding resource on Windows.
.DESCRIPTION
    Run with Windows PowerShell in STA mode. No external image tool or network is used.
    -Verify checks the committed export without modifying it.
#>
param([switch]$Verify)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'src/TimeGuard.App/Branding/ScreenTimeBranding.xaml'
$destination = Join-Path $repo 'src/TimeGuard.App/Branding/ScreenTime.ico'
$dictionary = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($source))
$image = $dictionary['ScreenTimeBrandImage']
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.DrawImage($image, [Windows.Rect]::new(0, 0, $size, $size))
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $png = [IO.MemoryStream]::new()
    try { $encoder.Save($png); $frames += ,$png.ToArray() } finally { $png.Dispose() }
}
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $writer.Flush()
    $bytes = $stream.ToArray()
    if ($Verify) {
        if (-not (Test-Path -LiteralPath $destination) -or
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($destination)) -cne [Convert]::ToBase64String($bytes)) {
            throw 'ScreenTime.ico differs from the shared branding resource. Run tools/Generate-BrandingIcon.ps1.'
        }
        Write-Output 'ScreenTime.ico matches the shared vector at all nine sizes.'
    } else {
        [IO.File]::WriteAllBytes($destination, $bytes)
        Write-Output "Generated $destination ($($sizes -join ', ') px)."
    }
} finally { $writer.Dispose(); $stream.Dispose() }
