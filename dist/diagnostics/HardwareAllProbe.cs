using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Blackout;

internal static class HardwareAllProbe
{
    sealed class LoggedBackend : HardwarePower.IBackend
    {
        internal HardwarePower.IBackend Inner;
        internal uint Off = 4;
        public System.Collections.Generic.List<HardwarePower.Monitor> Enumerate()
        {
            var monitors = Inner.Enumerate();
            foreach (var monitor in monitors) Console.WriteLine("ENUM " + monitor.Name + " handle=" + monitor.Handle + " identity=" + monitor.Identity);
            return monitors;
        }
        public string[] SignalSleepTargets(string[] names) { return Inner.SignalSleepTargets(names); }
        public string Capabilities(HardwarePower.Monitor monitor) { return Inner.Capabilities(monitor); }
        public bool IsOn(HardwarePower.Monitor monitor) { return Inner.IsOn(monitor); }
        public bool SetPower(HardwarePower.Monitor monitor, uint value) { return Inner.SetPower(monitor, value == 4 ? Off : value); }
    }
    static void Wait(Func<bool> ready, string message)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(20);
        while (!ready() && DateTime.UtcNow < until) Thread.Sleep(50);
        if (!ready()) throw new Exception(message);
    }
    static DisplaySleep.Snapshot Capture()
    {
        return (DisplaySleep.Snapshot)typeof(DisplaySleep).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    }
    [STAThread]
    static int Main(string[] commandLine)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Blackout-live-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Console.WriteLine("Recovery directory (retained): " + directory);
        DisplaySleep.Snapshot before = Capture();
        var backend = (HardwarePower.IBackend)Activator.CreateInstance(typeof(HardwarePower).GetNestedType("NativeBackend", BindingFlags.NonPublic), true);
        var display = new DisplaySleep(Path.Combine(directory, "display-recovery.bin"));
        var power = new HardwarePower(new LoggedBackend { Inner = backend, Off = commandLine.Length > 0 ? uint.Parse(commandLine[0]) : 4 }, display, Path.Combine(directory, "power-recovery.txt"), 2000);
        power.Status += delegate(string message) { Console.WriteLine(DateTime.UtcNow.ToString("o") + " " + message); };
        try
        {
            Wait(delegate { return power.IsRestored; }, "Initial recovery did not finish");
            string[] names = Array.ConvertAll(Screen.AllScreens, delegate(Screen screen) { return screen.DeviceName; });
            power.SetAllTargets(names);
            Wait(delegate { return power.AllScreensPoweredOff || power.NeedsDisplaySleepFallback; }, "No hardware preparation result");
            if (!power.AllScreensPoweredOff) throw new Exception("Direct hardware off not supported on this setup");
            Console.WriteLine("Holding direct hardware off for 20 seconds; no Windows sleep command used.");
            foreach (int delay in new int[] { 0, 1000, 4000, 15000 })
            {
                Thread.Sleep(delay);
                var samples = (System.Collections.IEnumerable)typeof(HardwarePower).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(power);
                foreach (HardwarePower.Monitor monitor in samples)
                {
                    object[] sample = { monitor.Handle, (byte)0xD6, 0, (uint)0, (uint)0 };
                    bool readable = (bool)typeof(HardwarePower).GetMethod("GetVCPFeatureAndVCPFeatureReply", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, sample);
                    Console.WriteLine(DateTime.UtcNow.ToString("o") + " sample readable=" + readable + " value=" + sample[3]);
                }
            }
            DisplaySleep.Snapshot during = Capture();
            Console.WriteLine("Active signal paths before=" + before.Paths.Length + " after20s=" + during.Paths.Length);
            if (during.Paths.Length >= before.Paths.Length || !display.IsActive) throw new Exception("BenQ signal did not stay suspended");
            var pending = (System.Collections.IEnumerable)typeof(HardwarePower).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(power);
            int checkedMonitors = 0;
            foreach (HardwarePower.Monitor monitor in pending)
            {
                object[] args = { monitor.Handle, (byte)0xD6, 0, (uint)0, (uint)0 };
                bool readable = (bool)typeof(HardwarePower).GetMethod("GetVCPFeatureAndVCPFeatureReply", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
                Console.WriteLine("DDC power read after20s: " + monitor.Name + " readable=" + readable + " value=" + args[3]);
                if (!readable || (uint)args[3] != 4) throw new Exception("Could not confirm monitor remained powered off");
                checkedMonitors++;
            }
            if (checkedMonitors == 0) throw new Exception("No direct DDC monitor was checked");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            try
            {
                power.Restore();
                Wait(delegate { return power.IsRestored; }, "Restoration incomplete; retain recovery directory " + directory);
                if (!DisplaySleep.EquivalentConfiguration(before, Capture())) throw new Exception("Restored topology differs");
                Console.WriteLine("PASS: original display layout restored; journals cleared.");
            }
            finally { power.Dispose(); }
        }
        Console.WriteLine("PASS: direct hardware off survived 20 seconds and restored successfully.");
        return 0;
    }
}
