param([string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;
public static class BenqShortcutTest
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static void Pump(int ms) { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(20); } }
    static void Wait(Func<bool> condition, string error) { DateTime end = DateTime.UtcNow.AddSeconds(15); while (!condition() && DateTime.UtcNow < end) Pump(50); if (!condition()) throw new Exception(error); }
    public static void Run(string path)
    {
        Assembly assembly = Assembly.LoadFrom(path);
        Type type = assembly.GetType("Nulluxion.TrayApp", true);
        object app = Activator.CreateInstance(type, Flags, null, new object[] { true }, null);
        Point originalCursor = Cursor.Position;
        try
        {
            object settings = type.GetField("Settings", Flags).GetValue(app);
            settings.GetType().GetField("TurnOffBacklights").SetValue(settings, true);
            settings.GetType().GetField("UsePointer").SetValue(settings, true);
            object hardware = type.GetField("hardware", Flags).GetValue(app);
            object display = hardware.GetType().GetField("displaySleep", Flags).GetValue(hardware);
            Func<bool> sleeping = delegate { return (bool)display.GetType().GetProperty("IsActive", Flags).GetValue(display, null); };
            Func<bool> restored = delegate { return (bool)hardware.GetType().GetProperty("IsRestored", Flags).GetValue(hardware, null); };
            object hotkeys = type.GetField("hotkeys", Flags).GetValue(app);
            Action<int> pressed = (Action<int>)hotkeys.GetType().GetField("Pressed", Flags).GetValue(hotkeys);
            Screen keep = Array.Find(Screen.AllScreens, delegate(Screen screen) { return screen.DeviceName == @"\\.\DISPLAY1"; });
            if (keep == null || Screen.AllScreens.Length != 2) throw new Exception("Expected this setup's two monitors.");
            Cursor.Position = new Point(keep.Bounds.Left + 100, keep.Bounds.Top + 100);
            pressed(1);
            Wait(sleeping, "BenQ signal-sleep path did not activate.");
            Pump(4000);
            if (!(bool)type.GetProperty("IsActive", Flags).GetValue(app, null) || !sleeping()) throw new Exception("Display-change event cancelled blackout.");
            pressed(1);
            Wait(restored, "Original shortcut failed to restore the BenQ.");
            Pump(1000);
            Cursor.Position = new Point(keep.Bounds.Left + 100, keep.Bounds.Top + 100);
            pressed(1);
            try { Wait(sleeping, "Second BenQ blackout failed."); }
            catch { Console.WriteLine(type.GetField("HardwareStatus", Flags).GetValue(app)); throw; }
            pressed(2);
            Wait(restored, "Escape failed to restore the BenQ.");
            Console.WriteLine("PASS: Ctrl+Alt+B handler enters BenQ signal sleep, survives display-change events, and restores via shortcut and Escape.");
        }
        finally
        {
            type.GetMethod("ExitThreadCore", Flags).Invoke(app, null);
            ((IDisposable)app).Dispose();
            Cursor.Position = originalCursor;
        }
    }
}
'@
[BenqShortcutTest]::Run((Resolve-Path -LiteralPath $AssemblyPath).Path)
