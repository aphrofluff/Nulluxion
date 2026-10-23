# Copyright (C) 2026 Nulluxion contributors
# SPDX-License-Identifier: GPL-3.0-only

param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'),
    [string]$DeviceName = '\\.\DISPLAY2',
    [ValidateRange(1, 60)][int]$Seconds = 20,
    [switch]$AllowMonitorChanges
)
$ErrorActionPreference = 'Stop'
if (-not $AllowMonitorChanges) { throw 'This manual diagnostic changes physical display configuration. Read README.md, close Nulluxion, then opt in with -AllowMonitorChanges.' }
if (Get-Process -Name Nulluxion,Blackout -ErrorAction SilentlyContinue) { throw 'Close Nulluxion/Blackout before running physical diagnostics.' }
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$type = $assembly.GetType('Nulluxion.DisplaySleep', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$sleep = [Activator]::CreateInstance($type, $true)
$before = @([Windows.Forms.Screen]::AllScreens | ForEach-Object { "$($_.DeviceName):$($_.Bounds):$($_.Primary)" } | Sort-Object)
try {
    $ok = $type.GetMethod('Sleep', $flags).Invoke($sleep, [object[]]@(,[string[]]@($DeviceName)))
    if (!$ok) { throw $type.GetProperty('LastError', $flags).GetValue($sleep, $null) }
    Write-Output "Signal suspended for $DeviceName; waiting $Seconds seconds before automatic restore."
    Start-Sleep -Seconds $Seconds
} finally {
    $restored = $type.GetMethod('Restore', $flags).Invoke($sleep, @())
    if (!$restored) { throw $type.GetProperty('LastError', $flags).GetValue($sleep, $null) }
    Write-Output 'Saved display configuration restored.'
}
# Screen.AllScreens caches the topology until it processes a display-change notification.
# Query a fresh PowerShell process instead to compare the live post-restore arrangement.
$query = 'Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.Screen]::AllScreens | ForEach-Object { "$($_.DeviceName):$($_.Bounds):$($_.Primary)" } | Sort-Object'
$encodedQuery = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($query))
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$after = @(& $windowsPowerShell -NoProfile -EncodedCommand $encodedQuery)
if ($LASTEXITCODE -ne 0) { throw 'Could not query the restored display arrangement.' }
if (Compare-Object $before $after) { throw 'Display arrangement differs after restoration.' }
Write-Output 'PASS: signal sleep, restore, display bounds and primary monitor preserved.'
