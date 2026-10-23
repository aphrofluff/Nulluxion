// Copyright (C) 2026 Nulluxion contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Nulluxion;

public static class DisplaySleepTests
{
    static int assertions;
    sealed class Backend : DisplaySleep.IBackend
    {
        internal DisplaySleep.Snapshot Current;
        internal int Writes, Error;
        internal bool Partial, NormalizeSleep, IgnoreApply, ThrowAfterApply, FailNextCapture;
        internal Action OnValidate;
        public DisplaySleep.Snapshot Capture()
        {
            if (FailNextCapture) { FailNextCapture = false; throw new IOException("Synthetic capture failure"); }
            return Copy(Current);
        }
        public int Set(DisplaySleep.Snapshot value, uint operation)
        {
            if (operation == 0x40) { if (OnValidate != null) OnValidate(); return 0; }
            Writes++;
            if (!IgnoreApply && (Error == 0 || Partial))
            {
                Current = Copy(value);
                if (NormalizeSleep && Current.Paths.Length == 1) Current.Modes[Current.Paths[0].Source.Mode].X++;
            }
            if (ThrowAfterApply) FailNextCapture = true;
            return Error;
        }
        public string SourceDevice(DisplaySleep.PathInfo path) { return "DISPLAY" + path.Source.Id; }
    }
    static void Assert(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
    static DisplaySleep.Snapshot Copy(DisplaySleep.Snapshot s)
    {
        return new DisplaySleep.Snapshot { Paths = (DisplaySleep.PathInfo[])s.Paths.Clone(), Modes = (DisplaySleep.ModeInfo[])s.Modes.Clone() };
    }
    static DisplaySleep.Snapshot Desktop(int count, uint adapter)
    {
        DisplaySleep.Snapshot s = new DisplaySleep.Snapshot { Paths = new DisplaySleep.PathInfo[count], Modes = new DisplaySleep.ModeInfo[count] };
        for (int i = 0; i < count; i++)
        {
            DisplaySleep.Luid id = new DisplaySleep.Luid { Low = adapter };
            s.Paths[i] = new DisplaySleep.PathInfo { Source = new DisplaySleep.SourceInfo { Adapter = id, Id = (uint)i, Mode = (uint)i }, Target = new DisplaySleep.TargetInfo { Adapter = id, Id = (uint)i, Mode = uint.MaxValue }, Flags = 1 };
            s.Modes[i] = new DisplaySleep.ModeInfo { Raw0 = ((ulong)i << 32) | 1, Raw1 = adapter, Width = 1920, Height = 1080, Raw3 = 4, X = i * 1920 };
        }
        return s;
    }
    public static void Run()
    {
        Assert(Marshal.SizeOf(typeof(DisplaySleep.SourceInfo)) == 20 && Marshal.SizeOf(typeof(DisplaySleep.TargetInfo)) == 48 &&
            Marshal.SizeOf(typeof(DisplaySleep.PathInfo)) == 72 && Marshal.SizeOf(typeof(DisplaySleep.ModeInfo)) == 64, "Native CCD structure sizes");
        Assert(Marshal.OffsetOf(typeof(DisplaySleep.ModeInfo), "X").ToInt32() == 28 &&
            Marshal.OffsetOf(typeof(DisplaySleep.ModeInfo), "Y").ToInt32() == 32, "Native source position offsets");
        Assert(Marshal.SizeOf(typeof(DisplaySleep).GetNestedType("SourceName", BindingFlags.NonPublic)) == 84 &&
            Marshal.SizeOf(typeof(DisplaySleep).GetNestedType("TargetName", BindingFlags.NonPublic)) == 420, "Native device-name structure sizes");
        DisplaySleep.Snapshot baseline = Desktop(2, 1), stale = Desktop(2, 77);
        Assert(DisplaySleep.EquivalentConfiguration(baseline, stale), "Single adapter stale LUID");
        DisplaySleep.Snapshot reordered = Copy(baseline);
        Array.Reverse(reordered.Paths); Array.Reverse(reordered.Modes);
        for (int i = 0; i < 2; i++) reordered.Paths[i].Source.Mode = (uint)i;
        Assert(DisplaySleep.EquivalentConfiguration(baseline, reordered), "Order independent paths and modes");
        var noise = Copy(baseline);
        noise.Paths[0].Source.Flags = 123; noise.Paths[0].Target.Flags = 456; noise.Paths[0].Target.Available = 1;
        noise.Modes[0].Raw4 |= 0xabcdef0000000000UL;
        noise.Modes[0].Raw5 = noise.Modes[0].Raw6 = noise.Modes[0].Raw7 = ulong.MaxValue;
        Assert(DisplaySleep.EquivalentConfiguration(baseline, noise), "Query status and unused source union bytes are ignored");
        var targetModes = Copy(baseline);
        Array.Resize(ref targetModes.Modes, 3);
        targetModes.Paths[0].Target.Mode = 2;
        targetModes.Modes[2] = new DisplaySleep.ModeInfo { Type = 2, Raw1 = 1, Raw2 = 148500000, Raw3 = (1UL << 32) | 67500, Raw4 = (1UL << 32) | 60, Raw5 = (1080UL << 32) | 1920, Raw6 = (1125UL << 32) | 2200, Raw7 = 1UL << 32 };
        var targetNoise = Copy(targetModes);
        targetNoise.Modes[2].Raw7 |= 0xffc00000UL;
        Assert(DisplaySleep.EquivalentConfiguration(targetModes, targetNoise), "Reserved target signal bits ignored");
        targetNoise.Modes[2].Raw4++;
        Assert(!DisplaySleep.EquivalentConfiguration(targetModes, targetNoise), "Target refresh change detected");
        var pathRefresh = Copy(baseline); pathRefresh.Paths[0].Target.RateNumerator++;
        Assert(!DisplaySleep.EquivalentConfiguration(baseline, pathRefresh), "Path refresh change detected");
        var resolution = Copy(baseline); resolution.Modes[0].Raw2++;
        Assert(!DisplaySleep.EquivalentConfiguration(baseline, resolution), "Source dimensions change detected");
        stale.Modes[1].X++;
        Assert(!DisplaySleep.EquivalentConfiguration(baseline, stale), "Changed layout");
        Assert(!DisplaySleep.EquivalentConfiguration(baseline, Desktop(1, 77)), "Missing display");
        var multi = Copy(baseline); multi.Paths[1].Source.Adapter.Low = multi.Paths[1].Target.Adapter.Low = 2; multi.Modes[1].Raw1 = 2;
        var multiStale = Copy(multi); multiStale.Paths[0].Source.Adapter.Low = multiStale.Paths[0].Target.Adapter.Low = 77; multiStale.Modes[0].Raw1 = 77;
        Assert(!DisplaySleep.EquivalentConfiguration(multi, multiStale), "Ambiguous multi adapter LUID");
        Assert(DisplaySleep.EquivalentConfiguration(multi, Copy(multi)), "Same multi adapter configuration");
        string dir = Path.Combine(Path.GetTempPath(), "Nulluxion-display-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string journal = Path.Combine(dir, "recovery.bin");
        try
        {
            Backend fake = new Backend { Current = Copy(baseline) };
            DisplaySleep display = new DisplaySleep(journal, fake);
            Assert(!display.Sleep(new[] { "DISPLAY0", "DISPLAY1" }) && fake.Writes == 0 && !display.IsActive, "Cannot suspend every active source");
            Assert(!display.Sleep(new[] { "DISPLAY99" }) && fake.Writes == 0 && !display.IsActive, "Missing target cannot suspend another source");
            Assert(display.Sleep(new[] { "DISPLAY1" }) && display.CheckTopology(), "Own sleep transition");
            Assert(display.Restore() && !display.IsActive && fake.Writes == 2, "Normal restore");
            Assert(display.Sleep(new[] { "DISPLAY0" }) && fake.Current.Modes[1].X == 0, "Sleeping primary moves retained source to origin");
            Assert(display.Restore() && fake.Current.Modes[1].X == 1920, "Primary restore retains original coordinates");
            fake.IgnoreApply = true;
            Assert(!display.Sleep(new[] { "DISPLAY1" }) && display.IsActive && File.Exists(journal), "Driver success without suspending signal is not reported as powered off");
            fake.IgnoreApply = false;
            int unchangedWrites = fake.Writes;
            Assert(display.Restore() && fake.Writes == unchangedWrites, "Ignored suspension retires recovery after confirming unchanged desktop");
            fake.ThrowAfterApply = true;
            Assert(!display.Sleep(new[] { "DISPLAY1" }) && display.IsActive && File.Exists(journal), "Unverified suspension retains recovery and reports failure");
            fake.ThrowAfterApply = false;
            Assert(display.Restore(), "Unverified suspension restores from saved expected topology");
            fake.OnValidate = delegate { fake.Current = Desktop(3, 1); };
            int beforeRace = fake.Writes;
            Assert(!display.Sleep(new[] { "DISPLAY1" }) && fake.Writes == beforeRace && !display.IsActive, "Topology race before apply aborts without journal or writes");
            fake.OnValidate = null; fake.Current = Copy(baseline);

            var extreme = Desktop(3, 1);
            extreme.Modes[1].X = int.MinValue; extreme.Modes[2].X = int.MaxValue;
            fake.Current = extreme;
            Assert(!display.Sleep(new[] { "DISPLAY0" }) && !display.IsActive && fake.Writes == beforeRace, "Coordinate overflow aborts without apply");
            var cloned = Desktop(3, 1); cloned.Paths[1].Source = cloned.Paths[0].Source;
            fake.Current = Copy(cloned);
            Assert(display.Sleep(new[] { "DISPLAY0" }) && fake.Current.Paths.Length == 1 && fake.Current.Paths[0].Source.Id == 2, "Clone paths sharing a source suspend together");
            Assert(display.Restore() && DisplaySleep.EquivalentConfiguration(cloned, fake.Current), "Cloned topology restores shared source reference");
            fake.Current = Copy(baseline);

            Assert(display.Sleep(new[] { "DISPLAY1" }), "Sleep before ignored restore");
            fake.IgnoreApply = true;
            Assert(!display.Restore() && display.IsActive && File.Exists(journal), "Successful driver return without restoring retains recovery");
            fake.IgnoreApply = false;
            display = new DisplaySleep(journal, fake);
            Assert(display.Restore(), "Retry unverified restore after restart");

            Assert(display.Sleep(new[] { "DISPLAY1" }), "Sleep before verification capture failure");
            fake.ThrowAfterApply = true;
            Assert(!display.Restore() && display.IsActive && File.Exists(journal), "Capture failure after restore retains recovery");
            fake.ThrowAfterApply = false;
            int restoredWrites = fake.Writes;
            Assert(display.Restore() && fake.Writes == restoredWrites, "Verified retry retires recovery without redundant apply");
            Assert(display.Sleep(new[] { "DISPLAY1" }), "Second sleep");
            fake.Current = Desktop(3, 1);
            int writes = fake.Writes;
            Assert(!display.CheckTopology() && display.RecoveryBlocked, "External addition blocks");
            Assert(!display.Restore() && fake.Writes == writes && File.Exists(journal), "Conflict preserves journal without writes");
            string archive;
            Assert(display.ArchiveRecovery(out archive) && File.Exists(archive) && !display.IsActive, "Explicit archive preserves backup");
            fake.Current = Copy(baseline); fake.Error = 87; fake.Partial = true;
            Assert(!display.Sleep(new[] { "DISPLAY1" }) && display.IsActive, "Failed partial apply retains recovery");
            fake.Error = 0;
            Assert(display.Restore() && DisplaySleep.EquivalentConfiguration(fake.Current, baseline), "Failed apply rollback");
            // Simulate restart with a persisted expected topology.
            fake.NormalizeSleep = true;
            Assert(display.Sleep(new[] { "DISPLAY1" }), "Sleep before restart");
            fake.NormalizeSleep = false;
            display = new DisplaySleep(journal, fake);
            Assert(display.Restore(), "Restart recovery with expected topology");
            Assert(display.Sleep(new[] { "DISPLAY1" }), "Sleep before failed restore");
            fake.Error = 87; fake.Partial = true;
            Assert(!display.Restore() && display.IsActive && !display.RecoveryBlocked, "Partial restore failure retains retry");
            fake.Error = 0;
            Assert(display.Restore(), "Retry partial restore");
            // Preserve the original user's stale-LUID retirement regression, entirely synthetic.
            typeof(DisplaySleep).GetMethod("WriteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(display, new object[] { Desktop(2, 77) });
            writes = fake.Writes;
            Assert(display.Restore() && fake.Writes == writes && !File.Exists(journal), "Stale journal retired with no native apply");
            Action<DisplaySleep.Snapshot>[] corruptions = new Action<DisplaySleep.Snapshot>[] {
                s => s.Paths[0].Source.Mode = uint.MaxValue,
                s => s.Paths[0].Source.Mode = 99,
                s => s.Paths[0].Source.Mode = 1,
                s => s.Paths[0].Target.Mode = 0,
                s => s.Modes[0].Type = 3,
                s => s.Modes[0].Adapter.Low = 99,
                s => s.Modes[0].Id = 99,
                s => s.Modes[0].Width = 0,
                s => s.Modes[1] = s.Modes[0],
                s => s.Paths[1].Target = s.Paths[0].Target,
                s => s.Paths[0].Flags = 0
            };
            foreach (Action<DisplaySleep.Snapshot> corrupt in corruptions)
            {
                DisplaySleep.Snapshot invalid = Copy(baseline); corrupt(invalid);
                typeof(DisplaySleep).GetMethod("WriteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(display, new object[] { invalid });
                writes = fake.Writes;
                display = new DisplaySleep(journal, fake);
                Assert(display.RecoveryBlocked && !display.Restore() && fake.Writes == writes && File.Exists(journal), "Invalid recovery structure blocked before apply");
                Assert(display.ArchiveRecovery(out archive) && File.Exists(archive), "Invalid structure archive retained");
            }
            var invalidExpected = Copy(baseline); invalidExpected.Paths[0].Source.Mode = 99;
            typeof(DisplaySleep).GetField("expected", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(display, invalidExpected);
            typeof(DisplaySleep).GetMethod("WriteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(display, new object[] { baseline });
            display = new DisplaySleep(journal, fake);
            Assert(display.RecoveryBlocked, "Invalid expected topology blocked");
            Assert(display.ArchiveRecovery(out archive), "Invalid expected topology archive");
            Directory.CreateDirectory(journal);
            display = new DisplaySleep(journal, fake);
            writes = fake.Writes;
            Assert(display.RecoveryBlocked && display.IsActive && !display.Sleep(new[] { "DISPLAY1" }) && !display.Restore() && fake.Writes == writes,
                "Unreadable recovery path blocks display changes");
            Assert(!display.ArchiveRecovery(out archive) && display.RecoveryBlocked && display.IsActive, "Unreadable recovery cannot be silently archived");
            Directory.Delete(journal);
            Assert(display.ArchiveRecovery(out archive), "Explicit archive clears resolved recovery-path obstruction");
            foreach (byte[] bad in new[] { new byte[0], new byte[] { 0x31, 0x44, 0x53, 0x42 }, new byte[] { 1, 2, 3, 4 } })
            {
                File.WriteAllBytes(journal, bad);
                display = new DisplaySleep(journal, fake);
                Assert(display.RecoveryBlocked && !display.Restore() && !display.Sleep(new[] { "DISPLAY1" }), "Corrupt journal remains blocked");
                using (FileStream held = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert(!display.ArchiveRecovery(out archive) && display.IsActive && display.RecoveryBlocked, "Archive failure retains blocked state");
                Assert(display.ArchiveRecovery(out archive) && File.Exists(archive), "Corrupt journal archive");
            }
            Console.WriteLine("PASS: " + assertions + " display recovery assertions (synthetic topology, journal, rollback and archive tests).");
        }
        finally { Directory.Delete(dir, true); }
    }
}
