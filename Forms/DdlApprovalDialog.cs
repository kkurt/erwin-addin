using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Services;
using EliteSoft.MetaAdmin.Shared.Services;        // ApprovalConfigService / ApprovalCallbackInvoker (no-approval REST)
using EliteSoft.MetaAdmin.Shared.Data.Entities;   // DdlApprovalQueue (token source for the callback)

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Review popup for Generate-DDL output. Replaces the inline dark
    /// RichTextBox that previously rendered alter scripts on the DDL
    /// Generation tab. Header summarises what is being approved, the
    /// RichTextBox shows the SQL (dark theme + VS-Code-flavoured highlight,
    /// same palette as the legacy inline view), and the user either Cancels
    /// or hits Send to Approve which calls
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
        // Driven by the admin USE_APPROVEMENT_MECHANISM flag (NOT the approver
        // count). Controls both the button text ("Send to Approve" vs "Send") and
        // the post-submit flow: when ON the row goes in as 'Pending' and the admin
        // fires the REST callback on approve; when OFF the add-in inserts it as
        // 'ApprovedBySystem' and fires the REST callback itself (same logic, shared
        // ApprovalCallbackInvoker). (2026-06-06)
        private readonly bool _approvalEnabled;
        private readonly Action<string> _log;
        // Callback supplied by the owner (ModelConfigForm). Invoked AFTER
        // the user confirms submission. Takes the version description the
        // user typed into ConfirmSubmitDialog; the implementer pushes it
        // through the native bridge into MCXGDMPersister_Mart::SetDescription
        // and then calls pu.Save() so the description is stamped on the
        // resulting Mart version without erwin's own dialog opening.
        // Returns true on commit success.
        private readonly Func<string, System.Threading.Tasks.Task<bool>> _martSaveCallback;

        // Version Promotion (WP 322): when _promotion is non-null the send
        // writes ONE REQUEST_TYPE='PROMOTION' row instead of the DDL row
        // (project decision D3, 2026-07-22). The context carries the config's
        // environment topology, the candidate routes and the preloaded
        // per-transition approver lists; the promotion save callback is the
        // version-capturing Mart save (SavePromotionModelWithDescription).
        private readonly PromotionSendContext _promotion;
        private readonly Func<string, System.Threading.Tasks.Task<PromotionSaveOutcome>> _promotionSaveCallback;
        private readonly int _promotionFirstEnvId;

        private RichTextBox _rtb;
        private TextBox _txtNote;
        private Button _btnCancel;
        private Button _btnSend;
        private Button _btnCopy;
        private Label _lblStatus;
        private ComboBox _cmbPromoteTarget;
        private ComboBox _cmbPromoteSource;
        private Label _lblPromoteFrom;
        private Label _lblPromoteDecision;

        private bool PromotionMode => _promotion != null;

        public DdlApprovalDialog(
            string ddlText,
            int configId,
            string modelName,
            string modelLocator,
            string sourceMode,
            string dbmsType,
            bool approvalEnabled,
            Action<string> log,
            Func<string, System.Threading.Tasks.Task<bool>> martSaveCallback = null,
            PromotionSendContext promotionContext = null,
            Func<string, System.Threading.Tasks.Task<PromotionSaveOutcome>> promotionSaveCallback = null)
        {
            _ddlText           = ddlText ?? string.Empty;
            _configId          = configId;
            _modelName         = modelName;
            _modelLocator      = modelLocator;
            _sourceMode        = sourceMode;
            _dbmsType          = dbmsType;
            _approvalEnabled   = approvalEnabled;
            _log               = log ?? (_ => { });
            _martSaveCallback  = martSaveCallback;
            _promotion         = (promotionContext != null && promotionContext.Routes.Count > 0) ? promotionContext : null;
            _promotionSaveCallback = promotionSaveCallback;
            if (_promotion != null)
            {
                if (_promotionSaveCallback == null)
                    throw new ArgumentNullException(nameof(promotionSaveCallback),
                        "Promotion mode requires the version-capturing Mart save callback.");
                int firstId = _promotion.Environments[0].Id;
                int firstSort = _promotion.Environments[0].SortOrder;
                foreach (var env in _promotion.Environments)
                {
                    if (env.SortOrder < firstSort) { firstSort = env.SortOrder; firstId = env.Id; }
                }
                _promotionFirstEnvId = firstId;
            }

            BuildUi();
            Forms.SqlHighlighter.Apply(_rtb, _ddlText);
        }

        /// <summary>
        /// Pop this review window to the FRONT when it opens. The From-DB /
        /// Review teardown now runs on a background task (perceived-speed
        /// optimization) and can briefly steal foreground while it dismisses
        /// erwin's own dialogs (the "Save Models" mouse-sim), which otherwise
        /// leaves this dialog buried behind erwin. The owner (ModelConfigForm)
        /// also re-asserts the front once teardown fully completes.
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                TopMost = true;
                BringToFront();
                Activate();
                TopMost = false;
            }
            catch (Exception ex) { _log($"DdlApprovalDialog OnShown bring-to-front err: {ex.Message}"); }
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
                // USE_APPROVEMENT_MECHANISM off -> there is no approval queue and the
                // action button reads "Save Model", so the header must describe the
                // save rather than "sending to approval queue". Promotion mode always
                // sends to the (promotion) approval queue regardless of that flag.
                Text = (_approvalEnabled || PromotionMode)
                    ? "Review DDL before sending to approval queue"
                    : "Review DDL before saving the model",
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
                // Promotion mode adds the "Promote to" row above the note row.
                Height = PromotionMode ? 146 : 110,
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
            // (left to right): Copy | Cancel | Send to Approve.
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
                // Promotion mode: the send targets an environment ("Send to
                // Approval" per the release-management spec wording).
                // Otherwise: USE_APPROVEMENT_MECHANISM off -> there is no
                // approval queue; the add-in commits the model to the Mart
                // itself, so the verb is "Save Model" rather than
                // "Send to Approve".
                Text = PromotionMode ? "Send to Approval" : (_approvalEnabled ? "Send to Approve" : "Save Model"),
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

            // ------------- Promotion row (promotion mode only) -------------
            // "Promote to: [target v] From: <derived source> <decision>".
            // Docked Top inside bottomPanel so it sits ABOVE the note row.
            Panel promoRow = null;
            if (PromotionMode)
            {
                promoRow = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 34,
                    BackColor = clrPanelBg,
                };

                var lblPromote = new Label
                {
                    Text = "Promote to:",
                    Font = fontBodyBold,
                    ForeColor = Color.FromArgb(80, 80, 80),
                    AutoSize = false,
                    Size = new Size(85, 24),
                    Location = new Point(0, 6),
                    TextAlign = ContentAlignment.MiddleLeft,
                };

                _cmbPromoteTarget = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = fontBody,
                    Location = new Point(90, 4),
                    Size = new Size(210, 26),
                    // Owner-drawn so each environment shows its COLOR_HEX dot.
                    DrawMode = DrawMode.OwnerDrawFixed,
                };
                _cmbPromoteTarget.DrawItem += CmbPromoteTarget_DrawItem;
                _cmbPromoteTarget.SelectedIndexChanged += (s, ev) => UpdatePromotionRouteInfo();

                _lblPromoteFrom = new Label
                {
                    Font = fontBody,
                    ForeColor = clrTextLite,
                    AutoSize = false,
                    Location = new Point(312, 6),
                    Size = new Size(300, 24),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                };

                // Shown INSTEAD of the From label when the derivation rule
                // leaves several candidate sources (spec rule 3: the user
                // chooses).
                _cmbPromoteSource = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = fontBody,
                    Location = new Point(312, 4),
                    Size = new Size(300, 26),
                    Visible = false,
                };
                _cmbPromoteSource.SelectedIndexChanged += (s, ev) => UpdatePromotionDecisionLabel();

                _lblPromoteDecision = new Label
                {
                    Font = fontBodyBold,
                    AutoSize = false,
                    Location = new Point(624, 6),
                    Size = new Size(220, 24),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                };

                promoRow.Controls.Add(lblPromote);
                promoRow.Controls.Add(_cmbPromoteTarget);
                promoRow.Controls.Add(_lblPromoteFrom);
                promoRow.Controls.Add(_cmbPromoteSource);
                promoRow.Controls.Add(_lblPromoteDecision);

                foreach (var target in PromotionPlanner.TargetsOf(_promotion.Routes))
                    _cmbPromoteTarget.Items.Add(new PromotionTargetItem(target));
                if (_cmbPromoteTarget.Items.Count > 0)
                    _cmbPromoteTarget.SelectedIndex = 0; // preselect; UpdatePromotionRouteInfo fires
            }

            // Compose bottomPanel: buttonRow first (Bottom), then the optional
            // promotion row (Top), then noteRow (Fill). Dock layout runs in
            // reverse add order, so this ordering yields top=promo,
            // middle=note, bottom=buttons.
            bottomPanel.Controls.Add(noteRow);
            if (promoRow != null) bottomPanel.Controls.Add(promoRow);
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

        // ==================== Version Promotion helpers ====================

        /// <summary>Target combo item: environment + display name.</summary>
        private sealed class PromotionTargetItem
        {
            public IntegrationEnvironment Env { get; }
            public PromotionTargetItem(IntegrationEnvironment env) { Env = env; }
            public override string ToString() => Env.Name;
        }

        /// <summary>Source combo item (multi-candidate case): the full route.</summary>
        private sealed class PromotionSourceItem
        {
            public PromotionRoute Route { get; }
            private readonly string _label;
            public PromotionSourceItem(PromotionRoute route, string label) { Route = route; _label = label; }
            public override string ToString() => _label;
        }

        private void CmbPromoteTarget_DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index >= 0 && e.Index < _cmbPromoteTarget.Items.Count)
            {
                var item = (PromotionTargetItem)_cmbPromoteTarget.Items[e.Index];
                // COLOR_HEX dot; the fallback grey matches the pipeline
                // diagram's BorderDefault.
                var dot = EnvironmentPipelineDiagram.TryParseHex(item.Env.ColorHex) ?? Color.FromArgb(148, 163, 184);
                using (var b = new SolidBrush(dot))
                    e.Graphics.FillEllipse(b, e.Bounds.Left + 4, e.Bounds.Top + (e.Bounds.Height - 10) / 2, 10, 10);
                using (var tb = new SolidBrush(e.ForeColor))
                    e.Graphics.DrawString(item.Env.Name, e.Font, tb,
                        e.Bounds.Left + 20, e.Bounds.Top + (e.Bounds.Height - e.Font.Height) / 2f);
            }
            e.DrawFocusRectangle();
        }

        /// <summary>
        /// Re-derives the candidate routes for the selected target: one route =
        /// show the derived source as a label; several = swap in the source
        /// combo so the user chooses (spec derivation rule 3).
        /// </summary>
        private void UpdatePromotionRouteInfo()
        {
            var target = (_cmbPromoteTarget.SelectedItem as PromotionTargetItem)?.Env;
            if (target == null) return;

            var routes = PromotionPlanner.RoutesForTarget(_promotion.Routes, target.Id);
            if (routes.Count == 1)
            {
                _cmbPromoteSource.Visible = false;
                _lblPromoteFrom.Visible = true;
                _lblPromoteFrom.Text = "From: " + DescribePromotionSource(routes[0]);
            }
            else
            {
                _cmbPromoteSource.Items.Clear();
                foreach (var route in routes)
                    _cmbPromoteSource.Items.Add(new PromotionSourceItem(route, "From: " + DescribePromotionSource(route)));
                _cmbPromoteSource.SelectedIndex = 0;
                _lblPromoteFrom.Visible = false;
                _cmbPromoteSource.Visible = true;
            }
            UpdatePromotionDecisionLabel();
        }

        private string DescribePromotionSource(PromotionRoute route)
        {
            return route.Source.Id == _promotionFirstEnvId
                ? $"{route.Source.Name} (current)"
                : $"{route.Source.Name} (holds v{_promotion.PlanningVersion})";
        }

        /// <summary>
        /// Shows what the send will do for the selected route: go to the
        /// approval inbox or auto-approve (transition without approvers).
        /// </summary>
        private void UpdatePromotionDecisionLabel()
        {
            var route = SelectedPromotionRoute();
            if (route == null) { _lblPromoteDecision.Text = string.Empty; return; }

            bool requiresVote = PromotionPlanner.RequiresApprovalVote(
                route.Relation, LookupPromotionApprovers(route.Relation.Id));
            _lblPromoteDecision.Text = requiresVote ? "Approval required" : "Auto-approve";
            _lblPromoteDecision.ForeColor = requiresVote
                ? Color.FromArgb(202, 138, 4)
                : Color.FromArgb(46, 125, 50);
        }

        private PromotionRoute SelectedPromotionRoute()
        {
            var target = (_cmbPromoteTarget?.SelectedItem as PromotionTargetItem)?.Env;
            if (target == null) return null;
            if (_cmbPromoteSource != null && _cmbPromoteSource.Visible)
                return (_cmbPromoteSource.SelectedItem as PromotionSourceItem)?.Route;
            var routes = PromotionPlanner.RoutesForTarget(_promotion.Routes, target.Id);
            return routes.Count > 0 ? routes[0] : null;
        }

        /// <summary>
        /// Approver names of a transition: preloaded in the context for every
        /// offered route; a live read covers a route re-derived at send time
        /// (its relation may not be among the preloaded ones).
        /// </summary>
        private System.Collections.Generic.IReadOnlyList<string> LookupPromotionApprovers(int relationId)
        {
            if (_promotion.ApproversByRelationId.TryGetValue(relationId, out var preloaded))
                return preloaded;
            return PromotionService.Instance.GetRelationApprovers(relationId);
        }

        private void DdlApprovalDialog_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _btnCancel.Enabled)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        // Win32 clipboard P/Invoke - bypasses .NET's Clipboard wrapper which
        // requires STA + an OLE clipboard message pump.
        //
        // BACKGROUND: erwin loads this COM addin on an MTA thread - any
        // System.Windows.Forms.Clipboard.SetText call from there throws
        // "Current thread must be set to single thread apartment (STA) mode
        // before OLE calls can be made" (user-reported 2026-05-30).
        //
        // FIRST FIX ATTEMPT (also user-reported, 2026-05-30): spin a transient
        // STA Thread + Clipboard.SetText + Join. The Join FROZE erwin because
        // a worker STA thread has no message loop, and Clipboard.SetText's
        // internal OLE handshake (clipboard viewer chain notification +
        // delayed-render support) waits for a windows message it never gets.
        // The UI/COM thread blocked on Join -> hard hang.
        //
        // FINAL FIX: write the clipboard text via the raw Win32 user32 API
        // (OpenClipboard / EmptyClipboard / SetClipboardData / CloseClipboard).
        // No OLE, no STA requirement, no message pump - works from any thread.
        // Ownership of the GlobalAlloc'd HGLOBAL transfers to the clipboard
        // on a successful SetClipboardData call; if SetClipboardData fails we
        // GlobalFree the block ourselves.
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
        private const uint GMEM_MOVEABLE = 0x0002;
        private const uint CF_UNICODETEXT = 13;

        /// <summary>
        /// MTA-safe clipboard write. Uses Win32 user32 directly to avoid the
        /// STA/OLE-clipboard requirement of <see cref="Clipboard"/>. Returns
        /// true on success; on failure the caller surfaces the error in
        /// <c>_lblStatus</c> so the user can copy-by-hand instead.
        /// </summary>
        private bool SetClipboardWin32(string text)
        {
            if (text == null) text = string.Empty;
            int charCount = text.Length + 1; // +1 for NUL
            int byteCount = charCount * 2;   // Unicode
            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
            if (hMem == IntPtr.Zero) return false;
            bool transferredOwnership = false;
            try
            {
                IntPtr p = GlobalLock(hMem);
                if (p == IntPtr.Zero) return false;
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                    Marshal.WriteInt16(p, text.Length * 2, 0); // NUL terminator
                }
                finally { GlobalUnlock(hMem); }

                // OpenClipboard with our form's handle so the OS attributes
                // the clipboard owner correctly (and EmptyClipboard fires the
                // expected ownership-change event).
                if (!OpenClipboard(this.Handle)) return false;
                try
                {
                    EmptyClipboard();
                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) return false;
                    transferredOwnership = true; // clipboard owns hMem now
                    return true;
                }
                finally { CloseClipboard(); }
            }
            finally
            {
                if (!transferredOwnership && hMem != IntPtr.Zero) GlobalFree(hMem);
            }
        }

        private void BtnCopy_Click(object sender, EventArgs e)
        {
            try
            {
                string text = !string.IsNullOrEmpty(_rtb.SelectedText) ? _rtb.SelectedText : _ddlText;
                if (string.IsNullOrEmpty(text)) return;
                if (!SetClipboardWin32(text))
                    throw new InvalidOperationException("Win32 clipboard write failed (OpenClipboard/SetClipboardData returned 0).");
                _btnCopy.Text = "Copied";
                // Brief revert so the user sees confirmation without us
                // permanently masking the original label.
                var revert = new System.Windows.Forms.Timer { Interval = 900 };
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
            // Promotion mode always submits a request (never a plain save), so
            // the confirm step must use the "Submit for Approval" wording even
            // when USE_APPROVEMENT_MECHANISM is off - the two flags are
            // independent by spec.
            using (var confirm = new ConfirmSubmitDialog(_approvalEnabled || PromotionMode))
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
            // The promotion pickers must freeze too: the send captures the
            // route ONCE, so a selection change during the async save would
            // show a different target than the one actually submitted.
            if (_cmbPromoteTarget != null) _cmbPromoteTarget.Enabled = false;
            if (_cmbPromoteSource != null) _cmbPromoteSource.Enabled = false;
            _lblStatus.ForeColor = Color.FromArgb(80, 80, 80);

            // Version Promotion (D3): the send writes ONE PROMOTION row and
            // follows its own lock/save/insert sequence.
            if (PromotionMode)
            {
                await RunPromotionSendAsync(versionDescription);
                return;
            }

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
            // With approvers the row waits for the admin ('Pending'); without, there
            // is no gate so it is auto-approved by the system and the add-in fires
            // the REST callback itself.
            string status = _approvalEnabled ? "Pending" : "ApprovedBySystem";

            _lblStatus.Text = _approvalEnabled ? "Submitting to approval queue..." : "Saving...";
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
                    log:         _log,
                    status:      status);
            }
            catch (Exception ex)
            {
                _log($"DdlApprovalDialog: submit failed: {ex.GetType().Name}: {ex.Message}");
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                _lblStatus.Text = $"Save failed: {ex.Message}";
                ReenableForRetry();
                return;
            }

            // Step 5 (no-approval path only): there is no approver gate, so the
            // change is auto-approved. Fire the configured REST callback NOW - the
            // SAME shared ApprovalCallbackInvoker the admin tool runs after its
            // approve step - and stamp the outcome onto the queue row's CALLBACK_*
            // columns. With approvers the add-in does NOT call REST; the admin
            // fires it on approve.
            string restSummary = null;   // null => no REST attempted / none configured
            bool restFailed = false;
            if (!_approvalEnabled)
            {
                try
                {
                    var bootstrap = DatabaseService.Instance.BootstrapService;
                    var approvalCfg = new ApprovalConfigService(bootstrap);
                    var cb = approvalCfg.GetCallback(_configId);
                    if (cb != null && cb.RestConnectionId != null)
                    {
                        _lblStatus.Text = "Calling REST callback...";
                        Application.DoEvents();

                        var queueRow = new DdlApprovalQueue
                        {
                            Id          = newId,
                            ConfigId    = _configId,
                            ModelName   = _modelName,
                            DdlText     = _ddlText,
                            Note        = note,
                            SubmittedBy = Environment.UserName,
                            DbmsType    = _dbmsType,
                        };

                        var invoker = new ApprovalCallbackInvoker(approvalCfg, bootstrap);
                        var result = await invoker.InvokeAsync(_configId, queueRow).ConfigureAwait(true);

                        restFailed = !result.Success;
                        restSummary = result.Message;

                        try
                        {
                            DdlApprovalService.Instance.RecordCallbackResult(
                                newId,
                                result.Success ? "Success" : "Failed",
                                DateTime.UtcNow,
                                result.Message,
                                _log);
                        }
                        catch (Exception ex)
                        {
                            // The REST call already happened; failing to record the
                            // CALLBACK_* audit columns must not mask it - log + append.
                            _log($"DdlApprovalDialog: RecordCallbackResult failed: {ex.Message}");
                            restSummary = (restSummary ?? "") + $" (audit write failed: {ex.Message})";
                        }
                    }
                    else
                    {
                        _log($"DdlApprovalDialog: no REST callback configured for config {_configId}; skipping.");
                    }
                }
                catch (Exception ex)
                {
                    // Do not swallow: the row IS saved, but the REST step errored.
                    _log($"DdlApprovalDialog: REST callback threw {ex.GetType().Name}: {ex.Message}");
                    restFailed = true;
                    restSummary = ex.Message;
                }
            }

            // Step 6 (WP 319): the save is committed (and, for the no-approval
            // path, the REST callback attempted), so the DDL review/save flow is
            // done - close the model automatically (the review dialog's confirm
            // step is labelled "...and Close" and already told the user this would
            // happen). The Mart save above is verified (SaveCurrentModelWithDescription
            // re-probes dirty), so the model is CLEAN and this discard-close drops
            // nothing. Reuses the fast poll-driven close, which dismisses erwin's
            // "Mart Offline" prompt; it MUST run OFF the UI thread so erwin's UI
            // thread stays free to raise + tear down that prompt.
            bool modelClosed;
            try
            {
                _lblStatus.Text = "Closing the model...";
                Application.DoEvents();
                modelClosed = await System.Threading.Tasks.Task
                    .Run(() => MartMartAutomation.CloseActiveModelFast(_log))
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Do not swallow: the save already succeeded, but the auto-close
                // failed - the confirmation popup below surfaces it so the user
                // knows to close the model by hand.
                _log($"DdlApprovalDialog: model auto-close threw {ex.GetType().Name}: {ex.Message}");
                modelClosed = false;
            }

            // Success: the row is saved (and, for the no-approval path, the REST
            // callback - if any - has been attempted). Update the status strip and
            // surface an explicit confirmation modal (user rule 2026-05-31: the
            // colour-change-only signal was too subtle for a multi-step pipeline).
            _lblStatus.ForeColor = restFailed ? Color.FromArgb(192, 57, 43) : Color.FromArgb(46, 125, 50);
            _lblStatus.Text = _approvalEnabled
                ? $"Submitted to approval queue. ID = {newId}."
                : (restFailed ? $"Saved (ID {newId}); REST callback FAILED." : $"Saved. ID = {newId}.");
            _btnCancel.Text = "Close";
            _btnCancel.Enabled = true;
            _btnCopy.Enabled = true;

            // User rule 2026-06-07: the no-approval path must NOT pop a success
            // "saved / Queue ID" modal - there is no approval queue to confirm and
            // the ConfirmSubmitDialog already told the user the model would be
            // saved AND closed. It still pops when something that must not vanish
            // unseen happened (a REST callback failure, or the model could NOT be
            // closed automatically) - the form auto-closes below. The approval
            // path is unchanged: its queue confirmation is legitimate and now also
            // reports the close.
            bool closeFailed = !modelClosed;
            bool showSuccessModal = _approvalEnabled || restFailed || closeFailed;

            try
            {
                // AddinMessageDialog (not MessageBox) keeps the popup on the addin's
                // design language - user rule (lessons.md).
                string body;
                MessageBoxIcon icon;
                string title;
                if (_approvalEnabled)
                {
                    body = $"Model committed to Mart and submitted to the approval queue.\n\nApproval Queue ID: {newId}";
                    icon = MessageBoxIcon.Information;
                    title = "Success";
                }
                else if (restSummary == null)
                {
                    body = $"Model committed to Mart and saved.\n\nQueue ID: {newId}\n(No REST callback is configured for this config.)";
                    icon = MessageBoxIcon.Information;
                    title = "Success";
                }
                else if (restFailed)
                {
                    body = $"Model committed to Mart and saved (Queue ID: {newId}),\nbut the REST callback FAILED:\n\n{restSummary}";
                    icon = MessageBoxIcon.Warning;
                    title = "Saved - callback failed";
                }
                else
                {
                    body = $"Model committed to Mart, saved, and the REST callback succeeded.\n\nQueue ID: {newId}\n{restSummary}";
                    icon = MessageBoxIcon.Information;
                    title = "Success";
                }

                // WP 319: report the automatic model close (or its failure) so the
                // user always knows the current model is gone.
                if (modelClosed)
                {
                    body += "\n\nThe model has been closed.";
                }
                else
                {
                    body += "\n\nWARNING: the model could NOT be closed automatically - please close it manually.";
                    if (icon == MessageBoxIcon.Information) icon = MessageBoxIcon.Warning;
                    if (title == "Success") title = "Saved - model not closed";
                }

                if (showSuccessModal)
                    Forms.AddinMessageDialog.Show(this, body, title, MessageBoxButtons.OK, icon);
            }
            catch (Exception ex)
            {
                // Never let a UI hiccup mask the actual success - the row is
                // already saved, just log and move on.
                _log($"DdlApprovalDialog: success AddinMessageDialog.Show threw {ex.GetType().Name}: {ex.Message}");
            }

            // Auto-close after the user dismisses the modal - the review window has
            // nothing useful left to do. Close the form (also disposes the RTB /
            // fonts that BuildUi created).
            try
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _log($"DdlApprovalDialog: auto-close after success threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// The promotion send sequence (spec flows 1-3): lock-support gate,
        /// version-capturing Mart save, send-time route re-derivation when the
        /// planning assumption flipped, lock apply, single-transaction queue
        /// insert (with the MODEL_ENVIRONMENT_VERSION upsert on the
        /// auto-approve branch), model close and the outcome modal. Every
        /// failure re-enables the dialog for retry and surfaces the reason.
        /// </summary>
        private async System.Threading.Tasks.Task RunPromotionSendAsync(string versionDescription)
        {
            var route = SelectedPromotionRoute();
            if (route == null)
            {
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                _lblStatus.Text = "Select a target environment first.";
                ReenableForRetry();
                return;
            }

            // Lock-support gate BEFORE the Mart save: when the effective
            // PROMOTION_LOCK_TYPE is one this build cannot apply (all
            // non-UNLOCKED codes until the lock phase ships), fail loudly and
            // do NOT mint a Mart version for a send that cannot proceed.
            PromotionLockType lockType;
            var lockService = new UnlockedOnlyMartVersionLockService();
            try
            {
                lockType = PromotionFlow.ResolveEffectiveLockType();
                if (!lockService.Supports(lockType))
                    throw new NotSupportedException(
                        $"Mart lock '{PromotionLockTypeCodes.ToCode(lockType)}' is not supported by this add-in build yet. " +
                        "Set PROMOTION_LOCK_TYPE to UNLOCKED in the admin settings, or wait for the lock-capable release.");
            }
            catch (Exception ex)
            {
                _log($"Promotion: lock gate failed: {ex.GetType().Name}: {ex.Message}");
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                _lblStatus.Text = ex.Message;
                AddinMessageDialog.Show(this, ex.Message, "Version Promotion", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ReenableForRetry();
                return;
            }

            // Version-capturing Mart save (clean model = no save, promote the
            // open version; dirty = save + capture the minted version).
            _lblStatus.Text = "Committing model to Mart...";
            Application.DoEvents();
            PromotionSaveOutcome save;
            try
            {
                save = await _promotionSaveCallback(versionDescription).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log($"Promotion: Mart save threw {ex.GetType().Name}: {ex.Message}");
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                _lblStatus.Text = $"Mart save failed: {ex.Message}";
                ReenableForRetry();
                return;
            }
            if (!save.Ok || save.PromotedVersion == null)
            {
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                _lblStatus.Text = save.FailReason ?? "Mart save failed.";
                ReenableForRetry();
                return;
            }
            int promotedVersion = save.PromotedVersion.Value;

            // Send-time re-derivation: the routes were planned before the save
            // (fresh version expected when dirty, the open version when
            // clean). If the actual save behaved differently - the model was
            // edited or saved behind the dialog - the planned source may no
            // longer satisfy the derivation rule for the ACTUAL promoted
            // version, so re-run the rule and remap the selected target.
            // Two triggers (review finding 2026-07-22): the dirty/clean
            // assumption flipped, OR the model stayed clean but its version
            // ADVANCED behind the dialog (a Mart save in erwin while this
            // dialog was open) - the planned holders were for the OLD version.
            bool plannedFresh = _promotion.PlanningVersion == null;
            bool assumptionFlipped = plannedFresh == !save.SavedNewVersion;
            bool cleanVersionDrifted = !save.SavedNewVersion
                && promotedVersion != (_promotion.PlanningVersion ?? -1);
            if (assumptionFlipped || cleanVersionDrifted)
            {
                try
                {
                    var versions = save.SavedNewVersion
                        ? (System.Collections.Generic.IReadOnlyList<PromotionEnvironmentVersion>)Array.Empty<PromotionEnvironmentVersion>()
                        : PromotionService.Instance.GetEnvironmentVersions(_promotion.MartPath);
                    var rederived = PromotionPlanner.BuildRoutes(
                        save.SavedNewVersion ? -1 : promotedVersion,
                        _promotion.Environments, _promotion.Relations, versions);
                    var candidates = PromotionPlanner.RoutesForTarget(rederived, route.Target.Id);

                    PromotionRoute remapped = null;
                    foreach (var c in candidates)
                        if (c.Relation.Id == route.Relation.Id) { remapped = c; break; }
                    if (remapped == null && candidates.Count == 1) remapped = candidates[0];

                    if (remapped == null)
                    {
                        _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                        _lblStatus.Text = $"The model state changed since this dialog opened; the promotion to '{route.Target.Name}' could not be re-derived. Please close and retry.";
                        ReenableForRetry();
                        return;
                    }
                    if (remapped.Relation.Id != route.Relation.Id)
                        _log($"Promotion: source re-derived for v{promotedVersion}: {route.Source.Name} -> {remapped.Source.Name}");
                    route = remapped;
                }
                catch (Exception ex)
                {
                    _log($"Promotion: send-time re-derivation failed: {ex.GetType().Name}: {ex.Message}");
                    _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                    _lblStatus.Text = $"Promotion route re-check failed: {ex.Message}";
                    ReenableForRetry();
                    return;
                }
            }

            // Approval decision + lock + single-transaction insert.
            _lblStatus.Text = "Submitting promotion request...";
            Application.DoEvents();
            PromotionSubmitResult result;
            bool lockApplied = false;
            try
            {
                bool requiresVote = PromotionPlanner.RequiresApprovalVote(
                    route.Relation, LookupPromotionApprovers(route.Relation.Id));

                lockService.ApplyLock(_promotion.MartPath, promotedVersion, lockType, _log);
                lockApplied = true;

                result = PromotionService.Instance.SubmitPromotion(new PromotionSubmitRequest(
                    ConfigId: _promotion.ConfigId,
                    ModelName: _modelName,
                    MartPath: _promotion.MartPath,
                    SourceMode: _sourceMode,
                    DbmsType: _dbmsType,
                    DdlText: _ddlText,
                    Note: _txtNote.Text,
                    SourceEnvironmentId: route.Source.Id,
                    TargetEnvironmentId: route.Target.Id,
                    ModelVersion: promotedVersion,
                    LockTypeCode: PromotionLockTypeCodes.ToCode(lockType),
                    AutoApprove: !requiresVote), _log);

                // Auto-approve = terminal immediately: release the lock now
                // (spec flow 3). The approver path keeps it until the watcher
                // sees a terminal STATUS.
                if (result.AutoApproved)
                    lockService.ReleaseLock(_promotion.MartPath, promotedVersion, lockType, _log);
            }
            catch (Exception ex)
            {
                // The insert failed after a lock was applied: release it so a
                // failed send never leaves the version locked.
                if (lockApplied)
                {
                    try { lockService.ReleaseLock(_promotion.MartPath, promotedVersion, lockType, _log); }
                    catch (Exception rex) { _log($"Promotion: lock release after failed insert threw: {rex.Message}"); }
                }
                _log($"Promotion: submit failed: {ex.GetType().Name}: {ex.Message}");
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                _lblStatus.Text = $"Promotion submit failed: {ex.Message}";
                ReenableForRetry();
                return;
            }

            // Close the model - same WP 319 behavior as the classic flow (the
            // confirm step already announced the close).
            bool modelClosed;
            try
            {
                _lblStatus.Text = "Closing the model...";
                Application.DoEvents();
                modelClosed = await System.Threading.Tasks.Task
                    .Run(() => MartMartAutomation.CloseActiveModelFast(_log))
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _log($"Promotion: model auto-close threw {ex.GetType().Name}: {ex.Message}");
                modelClosed = false;
            }

            _lblStatus.ForeColor = Color.FromArgb(46, 125, 50);
            _lblStatus.Text = result.AutoApproved
                ? $"Promotion auto-approved. Queue ID = {result.QueueId}."
                : $"Promotion submitted for approval. Queue ID = {result.QueueId}.";
            _btnCancel.Text = "Close";
            _btnCancel.Enabled = true;
            _btnCopy.Enabled = true;

            try
            {
                string body = result.AutoApproved
                    ? $"Model promoted to '{route.Target.Name}'.\n\nVersion v{promotedVersion} is now recorded for {route.Target.Name}.\nQueue ID: {result.QueueId} (auto-approved)"
                    : $"Promotion request submitted for approval.\n\nTarget: {route.Target.Name}\nVersion: v{promotedVersion}\nApproval Queue ID: {result.QueueId}\n\nThe request will be decided in the admin approval inbox.";
                MessageBoxIcon icon = MessageBoxIcon.Information;
                string title = result.AutoApproved ? "Promotion completed" : "Promotion submitted";
                if (modelClosed)
                {
                    body += "\n\nThe model has been closed.";
                }
                else
                {
                    body += "\n\nWARNING: the model could NOT be closed automatically - please close it manually.";
                    icon = MessageBoxIcon.Warning;
                }
                AddinMessageDialog.Show(this, body, title, MessageBoxButtons.OK, icon);
            }
            catch (Exception ex)
            {
                _log($"Promotion: outcome AddinMessageDialog.Show threw {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _log($"Promotion: auto-close after success threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ReenableForRetry()
        {
            _btnSend.Enabled = true;
            _btnCancel.Enabled = true;
            _btnCopy.Enabled = true;
            _txtNote.Enabled = true;
            if (_cmbPromoteTarget != null) _cmbPromoteTarget.Enabled = true;
            if (_cmbPromoteSource != null) _cmbPromoteSource.Enabled = true;
        }
    }
}
