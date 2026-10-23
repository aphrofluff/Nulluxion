# Copyright (C) 2026 Nulluxion contributors
# SPDX-License-Identifier: GPL-3.0-only

param(
    [ValidateSet(2,3)][uint32]$StandbyMode = 2,
    [string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'),
    [switch]$AllowMonitorChanges
)
$ErrorActionPreference = 'Stop'
if (-not $AllowMonitorChanges) { throw 'This experimental manual diagnostic sends physical monitor power commands. Read README.md, close Nulluxion, then opt in with -AllowMonitorChanges.' }
if (Get-Process -Name Nulluxion,Blackout -ErrorAction SilentlyContinue) { throw 'Close Nulluxion/Blackout before running physical diagnostics.' }
# Explicit hardware diagnostic: target only EX240N, restore in finally, journal first.
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$type = $assembly.GetType('Nulluxion.HardwarePower', $true)
$static = [Reflection.BindingFlags]'Static,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,NonPublic'
$journal = Join-Path $env:LOCALAPPDATA 'Blackout\power-recovery.txt'
$monitors = $type.GetMethod('Enumerate', $static).Invoke($null, @())
$matched = $false
try {
    foreach ($monitor in $monitors) {
        $handle = $monitor.GetType().GetField('Handle', $instance).GetValue($monitor)
        $name = $monitor.GetType().GetField('Name', $instance).GetValue($monitor)
        $identity = $monitor.GetType().GetField('Identity', $instance).GetValue($monitor)
        $lengthArgs = [object[]]@($handle, [uint32]0)
        if (!$type.GetMethod('GetCapabilitiesStringLength', $static).Invoke($null, $lengthArgs)) { continue }
        if ($lengthArgs[1] -lt 1 -or $lengthArgs[1] -gt 65536) { continue }
        $buffer = New-Object Text.StringBuilder ([int]$lengthArgs[1])
        $capsOk = $type.GetMethod('CapabilitiesRequestAndCapabilitiesReply', $static).Invoke($null, [object[]]@($handle, $buffer.PSObject.BaseObject, [uint32]$lengthArgs[1]))
        if (!$capsOk -or $buffer.ToString() -notmatch 'model\(EX240N\)') { continue }
        $matched = $true
        if ([string]::IsNullOrEmpty($identity)) { throw 'Cannot safely journal this monitor: stable identity is unavailable.' }
        $before = [object[]]@($handle, [byte]0xD6, [int]0, [uint32]0, [uint32]0)
        if (!$type.GetMethod('GetVCPFeatureAndVCPFeatureReply', $static).Invoke($null, $before) -or $before[3] -ne 1) { throw 'BenQ is not confirmed on.' }
        $existing = @(if (Test-Path -LiteralPath $journal) { [IO.File]::ReadAllLines($journal) })
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($journal)) | Out-Null
        [IO.File]::WriteAllLines($journal, [string[]]@($existing + $identity | Select-Object -Unique))
        try {
            $accepted = $type.GetMethod('SetVCPFeature', $static).Invoke($null, [object[]]@($handle, [byte]0xD6, $StandbyMode))
            Start-Sleep -Seconds 5
            $during = [object[]]@($handle, [byte]0xD6, [int]0, [uint32]0, [uint32]0)
            $read = $type.GetMethod('GetVCPFeatureAndVCPFeatureReply', $static).Invoke($null, $during)
            Write-Output "EX240N standby=$StandbyMode accepted=$accepted readable=$read reportedPower=$($during[3])"
        } finally {
            $wake = $type.GetMethod('SetVCPFeature', $static).Invoke($null, [object[]]@($handle, [byte]0xD6, [uint32]1))
            Start-Sleep -Seconds 2
            $after = [object[]]@($handle, [byte]0xD6, [int]0, [uint32]0, [uint32]0)
            $readOn = $type.GetMethod('GetVCPFeatureAndVCPFeatureReply', $static).Invoke($null, $after)
            Write-Output "EX240N wake=$wake readable=$readOn reportedPower=$($after[3])"
            if ($wake -and $readOn -and $after[3] -eq 1) {
                [IO.File]::WriteAllLines($journal, [string[]]$existing)
            } else { throw 'BenQ did not confirm software wake. Recovery journal retained; physical power button may be required.' }
        }
    }
    if (!$matched) { throw 'BenQ EX240N is not connected or its capabilities are unavailable.' }
} finally {
    foreach ($monitor in $monitors) { ([IDisposable]$monitor).Dispose() }
}
