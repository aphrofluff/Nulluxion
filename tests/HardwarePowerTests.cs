using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nulluxion;

internal static class HardwarePowerTests
{
    static int assertions;
    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    static void Await(Func<bool> condition, string message)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < end) Thread.Sleep(5);
        Check(condition(), message);
    }
    sealed class Backend : HardwarePower.IBackend
    {
        internal volatile bool Present = true, FailWake, Signal, ThrowOnce, NoCaps, ThrowAlways;
        internal int Off, Wake, Released, Enumerations, Count = 1;
        internal string Identity = "v2:test";
        internal Action OnCapabilities;
        internal bool TwoLogical, UnsupportedSecond, FailOff, ThrowOff, ZeroHandle;
        internal bool Renumbered, DdcOff;
        internal int ReleasedAtOff;
        internal string PowerCaps = "(vcp(D6(01 04)))";
        internal uint LastOffValue;
        internal string[] SignalNames;
        public List<HardwarePower.Monitor> Enumerate()
        {
            Interlocked.Increment(ref Enumerations);
            if (ThrowOnce || ThrowAlways) { ThrowOnce = false; throw new IOException("transient fake enumeration"); }
            List<HardwarePower.Monitor> items = new List<HardwarePower.Monitor>();
            if (Present) for (int i = 0; i < Count; i++) items.Add(new HardwarePower.Monitor { Name = Renumbered ? (i == 0 ? "DISPLAY2" : "DISPLAY9") : (TwoLogical && i > 0 ? "DISPLAY2" : "DISPLAY1"), Identity = TwoLogical && i > 0 ? "v2:second" : Identity, Handle = ZeroHandle ? IntPtr.Zero : new IntPtr(i + 1), Release = delegate { Interlocked.Increment(ref Released); } });
            return items;
        }
        public string[] SignalSleepTargets(string[] names) { return SignalNames ?? (Signal ? names : new string[0]); }
        public string Capabilities(HardwarePower.Monitor item) { Action callback = OnCapabilities; OnCapabilities = null; if (callback != null) callback(); return NoCaps || (UnsupportedSecond && item.Handle.ToInt64() == 2) ? null : PowerCaps; }
        public bool IsOn(HardwarePower.Monitor item) { return true; }
        public bool SetPower(HardwarePower.Monitor item, uint value)
        {
            if (value == 4 || value == 5) { LastOffValue = value; ReleasedAtOff = Released; Interlocked.Increment(ref Off); if (ThrowOff) throw new IOException("fake power write error"); DdcOff = !FailOff; return !FailOff; }
            Interlocked.Increment(ref Wake); return !FailWake;
        }
    }
    sealed class Display : IDisplayPower
    {
        internal volatile bool Active, FailSleep, FailRestore, Changed;
        internal int Restores, Sleeps, Archives;
        internal Action OnSleep;
        public bool IsActive { get { return Active; } }
        public bool RecoveryBlocked { get { return Changed; } }
        public string LastError { get { return "fake failure"; } }
        public bool Sleep(string[] names) { Interlocked.Increment(ref Sleeps); Active = true; if (OnSleep != null) OnSleep(); return !FailSleep; }
        public bool Restore() { Interlocked.Increment(ref Restores); if (FailRestore) return false; Active = false; return true; }
        public bool CheckTopology() { return !Changed; }
        public bool ArchiveRecovery(out string path) { Archives++; path = "fake archive"; Active = false; return true; }
    }
    internal static int Run()
    {
        assertions = 0;
        Check(HardwarePower.PowerOffValue("(vcp(D6(01 04 05)))") == 5, "prefer advertised hard off when reversible support boundary is met");
        Check(HardwarePower.PowerOffValue("(vcp(D6(01 04)))") == 4, "retain off04 for monitors without advertised05");
        Check(HardwarePower.PowerOffValue("(vcp(D6(01 05)))") == 0, "01/05 alone does not broaden reversible support boundary");
        Check(HardwarePower.PowerOffValue("(vcp(D6(05)))") == 0, "05-only monitor remains unsupported");
        Check(HardwarePower.PowerOffValue("(vcp(10(01 04 05) D6(01 04)))") == 4, "05 in another feature cannot select hard off");
        string singleIdentity = HardwarePower.ActiveMonitorIdentity(new[] { new KeyValuePair<string, uint>("monitor-a", 1) });
        Check(singleIdentity == HardwarePower.ActiveMonitorIdentity(new[] { new KeyValuePair<string, uint>("old-monitor-b", 0), new KeyValuePair<string, uint>("MONITOR-A", 1) }), "inactive monitor history after signal removal cannot change stable identity");
        Check(singleIdentity != HardwarePower.ActiveMonitorIdentity(new[] { new KeyValuePair<string, uint>("monitor-b", 1), new KeyValuePair<string, uint>("monitor-a", 1) }), "active clone members remain part of physical group identity");
        Check(HardwarePower.ActiveMonitorIdentity(new[] { new KeyValuePair<string, uint>("monitor-b", 1), new KeyValuePair<string, uint>("monitor-a", 1) }) == HardwarePower.ActiveMonitorIdentity(new[] { new KeyValuePair<string, uint>("monitor-a", 1), new KeyValuePair<string, uint>("monitor-b", 1) }), "active clone identity is independent of device enumeration order");
        Check(HardwarePower.ActiveMonitorIdentity(new[] { new KeyValuePair<string, uint>("inactive-monitor", 0) }) == null, "no active monitor identity prevents unsafe targeting");
        string directory = Path.Combine(Path.GetTempPath(), "Nulluxion-power-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "power.txt");
            Backend backend = new Backend(); Display display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                Await(delegate { return power.IsRestored; }, "empty recovery reaches restored");
                power.SetTargets(new[] { "DISPLAY1" });
                Await(delegate { return backend.Off == 1; }, "fake monitor sleeps");
                Check(File.ReadAllText(path).Contains("v2:test"), "journal written before sleep");
                backend.FailWake = true; power.Restore();
                Await(delegate { return backend.Wake > 2; }, "failed wake retries");
                Check(!power.IsRestored, "failed wake is not restored");
                backend.FailWake = false;
                Await(delegate { return power.IsRestored; }, "wake eventually recovers");
            }
            File.Delete(path);
            backend = new Backend { Signal = true }; display = new Display { FailSleep = true, FailRestore = true };
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                power.SetTargets(new[] { "DISPLAY1" });
                Await(delegate { return display.Restores > 2; }, "failed sleep rollback retries without user request");
                display.FailRestore = false;
                Await(delegate { return !display.Active; }, "failed rollback eventually restores");
                Thread.Sleep(50);
                Check(display.Sleeps == 1, "failed signal sleep is not reapplied after recovery");
            }
            File.WriteAllText(path, "v2:test\n");
            backend = new Backend { Present = false, Count = 2 }; display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                Await(delegate { return backend.Enumerations > 1; }, "missing monitor recovery retries");
                Check(power.HasUnresolvedRecovery, "missing recovery visible");
                backend.Present = true;
                Await(delegate { return power.IsRestored; }, "reconnected group restores");
                Check(backend.Wake == 2, "every physical monitor in reconnected logical group wakes");
            }
            File.WriteAllText(path, "\\\\.\\DISPLAY1\ncorrupt-entry\n");
            backend = new Backend(); display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                Await(delegate { return backend.Enumerations > 0; }, "legacy recovery examined");
                Check(backend.Wake == 0, "legacy name never wakes an unrelated current monitor");
                power.RetireRecovery();
                Await(delegate { return power.IsRestored; }, "explicit retirement unblocks recovery");
                Check(Directory.GetFiles(directory, "power.txt.retired-*").Length == 1, "unresolved recovery archived");
            }
            File.WriteAllText(path, "v2:test\n");
            backend = new Backend { ThrowOnce = true }; display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
                Await(delegate { return power.IsRestored; }, "worker survives enumeration exception");
            backend = new Backend { Signal = true }; display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                int external = 0; power.ExternalDisplayChanged += delegate { Interlocked.Increment(ref external); };
                power.SetTargets(new[] { "DISPLAY1" });
                Await(delegate { return display.Active; }, "intentional signal sleep active");
                power.NotifyTopologyChanged(); Thread.Sleep(30);
                Check(external == 0, "expected topology notification is ignored");
                display.Changed = true; power.NotifyTopologyChanged();
                Await(delegate { return external == 1; }, "external topology detected during intentional sleep");
            }
            backend = new Backend { NoCaps = true }; display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                Await(delegate { return power.IsRestored; }, "initial restoration completes before cancellation test");
                backend.OnCapabilities = delegate { power.Restore(); };
                power.SetTargets(new[] { "DISPLAY1" });
                Await(delegate { return backend.OnCapabilities == null; }, "cancellation injected inside capability call");
                Await(delegate { return power.IsRestored; }, "consumed request signal cannot strand cancelled generation");
                Check(backend.Off == 0, "cancellation during capability retry prevents write");
            }
            backend = new Backend(); display = new Display { Active = true, FailRestore = true, Changed = true };
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                Await(delegate { return display.Restores > 0; }, "blocked display journal attempted once");
                int attempts = display.Restores; Thread.Sleep(80);
                Check(attempts == display.Restores, "known blocked recovery does not retry continuously");
                power.RetireRecovery();
                Await(delegate { return power.IsRestored; }, "explicit archive clears blocked display recovery");
                Check(display.Archives == 1, "display recovery archive API used");
            }
            File.WriteAllText(path, "v2:test\n");
            backend = new Backend { ThrowAlways = true }; display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                Await(delegate { return backend.Enumerations > 0; }, "persistent backend failure observed");
                power.RetireRecovery();
                Await(delegate { return power.IsRestored; }, "explicit retirement succeeds despite persistent backend exception");
            }
            backend = new Backend { ThrowAlways = true, Signal = true }; display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                power.SetTargets(new[] { "DISPLAY1" });
                Await(delegate { return display.Sleeps == 1; }, "identified signal target sleeps despite DDC enumeration exception");
            }
            backend = new Backend { NoCaps = true }; display = new Display();
            HardwarePower racing = new HardwarePower(backend, display, path, 10);
            Exception requestError = null;
            Thread requester = new Thread(delegate() { try { for (int i = 0; i < 1000; i++) { racing.SetTargets(new[] { "DISPLAY1" }); racing.Restore(); racing.NotifyTopologyChanged(); } } catch (Exception ex) { requestError = ex; } });
            requester.Start(); racing.Dispose(); requester.Join();
            racing.SetTargets(new[] { "DISPLAY1" }); racing.Dispose();
            Check(requestError == null, "concurrent request/dispose never touches disposed event");
            Check(backend.Off == 0, "cancelled requests never write monitor power");
            backend = new Backend { Count = 2, TwoLogical = true, SignalNames = new[] { "display2" } }; display = new Display();
            display.OnSleep = delegate { backend.DdcOff = false; backend.Renumbered = true; };
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                int prepared = 0; power.AllScreensPrepared += delegate { Interlocked.Increment(ref prepared); };
                power.SetAllTargets(new[] { "DISPLAY1", "DISPLAY2", "display1" });
                Await(delegate { return prepared == 1; }, "mixed DDC and signal all-screen request prepares once");
                Check(power.AllScreensPoweredOff && !power.NeedsDisplaySleepFallback, "both logical displays directly powered off");
                Check(backend.Off == 1 && display.Sleeps == 1, "BenQ uses signal while other monitor uses DDC");
                Check(backend.DdcOff, "DDC off occurs after signal topology change that wakes other monitors");
                Check(backend.Enumerations >= 2 && backend.ReleasedAtOff >= 2, "old physical handles released and reacquired after topology change");
                Check(power.AllScreensPoweredOff, "stable identity maps renamed physical monitor back to original target");
                Check(!power.IsRestored, "direct all-screen off is not restored");
                Check(power.LastStatus == "All monitors powered off; use shortcut or Escape to restore.", "all-screen status explains restore controls");
                power.Restore();
                Check(!power.AllScreensPoweredOff && !power.NeedsDisplaySleepFallback, "restore invalidates prepared result immediately");
                Await(delegate { return power.IsRestored; }, "all-screen hardware and signal restore");
                Check(backend.Wake == 1 && display.Restores == 1, "both power mechanisms restored");
            }
            Backend[] incomplete = {
                new Backend { Count = 2, TwoLogical = true, UnsupportedSecond = true },
                new Backend { Count = 2, UnsupportedSecond = true },
                new Backend { Present = false },
                new Backend { FailOff = true },
                new Backend { ThrowOff = true },
                new Backend { ThrowAlways = true }
            };
            foreach (Backend candidate in incomplete)
            {
                using (HardwarePower power = new HardwarePower(candidate, new Display(), path, 10))
                {
                    int prepared = 0; power.AllScreensPrepared += delegate { Interlocked.Increment(ref prepared); };
                    power.SetAllTargets(candidate.TwoLogical ? new[] { "DISPLAY1", "DISPLAY2" } : new[] { "DISPLAY1" });
                    Await(delegate { return prepared == 1; }, "incomplete hardware request publishes fallback");
                    Check(!power.AllScreensPoweredOff && power.NeedsDisplaySleepFallback, "missing, unsupported, or failed physical monitor prevents complete result");
                    power.Restore();
                    Await(delegate { return power.IsRestored; }, "partial hardware restored before Windows fallback");
                    Check(prepared == 1, "fallback event is not repeated by recovery");
                }
            }
            backend = new Backend { ZeroHandle = true, PowerCaps = "(vcp(D6(01 04 05)))" }; display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                power.SetAllTargets(new[] { "DISPLAY1" });
                Await(delegate { return power.AllScreensPoweredOff; }, "enumerated physical-monitor token zero is valid for DDC power");
                Check(backend.Off == 1 && !power.NeedsDisplaySleepFallback, "zero token is not treated as absent hardware");
                Check(backend.LastOffValue == 5, "advertised05 reaches hardware backend");
                power.Restore();
                Await(delegate { return power.IsRestored; }, "zero-token monitor restored");
                Check(backend.Wake == 1 && backend.Released >= 1, "zero-token physical handles are released on restoration");
            }
            backend = new Backend { Signal = true }; display = new Display { FailSleep = true };
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                power.SetAllTargets(new[] { "DISPLAY1" });
                Await(delegate { return power.NeedsDisplaySleepFallback; }, "signal sleep rejection, including last active path, falls back");
                Check(!power.AllScreensPoweredOff, "failed signal sleep cannot report complete");
                power.Restore(); Await(delegate { return power.IsRestored; }, "failed signal request restored");
            }
            backend = new Backend(); display = new Display();
            using (HardwarePower power = new HardwarePower(backend, display, path, 10))
            {
                Await(delegate { return power.IsRestored; }, "ready for all-screen cancellation");
                int prepared = 0; power.AllScreensPrepared += delegate { Interlocked.Increment(ref prepared); };
                backend.OnCapabilities = delegate { power.Restore(); };
                power.SetAllTargets(new[] { "DISPLAY1" });
                Await(delegate { return backend.OnCapabilities == null && power.IsRestored; }, "all-screen cancelled in native call restores");
                Check(prepared == 0 && !power.AllScreensPoweredOff && !power.NeedsDisplaySleepFallback, "cancelled generation never publishes readiness");
            }
            return assertions;
        }
        finally { Directory.Delete(directory, true); }
    }
}
