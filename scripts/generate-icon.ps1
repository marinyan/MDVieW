# Render the vector source into PNGs and a multi-resolution Windows icon.
# Run on Windows with PowerShell in STA mode (pwsh -STA -File scripts/generate-icon.ps1).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskAssets = Join-Path $taskRoot 'src/MDVieW/Assets'
[xml]$taskSvg = Get-Content -LiteralPath (Join-Path $taskAssets 'AppIcon.svg') -Raw
$taskRect = $taskSvg.DocumentElement.SelectSingleNode('*[local-name()="rect"]')
$taskPaths = $taskSvg.DocumentElement.SelectNodes('*[local-name()="path"]')
$taskFrames = [System.Collections.Generic.List[object]]::new()

foreach ($taskSize in @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512)) {
    $taskVisual = [System.Windows.Media.DrawingVisual]::new()
    $taskDrawing = $taskVisual.RenderOpen()
    try {
        $taskScale = $taskSize / 256.0
        $taskDrawing.PushTransform([System.Windows.Media.ScaleTransform]::new($taskScale, $taskScale))
        $taskFill = [System.Windows.Media.BrushConverter]::new().ConvertFromString($taskRect.GetAttribute('fill'))
        $taskBounds = [System.Windows.Rect]::new([double]$taskRect.x, [double]$taskRect.y, [double]$taskRect.width, [double]$taskRect.height)
        $taskDrawing.DrawRoundedRectangle($taskFill, $null, $taskBounds, [double]$taskRect.rx, [double]$taskRect.rx)
        foreach ($taskPath in $taskPaths) {
            $taskGeometry = [System.Windows.Media.Geometry]::Parse($taskPath.GetAttribute('d'))
            $taskFill = [System.Windows.Media.BrushConverter]::new().ConvertFromString($taskPath.GetAttribute('fill'))
            $taskDrawing.DrawGeometry($taskFill, $null, $taskGeometry)
        }
        $taskDrawing.Pop()
    }
    finally { $taskDrawing.Close() }
    $taskBitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($taskSize, $taskSize, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    $taskEncoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
    $taskStream = [System.IO.MemoryStream]::new()
    try { $taskEncoder.Save($taskStream); $taskBytes = $taskStream.ToArray() }
    finally { $taskStream.Dispose() }
    if ($taskSize -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $taskAssets 'AppIcon.png'), $taskBytes) }
    if ($taskSize -eq 512) { [System.IO.File]::WriteAllBytes((Join-Path $taskAssets 'AppIcon-preview.png'), $taskBytes) }
    else { $taskFrames.Add(@{ Size = $taskSize; Bytes = $taskBytes }) }
}

$taskIconPath = Join-Path $taskAssets 'AppIcon.ico'
$taskFile = [System.IO.File]::Create($taskIconPath)
$taskWriter = [System.IO.BinaryWriter]::new($taskFile)
try {
    $taskWriter.Write([UInt16]0)
    $taskWriter.Write([UInt16]1)
    $taskWriter.Write([UInt16]$taskFrames.Count)
    $taskOffset = 6 + 16 * $taskFrames.Count
    foreach ($taskFrame in $taskFrames) {
        $taskDimension = if ($taskFrame.Size -eq 256) { 0 } else { $taskFrame.Size }
        $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([byte]0)
        $taskWriter.Write([UInt16]1)
        $taskWriter.Write([UInt16]32)
        $taskWriter.Write([UInt32]$taskFrame.Bytes.Length)
        $taskWriter.Write([UInt32]$taskOffset)
        $taskOffset += $taskFrame.Bytes.Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame.Bytes) }
}
finally { $taskWriter.Dispose(); $taskFile.Dispose() }
Write-Output "Generated icon: $taskIconPath ($($taskFrames.Count) sizes)"
