param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
# Parse every repository PowerShell script without executing hardware diagnostics.
$scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' })
foreach ($script in $scripts) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw "PowerShell syntax error in $($script.FullName): $($parseErrors -join '; ')" }
}
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $output
$compiler = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$testOutput = Join-Path $output 'tests'
New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
$testExe = Join-Path $testOutput 'Nulluxion.Tests.exe'
& $compiler /nologo /target:exe /main:Nulluxion.Tests.TestRunner /optimize+ /platform:anycpu /warn:4 /warnaserror+ "/out:$testExe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll @sources @testSources
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Isolated C# tests failed.' }
# A fresh process avoids assembly identity collisions with prior diagnostic runs.
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell)) { throw 'Windows PowerShell 5.1 is required to run the script checks.' }
& $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'hardware-test.ps1') -AssemblyPath (Join-Path $output 'Nulluxion.exe')
if ($LASTEXITCODE -ne 0) { throw 'Hardware capability parser tests failed.' }
& $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'smoke-test.ps1') -AssemblyPath (Join-Path $output 'Nulluxion.exe')
if ($LASTEXITCODE -ne 0) { throw 'Isolated tray smoke tests failed.' }
Write-Output "Checks passed ($($scripts.Count) PowerShell scripts parsed; no physical monitor tests run)."
