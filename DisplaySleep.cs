// Copyright (C) 2026 Nulluxion contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Nulluxion
{
    // Selective signal suspension. Unlike an overlay this changes the desktop topology.
    // Call exclusively from one worker, and Restore on exit and before changing targets.
    internal sealed class DisplaySleep : IDisplayPower
    {
        const uint Supplied = 0x20, Validate = 0x40, Apply = 0x80, QueryOnlyActivePaths = 2;
        readonly string journal;
        Snapshot original, expected;
        readonly IBackend backend;
        public bool RecoveryBlocked { get; private set; }
        internal interface IBackend
        {
            Snapshot Capture();
            int Set(Snapshot snapshot, uint operation);
            string SourceDevice(PathInfo path);
        }
        sealed class NativeBackend : IBackend
        {
            public Snapshot Capture() { return DisplaySleep.Capture(); }
            public int Set(Snapshot value, uint operation) { return DisplaySleep.Set(value, operation); }
            public string SourceDevice(PathInfo path)
            {
                SourceName name = new SourceName { Type = 1, Size = (uint)Marshal.SizeOf(typeof(SourceName)), Adapter = path.Source.Adapter, Id = path.Source.Id };
                Check(DisplayConfigGetDeviceInfo(ref name), "Read display name");
                return name.Name;
            }
        }
        public string LastError { get; private set; }
        public bool IsActive
        {
            get
            {
                if (original != null || RecoveryBlocked) return true;
                try { return RecoveryExists(); }
                catch { return true; } // An unreadable record must never look absent.
            }
        }

        bool RecoveryExists()
        {
            try
            {
                if ((File.GetAttributes(journal) & FileAttributes.Directory) != 0)
                    throw new IOException("The display recovery path is a directory.");
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        internal DisplaySleep() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackout", "display-recovery.bin")) { }
        internal DisplaySleep(string recoveryPath) : this(recoveryPath, new NativeBackend()) { }
        internal DisplaySleep(string recoveryPath, IBackend displayBackend)
        {
            journal = recoveryPath; backend = displayBackend;
            try { if (RecoveryExists()) LoadRecovery(); }
            catch (Exception ex) { Block("Cannot read display recovery: " + ex.Message); }
        }

        public bool Sleep(string[] deviceNames)
        {
            if (RecoveryBlocked) return false;
            LastError = null;
            try
            {
                if (IsActive) throw new InvalidOperationException("Restore the previous display configuration first.");
                if (deviceNames == null || deviceNames.Length == 0) return true;
                Snapshot before = backend.Capture();
                ValidateSnapshot(before, false);
                HashSet<string> requested = new HashSet<string>(deviceNames, StringComparer.OrdinalIgnoreCase);
                HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<PathInfo> remaining = new List<PathInfo>();
                foreach (PathInfo path in before.Paths)
                {
                    string name = backend.SourceDevice(path);
                    if (requested.Contains(name)) found.Add(name);
                    else remaining.Add(path);
                }
                if (found.Count != requested.Count) throw new InvalidOperationException("A requested display is no longer active.");
                if (remaining.Count == 0) throw new InvalidOperationException("At least one display must remain active.");
                Snapshot asleep = new Snapshot { Paths = remaining.ToArray(), Modes = (ModeInfo[])before.Modes.Clone() };
                // If the primary source is suspended, move the retained desktop to the origin.
                HashSet<uint> sources = new HashSet<uint>();
                bool hasOrigin = false;
                foreach (PathInfo path in asleep.Paths)
                {
                    uint index = path.Source.Mode;
                    if (index >= asleep.Modes.Length || asleep.Modes[index].Type != 1) throw new InvalidOperationException("Display source mode is unavailable.");
                    sources.Add(index);
                    if (asleep.Modes[index].X == 0 && asleep.Modes[index].Y == 0) hasOrigin = true;
                }
                if (!hasOrigin)
                {
                    ModeInfo anchor = asleep.Modes[asleep.Paths[0].Source.Mode];
                    foreach (uint index in sources)
                    {
                        asleep.Modes[index].X = checked(asleep.Modes[index].X - anchor.X);
                        asleep.Modes[index].Y = checked(asleep.Modes[index].Y - anchor.Y);
                    }
                }
                Check(backend.Set(asleep, Validate), "Validate display sleep");
                Snapshot latest = backend.Capture();
                if (!SameAdapters(before, latest) || !EquivalentConfiguration(before, latest))
                    throw new InvalidOperationException("Display configuration changed while preparing signal suspension. Try again.");
                expected = asleep;
                WriteSnapshot(before);
                original = before; // Keep recovery even when the driver reports a failed apply.
                int applyError = backend.Set(asleep, Apply);
                // Record what the driver actually did, including a partial failed apply.
                // Subsequent retries are safe only while that topology remains unchanged.
                expected = backend.Capture();
                RefreshRecovery();
                Check(applyError, "Suspend display signal");
                ValidateSnapshot(expected, false);
                if (!SameTargets(asleep, expected))
                    throw new InvalidOperationException("The driver did not suspend the requested display signals. Recovery is retained for restoration.");
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        public bool Restore()
        {
            if (RecoveryBlocked) return false;
            LastError = null;
            try
            {
                if (original == null && RecoveryExists()) LoadRecovery();
                if (original == null) return true;
                Snapshot current = backend.Capture();
                if (!EquivalentConfiguration(original, current))
                {
                    if (!SafeExpected(current)) return Block("Display configuration changed outside Nulluxion. Recovery is retained; archive it explicitly to keep the current layout.");
                    int error = backend.Set(original, Apply);
                    if (error != 0)
                    {
                        // A failing driver can still change part of the desktop. Keep
                        // retries possible while guarding against later external edits.
                        try { expected = backend.Capture(); } catch { }
                        RefreshRecovery();
                    }
                    Check(error, "Restore display configuration");
                    // A driver may accept the request but normalize or omit part of
                    // the layout. Do not retire the only recovery copy until verified.
                    current = backend.Capture();
                    if (!EquivalentConfiguration(original, current))
                    {
                        expected = current;
                        RefreshRecovery();
                        throw new InvalidOperationException("The driver did not restore the saved display configuration. Recovery is retained for retry.");
                    }
                }
                if (RecoveryExists()) File.Delete(journal);
                original = expected = null;
                return true;
            }
            catch (InvalidDataException ex) { return Block("Cannot read display recovery: " + ex.Message); }
            catch (EndOfStreamException ex) { return Block("Cannot read display recovery: " + ex.Message); }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        bool SafeExpected(Snapshot current)
        {
            return expected != null && SameAdapters(expected, current) && EquivalentConfiguration(expected, current);
        }
        public bool CheckTopology()
        {
            if (RecoveryBlocked) return false;
            if (original == null) return !IsActive;
            try
            {
                Snapshot current = backend.Capture();
                if (EquivalentConfiguration(original, current) || SafeExpected(current)) return true;
                return Block("Display configuration changed outside Nulluxion. Recovery is retained; archive it explicitly to keep the current layout.");
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }
        bool Block(string message) { RecoveryBlocked = true; LastError = message; return false; }
        public bool ArchiveRecovery(out string archivePath)
        {
            archivePath = null;
            try
            {
                if (RecoveryExists())
                {
                    string destination = journal + ".archived-" + Guid.NewGuid().ToString("N");
                    File.Move(journal, destination);
                    archivePath = destination;
                }
                original = expected = null; RecoveryBlocked = false; LastError = null;
                return true;
            }
            catch (Exception ex) { LastError = "Cannot archive display recovery: " + ex.Message; return false; }
        }

        internal static string[] FindBenqDisplays(string[] requestedNames)
        {
            HashSet<string> requested = new HashSet<string>(requestedNames, StringComparer.OrdinalIgnoreCase);
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PathInfo path in Capture().Paths)
            {
                SourceName source = new SourceName { Type = 1, Size = (uint)Marshal.SizeOf(typeof(SourceName)), Adapter = path.Source.Adapter, Id = path.Source.Id };
                if (DisplayConfigGetDeviceInfo(ref source) != 0 || !requested.Contains(source.Name)) continue;
                TargetName target = new TargetName { Type = 2, Size = (uint)Marshal.SizeOf(typeof(TargetName)), Adapter = path.Target.Adapter, Id = path.Target.Id };
                if (DisplayConfigGetDeviceInfo(ref target) == 0 && target.FriendlyName != null && target.FriendlyName.IndexOf("EX240N", StringComparison.OrdinalIgnoreCase) >= 0) result.Add(source.Name);
            }
            return new List<string>(result).ToArray();
        }

        static int Set(Snapshot value, uint operation)
        {
            return SetDisplayConfig((uint)value.Paths.Length, value.Paths, (uint)value.Modes.Length, value.Modes, Supplied | operation);
        }

        internal static bool EquivalentConfiguration(Snapshot saved, Snapshot current)
        {
            if (saved.Paths.Length != current.Paths.Length) return false;
            bool sameAdapters = SameAdapters(saved, current);
            if (!sameAdapters && (Adapters(saved).Count != 1 || Adapters(current).Count != 1)) return false;
            bool[] used = new bool[current.Paths.Length];
            foreach (PathInfo left in saved.Paths)
            {
                bool found = false;
                for (int i = 0; i < current.Paths.Length; i++)
                {
                    if (used[i]) continue;
                    PathInfo a = left, b = current.Paths[i];
                    if (!ModeEqual(saved, a.Source.Mode, current, b.Source.Mode, sameAdapters) ||
                        !ModeEqual(saved, a.Target.Mode, current, b.Target.Mode, sameAdapters)) continue;
                    a.Source.Mode = b.Source.Mode = a.Target.Mode = b.Target.Mode = 0;
                    // Query-only status is not part of the requested desktop layout.
                    a.Source.Flags = b.Source.Flags = a.Target.Flags = b.Target.Flags = 0;
                    a.Target.Available = b.Target.Available = 0;
                    if (!sameAdapters) { a.Source.Adapter = b.Source.Adapter = new Luid(); a.Target.Adapter = b.Target.Adapter = new Luid(); }
                    if (!a.Equals(b)) continue;
                    used[i] = true; found = true; break;
                }
                if (!found) return false;
            }
            return true;
        }
        static bool ModeEqual(Snapshot a, uint ai, Snapshot b, uint bi, bool sameAdapters)
        {
            if (ai == uint.MaxValue || bi == uint.MaxValue) return ai == bi;
            if (ai >= a.Modes.Length || bi >= b.Modes.Length) return false;
            ModeInfo x = a.Modes[ai], y = b.Modes[bi];
            if (x.Raw0 != y.Raw0 || (sameAdapters && x.Raw1 != y.Raw1)) return false;
            // SOURCE_MODE contains width, height, pixel format and position (20
            // bytes). The remainder of the 48-byte native union is undefined.
            if (x.Type == 1)
                return x.Raw2 == y.Raw2 && x.Raw3 == y.Raw3 && (uint)x.Raw4 == (uint)y.Raw4;
            if (x.Type == 2)
            {
                // TARGET_MODE uses the full VIDEO_SIGNAL_INFO union. Ignore only
                // AdditionalSignalInfo.reserved (bits 22..31), preserving timings,
                // refresh divider, dimensions, video standard and scan-line order.
                const ulong definedSignalBits = 0xffffffff003fffffUL;
                return x.Raw2 == y.Raw2 && x.Raw3 == y.Raw3 && x.Raw4 == y.Raw4 &&
                    x.Raw5 == y.Raw5 && x.Raw6 == y.Raw6 &&
                    (x.Raw7 & definedSignalBits) == (y.Raw7 & definedSignalBits);
            }
            // QueryOnlyActivePaths does not request virtual desktop-image modes.
            return false;
        }
        static HashSet<ulong> Adapters(Snapshot value)
        {
            HashSet<ulong> result = new HashSet<ulong>();
            foreach (PathInfo path in value.Paths) { result.Add(AdapterKey(path.Source.Adapter)); result.Add(AdapterKey(path.Target.Adapter)); }
            return result;
        }
        static bool SameAdapters(Snapshot a, Snapshot b) { return Adapters(a).SetEquals(Adapters(b)); }
        static bool SameTargets(Snapshot a, Snapshot b)
        {
            // Drivers may normalize source modes and positions. Verify the actual
            // physical target set rather than assuming a successful return means off.
            var targets = new HashSet<Tuple<ulong, uint>>();
            foreach (PathInfo path in a.Paths) targets.Add(Tuple.Create(AdapterKey(path.Target.Adapter), path.Target.Id));
            if (a.Paths.Length != b.Paths.Length) return false;
            foreach (PathInfo path in b.Paths)
                if (!targets.Remove(Tuple.Create(AdapterKey(path.Target.Adapter), path.Target.Id))) return false;
            return targets.Count == 0;
        }
        static ulong AdapterKey(Luid adapter) { return ((ulong)(uint)adapter.High << 32) | adapter.Low; }

        static Snapshot Capture()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint paths, modes;
                Check(GetDisplayConfigBufferSizes(QueryOnlyActivePaths, out paths, out modes), "Get display buffer sizes");
                PathInfo[] pathArray = new PathInfo[paths]; ModeInfo[] modeArray = new ModeInfo[modes];
                int error = QueryDisplayConfig(QueryOnlyActivePaths, ref paths, pathArray, ref modes, modeArray, IntPtr.Zero);
                if (error == 122) continue;
                Check(error, "Capture display configuration");
                Array.Resize(ref pathArray, (int)paths); Array.Resize(ref modeArray, (int)modes);
                return new Snapshot { Paths = pathArray, Modes = modeArray };
            }
            throw new InvalidOperationException("Display configuration is still changing.");
        }

        void WriteSnapshot(Snapshot value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journal)));
            string temporary = journal + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                WriteRecord(temporary, value);
                File.Move(temporary, journal); // Never replace a previous recovery record.
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        void RefreshRecovery()
        {
            string temporary = journal + ".refresh-" + Guid.NewGuid().ToString("N");
            try
            {
                WriteRecord(temporary, original);
                // Atomic replacement keeps the previous recovery intact if updating fails.
                File.Replace(temporary, journal, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        void WriteRecord(string file, Snapshot value)
        {
            using (FileStream stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(expected == null ? 0x42534431 : 0x42534432); WriteArray(writer, value.Paths); WriteArray(writer, value.Modes);
                if (expected != null) { WriteArray(writer, expected.Paths); WriteArray(writer, expected.Modes); }
                writer.Flush(); stream.Flush(true);
            }
        }

        void LoadRecovery()
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(journal)))
            {
                int format = reader.ReadInt32();
                if (format != 0x42534431 && format != 0x42534432) throw new InvalidDataException("Unknown recovery format.");
                Snapshot saved = new Snapshot { Paths = ReadArray<PathInfo>(reader), Modes = ReadArray<ModeInfo>(reader) };
                Snapshot sleep = format == 0x42534432 ? new Snapshot { Paths = ReadArray<PathInfo>(reader), Modes = ReadArray<ModeInfo>(reader) } : null;
                if (saved.Paths.Length == 0 || reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Invalid display recovery record.");
                ValidateSnapshot(saved, false);
                if (sleep != null) ValidateSnapshot(sleep, true);
                original = saved; expected = sleep;
            }
        }

        static void ValidateSnapshot(Snapshot value, bool allowEmpty)
        {
            if (value == null || value.Paths == null || value.Modes == null ||
                (!allowEmpty && value.Paths.Length == 0) || value.Paths.Length > 512 || value.Modes.Length > 512)
                throw new InvalidDataException("Invalid display recovery configuration.");
            HashSet<Tuple<ulong, uint, uint>> modes = new HashSet<Tuple<ulong, uint, uint>>();
            foreach (ModeInfo mode in value.Modes)
            {
                // This backend deliberately uses the non-virtual CCD representation.
                if ((mode.Type != 1 && mode.Type != 2) ||
                    !modes.Add(Tuple.Create(AdapterKey(mode.Adapter), mode.Id, mode.Type)) ||
                    (mode.Type == 1 && (mode.Width == 0 || mode.Height == 0)))
                    throw new InvalidDataException("Invalid or duplicate display recovery mode.");
            }
            HashSet<Tuple<ulong, uint>> targets = new HashSet<Tuple<ulong, uint>>();
            foreach (PathInfo path in value.Paths)
            {
                if ((path.Flags & 1) == 0 || !targets.Add(Tuple.Create(AdapterKey(path.Target.Adapter), path.Target.Id)))
                    throw new InvalidDataException("Invalid or duplicate active display recovery path.");
                ValidateModeReference(value, path.Source.Mode, 1, path.Source.Adapter, path.Source.Id);
                // Missing target timing is legal: SetDisplayConfig derives it from
                // the supplied source mode. A missing source is unsafe for recovery.
                if (path.Target.Mode != uint.MaxValue)
                    ValidateModeReference(value, path.Target.Mode, 2, path.Target.Adapter, path.Target.Id);
            }
        }

        static void ValidateModeReference(Snapshot value, uint index, uint type, Luid adapter, uint id)
        {
            if (index >= value.Modes.Length || value.Modes[index].Type != type ||
                value.Modes[index].Id != id || !value.Modes[index].Adapter.Equals(adapter))
                throw new InvalidDataException("Invalid display recovery mode reference.");
        }

        static void WriteArray<T>(BinaryWriter writer, T[] values) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T)); writer.Write(size); writer.Write(values.Length);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                byte[] bytes = new byte[size];
                foreach (T value in values) { Marshal.StructureToPtr(value, buffer, false); Marshal.Copy(buffer, bytes, 0, size); writer.Write(bytes); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        static T[] ReadArray<T>(BinaryReader reader) where T : struct
        {
            int size = reader.ReadInt32(), count = reader.ReadInt32();
            if (size != Marshal.SizeOf(typeof(T)) || count < 0 || count > 512) throw new InvalidDataException("Invalid recovery array.");
            T[] values = new T[count]; IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    byte[] bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException();
                    Marshal.Copy(bytes, 0, buffer, size); values[i] = (T)Marshal.PtrToStructure(buffer, typeof(T));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return values;
        }

        static void Check(int error, string operation) { if (error != 0) throw new Win32Exception(error, operation + ": " + new Win32Exception(error).Message); }
        internal sealed class Snapshot { internal PathInfo[] Paths; internal ModeInfo[] Modes; }
        [StructLayout(LayoutKind.Sequential)] internal struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)] internal struct SourceInfo { internal Luid Adapter; internal uint Id, Mode, Flags; }
        [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo
        {
            internal Luid Adapter;
            internal uint Id, Mode, Technology, Rotation, Scaling, RateNumerator, RateDenominator, Scanline;
            internal int Available;
            internal uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { internal SourceInfo Source; internal TargetInfo Target; internal uint Flags; }
        // Preserve the complete native 48-byte mode union without reinterpretation.
        [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct ModeInfo
        {
            [FieldOffset(0)] internal uint Type;
            [FieldOffset(4)] internal uint Id;
            [FieldOffset(8)] internal Luid Adapter;
            [FieldOffset(16)] internal uint Width;
            [FieldOffset(20)] internal uint Height;
            [FieldOffset(0)] internal ulong Raw0;
            [FieldOffset(8)] internal ulong Raw1;
            [FieldOffset(16)] internal ulong Raw2;
            [FieldOffset(24)] internal ulong Raw3;
            [FieldOffset(32)] internal ulong Raw4;
            [FieldOffset(40)] internal ulong Raw5;
            [FieldOffset(48)] internal ulong Raw6;
            [FieldOffset(56)] internal ulong Raw7;
            [FieldOffset(28)] internal int X;
            [FieldOffset(32)] internal int Y;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SourceName
        {
            internal uint Type, Size; internal Luid Adapter; internal uint Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TargetName
        {
            internal uint Type, Size; internal Luid Adapter; internal uint Id, Flags, Technology;
            internal ushort Manufacturer, Product; internal uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DevicePath;
        }
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, [Out] ModeInfo[] modeArray, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint paths, [In] PathInfo[] pathArray, uint modes, [In] ModeInfo[] modeArray, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DisplayConfigGetDeviceInfo(ref TargetName name);
    }
}
