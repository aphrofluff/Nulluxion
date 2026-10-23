using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace Nulluxion
{
    public class Settings
    {
        public uint Modifiers = 3;
        public uint Key = (uint)Keys.B;
        public bool UsePointer;
        public bool TurnOffBacklights = true;
        // Keep the legacy data location so existing settings and recovery records remain usable.
        public static readonly string FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackout", "settings.xml");
        public static Settings Load()
        {
            try { using (var file = File.OpenRead(FileName)) return (Settings)new XmlSerializer(typeof(Settings)).Deserialize(file) ?? new Settings(); }
            catch { return new Settings(); }
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileName));
            string temporary = FileName + ".tmp";
            using (var file = File.Create(temporary)) new XmlSerializer(typeof(Settings)).Serialize(file, this);
            if (File.Exists(FileName)) File.Replace(temporary, FileName, null); else File.Move(temporary, FileName);
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LastInputInfo info);
        [StructLayout(LayoutKind.Sequential)] internal struct LastInputInfo { internal uint Size, Time; }
    }

    // SC_MONITORPOWER can be undone by the key-up events of the shortcut that
    // requested it. Observe a quiet period AFTER all keys/buttons are released.
    internal sealed class DisplaySleepInput
    {
        internal const uint QuietMilliseconds = 800;
        bool observing, previousHeld;
        uint previousInput, quietSince;
        internal bool IsQuiet()
        {
            var info = new Native.LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(Native.LastInputInfo)) };
            if (!Native.GetLastInputInfo(ref info)) { Reset(); return false; }
            bool held = false;
            // Virtual keys include physical mouse buttons; do not use the
            // unreliable low bit ("pressed since last call") of GetAsyncKeyState.
            for (int key = 1; key < 255; key++)
                if (Native.GetAsyncKeyState(key) < 0) { held = true; break; }
            return Observe(unchecked((uint)Environment.TickCount), info.Time, held);
        }
        internal bool Observe(uint now, uint lastInput, bool held)
        {
            // Input timestamps may move backwards (e.g. SendInput). Any change
            // restarts the interval; elapsed time uses our own monotonic tick.
            if (!observing || held || previousHeld || previousInput != lastInput)
            {
                observing = true;
                previousHeld = held;
                previousInput = lastInput;
                quietSince = now;
                return false;
            }
            return unchecked(now - quietSince) >= QuietMilliseconds;
        }
        internal void Reset() { observing = false; previousHeld = false; }
    }

    internal sealed class Overlay : Form
    {
        internal Overlay(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; }
        }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        internal Action<int> Pressed;
        internal HotkeyWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && Pressed != null) Pressed(message.WParam.ToInt32());
            base.WndProc(ref message);
        }
        public void Dispose() { Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2); Native.UnregisterHotKey(Handle, 3); DestroyHandle(); }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        internal readonly Settings Settings;
        private readonly HotkeyWindow hotkeys = new HotkeyWindow();
        private readonly List<Overlay> overlays = new List<Overlay>();
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem toggle;
        private readonly ToolStripMenuItem toggleAll;
        private bool allScreens;
        private SettingsForm window;
        private readonly Icon icon;
        private readonly IPowerController hardware;
        private readonly bool interactive;
        private readonly Action<bool> displayPower;
        private readonly Func<bool> inputIsQuiet;
        private readonly ToolStripMenuItem retireRecovery;
        internal string HardwareStatus = "Hardware power enabled for compatible DDC/CI monitors.";
        private readonly System.Windows.Forms.Timer sleepTimer = new System.Windows.Forms.Timer { Interval = 800 };
        private bool displaySleepRequested;
        private bool windowsSleepFallback;
        internal bool IsActive { get { return overlays.Count > 0; } }
        internal static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        internal TrayApp(bool startup) : this(startup, new HardwarePower(), Settings.Load(), true, null) { }
        internal TrayApp(bool startup, IPowerController power, Settings settings, bool interactive, Action<bool> displayPower)
            : this(startup, power, settings, interactive, displayPower, null) { }
        internal TrayApp(bool startup, IPowerController power, Settings settings, bool interactive, Action<bool> displayPower, Func<bool> inputIsQuiet)
        {
            if (power == null) throw new ArgumentNullException("power");
            hardware = power;
            Settings = settings ?? new Settings();
            this.interactive = interactive;
            this.inputIsQuiet = inputIsQuiet ?? (interactive ? new Func<bool>(new DisplaySleepInput().IsQuiet) : delegate { return true; });
            this.displayPower = displayPower ?? delegate(bool sleep) { Native.DefWindowProc(hotkeys.Handle, 0x0112, new IntPtr(0xF170), new IntPtr(sleep ? 2 : -1)); };
            icon = Icon.ExtractAssociatedIcon(ExePath) ?? (Icon)SystemIcons.Application.Clone();
            var menu = new ContextMenuStrip();
            toggle = new ToolStripMenuItem("Black out other monitors", null, delegate { Toggle(); });
            menu.Items.Add(toggle);
            toggleAll = new ToolStripMenuItem("Black out all monitors (Ctrl+Alt+Shift+B)", null, delegate { ToggleAll(); });
            menu.Items.Add(toggleAll);
            menu.Items.Add("Settings", null, delegate { OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit Nulluxion", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = icon, Text = "Nulluxion", ContextMenuStrip = menu, Visible = interactive };
            sleepTimer.Tick += delegate { RequestDisplaySleep(); };
            hardware.Status += delegate(string message)
            {
                uiContext.Post(delegate
                {
                    if (disposed) return;
                    HardwareStatus = message;
                    retireRecovery.Enabled = hardware.HasUnresolvedRecovery;
                    if (window != null && !window.IsDisposed) window.UpdateHardwareStatus();
                }, null);
            };
            hardware.Restored += delegate
            {
                uiContext.Post(delegate
                {
                    if (disposed) return;
                    retireRecovery.Enabled = hardware.HasUnresolvedRecovery;
                    HardwareStatus = hardware.LastStatus;
                    if (window != null && !window.IsDisposed) window.UpdateHardwareStatus();
                    if (IsActive && allScreens && windowsSleepFallback && Settings.TurnOffBacklights && !displaySleepRequested)
                    {
                        sleepTimer.Interval = 800;
                        sleepTimer.Start();
                    }
                }, null);
            };
            hardware.AllScreensPrepared += delegate
            {
                uiContext.Post(delegate
                {
                    if (disposed || !IsActive || !allScreens || !Settings.TurnOffBacklights) return;
                    // Results are generation-checked by the controller: ignore a
                    // completion queued before cancellation or another request.
                    if (hardware.NeedsDisplaySleepFallback && !windowsSleepFallback)
                    {
                        windowsSleepFallback = true;
                        hardware.Restore(); // Undo partial hardware off before Windows sleep.
                    }
                    else if (hardware.AllScreensPoweredOff)
                    {
                        windowsSleepFallback = false;
                        sleepTimer.Stop();
                    }
                }, null);
            };
            hardware.ExternalDisplayChanged += DispatchRestore;
            menu.Items.Insert(2, new ToolStripMenuItem("Restore / retry monitor power", null, delegate { Restore(); }));
            retireRecovery = new ToolStripMenuItem("Retire saved recovery...", null, delegate { ConfirmRetireRecovery(); });
            retireRecovery.Enabled = hardware.HasUnresolvedRecovery;
            menu.Items.Insert(3, retireRecovery);
            HardwareStatus = hardware.LastStatus;
            tray.DoubleClick += delegate { OpenSettings(); };
            hotkeys.Pressed = delegate(int id) { if (id == 2) Restore(); else if (id == 3) ToggleAll(); else if (id == 1) Toggle(); };
            bool allRegistered = !interactive || Native.RegisterHotKey(hotkeys.Handle, 3, 7 | 0x4000, (uint)Keys.B);
            bool registered = !interactive || Native.RegisterHotKey(hotkeys.Handle, 1, Settings.Modifiers | 0x4000, Settings.Key);
            if (interactive)
            {
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionEnding += SessionEnding;
            if (!startup || !registered) OpenSettings();
            if (!registered) MessageBox.Show("Your shortcut is already in use. Choose another shortcut in Nulluxion settings.", "Nulluxion", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!allRegistered) tray.ShowBalloonTip(4000, "Nulluxion", "Ctrl+Alt+Shift+B is already in use. All-screen blackout is still available from the tray menu. Restart Nulluxion after freeing the shortcut.", ToolTipIcon.Info);
            }
        }
        // Used by the isolated smoke test; never creates a real recovery worker.
        internal static TrayApp CreateForSmokeTest()
        {
            return new TrayApp(true, new OverlayOnlyPower(), new Settings { TurnOffBacklights = false }, false, delegate { });
        }
        internal void RequestDisplaySleep()
        {
            sleepTimer.Stop();
            if (disposed || !IsActive || !allScreens || !windowsSleepFallback || !Settings.TurnOffBacklights || !hardware.IsRestored || displaySleepRequested) return;
            if (!inputIsQuiet())
            {
                sleepTimer.Interval = 100;
                sleepTimer.Start();
                return;
            }
            displaySleepRequested = true;
            HardwareStatus = "Windows display sleep requested. Keyboard or mouse input can wake displays; the shortcut removes overlays.";
            if (window != null && !window.IsDisposed) window.UpdateHardwareStatus();
            // Finish UI work before issuing the power command. Do not repeatedly
            // sleep displays after subsequent intentional input wakes them.
            displayPower(true);
        }
        internal void ConfirmRetireRecovery()
        {
            Restore();
            if (MessageBox.Show("Use this only after you have restored your monitors and layout manually. Nulluxion will retry restoration, then archive any remaining recovery records. Archived records will no longer be retried automatically.\n\nCurrent status: " + hardware.LastStatus + "\n\nRetire saved recovery?", "Nulluxion recovery", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                hardware.RetireRecovery();
        }
        internal void Toggle()
        {
            if (IsActive) { Restore(); return; }
            BlackOut(false);
        }
        internal void ToggleAll()
        {
            if (IsActive && allScreens) { Restore(); return; }
            if (IsActive) Restore();
            BlackOut(true);
        }
        private void BlackOut(bool includeAll)
        {
            Screen keep = Settings.UsePointer ? Screen.FromPoint(Cursor.Position) : Screen.FromHandle(Native.GetForegroundWindow());
            if (!includeAll && Screen.AllScreens.Length < 2)
            {
                tray.ShowBalloonTip(2500, "Nulluxion", "Connect another monitor to use Nulluxion.", ToolTipIcon.Info);
                return;
            }
            if (interactive && !Native.RegisterHotKey(hotkeys.Handle, 2, 0x4000, (uint)Keys.Escape))
            {
                tray.ShowBalloonTip(2500, "Nulluxion", "Escape is in use by another app. Use your Nulluxion shortcut or tray menu to restore.", ToolTipIcon.Info);
            }
            try
            {
                var targets = new List<string>();
                foreach (Screen screen in Screen.AllScreens)
                    if (includeAll || screen.DeviceName != keep.DeviceName)
                    {
                        var overlay = new Overlay(screen.Bounds); overlays.Add(overlay); if (interactive) overlay.Show();
                        targets.Add(screen.DeviceName);
                    }
                allScreens = includeAll;
                windowsSleepFallback = false;
                toggle.Text = "Restore all monitors";
                toggleAll.Text = includeAll ? "Restore all monitors (Ctrl+Alt+Shift+B)" : "Black out all monitors (Ctrl+Alt+Shift+B)";
                tray.Text = "Nulluxion — active";
                if (Settings.TurnOffBacklights)
                {
                    // Direct hardware off survives background Windows input.
                    // Use Windows sleep only if not every target can be controlled.
                    if (includeAll) hardware.SetAllTargets(targets.ToArray());
                    else hardware.SetTargets(targets.ToArray());
                }
            }
            catch { Restore(); throw; }
        }
        internal void Restore()
        {
            sleepTimer.Stop();
            windowsSleepFallback = false;
            if (displaySleepRequested)
            {
                displayPower(false);
                displaySleepRequested = false;
            }
            hardware.Restore();
            foreach (var overlay in overlays) overlay.Dispose();
            overlays.Clear();
            allScreens = false;
            Native.UnregisterHotKey(hotkeys.Handle, 2);
            toggle.Text = "Black out other monitors";
            toggleAll.Text = "Black out all monitors (Ctrl+Alt+Shift+B)";
            tray.Text = "Nulluxion";
        }
        private void DisplayChanged(object sender, EventArgs args)
        {
            hardware.NotifyTopologyChanged();
        }
        private void SessionChanged(object sender, SessionSwitchEventArgs args) { DispatchRestore(); }
        private void PowerChanged(object sender, PowerModeChangedEventArgs args) { DispatchRestore(); }
        private void SessionEnding(object sender, SessionEndingEventArgs args) { hardware.Restore(); }
        private void DispatchRestore()
        {
            // SystemEvents may invoke from a worker thread; marshal through the UI context.
            uiContext.Post(delegate { if (!disposed) Restore(); }, null);
        }
        private readonly SynchronizationContext uiContext = new WindowsFormsSynchronizationContext();
        private bool disposed;
        internal bool ChangeShortcut(uint modifiers, uint key)
        {
            // Keep the all-screen shortcut separate, even if its registration failed.
            if (modifiers == 7 && key == (uint)Keys.B) return false;
            Native.UnregisterHotKey(hotkeys.Handle, 1);
            if (!Native.RegisterHotKey(hotkeys.Handle, 1, modifiers | 0x4000, key))
            {
                Native.RegisterHotKey(hotkeys.Handle, 1, Settings.Modifiers | 0x4000, Settings.Key);
                return false;
            }
            uint oldModifiers = Settings.Modifiers, oldKey = Settings.Key;
            Settings.Modifiers = modifiers; Settings.Key = key;
            try { Settings.Save(); }
            catch
            {
                Settings.Modifiers = oldModifiers; Settings.Key = oldKey;
                Native.UnregisterHotKey(hotkeys.Handle, 1);
                Native.RegisterHotKey(hotkeys.Handle, 1, oldModifiers | 0x4000, oldKey);
                throw;
            }
            return true;
        }
        internal static bool StartsOnBoot()
        {
            // Retain the original Run value name to avoid duplicate startup entries after upgrading.
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && string.Equals(key.GetValue("Blackout") as string, "\"" + ExePath + "\" --startup", StringComparison.OrdinalIgnoreCase);
        }
        internal static void SetStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                if (enabled) key.SetValue("Blackout", "\"" + ExePath + "\" --startup"); else key.DeleteValue("Blackout", false);
        }
        private void OpenSettings()
        {
            Restore();
            if (window == null || window.IsDisposed) window = new SettingsForm(this, icon);
            window.Show(); window.WindowState = FormWindowState.Normal; window.Activate();
        }
        protected override void ExitThreadCore()
        {
            if (disposed) return;
            disposed = true;
            hardware.ExternalDisplayChanged -= DispatchRestore;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.SessionSwitch -= SessionChanged;
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.SessionEnding -= SessionEnding;
            Restore(); hardware.Dispose(); sleepTimer.Dispose(); hotkeys.Dispose(); tray.Visible = false;
            tray.ContextMenuStrip.Dispose(); tray.Dispose();
            if (window != null) window.Dispose();
            icon.Dispose(); base.ExitThreadCore();
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TrayApp app;
        private readonly TextBox shortcut;
        private readonly Label status;
        private readonly Label hardwareStatus;
        internal SettingsForm(TrayApp app, Icon icon)
        {
            this.app = app;
            Text = "Nulluxion"; Icon = icon; ClientSize = new Size(460, 740);
            Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(22, 22, 24); ForeColor = Color.WhiteSmoke;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
            Controls.Add(new Label { Text = "Nulluxion", Font = new Font("Segoe UI", 26, FontStyle.Bold), Location = new Point(28, 22), AutoSize = true });
            Controls.Add(new Label { Text = "One screen. Your full attention.", Location = new Point(30, 78), AutoSize = true, ForeColor = Color.Silver });
            Controls.Add(new Label { Text = "Toggle shortcut", Location = new Point(30, 124), AutoSize = true });
            shortcut = new TextBox { Location = new Point(30, 151), Size = new Size(398, 30), ReadOnly = true, BackColor = Color.FromArgb(40, 40, 44), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            shortcut.Text = ShortcutText(); shortcut.KeyDown += CaptureShortcut;
            Controls.Add(shortcut);
            Controls.Add(new Label { Text = "Click above, then press your new shortcut.", Location = new Point(30, 187), AutoSize = true, ForeColor = Color.Silver });
            var startup = new CheckBox { Text = "Start Nulluxion when I sign in", Location = new Point(30, 231), AutoSize = true, Checked = TrayApp.StartsOnBoot() };
            bool updatingStartup = false;
            startup.CheckedChanged += delegate
            {
                if (updatingStartup) return;
                bool old = !startup.Checked;
                try { TrayApp.SetStartup(startup.Checked); status.Text = "Startup setting saved."; }
                catch (Exception ex) { updatingStartup = true; startup.Checked = old; updatingStartup = false; status.Text = "Could not change startup: " + ex.Message; }
            };
            Controls.Add(startup);
            var pointer = new CheckBox { Text = "Keep the monitor with my mouse pointer", Location = new Point(30, 266), AutoSize = true, Checked = app.Settings.UsePointer };
            bool updatingPointer = false;
            pointer.CheckedChanged += delegate
            {
                if (updatingPointer) return;
                bool old = app.Settings.UsePointer;
                try { app.Settings.UsePointer = pointer.Checked; app.Settings.Save(); status.Text = "Monitor preference saved."; }
                catch (Exception ex) { app.Settings.UsePointer = old; updatingPointer = true; pointer.Checked = old; updatingPointer = false; status.Text = "Could not save: " + ex.Message; }
            };
            Controls.Add(pointer);
            Controls.Add(new Label { Text = "By default, keep the active window’s monitor.\nPress the shortcut again or Escape to restore.", Location = new Point(30, 307), Size = new Size(400, 46), ForeColor = Color.Silver });
            Controls.Add(new Label { Text = "All screens: Ctrl + Alt + Shift + B (fixed)\nPress again to restore all screens.", Location = new Point(30, 363), Size = new Size(400, 46), ForeColor = Color.Silver });
            var backlights = new CheckBox { Text = "Turn off LCD backlights when blacking out", Location = new Point(30, 420), AutoSize = true, Checked = app.Settings.TurnOffBacklights };
            bool updatingBacklights = false;
            backlights.CheckedChanged += delegate
            {
                if (updatingBacklights) return;
                bool old = app.Settings.TurnOffBacklights;
                try
                {
                    app.Settings.TurnOffBacklights = backlights.Checked;
                    app.Settings.Save();
                    if (!backlights.Checked) app.Restore();
                    UpdateHardwareStatus();
                    status.Text = "Backlight preference saved.";
                }
                catch (Exception ex)
                {
                    app.Settings.TurnOffBacklights = old;
                    updatingBacklights = true; backlights.Checked = old; updatingBacklights = false;
                    status.Text = "Could not save: " + ex.Message;
                }
            };
            Controls.Add(backlights);
            Controls.Add(new Label { Text = "BenQ: signal sleep. Other displays: DDC/CI.\nAll screens: hardware off, with Windows sleep fallback.", Location = new Point(30, 451), Size = new Size(400, 46), ForeColor = Color.Silver });
            hardwareStatus = new Label { Location = new Point(30, 501), Size = new Size(398, 110), ForeColor = Color.Silver, AutoEllipsis = true };
            Controls.Add(hardwareStatus);
            UpdateHardwareStatus();
            var retry = new Button { Text = "Retry restoration", Location = new Point(30, 620), Size = new Size(185, 34) };
            retry.Click += delegate { app.Restore(); };
            Controls.Add(retry);
            var retire = new Button { Text = "Retire saved recovery...", Location = new Point(225, 620), Size = new Size(203, 34) };
            retire.Click += delegate { app.ConfirmRetireRecovery(); };
            Controls.Add(retire);
            status = new Label { Text = "Closing this window keeps Nulluxion in the tray.", Location = new Point(30, 674), Size = new Size(398, 48), ForeColor = Color.FromArgb(170, 180, 195) };
            Controls.Add(status);
        }
        internal void UpdateHardwareStatus()
        {
            hardwareStatus.Text = (app.Settings.TurnOffBacklights ? "" : "Backlight power disabled. Using black overlays only.\n") + app.HardwareStatus;
        }
        private string ShortcutText()
        {
            uint m = app.Settings.Modifiers;
            return ((m & 2) != 0 ? "Ctrl + " : "") + ((m & 1) != 0 ? "Alt + " : "") + ((m & 4) != 0 ? "Shift + " : "") + ((m & 8) != 0 ? "Win + " : "") + ((Keys)app.Settings.Key).ToString();
        }
        private void CaptureShortcut(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;
            uint modifiers = (uint)((e.Control ? 2 : 0) | (e.Alt ? 1 : 0) | (e.Shift ? 4 : 0));
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F12 || (modifiers == 0 && (e.KeyCode < Keys.F1 || e.KeyCode > Keys.F24)))
            { status.Text = "Use Ctrl, Alt or Shift with a key, or a function key.\nEscape and F12 are reserved."; return; }
            try
            {
                if (!app.ChangeShortcut(modifiers, (uint)e.KeyCode)) { status.Text = "That shortcut is in use or reserved for all screens."; return; }
                shortcut.Text = ShortcutText(); status.Text = "Shortcut saved. Ready whenever you are.";
            }
            catch (Exception ex) { status.Text = "Could not save shortcut: " + ex.Message; }
        }
    }

    // Test-only controller selected explicitly by CreateForSmokeTest; no native calls or journals.
    internal sealed class OverlayOnlyPower : IPowerController
    {
        public event Action<string> Status { add { } remove { } }
        public event Action Restored { add { } remove { } }
        public event Action ExternalDisplayChanged { add { } remove { } }
        public event Action AllScreensPrepared { add { } remove { } }
        public bool AllScreensPoweredOff { get { return false; } }
        public bool NeedsDisplaySleepFallback { get { return false; } }
        public bool IsRestored { get { return true; } }
        public bool HasUnresolvedRecovery { get { return false; } }
        public string LastStatus { get { return "Overlay test: hardware operations disabled."; } }
        public void SetTargets(string[] names) { }
        public void SetAllTargets(string[] names) { }
        public void Restore() { }
        public void NotifyTopologyChanged() { }
        public void RetireRecovery() { }
        public void Dispose() { }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            // Share the legacy mutex so old and rebranded builds cannot control monitors together.
            using (var mutex = new Mutex(true, @"Local\Blackout.TrayApp", out created))
            {
                if (!created) { MessageBox.Show("Nulluxion is already running. Open it from the system tray.", "Nulluxion"); return; }
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new TrayApp(Array.IndexOf(args, "--startup") >= 0)) Application.Run(app);
                mutex.ReleaseMutex();
            }
        }
    }
}
