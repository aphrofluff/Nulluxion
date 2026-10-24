// Copyright (C) 2026 Nulluxion contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Nulluxion.Tests
{
    internal static class SettingsFormTests
    {
        static int assertions;
        static void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception("Settings UI: " + message);
        }

        static T Find<T>(Control form, string name) where T : Control
        {
            Control[] matches = form.Controls.Find(name, true);
            if (matches.Length != 1) throw new Exception("Missing unique UI control: " + name);
            return (T)matches[0];
        }

        static void Layout(Control control)
        {
            // Handles permit native layout without showing a window or sending input.
            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls) Layout(child);
            control.PerformLayout();
        }

        internal static void Run()
        {
            using (TrayApp app = TrayApp.CreateForSmokeTest())
            using (var form = new SettingsForm(app, SystemIcons.Application))
            {
                form.ClientSize = new Size(680, 820);
                Layout(form);
                var shortcut = Find<TextBox>(form, "shortcut");
                var allShortcut = Find<TextBox>(form, "allShortcut");
                var feedback = Find<Label>(form, "status");
                var body = Find<Panel>(form, "settingsBody");
                var content = Find<TableLayoutPanel>(form, "settingsContent");
                var hardware = Find<Label>(form, "hardwareStatus");
                var footer = Find<Label>(form, "trayNote");
                MethodInfo capture = typeof(SettingsForm).GetMethod("CaptureShortcut", BindingFlags.Instance | BindingFlags.NonPublic);
                string originalFeedback = feedback.Text;
                foreach (TextBox field in new[] { shortcut, allShortcut })
                foreach (Keys key in new[] { Keys.Tab, Keys.Shift | Keys.Tab })
                {
                    var args = new KeyEventArgs(key);
                    capture.Invoke(form, new object[] { field, args });
                    Check(!args.SuppressKeyPress && !args.Handled, "Tab and Shift+Tab must remain available for navigation");
                }
                Check(feedback.Text == originalFeedback && app.Settings.Key == (uint)Keys.B && app.Settings.Modifiers == 3,
                    "navigation must not change shortcut settings or feedback");
                var reserved = new KeyEventArgs(Keys.F12);
                capture.Invoke(form, new object[] { shortcut, reserved });
                Check(reserved.SuppressKeyPress && app.Settings.Key == (uint)Keys.B, "reserved keys must not register or save a shortcut");
                Check(footer.Text.Contains("keeps Nulluxion in the tray"), "feedback must not replace the closing-to-tray explanation");
                capture.Invoke(form, new object[] { allShortcut, new KeyEventArgs(Keys.Control | Keys.F8) });
                Check(app.Settings.AllModifiers == 2 && app.Settings.AllKey == (uint)Keys.F8 && allShortcut.Text == "Ctrl + F8" && shortcut.Text == "Ctrl + Alt + B",
                    "second field updates only the all-screen shortcut in the isolated fixture");
                capture.Invoke(form, new object[] { shortcut, new KeyEventArgs(Keys.Control | Keys.F8) });
                Check(app.Settings.Key == (uint)Keys.B && feedback.Text.Contains("different shortcut"), "capture rejects duplicate actions with useful feedback");
                capture.Invoke(form, new object[] { shortcut, new KeyEventArgs(Keys.Alt | Keys.F9) });
                Check(app.Settings.Modifiers == 1 && app.Settings.Key == (uint)Keys.F9 && shortcut.Text == "Alt + F9" && allShortcut.Text == "Ctrl + F8",
                    "first field remains independently configurable");
                Check(!form.TroubleshootingExpanded && hardware.Parent.Name == "troubleshootingDetails", "technical status lives in initially collapsed troubleshooting");
                form.SetTroubleshootingExpanded(true);
                Layout(form);
                Check(form.TroubleshootingExpanded && Find<Button>(form, "troubleshootingToggle").Text.Contains("Hide"), "troubleshooting can expose details and recovery actions");

                int shortHeight = hardware.Height;
                string longMessage = "Restoration requires attention. " + new string('W', 120) +
                    " Restore your monitors and arrangement manually, then retry restoration. " +
                    "Saved recovery records remain available. " + new string('M', 120);
                app.HardwareStatus = longMessage;
                form.UpdateHardwareStatus();
                Layout(form);
                Check(hardware.Text.EndsWith(longMessage) && !hardware.AutoEllipsis,
                    "recovery details must remain complete instead of being ellipsized");
                Check(hardware.Height > shortHeight, "long recovery text must grow vertically");

                form.ClientSize = new Size(484, 480);
                Layout(form);
                Check(body.AutoScroll && content.Height > body.ClientSize.Height,
                    "small windows must offer scrollable content");
                Check(content.Width <= body.ClientSize.Width && !body.HorizontalScroll.Visible,
                    "compact layout must fit horizontally");
                Check(body.Bottom <= footer.Top && footer.Bottom <= form.ClientSize.Height,
                    "tray explanation must remain visible outside scrolling content");
                var actions = Find<FlowLayoutPanel>(form, "recoveryActions");
                foreach (Control action in actions.Controls)
                    Check(action.Right <= actions.ClientSize.Width && action.Bottom <= actions.ClientSize.Height,
                        "recovery actions must fit their wrapping container");
                int expandedHeight = content.Height;
                form.SetTroubleshootingExpanded(false);
                Layout(form);
                Check(!form.TroubleshootingExpanded && content.Height < expandedHeight, "collapsing troubleshooting returns space to the everyday settings");

                // Simulate dimensions after initial DPI autoscaling. Invoke only the
                // lifecycle callback: do not show the form or send desktop input.
                Rectangle workArea = Screen.FromControl(form).WorkingArea;
                form.MinimumSize = new Size(workArea.Width + 100, workArea.Height + 100);
                form.Size = new Size(workArea.Width + 300, workArea.Height + 300);
                typeof(SettingsForm).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, new object[] { EventArgs.Empty });
                Check(form.Width <= workArea.Width && form.Height <= workArea.Height,
                    "initial autoscaled window must fit the screen working area");
                Check(workArea.Contains(form.Bounds), "initial window must remain wholly inside the working area");
                Check(form.MinimumSize.Width <= workArea.Width && form.MinimumSize.Height <= workArea.Height,
                    "scaled minimum dimensions must not prevent working-area clamping");
            }
            Console.WriteLine("PASS: " + assertions + " isolated settings UI assertions (no visible windows or settings writes).");
        }
    }
}
