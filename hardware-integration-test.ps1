# Copyright (C) 2026 Nulluxion contributors
# SPDX-License-Identifier: GPL-3.0-only

param([string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'), [switch]$AllowMonitorChanges)
$ErrorActionPreference = 'Stop'
if (-not $AllowMonitorChanges) { throw 'This manual diagnostic changes physical monitor power and display configuration. Read README.md, close Nulluxion, then opt in with -AllowMonitorChanges.' }
if (Get-Process -Name Nulluxion,Blackout -ErrorAction SilentlyContinue) { throw 'Close Nulluxion/Blackout before running physical diagnostics.' }
# This test intentionally requests physical monitor sleep, then restores in finally.
# Close the tray app first to avoid global shortcut conflicts.
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
public static class PowerIntegrationTest
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static void Pump(int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(15); }
    }
    static bool HardwareState(object app, Type type, string property)
    {
        object hardware = type.GetField("hardware", Flags).GetValue(app);
        return (bool)hardware.GetType().GetProperty(property, Flags).GetValue(hardware, null);
    }
    static bool SleepRequested(object app, Type type)
    {
        return (bool)type.GetField("displaySleepRequested", Flags).GetValue(app);
    }
    static void WaitForRestore(object app, Type type)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!HardwareState(app, type, "IsRestored") && DateTime.UtcNow < deadline) Pump(50);
        if (!HardwareState(app, type, "IsRestored")) throw new Exception("Hardware restoration did not complete within 15 seconds.");
        if (SleepRequested(app, type)) throw new Exception("Sleep state was not cleared.");
    }
    public static void Run(string path)
    {
        Assembly assembly = Assembly.LoadFrom(path);
        Type type = assembly.GetType("Nulluxion.TrayApp", true);
        object app = Activator.CreateInstance(type, Flags, null, new object[] { true }, null);
        try
        {
            object settings = type.GetField("Settings", Flags).GetValue(app);
            settings.GetType().GetField("TurnOffBacklights").SetValue(settings, true);
            // Give recovery and synchronization-context dispatch a chance to settle.
            Pump(250);
            type.GetMethod("ToggleAll", Flags).Invoke(app, null);
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!HardwareState(app, type, "AllScreensPoweredOff") && !SleepRequested(app, type) && DateTime.UtcNow < deadline) Pump(50);
            bool poweredOff = HardwareState(app, type, "AllScreensPoweredOff");
            bool requested = SleepRequested(app, type);
            if (poweredOff == requested) throw new Exception("Expected exactly one all-screen outcome: hardware off or Windows display sleep fallback.");
            type.GetMethod("Restore", Flags).Invoke(app, null);
            WaitForRestore(app, type);
            // Cancellation before the delayed sleep must prevent a late power-off.
            type.GetMethod("ToggleAll", Flags).Invoke(app, null);
            type.GetMethod("Restore", Flags).Invoke(app, null);
            Pump(1200);
            if ((bool)type.GetField("displaySleepRequested", Flags).GetValue(app)) throw new Exception("Cancelled sleep ran late.");
            WaitForRestore(app, type);
            if (HardwareState(app, type, "AllScreensPoweredOff")) throw new Exception("Cancelled hardware power-off ran late.");
            if (Screen.AllScreens.Length > 1)
            {
                type.GetMethod("Toggle", Flags).Invoke(app, null);
                Pump(6000);
                Console.WriteLine(type.GetField("HardwareStatus", Flags).GetValue(app));
                type.GetMethod("Restore", Flags).Invoke(app, null);
                WaitForRestore(app, type);
            }
            Console.WriteLine("PASS: all-screen hardware off or Windows sleep fallback/restore, cancellation, selective hardware/fallback path. Physical light output requires observation.");
        }
        finally { type.GetMethod("ExitThreadCore", Flags).Invoke(app, null); ((IDisposable)app).Dispose(); }
        Type hardwareType = assembly.GetType("Nulluxion.HardwarePower", true);
        object hardware = Activator.CreateInstance(hardwareType, true);
        hardwareType.GetField("Status", Flags).SetValue(hardware, new Action<string>(Console.WriteLine));
        try
        {
            string[] targets = Array.ConvertAll(Screen.AllScreens, delegate(Screen screen) { return screen.DeviceName; });
            hardwareType.GetMethod("SetTargets", Flags).Invoke(hardware, new object[] { targets });
            Thread.Sleep(5000);
            hardwareType.GetMethod("Restore", Flags).Invoke(hardware, null);
            Thread.Sleep(2000);
        }
        finally { ((IDisposable)hardware).Dispose(); }
    }
}
'@
[PowerIntegrationTest]::Run((Resolve-Path -LiteralPath $AssemblyPath).Path)
