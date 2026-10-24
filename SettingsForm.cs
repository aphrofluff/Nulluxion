// Copyright (C) 2026 Nulluxion contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Nulluxion
{
    // A small monochrome palette shared by the settings window and tray menu.
    internal static class UtilityTheme
    {
        internal static Color Void { get { return SystemInformation.HighContrast ? SystemColors.Window : Color.Black; } }
        internal static Color Surface { get { return SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(23, 23, 23); } }
        internal static Color Raised { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(37, 37, 37); } }
        internal static Color Edge { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(70, 70, 70); } }
        internal static Color Muted { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(184, 184, 184); } }
        internal static Color Ink { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(244, 244, 244); } }

        internal static void StyleMenu(ContextMenuStrip menu)
        {
            var menuFont = new Font("Segoe UI", 10);
            menu.Font = menuFont;
            menu.Disposed += delegate { menuFont.Dispose(); };
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(5);
            menu.BackColor = Surface;
            menu.ForeColor = Ink;
            if (!SystemInformation.HighContrast) menu.Renderer = new UtilityMenuRenderer();
        }
    }

    internal sealed class UtilityMenuRenderer : ToolStripProfessionalRenderer
    {
        internal UtilityMenuRenderer() : base(new UtilityMenuColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? UtilityTheme.Ink : UtilityTheme.Muted;
            base.OnRenderItemText(e);
        }
    }

    internal sealed class UtilityMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return UtilityTheme.Surface; } }
        public override Color MenuBorder { get { return UtilityTheme.Edge; } }
        public override Color MenuItemSelected { get { return UtilityTheme.Raised; } }
        public override Color MenuItemBorder { get { return UtilityTheme.Edge; } }
        public override Color SeparatorDark { get { return UtilityTheme.Edge; } }
        public override Color SeparatorLight { get { return UtilityTheme.Surface; } }
    }

    internal static class SoftEdges
    {
        internal static GraphicsPath Path(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // Paint the surface over its parent background instead of clipping a Region;
    // this keeps the outward corners antialiased and the children fully native.
    internal sealed class RoundedSurface : TableLayoutPanel
    {
        private readonly float radius;
        private readonly Color fill;
        private readonly bool focusOutline;
        internal RoundedSurface(float radius, Color fill, bool focusOutline)
        {
            this.radius = radius;
            this.fill = fill;
            this.focusOutline = focusOutline;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (Width < 2 || Height < 2) return;
            float scale = e.Graphics.DpiX / 96f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = SoftEdges.Path(new RectangleF(scale, scale, Width - 2 * scale, Height - 2 * scale), radius * scale))
            using (var brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, shape);
                if (focusOutline || SystemInformation.HighContrast)
                    using (var pen = new Pen(ContainsFocus && focusOutline ? UtilityTheme.Ink : UtilityTheme.Edge,
                        ContainsFocus && focusOutline ? 2 * scale : scale))
                        e.Graphics.DrawPath(pen, shape);
            }
        }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
    }

    // Keep Button's accessibility, keyboard activation and click semantics while
    // rendering only its outward shape. High contrast uses the system renderer.
    internal sealed class RecoveryButton : Button
    {
        private readonly bool secondary;
        private bool hovered, pressed;
        internal RecoveryButton(bool secondary)
        {
            this.secondary = secondary;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
            if (Width < 2 || Height < 2) return;
            float scale = e.Graphics.DpiX / 96f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = pressed ? UtilityTheme.Surface : hovered ? UtilityTheme.Edge : secondary ? UtilityTheme.Surface : UtilityTheme.Raised;
            using (var shape = SoftEdges.Path(new RectangleF(1, 1, Width - 2, Height - 2), 8 * scale))
            using (var brush = new SolidBrush(fill))
            using (var edge = new Pen(UtilityTheme.Edge, scale))
            {
                e.Graphics.FillPath(brush, shape);
                e.Graphics.DrawPath(edge, shape);
            }
            if (Focused && ShowFocusCues)
                using (var focus = SoftEdges.Path(new RectangleF(4 * scale, 4 * scale,
                    Width - 8 * scale, Height - 8 * scale), 5 * scale))
                using (var pen = new Pen(UtilityTheme.Ink, scale))
                {
                    pen.DashStyle = DashStyle.Dot;
                    e.Graphics.DrawPath(pen, focus);
                }
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                !Enabled || secondary && !hovered ? UtilityTheme.Muted : UtilityTheme.Ink, flags);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) pressed = true; Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); pressed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    // FlowLayoutPanel can report two rows when TableLayoutPanel first measures it
    // at an unconstrained width. Measure the actual button rows explicitly so the
    // recovery surface stays compact and still wraps on narrow windows.
    internal sealed class RecoveryActionsPanel : FlowLayoutPanel
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            int available = proposedSize.Width > 1 ? proposedSize.Width : Width;
            if (available <= 1) available = int.MaxValue;
            int rowWidth = 0, rowHeight = 0, height = 0, width = 0;
            foreach (Control control in Controls)
            {
                Size size = control.GetPreferredSize(Size.Empty);
                int itemWidth = size.Width + control.Margin.Horizontal;
                int itemHeight = size.Height + control.Margin.Vertical;
                if (rowWidth > 0 && rowWidth + itemWidth > available)
                {
                    width = Math.Max(width, rowWidth);
                    height += rowHeight;
                    rowWidth = 0;
                    rowHeight = 0;
                }
                rowWidth += itemWidth;
                rowHeight = Math.Max(rowHeight, itemHeight);
            }
            return new Size(Math.Max(width, rowWidth), height + rowHeight);
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TrayApp app;
        private readonly TextBox shortcut;
        private readonly TextBox allShortcut;
        private readonly Label status;
        private readonly Label hardwareStatus;
        private readonly Label recoveryAttention;
        private readonly TableLayoutPanel troubleshootingDetails;
        private readonly Button troubleshootingToggle;
        internal bool TroubleshootingExpanded { get; private set; }
        private readonly Panel body;
        private readonly TableLayoutPanel content;
        private readonly Font bodyFont;
        private readonly Font shortcutLabelFont;
        private readonly Font sectionFont;
        private readonly Font shortcutFont;
        private bool initialBoundsAdjusted;

        internal SettingsForm(TrayApp app, Icon icon)
        {
            this.app = app;
            Text = "Nulluxion";
            Icon = icon;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            bodyFont = new Font("Segoe UI", 10);
            Font = bodyFont;
            shortcutLabelFont = new Font("Segoe UI", 13, FontStyle.Bold);
            sectionFont = new Font("Segoe UI", 11, FontStyle.Bold);
            shortcutFont = new Font("Segoe UI", 18);
            BackColor = UtilityTheme.Void;
            ForeColor = UtilityTheme.Ink;
            MinimumSize = new Size(500, 360);
            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(680, workArea.Width - 48), Math.Min(720, workArea.Height - 96));
            StartPosition = FormStartPosition.CenterScreen;

            var footer = new Label
            {
                Name = "trayNote", Text = "Closing this window keeps Nulluxion in the tray.",
                Dock = DockStyle.Bottom, AutoSize = false, Height = 48,
                Padding = new Padding(28, 0, 20, 0), TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UtilityTheme.Muted, BackColor = UtilityTheme.Surface
            };
            body = new Panel { Name = "settingsBody", Dock = DockStyle.Fill, AutoScroll = true, TabIndex = 0 };
            content = Stack();
            content.Name = "settingsContent";
            content.Dock = DockStyle.Top;
            content.Padding = new Padding(28, 22, 28, 24);
            body.Controls.Add(content);
            Controls.Add(body);
            Controls.Add(footer);

            shortcut = AddShortcutField("Focus on one screen", "shortcut", false);
            allShortcut = AddShortcutField("Black out all screens", "allShortcut", true);
            Add(content, Label("Click a field, then press your new shortcut. Tab moves to the next setting.\nPress the shortcut again or Escape to restore.", Font, true), 12);
            status = Label("Changes save automatically.", Font, true);
            status.Name = "status";
            status.AccessibleName = "Settings feedback";
            Add(content, status, 24);

            Add(content, Label("Preferences", sectionFont, false), 12);
            var startup = Check("Start Nulluxion when I sign in", TrayApp.StartsOnBoot());
            bool updatingStartup = false;
            startup.CheckedChanged += delegate
            {
                if (updatingStartup) return;
                bool old = !startup.Checked;
                try { TrayApp.SetStartup(startup.Checked); status.Text = "Startup setting saved."; }
                catch (Exception ex) { updatingStartup = true; startup.Checked = old; updatingStartup = false; status.Text = "Could not change startup: " + ex.Message; }
            };
            Add(content, startup, 12);
            var pointer = Check("Keep the monitor with my mouse pointer", app.Settings.UsePointer);
            bool updatingPointer = false;
            pointer.CheckedChanged += delegate
            {
                if (updatingPointer) return;
                bool old = app.Settings.UsePointer;
                try { app.Settings.UsePointer = pointer.Checked; app.Settings.Save(); status.Text = "Monitor preference saved."; }
                catch (Exception ex) { app.Settings.UsePointer = old; updatingPointer = true; pointer.Checked = old; updatingPointer = false; status.Text = "Could not save: " + ex.Message; }
            };
            Add(content, pointer, 5);
            Add(content, Label("By default, keep the active window’s monitor.", Font, true), 24);

            var recovery = Stack(new RoundedSurface(16, UtilityTheme.Surface, false));
            recovery.Name = "recoverySection";
            recovery.Padding = new Padding(20);
            var backlights = Check("Turn off monitor backlights", app.Settings.TurnOffBacklights);
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
            Add(recovery, backlights, 7);
            Add(recovery, Label("Darken supported screens fully. Other screens use a black overlay.", Font, true), 12);
            recoveryAttention = Label("", Font, false);
            recoveryAttention.Name = "recoveryAttention";
            Add(recovery, recoveryAttention, 8);
            troubleshootingToggle = Button("Troubleshooting", true);
            troubleshootingToggle.Name = "troubleshootingToggle";
            troubleshootingToggle.Anchor = AnchorStyles.Left;
            troubleshootingToggle.Click += delegate { SetTroubleshootingExpanded(!TroubleshootingExpanded); };
            Add(recovery, troubleshootingToggle, 0);
            troubleshootingDetails = Stack();
            troubleshootingDetails.Name = "troubleshootingDetails";
            troubleshootingDetails.BackColor = Color.Transparent;
            troubleshootingDetails.Padding = new Padding(0, 18, 0, 0);
            troubleshootingDetails.Visible = false;
            Add(troubleshootingDetails, Label("Hardware status", Font, false), 6);
            hardwareStatus = Label("", Font, true);
            hardwareStatus.Name = "hardwareStatus";
            hardwareStatus.AccessibleName = "Hardware status";
            Add(troubleshootingDetails, hardwareStatus, 16);
            var actions = new RecoveryActionsPanel
            {
                Name = "recoveryActions", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = true, BackColor = Color.Transparent
            };
            var retry = Button("Retry restoration", false);
            retry.Name = "retryRestoration";
            retry.Click += delegate { app.Restore(); };
            var retire = Button("Retire saved recovery...", true);
            retire.Name = "retireRecovery";
            retire.Click += delegate { app.ConfirmRetireRecovery(); };
            actions.Controls.Add(retry);
            actions.Controls.Add(retire);
            Add(troubleshootingDetails, actions, 0);
            Add(recovery, troubleshootingDetails, 0);
            Add(content, recovery, 0);
            SetTroubleshootingExpanded(false);
            UpdateHardwareStatus();
            app.ShortcutCapture = TryCaptureRegisteredShortcut;
        }

        private TextBox AddShortcutField(string label, string name, bool all)
        {
            Add(content, Label(label, shortcutLabelFont, false), 10);
            var surface = Stack(new RoundedSurface(12, UtilityTheme.Raised, true));
            surface.Name = name + "Surface";
            surface.Padding = new Padding(16, 11, 16, 11);
            var field = new TextBox
            {
                Name = name, ReadOnly = true, Dock = DockStyle.Fill,
                BackColor = UtilityTheme.Raised, ForeColor = UtilityTheme.Ink,
                BorderStyle = BorderStyle.None, Font = shortcutFont, AccessibleName = label,
                AccessibleDescription = "Press a new shortcut to save it. Tab moves to the next setting.",
                Text = app.ShortcutText(all)
            };
            field.KeyDown += CaptureShortcut;
            surface.MouseDown += delegate { field.Focus(); };
            Add(surface, field, 0);
            Add(content, surface, 18);
            return field;
        }

        internal void SetTroubleshootingExpanded(bool expanded)
        {
            TroubleshootingExpanded = expanded;
            troubleshootingDetails.Visible = expanded;
            troubleshootingToggle.Text = expanded ? "Hide troubleshooting" : "Troubleshooting";
            troubleshootingToggle.AccessibleName = troubleshootingToggle.Text;
            troubleshootingToggle.AccessibleDescription = expanded ? "Expanded. Hide monitor status and recovery actions." : "Collapsed. Show monitor status and recovery actions.";
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (initialBoundsAdjusted) return;
            initialBoundsAdjusted = true;

            // WinForms applies DPI autoscaling before Load. The constructor's
            // logical dimensions therefore cannot guarantee that the final
            // outer bounds fit the physical working area (including the taskbar).
            Rectangle workArea = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, workArea.Width),
                Math.Min(MinimumSize.Height, workArea.Height));
            Size fitted = new Size(Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
            Bounds = new Rectangle(workArea.Left + (workArea.Width - fitted.Width) / 2,
                workArea.Top + (workArea.Height - fitted.Height) / 2, fitted.Width, fitted.Height);
        }

        private static TableLayoutPanel Stack()
        {
            return Stack(new TableLayoutPanel());
        }

        private static TableLayoutPanel Stack(TableLayoutPanel panel)
        {
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.ColumnCount = 1;
            panel.Dock = DockStyle.Fill;
            panel.Margin = Padding.Empty;
            panel.Padding = Padding.Empty;
            panel.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return panel;
        }

        private static void Add(TableLayoutPanel parent, Control control, int bottomMargin)
        {
            control.Margin = new Padding(0, 0, 0, bottomMargin);
            int row = parent.RowCount++;
            parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            parent.Controls.Add(control, 0, row);
        }

        private static Label Label(string text, Font font, bool muted)
        {
            return new Label
            {
                Text = text, AutoSize = true, Dock = DockStyle.Fill, Font = font,
                ForeColor = muted ? UtilityTheme.Muted : UtilityTheme.Ink,
                BackColor = Color.Transparent, UseMnemonic = false
            };
        }

        private static CheckBox Check(string text, bool value)
        {
            return new CheckBox
            {
                Text = text, Checked = value, AutoSize = true, Dock = DockStyle.Fill,
                ForeColor = UtilityTheme.Ink, BackColor = Color.Transparent,
                UseVisualStyleBackColor = false, AccessibleName = text, Padding = new Padding(0, 2, 0, 2)
            };
        }

        private static Button Button(string text, bool secondary)
        {
            var button = new RecoveryButton(secondary)
            {
                Text = text, AccessibleName = text, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(160, 38),
                Padding = new Padding(12, 5, 12, 5), Margin = new Padding(0, 0, 10, 6),
                ForeColor = UtilityTheme.Ink,
                UseVisualStyleBackColor = false, FlatStyle = SystemInformation.HighContrast ? FlatStyle.Standard : FlatStyle.Flat
            };
            button.FlatAppearance.BorderColor = UtilityTheme.Edge;
            button.FlatAppearance.MouseOverBackColor = UtilityTheme.Edge;
            button.FlatAppearance.MouseDownBackColor = UtilityTheme.Surface;
            return button;
        }

        internal void UpdateHardwareStatus()
        {
            hardwareStatus.Text = (app.Settings.TurnOffBacklights ? "" : "Backlight power disabled. Using black overlays only.\n") + app.HardwareStatus;
            recoveryAttention.Text = app.RecoveryNeedsAttention ? "A screen may still need restoring. Open Troubleshooting to retry restoration." : "";
            recoveryAttention.Visible = app.RecoveryNeedsAttention;
        }

        private void CaptureShortcut(object sender, KeyEventArgs e)
        {
            // Preserve normal keyboard navigation instead of consuming Tab as a shortcut.
            if (e.KeyCode == Keys.Tab && !e.Control && !e.Alt) return;
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;
            uint modifiers = (uint)((e.Control ? 2 : 0) | (e.Alt ? 1 : 0) | (e.Shift ? 4 : 0));
            ApplyShortcut(sender == allShortcut, modifiers, (uint)e.KeyCode);
        }

        internal bool TryCaptureRegisteredShortcut(int id)
        {
            if (id != 1 && id != 3 || !shortcut.Focused && !allShortcut.Focused) return false;
            ApplyShortcut(allShortcut.Focused, id == 3 ? app.Settings.AllModifiers : app.Settings.Modifiers,
                id == 3 ? app.Settings.AllKey : app.Settings.Key);
            return true;
        }

        private void ApplyShortcut(bool all, uint modifiers, uint key)
        {
            if (!Settings.IsValidShortcut(modifiers, key))
            { status.Text = "Use Ctrl, Alt or Shift with a key, or a function key.\nEscape and F12 are reserved."; return; }
            try
            {
                if (!(all ? app.ChangeAllShortcut(modifiers, key) : app.ChangeShortcut(modifiers, key)))
                { status.Text = "That shortcut is already in use. Choose a different shortcut for each action."; return; }
                shortcut.Text = app.ShortcutText(false);
                allShortcut.Text = app.ShortcutText(true);
                status.Text = "Shortcut saved. Ready whenever you are.";
            }
            catch (Exception ex) { status.Text = "Could not save shortcut: " + ex.Message; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && app.ShortcutCapture == TryCaptureRegisteredShortcut) app.ShortcutCapture = null;
            base.Dispose(disposing);
            if (disposing)
            {
                bodyFont.Dispose();
                shortcutLabelFont.Dispose();
                sectionFont.Dispose();
                shortcutFont.Dispose();
            }
        }
    }
}
