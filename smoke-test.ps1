# Copyright (C) 2026 Nulluxion contributors
# SPDX-License-Identifier: GPL-3.0-only

param([string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$overlayType = $assembly.GetType('Nulluxion.Overlay')
$bounds = New-Object System.Drawing.Rectangle -1920,0,1920,1080
$overlay = [Activator]::CreateInstance($overlayType, $flags, $null, @($bounds), $null)
try {
    if ($overlay.Bounds -ne $bounds) { throw 'Overlay does not preserve monitor bounds.' }
    if ($overlay.BackColor.ToArgb() -ne [Drawing.Color]::Black.ToArgb()) { throw 'Overlay is not black.' }
    if (!$overlay.TopMost -or $overlay.ShowInTaskbar) { throw 'Incorrect overlay window configuration.' }
    $parameters = $overlayType.GetProperty('CreateParams', $flags).GetValue($overlay, $null)
    if (($parameters.ExStyle -band 0x08000000) -eq 0) { throw 'Overlay could steal focus.' }
} finally { $overlay.Dispose() }
$type = $assembly.GetType('Nulluxion.TrayApp')
# The factory injects a no-op controller BEFORE construction. It does not read
# settings/recovery, register global shortcuts, display overlays, or power monitors.
$app = $type.GetMethod('CreateForSmokeTest', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @())
try {
    $screens = [Windows.Forms.Screen]::AllScreens.Count
    $type.GetMethod('Toggle', $flags).Invoke($app, @()) | Out-Null
    $active = $type.GetProperty('IsActive', $flags).GetValue($app, $null)
    if ($active -ne ($screens -gt 1)) { throw 'Unexpected blackout state.' }
    $type.GetMethod('Restore', $flags).Invoke($app, @()) | Out-Null
    if ($type.GetProperty('IsActive', $flags).GetValue($app, $null)) { throw 'Restore failed.' }
    $hotkeys = $type.GetField('hotkeys', $flags).GetValue($app)
    $pressed = $hotkeys.GetType().GetField('Pressed', $flags).GetValue($hotkeys)
    $pressed.Invoke(3)
    $overlays = $type.GetField('overlays', $flags).GetValue($app)
    if ($overlays.Count -ne $screens) { throw 'All-screen shortcut did not cover every screen.' }
    $pressed.Invoke(3)
    if ($type.GetProperty('IsActive', $flags).GetValue($app, $null)) { throw 'All-screen toggle did not restore.' }
    $pressed.Invoke(3)
    $pressed.Invoke(2)
    if ($type.GetProperty('IsActive', $flags).GetValue($app, $null)) { throw 'Escape did not restore all screens.' }
    $pressed.Invoke(3)
    $pressed.Invoke(1)
    if ($type.GetProperty('IsActive', $flags).GetValue($app, $null)) { throw 'Original shortcut did not restore all screens.' }
    if ($screens -gt 1) {
        $pressed.Invoke(1)
        $pressed.Invoke(3)
        if ($overlays.Count -ne $screens) { throw 'Switch from other screens to all screens failed.' }
        $pressed.Invoke(2)
    }
    $changed = $type.GetMethod('ChangeShortcut', $flags).Invoke($app, @([uint32]7, [uint32][Windows.Forms.Keys]::B))
    if ($changed) { throw 'All-screen shortcut was allowed as the original shortcut.' }
    Write-Output "PASS: isolated overlay bounds/style, both blackout handlers on $screens monitor(s), repeat-toggle/Escape/original-shortcut restoration, reserved shortcut. No visible overlays or hardware writes."
} finally {
    $type.GetMethod('ExitThreadCore', $flags).Invoke($app, @()) | Out-Null
    $app.Dispose()
}
