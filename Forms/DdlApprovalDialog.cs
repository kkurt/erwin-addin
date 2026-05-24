using System;
using System.Drawing;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Review popup for Generate-DDL output. Replaces the inline dark
    /// RichTextBox that previously rendered alter scripts on the DDL
    /// Generation tab. Header summarises what is being approved, the
    /// RichTextBox shows the SQL (dark theme + VS-Code-flavoured highlight,
    /// same palette as the legacy inline view), and the user either Cancels
    /// or hits Sent to Approve which calls
    /// <see cref="DdlApprovalService"/> to persist the row.
    /// </summary>
    public sealed class DdlApprovalDialog : Form
    {
        private readonly string _ddlText;
        private readonly int _configId;
        private readonly string _modelName;
        private readonly string _modelLocator;
        private readonly string _sourceMode;
        private readonly string _dbmsType;
        private readonly Action<string> _log;
        // Callback supplied by the owner (ModelConfigForm). Invoked AFTER
        // the user confirms submission. Takes the version description the
        // user typed into ConfirmSubmitDialog; the implementer pushes it
        // through the native bridge into MCXGDMPersister_Mart::SetDescription
        // and then calls pu.Save() so the description is stamped on the
        // resulting Mart version without erwin's own dialog opening.
        // Returns true on commit success.
        private readonly Func<string, System.Threading.Tasks.Task<bool>> _martSaveCallback;

        private RichTextBox _rtb;
        private TextBox _txtNote;
        private Button _btnCancel;
        private Button _btnSend;
        private Button _btnCopy;
        private Label _lblStatus;

        public DdlApprovalDialog(
            string ddlText,
            int configId,
            string modelName,
            string modelLocator,
            string sourceMode,
            string dbmsType,
            Action<string> log,
            Func<string, System.Threading.Tasks.Task<bool>> martSaveCallback = null)
        {
            _ddlText           = ddlText ?? string.Empty;
            _configId          = configId;
            _modelName         = modelName;
            _modelLocator      = modelLocator;
            _sourceMode        = sourceMode;
            _dbmsType          = dbmsType;
            _log               = log ?? (_ => { });
            _martSaveCallback  = martSaveCallback;

            BuildUi();
            Forms.SqlHighlighter.Apply(_rtb, _ddlText);
        }

        private void BuildUi()
        {
            Text             = "DDL Review";
            StartPosition    = FormStartPosition.CenterParent;
            FormBorderStyle  = FormBorderStyle.Sizable;
            MinimizeBox      = false;
            MaximizeBox      = true;
            ShowInTaskbar    = false;
            ClientSize       = new Size(1100, 780);
            MinimumSize      = new Size(720, 480);
            BackColor        = Color.White;
            KeyPreview       = true;
            KeyDown += DdlApprovalDialog_KeyDown;

            var fontBody     = new Font("Segoe UI", 9.5f);
            var fontBodyBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var fontMono     = new Font("Consolas", 9.5f);

            var clrPanelBg   = Color.White;
            var clrHeaderBg  = Color.FromArgb(247, 249, 252);
            var clrDivider   = Color.FromArgb(228, 231, 236);
            var clrTextLite  = Color.FromArgb(90, 90, 90);
            var clrTextDark  = Color.FromArgb(40, 40, 40);
            var clrSecondary = Color.FromArgb(60, 60, 60);

            // ============================================================
            // Header (Dock=Top): title + meta line. The TableLayoutPanel
            // gives both labels predictable rows without manual Y math.
            // ============================================================
            var headerPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 60,
                BackColor = clrHeaderBg,
                Padding = new Padding(20, 8, 20, 8),
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = false,
            };
            headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headerPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            headerPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

            var lblTitle = new Label
            {
                Text = "Review DDL before sending to approval queue",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = clrTextDark,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            headerPanel.Controls.Add(lblTitle, 0, 0);

            var lblMeta = new Label
            {
                Text = BuildMetaLine(),
                Font = fontBody,
                ForeColor = clrTextLite,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            headerPanel.Controls.Add(lblMeta, 0, 1);

            var headerDivider = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = clrDivider,
            };

            // ============================================================
            // Bottom panel (Dock=Bottom): two stacked rows.
            // 1. buttonRow (Dock=Bottom, fixed 48) -- buttons + status
            // 2. noteRow   (Dock=Fill)             -- note label + textbox
            // Using sub-panels rather than absolute X/Y positions makes the
            // layout DPI- and resize-proof (the previous absolute coords
            // pushed buttons off-screen at certain client sizes).
            // ============================================================
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 110,
                BackColor = clrPanelBg,
                Padding = new Padding(20, 6, 20, 10),
            };

            // ------------- Button row (docks Bottom of bottomPanel) -------------
            var buttonRow = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = clrPanelBg,
            };

            // FlowLayoutPanel grows right-to-left as we add children, so the
            // FIRST control added (Send) lands at the right edge and each
            // subsequent control stacks to its left. Final visual order
            // (left to right): Copy | Cancel | Sent to Approve.
            var flowButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = clrPanelBg,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };

            _btnSend = new Button
            {
                Text = "Sent to Approve",
                Size = new Size(160, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(46, 125, 50),
                ForeColor = Color.White,
                Font = fontBodyBold,
                Cursor = Cursors.Hand,
                Margin = new Padding(6, 4, 0, 4),
            };
            _btnSend.FlatAppearance.BorderColor = Color.FromArgb(36, 105, 40);
            _btnSend.Click += BtnSend_Click;
            flowButtons.Controls.Add(_btnSend);

            _btnCancel = new Button
            {
                Text = "Cancel",
                Size = new Size(110, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = clrSecondary,
                Font = fontBody,
                Cursor = Cursors.Hand,
                DialogResult = DialogResult.Cancel,
                Margin = new Padding(6, 4, 0, 4),
            };
            _btnCancel.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
            flowButtons.Controls.Add(_btnCancel);
            CancelButton = _btnCancel;

            _btnCopy = new Button
            {
                Text = "Copy",
                Size = new Size(90, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = clrSecondary,
                Font = fontBody,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 4, 0, 4),
            };
            _btnCopy.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
            _btnCopy.Click += BtnCopy_Click;
            flowButtons.Controls.Add(_btnCopy);

            // Status label fills the remaining width to the left of the
            // button flow. Vertically centered against the button row.
            _lblStatus = new Label
            {
                Text = string.Empty,
                Font = fontBody,
                ForeColor = Color.FromArgb(80, 80, 80),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };

            // Order matters: docked siblings are placed in REVERSE add order,
            // so Add the Fill (status) FIRST and the docked Right (flow) AFTER
            // -- otherwise the Fill consumes the full width and the flow
            // overlaps it. Also Z-order: flow added later = on top.
            buttonRow.Controls.Add(_lblStatus);
            buttonRow.Controls.Add(flowButtons);

            // ------------- Note row (fills the rest of bottomPanel) -------------
            var noteRow = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = clrPanelBg,
            };

            _txtNote = new TextBox
            {
                Font = fontBody,
                Dock = DockStyle.Top,
                MaxLength = 1024,
                Margin = new Padding(0, 0, 0, 0),
            };

            var lblNote = new Label
            {
                Text = "Note (optional):",
                Font = fontBodyBold,
                ForeColor = Color.FromArgb(80, 80, 80),
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            // Dock-Top stacks in REVERSE add order: add textbox first so the
            // label lands ABOVE it.
            noteRow.Controls.Add(_txtNote);
            noteRow.Controls.Add(lblNote);

            // Compose bottomPanel: buttonRow first (Bottom), then noteRow (Fill).
            bottomPanel.Controls.Add(noteRow);
            bottomPanel.Controls.Add(buttonRow);

            var bottomDivider = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = clrDivider,
            };

            // ============================================================
            // Center: SQL viewer (fills remaining space)
            // ============================================================
            _rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Font = fontMono,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(212, 212, 212),
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
            };

            // Form composition: Fill control added FIRST so the Top / Bottom
            // docks (added AFTER) get their slice first and the Fill takes the
            // remainder. Within each dock direction, REVERSE add order
            // determines stacking, so the LAST-added Top is the topmost.
            Controls.Add(_rtb);
            Controls.Add(bottomDivider);
            Controls.Add(bottomPanel);
            Controls.Add(headerDivider);
            Controls.Add(headerPanel);
        }

        private string BuildMetaLine()
        {
            string sourcePart = string.IsNullOrEmpty(_sourceMode) ? "Source: (unknown)" : $"Source: {_sourceMode}";
            string dbmsPart   = string.IsNullOrEmpty(_dbmsType)   ? null                : $"DBMS: {_dbmsType}";
            string modelPart  = string.IsNullOrEmpty(_modelName)  ? null                : $"Model: {_modelName}";
            string lenPart    = $"Length: {_ddlText.Length:N0} chars";

            var parts = new System.Collections.Generic.List<string>();
            if (modelPart != null) parts.Add(modelPart);
            parts.Add(sourcePart);
            if (dbmsPart != null) parts.Add(dbmsPart);
            parts.Add(lenPart);
            return string.Join("   |   ", parts);
        }

        private void DdlApprovalDialog_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _btnCancel.Enabled)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        private void BtnCopy_Click(object sender, EventArgs e)
        {
            try
            {
                string text = !string.IsNullOrEmpty(_rtb.SelectedText) ? _rtb.SelectedText : _ddlText;
                if (string.IsNullOrEmpty(text)) return;
                Clipboard.SetText(text);
                _btnCopy.Text = "Copied";
                // Brief revert so the user sees confirmation without us
                // permanently masking the original label.
                var revert = new Timer { Interval = 900 };
                revert.Tick += (s, ev) =>
                {
                    revert.Stop();
                    revert.Dispose();
                    if (!IsDisposed && _btnCopy != null && !_btnCopy.IsDisposed)
                        _btnCopy.Text = "Copy";
                };
                revert.Start();
            }
            catch (Exception ex)
            {
                _log($"DdlApprovalDialog: clipboard copy failed: {ex.Message}");
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                _lblStatus.Text = $"Copy failed: {ex.Message}";
            }
        }

        private async void BtnSend_Click(object sender, EventArgs e)
        {
            // Step 1: confirm with the user and collect the version
            // description they want stamped on the Mart commit. The
            // description is what erwin's own MCXIncrementalSave_VersionDescriptionDialog
            // would have asked for - we route it programmatically via the
            // native bridge instead so no extra dialog appears.
            string versionDescription;
            using (var confirm = new ConfirmSubmitDialog())
            {
                if (confirm.ShowDialog(this) != DialogResult.OK)
                    return;
                versionDescription = confirm.Description;
            }

            // Step 2: lock the UI while we commit + insert. Buttons re-enable
            // on failure (so the user can retry); on success Cancel becomes
            // "Close".
            _btnSend.Enabled = false;
            _btnCancel.Enabled = false;
            _btnCopy.Enabled = false;
            _txtNote.Enabled = false;
            _lblStatus.ForeColor = Color.FromArgb(80, 80, 80);

            // Step 3: commit the model to Mart programmatically. The callback
            // pushes our description through
            // MCXGDMPersister_Mart::SetDescription via the bridge and then
            // invokes pu.Save() - erwin's description dialog never opens.
            // If SetDescription fails (e.g. persister not yet cached during
            // this erwin session, see BridgeSetMartSaveDescription comments),
            // the callback surfaces it and we let the user retry.
            if (_martSaveCallback != null)
            {
                _lblStatus.Text = "Committing model to Mart...";
                Application.DoEvents();
                try
                {
                    bool ok = await _martSaveCallback(versionDescription).ConfigureAwait(true);
                    if (!ok)
                    {
                        _log("DdlApprovalDialog: Mart save callback returned false; aborting queue insert.");
                        _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                        _lblStatus.Text = "Mart save failed. Approval not submitted - see Debug Log.";
                        ReenableForRetry();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log($"DdlApprovalDialog: Mart save threw {ex.GetType().Name}: {ex.Message}");
                    _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                    _lblStatus.Text = $"Mart save failed: {ex.Message}";
                    ReenableForRetry();
                    return;
                }
            }

            // Step 4: insert the approval row. NOTE column carries the
            // optional admin-facing note typed in the main popup (this is
            // independent of the Mart version description, which already
            // lives on the new Mart commit and does not need duplicating
            // in our DB).
            string note = _txtNote.Text;

            _lblStatus.Text = "Submitting to approval queue...";
            Application.DoEvents();

            int newId;
            try
            {
                newId = DdlApprovalService.Instance.Submit(
                    configId:    _configId,
                    modelName:   _modelName,
                    modelLocator:_modelLocator,
                    sourceMode:  _sourceMode,
                    dbmsType:    _dbmsType,
                    ddlText:     _ddlText,
                    note:        note,
                    log:         _log);
            }
            catch (Exception ex)
            {
                _log($"DdlApprovalDialog: submit failed: {ex.GetType().Name}: {ex.Message}");
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                _lblStatus.Text = $"Queue insert failed: {ex.Message}";
                ReenableForRetry();
                return;
            }

            // Success: keep the popup open so the user sees the inserted ID.
            // Cancel button re-labels to "Close" - explicit dismissal only.
            _lblStatus.ForeColor = Color.FromArgb(46, 125, 50);
            _lblStatus.Text = $"Submitted to approval queue. ID = {newId}.";
            _btnCancel.Text = "Close";
            _btnCancel.Enabled = true;
            _btnCopy.Enabled = true;
        }

        private void ReenableForRetry()
        {
            _btnSend.Enabled = true;
            _btnCancel.Enabled = true;
            _btnCopy.Enabled = true;
            _txtNote.Enabled = true;
        }
    }
}
