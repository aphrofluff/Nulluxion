# Copyright (C) 2026 Nulluxion contributors
# SPDX-License-Identifier: GPL-3.0-only

#requires -Version 5.1
param(
    [ValidateNotNullOrEmpty()][string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'),
    [ValidateNotNullOrEmpty()][string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist\ui-preview')
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'UI previews require Windows and .NET Framework 4.8.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this script in a fresh Windows PowerShell process with -NoProfile -STA -File.' }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ($output.TrimEnd('\') -ieq $PSScriptRoot.TrimEnd('\')) { throw 'Choose a generated output directory, such as dist\ui-preview.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$trayType = $assembly.GetType('Nulluxion.TrayApp', $true)
$formType = $assembly.GetType('Nulluxion.SettingsForm', $true)
$report = New-Object 'System.Collections.Generic.List[string]'
$ownedFonts = New-Object 'System.Collections.Generic.List[System.Drawing.Font]'

function Get-ControlTree([Windows.Forms.Control]$Control) {
    $Control
    foreach ($child in $Control.Controls) { Get-ControlTree $child }
}

function Save-ControlImage([Windows.Forms.Control]$Control, [string]$Name) {
    $null = $Control.Handle
    $Control.PerformLayout()
    foreach ($child in @(Get-ControlTree $Control)) { $null = $child.Handle; $child.PerformLayout() }
    $bitmap = New-Object Drawing.Bitmap $Control.Width, $Control.Height
    try {
        $Control.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height))
        $bitmap.Save((Join-Path $output ($Name + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    $report.Add("$Name : $($Control.Width) x $($Control.Height)")
    foreach ($child in @(Get-ControlTree $Control)) {
        if ($child -eq $Control) { continue }
        $text = ($child.Text -replace '\s+', ' ').Trim()
        if ($text.Length -gt 100) { $text = $text.Substring(0, 100) + '...' }
        $parent = $child.Parent
        $outside = $child.Left -lt 0 -or $child.Top -lt 0 -or $child.Right -gt $parent.ClientSize.Width -or $child.Bottom -gt $parent.ClientSize.Height
        $report.Add(('  {0} [{1},{2} {3}x{4}] {5}{6}' -f $child.GetType().Name, $child.Left, $child.Top, $child.Width, $child.Height, $(if ($outside) { 'OUTSIDE PARENT: ' } else { '' }), $text))
    }
}

# This factory injects OverlayOnlyPower before construction. It does not load user
# settings/recovery, register shortcuts, show overlays, or operate monitor power.
$app = $trayType.GetMethod('CreateForSmokeTest', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @())
$icon = [Drawing.Icon]::ExtractAssociatedIcon((Resolve-Path -LiteralPath $AssemblyPath).Path)
if ($null -eq $icon) { $icon = [Drawing.SystemIcons]::Application.Clone() }
try {
    foreach ($scenario in @('default', 'compact', 'long-status', 'simulated-150pct')) {
        $form = [Activator]::CreateInstance($formType, $flags, $null, @($app, $icon), $null)
        try {
            # Never call Show, ShowDialog, or any control action/event handler.
            $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
            $form.Location = New-Object Drawing.Point -32000, -32000
            $form.ShowInTaskbar = $false
            if ($scenario -eq 'compact') {
                $form.MinimumSize = [Drawing.Size]::Empty
                $form.ClientSize = New-Object Drawing.Size ([Math]::Min($form.ClientSize.Width, 760)), ([Math]::Min($form.ClientSize.Height, 560))
            }
            if ($scenario -eq 'long-status') {
                $trayType.GetField('HardwareStatus', $flags).SetValue($app, 'Restoration requires attention. A monitor could not be matched safely after the display layout changed. Restore your monitors and arrangement manually, then retry restoration. Saved recovery records remain available. Monitor: DISPLAY-EXAMPLE-1234567890; connection: DisplayPort; the previous operation was interrupted before confirmation.')
                $formType.GetMethod('UpdateHardwareStatus', $flags).Invoke($form, @()) | Out-Null
            }
            if ($scenario -eq 'simulated-150pct') {
                # A geometry/font stress preview, not a real per-monitor DPI test.
                $controls = @(Get-ControlTree $form)
                $fonts = @($controls | ForEach-Object { $_.Font })
                $form.Scale((New-Object Drawing.SizeF 1.5, 1.5))
                for ($i = 0; $i -lt $controls.Count; $i++) {
                    $scaledFont = New-Object Drawing.Font $fonts[$i].FontFamily, ($fonts[$i].Size * 1.5), $fonts[$i].Style, $fonts[$i].Unit
                    $ownedFonts.Add($scaledFont)
                    $controls[$i].Font = $scaledFont
                }
            }
            Save-ControlImage $form $scenario
        } finally { $form.Dispose() }
        $trayType.GetField('HardwareStatus', $flags).SetValue($app, 'Overlay test: hardware operations disabled.')
    }
    $tray = $trayType.GetField('tray', $flags).GetValue($app)
    $menu = $tray.ContextMenuStrip
    $menu.Size = $menu.GetPreferredSize([Drawing.Size]::Empty)
    Save-ControlImage $menu 'tray-menu'
    foreach ($item in $menu.Items) { $report.Add("  Menu: $($item.Text); enabled=$($item.Enabled)") }
    $report.Add('No visible windows, control input, user settings writes, global shortcuts, overlays, or physical monitor operations were used.')
    $report.Add('simulated-150pct scales geometry and fonts; it does not replace testing real Windows DPI changes. OUTSIDE PARENT can include content reachable through scrolling.')
    $report | Set-Content -LiteralPath (Join-Path $output 'geometry.txt') -Encoding UTF8
    Write-Output "Rendered isolated settings scenarios and tray menu to $output"
} finally {
    $app.Dispose()
    $icon.Dispose()
    foreach ($font in $ownedFonts) { $font.Dispose() }
}
