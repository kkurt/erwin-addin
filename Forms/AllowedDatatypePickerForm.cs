#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Modal picker shown when a column's datatype is not in the configuration's
    /// Datatype Library whitelist. Instead of silently forcing the first allowed
    /// type, the user chooses the allowed base type from a locked ComboBox and,
    /// when the chosen type takes a parameter (PARAMETRIZATION_TYPE Standard/Regex),
    /// enters the length/precision parameter(s) - composed as <c>base(param)</c>.
    /// <para>
    /// Cancel / [X] / Esc returns <see cref="DialogResult.Cancel"/>; the caller
    /// keeps its automatic fallback value (the model must never hold a
    /// disallowed type, so the dialog never blocks that invariant).
    /// </para>
    /// <para>
    /// Visual contract matches <see cref="RequiredFieldDialog"/> /
    /// <see cref="AddinMessageDialog"/>: borderless chrome, primary-blue accent
    /// strip, drag-by-header, active-screen positioning, sticky TopMost.
    /// </para>
    /// </summary>
    public sealed class AllowedDatatypePickerForm : Form
    {
        // Design tokens shared with AddinMessageDialog / RequiredFieldDialog.
        private static readonly Color ClrPrimary = Color.FromArgb(0, 102, 204);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);
        private static readonly Color ClrCloseHover = Color.FromArgb(232, 17, 35);
        private static readonly Color ClrFieldBorder = Color.FromArgb(180, 180, 180);
        private static readonly Color ClrError = Color.FromArgb(196, 43, 28);

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

        // Parameter: digits, optionally ",digits" (length or precision,scale).
        private static readonly Regex ParamPattern =
            new Regex(@"^\s*\d{1,9}(\s*,\s*\d{1,9})?\s*$", RegexOptions.Compiled);

        private readonly List<AllowedDatatypeEntry> _entries;
        private readonly ComboBox _cmbType;
        private readonly TextBox _txtParam;
        private readonly Label _lblParam;
        private readonly Label _lblError;

        // Optional rule validator (2026-07-07): the composed datatype token is passed
        // here on Accept; a non-empty return is a violation message that keeps the
        // dialog open. Lets the caller enforce the admin naming/regex rules (e.g. a
        // "length must be <= 4000" Column.Physical_Data_Type rule) BEFORE the pick is
        // committed - closing the gap where a picked value bypassed rule validation.
        // Contract: returns null/empty when the value is acceptable; never throws.
        private readonly Func<string, string?>? _validate;

        // Term-type length lock (2026-07-09): when the column's glossary term type fixes the
        // length/precision (BUSINESS_TERM never reaches the picker; AMORPH_DATA_TYPE does),
        // the parameter field stays visible but DISABLED, pinned to the authoritative value
        // supplied via prefillParam. The base-type lock is applied directly to the combo in
        // the ctor (no field needed - the combo never re-enables).
        private readonly bool _lockParam;
        // The term-locked parameter value (prefillParam captured at construction). SyncParamEnabled
        // re-applies it whenever a parameter-taking type is selected: the initial preselect can be a
        // PARAMETERLESS base (e.g. DATE picked earlier under AMORPH_DATA_TYPE), whose sync pass
        // clears the textbox - without this, switching back to a parameterized type showed the
        // pinned field EMPTY (live repro 2026-07-10, MUSTERI_NO DATE -> NUMBER).
        private readonly string _pinnedParam;

        /// <summary>Composed datatype (<c>base</c> or <c>base(param)</c>) the user
        /// confirmed with OK. Empty when cancelled.</summary>
        public string SelectedDatatype { get; private set; } = "";

        /// <summary>Compose the physical datatype token from a base type and an
        /// optional parameter string. Empty/whitespace parameter yields the bare
        /// base token. Public+pure so the composition is unit-tested.</summary>
        public static string Compose(string baseToken, string? param)
        {
            string b = (baseToken ?? "").Trim();
            string p = (param ?? "").Trim();
            if (b.Length == 0) return "";
            if (p.Length == 0) return b;
            // Normalize ONLY whitespace around a separator comma ("10 , 2" -> "10,2", cosmetic for
            // Standard precision,scale). Do NOT strip other internal whitespace: it is significant
            // for Regex-parametrized types such as Oracle "VARCHAR2(55 CHAR)", whose admin regex
            // requires the space. Stripping it (the old `\s+`->"" collapse) produced "55CHAR",
            // which then failed the very Datatype-Library / naming regex the raw parameter had just
            // passed in ValidateComposition - a compose-vs-validate divergence. (2026-07-10)
            string normalized = Regex.Replace(p, @"\s*,\s*", ",");
            return $"{b}({normalized})";
        }

        /// <summary>True when the parameter text is empty (bare type) or matches
        /// <c>n</c> / <c>n,m</c>. Public+pure so validation is unit-tested.</summary>
        public static bool IsValidParameter(string? param)
        {
            if (string.IsNullOrWhiteSpace(param)) return true;
            return ParamPattern.IsMatch(param);
        }

        /// <summary>Extract the parenthesized parameter of a physical datatype
        /// (<c>char(18)</c> -&gt; <c>18</c>; none -&gt; empty). Used to prefill the
        /// parameter field from the attempted type so a length the user already
        /// chose carries over to the allowed replacement.</summary>
        public static string ExtractParameter(string? datatype)
        {
            if (string.IsNullOrEmpty(datatype)) return "";
            var m = Regex.Match(datatype, @"\(([^)]*)\)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private AllowedDatatypePickerForm(
            string title, string message, IReadOnlyList<AllowedDatatypeEntry> entries,
            string preselectBase, string prefillParam, Func<string, string?>? validate,
            bool lockType, bool lockParam)
        {
            _entries = entries.Where(e => e != null && !string.IsNullOrEmpty(e.Datatype)).ToList();
            _validate = validate;
            _lockParam = lockParam;
            _pinnedParam = lockParam ? (prefillParam ?? "") : "";

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

            var accentStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = AccentStripHeight,
                BackColor = ClrPrimary,
            };

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

            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
            };

            int contentWidth = DialogWidth - BodyHorizontalPadding * 2;
            int yCursor = BodyTopPadding;

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
            Size measured;
            using (var g = CreateGraphics())
            {
                measured = TextRenderer.MeasureText(g, lblMessage.Text, lblMessage.Font,
                    new Size(contentWidth, int.MaxValue), TextFormatFlags.WordBreak);
            }
            lblMessage.Height = measured.Height + 4;
            yCursor += lblMessage.Height + MessageToLabelGap;

            var lblType = new Label
            {
                Text = "Allowed datatype",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(BodyHorizontalPadding, yCursor),
                UseMnemonic = false,
            };
            yCursor += lblType.PreferredHeight + LabelToFieldGap;

            var typeFrame = new Panel
            {
                Location = new Point(BodyHorizontalPadding, yCursor),
                Size = new Size(contentWidth, FieldHeight),
                BackColor = ClrFieldBorder,
                Padding = new Padding(1),
            };
            _cmbType = new ComboBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F),
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
            };
            foreach (var entry in _entries)
                _cmbType.Items.Add(TakesParameter(entry) ? $"{entry.Datatype} (n)" : entry.Datatype);
            int matchIdx = -1;
            if (!string.IsNullOrEmpty(preselectBase))
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (string.Equals(_entries[i].Datatype, preselectBase, StringComparison.OrdinalIgnoreCase))
                    { matchIdx = i; break; }
                }
            }
            _cmbType.SelectedIndex = matchIdx >= 0 ? matchIdx : (_cmbType.Items.Count > 0 ? 0 : -1);
            // Term-type base lock: the column's glossary term type says the BASE type may not
            // change - pin the combo to the (caller-guaranteed whitelisted) authoritative base
            // and disable it. Only when the preselect matched: locking to an arbitrary first
            // entry would pin the WRONG base, so an unmatched preselect leaves the combo free
            // (the caller routes non-representable locked bases to a warn-only dialog instead).
            if (lockType && matchIdx >= 0)
                _cmbType.Enabled = false;
            typeFrame.Controls.Add(_cmbType);
            yCursor += FieldHeight + 12;

            _lblParam = new Label
            {
                Text = "Parameter (length or precision,scale) - optional",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(BodyHorizontalPadding, yCursor),
                UseMnemonic = false,
            };
            yCursor += _lblParam.PreferredHeight + LabelToFieldGap;

            var paramFrame = new Panel
            {
                Location = new Point(BodyHorizontalPadding, yCursor),
                Size = new Size(contentWidth, FieldHeight),
                BackColor = ClrFieldBorder,
                Padding = new Padding(1),
            };
            _txtParam = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                Text = prefillParam ?? "",
            };
            paramFrame.Controls.Add(_txtParam);
            yCursor += FieldHeight + 8;

            // AutoSize=false + fixed width so a long rule message (e.g. an admin
            // "NVARCHAR length must be <= 4000" message surfaced by the validator)
            // WRAPS inside the dialog instead of overrunning its width. Height is
            // measured for the default one-line text; ShowInlineError grows the form
            // when a taller message must be shown so the footer never overlaps it.
            _lblError = new Label
            {
                Text = "Enter digits, optionally as precision,scale (e.g. 18 or 10,2).",
                Font = new Font("Segoe UI", 8.75F),
                ForeColor = ClrError,
                AutoSize = false,
                Width = contentWidth,
                Location = new Point(BodyHorizontalPadding, yCursor),
                Visible = false,
                UseMnemonic = false,
            };
            using (var g = CreateGraphics())
            {
                _lblError.Height = TextRenderer.MeasureText(g, _lblError.Text, _lblError.Font,
                    new Size(contentWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
            }
            yCursor += _lblError.Height + 10;

            void SyncParamEnabled()
            {
                var entry = SelectedEntry();
                bool on = TakesParameter(entry);                              // Standard or Regex takes a parameter
                bool optional = on && entry != null && entry.AllowNonParametrized; // ...and may also be used bare
                // Term-type length lock: the field stays visible (the user must SEE the pinned
                // authoritative value that will be composed) but cannot be edited.
                _txtParam.Enabled = on && !_lockParam;
                // Clear any stale violation message when the chosen type changes so a rule
                // error from the previous selection does not linger over the new one.
                _lblError.Visible = false;
                // The label reflects the entry's parametrization rule: required when the type must
                // carry a parameter, optional when the bare form is also allowed, N/A for NONE;
                // the term lock overrides all of those wordings.
                _lblParam.Text = !on
                    ? "Parameter - not applicable for this type"
                    : _lockParam
                        ? "Parameter - fixed by the glossary term mapping"
                        : optional
                            ? "Parameter (length or precision,scale) - optional"
                            : "Parameter (length or precision,scale) - required";
                _lblParam.ForeColor = on && !_lockParam ? ClrTextSecondary : ClrBorder;
                paramFrame.BackColor = on && !_lockParam ? ClrFieldBorder : ClrBorder;
                if (!on) { _txtParam.Text = ""; _lblError.Visible = false; }
                // Re-apply the term-locked parameter on every switch TO a parameter-taking type:
                // a parameterless preselect's pass above just cleared it, and the locked field is
                // not user-editable so nobody else can restore it.
                else if (_lockParam && _pinnedParam.Length > 0) _txtParam.Text = _pinnedParam;
            }
            _cmbType.SelectedIndexChanged += (_, _) => SyncParamEnabled();
            SyncParamEnabled();

            void KeyHandler(object? s, KeyEventArgs e)
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
            }
            _cmbType.KeyDown += KeyHandler;
            _txtParam.KeyDown += KeyHandler;

            body.Controls.Add(lblMessage);
            body.Controls.Add(lblType);
            body.Controls.Add(typeFrame);
            body.Controls.Add(_lblParam);
            body.Controls.Add(paramFrame);
            body.Controls.Add(_lblError);

            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = ClrSurface,
            };
            var btnCancel = new Button
            {
                Text = "Keep Automatic Choice",
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextPrimary,
                BackColor = Color.White,
                Size = new Size(180, 32),
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
            var btnOk = new Button
            {
                Text = "Apply",
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = ClrPrimary,
                Size = new Size(96, 32),
                Margin = new Padding(0, 12, 0, 12),
                TabIndex = 1,
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (_, _) => AcceptIfValid();

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
            footerFlow.Controls.Add(btnOk);
            footer.Controls.Add(footerFlow);

            Controls.Add(body);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            ActiveControl = _cmbType;

            int chromeHeight = AccentStripHeight + HeaderHeight + 1 + 1 + FooterHeight;
            ClientSize = new Size(DialogWidth, chromeHeight + yCursor + BodyTopPadding);

            Shown += (_, _) =>
            {
                if (IsDisposed) return;
                try { SetForegroundWindow(Handle); } catch { /* best effort */ }
                _cmbType.Focus();
            };
        }

        private AllowedDatatypeEntry? SelectedEntry()
            => _cmbType.SelectedIndex >= 0 && _cmbType.SelectedIndex < _entries.Count
                ? _entries[_cmbType.SelectedIndex]
                : null;

        /// <summary>A type takes a parameter when its parametrization is Standard or Regex
        /// (None is bare-only). Drives the parameter field enable + the accept logic.</summary>
        private static bool TakesParameter(AllowedDatatypeEntry? entry) =>
            entry != null && entry.ParametrizationType != DatatypeParametrization.None;

        /// <summary>
        /// Pure accept/reject decision for <see cref="AcceptIfValid"/>: given the selected entry,
        /// the raw parameter text, and an optional rule validator, return the inline error message
        /// to show, or <c>null</c> when the composition is acceptable and the dialog may close.
        /// Applies, in order: (1) the whitelist entry's own parametrization rule via the shared
        /// <see cref="AllowedDatatypeService.ValidateAgainstEntry"/> - NONE rejects a parameter,
        /// STANDARD/REGEX require one unless the bare form is allowed, and REGEX validates the
        /// parameter against REGEX_PATTERN (surfacing REGEX_ERROR on failure); (2) the admin
        /// naming/regex rules for the COMPOSED datatype (via <paramref name="ruleValidate"/>).
        /// Public + pure (no UI) so the branching is unit-tested.
        /// </summary>
        public static string? ValidateComposition(
            AllowedDatatypeEntry entry, string paramText, Func<string, string?>? ruleValidate)
        {
            if (entry == null) return null; // no selectable type -> caller cancels, not an error

            string param = (paramText ?? "").Trim();
            bool hasParam = param.Length > 0;

            // (1) Whitelist entry rule - the single source of the NONE/STANDARD/REGEX semantics,
            // shared with model validation. This surfaces the admin Datatype Library rule inline
            // (e.g. REGEX_ERROR for a parameter that fails REGEX_PATTERN) so a non-conforming
            // datatype can never leave the picker.
            var wl = AllowedDatatypeService.ValidateAgainstEntry(entry, hasParam, param);
            if (!wl.IsValid) return wl.Message;

            // (2) Additional admin naming/regex rules for Physical_Data_Type (separate table from
            // the whitelist) run against the composed value BEFORE committing - closes the Model
            // Explorer gap where a picked value was never naming-validated.
            if (ruleValidate != null)
            {
                string? ruleError = ruleValidate(Compose(entry.Datatype, hasParam ? param : ""));
                if (!string.IsNullOrEmpty(ruleError)) return ruleError;
            }

            return null;
        }

        private void AcceptIfValid()
        {
            var entry = SelectedEntry();
            if (entry == null) { DialogResult = DialogResult.Cancel; Close(); return; }

            string? error;
            try
            {
                error = ValidateComposition(entry, _txtParam.Text, _validate);
            }
            catch (Exception ex)
            {
                // The validator owns its own error handling and must not throw; if it somehow
                // does, fail OPEN (accept the pick) rather than trap the user, but log so the
                // swallow is never silent.
                AddinLogger.Log($"AllowedDatatypePicker: composition/rule validation threw: {ex.Message}");
                error = null;
            }

            if (!string.IsNullOrEmpty(error))
            {
                ShowInlineError(error);
                if (TakesParameter(entry)) { _txtParam.Focus(); _txtParam.SelectAll(); }
                return;
            }

            string param = TakesParameter(entry) ? _txtParam.Text.Trim() : "";
            SelectedDatatype = Compose(entry.Datatype, param);
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Show an inline error under the parameter field, wrapping and growing
        /// the dialog when the message needs more than the reserved single line (long
        /// admin rule messages) so it never overlaps the footer.</summary>
        private void ShowInlineError(string message)
        {
            _lblError.Text = message ?? "";
            int contentWidth = DialogWidth - BodyHorizontalPadding * 2;
            int needed;
            using (var g = CreateGraphics())
            {
                needed = TextRenderer.MeasureText(g, _lblError.Text, _lblError.Font,
                    new Size(contentWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
            }
            int delta = needed - _lblError.Height;
            if (delta > 0)
            {
                _lblError.Height = needed;
                Height += delta; // grow the form so the footer stays clear of the taller message
            }
            _lblError.Visible = true;
        }

        /// <summary>
        /// Show the picker. <paramref name="preselectBase"/> is the base token to
        /// preselect (the automatic fallback, so Enter keeps today's behaviour);
        /// <paramref name="prefillParam"/> seeds the parameter box (typically the
        /// parameter of the attempted disallowed type, e.g. 18 from char(18)).
        /// Returns OK with the composed pick in <paramref name="selectedDatatype"/>,
        /// or Cancel (caller keeps its automatic value).
        /// </summary>
        public static DialogResult Show(
            string title,
            string message,
            IReadOnlyList<AllowedDatatypeEntry> entries,
            string preselectBase,
            string prefillParam,
            out string selectedDatatype,
            IWin32Window? owner = null,
            Func<string, string?>? validate = null,
            bool lockType = false,
            bool lockParam = false)
        {
            using var dlg = new AllowedDatatypePickerForm(title, message, entries, preselectBase, prefillParam, validate, lockType, lockParam);
            dlg.PositionOnActiveScreen(owner);
            var rc = dlg.ShowDialog(owner);
            selectedDatatype = rc == DialogResult.OK ? dlg.SelectedDatatype : "";
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
