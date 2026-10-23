param([string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'), [string]$DeviceName = '\\.\DISPLAY2', [int]$Seconds = 20)
$ErrorActionPreference = 'Stop'
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
$after = @(& powershell -NoProfile -EncodedCommand $encodedQuery)
if ($LASTEXITCODE -ne 0) { throw 'Could not query the restored display arrangement.' }
if (Compare-Object $before $after) { throw 'Display arrangement differs after restoration.' }
Write-Output 'PASS: signal sleep, restore, display bounds and primary monitor preserved.'
