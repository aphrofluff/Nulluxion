// Copyright (C) 2026 Nulluxion contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;

namespace Nulluxion.Tests
{
    internal static class TrayAppTests
    {
        static int assertions;
        static void Check(bool value, string message)
        {
            assertions++;
            if (!value) throw new Exception("Tray: " + message);
        }
        static void Pump() { for (int i = 0; i < 4; i++) Application.DoEvents(); }

        internal static void Run()
        {
            CheckInputQuietPeriod();
            CheckDeferredSleep();
            CheckDirectHardwareSleep();
            CheckSettings();
            CheckShortcutTransactions();
            CheckShortcutCaptureDispatch();
            CheckDisposalAndRestoreFailures();
            FakePower power = new FakePower();
            int sleep = 0, wake = 0;
            var settings = new Settings { TurnOffBacklights = true };
            var app = new TrayApp(true, power, settings, false, delegate(bool off) { if (off) sleep++; else wake++; });
            try
            {
                Check(power.RestoreCalls == 0, "test construction must not start native recovery");
                Check(app.HardwareStatus == power.LastStatus, "initial status must be available even before subscription");
                app.ToggleAll();
                Check(app.IsActive && power.AllCalls == 1 && power.RestoreCalls == 0, "all-screen mode tries direct hardware control first");
                app.RequestDisplaySleep();
                Check(sleep == 0, "unresolved recovery must prevent display sleep");
                CompleteFallback(power);
                app.RequestDisplaySleep(); app.RequestDisplaySleep();
                Check(sleep == 1, "completed recovery allows exactly one sleep request");
                app.Restore();
                Check(!app.IsActive && wake == 1, "restore removes overlays and wakes display");
                app.ToggleAll();
                power.CompletePreparation(false); Pump();
                power.CompleteRecovery();
                app.Restore(); Pump(); app.RequestDisplaySleep();
                Check(sleep == 1 && !app.IsActive, "late recovery callback cannot sleep cancelled blackout");
                app.ToggleAll(); app.Toggle();
                Check(!app.IsActive, "original shortcut restores all-screen mode");
                app.ToggleAll(); app.ToggleAll();
                Check(!app.IsActive, "repeated all-screen shortcut restores");
                app.ToggleAll(); power.ExternalChange(); Pump();
                Check(!app.IsActive, "external topology change removes overlays");
                Check(!app.ChangeShortcut(7, (uint)Keys.B), "duplicate all-screen shortcut is rejected without writing settings");
                power.Publish("Recovery requires attention"); Pump();
                Check(app.HardwareStatus == "Recovery requires attention", "recovery status reaches UI");
                settings.TurnOffBacklights = false;
                app.ToggleAll(); power.CompleteRecovery(); Pump(); app.RequestDisplaySleep();
                Check(sleep == 1, "disabled hardware never requests display sleep");
                app.Restore();
            }
            finally { app.ExitThread(); app.Dispose(); }
            Check(power.Disposed, "exit disposes injected controller");
            power.ExternalChange(); power.Publish("late shutdown callback"); Pump();

            using (TrayApp isolated = TrayApp.CreateForSmokeTest())
            {
                isolated.ToggleAll();
                Check(isolated.IsActive, "isolated factory can exercise overlays without hardware");
                isolated.ExitThread();
            }
            Console.WriteLine("PASS: " + assertions + " isolated tray assertions.");
        }

        static void CheckInputQuietPeriod()
        {
            var input = new DisplaySleepInput();
            Check(!input.Observe(0, 0, true), "held shortcut prevents immediate sleep");
            Check(!input.Observe(5000, 0, true), "held modifier prevents sleep beyond old 800ms delay");
            Check(!input.Observe(10000, 0, false), "release begins quiet period even when last-input timestamp is unchanged");
            Check(!input.Observe(10799, 0, false), "sleep waits full quiet interval");
            Check(input.Observe(10800, 0, false), "released and quiet input permits sleep");
            Check(!input.Observe(10900, 10900, false), "mouse movement restarts quiet interval");
            Check(!input.Observe(11699, 10900, false), "recent movement postpones sleep");
            Check(input.Observe(11700, 10900, false), "sleep follows quiet mouse input");
            Check(!input.Observe(11800, 100, false), "backward input timestamps also restart quiet interval");
            Check(!input.Observe(12600, 100, true), "held mouse button prevents sleep despite unchanged timestamp");
            input.Reset();
            Check(!input.Observe(uint.MaxValue - 399, 42, false), "fresh observation after unreadable input defers sleep");
            Check(!input.Observe(399, 42, false), "tick wrap does not shorten quiet interval");
            Check(input.Observe(400, 42, false), "quiet interval handles tick wrap");
        }

        static void CheckSettings()
        {
            string directory = Path.Combine(Path.GetTempPath(), "Nulluxion-settings-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "settings.xml");
            try
            {
                Check(Settings.Load(path).Key == (uint)Keys.B, "missing settings use defaults");
                var settings = new Settings { Modifiers = 2, Key = (uint)Keys.F8, AllModifiers = 5, AllKey = (uint)Keys.F9, UsePointer = true, TurnOffBacklights = false };
                settings.Save(path);
                Settings loaded = Settings.Load(path);
                Check(loaded.Modifiers == 2 && loaded.Key == (uint)Keys.F8 && loaded.UsePointer && !loaded.TurnOffBacklights, "settings round trip");
                Check(loaded.AllModifiers == 5 && loaded.AllKey == (uint)Keys.F9, "all-screen shortcut persists independently");
                settings.Key = (uint)Keys.F9; settings.Save(path);
                Check(Settings.Load(path).Key == (uint)Keys.F9 && !File.Exists(path + ".tmp"), "settings atomically replace existing file");
                File.WriteAllText(path, "<Settings><Modifiers>7</Modifiers><Key>123</Key><UsePointer>true</UsePointer><TurnOffBacklights>false</TurnOffBacklights></Settings>");
                loaded = Settings.Load(path);
                Check(loaded.Modifiers == 3 && loaded.Key == (uint)Keys.B && loaded.UsePointer && !loaded.TurnOffBacklights, "reserved persisted shortcut resets without discarding preferences");
                File.WriteAllText(path, "<Settings><Modifiers>2</Modifiers><Key>119</Key><UsePointer>true</UsePointer></Settings>");
                loaded = Settings.Load(path);
                Check(loaded.Modifiers == 2 && loaded.Key == (uint)Keys.F8 && loaded.AllModifiers == 7 && loaded.AllKey == (uint)Keys.B && loaded.UsePointer,
                    "legacy settings preserve focus preference and add default all-screen shortcut");
                File.WriteAllText(path, "<Settings><Modifiers>7</Modifiers><Key>66</Key></Settings>");
                loaded = Settings.Load(path);
                Check(loaded.Modifiers == 7 && loaded.Key == (uint)Keys.B && loaded.AllModifiers == 3 && loaded.AllKey == (uint)Keys.B,
                    "legacy default collision preserves focus and selects a distinct all-screen fallback");
                File.WriteAllText(path, "<Settings><Modifiers>2</Modifiers><Key>119</Key><AllModifiers>2</AllModifiers><AllKey>119</AllKey></Settings>");
                loaded = Settings.Load(path);
                Check(loaded.Key == (uint)Keys.F8 && loaded.AllModifiers == 7 && loaded.AllKey == (uint)Keys.B, "duplicate persisted pair keeps focus and repairs all-screen");
                File.WriteAllText(path, "<Settings><Modifiers>3</Modifiers><Key>123</Key><AllModifiers>3</AllModifiers><AllKey>66</AllKey></Settings>");
                loaded = Settings.Load(path);
                Check(loaded.Modifiers == 7 && loaded.AllModifiers == 3, "invalid focus repair preserves valid all-screen preference");
                File.WriteAllText(path, "<Settings><Modifiers>3</Modifiers><Key>66</Key><AllModifiers>0</AllModifiers><AllKey>123</AllKey></Settings>");
                loaded = Settings.Load(path);
                Check(loaded.AllModifiers == 7 && loaded.AllKey == (uint)Keys.B, "invalid all-screen preference resets safely");
                loaded.AllModifiers = loaded.Modifiers; loaded.AllKey = loaded.Key;
                bool duplicateRejected = false;
                try { loaded.Save(path); } catch (InvalidOperationException) { duplicateRejected = true; }
                Check(duplicateRejected, "persistence rejects duplicate pair");
                File.WriteAllText(path, "<!DOCTYPE Settings [<!ENTITY value 'false'>]><Settings><TurnOffBacklights>&value;</TurnOffBacklights></Settings>");
                Check(Settings.Load(path).TurnOffBacklights, "DTD settings rejected");
                File.WriteAllText(path, "<Settings><!--" + new string('x', 65536) + "--><TurnOffBacklights>false</TurnOffBacklights></Settings>");
                Check(Settings.Load(path).TurnOffBacklights, "oversized settings rejected");
                File.WriteAllText(path, "<Settings><Key>");
                Check(Settings.Load(path).Key == (uint)Keys.B, "truncated settings use defaults");
                Check(!Settings.IsValidShortcut(0, (uint)Keys.A) && !Settings.IsValidShortcut(16, (uint)Keys.A) &&
                    !Settings.IsValidShortcut(3, 0) && !Settings.IsValidShortcut(3, uint.MaxValue), "invalid keys and modifier flags rejected");
                Check(!Settings.IsValidShortcut(3, (uint)Keys.Escape) && !Settings.IsValidShortcut(3, (uint)Keys.F12) &&
                    !Settings.IsValidShortcut(3, (uint)Keys.ControlKey), "escape, debugger key and standalone modifier rejected");
                Check(Settings.IsValidShortcut(0, (uint)Keys.F1) && Settings.IsValidShortcut(3, (uint)Keys.B), "supported shortcuts accepted");
            }
            finally { Directory.Delete(directory, true); }
        }

        static void CheckShortcutTransactions()
        {
            var keys = new FakeShortcuts();
            var settings = new Settings();
            int saves = 0;
            bool failSave = false;
            using (var app = new TrayApp(true, new FakePower(), settings, false, delegate { }, null,
                keys.Register, keys.Unregister, delegate { if (failSave) throw new IOException("Simulated settings write failure"); saves++; }))
            {
                Check(keys.Has(1, 3, Keys.B) && keys.Has(3, 7, Keys.B), "both initial shortcuts register through the injected backend");
                Check(app.ChangeShortcut(2, (uint)Keys.F8) && settings.Key == (uint)Keys.F8 && keys.Has(1, 2, Keys.F8) && saves == 1,
                    "focus shortcut registration and persistence commit together");
                Check(app.ChangeAllShortcut(6, (uint)Keys.F9) && settings.AllKey == (uint)Keys.F9 && keys.Has(3, 6, Keys.F9) && saves == 2,
                    "all-screen shortcut registration and persistence commit together");
                Check(app.ChangeShortcut(7, (uint)Keys.B), "old all-screen default is available after that shortcut changes");
                int calls = keys.RegisterCalls;
                Check(!app.ChangeAllShortcut(7, (uint)Keys.B) && keys.RegisterCalls == calls && saves == 3,
                    "duplicates are rejected before registration or saving");
                Check(!app.ChangeAllShortcut(0, (uint)Keys.F12) && !app.ChangeShortcut(2, (uint)Keys.Escape), "reserved keys rejected for both actions");
                foreach (bool all in new[] { false, true })
                {
                    uint oldModifiers = all ? settings.AllModifiers : settings.Modifiers;
                    uint oldKey = all ? settings.AllKey : settings.Key;
                    int id = all ? 3 : 1;
                    keys.Blocked.Add(FakeShortcuts.Code(2, (uint)Keys.F6));
                    bool changed = all ? app.ChangeAllShortcut(2, (uint)Keys.F6) : app.ChangeShortcut(2, (uint)Keys.F6);
                    Check(!changed && keys.Has(id, oldModifiers, (Keys)oldKey) && saves == 3, "OS conflict restores the previous registration without saving");
                    keys.Blocked.Clear();
                    failSave = true;
                    bool failed = false;
                    try { if (all) app.ChangeAllShortcut(1, (uint)Keys.F7); else app.ChangeShortcut(1, (uint)Keys.F7); }
                    catch (IOException) { failed = true; }
                    failSave = false;
                    Check(failed && keys.Has(id, oldModifiers, (Keys)oldKey) && saves == 3 &&
                        (all ? settings.AllModifiers : settings.Modifiers) == oldModifiers && (all ? settings.AllKey : settings.Key) == oldKey,
                        "save failure rolls back both settings and native registration");
                }
                ToolStripMenuItem focusItem = (ToolStripMenuItem)typeof(TrayApp).GetField("toggle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app);
                ToolStripMenuItem allItem = (ToolStripMenuItem)typeof(TrayApp).GetField("toggleAll", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app);
                Check(focusItem.Text.Contains(app.ShortcutText(false)) && allItem.Text.Contains(app.ShortcutText(true)), "tray labels show both configured shortcuts");
                app.ToggleAll();
                Check(allItem.Text.StartsWith("Restore") && allItem.Text.Contains(app.ShortcutText(true)), "active tray action retains configured shortcut");
                app.Restore();
                Check(allItem.Text.StartsWith("Black out") && allItem.Text.Contains(app.ShortcutText(true)), "restored tray action retains configured shortcut");
            }
            Check(keys.Active.Count == 0, "cleanup releases both injected registrations");

            keys = new FakeShortcuts();
            keys.Blocked.Add(FakeShortcuts.Code(7, (uint)Keys.B));
            using (var app = new TrayApp(true, new FakePower(), new Settings(), false, delegate { }, null,
                keys.Register, keys.Unregister, delegate { }))
            {
                Check(!keys.Active.ContainsKey(3) && keys.Has(1, 3, Keys.B), "an initial all-screen conflict leaves focus registration available");
                keys.Blocked.Clear();
                Check(app.ChangeAllShortcut(7, (uint)Keys.B) && keys.Has(3, 7, Keys.B), "same configured key can retry a failed initial registration");
                keys.Blocked.Add(FakeShortcuts.Code(2, (uint)Keys.F6));
                keys.Blocked.Add(FakeShortcuts.Code(7, (uint)Keys.B));
                bool reported = false;
                try { app.ChangeAllShortcut(2, (uint)Keys.F6); }
                catch (InvalidOperationException error) { reported = error.Message.Contains("previous shortcut"); }
                Check(reported && app.Settings.AllModifiers == 7 && app.Settings.AllKey == (uint)Keys.B,
                    "rollback registration failure is reported without overwriting saved preference");
                keys.Blocked.Clear();
                Check(app.ChangeAllShortcut(7, (uint)Keys.B) && keys.Has(3, 7, Keys.B), "failed rollback can be repaired in settings");
            }
        }

        sealed class FakeShortcuts
        {
            internal readonly Dictionary<int, string> Active = new Dictionary<int, string>();
            internal readonly HashSet<string> Blocked = new HashSet<string>();
            internal int RegisterCalls;
            internal static string Code(uint modifiers, uint key) { return (modifiers & 15) + ":" + key; }
            internal bool Register(int id, uint modifiers, uint key)
            {
                RegisterCalls++;
                string code = Code(modifiers, key);
                if (Blocked.Contains(code)) return false;
                foreach (var entry in Active) if (entry.Value == code && entry.Key != id) return false;
                Active[id] = code;
                return true;
            }
            internal void Unregister(int id) { Active.Remove(id); }
            internal bool Has(int id, uint modifiers, Keys key)
            { string value; return Active.TryGetValue(id, out value) && value == Code(modifiers, (uint)key); }
        }

        static void CheckShortcutCaptureDispatch()
        {
            var power = new FakePower();
            int captured = 0;
            using (var app = new TrayApp(true, power, new Settings(), false, delegate { }))
            {
                app.ShortcutCapture = delegate(int id) { captured++; return true; };
                app.DispatchHotkey(1);
                app.DispatchHotkey(3);
                Check(captured == 2 && !app.IsActive && power.AllCalls == 0 && power.RestoreCalls == 0,
                    "both registered actions are consumed during shortcut capture without monitor operations");
                app.DispatchHotkey(2);
                Check(captured == 2 && power.RestoreCalls == 1, "Escape bypasses capture and always restores");
                app.ShortcutCapture = delegate { return false; };
                app.DispatchHotkey(3);
                Check(app.IsActive && power.AllCalls == 1, "registered all-screen action works outside shortcut capture");
                app.DispatchHotkey(1);
                Check(!app.IsActive && power.RestoreCalls == 2, "registered focus action restores outside shortcut capture");
            }
        }

        static void CheckDisposalAndRestoreFailures()
        {
            FakePower power = new FakePower();
            var app = new TrayApp(true, power, new Settings(), false, delegate { });
            app.ToggleAll();
            app.Dispose();
            Check(power.Disposed && !app.IsActive, "Dispose alone restores monitors and releases controller");
            int restores = power.RestoreCalls;
            app.Dispose(); app.ExitThread();
            Check(power.RestoreCalls == restores, "cleanup is idempotent");
            power.Publish("after disposal"); power.ExternalChange(); Pump();

            power = new FakePower();
            app = new TrayApp(true, power, new Settings(), false, delegate(bool sleep) { if (!sleep) throw new InvalidOperationException("Simulated wake failure"); });
            try
            {
                app.ToggleAll(); CompleteFallback(power); app.RequestDisplaySleep();
                restores = power.RestoreCalls;
                bool failed = false;
                try { app.Restore(); } catch (InvalidOperationException) { failed = true; }
                Check(failed && !app.IsActive && power.RestoreCalls == restores + 1, "Windows wake exception still restores hardware and removes overlays");
                app.ToggleAll(); power.ThrowOnRestore = true;
                failed = false;
                try { app.Restore(); } catch (InvalidOperationException) { failed = true; }
                Check(failed && !app.IsActive, "controller exception cannot strand black overlays");
            }
            finally { power.ThrowOnRestore = false; app.Dispose(); }
        }

        static void CheckDeferredSleep()
        {
            FakePower power = new FakePower();
            bool quiet = false;
            int sleep = 0;
            var app = new TrayApp(true, power, new Settings(), false,
                delegate(bool off) { if (off) sleep++; }, delegate { return quiet; });
            try
            {
                app.ToggleAll(); CompleteFallback(power);
                app.RequestDisplaySleep(); app.RequestDisplaySleep();
                Check(sleep == 0 && app.IsActive, "held input defers power command while overlays remain");
                quiet = true; app.RequestDisplaySleep();
                Check(sleep == 1, "quiet input completes pending sleep");
                quiet = false; app.RequestDisplaySleep();
                quiet = true; app.RequestDisplaySleep();
                Check(sleep == 1, "later wake input never starts a repeated sleep loop");
                app.Restore();
                quiet = false;
                app.ToggleAll(); CompleteFallback(power); app.RequestDisplaySleep();
                app.Restore(); quiet = true; Pump(); app.RequestDisplaySleep();
                Check(sleep == 1 && !app.IsActive, "Escape/restore cancels sleep pending key release");
                quiet = false;
                app.ToggleAll(); CompleteFallback(power); app.RequestDisplaySleep();
                app.Settings.TurnOffBacklights = false; quiet = true; app.RequestDisplaySleep();
                Check(sleep == 1, "disabling hardware cancels deferred input wait");
            }
            finally { app.ExitThread(); app.Dispose(); }
        }

        static void CompleteFallback(FakePower power)
        {
            power.CompletePreparation(false); Pump();
            power.CompleteRecovery(); Pump();
        }

        static void CheckDirectHardwareSleep()
        {
            FakePower power = new FakePower();
            int windowsCommands = 0;
            var app = new TrayApp(true, power, new Settings(), false, delegate { windowsCommands++; });
            try
            {
                app.ToggleAll(); power.CompletePreparation(true); Pump();
                app.RequestDisplaySleep();
                Check(power.AllCalls == 1 && windowsCommands == 0 && app.IsActive, "supported monitors stay hardware-off without Windows sleep");
                power.CompleteRecovery(); Pump(); app.RequestDisplaySleep();
                Check(windowsCommands == 0, "unrelated recovery completion cannot start Windows sleep in hardware mode");
                app.Restore();
                Check(power.RestoreCalls == 1 && !app.IsActive && windowsCommands == 0, "Escape restores hardware without Windows power command");
                app.ToggleAll(); power.CompletePreparation(false); // Queue old fallback event.
                app.Restore(); app.ToggleAll(); Pump();
                Check(power.RestoreCalls == 2, "stale preparation cannot restore a newer all-screen request");
                power.CompletePreparation(false); Pump();
                Check(power.RestoreCalls == 3, "fallback first restores any partly powered-off monitors");
                app.RequestDisplaySleep();
                Check(windowsCommands == 0, "fallback waits for hardware restoration");
                power.CompleteRecovery(); Pump(); app.RequestDisplaySleep();
                Check(windowsCommands == 1, "unsupported setup falls back only after restoration");
            }
            finally { app.ExitThread(); app.Dispose(); }
        }

        sealed class FakePower : IPowerController
        {
            public event Action<string> Status;
            public event Action Restored;
            public event Action ExternalDisplayChanged;
            public event Action AllScreensPrepared;
            public bool AllScreensPoweredOff { get; private set; }
            public bool NeedsDisplaySleepFallback { get; private set; }
            public bool IsRestored { get; private set; }
            public bool HasUnresolvedRecovery { get { return !IsRestored; } }
            public string LastStatus { get { return "Test recovery pending"; } }
            internal int RestoreCalls;
            internal int AllCalls;
            internal bool Disposed;
            internal bool ThrowOnRestore;
            public void SetTargets(string[] names) { IsRestored = false; AllScreensPoweredOff = NeedsDisplaySleepFallback = false; }
            public void SetAllTargets(string[] names) { AllCalls++; SetTargets(names); }
            public void Restore() { RestoreCalls++; if (ThrowOnRestore) throw new InvalidOperationException("Simulated controller failure"); SetTargets(new string[0]); }
            public void NotifyTopologyChanged() { ExternalChange(); }
            public void RetireRecovery() { CompleteRecovery(); }
            public void Dispose() { Disposed = true; }
            internal void CompleteRecovery() { IsRestored = true; if (Restored != null) Restored(); }
            internal void CompletePreparation(bool supported)
            {
                AllScreensPoweredOff = supported; NeedsDisplaySleepFallback = !supported;
                if (AllScreensPrepared != null) AllScreensPrepared();
            }
            internal void ExternalChange() { if (ExternalDisplayChanged != null) ExternalDisplayChanged(); }
            internal void Publish(string message) { if (Status != null) Status(message); }
        }
    }
}
