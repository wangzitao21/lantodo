# Regenerate platform assets from the checked-in SVG. Requires Windows PowerShell/WPF.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
[xml]$svg = Get-Content -LiteralPath (Join-Path $projectRoot 'assets/icon/lantodo.svg') -Raw
$rect = $svg.svg.rect
$mark = $svg.svg.path
$stops = $svg.svg.defs.linearGradient.stop
$first = $stops[0].GetAttribute('stop-color')
$last = $stops[1].GetAttribute('stop-color')
$geometry = $mark.d
$thickness = $mark.GetAttribute('stroke-width')
$xaml = @"
<DrawingImage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <DrawingImage.Drawing><DrawingGroup>
    <GeometryDrawing Brush="Transparent" Geometry="M0,0H96V96H0Z"/>
    <GeometryDrawing>
      <GeometryDrawing.Brush><LinearGradientBrush StartPoint="0,0" EndPoint="1,1"><GradientStop Color="$first" Offset="0"/><GradientStop Color="$last" Offset="1"/></LinearGradientBrush></GeometryDrawing.Brush>
      <GeometryDrawing.Geometry><RectangleGeometry Rect="$($rect.x),$($rect.y),$($rect.width),$($rect.height)" RadiusX="$($rect.rx)" RadiusY="$($rect.rx)"/></GeometryDrawing.Geometry>
    </GeometryDrawing>
    <GeometryDrawing Geometry="$geometry"><GeometryDrawing.Pen><Pen Brush="$($mark.stroke)" Thickness="$thickness" StartLineCap="Round" EndLineCap="Round" LineJoin="Round"/></GeometryDrawing.Pen></GeometryDrawing>
  </DrawingGroup></DrawingImage.Drawing>
</DrawingImage>
"@
$resource = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + "`n" + $xaml.Replace('<DrawingImage xmlns=', '<DrawingImage x:Key="BrandMark" xmlns=') + "`n</ResourceDictionary>`n"
[IO.File]::WriteAllText((Join-Path $projectRoot 'src/LanTodo.Windows/Assets/Brand.xaml'), $resource)
$drawing = [Windows.Markup.XamlReader]::Parse($xaml)
function Get-IconPng([int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawImage($drawing, [Windows.Rect]::new(0, 0, $size, $size))
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() }
    finally { $stream.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $projectRoot 'assets/icon/lantodo.png'), (Get-IconPng 512))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Get-IconPng $_) })
$file = [IO.File]::Create((Join-Path $projectRoot 'src/LanTodo.Windows/Assets/lantodo.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose() }
$background = "M28,4 H68 Q92,4 92,28 V68 Q92,92 68,92 H28 Q4,92 4,68 V28 Q4,4 28,4 Z"
$vector = @"
<vector xmlns:android="http://schemas.android.com/apk/res/android" xmlns:aapt="http://schemas.android.com/aapt" android:width="96dp" android:height="96dp" android:viewportWidth="96" android:viewportHeight="96">
  <path android:pathData="$background">
    <aapt:attr name="android:fillColor"><gradient android:startX="4" android:startY="4" android:endX="92" android:endY="92" android:startColor="$first" android:endColor="$last" android:type="linear"/></aapt:attr>
  </path>
  <path android:pathData="$geometry" android:fillColor="@android:color/transparent" android:strokeColor="$($mark.stroke)" android:strokeWidth="$thickness" android:strokeLineCap="round" android:strokeLineJoin="round"/>
</vector>
"@
[IO.File]::WriteAllText((Join-Path $projectRoot 'src/LanTodo.Android/Resources/drawable/ic_launcher.xml'), $vector + "`n")
$foreground = @"
<vector xmlns:android="http://schemas.android.com/apk/res/android" android:width="108dp" android:height="108dp" android:viewportWidth="108" android:viewportHeight="108">
  <group android:scaleX="0.8" android:scaleY="0.8" android:translateX="15.6" android:translateY="15.6">
    <path android:pathData="$geometry" android:fillColor="@android:color/transparent" android:strokeColor="$($mark.stroke)" android:strokeWidth="$thickness" android:strokeLineCap="round" android:strokeLineJoin="round"/>
  </group>
</vector>
"@
[IO.File]::WriteAllText((Join-Path $projectRoot 'src/LanTodo.Android/Resources/drawable/ic_launcher_foreground.xml'), $foreground + "`n")
Write-Output 'Generated PNG, multi-resolution ICO, WPF drawing, and Android vectors.'
