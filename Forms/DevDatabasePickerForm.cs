#if DEV
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// DEV-ONLY (compiled only when the DEV symbol is defined - non-packaged builds).
    /// Modal picker shown at add-in startup, BEFORE any DB connection, so a developer
    /// can choose which local <c>MetaRepo*</c> database to run against instead of the
    /// registry-configured one. Cancel / [X] / Esc returns null (the caller aborts the
    /// load). Never present in a packaged / production build.
    /// <para>
    /// Visual contract matches <see cref="AllowedDatatypePickerForm"/> /
    /// <see cref="AddinMessageDialog"/>: borderless chrome, primary-blue accent strip,
    /// drag-by-header, active-screen positioning, sticky TopMost - not the classic
    /// gray Windows modal.
    /// </para>
    /// </summary>
    internal sealed class DevDatabasePickerForm : Form
    {
        // Design tokens shared with AddinMessageDialog / AllowedDatatypePickerForm.
        private static readonly Color ClrPrimary = Color.FromArgb(0, 102, 204);
        private static readonly Color ClrPrimaryHover = Color.FromArgb(0, 90, 180);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);
        private static readonly Color ClrCloseHover = Color.FromArgb(232, 17, 35);
        private static readonly Color ClrFieldBorder = Color.FromArgb(180, 180, 180);

        private const int DialogWidth = 460;
        private const int AccentStripHeight = 4;
        private const int HeaderHeight = 46;
        private const int FooterHeight = 58;
        private const int CloseButtonSize = 32;
        private const int BodyPadding = 22;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private readonly ListBox _list;

        public string SelectedDatabase { get; private set; }

        private DevDatabasePickerForm(string server, IReadOnlyList<string> databases)
        {
            Text = "Select MetaRepo Database";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5F);
            Padding = new Padding(1);
            TopMost = true;
            ClientSize = new Size(DialogWidth, 372);

            Paint += (_, e) =>
            {
                using var pen = new Pen(ClrBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // --- Accent strip + header (drag + close) ---
            var accentStrip = new Panel { Dock = DockStyle.Top, Height = AccentStripHeight, BackColor = ClrPrimary };

            var header = new Panel { Dock = DockStyle.Top, Height = HeaderHeight, BackColor = Color.White, Cursor = Cursors.SizeAll };
            var lblHeader = new Label
            {
                Text = "DEV: Select Database",
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(BodyPadding, 0, CloseButtonSize + 8, 0),
                UseMnemonic = false,
                Cursor = Cursors.SizeAll,
            };
            var btnClose = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 10F),
                ForeColor = ClrTextSecondary,
                BackColor = Color.White,
                AutoSize = false,
                Size = new Size(CloseButtonSize, CloseButtonSize),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(DialogWidth - CloseButtonSize - 2, (HeaderHeight - CloseButtonSize) / 2),
            };
            btnClose.MouseEnter += (_, _) => { btnClose.BackColor = ClrCloseHover; btnClose.ForeColor = Color.White; };
            btnClose.MouseLeave += (_, _) => { btnClose.BackColor = Color.White; btnClose.ForeColor = ClrTextSecondary; };
            btnClose.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

            void StartDrag(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            header.MouseDown += StartDrag;
            lblHeader.MouseDown += StartDrag;
            header.Controls.Add(btnClose);
            header.Controls.Add(lblHeader);

            var headerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = ClrBorder };

            // --- Body: subtitle + framed list ---
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(BodyPadding, 16, BodyPadding, 14) };

            var lblSub = new Label
            {
                Text = $"Local MSSQL server:  {server}\r\nPick the MetaRepo* database to run against for this session.",
                Font = new Font("Segoe UI", 9.75F),
                ForeColor = ClrTextSecondary,
                Dock = DockStyle.Top,
                Height = 42,
                UseMnemonic = false,
            };

            var listFrame = new Panel { Dock = DockStyle.Fill, BackColor = ClrFieldBorder, Padding = new Padding(1), Margin = new Padding(0, 8, 0, 0) };
            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                Font = new Font("Consolas", 10.5F),
                IntegralHeight = false,
                ItemHeight = 22,
            };
            foreach (var d in databases)
                _list.Items.Add(d);
            if (_list.Items.Count > 0)
                _list.SelectedIndex = 0;
            _list.DoubleClick += (_, _) => Accept();
            listFrame.Controls.Add(_list);

            // Fill (listFrame) added before Top (lblSub) so the list fills the remainder.
            body.Controls.Add(listFrame);
            body.Controls.Add(lblSub);

            // --- Footer: primary + cancel ---
            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = FooterHeight, BackColor = ClrSurface };

            var btnCancel = new Button
            {
                Text = "Cancel (abort load)",
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextPrimary,
                BackColor = Color.White,
                Size = new Size(160, 32),
                Margin = new Padding(8, 13, 0, 13),
                DialogResult = DialogResult.Cancel,
            };
            btnCancel.FlatAppearance.BorderColor = ClrBorder;
            btnCancel.FlatAppearance.BorderSize = 1;

            var btnOk = new Button
            {
                Text = "Use This DB",
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = ClrPrimary,
                Size = new Size(120, 32),
                Margin = new Padding(0, 13, 0, 13),
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.FlatAppearance.MouseOverBackColor = ClrPrimaryHover;
            btnOk.Click += (_, _) => Accept();

            var footerFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 0, 16, 0),
                BackColor = ClrSurface,
                WrapContents = false,
            };
            footerFlow.Controls.Add(btnCancel);
            footerFlow.Controls.Add(btnOk);
            footer.Controls.Add(footerFlow);

            // Z-order: Fill body first, then edges (bottom then top), accent last (outermost top).
            Controls.Add(body);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); }
            };

            PositionOnActiveScreen();

            Shown += (_, _) =>
            {
                if (IsDisposed) return;
                try { SetForegroundWindow(Handle); } catch { /* best effort */ }
                _list.Focus();
            };
        }

        private void Accept()
        {
            if (_list.SelectedItem == null)
                return;
            SelectedDatabase = _list.SelectedItem.ToString();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void PositionOnActiveScreen()
        {
            Screen target;
            try
            {
                IntPtr fg = GetForegroundWindow();
                target = fg != IntPtr.Zero ? Screen.FromHandle(fg) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]);
            }
            catch { target = Screen.PrimaryScreen ?? Screen.AllScreens[0]; }

            var area = target.WorkingArea;
            int x = area.Left + Math.Max(0, (area.Width - Width) / 2);
            int y = area.Top + Math.Max(0, (area.Height - Height) / 2);
            Location = new Point(x, y);
        }

        /// <summary>Shows the picker modally; returns the chosen DB name, or null on cancel.</summary>
        public static string Show(string server, IReadOnlyList<string> databases)
        {
            using (var dlg = new DevDatabasePickerForm(server, databases))
                return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedDatabase : null;
        }
    }
}
#endif
