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
    /// "Domain Like Glossary" picker: choose a domain, then one of that domain's columns, and
    /// the chosen glossary row's mapped values are previewed before they are applied to the
    /// erwin column.
    /// <para>
    /// Both combos are TYPEAHEAD: the drop-down list narrows as the user types. No existing form
    /// in the repo does this (there is not a single AutoCompleteMode/AutoCompleteSource usage),
    /// so the behaviour is implemented here by refiltering the item list on TextChanged. The
    /// filter itself is <see cref="Filter"/> - a pure static so it is unit-testable without a UI.
    /// </para>
    /// <para>
    /// Visual contract matches <see cref="AllowedDatatypePickerForm"/> / DevDatabasePickerForm:
    /// borderless chrome, primary-blue accent strip, drag-by-header, active-screen positioning.
    /// </para>
    /// </summary>
    internal sealed class DomainGlossaryPickerForm : Form
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
        private static readonly Color ClrError = Color.FromArgb(196, 43, 28);
        // The composed name is the single most important thing on this dialog - it is what gets
        // written to the model - so it gets a tinted banner rather than a grey caption.
        private static readonly Color ClrOkBack = Color.FromArgb(233, 246, 236);
        private static readonly Color ClrOkText = Color.FromArgb(21, 115, 71);
        private static readonly Color ClrBadBack = Color.FromArgb(253, 236, 234);

        private const int DialogWidth = 480;
        private const int AccentStripHeight = 4;
        private const int HeaderHeight = 46;
        private const int FooterHeight = 58;
        private const int CloseButtonSize = 32;
        private const int BodyPadding = 22;
        private const int FieldHeight = 26;
        private const int LabelHeight = 18;
        private const int FieldGap = 10;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private readonly DomainGlossaryService _glossary;
        private readonly IReadOnlyList<string> _allDomains;
        private readonly ComboBox _cmbDomain;
        private readonly ComboBox _cmbColumn;
        private readonly TextBox _txtPrefix;
        private readonly Panel _nameBanner;
        private readonly Label _lblNamePreview;
        private readonly Label _lblNameStatus;
        private readonly ListBox _preview;
        private readonly Button _btnOk;

        // Guards the TextChanged handlers while we repopulate Items programmatically
        // (Items.Clear() blanks Text, which would re-enter the handler).
        private bool _suppress;

        // The combo whose text the user is currently TYPING into, set by its key handlers and
        // consumed by the next TextChanged. A mouse pick never sets it, so its Text change is not
        // mistaken for typing (which would refilter the list and reopen it over the choice).
        private ComboBox _typingCombo;

        /// <summary>The row the user committed. Null unless the dialog returned OK.</summary>
        public DomainGlossaryRow SelectedRow { get; private set; }

        /// <summary>The composed physical column name. Only valid when the dialog returned OK.</summary>
        public string SelectedName { get; private set; } = "";

        /// <summary>
        /// The fixed literal tail a naming-standard regex demands, e.g.
        /// <c>^[A-Z0-9_]+_ADRES_TIP$</c> -&gt; <c>_ADRES_TIP</c>.
        /// <para>The glossary carries a SUFFIX CONTRACT, not a finished column name: its own
        /// "column" value is a human label ("ADRES TIPI"), while the regex requires a modeler-chosen
        /// prefix plus this tail. Extracting the tail lets the picker ask only for the prefix.</para>
        /// <para>Scans back over word characters from the end, so it stops at the first regex
        /// metacharacter. An all-literal pattern (<c>^ADRES_TIP$</c>) yields the whole token, which
        /// composes correctly with an empty prefix. A pattern with no literal tail
        /// (<c>^[A-Z_]+$</c>) yields "" - the caller then asks for the whole name instead of
        /// fabricating one.</para>
        /// </summary>
        public static string LiteralSuffix(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return "";
            string p = pattern.Trim();
            if (p.EndsWith("$", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            if (p.StartsWith("^", StringComparison.Ordinal)) p = p.Substring(1);

            int i = p.Length;
            while (i > 0 && (char.IsLetterOrDigit(p[i - 1]) || p[i - 1] == '_')) i--;
            return p.Substring(i);
        }

        /// <summary>
        /// The column name a prefix + the row's suffix contract produce. With no suffix to append
        /// (no literal tail in the regex, or no regex at all) the prefix IS the whole name, so the
        /// user types it in full.
        /// </summary>
        public static string ComposeName(string prefix, string suffix) =>
            (prefix ?? "").Trim() + (suffix ?? "");

        /// <summary>
        /// Case-insensitive "contains" filter behind both combos. An empty/whitespace term
        /// yields the full list, so clearing the box restores every option.
        /// </summary>
        public static IReadOnlyList<string> Filter(IReadOnlyList<string> source, string term)
        {
            if (source == null || source.Count == 0) return Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(term)) return source;
            return source
                .Where(s => s != null && s.IndexOf(term.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        private DomainGlossaryPickerForm(string title, string message, DomainGlossaryService glossary,
            string preselectDomain, string preselectColumn, string currentName)
        {
            _glossary = glossary;
            _allDomains = glossary.Domains;

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
            ClientSize = new Size(DialogWidth, 500);

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
                Text = title,
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

            // --- Body: message, the two typeahead combos, then the value preview ---
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(BodyPadding, 16, BodyPadding, 14) };

            var lblMessage = new Label
            {
                Text = message,
                Font = new Font("Segoe UI", 9.75F),
                ForeColor = ClrTextSecondary,
                Dock = DockStyle.Top,
                Height = 38,
                UseMnemonic = false,
            };

            _cmbDomain = MakeCombo("Domain");
            _cmbColumn = MakeCombo("Column type");
            _cmbColumn.Enabled = false;

            _txtPrefix = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.75F),
                Dock = DockStyle.Fill,
                AccessibleName = "Column name",
                // Typed text is left EXACTLY as entered. Forcing upper case here would silently
                // rewrite the user's input; when a naming standard demands upper case its regex
                // already says so, and the banner reports the mismatch instead of hiding it.
                CharacterCasing = CharacterCasing.Normal,
            };
            _txtPrefix.TextChanged += (_, _) => { if (!_suppress) RefreshPreview(); };

            // Result banner: the name that will actually be written. Big, monospaced and tinted,
            // because it is the thing the user is deciding about.
            _lblNameStatus = new Label
            {
                Font = new Font("Segoe UI", 8.5F),
                Dock = DockStyle.Bottom,
                Height = 16,
                UseMnemonic = false,
                AutoEllipsis = true,
            };
            _lblNamePreview = new Label
            {
                Font = new Font("Consolas", 13F, FontStyle.Bold),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                AutoEllipsis = true,
            };
            _nameBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 58,
                Padding = new Padding(12, 8, 12, 6),
                Margin = new Padding(0, 0, 0, FieldGap),
            };
            _nameBanner.Controls.Add(_lblNamePreview);
            _nameBanner.Controls.Add(_lblNameStatus);

            var lblPreview = new Label
            {
                Text = "Values that will be applied",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Dock = DockStyle.Top,
                Height = LabelHeight,
                Margin = new Padding(0, FieldGap, 0, 0),
                UseMnemonic = false,
            };

            var previewFrame = new Panel { Dock = DockStyle.Fill, BackColor = ClrFieldBorder, Padding = new Padding(1) };
            _preview = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                Font = new Font("Consolas", 9.5F),
                IntegralHeight = false,
                ItemHeight = 18,
                SelectionMode = SelectionMode.None,
            };
            previewFrame.Controls.Add(_preview);

            // Fill first, then the Top-docked stack in reverse visual order.
            body.Controls.Add(previewFrame);
            body.Controls.Add(lblPreview);
            body.Controls.Add(_nameBanner);
            body.Controls.Add(WrapTextField(_txtPrefix, "Column Name"));
            body.Controls.Add(WrapField(_cmbColumn, "Column Type"));
            body.Controls.Add(WrapField(_cmbDomain, "Domain"));
            body.Controls.Add(lblMessage);

            // --- Footer: primary + cancel ---
            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = FooterHeight, BackColor = ClrSurface };

            var btnCancel = new Button
            {
                Text = "Cancel",
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextPrimary,
                BackColor = Color.White,
                Size = new Size(100, 32),
                Margin = new Padding(8, 13, 0, 13),
                DialogResult = DialogResult.Cancel,
            };
            btnCancel.FlatAppearance.BorderColor = ClrBorder;
            btnCancel.FlatAppearance.BorderSize = 1;

            _btnOk = new Button
            {
                Text = "Apply",
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = ClrPrimary,
                Size = new Size(110, 32),
                Margin = new Padding(0, 13, 0, 13),
                Enabled = false,
            };
            _btnOk.FlatAppearance.BorderSize = 0;
            _btnOk.FlatAppearance.MouseOverBackColor = ClrPrimaryHover;
            _btnOk.Click += (_, _) => Accept();

            var footerFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 0, 16, 0),
                BackColor = ClrSurface,
                WrapContents = false,
            };
            footerFlow.Controls.Add(btnCancel);
            footerFlow.Controls.Add(_btnOk);
            footer.Controls.Add(footerFlow);

            // Z-order: Fill body first, then edges (bottom then top), accent last.
            Controls.Add(body);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            AcceptButton = _btnOk;
            CancelButton = btnCancel;
            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };

            SetComboItems(_cmbDomain, _allDomains, preselectDomain);
            if (!string.IsNullOrEmpty(preselectDomain))
            {
                OnDomainCommitted();
                if (!string.IsNullOrEmpty(preselectColumn))
                    SetComboItems(_cmbColumn, _glossary.ColumnsOf(preselectDomain), preselectColumn);
            }

            // Seed the name box from the column's current name so a re-pick does not make the user
            // retype it. Placeholder names ("<default>", "PLEASE CHANGE IT") are not real names.
            string seed = (currentName ?? "").Trim();
            if (seed.Length > 0 && !seed.StartsWith("<", StringComparison.Ordinal))
            {
                _suppress = true;
                try { _txtPrefix.Text = seed; }
                finally { _suppress = false; }
            }

            RefreshPreview();

            PositionOnActiveScreen();

            Shown += (_, _) =>
            {
                if (IsDisposed) return;
                try { SetForegroundWindow(Handle); } catch { /* best effort */ }
                _cmbDomain.Focus();
            };
        }

        private ComboBox MakeCombo(string accessibleName)
        {
            var combo = new ComboBox
            {
                // Editable on purpose: typing is what narrows the list.
                DropDownStyle = ComboBoxStyle.DropDown,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.75F),
                // NOT Dock.Fill: a DropDown ComboBox does not stretch vertically, it keeps the
                // height its font dictates. Filling a taller container just leaves the frame's
                // border colour showing under the control (it renders as a half-height combo).
                Dock = DockStyle.Top,
                AccessibleName = accessibleName,
                // The stock AutoComplete* modes PREPEND a completion into the text box; we want the
                // LIST narrowed instead, so completion is left off and the filtering is ours.
                AutoCompleteMode = AutoCompleteMode.None,
                AutoCompleteSource = AutoCompleteSource.None,
            };
            // The list is refiltered ONLY when the user actually typed. Do not try to infer that
            // from event ORDER: on a mouse pick WinForms raises TextChanged BEFORE
            // SelectionChangeCommitted, so a "committing" flag set in the latter never covers the
            // former - the list got refiltered down to the picked item and reopened on top of it,
            // which looked like the choice had to be made twice.
            // A keystroke is unambiguous: picking with the mouse raises no KeyPress at all.
            combo.KeyPress += (_, e) =>
            {
                // Printable characters and Backspace edit the text; other control chars do not.
                if (!char.IsControl(e.KeyChar) || e.KeyChar == '\b') _typingCombo = combo;
            };
            combo.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Delete) _typingCombo = combo;
            };
            combo.TextChanged += (_, _) =>
            {
                bool typed = ReferenceEquals(_typingCombo, combo);
                _typingCombo = null;
                OnComboTextChanged(combo, typed);
            };
            combo.SelectionChangeCommitted += (_, _) => combo.DroppedDown = false;
            combo.Leave += (_, _) => OnComboCommitted(combo);
            return combo;
        }

        private Panel WrapField(ComboBox combo, string caption)
        {
            // The 1px frame is sized to the combo, not the other way round: PreferredHeight is the
            // height the ComboBox will actually paint at for its font, so the border hugs it and
            // no strip of frame colour is left over.
            int frameHeight = combo.PreferredHeight + 2;

            var host = new Panel
            {
                Dock = DockStyle.Top,
                Height = LabelHeight + frameHeight + FieldGap,
                BackColor = Color.White,
                Padding = new Padding(0, 0, 0, FieldGap),
            };
            var frame = new Panel
            {
                Dock = DockStyle.Top,
                Height = frameHeight,
                BackColor = ClrFieldBorder,
                Padding = new Padding(1),
            };
            frame.Controls.Add(combo);
            var label = new Label
            {
                Text = caption,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Dock = DockStyle.Top,
                Height = LabelHeight,
                UseMnemonic = false,
            };
            host.Controls.Add(frame);
            host.Controls.Add(label);
            return host;
        }

        private Panel WrapTextField(TextBox box, string caption)
        {
            int frameHeight = box.PreferredHeight + 6;

            var host = new Panel
            {
                Dock = DockStyle.Top,
                Height = LabelHeight + frameHeight + FieldGap,
                BackColor = Color.White,
                Padding = new Padding(0, 0, 0, FieldGap),
            };
            var frame = new Panel
            {
                Dock = DockStyle.Top,
                Height = frameHeight,
                BackColor = ClrFieldBorder,
                Padding = new Padding(1, 3, 1, 2),
            };
            frame.Controls.Add(box);
            var label = new Label
            {
                Text = caption,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Dock = DockStyle.Top,
                Height = LabelHeight,
                UseMnemonic = false,
            };
            host.Controls.Add(frame);
            host.Controls.Add(label);
            return host;
        }

        private void OnComboTextChanged(ComboBox combo, bool typed)
        {
            if (_suppress) return;
            if (!typed)
            {
                // The text changed because the user picked from the list (or code set it), not
                // because they typed. Leave the item list and the drop-down alone; just refresh
                // whatever depends on the new value.
                if (combo == _cmbDomain) OnDomainCommitted();
                RefreshPreview();
                return;
            }

            var source = combo == _cmbDomain ? _allDomains : _glossary.ColumnsOf(_cmbDomain.Text);
            string term = combo.Text;
            int caret = combo.SelectionStart;
            var filtered = Filter(source, term);

            _suppress = true;
            try
            {
                combo.BeginUpdate();
                combo.Items.Clear();
                foreach (var item in filtered) combo.Items.Add(item);
                combo.EndUpdate();
                // Items.Clear() blanks Text; restore what the user typed and where the caret was.
                combo.Text = term;
                combo.SelectionStart = Math.Min(caret, term?.Length ?? 0);
                combo.SelectionLength = 0;
                combo.DroppedDown = filtered.Count > 0 && !string.IsNullOrEmpty(term);
                // Opening the drop-down programmatically hides the mouse cursor until it moves.
                Cursor = Cursors.Default;
            }
            finally { _suppress = false; }

            if (combo == _cmbDomain) OnDomainCommitted();
            RefreshPreview();
        }

        private void OnComboCommitted(ComboBox combo)
        {
            if (_suppress) return;
            if (combo == _cmbDomain) OnDomainCommitted();
            RefreshPreview();
        }

        /// <summary>
        /// Rebinds the Column combo to the columns of the currently typed domain. A domain that
        /// does not resolve leaves the combo empty and disabled rather than showing a stale list.
        /// </summary>
        private void OnDomainCommitted()
        {
            var columns = _glossary.ColumnsOf(_cmbDomain.Text);
            string keep = columns.Contains(_cmbColumn.Text, StringComparer.OrdinalIgnoreCase)
                ? _cmbColumn.Text
                : "";
            SetComboItems(_cmbColumn, columns, keep);
            _cmbColumn.Enabled = columns.Count > 0;
        }

        private void SetComboItems(ComboBox combo, IReadOnlyList<string> items, string text)
        {
            _suppress = true;
            try
            {
                combo.BeginUpdate();
                combo.Items.Clear();
                foreach (var item in items) combo.Items.Add(item);
                combo.EndUpdate();
                combo.Text = text ?? "";
            }
            finally { _suppress = false; }
        }

        /// <summary>
        /// Shows what applying the current selection would write. Also the OK gate: the button is
        /// enabled only while the two texts resolve to a real glossary row.
        /// </summary>
        private void RefreshPreview()
        {
            var row = _glossary.Find(_cmbDomain.Text, _cmbColumn.Text);
            SelectedRow = row;

            // The glossary's column value is a LABEL ("ADRES TIPI"), and the naming standard is a
            // regex demanding a modeler-chosen prefix plus a fixed tail ("^[A-Z0-9_]+_ADRES_TIP$").
            // So the final name is what the user types here plus that tail, and Apply stays
            // disabled until the result actually satisfies the standard - the name can never be
            // written in a state the standard rejects.
            string suffix = LiteralSuffix(row?.NamingStandard);
            string composed = ComposeName(_txtPrefix.Text, suffix);
            SelectedName = composed;

            bool nameOk;
            string status;
            if (row == null)
            {
                nameOk = false;
                status = "Pick a domain and a column type.";
            }
            else if (string.IsNullOrWhiteSpace(composed))
            {
                nameOk = false;
                status = "Enter a column name.";
            }
            else if (string.IsNullOrWhiteSpace(row.NamingStandard))
            {
                // No standard configured for this row: nothing to enforce, the typed name stands.
                nameOk = true;
                status = "No naming standard for this row.";
            }
            else
            {
                string std = row.NamingStandard.Trim();
                try
                {
                    nameOk = Regex.IsMatch(composed, std);
                    status = nameOk ? $"Matches {std}" : $"Does not match {std}";
                }
                catch (ArgumentException)
                {
                    // A malformed admin-authored pattern cannot be enforced; say so rather than
                    // blocking the user on a rule that can never be satisfied.
                    nameOk = true;
                    status = $"Naming standard is not a valid regex: {std}";
                }
            }

            _lblNamePreview.Text = string.IsNullOrWhiteSpace(composed) ? "(no name yet)" : composed;
            _lblNamePreview.ForeColor = nameOk ? ClrOkText : ClrError;
            _lblNameStatus.Text = status;
            _lblNameStatus.ForeColor = nameOk ? ClrOkText : ClrError;
            _nameBanner.BackColor = nameOk ? ClrOkBack : ClrBadBack;

            _preview.BeginUpdate();
            _preview.Items.Clear();
            if (row == null)
            {
                _preview.Items.Add("Pick a domain and a column type.");
            }
            else
            {
                _preview.Items.Add($"Column type = {row.ColumnName}");
                if (!string.IsNullOrEmpty(suffix)) _preview.Items.Add($"Name suffix = {suffix}");
                foreach (var kv in row.Values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    _preview.Items.Add($"{kv.Key} = {kv.Value}");
                }
            }
            _preview.EndUpdate();

            _btnOk.Enabled = row != null && nameOk;
        }

        private void Accept()
        {
            if (SelectedRow == null) return;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void PositionOnActiveScreen()
        {
            Screen target;
            try
            {
                var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
                if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
                {
                    target = Screen.FromControl(addinForm);
                }
                else
                {
                    IntPtr fg = GetForegroundWindow();
                    target = fg != IntPtr.Zero ? Screen.FromHandle(fg) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]);
                }
            }
            catch { target = Screen.PrimaryScreen ?? Screen.AllScreens[0]; }

            var area = target.WorkingArea;
            int x = area.Left + Math.Max(0, (area.Width - Width) / 2);
            int y = area.Top + Math.Max(0, (area.Height - Height) / 2);
            Location = new Point(x, y);
        }

        /// <summary>
        /// Shows the picker modally. Returns OK with <paramref name="picked"/> set, or Cancel with
        /// it null. The caller decides what Cancel means (GLOSSARY_REQUIRED_OPTION).
        /// </summary>
        public static DialogResult Show(
            string title,
            string message,
            DomainGlossaryService glossary,
            out DomainGlossaryRow picked,
            out string composedName,
            string currentName = null,
            string preselectDomain = null,
            string preselectColumn = null,
            IWin32Window owner = null)
        {
            using var dlg = new DomainGlossaryPickerForm(
                title, message, glossary, preselectDomain, preselectColumn, currentName);
            var rc = dlg.ShowDialog(owner);
            picked = rc == DialogResult.OK ? dlg.SelectedRow : null;
            composedName = rc == DialogResult.OK ? dlg.SelectedName : "";
            return rc;
        }
    }
}
