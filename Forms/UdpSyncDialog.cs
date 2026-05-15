#nullable enable
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Modal popup shown when admin's UDP definitions differ from the active
    /// model's state (Phase 2 of the Admin -> Model UDP sync feature).
    /// Renders the <see cref="UdpDiff"/> as two summary counters plus a
    /// ListView of Create/Update rows, with an Apply/Cancel footer. Delete
    /// is never emitted by the sync engine - users remove unwanted UDPs
    /// themselves through erwin's UDP editor.
    ///
    /// Apply (<see cref="DialogResult.OK"/>) hands control back to the
    /// caller which invokes <c>UdpSyncEngine.Apply(diff)</c> (Phase 3 - not
    /// wired in Phase 2). Cancel (<see cref="DialogResult.Cancel"/>) skips
    /// the apply path; the next model open recomputes the same diff and
    /// shows the popup again (spec: stateless, no last-seen).
    ///
    /// Visual design follows <see cref="AddinMessageDialog"/> (borderless
    /// chrome, accent strip, sticky TopMost, multi-monitor positioning
    /// against <see cref="ErwinAddIn.ActiveForm"/>'s screen, drag-by-header)
    /// so the user sees one consistent add-in dialog language. The dialog
    /// is wider (720px) than <see cref="AddinMessageDialog"/> because the
    /// ListView needs room for four columns; height auto-grows with the
    /// row count but is clamped to 75% of the working area.
    ///
    /// Per-row checkboxes are out of scope (spec: per-item selection
    /// deferred). The dialog is all-or-nothing: Apply commits every diff
    /// entry, Cancel commits nothing.
    /// </summary>
    public sealed class UdpSyncDialog : Form
    {
        // Design tokens (shared with the other add-in dialogs).
        private static readonly Color ClrPrimary = Color.FromArgb(0, 102, 204);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);
        private static readonly Color ClrCloseHover = Color.FromArgb(232, 17, 35);
        private static readonly Color ClrCreate = Color.FromArgb(34, 139, 34);   // green
        private static readonly Color ClrUpdate = Color.FromArgb(204, 102, 0);   // amber

        private const int DialogWidth = 720;
        private const int MinDialogHeight = 360;
        private const int AccentStripHeight = 4;
        private const int HeaderHeight = 46;
        private const int CloseButtonSize = 32;
        private const int SubtitleHeight = 32;
        private const int SummaryHeight = 40;
        private const int FooterHeight = 56;
        private const int SidePadding = 20;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private readonly UdpDiff _diff;

        public UdpSyncDialog(UdpDiff diff)
        {
            _diff = diff ?? throw new ArgumentNullException(nameof(diff));
            if (_diff.IsEmpty)
                throw new ArgumentException("UdpSyncDialog should not be opened with an empty diff", nameof(diff));

            Text = "Sync UDP definitions from config?";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5F);
            Padding = new Padding(1);
            Paint += (_, e) =>
            {
                using var pen = new Pen(ClrBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };
            TopMost = true;

            // === Top to bottom layout ===

            var accentStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = AccentStripHeight,
                BackColor = ClrPrimary
            };

            // Header: title + close X, drag-enabled
            var header = BuildHeader();
            var headerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = ClrBorder };

            // Subtitle / explanation
            var subtitle = new Label
            {
                Text = "Config definitions differ from this model. Review the changes below, then Apply or Cancel.",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextSecondary,
                Dock = DockStyle.Top,
                Height = SubtitleHeight,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(SidePadding, 0, SidePadding, 0),
                UseMnemonic = false
            };

            // Summary counters strip
            var summary = BuildSummary();

            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };
            var footer = BuildFooter(out Button apply, out Button cancel);

            // ListView fills the centre. Constructed AFTER header / subtitle /
            // summary are docked so it claims the remaining vertical space.
            var listView = BuildListView();
            PopulateListView(listView);

            // Docking order: Fill child first (claims the middle), then
            // bottom-docked footer + separator, then top-docked items in
            // reverse z-order.
            Controls.Add(listView);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(summary);
            Controls.Add(subtitle);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            AcceptButton = apply;
            CancelButton = cancel;
            ActiveControl = apply;

            Shown += (_, _) =>
            {
                if (IsDisposed) return;
                try { SetForegroundWindow(Handle); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UdpSyncDialog SetForegroundWindow failed: {ex.Message}");
                }
            };

            // Size the form to fit the row count, then place it on the
            // active screen. ResizeToFitContent must run before
            // PositionOnActiveScreen so the centre maths uses the final
            // height.
            ResizeToFitContent(listView);
        }

        #region Header

        private Panel BuildHeader()
        {
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = HeaderHeight,
                BackColor = Color.White,
                Cursor = Cursors.SizeAll
            };

            var lblTitle = new Label
            {
                Text = "Sync UDP definitions from config?",
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(SidePadding, 0, CloseButtonSize + 8, 0),
                UseMnemonic = false,
                Cursor = Cursors.SizeAll
            };

            var btnClose = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 10F, FontStyle.Regular),
                ForeColor = ClrTextSecondary,
                BackColor = Color.White,
                AutoSize = false,
                Size = new Size(CloseButtonSize, CloseButtonSize),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(DialogWidth - CloseButtonSize - 2, (HeaderHeight - CloseButtonSize) / 2)
            };
            btnClose.MouseEnter += (_, _) =>
            {
                btnClose.BackColor = ClrCloseHover;
                btnClose.ForeColor = Color.White;
            };
            btnClose.MouseLeave += (_, _) =>
            {
                btnClose.BackColor = Color.White;
                btnClose.ForeColor = ClrTextSecondary;
            };
            btnClose.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            // Drag-by-header via WM_NCLBUTTONDOWN HTCAPTION (same trick the
            // other add-in dialogs use; required because FormBorderStyle.None
            // removes the system drag handle).
            void StartDrag(object? s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            header.MouseDown += StartDrag;
            lblTitle.MouseDown += StartDrag;

            header.Controls.Add(btnClose);
            header.Controls.Add(lblTitle);
            return header;
        }

        #endregion

        #region Summary

        private Panel BuildSummary()
        {
            // Three coloured counters laid out horizontally. Each counter is
            // hidden when its count is zero, so the strip self-trims to only
            // the relevant categories.
            var panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = SummaryHeight,
                BackColor = Color.White,
                Padding = new Padding(SidePadding, 4, SidePadding, 8)
            };

            int x = SidePadding;
            x = AddCounter(panel, x, _diff.Creates.Count, "Create", ClrCreate);
            x = AddCounter(panel, x, _diff.Updates.Count, "Update", ClrUpdate);
            // x consumed but not used further; kept assigned so future
            // additions can chain off it without re-reading the layout.
            _ = x;

            return panel;
        }

        private static int AddCounter(Panel panel, int x, int count, string label, Color color)
        {
            if (count == 0) return x;

            // Coloured pill: count number + label after it.
            var pill = new Label
            {
                Text = $"{count}",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = color,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(x, 4),
                Size = new Size(34, 24),
                Padding = new Padding(2)
            };
            panel.Controls.Add(pill);

            var lbl = new Label
            {
                Text = count == 1 ? $"{label}" : $"{label}s",
                Font = new Font("Segoe UI", 10F),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(x + 38, 4),
                Size = new Size(80, 24),
                UseMnemonic = false
            };
            panel.Controls.Add(lbl);

            return x + 124; // pill + label + gap
        }

        #endregion

        #region ListView

        private ListView BuildListView()
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5F),
                BackColor = Color.White,
                Margin = new Padding(SidePadding, 0, SidePadding, 0),
                Padding = new Padding(SidePadding, 0, SidePadding, 0),
                OwnerDraw = true
            };

            lv.Columns.Add("Action", 90);
            lv.Columns.Add("UDP Name", 180);
            lv.Columns.Add("Object Type", 120);
            // Details column auto-sizes to remaining width via DialogWidth
            // - other columns - vertical scrollbar reserve - paddings.
            int detailsWidth = DialogWidth - 90 - 180 - 120 - SidePadding * 2 - 24;
            lv.Columns.Add("Details", Math.Max(120, detailsWidth));

            // OwnerDraw lets us paint the Action column with the same colour
            // palette as the summary counters. Without it the action label
            // becomes a black-on-white block of text and the row's intent
            // disappears at a glance on a dense diff. Other columns fall
            // through to default rendering.
            lv.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            lv.DrawSubItem += (s, e) =>
            {
                if (e.ColumnIndex != 0)
                {
                    e.DrawDefault = true;
                    return;
                }
                // Column 0 = Action. Paint with the action colour as a small
                // chip background for instant recognition.
                var item = e.Item;
                if (item == null)
                {
                    e.DrawDefault = true;
                    return;
                }
                var entry = item.Tag as UdpDiffEntry;
                var chipColor = entry?.Action switch
                {
                    UdpDiffAction.Create => ClrCreate,
                    UdpDiffAction.Update => ClrUpdate,
                    _ => ClrTextSecondary
                };

                // Row background (selected vs. normal).
                Color rowBg = item.Selected ? SystemColors.Highlight : Color.White;

                using (var bg = new SolidBrush(rowBg))
                    e.Graphics.FillRectangle(bg, e.Bounds);

                // Coloured chip on the left, label text in white.
                var chipRect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 3,
                                             e.Bounds.Width - 8, e.Bounds.Height - 6);
                using (var chip = new SolidBrush(chipColor))
                    e.Graphics.FillRectangle(chip, chipRect);

                TextRenderer.DrawText(
                    e.Graphics,
                    e.SubItem?.Text ?? "",
                    new Font("Segoe UI", 9F, FontStyle.Bold),
                    chipRect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };

            return lv;
        }

        private void PopulateListView(ListView lv)
        {
            void Add(UdpDiffEntry entry)
            {
                var li = new ListViewItem(entry.Action.ToString())
                {
                    Tag = entry,
                    UseItemStyleForSubItems = false
                };
                li.SubItems.Add(entry.UdpName);
                li.SubItems.Add(entry.ObjectType);
                li.SubItems.Add(entry.Details);

                lv.Items.Add(li);
            }

            // Same order as the summary counters: Creates, Updates.
            foreach (var e in _diff.Creates) Add(e);
            foreach (var e in _diff.Updates) Add(e);
        }

        #endregion

        #region Footer

        private Panel BuildFooter(out Button apply, out Button cancel)
        {
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = ClrSurface,
                Padding = new Padding(SidePadding, 12, SidePadding, 12)
            };

            var btnApply = new Button
            {
                Text = "Apply",
                Size = new Size(104, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = ClrPrimary,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = DialogResult.OK,
                UseMnemonic = false,
                TabStop = true
            };
            btnApply.FlatAppearance.BorderSize = 0;

            var btnCancel = new Button
            {
                Text = "Cancel",
                Size = new Size(104, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = DialogResult.Cancel,
                UseMnemonic = false,
                TabStop = true
            };
            btnCancel.FlatAppearance.BorderColor = ClrBorder;

            void Place()
            {
                btnApply.Location = new Point(footer.ClientSize.Width - 16 - btnApply.Width, 12);
                btnCancel.Location = new Point(btnApply.Left - 8 - btnCancel.Width, 12);
            }
            Place();
            footer.Resize += (_, _) => Place();
            footer.Controls.Add(btnApply);
            footer.Controls.Add(btnCancel);

            apply = btnApply;
            cancel = btnCancel;
            return footer;
        }

        #endregion

        #region Sizing + positioning

        /// <summary>
        /// Auto-size the form so the ListView shows up to 12 rows without
        /// scrolling; rows beyond that scroll inside the ListView while the
        /// dialog itself caps at ~75% of the working area. The math runs
        /// AFTER PopulateListView so item count is known.
        /// </summary>
        private void ResizeToFitContent(ListView listView)
        {
            const int defaultRowHeight = 22;       // empirical default for ListView Details mode
            const int listChromeHeight = 28;       // column header + paddings + bottom margin
            const int desiredRowsVisible = 12;     // soft target; scrolling kicks in beyond

            int rowCount = _diff.TotalCount;
            int visibleRows = Math.Min(rowCount, desiredRowsVisible);
            int listHeight = Math.Max(visibleRows, 3) * defaultRowHeight + listChromeHeight;

            int chromeHeight = AccentStripHeight
                               + HeaderHeight + 1   // header + separator
                               + SubtitleHeight
                               + SummaryHeight
                               + 1 + FooterHeight;  // separator + footer
            int totalHeight = Math.Max(MinDialogHeight, chromeHeight + listHeight);

            int maxScreen = (int)((Screen.PrimaryScreen?.WorkingArea.Height ?? 800) * 0.75);
            if (totalHeight > maxScreen) totalHeight = maxScreen;

            ClientSize = new Size(DialogWidth, totalHeight);
        }

        /// <summary>
        /// Place the dialog on the same screen as the add-in's active form.
        /// Identical strategy to <see cref="AddinMessageDialog.PositionOnActiveScreen"/>:
        /// <see cref="ErwinAddIn.ActiveForm"/> first, owner second,
        /// foreground window third, primary screen last.
        /// </summary>
        public void PositionOnActiveScreen(IWin32Window? owner)
        {
            Screen target;
            string source;

            var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
            if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
            {
                target = Screen.FromControl(addinForm);
                source = "addinForm";
            }
            else if (owner is Control { IsDisposed: false } ownerCtrl && ownerCtrl.IsHandleCreated)
            {
                target = Screen.FromControl(ownerCtrl);
                source = "owner";
            }
            else
            {
                IntPtr fg = IntPtr.Zero;
                try { fg = GetForegroundWindow(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"UdpSyncDialog GetForegroundWindow failed: {ex.Message}"); }
                if (fg != IntPtr.Zero)
                {
                    target = Screen.FromHandle(fg);
                    source = "foreground";
                }
                else
                {
                    target = Screen.PrimaryScreen ?? Screen.AllScreens[0];
                    source = "primary";
                }
            }

            var area = target.WorkingArea;
            int x = area.Left + Math.Max(0, (area.Width - Width) / 2);
            int y = area.Top + Math.Max(0, (area.Height - Height) / 2);
            Location = new Point(x, y);
            System.Diagnostics.Debug.WriteLine(
                $"UdpSyncDialog positioned via {source} on screen '{target.DeviceName}' ({area}) -> ({x},{y}) size {Width}x{Height}");
        }

        #endregion

        /// <summary>
        /// Convenience static API: positions the dialog on the active screen,
        /// modal-attaches to the add-in form (or the supplied owner), and
        /// returns true when the user pressed Apply, false otherwise.
        /// Mirrors <see cref="AddinMessageDialog.Show"/>'s ergonomics so
        /// call sites have a single-line invocation.
        /// </summary>
        public static bool ShowFor(UdpDiff diff, IWin32Window? owner = null)
        {
            if (diff == null) throw new ArgumentNullException(nameof(diff));
            if (diff.IsEmpty) return false;

            using var dlg = new UdpSyncDialog(diff);
            dlg.PositionOnActiveScreen(owner);

            IWin32Window? effectiveOwner = owner;
            if (effectiveOwner == null)
            {
                var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
                if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
                    effectiveOwner = addinForm;
            }

            var result = effectiveOwner != null
                ? dlg.ShowDialog(effectiveOwner)
                : dlg.ShowDialog();
            return result == DialogResult.OK;
        }
    }
}
