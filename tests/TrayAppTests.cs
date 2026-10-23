// Copyright (C) 2026 Nulluxion contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
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
                Check(!app.ChangeShortcut(7, (uint)Keys.B), "fixed shortcut remains reserved without writing settings");
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
                var settings = new Settings { Modifiers = 2, Key = (uint)Keys.F8, UsePointer = true, TurnOffBacklights = false };
                settings.Save(path);
                Settings loaded = Settings.Load(path);
                Check(loaded.Modifiers == 2 && loaded.Key == (uint)Keys.F8 && loaded.UsePointer && !loaded.TurnOffBacklights, "settings round trip");
                settings.Key = (uint)Keys.F9; settings.Save(path);
                Check(Settings.Load(path).Key == (uint)Keys.F9 && !File.Exists(path + ".tmp"), "settings atomically replace existing file");
                File.WriteAllText(path, "<Settings><Modifiers>7</Modifiers><Key>66</Key><UsePointer>true</UsePointer><TurnOffBacklights>false</TurnOffBacklights></Settings>");
                loaded = Settings.Load(path);
                Check(loaded.Modifiers == 3 && loaded.Key == (uint)Keys.B && loaded.UsePointer && !loaded.TurnOffBacklights, "reserved persisted shortcut resets without discarding preferences");
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
