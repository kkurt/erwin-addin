#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Outcome contract for the Required popups (this dialog and
    /// <see cref="RequiredUdpForm"/>). The Cancel branch is destructive
    /// (delete the new object) or reversive (revert the changed
    /// property), so the caller must tell the dialog which one applies
    /// up front - the button label changes accordingly so the user
    /// sees what will happen before they click.
    /// </summary>
    public enum RequiredOperationMode
    {
        /// <summary>Object was just created. Cancel deletes it.</summary>
        Create,
        /// <summary>Existing object was edited. Cancel reverts the edit.</summary>
        Update,
    }

    /// <summary>
    /// Modal input dialog for naming-standard Required violations
    /// (<c>IS_REQUIRED=true</c>). The user is forced to type a value
    /// for the property before continuing.
    /// <para>
    /// Cancel / [X] / Esc returns <see cref="DialogResult.Cancel"/>.
    /// The caller (per the 2026-05-20 contract) interprets that as
    /// "discard new object" or "revert the edit" depending on the
    /// <see cref="RequiredOperationMode"/> it passed in. The button
    /// label is rewritten to surface that destination so the user is
    /// not surprised by the action.
    /// </para>
    /// <para>
    /// Visual contract matches <see cref="AddinMessageDialog"/>:
    /// borderless chrome, primary-blue accent strip, drag-by-header,
    /// multi-monitor positioning on the addin's active screen, sticky
    /// TopMost. Layout reads top-to-bottom: header -> message -> field
    /// label -> textbox -> Apply/Cancel.
    /// </para>
    /// </summary>
    public sealed class RequiredFieldDialog : Form
    {
        // Design tokens shared with AddinMessageDialog.
        private static readonly Color ClrPrimary = Color.FromArgb(0, 102, 204);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);
        private static readonly Color ClrCloseHover = Color.FromArgb(232, 17, 35);
        private static readonly Color ClrFieldBorder = Color.FromArgb(180, 180, 180);

        private const int DialogWidth = 480;
        private const int AccentStripHeight = 4;
        private const int HeaderHeight = 46;
        private const int BodyHorizontalPadding = 22;
        private const int BodyTopPadding = 18;
        private const int FooterHeight = 56;
        private const int CloseButtonSize = 32;
        private const int FieldHeight = 28;
        private const int LabelToFieldGap = 6;
        private const int MessageToLabelGap = 12;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        // Either _txtValue OR _cmbValue is non-null - never both. When a
        // <c>choices</c> list was passed we render a ComboBox locked to
        // those entries (DropDownList style, no free typing) so the user
        // cannot enter a value the model doesn't accept (2026-05-25 user
        // request, primarily for Owner / Schema_Ref which erwin rejects
        // unless the name matches an existing Schema object).
        private readonly TextBox? _txtValue;
        private readonly ComboBox? _cmbValue;

        /// <summary>The value the user typed when they clicked Apply. Empty when
        /// they cancelled or the dialog was closed without confirming.</summary>
        public string EnteredValue { get; private set; } = "";

        private RequiredFieldDialog(string title, string message, string fieldLabel, string initialValue,
            RequiredOperationMode mode, string objectKind, IReadOnlyList<string>? choices)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5F);
            Padding = new Padding(1);
            TopMost = true;

            Paint += (_, e) =>
            {
                using var pen = new Pen(ClrBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // Accent strip
            var accentStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = AccentStripHeight,
                BackColor = ClrPrimary,
            };

            // Header (drag handle + close X)
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = HeaderHeight,
                BackColor = Color.White,
                Cursor = Cursors.SizeAll,
            };
            var lblHeader = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(BodyHorizontalPadding, 0, CloseButtonSize + 8, 0),
                UseMnemonic = false,
                Cursor = Cursors.SizeAll,
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
                Location = new Point(DialogWidth - CloseButtonSize - 2, (HeaderHeight - CloseButtonSize) / 2),
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

            void StartDrag(object? s, MouseEventArgs e)
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

            // Body container
            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
            };

            int contentWidth = DialogWidth - BodyHorizontalPadding * 2;
            int yCursor = BodyTopPadding;

            // Message label - explains which property and why it is required
            var lblMessage = new Label
            {
                Text = message ?? "",
                Font = new Font("Segoe UI", 9.75F),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                Location = new Point(BodyHorizontalPadding, yCursor),
                Size = new Size(contentWidth, 0),
                TextAlign = ContentAlignment.TopLeft,
                UseMnemonic = false,
            };
            // Measure to fit the message height (descender-safe, mirrors
            // AddinMessageDialog.ResizeToFitBody).
            Size measured;
            using (var g = CreateGraphics())
            {
                measured = TextRenderer.MeasureText(g, lblMessage.Text, lblMessage.Font,
                    new Size(contentWidth, int.MaxValue), TextFormatFlags.WordBreak);
            }
            lblMessage.Height = measured.Height + 4;
            yCursor += lblMessage.Height + MessageToLabelGap;

            // Field label
            var lblField = new Label
            {
                Text = fieldLabel ?? "",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(BodyHorizontalPadding, yCursor),
                UseMnemonic = false,
            };
            yCursor += lblField.PreferredHeight + LabelToFieldGap;

            // Field control: ComboBox (locked DropDownList) when choices are
            // supplied so the user is forced to pick a valid value, TextBox
            // otherwise. Manual 1px border frame for visual consistency with
            // the borderless chrome.
            bool useCombo = choices != null && choices.Count > 0;
            var fieldFrame = new Panel
            {
                Location = new Point(BodyHorizontalPadding, yCursor),
                Size = new Size(contentWidth, FieldHeight),
                BackColor = ClrFieldBorder,
                Padding = new Padding(1),
            };

            if (useCombo)
            {
                _cmbValue = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 10F),
                    BackColor = Color.White,
                    ForeColor = ClrTextPrimary,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                };
                foreach (var c in choices!)
                {
                    if (!string.IsNullOrEmpty(c)) _cmbValue.Items.Add(c);
                }
                // Pre-select case-insensitive match to initialValue when
                // possible so re-prompts after a failed write land back on
                // what the user just picked. Otherwise default to first.
                int matchIdx = -1;
                if (!string.IsNullOrEmpty(initialValue))
                {
                    for (int i = 0; i < _cmbValue.Items.Count; i++)
                    {
                        if (string.Equals(_cmbValue.Items[i]?.ToString() ?? "", initialValue,
                                StringComparison.OrdinalIgnoreCase))
                        { matchIdx = i; break; }
                    }
                }
                if (matchIdx >= 0) _cmbValue.SelectedIndex = matchIdx;
                else if (_cmbValue.Items.Count > 0) _cmbValue.SelectedIndex = 0;
                _cmbValue.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        AcceptIfValid();
                    }
                    else if (e.KeyCode == Keys.Escape)
                    {
                        e.SuppressKeyPress = true;
                        DialogResult = DialogResult.Cancel;
                        Close();
                    }
                };
                fieldFrame.Controls.Add(_cmbValue);
            }
            else
            {
                _txtValue = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 10F),
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.White,
                    ForeColor = ClrTextPrimary,
                    Text = initialValue ?? "",
                };
                _txtValue.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        AcceptIfValid();
                    }
                    else if (e.KeyCode == Keys.Escape)
                    {
                        e.SuppressKeyPress = true;
                        DialogResult = DialogResult.Cancel;
                        Close();
                    }
                };
                fieldFrame.Controls.Add(_txtValue);
            }
            yCursor += FieldHeight + 14;

            body.Controls.Add(lblMessage);
            body.Controls.Add(lblField);
            body.Controls.Add(fieldFrame);

            // Footer with OK / Cancel. Use a FlowLayoutPanel docked Right
            // inside the footer for the button group - this guarantees
            // button visibility no matter how the parent's Padding /
            // Anchor / Form.Padding interactions land. (The earlier
            // absolute-position approach produced a dialog with the
            // buttons rendered but invisible against the gray surface
            // on r10.10 - verified 2026-05-17 by user-reported screenshot.)
            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = ClrSurface,
            };

            // Cancel button label surfaces the destructive action so the
            // user knows what Cancel will do before they click. CREATE
            // discards the new object; UPDATE reverts the edit. The
            // button has to be a bit wider to fit the longer text.
            string cancelText = mode == RequiredOperationMode.Create
                ? $"Discard New {(string.IsNullOrEmpty(objectKind) ? "Object" : objectKind)}"
                : "Revert Change";
            int cancelWidth = mode == RequiredOperationMode.Create ? 170 : 130;

            var btnCancel = new Button
            {
                Text = cancelText,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextPrimary,
                BackColor = Color.White,
                Size = new Size(cancelWidth, 32),
                Margin = new Padding(8, 12, 0, 12),
                TabIndex = 2,
            };
            btnCancel.FlatAppearance.BorderSize = 1;
            btnCancel.FlatAppearance.BorderColor = ClrBorder;
            btnCancel.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            var btnApply = new Button
            {
                Text = "OK",
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = ClrPrimary,
                Size = new Size(96, 32),
                Margin = new Padding(0, 12, 0, 12),
                TabIndex = 1,
            };
            btnApply.FlatAppearance.BorderSize = 0;
            btnApply.Click += (_, _) => AcceptIfValid();

            // FlowLayoutPanel: RightToLeft so buttons stack from the right
            // edge in the order added. AutoSize=false + Dock=Fill so the
            // panel always covers the footer's full width regardless of
            // any DPI scaling quirks.
            var footerFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 0, 16, 0),
                BackColor = ClrSurface,
                WrapContents = false,
                AutoSize = false,
            };
            footerFlow.Controls.Add(btnCancel);
            footerFlow.Controls.Add(btnApply);
            footer.Controls.Add(footerFlow);

            // Add order: outermost (Top/Bottom edges) added last so dock
            // layout pins them to the form edges, Fill body added first
            // gets the remaining space.
            Controls.Add(body);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            AcceptButton = btnApply;
            CancelButton = btnCancel;
            ActiveControl = (Control?)_cmbValue ?? (Control?)_txtValue;

            // Final size: chrome + body content + footer.
            int chromeHeight = AccentStripHeight + HeaderHeight + 1 + 1 + FooterHeight;
            int totalHeight = chromeHeight + yCursor + BodyTopPadding;
            ClientSize = new Size(DialogWidth, totalHeight);

            Shown += (_, _) =>
            {
                if (IsDisposed) return;
                try { SetForegroundWindow(Handle); } catch { /* best effort */ }
                if (_cmbValue != null)
                {
                    _cmbValue.Focus();
                }
                else if (_txtValue != null)
                {
                    _txtValue.Focus();
                    _txtValue.SelectAll();
                }
            };
        }

        private void AcceptIfValid()
        {
            // Required-by-contract: empty submission is not allowed. The
            // caller can still detect a cancellation via DialogResult.
            string typed;
            if (_cmbValue != null)
            {
                typed = (_cmbValue.SelectedItem?.ToString() ?? "").Trim();
                if (typed.Length == 0)
                {
                    _cmbValue.Focus();
                    if (_cmbValue.Items.Count > 0 && _cmbValue.SelectedIndex < 0)
                        _cmbValue.SelectedIndex = 0;
                    return;
                }
            }
            else
            {
                typed = (_txtValue?.Text ?? "").Trim();
                if (typed.Length == 0)
                {
                    _txtValue?.Focus();
                    _txtValue?.SelectAll();
                    return;
                }
            }
            EnteredValue = typed;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// Show the dialog and return the captured value. <paramref name="enteredValue"/>
        /// is non-empty only on <see cref="DialogResult.OK"/>.
        /// <para>
        /// <paramref name="mode"/> controls the Cancel button label so the
        /// destructive consequence (delete vs revert) is visible up front.
        /// <paramref name="objectKind"/> is the human-readable object type
        /// (e.g. "Table", "Column") interpolated into the discard label;
        /// pass empty / null for a generic "Object" fallback.
        /// </para>
        /// </summary>
        public static DialogResult Show(
            string title,
            string message,
            string fieldLabel,
            out string enteredValue,
            IWin32Window? owner = null,
            string initialValue = "",
            RequiredOperationMode mode = RequiredOperationMode.Update,
            string objectKind = "",
            IReadOnlyList<string>? choices = null)
        {
            using var dlg = new RequiredFieldDialog(title, message, fieldLabel, initialValue, mode, objectKind, choices);
            dlg.PositionOnActiveScreen(owner);
            var rc = dlg.ShowDialog(owner);
            enteredValue = rc == DialogResult.OK ? dlg.EnteredValue : "";
            return rc;
        }

        private void PositionOnActiveScreen(IWin32Window? owner)
        {
            Screen target;
            var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
            if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
            {
                target = Screen.FromControl(addinForm);
            }
            else if (owner is Control { IsDisposed: false } ownerCtrl && ownerCtrl.IsHandleCreated)
            {
                target = Screen.FromControl(ownerCtrl);
            }
            else
            {
                IntPtr fg = IntPtr.Zero;
                try { fg = GetForegroundWindow(); } catch { /* primary fallback below */ }
                target = fg != IntPtr.Zero ? Screen.FromHandle(fg) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]);
            }
            var area = target.WorkingArea;
            int x = area.Left + Math.Max(0, (area.Width - Width) / 2);
            int y = area.Top + Math.Max(0, (area.Height - Height) / 2);
            Location = new Point(x, y);
        }
    }
}
