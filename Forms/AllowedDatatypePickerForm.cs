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
    /// when the chosen type takes a parameter (PARAMETRIZATION_TYPE
    /// Standard/Regex/Structured), enters the length/precision parameter(s) -
    /// composed as <c>base(param)</c>. A Structured entry (WP 323) swaps the single
    /// parameter box for dedicated Length + Scale boxes and a length-semantics
    /// combo, composed as <c>base(p[,s][ suffix])</c>.
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
        // Admin DESCRIPTION of the selected type, shown under the combo (2026-07-14).
        // Empty/absent -> hidden (height 0). Sits BETWEEN the combo and the parameter
        // label, so when it grows/shrinks the parameter + error rows and the form
        // height shift by the delta (same technique as ShowInlineError).
        private readonly Label _lblDescription;
        private readonly Panel _paramFrame;
        // STRUCTURED editing surface (WP 323): when the selected entry is Structured the single
        // parameter box is swapped for a Length/precision box + a Scale box (per SCALE_MODE) + a
        // length-semantics combo (SUFFIX_VALUES, per SUFFIX_MODE), all hosted in _paramRow - one
        // moving container so the description-delta layout keeps shifting a single control. The
        // plain _paramFrame remains for Standard/Regex entries and for term-locked parameters
        // (the pinned value is not editable, so the structured surface would be pointless).
        private readonly Panel _paramRow;
        private readonly Label _lblLength;
        private readonly Label _lblScale;
        private readonly Label _lblSuffix;
        private readonly Panel _lengthFrame;
        private readonly Panel _scaleFrame;
        private readonly Panel _suffixFrame;
        private readonly TextBox _txtLength;
        private readonly TextBox _txtScale;
        private readonly ComboBox _cmbSuffix;
        private int _paramRowHeight = FieldHeight; // occupied height of the parameter row (single or structured)
        private bool _structuredActive; // the structured surface is the one currently shown
        private bool _scaleInUse;       // scale column applies to the current structured entry
        private bool _suffixInUse;      // semantics column applies to the current structured entry
        private bool _suffixOptionalMode; // current semantics combo is OPTIONAL (has the empty "no suffix" item)
        // Lossless carry-over across editing surfaces: the raw single-box text is stashed when the
        // structured surface takes over, and restored VERBATIM when the user leaves it without
        // editing the structured fields - so merely browsing the type combo through a Structured
        // entry cannot destroy content the structured grammar cannot represent (e.g. "max" for an
        // nvarchar(max) Regex row, or "200 CHAR" whose suffix the structured entry does not allow).
        private string _stashedPlainParam = "";
        private bool _structuredEdited;   // the USER changed a structured field since the last seed
        private bool _syncingStructured;  // programmatic structured-field updates in progress (no edit tracking)
        private int _descHeight; // current occupied height of the description block (0 when hidden)
        private const int DescriptionGap = 12; // space below the description before the parameter label
        private const int StructuredColumnGap = 8; // horizontal gap between structured columns
        private const int StructuredLabelGap = 4;  // gap between a mini label and its field

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
                _cmbType.Items.Add(FormatComboLabel(entry));
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

            // Admin DESCRIPTION of the selected type - sits here, between the combo and the
            // parameter label, at the parameter's baseline Y. Starts hidden (height 0);
            // LayoutDescription() (invoked from SyncParamEnabled) fills it for the current
            // selection and pushes the parameter + error rows and the form height down by
            // its measured height, so an empty description takes no space.
            _lblDescription = new Label
            {
                Font = new Font("Segoe UI", 8.75F, FontStyle.Italic),
                ForeColor = ClrTextSecondary,
                AutoSize = false,
                Width = contentWidth,
                Location = new Point(BodyHorizontalPadding, yCursor),
                Height = 0,
                Visible = false,
                UseMnemonic = false,
            };

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

            // One container hosts BOTH parameter surfaces (the single box and the structured
            // Length/Scale/Semantics columns); LayoutParamRow toggles between them and sizes the
            // container, so LayoutDescription only ever shifts this one control.
            _paramRow = new Panel
            {
                Location = new Point(BodyHorizontalPadding, yCursor),
                Size = new Size(contentWidth, FieldHeight),
                BackColor = Color.White,
            };
            _paramFrame = new Panel
            {
                Location = new Point(0, 0),
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
            _paramFrame.Controls.Add(_txtParam);

            Label MiniLabel() => new Label
            {
                Font = new Font("Segoe UI", 8.75F),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Visible = false,
                UseMnemonic = false,
            };
            Panel FieldFrame() => new Panel
            {
                Size = new Size(140, FieldHeight),
                BackColor = ClrFieldBorder,
                Padding = new Padding(1),
                Visible = false,
            };
            TextBox FieldText() => new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
            };
            _lblLength = MiniLabel();
            _lblScale = MiniLabel();
            _lblSuffix = MiniLabel();
            _lengthFrame = FieldFrame();
            _scaleFrame = FieldFrame();
            _suffixFrame = FieldFrame();
            _txtLength = FieldText();
            _txtScale = FieldText();
            _cmbSuffix = new ComboBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F),
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
            };
            _lengthFrame.Controls.Add(_txtLength);
            _scaleFrame.Controls.Add(_txtScale);
            _suffixFrame.Controls.Add(_cmbSuffix);
            // Numeric-only input: length is unsigned digits; scale additionally allows one
            // leading '-' (Oracle scale can be negative, e.g. -84). Full range/mode validation
            // still runs through the shared ValidateAgainstEntry on accept.
            _txtLength.KeyPress += (_, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            };
            _txtScale.KeyPress += (_, e) =>
            {
                if (char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar)) return;
                if (e.KeyChar == '-' && _txtScale.SelectionStart == 0)
                {
                    // Judge against the text AFTER the selection is replaced, so select-all +
                    // typing "-84" over an existing "-5" is not blocked by the old minus.
                    string remaining = _txtScale.Text.Remove(_txtScale.SelectionStart, _txtScale.SelectionLength);
                    if (!remaining.Contains('-')) return;
                }
                e.Handled = true;
            };
            // Edit tracking for the lossless carry-over: only USER changes flip the flag -
            // programmatic seeding/combo rebuilds run under _syncingStructured.
            void MarkStructuredEdited(object? s, EventArgs e)
            {
                if (!_syncingStructured) _structuredEdited = true;
            }
            _txtLength.TextChanged += MarkStructuredEdited;
            _txtScale.TextChanged += MarkStructuredEdited;
            _cmbSuffix.SelectedIndexChanged += MarkStructuredEdited;
            _paramRow.Controls.Add(_paramFrame);
            _paramRow.Controls.Add(_lblLength);
            _paramRow.Controls.Add(_lblScale);
            _paramRow.Controls.Add(_lblSuffix);
            _paramRow.Controls.Add(_lengthFrame);
            _paramRow.Controls.Add(_scaleFrame);
            _paramRow.Controls.Add(_suffixFrame);
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
                bool on = TakesParameter(entry);                              // Standard, Regex or Structured takes a parameter
                bool optional = on && entry != null && entry.AllowNonParametrized; // ...and may also be used bare
                bool structured = on && IsStructuredEditing(entry);

                // Carry the parameter text across editing surfaces so a value the user already
                // entered survives switching between a structured and a plain entry: entering
                // structured mode stashes the raw single-box text and parses it into the
                // Length/Scale/Semantics fields; leaving recomposes the fields when the user
                // edited them, otherwise restores the stash VERBATIM (browsing through a
                // Structured entry must not destroy "max" / "200 CHAR"-style content).
                string? carriedSuffix = null;
                if (structured && !_structuredActive)
                {
                    _stashedPlainParam = _txtParam.Text;
                    _syncingStructured = true;
                    carriedSuffix = SeedStructuredFromText(_txtParam.Text);
                    _structuredEdited = false;
                }
                else if (!structured && _structuredActive && !_lockParam)
                {
                    _txtParam.Text = _structuredEdited ? BuildStructuredParamText() : _stashedPlainParam;
                }
                // Re-fill the semantics combo for the CURRENT entry on every structured pass:
                // two structured entries may allow different SUFFIX_VALUES, and a still-valid
                // selection (matched OrdinalIgnoreCase) is preserved across the switch.
                if (structured)
                {
                    _syncingStructured = true;
                    ConfigureSuffixCombo(entry!, carriedSuffix ?? CurrentSuffixSelection());
                    _syncingStructured = false;
                }
                _structuredActive = structured;

                // Term-type length lock: the field stays visible (the user must SEE the pinned
                // authoritative value that will be composed) but cannot be edited.
                _txtParam.Enabled = on && !_lockParam;
                _txtLength.Enabled = structured;
                _txtScale.Enabled = structured;
                _cmbSuffix.Enabled = structured;
                // Clear any stale violation message when the chosen type changes so a rule
                // error from the previous selection does not linger over the new one.
                _lblError.Visible = false;
                // The label reflects the entry's parametrization rule: required when the type must
                // carry a parameter, optional when the bare form is also allowed, N/A for NONE;
                // the term lock overrides all of those wordings. Structured entries get the short
                // plural wording - the per-field mini labels carry the part-level detail.
                _lblParam.Text = !on
                    ? "Parameter - not applicable for this type"
                    : _lockParam
                        ? "Parameter - fixed by the glossary term mapping"
                        : structured
                            ? (optional ? "Parameters - optional" : "Parameters - required")
                            : optional
                                ? "Parameter (length or precision,scale) - optional"
                                : "Parameter (length or precision,scale) - required";
                _lblParam.ForeColor = on && !_lockParam ? ClrTextSecondary : ClrBorder;
                _paramFrame.BackColor = on && !_lockParam ? ClrFieldBorder : ClrBorder;
                if (!on) { _txtParam.Text = ""; _lblError.Visible = false; }
                // Re-apply the term-locked parameter on every switch TO a parameter-taking type:
                // a parameterless preselect's pass above just cleared it, and the locked field is
                // not user-editable so nobody else can restore it.
                else if (_lockParam && _pinnedParam.Length > 0) _txtParam.Text = _pinnedParam;

                if (structured && entry != null)
                {
                    // "Precision" reads better than "Length" once a scale column is in play
                    // (NUMBER(p,s)); pure-length types (VARCHAR2(n CHAR)) keep "Length".
                    _lblLength.Text = entry.ScaleMode != StructuredPartMode.None ? "Precision" : "Length";
                    _lblScale.Text = entry.ScaleMode == StructuredPartMode.Required
                        ? "Scale (required)" : "Scale (optional)";
                    _lblSuffix.Text = entry.SuffixMode == StructuredPartMode.Required
                        ? "Semantics (required)" : "Semantics (optional)";
                }

                // Show the selected type's admin DESCRIPTION under the combo and re-flow the
                // parameter/error rows + form height for its size (no-op height change when blank),
                // then swap/size the parameter row for the current editing surface.
                LayoutDescription();
                LayoutParamRow(entry);
            }
            _cmbType.SelectedIndexChanged += (_, _) => SyncParamEnabled();
            SyncParamEnabled();

            void KeyHandler(object? s, KeyEventArgs e)
            {
                // While a combo's dropdown is OPEN, Enter/Escape belong to the dropdown
                // (commit/dismiss the list): ComboBox.IsInputKey claims them, so they arrive
                // here instead of the form's Accept/CancelButton processing - without this
                // guard Esc on an open list would cancel the WHOLE dialog.
                if (s is ComboBox { DroppedDown: true }) return;
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
            _txtLength.KeyDown += KeyHandler;
            _txtScale.KeyDown += KeyHandler;
            _cmbSuffix.KeyDown += KeyHandler;

            body.Controls.Add(lblMessage);
            body.Controls.Add(lblType);
            body.Controls.Add(typeFrame);
            body.Controls.Add(_lblDescription);
            body.Controls.Add(_lblParam);
            body.Controls.Add(_paramRow);
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
            // _descHeight was set by the initial LayoutDescription() (via SyncParamEnabled above)
            // for the preselected type; include it so the form opens tall enough for a description.
            // Likewise _paramRowHeight (set by the initial LayoutParamRow) exceeds FieldHeight when
            // the preselected type is Structured - yCursor only accounted for the single-box row.
            ClientSize = new Size(DialogWidth,
                chromeHeight + yCursor + BodyTopPadding + _descHeight + (_paramRowHeight - FieldHeight));

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

        /// <summary>
        /// Shows the selected type's admin DESCRIPTION (DATATYPE_LIBRARY.DESCRIPTION) under the
        /// combo, hidden when blank. The label sits at the parameter row's baseline Y, so its
        /// occupied height (measured text + <see cref="DescriptionGap"/>) pushes the parameter and
        /// error rows and the form height DOWN by the change since the last layout - the same delta
        /// technique <see cref="ShowInlineError"/> uses. Called on every combo selection change and
        /// once from the ctor for the preselected type (that first call sets <c>_descHeight</c>,
        /// which the ctor folds into <see cref="Form.ClientSize"/>).
        /// </summary>
        private void LayoutDescription()
        {
            string desc = SelectedEntry()?.Description?.Trim() ?? "";
            int occupied = 0;
            if (desc.Length > 0)
            {
                int textHeight;
                using (var g = CreateGraphics())
                {
                    textHeight = TextRenderer.MeasureText(
                        g, desc, _lblDescription.Font,
                        new Size(_lblDescription.Width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                }
                _lblDescription.Text = desc;
                _lblDescription.Height = textHeight + 2;
                _lblDescription.Visible = true;
                occupied = _lblDescription.Height + DescriptionGap;
            }
            else
            {
                _lblDescription.Visible = false;
                _lblDescription.Text = "";
            }

            int delta = occupied - _descHeight;
            if (delta != 0)
            {
                _lblParam.Top += delta;
                _paramRow.Top += delta;
                _lblError.Top += delta;
                Height += delta;
                _descHeight = occupied;
            }
        }

        /// <summary>A type takes a parameter when its parametrization is Standard, Regex or
        /// Structured (None is bare-only). Drives the parameter field enable + the accept logic.</summary>
        private static bool TakesParameter(AllowedDatatypeEntry? entry) =>
            entry != null && entry.ParametrizationType != DatatypeParametrization.None;

        /// <summary>The structured Length/Scale/Semantics surface is used for a Structured entry
        /// UNLESS the parameter is term-locked: a pinned value is not editable, so the single
        /// (disabled) box keeps showing it verbatim - exactly the pre-WP-323 lock behavior.</summary>
        private bool IsStructuredEditing(AllowedDatatypeEntry? entry) =>
            entry != null && entry.ParametrizationType == DatatypeParametrization.Structured && !_lockParam;

        /// <summary>
        /// Seed the structured fields from a raw single-box parameter text (the prefill from the
        /// attempted type, or whatever the user typed before switching entries). Unparseable text
        /// clears the fields. Returns the parsed suffix (empty when none) so the caller can apply
        /// it once <see cref="ConfigureSuffixCombo"/> has (re)built the combo items.
        /// </summary>
        private string SeedStructuredFromText(string raw)
        {
            if (StructuredParamParser.TryParse(raw ?? "", out int p, out int? s, out string suffix))
            {
                _txtLength.Text = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _txtScale.Text = s?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                return suffix;
            }
            _txtLength.Text = "";
            _txtScale.Text = "";
            return "";
        }

        /// <summary>
        /// Fill the semantics combo from the entry's SUFFIX_VALUES: OPTIONAL gets a leading empty
        /// "no suffix" item (and defaults to it); REQUIRED lists the values only and preselects
        /// one ONLY when it is unambiguous (a single value). A desired suffix (carried over from
        /// the previous selection/prefill) is matched OrdinalIgnoreCase and wins when present, so
        /// composition always uses the admin casing.
        /// </summary>
        private void ConfigureSuffixCombo(AllowedDatatypeEntry entry, string desired)
        {
            var values = entry.GetSuffixValueList();
            _cmbSuffix.Items.Clear();
            _suffixOptionalMode = false;
            if (entry.SuffixMode == StructuredPartMode.None || values.Count == 0) return;

            bool optionalSuffix = entry.SuffixMode == StructuredPartMode.Optional;
            _suffixOptionalMode = optionalSuffix;
            if (optionalSuffix) _cmbSuffix.Items.Add("");
            foreach (var v in values) _cmbSuffix.Items.Add(v);

            int desiredIdx = -1;
            if (!string.IsNullOrEmpty(desired))
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (string.Equals(values[i], desired, StringComparison.OrdinalIgnoreCase))
                    { desiredIdx = i + (optionalSuffix ? 1 : 0); break; }
                }
            }
            _cmbSuffix.SelectedIndex = desiredIdx >= 0
                ? desiredIdx
                : optionalSuffix
                    ? 0
                    : values.Count == 1 ? 0 : -1;
        }

        /// <summary>The semantics suffix currently selected in the combo; empty when the combo is
        /// not in use for the current entry, nothing is selected, or the "no suffix" item is.</summary>
        private string CurrentSuffixSelection()
        {
            if (!_suffixInUse || _cmbSuffix.SelectedIndex < 0) return "";
            return (_cmbSuffix.SelectedItem?.ToString() ?? "").Trim();
        }

        /// <summary>
        /// Combine the structured fields into the <c>p[,s][ suffix]</c> parameter text the shared
        /// <see cref="Compose"/> wraps in parens. Empty Length AND Scale mean the user wants the
        /// BARE form (validated by ALLOW_NON_PARAMETRIZED downstream) - deliberately ignoring a
        /// REQUIRED suffix preselection, which cannot be deselected and must not force a
        /// parameter on a bare pick. An OPTIONAL suffix is different: its default is the empty
        /// item, so a non-empty selection is a deliberate user pick and composes even alone -
        /// like a lone scale (",5"), it reaches the shared validation so the error explains the
        /// incomplete combination instead of the pick being silently dropped.
        /// </summary>
        private string BuildStructuredParamText()
        {
            string p = _txtLength.Text.Trim();
            string s = _scaleInUse ? _txtScale.Text.Trim() : "";
            string suffix = CurrentSuffixSelection();
            bool deliberateSuffix = suffix.Length > 0 && _suffixOptionalMode;
            if (p.Length == 0 && s.Length == 0 && !deliberateSuffix) return "";

            var sb = new System.Text.StringBuilder(p);
            if (s.Length > 0) sb.Append(',').Append(s);
            if (suffix.Length > 0) sb.Append(' ').Append(suffix);
            return sb.ToString().Trim();
        }

        /// <summary>The parameter text of the surface currently shown: the structured fields for a
        /// Structured entry (unless term-locked), otherwise the single box. Feeds both the accept
        /// validation and the final composition so they can never diverge.</summary>
        private string CurrentParamText(AllowedDatatypeEntry? entry)
        {
            if (!TakesParameter(entry)) return "";
            return IsStructuredEditing(entry) ? BuildStructuredParamText() : _txtParam.Text.Trim();
        }

        /// <summary>
        /// Swap and size the parameter row for the current selection: the single box for
        /// Standard/Regex/locked entries, or the structured columns (Length always; Scale per
        /// SCALE_MODE; Semantics per SUFFIX_MODE + non-empty values) laid out side by side with
        /// mini labels above the fields. The row's occupied-height delta shifts the error label
        /// and the form height - the same technique as <see cref="LayoutDescription"/>.
        /// </summary>
        private void LayoutParamRow(AllowedDatatypeEntry? entry)
        {
            bool structured = TakesParameter(entry) && IsStructuredEditing(entry);
            bool scaleOn = structured && entry!.ScaleMode != StructuredPartMode.None;
            bool suffixOn = structured && entry!.SuffixMode != StructuredPartMode.None
                            && _cmbSuffix.Items.Count > 0;
            _scaleInUse = scaleOn;
            _suffixInUse = suffixOn;

            _paramFrame.Visible = !structured;
            _lblLength.Visible = _lengthFrame.Visible = structured;
            _lblScale.Visible = _scaleFrame.Visible = scaleOn;
            _lblSuffix.Visible = _suffixFrame.Visible = suffixOn;

            int occupied;
            if (structured)
            {
                int contentWidth = DialogWidth - BodyHorizontalPadding * 2;
                int cols = 1 + (scaleOn ? 1 : 0) + (suffixOn ? 1 : 0);
                int colWidth = (contentWidth - StructuredColumnGap * (cols - 1)) / cols;
                int fieldsY = _lblLength.PreferredHeight + StructuredLabelGap;
                int x = 0;
                void PlaceColumn(Label lbl, Panel frame)
                {
                    lbl.Location = new Point(x, 0);
                    frame.Location = new Point(x, fieldsY);
                    frame.Size = new Size(colWidth, FieldHeight);
                    x += colWidth + StructuredColumnGap;
                }
                PlaceColumn(_lblLength, _lengthFrame);
                if (scaleOn) PlaceColumn(_lblScale, _scaleFrame);
                if (suffixOn) PlaceColumn(_lblSuffix, _suffixFrame);
                // The last visible column absorbs the integer-division remainder.
                var lastFrame = suffixOn ? _suffixFrame : scaleOn ? _scaleFrame : _lengthFrame;
                lastFrame.Width = contentWidth - lastFrame.Left;
                occupied = fieldsY + FieldHeight;
            }
            else
            {
                occupied = FieldHeight;
            }

            _paramRow.Height = occupied;
            int delta = occupied - _paramRowHeight;
            if (delta != 0)
            {
                _lblError.Top += delta;
                Height += delta;
                _paramRowHeight = occupied;
            }
        }

        /// <summary>
        /// Combo dropdown text for a whitelisted datatype: the admin LABEL when set, otherwise the
        /// base token (plus "(n)" when the type takes a parameter). The label lets two rows with the
        /// SAME base datatype but different rules - e.g. an "nvarchar(max)" row and an
        /// "nvarchar &lt;= 4000" row - render distinctly in the picker (WP 303). Display-only: the
        /// composed value still uses the entry's base <see cref="AllowedDatatypeEntry.Datatype"/>.
        /// Public + pure so the fallback is unit-tested.
        /// </summary>
        public static string FormatComboLabel(AllowedDatatypeEntry entry)
        {
            if (entry == null) return "";
            if (!string.IsNullOrWhiteSpace(entry.Label)) return entry.Label.Trim();
            return TakesParameter(entry) ? $"{entry.Datatype} (n)" : entry.Datatype;
        }

        /// <summary>
        /// Pure accept/reject decision for <see cref="AcceptIfValid"/>: given the selected entry,
        /// the raw parameter text, and an optional rule validator, return the inline error message
        /// to show, or <c>null</c> when the composition is acceptable and the dialog may close.
        /// Applies, in order: (1) the whitelist entry's own parametrization rule via the shared
        /// <see cref="AllowedDatatypeService.ValidateAgainstEntry"/> - NONE rejects a parameter,
        /// STANDARD/REGEX/STRUCTURED require one unless the bare form is allowed, REGEX validates
        /// the parameter against REGEX_PATTERN (surfacing REGEX_ERROR on failure), and STRUCTURED
        /// validates the <c>p[,s][ suffix]</c> grammar + bounds/modes (generated messages, never
        /// REGEX_ERROR); (2) the admin naming/regex rules for the COMPOSED datatype (via
        /// <paramref name="ruleValidate"/>). Public + pure (no UI) so the branching is unit-tested.
        /// </summary>
        public static string? ValidateComposition(
            AllowedDatatypeEntry entry, string paramText, Func<string, string?>? ruleValidate)
            => ValidateComposition(entry, paramText, ruleValidate, out _);

        /// <summary>Overload additionally surfacing WHICH structured parameter part failed
        /// (Length/Scale/Suffix; None for non-structured or naming-rule failures) so the dialog
        /// can focus the offending field. Same decision logic as the two-result form.</summary>
        public static string? ValidateComposition(
            AllowedDatatypeEntry entry, string paramText, Func<string, string?>? ruleValidate,
            out StructuredParamPart failedPart)
        {
            failedPart = StructuredParamPart.None;
            if (entry == null) return null; // no selectable type -> caller cancels, not an error

            string param = (paramText ?? "").Trim();
            bool hasParam = param.Length > 0;

            // (1) Whitelist entry rule - the single source of the NONE/STANDARD/REGEX/STRUCTURED
            // semantics, shared with model validation. This surfaces the admin Datatype Library
            // rule inline (e.g. REGEX_ERROR for a parameter that fails REGEX_PATTERN) so a
            // non-conforming datatype can never leave the picker.
            var wl = AllowedDatatypeService.ValidateAgainstEntry(entry, hasParam, param);
            if (!wl.IsValid)
            {
                failedPart = wl.Part;
                return wl.Message;
            }

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

            // One parameter text for BOTH validation and composition (single box or the
            // recombined structured fields) so what is checked is exactly what is committed.
            string param = CurrentParamText(entry);

            string? error;
            var failedPart = StructuredParamPart.None;
            try
            {
                error = ValidateComposition(entry, param, _validate, out failedPart);
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
                if (TakesParameter(entry))
                {
                    if (IsStructuredEditing(entry)) FocusStructuredField(failedPart);
                    else { _txtParam.Focus(); _txtParam.SelectAll(); }
                }
                return;
            }

            SelectedDatatype = Compose(entry.Datatype, param);
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Focus the structured field the failed validation points at (default: the
        /// length box) so the user's next keystroke lands on the offending value - a valid
        /// precision must not get select-all-overwritten because the SCALE was missing.</summary>
        private void FocusStructuredField(StructuredParamPart part)
        {
            switch (part)
            {
                case StructuredParamPart.Scale when _scaleInUse:
                    _txtScale.Focus();
                    _txtScale.SelectAll();
                    break;
                case StructuredParamPart.Suffix when _suffixInUse:
                    _cmbSuffix.Focus();
                    break;
                default:
                    _txtLength.Focus();
                    _txtLength.SelectAll();
                    break;
            }
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
