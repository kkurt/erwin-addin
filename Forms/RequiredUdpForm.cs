using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Modal dialog that forces the user to fill the required UDPs of a brand
    /// new entity before the new-entity pipeline returns control to erwin.
    ///
    /// Why it exists (2026-05-15): admin can mark a UDP definition with
    /// IS_REQUIRED=true; the existing flow (UdpRuntime.ApplyDefaults +
    /// UdpValidationEngine.ValidateAll) had both halves but no wiring -
    /// ValidateAll was never invoked on the new-entity path, so a required
    /// TABLE_TYPE without a default value could silently stay blank on every
    /// freshly-created table. This form is the wiring: it lists every
    /// IsRequired UDP for the entity's object type whose current value is
    /// empty, lets the user pick / type values, and returns the selections
    /// to the caller for write-back.
    ///
    /// UX rules:
    ///  - OK stays disabled until every required UDP has a non-empty value
    ///    (List = ComboBox with the admin-defined options, Text = free TextBox,
    ///    Date = DateTimePicker, Int/Real = TextBox with numeric validation
    ///    deferred to UdpValidationEngine on save).
    ///  - Cancel / [X] returns DialogResult.Cancel and an empty value map.
    ///    The caller treats that as "user chose not to fill them now"; the
    ///    entity stays with empty values (no auto-delete), the standard UDP
    ///    validation cycle will still surface the same problem on the next
    ///    save / popup tick, so nothing is lost.
    /// </summary>
    public class RequiredUdpForm : Form
    {
        private readonly string _tableName;
        private readonly List<UdpDefinitionRuntime> _requiredUdps;
        private readonly Dictionary<string, Control> _inputs = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);

        /// <summary>UDP name -> value the user picked. Populated on DialogResult.OK.</summary>
        public Dictionary<string, string> SelectedValues { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Same design tokens as QuestionWizardForm so the two dialogs feel
        // like one product. Picking different palettes here would make every
        // new-table popup look like a different app.
        private static readonly Color ClrPrimary = Color.FromArgb(0, 102, 204);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);

        private Button btnOk;
        private Button btnCancel;

        public RequiredUdpForm(string tableName, List<UdpDefinitionRuntime> requiredUdps)
        {
            _tableName = tableName ?? string.Empty;
            // Defensive copy + sort by SortOrder so admin-controlled ordering
            // shows up in the dialog (UDPs are not re-sorted at load time).
            _requiredUdps = (requiredUdps ?? new List<UdpDefinitionRuntime>())
                .OrderBy(u => u.SortOrder)
                .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            InitializeUI();
            BuildInputs();
            UpdateOkButtonState();
        }

        #region UI Setup

        private void InitializeUI()
        {
            this.Text = "Required Properties";
            this.Size = new Size(520, 380);
            this.MinimumSize = new Size(460, 320);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.BackColor = Color.White;
            this.Font = new Font("Segoe UI", 9.5F);

            // Header
            var pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = Color.White,
                Padding = new Padding(20, 12, 20, 8)
            };
            var lblTitle = new Label
            {
                Text = string.IsNullOrEmpty(_tableName) ? "Required Properties" : $"New Table: {_tableName}",
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = true,
                Location = new Point(20, 12)
            };
            var lblSubtitle = new Label
            {
                Text = "The following properties are marked as required and must have a value.",
                Font = new Font("Segoe UI", 9F),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(20, 38)
            };
            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblSubtitle);

            var headerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = ClrBorder };

            // Footer with buttons. AcceptButton = OK so Enter commits when
            // OK is enabled. CancelButton = Cancel so Esc / [X] returns
            // DialogResult.Cancel.
            var pnlFooter = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = ClrSurface,
                Padding = new Padding(16, 8, 16, 8)
            };
            btnOk = new Button
            {
                Text = "OK",
                Size = new Size(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = ClrPrimary,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Enabled = false
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += BtnOk_Click;

            btnCancel = new Button
            {
                Text = "Cancel",
                Size = new Size(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            btnCancel.FlatAppearance.BorderColor = ClrBorder;
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };

            btnOk.Location = new Point(pnlFooter.Width - 116, 9);
            btnCancel.Location = new Point(btnOk.Left - 108, 9);
            pnlFooter.Controls.AddRange(new Control[] { btnCancel, btnOk });
            pnlFooter.Resize += (s, e) =>
            {
                btnOk.Location = new Point(pnlFooter.Width - 116, 9);
                btnCancel.Location = new Point(btnOk.Left - 108, 9);
            };

            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };

            // Scrollable content area for the per-UDP rows. Dock order is
            // Bottom (footer) first so the scroll panel claims everything
            // above; without that the panel covers the footer.
            var pnlContent = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(24, 16, 24, 8),
                AutoScroll = true,
                Tag = "content-panel" // BuildInputs locates this by Tag
            };

            this.Controls.Add(pnlContent);
            this.Controls.Add(footerSep);
            this.Controls.Add(pnlFooter);
            this.Controls.Add(headerSep);
            this.Controls.Add(pnlHeader);

            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }

        private void BuildInputs()
        {
            var pnlContent = this.Controls
                .OfType<Panel>()
                .FirstOrDefault(p => ReferenceEquals(p.Tag, "content-panel") || (p.Tag as string) == "content-panel");
            if (pnlContent == null) return;

            int y = 8;
            const int labelHeight = 18;
            const int controlHeight = 24;
            const int rowGap = 14;
            int rowWidth = pnlContent.ClientSize.Width - 32; // padding inside panel

            foreach (var def in _requiredUdps)
            {
                // Label: name + a small "*" tag styled as ClrPrimary to mark
                // it as required. Using a separate red asterisk would be
                // louder but the modal title already says "required", so the
                // blue tag is enough visual hint without screaming.
                var lbl = new Label
                {
                    Text = def.Name + " *",
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                    ForeColor = ClrTextPrimary,
                    AutoSize = false,
                    Location = new Point(0, y),
                    Size = new Size(rowWidth, labelHeight)
                };
                pnlContent.Controls.Add(lbl);
                y += labelHeight + 2;

                Control input = BuildInputForUdp(def, new Size(rowWidth, controlHeight), new Point(0, y));
                if (input != null)
                {
                    _inputs[def.Name] = input;
                    pnlContent.Controls.Add(input);
                }
                y += controlHeight;

                if (!string.IsNullOrWhiteSpace(def.Description))
                {
                    var help = new Label
                    {
                        Text = def.Description,
                        Font = new Font("Segoe UI", 8F),
                        ForeColor = ClrTextSecondary,
                        AutoSize = false,
                        Location = new Point(0, y + 2),
                        Size = new Size(rowWidth, labelHeight)
                    };
                    pnlContent.Controls.Add(help);
                    y += labelHeight;
                }

                y += rowGap;
            }
        }

        /// <summary>
        /// Build the input control for a single required UDP. The mapping is
        /// intentionally narrow - we only handle the UDP types admin can
        /// actually mark IS_REQUIRED on today (List, Text, Date, Int, Real).
        /// Unknown types fall back to a TextBox so the dialog still works,
        /// rather than silently dropping the UDP from the requirement list.
        /// </summary>
        private Control BuildInputForUdp(UdpDefinitionRuntime def, Size size, Point location)
        {
            string type = def.UdpType?.Trim().ToLowerInvariant() ?? "text";

            if (type == "list" && def.ListOptions != null && def.ListOptions.Count > 0)
            {
                var cmb = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Size = size,
                    Location = location,
                    Font = new Font("Segoe UI", 9.5F)
                };
                // Sentinel "(select one)" row keeps the ComboBox empty in a
                // way the user understands. Without it the first option
                // would be auto-selected and the OK button would enable
                // without any user gesture - defeating the "must choose" UX.
                cmb.Items.Add("(select one)");
                foreach (var opt in def.ListOptions.OrderBy(o => o.SortOrder))
                {
                    string display = !string.IsNullOrWhiteSpace(opt.DisplayText) ? opt.DisplayText : opt.Value;
                    cmb.Items.Add(new ListItem(opt.Value, display));
                }
                cmb.SelectedIndex = 0;
                cmb.SelectedIndexChanged += (s, e) => UpdateOkButtonState();
                return cmb;
            }

            if (type == "date")
            {
                var dtp = new DateTimePicker
                {
                    Format = DateTimePickerFormat.Short,
                    Size = size,
                    Location = location,
                    ShowCheckBox = true,
                    Checked = false // mirrors the "(select one)" idea: forces an explicit gesture
                };
                dtp.ValueChanged += (s, e) => UpdateOkButtonState();
                return dtp;
            }

            // Text / Int / Real / unknown - all share a TextBox. Range +
            // type validation runs later in UdpValidationEngine on save; the
            // modal only enforces "non-empty".
            var tb = new TextBox
            {
                Size = size,
                Location = location,
                Font = new Font("Segoe UI", 9.5F)
            };
            tb.TextChanged += (s, e) => UpdateOkButtonState();
            return tb;
        }

        private sealed class ListItem
        {
            public string Value { get; }
            public string Display { get; }
            public ListItem(string value, string display) { Value = value; Display = display; }
            public override string ToString() => Display;
        }

        #endregion

        #region Validation + Output

        /// <summary>
        /// Walks every input control and returns true only when each one has
        /// a real, non-empty value. The OK button mirrors this; the form
        /// cannot be committed until it does.
        /// </summary>
        private bool AllRequiredFilled()
        {
            foreach (var def in _requiredUdps)
            {
                if (!_inputs.TryGetValue(def.Name, out var ctrl)) return false;
                if (string.IsNullOrEmpty(ReadInputValue(ctrl))) return false;
            }
            return true;
        }

        private void UpdateOkButtonState()
        {
            btnOk.Enabled = AllRequiredFilled();
        }

        private static string ReadInputValue(Control ctrl)
        {
            switch (ctrl)
            {
                case ComboBox cmb:
                    if (cmb.SelectedItem is ListItem li) return li.Value ?? string.Empty;
                    if (cmb.SelectedIndex <= 0) return string.Empty; // sentinel
                    return cmb.SelectedItem?.ToString() ?? string.Empty;
                case DateTimePicker dtp:
                    return dtp.Checked ? dtp.Value.ToString("yyyy-MM-dd") : string.Empty;
                case TextBox tb:
                    return tb.Text?.Trim() ?? string.Empty;
                default:
                    return string.Empty;
            }
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            // Defensive recheck. AcceptButton + an idle-state OK could be
            // hit via Enter from a focused TextBox right as the user
            // half-typed; re-validate so a stale Enabled flag never lets a
            // bad commit through.
            if (!AllRequiredFilled())
            {
                UpdateOkButtonState();
                return;
            }

            SelectedValues.Clear();
            foreach (var def in _requiredUdps)
            {
                if (!_inputs.TryGetValue(def.Name, out var ctrl)) continue;
                string val = ReadInputValue(ctrl);
                if (!string.IsNullOrEmpty(val))
                    SelectedValues[def.Name] = val;
            }
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        #endregion
    }
}
