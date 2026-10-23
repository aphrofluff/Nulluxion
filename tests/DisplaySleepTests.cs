using System;
using System.IO;
using System.Reflection;
using Nulluxion;

public static class DisplaySleepTests
{
    sealed class Backend : DisplaySleep.IBackend
    {
        internal DisplaySleep.Snapshot Current;
        internal int Writes, Error;
        internal bool Partial, NormalizeSleep;
        public DisplaySleep.Snapshot Capture() { return Copy(Current); }
        public int Set(DisplaySleep.Snapshot value, uint operation)
        {
            if (operation == 0x40) return 0;
            Writes++;
            if (Error == 0 || Partial)
            {
                Current = Copy(value);
                if (NormalizeSleep && Current.Paths.Length == 1) Current.Modes[Current.Paths[0].Source.Mode].X++;
            }
            return Error;
        }
        public string SourceDevice(DisplaySleep.PathInfo path) { return "DISPLAY" + path.Source.Id; }
    }
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
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
            s.Modes[i] = new DisplaySleep.ModeInfo { Raw0 = ((ulong)i << 32) | 1, Raw1 = adapter, X = i * 1920 };
        }
        return s;
    }
    public static void Run()
    {
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
            Assert(display.Sleep(new[] { "DISPLAY1" }) && display.CheckTopology(), "Own sleep transition");
            Assert(display.Restore() && !display.IsActive && fake.Writes == 2, "Normal restore");
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
            foreach (byte[] bad in new[] { new byte[0], new byte[] { 0x31, 0x44, 0x53, 0x42 }, new byte[] { 1, 2, 3, 4 } })
            {
                File.WriteAllBytes(journal, bad);
                display = new DisplaySleep(journal, fake);
                Assert(display.RecoveryBlocked && !display.Restore() && !display.Sleep(new[] { "DISPLAY1" }), "Corrupt journal remains blocked");
                using (FileStream held = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert(!display.ArchiveRecovery(out archive) && display.IsActive && display.RecoveryBlocked, "Archive failure retains blocked state");
                Assert(display.ArchiveRecovery(out archive) && File.Exists(archive), "Corrupt journal archive");
            }
            Console.WriteLine("PASS: display recovery synthetic topology, journal, rollback and archive tests");
        }
        finally { Directory.Delete(dir, true); }
    }
}
