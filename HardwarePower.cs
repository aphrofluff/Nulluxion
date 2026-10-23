using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Nulluxion
{
    internal interface IPowerController : IDisposable
    {
        event Action<string> Status;
        event Action Restored;
        event Action ExternalDisplayChanged;
        event Action AllScreensPrepared;
        bool AllScreensPoweredOff { get; }
        bool NeedsDisplaySleepFallback { get; }
        bool IsRestored { get; }
        bool HasUnresolvedRecovery { get; }
        string LastStatus { get; }
        void SetTargets(string[] names);
        void SetAllTargets(string[] names);
        void Restore();
        void NotifyTopologyChanged();
        void RetireRecovery();
    }
    internal interface IDisplayPower
    {
        bool IsActive { get; }
        bool RecoveryBlocked { get; }
        string LastError { get; }
        bool Sleep(string[] names);
        bool Restore();
        bool CheckTopology();
        bool ArchiveRecovery(out string archivePath);
    }
    // Native operations and recovery storage are injectable: worker tests never touch displays.
    internal sealed class HardwarePower : IPowerController
    {
        internal interface IBackend
        {
            List<Monitor> Enumerate();
            string[] SignalSleepTargets(string[] names);
            string Capabilities(Monitor monitor);
            bool IsOn(Monitor monitor);
            bool SetPower(Monitor monitor, uint value);
        }
        public event Action<string> Status;
        public event Action Restored;
        public event Action ExternalDisplayChanged;
        public event Action AllScreensPrepared;
        readonly object gate = new object();
        readonly AutoResetEvent changed = new AutoResetEvent(false);
        readonly Thread worker;
        readonly IBackend backend;
        readonly IDisplayPower displaySleep;
        readonly string journal;
        readonly int retryMilliseconds;
        readonly List<Monitor> pending = new List<Monitor>();
        readonly HashSet<string> recovery = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] desired = new string[0];
        int version, restoredVersion = -1;
        int preparedVersion = -1;
        bool desiredAll, allPoweredOff;
        bool stopping, topologyChanged, retireRequested, rollbackNeeded, unreadableJournal;
        volatile bool unresolved;
        string lastStatus = "Monitor recovery initializing.";
        long ignoreTopologyUntil;
        public string LastStatus { get { lock (gate) return lastStatus; } }
        public bool HasUnresolvedRecovery { get { return unresolved; } }
        public bool AllScreensPoweredOff { get { lock (gate) return desiredAll && preparedVersion == version && allPoweredOff; } }
        public bool NeedsDisplaySleepFallback { get { lock (gate) return desiredAll && preparedVersion == version && !allPoweredOff; } }
        public bool IsRestored { get { lock (gate) return desired.Length == 0 && restoredVersion == version && !unresolved; } }
        internal bool HandlesDisplayChanges { get { return DateTime.UtcNow.Ticks < Interlocked.Read(ref ignoreTopologyUntil); } }
        internal HardwarePower() : this(new NativeBackend(), new DisplaySleep(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackout", "power-recovery.txt"), 2000) { }
        internal HardwarePower(IBackend backend, IDisplayPower displaySleep, string recoveryPath, int retryMilliseconds)
        {
            this.backend = backend; this.displaySleep = displaySleep; journal = recoveryPath; this.retryMilliseconds = Math.Max(1, retryMilliseconds);
            try
            {
                if (File.Exists(journal)) foreach (string line in File.ReadAllLines(journal))
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    // Legacy DISPLAYn records cannot safely identify a device after reboot.
                    // Preserve them for explicit archive; never guess which monitor they meant.
                    recovery.Add(line);
                }
            }
            catch (Exception ex) { unreadableJournal = true; Report("Cannot read monitor recovery file: " + ex.Message); }
            unresolved = unreadableJournal || recovery.Count > 0 || displaySleep.IsActive;
            worker = new Thread(Run); worker.Name = "Nulluxion monitor power"; worker.IsBackground = true; worker.Start();
        }
        public void SetTargets(string[] names)
        {
            RequestTargets(names, false);
        }
        public void SetAllTargets(string[] names) { RequestTargets(names, true); }
        void RequestTargets(string[] names, bool all)
        {
            lock (gate) { if (stopping) return; desired = names == null ? new string[0] : (string[])names.Clone(); desiredAll = all; version++; changed.Set(); }
        }
        public void Restore() { SetTargets(new string[0]); }
        public void NotifyTopologyChanged() { lock (gate) { if (stopping) return; topologyChanged = true; changed.Set(); } }
        public void RetireRecovery() { lock (gate) { if (stopping) return; retireRequested = true; desiredAll = false; desired = new string[0]; version++; changed.Set(); } }
        public void Dispose()
        {
            lock (gate) { if (stopping) return; stopping = true; desiredAll = false; desired = new string[0]; version++; changed.Set(); }
            // Signal and disposal share the gate, so a queued requester cannot touch a disposed event.
            if (Thread.CurrentThread != worker && worker.Join(5000)) lock (gate) changed.Dispose();
        }
        bool Current(int generation) { lock (gate) return !stopping && version == generation; }
        void Report(string message)
        {
            lock (gate) lastStatus = message;
            try { Action<string> handler = Status; if (handler != null) handler(message); } catch { }
        }
        void SaveRecovery()
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(journal));
            Directory.CreateDirectory(directory);
            string temp = journal + ".tmp";
            File.WriteAllLines(temp, new List<string>(recovery).ToArray());
            if (File.Exists(journal)) File.Replace(temp, journal, null); else File.Move(temp, journal);
        }
        void Run()
        {
            int applied = -1;
            try
            {
                while (true)
                {
                    int generation; string[] targets; bool quit, topology, retire;
                    lock (gate) { generation = version; targets = (string[])desired.Clone(); quit = stopping; topology = topologyChanged; topologyChanged = false; retire = retireRequested; retireRequested = false; }
                    bool retry = false;
                    try
                    {
                        if (topology)
                        {
                            bool external = displaySleep.IsActive ? !displaySleep.CheckTopology() : !HandlesDisplayChanges;
                            if (external)
                            {
                                lock (gate) { desiredAll = false; desired = new string[0]; version++; generation = version; targets = new string[0]; }
                                try { Action handler = ExternalDisplayChanged; if (handler != null) handler(); } catch { }
                            }
                        }
                        if (generation != applied || quit || targets.Length == 0 || rollbackNeeded || retire)
                        {
                            if (retire)
                            {
                                // Explicit archive remains available even when the failing backend
                                // cannot enumerate or wake the old device at all.
                                try { RestorePending(false); }
                                catch (Exception ex) { Report("Recovery attempt failed before archive: " + ex.Message); }
                                ArchiveRecovery();
                            }
                            else RestorePending(false);
                            if (quit) break;
                            if (pending.Count == 0 && !displaySleep.IsActive && !unreadableJournal && recovery.Count == 0 && Current(generation))
                            {
                                bool newlyRequested = applied != generation;
                                applied = generation;
                                if (targets.Length > 0 && newlyRequested) PowerOff(targets, generation);
                                else if (targets.Length == 0) MarkRestored(generation);
                            }
                        }
                        else RestorePending(true);
                        unresolved = unreadableJournal || recovery.Count > 0 || displaySleep.IsActive;
                        bool needsWake = pending.Exists(delegate(Monitor item) { return item.NeedsWake; });
                        bool blocked = unreadableJournal || displaySleep.RecoveryBlocked;
                        foreach (string record in recovery) if (!record.StartsWith("v2:", StringComparison.Ordinal)) blocked = true;
                        retry = needsWake || (!blocked && (rollbackNeeded || generation != applied || (targets.Length == 0 && unresolved)));
                        PrepareAllScreens(generation, false);
                    }
                    catch (Exception ex)
                    {
                        // A transient backend failure must not permanently kill the sole recovery worker.
                        rollbackNeeded = true; unresolved = true; retry = true;
                        Report("Monitor power error; recovery will retry: " + ex.Message);
                        PrepareAllScreens(generation, false);
                        if (quit) break;
                    }
                    // A request can arrive during a native call or retry delay. Never wait
                    // on the old generation after consuming that request's wake signal.
                    lock (gate) { if (version != generation || topologyChanged || retireRequested || stopping) continue; }
                    changed.WaitOne(retry ? retryMilliseconds : Timeout.Infinite);
                }
            }
            finally
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { RestorePending(false); } catch (Exception ex) { Report("Recovery remains saved: " + ex.Message); }
                    if (pending.Count == 0 && !displaySleep.IsActive) break;
                    if (attempt < 2) Thread.Sleep(Math.Min(250, retryMilliseconds));
                }
                foreach (Monitor monitor in pending) monitor.Dispose();
                pending.Clear();
            }
        }
        void MarkRestored(int generation)
        {
            bool notify;
            lock (gate) { notify = version == generation && restoredVersion != generation; if (notify) { restoredVersion = generation; unresolved = false; } }
            if (notify && LastStatus == "Monitor recovery initializing.") Report("Monitor recovery complete. Hardware power is ready.");
            if (notify) try { Action handler = Restored; if (handler != null) handler(); } catch { }
        }
        void PrepareAllScreens(int generation, bool poweredOff)
        {
            lock (gate)
            {
                if (stopping || !desiredAll || version != generation || preparedVersion == generation) return;
                preparedVersion = generation; allPoweredOff = poweredOff;
            }
            Report(poweredOff ? "All monitors powered off; use shortcut or Escape to restore." : "Direct power-off unavailable for every monitor; preparing Windows display sleep.");
            try { Action handler = AllScreensPrepared; if (handler != null) handler(); } catch { }
        }
        void ArchiveRecovery()
        {
            string archive;
            if (displaySleep.IsActive)
            {
                if (!displaySleep.ArchiveRecovery(out archive)) { Report(displaySleep.LastError); return; }
                Report("Display recovery archived at " + archive);
            }
            if (File.Exists(journal))
            {
                archive = journal + ".retired-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
                File.Move(journal, archive);
                Report("Unresolved monitor recovery archived at " + archive);
            }
            foreach (Monitor monitor in pending) monitor.Dispose();
            pending.Clear(); recovery.Clear(); unreadableJournal = false; rollbackNeeded = false; unresolved = false;
        }
        void RestorePending(bool failedOnly)
        {
            if (!failedOnly && displaySleep.IsActive)
            {
                Interlocked.Exchange(ref ignoreTopologyUntil, DateTime.UtcNow.AddSeconds(3).Ticks);
                try { if (displaySleep.Restore()) { rollbackNeeded = false; Report("Display signal and saved layout restored."); } else { rollbackNeeded = true; Report(displaySleep.LastError); } }
                finally { Interlocked.Exchange(ref ignoreTopologyUntil, DateTime.UtcNow.AddSeconds(3).Ticks); }
            }
            if (!failedOnly && recovery.Count > 0)
            {
                HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Monitor item in pending) existing.Add(item.Identity);
                // Decide by group before adding any fresh handles: all physical monitors survive.
                foreach (Monitor fresh in backend.Enumerate())
                {
                    if (recovery.Contains(fresh.Identity) && !existing.Contains(fresh.Identity)) pending.Add(fresh); else fresh.Dispose();
                }
            }
            HashSet<string> groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Monitor item in pending) if (!failedOnly || item.NeedsWake) groups.Add(item.Identity);
            foreach (string identity in groups)
            {
                bool success = true;
                foreach (Monitor monitor in pending) if (String.Equals(monitor.Identity, identity, StringComparison.OrdinalIgnoreCase))
                {
                    if (!monitor.Awake) monitor.Awake = backend.SetPower(monitor, 1);
                    success &= monitor.Awake;
                }
                if (!success)
                {
                    bool found = false; success = true;
                    foreach (Monitor fresh in backend.Enumerate())
                    {
                        try { if (String.Equals(fresh.Identity, identity, StringComparison.OrdinalIgnoreCase)) { found = true; success &= backend.SetPower(fresh, 1); } }
                        finally { fresh.Dispose(); }
                    }
                    success &= found;
                }
                if (!success) { Report("Monitor wake failed; recovery retained for retry."); continue; }
                recovery.Remove(identity);
                try { SaveRecovery(); }
                catch { recovery.Add(identity); throw; }
                foreach (Monitor monitor in pending.ToArray()) if (String.Equals(monitor.Identity, identity, StringComparison.OrdinalIgnoreCase)) { pending.Remove(monitor); monitor.Dispose(); }
                Report("Monitor backlight restored.");
            }
            if (!displaySleep.IsActive && pending.Count == 0) rollbackNeeded = false;
            unresolved = unreadableJournal || recovery.Count > 0 || displaySleep.IsActive;
            if (!failedOnly && recovery.Count > 0 && pending.Count == 0) Report("Saved monitor recovery cannot be matched safely. Reconnect the original display or explicitly archive recovery.");
        }
        void PowerOff(string[] names, int generation)
        {
            HashSet<string> targets = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            HashSet<string> signalTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> powered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> matchedAfterSignal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (string name in backend.SignalSleepTargets(names)) if (targets.Contains(name)) signalTargets.Add(name); }
            catch (Exception ex) { Report("Monitor identification failed; checking DDC capabilities: " + ex.Message); }
            List<Monitor> monitors;
            try { monitors = backend.Enumerate(); }
            catch (Exception ex) { Report("DDC enumeration failed; using available signal sleep or overlay: " + ex.Message); monitors = new List<Monitor>(); }
            try
            {
                Dictionary<Monitor, string> capabilities = new Dictionary<Monitor, string>();
                Dictionary<string, string> originalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Monitor monitor in monitors)
                {
                    if (!Current(generation)) break;
                    if (!targets.Contains(monitor.Name)) continue;
                    if (!String.IsNullOrEmpty(monitor.Identity)) originalNames[monitor.Identity] = monitor.Name;
                    if (signalTargets.Contains(monitor.Name)) continue;
                    string caps = ReadCapabilities(monitor, generation);
                    capabilities[monitor] = caps;
                    if (caps != null && Regex.IsMatch(caps, @"\bmodel\s*\(\s*EX240N\s*\)", RegexOptions.IgnoreCase)) signalTargets.Add(monitor.Name);
                }
                bool signalAttempted = signalTargets.Count > 0 && Current(generation);
                if (signalAttempted)
                {
                    // A layout change can wake monitors already powered off over DDC.
                    // Finish signal sleep first, then acquire handles for the new layout.
                    Interlocked.Exchange(ref ignoreTopologyUntil, DateTime.UtcNow.AddSeconds(3).Ticks);
                    try
                    {
                        if (!displaySleep.Sleep(new List<string>(signalTargets).ToArray()))
                        {
                            Report("Signal sleep failed: " + displaySleep.LastError);
                            rollbackNeeded = displaySleep.IsActive;
                            if (rollbackNeeded && displaySleep.Restore()) rollbackNeeded = false;
                        }
                        else foreach (string name in signalTargets) powered.Add(name);
                    }
                    finally { Interlocked.Exchange(ref ignoreTopologyUntil, DateTime.UtcNow.AddSeconds(3).Ticks); }
                    foreach (Monitor monitor in monitors) monitor.Dispose();
                    monitors.Clear(); capabilities.Clear();
                    if (Current(generation) && !rollbackNeeded)
                    {
                        try { monitors = backend.Enumerate(); }
                        catch (Exception ex) { Report("DDC enumeration after signal sleep failed: " + ex.Message); }
                    }
                }
                foreach (Monitor monitor in monitors)
                {
                    if (!Current(generation) || rollbackNeeded) break;
                    string name = monitor.Name;
                    // DISPLAYn may be reassigned after removing a signal path. Only
                    // target the same physical identity observed before that change.
                    if (signalAttempted && (String.IsNullOrEmpty(monitor.Identity) || !originalNames.TryGetValue(monitor.Identity, out name))) continue;
                    if (!targets.Contains(name) || signalTargets.Contains(name)) continue;
                    matchedAfterSignal.Add(name);
                    string caps;
                    if (!capabilities.TryGetValue(monitor, out caps)) caps = ReadCapabilities(monitor, generation);
                    uint offValue = PowerOffValue(caps);
                    if (offValue == 0 || !backend.IsOn(monitor) || String.IsNullOrEmpty(monitor.Identity)) { failed.Add(name); Report(name + ": reversible DDC/CI power-off unavailable; using overlay."); continue; }
                    if (!Current(generation)) break;
                    bool added = recovery.Add(monitor.Identity);
                    try { SaveRecovery(); }
                    catch (Exception ex) { failed.Add(name); if (added) recovery.Remove(monitor.Identity); Report("Power-off skipped: cannot save recovery: " + ex.Message); continue; }
                    pending.Add(monitor);
                    if (!Current(generation)) break;
                    // Assume ambiguous native failures need waking, including exceptions.
                    monitor.NeedsWake = true;
                    if (backend.SetPower(monitor, offValue)) { powered.Add(name); monitor.NeedsWake = false; Report(name + ": backlight power-off command sent."); }
                    else { failed.Add(name); Report(name + ": power-off not acknowledged; restoring and using overlay."); }
                }
                if (signalAttempted && Current(generation)) foreach (string name in targets)
                    if (!signalTargets.Contains(name) && !matchedAfterSignal.Contains(name))
                        Report(name + ": original active monitor could not be matched after the display layout changed; direct power-off skipped.");
            }
            finally { foreach (Monitor monitor in monitors) if (!pending.Contains(monitor)) monitor.Dispose(); }
            PrepareAllScreens(generation, targets.Count > 0 && targets.IsSubsetOf(powered) && failed.Count == 0);
        }
        string ReadCapabilities(Monitor monitor, int generation)
        {
            for (int attempt = 0; attempt < 3 && Current(generation); attempt++)
            {
                string value = backend.Capabilities(monitor);
                if (value != null) return value;
                if (attempt < 2) changed.WaitOne(Math.Min(200, retryMilliseconds));
            }
            return null;
        }
        sealed class NativeBackend : IBackend
        {
            public List<Monitor> Enumerate() { return HardwarePower.Enumerate(); }
            public string[] SignalSleepTargets(string[] names) { return DisplaySleep.FindBenqDisplays(names); }
            public bool IsOn(Monitor monitor) { uint value, maximum; int type; return GetVCPFeatureAndVCPFeatureReply(monitor.Handle, 0xD6, out type, out value, out maximum) && value == 1; }
            public bool SetPower(Monitor monitor, uint value) { return SetVCPFeature(monitor.Handle, 0xD6, value); }
            public string Capabilities(Monitor monitor)
            {
                uint length;
                if (!GetCapabilitiesStringLength(monitor.Handle, out length) || length == 0 || length > 65536) return null;
                StringBuilder value = new StringBuilder((int)length);
                return CapabilitiesRequestAndCapabilitiesReply(monitor.Handle, value, length) ? value.ToString() : null;
            }
        }
        // EDD_GET_DEVICE_INTERFACE_NAME supplies the PnP monitor interface, not DISPLAYn.
        // A clone group is identified by its complete sorted device-interface set.
        static string StableIdentity(string name)
        {
            List<KeyValuePair<string, uint>> devices = new List<KeyValuePair<string, uint>>();
            for (uint index = 0; index < 128; index++)
            {
                DisplayDevice device = new DisplayDevice(); device.Size = Marshal.SizeOf(typeof(DisplayDevice));
                if (!EnumDisplayDevices(name, index, ref device, 1)) break;
                devices.Add(new KeyValuePair<string, uint>(device.Id, device.Flags));
            }
            return ActiveMonitorIdentity(devices);
        }
        internal static string ActiveMonitorIdentity(IEnumerable<KeyValuePair<string, uint>> devices)
        {
            List<string> identities = new List<string>();
            // EnumDisplayDevices retains disconnected monitor children after a
            // layout change. Only DISPLAY_DEVICE_ACTIVE children belong to the
            // current physical group; inactive history must not alter identity.
            foreach (KeyValuePair<string, uint> device in devices)
                if ((device.Value & 1) != 0 && !String.IsNullOrEmpty(device.Key)) identities.Add(device.Key.ToUpperInvariant());
            if (identities.Count == 0) return null;
            identities.Sort(StringComparer.Ordinal);
            return "v2:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(String.Join("\n", identities.ToArray())));
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DisplayDevice
        {
            internal int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Key;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string name, uint index, ref DisplayDevice device, uint flags);
        internal static bool SupportsPowerOff(string capabilities) { return PowerOffValue(capabilities) != 0; }
        internal static uint PowerOffValue(string capabilities)
        {
            if (String.IsNullOrEmpty(capabilities)) return 0;
            Match vcp = Regex.Match(capabilities, @"\bvcp\s*\(", RegexOptions.IgnoreCase);
            if (!vcp.Success) return 0;
            int start = vcp.Index + vcp.Length, end = start, depth = 1;
            while (end < capabilities.Length && depth > 0) { if (capabilities[end] == '(') depth++; if (capabilities[end] == ')') depth--; end++; }
            if (depth != 0) return 0;
            string body = capabilities.Substring(start, end - start - 1);
            Match feature = Regex.Match(body, @"(?:^|(?<=\s)|(?<=\)))D6\s*\(([^()]*)\)", RegexOptions.IgnoreCase);
            if (!feature.Success) return 0;
            int featureDepth = 0;
            for (int index = 0; index < feature.Index; index++) { if (body[index] == '(') featureDepth++; if (body[index] == ')') featureDepth--; }
            if (featureDepth != 0) return 0;
            bool on = false, off = false, hardOff = false;
            foreach (string token in Regex.Split(feature.Groups[1].Value.Trim(), @"\s+"))
            { uint value; if (UInt32.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) { on |= value == 1; off |= value == 4; hardOff |= value == 5; } }
            // Keep the established reversible 01/04 support boundary. Some
            // displays acknowledge 04 without sleeping but honor advertised 05.
            return on && off ? (hardOff ? 5u : 4u) : 0u;
        }
        internal sealed class Monitor : IDisposable
        {
            internal IntPtr Handle; internal string Name, Identity; internal bool NeedsWake, Awake; internal Action<IntPtr> Release = null;
            bool disposed;
            // DXVA2 may return zero as a valid physical-monitor token. Presence is
            // established by successful enumeration, not the numeric handle value.
            public void Dispose() { if (!disposed) { disposed = true; if (Release != null) Release(Handle); else DestroyPhysicalMonitor(Handle); Handle = IntPtr.Zero; } }
        }
        static List<Monitor> Enumerate()
        {
            List<Monitor> result = new List<Monitor>();
            MonitorEnum callback = delegate(IntPtr handle, IntPtr dc, ref Rect bounds, IntPtr data)
            {
                MonitorInfo info = new MonitorInfo(); info.Size = Marshal.SizeOf(typeof(MonitorInfo));
                uint count;
                if (!GetMonitorInfo(handle, ref info) || !GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out count) || count == 0 || count > 128) return true;
                PhysicalMonitor[] physical = new PhysicalMonitor[count];
                if (!GetPhysicalMonitorsFromHMONITOR(handle, count, physical)) return true;
                string identity = StableIdentity(info.Device);
                foreach (PhysicalMonitor item in physical) result.Add(new Monitor { Handle = item.Handle, Name = info.Device, Identity = identity });
                return true;
            };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) { foreach (Monitor item in result) item.Dispose(); result.Clear(); }
            GC.KeepAlive(callback); return result;
        }
        [StructLayout(LayoutKind.Sequential)] struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfo
        { internal int Size; internal Rect Monitor, Work; internal uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PhysicalMonitor
        { internal IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description; }
        delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] physical);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr monitor);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
        [DllImport("dxva2.dll", CharSet = CharSet.Ansi, SetLastError = true)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, StringBuilder capabilities, uint length);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out int type, out uint value, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
    }
}






