param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'Windows .NET Framework 4.x C# compiler is required. Enable/install .NET Framework 4.8, then rerun build.ps1 on Windows.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
Add-Type -AssemblyName System.Drawing
# A solid black image, including every icon pixel.
$bitmap = New-Object System.Drawing.Bitmap 32,32
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([System.Drawing.Color]::Black)
$graphics.Dispose()
$png = New-Object System.IO.MemoryStream
$bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Save((Join-Path $output 'nulluxion.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
$bytes = $png.ToArray()
$png.Dispose()
$iconPath = Join-Path $output 'nulluxion.ico'
$stream = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]32); $writer.Write([byte]32); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]22)
$writer.Write($bytes); $writer.Dispose()
$sources = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw 'No production C# sources found in the project root.' }
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /warn:4 /warnaserror+ "/out:$output\Nulluxion.exe" "/win32icon:$iconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built $output\Nulluxion.exe"
