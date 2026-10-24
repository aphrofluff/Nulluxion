// Copyright (C) 2026 Nulluxion contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace Nulluxion
{
    public class Settings
    {
        public uint Modifiers = 3;
        public uint Key = (uint)Keys.B;
        public uint AllModifiers = 7;
        public uint AllKey = (uint)Keys.B;
        public bool UsePointer;
        public bool TurnOffBacklights = true;
        // Keep the legacy data location so existing settings and recovery records remain usable.
        public static readonly string FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackout", "settings.xml");
        public static Settings Load()
        { return Load(FileName); }
        internal static Settings Load(string path)
        {
            try
            {
                var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 };
                using (var reader = XmlReader.Create(path, options))
                {
                    Settings settings = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(reader) ?? new Settings();
                    settings.RepairShortcuts();
                    return settings;
                }
            }
            catch { return new Settings(); }
        }
        public void Save()
        { Save(FileName); }
        internal void Save(string path)
        {
            if (!IsValidShortcut(Modifiers, Key) || !IsValidShortcut(AllModifiers, AllKey) ||
                (Modifiers == AllModifiers && Key == AllKey))
                throw new InvalidOperationException("Choose two different, valid shortcuts. Escape and F12 are reserved.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".tmp";
            using (var file = File.Create(temporary)) new XmlSerializer(typeof(Settings)).Serialize(file, this);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        internal static bool IsValidShortcut(uint modifiers, uint key)
        {
            if ((modifiers & ~15u) != 0 || key < 8 || key > 254 || key == (uint)Keys.Escape || key == (uint)Keys.F12) return false;
            if (key == (uint)Keys.ControlKey || key == (uint)Keys.Menu || key == (uint)Keys.ShiftKey ||
                key == (uint)Keys.LWin || key == (uint)Keys.RWin || (key >= (uint)Keys.LShiftKey && key <= (uint)Keys.RMenu)) return false;
            return modifiers != 0 || (key >= (uint)Keys.F1 && key <= (uint)Keys.F24);
        }
        internal void RepairShortcuts()
        {
            bool focusValid = IsValidShortcut(Modifiers, Key), allValid = IsValidShortcut(AllModifiers, AllKey);
            if (!focusValid)
            {
                Modifiers = allValid && AllModifiers == 3 && AllKey == (uint)Keys.B ? 7u : 3u;
                Key = (uint)Keys.B;
            }
            if (!allValid || (Modifiers == AllModifiers && Key == AllKey))
            {
                AllModifiers = Modifiers == 7 && Key == (uint)Keys.B ? 3u : 7u;
                AllKey = (uint)Keys.B;
            }
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
        internal Func<int, bool> ShortcutCapture;
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
        private readonly Func<int, uint, uint, bool> registerShortcut;
        private readonly Action<int> unregisterShortcut;
        private readonly Action<Settings> saveSettings;
        private bool focusShortcutRegistered, allShortcutRegistered;
        private readonly ToolStripMenuItem retireRecovery;
        internal string HardwareStatus = "Hardware power enabled for compatible DDC/CI monitors.";
        private readonly System.Windows.Forms.Timer sleepTimer = new System.Windows.Forms.Timer { Interval = 800 };
        private bool displaySleepRequested;
        private bool windowsSleepFallback;
        internal bool IsActive { get { return overlays.Count > 0; } }
        internal bool RecoveryNeedsAttention { get { return hardware.HasUnresolvedRecovery; } }
        internal static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        internal TrayApp(bool startup) : this(startup, new HardwarePower(), Settings.Load(), true, null) { }
        internal TrayApp(bool startup, IPowerController power, Settings settings, bool interactive, Action<bool> displayPower)
            : this(startup, power, settings, interactive, displayPower, null) { }
        internal TrayApp(bool startup, IPowerController power, Settings settings, bool interactive, Action<bool> displayPower, Func<bool> inputIsQuiet)
            : this(startup, power, settings, interactive, displayPower, inputIsQuiet, null, null, null) { }
        internal TrayApp(bool startup, IPowerController power, Settings settings, bool interactive, Action<bool> displayPower, Func<bool> inputIsQuiet,
            Func<int, uint, uint, bool> registerShortcut, Action<int> unregisterShortcut, Action<Settings> saveSettings)
        {
            if (power == null) throw new ArgumentNullException("power");
            hardware = power;
            Settings = settings ?? new Settings();
            Settings.RepairShortcuts();
            this.interactive = interactive;
            this.registerShortcut = registerShortcut ?? (interactive
                ? new Func<int, uint, uint, bool>(delegate(int id, uint modifiers, uint key) { return Native.RegisterHotKey(hotkeys.Handle, id, modifiers, key); })
                : delegate { return true; });
            this.unregisterShortcut = unregisterShortcut ?? (interactive
                ? new Action<int>(delegate(int id) { Native.UnregisterHotKey(hotkeys.Handle, id); }) : delegate { });
            // Noninteractive fixtures never write user settings, even for valid shortcuts.
            this.saveSettings = saveSettings ?? (interactive ? new Action<Settings>(delegate(Settings value) { value.Save(); }) : delegate { });
            this.inputIsQuiet = inputIsQuiet ?? (interactive ? new Func<bool>(new DisplaySleepInput().IsQuiet) : delegate { return true; });
            this.displayPower = displayPower ?? delegate(bool sleep) { Native.DefWindowProc(hotkeys.Handle, 0x0112, new IntPtr(0xF170), new IntPtr(sleep ? 2 : -1)); };
            icon = Icon.ExtractAssociatedIcon(ExePath) ?? (Icon)SystemIcons.Application.Clone();
            var menu = new ContextMenuStrip();
            UtilityTheme.StyleMenu(menu);
            toggle = new ToolStripMenuItem("Black out other monitors", null, delegate { Toggle(); });
            menu.Items.Add(toggle);
            toggleAll = new ToolStripMenuItem("Black out all monitors", null, delegate { ToggleAll(); });
            menu.Items.Add(toggleAll);
            UpdateTrayActions();
            menu.Items.Add("Settings", null, delegate { OpenSettings(); });
            menu.Items.Add("About / license", null, delegate
            {
                Restore();
                MessageBox.Show("Nulluxion\nCopyright (C) 2026 Nulluxion contributors\n\nLicensed under the GNU General Public License, version 3 only. You may redistribute and modify this program under that license. This program comes with absolutely no warranty.\n\nSee the included LICENSE file or https://www.gnu.org/licenses/gpl-3.0.html.\nSource: https://github.com/aphrofluff/Nulluxion", "About Nulluxion", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
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
            menu.Items.Insert(4, new ToolStripSeparator());
            HardwareStatus = hardware.LastStatus;
            tray.DoubleClick += delegate { OpenSettings(); };
            hotkeys.Pressed = DispatchHotkey;
            allShortcutRegistered = this.registerShortcut(3, Settings.AllModifiers | 0x4000, Settings.AllKey);
            focusShortcutRegistered = this.registerShortcut(1, Settings.Modifiers | 0x4000, Settings.Key);
            if (interactive)
            {
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionEnding += SessionEnding;
            if (!startup || !focusShortcutRegistered || !allShortcutRegistered) OpenSettings();
            if (!focusShortcutRegistered || !allShortcutRegistered)
                MessageBox.Show((!focusShortcutRegistered ? "Focus on one screen: " + ShortcutText(false) + " is already in use.\n" : "") +
                    (!allShortcutRegistered ? "Black out all screens: " + ShortcutText(true) + " is already in use.\n" : "") +
                    "Choose another shortcut in settings. Both actions remain available from the tray menu.", "Nulluxion", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        // Used by the isolated smoke test; never creates a real recovery worker.
        internal static TrayApp CreateForSmokeTest()
        {
            return new TrayApp(true, new OverlayOnlyPower(), new Settings { TurnOffBacklights = false }, false, delegate { });
        }
        internal void DispatchHotkey(int id)
        {
            if (id == 2) { Restore(); return; }
            if (id != 1 && id != 3) return;
            // A registered combination may arrive as WM_HOTKEY instead of a
            // textbox key event. Editing either field must never black out screens.
            if (ShortcutCapture != null && ShortcutCapture(id)) return;
            if (id == 3) ToggleAll(); else Toggle();
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
                UpdateTrayActions();
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
            try
            {
                if (displaySleepRequested)
                {
                    displaySleepRequested = false;
                    displayPower(false);
                }
            }
            finally
            {
                try { hardware.Restore(); }
                finally
                {
                    foreach (var overlay in overlays) overlay.Dispose();
                    overlays.Clear();
                    allScreens = false;
                    Native.UnregisterHotKey(hotkeys.Handle, 2);
                    UpdateTrayActions();
                    tray.Text = "Nulluxion";
                }
            }
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
        { return ChangeShortcutCore(false, modifiers, key); }
        internal bool ChangeAllShortcut(uint modifiers, uint key)
        { return ChangeShortcutCore(true, modifiers, key); }
        private bool ChangeShortcutCore(bool all, uint modifiers, uint key)
        {
            if (!Nulluxion.Settings.IsValidShortcut(modifiers, key)) return false;
            uint otherModifiers = all ? Settings.Modifiers : Settings.AllModifiers;
            uint otherKey = all ? Settings.Key : Settings.AllKey;
            if (modifiers == otherModifiers && key == otherKey) return false;
            uint oldModifiers = all ? Settings.AllModifiers : Settings.Modifiers;
            uint oldKey = all ? Settings.AllKey : Settings.Key;
            bool wasRegistered = all ? allShortcutRegistered : focusShortcutRegistered;
            if (wasRegistered && modifiers == oldModifiers && key == oldKey) return true;
            int id = all ? 3 : 1;
            unregisterShortcut(id);
            SetShortcutRegistered(all, false);
            if (!registerShortcut(id, modifiers | 0x4000, key))
            {
                RestoreShortcutRegistration(all, id, oldModifiers, oldKey, wasRegistered);
                return false;
            }
            SetShortcutRegistered(all, true);
            SetShortcutSettings(all, modifiers, key);
            try { saveSettings(Settings); }
            catch
            {
                SetShortcutSettings(all, oldModifiers, oldKey);
                unregisterShortcut(id);
                SetShortcutRegistered(all, false);
                RestoreShortcutRegistration(all, id, oldModifiers, oldKey, wasRegistered);
                throw;
            }
            UpdateTrayActions();
            return true;
        }
        private void SetShortcutSettings(bool all, uint modifiers, uint key)
        {
            if (all) { Settings.AllModifiers = modifiers; Settings.AllKey = key; }
            else { Settings.Modifiers = modifiers; Settings.Key = key; }
        }
        private void SetShortcutRegistered(bool all, bool registered)
        { if (all) allShortcutRegistered = registered; else focusShortcutRegistered = registered; }
        private void RestoreShortcutRegistration(bool all, int id, uint modifiers, uint key, bool wasRegistered)
        {
            if (!wasRegistered) return;
            bool restored = registerShortcut(id, modifiers | 0x4000, key);
            SetShortcutRegistered(all, restored);
            if (!restored) throw new InvalidOperationException("The previous shortcut could not be restored. Choose another shortcut; the tray menu is still available.");
        }
        internal string ShortcutText(bool all)
        {
            uint m = all ? Settings.AllModifiers : Settings.Modifiers;
            uint key = all ? Settings.AllKey : Settings.Key;
            return ((m & 2) != 0 ? "Ctrl + " : "") + ((m & 1) != 0 ? "Alt + " : "") +
                ((m & 4) != 0 ? "Shift + " : "") + ((m & 8) != 0 ? "Win + " : "") + ((Keys)key).ToString();
        }
        private void UpdateTrayActions()
        {
            toggle.Text = (IsActive ? "Restore all monitors" : "Black out other monitors") + " (" + ShortcutText(false) + ")";
            toggleAll.Text = (IsActive && allScreens ? "Restore all monitors" : "Black out all monitors") + " (" + ShortcutText(true) + ")";
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
            try { Cleanup(); }
            finally { base.ExitThreadCore(); }
        }
        protected override void Dispose(bool disposing)
        {
            try { if (disposing) Cleanup(); }
            finally { base.Dispose(disposing); }
        }
        private void Cleanup()
        {
            if (disposed) return;
            disposed = true;
            hardware.ExternalDisplayChanged -= DispatchRestore;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.SessionSwitch -= SessionChanged;
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.SessionEnding -= SessionEnding;
            try { Restore(); }
            finally
            {
                try { hardware.Dispose(); }
                finally
                {
                    sleepTimer.Dispose(); unregisterShortcut(1); unregisterShortcut(3); hotkeys.Dispose(); tray.Visible = false;
                    tray.ContextMenuStrip.Dispose(); tray.Dispose();
                    if (window != null) window.Dispose();
                    icon.Dispose();
                }
            }
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
                try
                {
                    try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                    catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    // Own power outside tray construction as well: initialization
                    // failures must still stop the worker and attempt restoration.
                    using (var power = new HardwarePower())
                    using (var app = new TrayApp(Array.IndexOf(args, "--startup") >= 0, power, Settings.Load(), true, null))
                    {
                        ThreadExceptionEventHandler failure = delegate(object sender, ThreadExceptionEventArgs error)
                        {
                            app.ExitThread();
                            MessageBox.Show("Nulluxion closed after an unexpected error and attempted to restore your monitors.\n\n" + error.Exception.Message,
                                "Nulluxion", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        };
                        Application.ThreadException += failure;
                        try { Application.Run(app); }
                        finally { Application.ThreadException -= failure; }
                    }
                }
                catch (Exception error)
                {
                    MessageBox.Show("Nulluxion could not continue.\n\n" + error.Message, "Nulluxion", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
