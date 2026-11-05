Add-Type -AssemblyName System.Drawing

$url = 'https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcRazGQM3lNOQvtZIE_ReOnLnMfKmew28fxWX8owwSRlPw&s=10'
$client = New-Object System.Net.WebClient
$data = $client.DownloadData($url)
$ms = New-Object System.IO.MemoryStream(,$data)
$img = [System.Drawing.Image]::FromStream($ms)

$minSize = [Math]::Min($img.Width, $img.Height)
$bmp = New-Object System.Drawing.Bitmap($minSize, $minSize)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddEllipse(0, 0, $minSize, $minSize)
$g.SetClip($path)
$g.DrawImage($img, 0, 0, $minSize, $minSize)
$g.Dispose()
$path.Dispose()

# Save as PNG first
$pngPath = 'D:\PROJECT\Git Trending\SoncaAudioInspector\Assets\app_icon_round.png'
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# Convert to ICO using a simple ICO header format
$icoPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\app_icon.ico'
$fs = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)

# Write ICONDIR
$bw.Write([short]0)
$bw.Write([short]1)
$bw.Write([short]1)

# Write ICONDIRENTRY
$bw.Write([byte]255) # Width (0 means 256)
$bw.Write([byte]255) # Height
$bw.Write([byte]0)   # Color count
$bw.Write([byte]0)   # Reserved
$bw.Write([short]1)  # Color planes
$bw.Write([short]32) # Bits per pixel

$pngStream = New-Object System.IO.MemoryStream
$bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()
$bw.Write([int]$pngBytes.Length) # Image size
$bw.Write([int]22) # Offset of image data
$bw.Write($pngBytes)

$bw.Close()
$fs.Dispose()
$pngStream.Dispose()
$bmp.Dispose()
$img.Dispose()
$ms.Dispose()

Write-Host "Icon successfully created!"
