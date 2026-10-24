# Compila o Cronômetro Glass usando o compilador C# que já vem no Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$icon = Join-Path $root 'src\cronometro.ico'

if (-not (Test-Path $icon)) {
    # Gera o ícone (uma bolha de vidro com ponteiro) em PNG 256px dentro de um .ico.
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 256, 256
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $rect = New-Object System.Drawing.Rectangle 16, 16, 224, 224
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 70, 80, 120)), ([System.Drawing.Color]::FromArgb(255, 20, 22, 36)), 45
    $g.FillEllipse($bg, $rect)
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 30, 22, 196, 110), ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90
    $g.FillEllipse($hl, 34, 24, 188, 110)
    $g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 255, 255, 255)), 6), $rect)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 48, 209, 88)), 16
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, 128, 128, 128, 66)
    $g.DrawLine($pen, 128, 128, 170, 150)
    $g.FillEllipse([System.Drawing.Brushes]::White, 116, 116, 24, 24)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $png = $ms.ToArray()
    $fs = [System.IO.File]::Create($icon)
    $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$png.Length); $w.Write([uint32]22)
    $w.Write($png)
    $w.Close()
}

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu `
    "/out:$root\Stopwatch.exe" "/win32icon:$icon" `
    "/r:$wpf\PresentationFramework.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" "$root\src\Cronometro.cs"
if ($LASTEXITCODE -ne 0) { throw "Falha na compilação" }
Write-Host "OK -> $root\Stopwatch.exe"
