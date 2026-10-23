# Copyright (C) 2026 Nulluxion contributors
# SPDX-License-Identifier: GPL-3.0-only

param(
    [ValidateRange(1, 600)]
    [int]$Seconds = 180,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'dist\diagnostics\display-wake-trace.log'),
    [switch]$ClassifyInput
)

$ErrorActionPreference = 'Stop'
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
if (-not ('NulluxionDiagnostics.DisplayWakeTrace' -as [type])) {
    Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NulluxionDiagnostics {
    public sealed class DisplayWakeTrace : NativeWindow {
        private static readonly Guid DisplayStatus = new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly StreamWriter log;
        private IntPtr notification;
        private uint lastInput;
        private bool hasInput;
        private IntPtr keyboardHook, mouseHook;
        private HookProc keyboardCallback, mouseCallback;
        // Counts only: keyboard plus mouse move/button/wheel/other, each by flag combination.
        private readonly long[,] inputCounts = new long[5, 4];
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);

        private IntPtr KeyboardEvent(int code, IntPtr message, IntPtr data) {
            if (code >= 0 && data != IntPtr.Zero) {
                // Read only flags; never read virtual-key, scan-code or text fields.
                int flags = Marshal.ReadInt32(data, 8);
                int category = ((flags & 0x10) != 0 ? 1 : 0) | ((flags & 0x02) != 0 ? 2 : 0);
                inputCounts[0, category]++;
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        private IntPtr MouseEvent(int code, IntPtr message, IntPtr data) {
            if (code >= 0 && data != IntPtr.Zero) {
                // No coordinates, wheel amounts or button data are read.
                int flags = Marshal.ReadInt32(data, 12);
                int category = ((flags & 1) != 0 ? 1 : 0) | ((flags & 2) != 0 ? 2 : 0);
                long kind = message.ToInt64();
                int group = kind == 0x200 ? 1 : (kind == 0x20A || kind == 0x20E) ? 3 :
                    (kind >= 0x201 && kind <= 0x209) || (kind >= 0x20B && kind <= 0x20D) ? 2 : 4;
                inputCounts[group, category]++;
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        private void StartInputHooks() {
            keyboardCallback = KeyboardEvent;
            mouseCallback = MouseEvent;
            IntPtr module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(13, keyboardCallback, module, 0);
            if (keyboardHook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            mouseHook = SetWindowsHookEx(14, mouseCallback, module, 0);
            if (mouseHook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Write("INPUT_CLASSIFICATION enabled; aggregate counts per timer interval; no key identities/text or pointer positions");
        }
        private void FlushInputCounts() {
            string[] groups = { "keyboard", "mouse_move", "mouse_button", "mouse_wheel", "mouse_other" };
            for (int group = 0; group < 5; group++) {
                for (int flags = 0; flags < 4; flags++) {
                    long count = inputCounts[group, flags];
                    inputCounts[group, flags] = 0;
                    if (count != 0) Write("INPUT_COUNT kind=" + groups[group] + " injected=" + ((flags & 1) != 0) +
                        " lowerIntegrity=" + ((flags & 2) != 0) + " count=" + count);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        private DisplayWakeTrace(StreamWriter writer) { log = writer; }
        private void Write(string value) {
            log.WriteLine(DateTime.UtcNow.ToString("o") + " elapsed_ms=" + elapsed.ElapsedMilliseconds + " " + value);
        }
        private void PollInput() {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref info)) return;
            if (!hasInput || lastInput != info.dwTime) {
                Write("LAST_INPUT " + (hasInput ? "changed" : "initial") + " tick_ms=" + info.dwTime);
                lastInput = info.dwTime;
                hasInput = true;
            }
        }
        protected override void WndProc(ref Message message) {
            if (message.Msg == 0x218 && message.WParam.ToInt64() == 0x8013 && message.LParam != IntPtr.Zero) {
                Guid setting = (Guid)Marshal.PtrToStructure(message.LParam, typeof(Guid));
                int length = Marshal.ReadInt32(message.LParam, 16);
                if (setting == DisplayStatus && length >= 4) {
                    int state = Marshal.ReadInt32(message.LParam, 20);
                    string name = state == 0 ? "off" : state == 1 ? "on" : state == 2 ? "dim" : "unknown";
                    Write("WM_POWERBROADCAST PBT_POWERSETTINGCHANGE session_display=" + state + "(" + name + ")");
                }
            } else if (message.Msg == 0x7E) {
                long dimensions = message.LParam.ToInt64();
                Write("WM_DISPLAYCHANGE bits_per_pixel=" + message.WParam.ToInt64() + " width=" + (dimensions & 0xFFFF) + " height=" + ((dimensions >> 16) & 0xFFFF));
            } else if (message.Msg == 0x219) {
                Write("WM_DEVICECHANGE event=0x" + message.WParam.ToInt64().ToString("X"));
            }
            base.WndProc(ref message);
        }
        public static void Run(int seconds, string path, bool classifyInput) {
            using (StreamWriter writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false))) {
                writer.AutoFlush = true;
                DisplayWakeTrace window = new DisplayWakeTrace(writer);
                Timer timer = new Timer();
                ApplicationContext context = new ApplicationContext();
                try {
                    window.Write("START seconds=" + seconds + " read_only=true; last-input timestamps only; no key identities or text");
                    CreateParams parameters = new CreateParams();
                    parameters.Caption = "Nulluxion display wake diagnostic";
                    // An invisible top-level window receives broadcast messages.
                    // A message-only window would not receive those broadcasts.
                    parameters.Parent = IntPtr.Zero;
                    parameters.Style = 0;
                    window.CreateHandle(parameters);
                    Guid setting = DisplayStatus;
                    window.notification = RegisterPowerSettingNotification(window.Handle, ref setting, 0);
                    if (window.notification == IntPtr.Zero)
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    window.PollInput();
                    if (classifyInput) window.StartInputHooks();
                    timer.Interval = 100;
                    timer.Tick += delegate {
                        window.PollInput();
                        if (classifyInput) window.FlushInputCounts();
                        if (window.elapsed.ElapsedMilliseconds >= seconds * 1000L) context.ExitThread();
                    };
                    timer.Start();
                    Application.Run(context);
                    window.Write("STOP");
                } finally {
                    timer.Stop();
                    timer.Dispose();
                    if (window.keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(window.keyboardHook);
                    if (window.mouseHook != IntPtr.Zero) UnhookWindowsHookEx(window.mouseHook);
                    GC.KeepAlive(window.keyboardCallback);
                    GC.KeepAlive(window.mouseCallback);
                    if (window.notification != IntPtr.Zero) {
                        if (!UnregisterPowerSettingNotification(window.notification))
                            window.Write("UnregisterPowerSettingNotification error=" + Marshal.GetLastWin32Error());
                        window.notification = IntPtr.Zero;
                    }
                    window.DestroyHandle();
                    context.Dispose();
                }
            }
        }
    }
}
'@
}

Write-Host "Observing display and input timestamps for $Seconds seconds. Log: $OutputPath"
[NulluxionDiagnostics.DisplayWakeTrace]::Run($Seconds, $OutputPath, [bool]$ClassifyInput)
Write-Output $OutputPath
