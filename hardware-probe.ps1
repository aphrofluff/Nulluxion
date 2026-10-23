# Copyright (C) 2026 Nulluxion contributors
# SPDX-License-Identifier: GPL-3.0-only

param([string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'))
$ErrorActionPreference = 'Stop'
# Read-only diagnostics. Never construct HardwarePower: its constructor starts recovery.
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$type = $assembly.GetType('Nulluxion.HardwarePower', $true)
$static = [Reflection.BindingFlags]'NonPublic,Static'
$instance = [Reflection.BindingFlags]'NonPublic,Instance'
$monitors = $type.GetMethod('Enumerate', $static).Invoke($null, @())
try {
    foreach ($monitor in $monitors) {
        $handle = $monitor.GetType().GetField('Handle', $instance).GetValue($monitor)
        $name = $monitor.GetType().GetField('Name', $instance).GetValue($monitor)
        $lengthArgs = [object[]]@($handle, [uint32]0)
        $lengthOk = $type.GetMethod('GetCapabilitiesStringLength', $static).Invoke($null, $lengthArgs)
        $caps = ''
        $capsOk = $false
        if ($lengthOk -and $lengthArgs[1] -gt 0 -and $lengthArgs[1] -le 65536) {
            $buffer = New-Object Text.StringBuilder ([int]$lengthArgs[1])
            $capsOk = $type.GetMethod('CapabilitiesRequestAndCapabilitiesReply', $static).Invoke($null, [object[]]@($handle, $buffer.PSObject.BaseObject, [uint32]$lengthArgs[1]))
            if ($capsOk) { $caps = $buffer.ToString() }
        }
        $powerArgs = [object[]]@($handle, [byte]0xD6, [int]0, [uint32]0, [uint32]0)
        $powerOk = $type.GetMethod('GetVCPFeatureAndVCPFeatureReply', $static).Invoke($null, $powerArgs)
        [pscustomobject]@{
            Device = $name
            CapabilitiesRead = $capsOk
            PowerOffAdvertised = $type.GetMethod('SupportsPowerOff', $static).Invoke($null, [object[]]@($caps))
            PowerRead = $powerOk
            CurrentPower = $(if ($powerOk) { $powerArgs[3] } else { $null })
            Capabilities = $caps
        }
    }
} finally {
    foreach ($monitor in $monitors) { ([IDisposable]$monitor).Dispose() }
}
