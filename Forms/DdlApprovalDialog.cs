using System;
using System.Drawing;
using System.Linq;
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
        // TRUE when this MODEL has an approver chain (MODEL_PROMOTION_APPROVER for
        // CONFIG_ID + MART_PATH). Resolved by the owner; the old
        // USE_APPROVEMENT_MECHANISM flag is retired and no longer consulted anywhere
        // (2026-07-25). Controls both the button text ("Send to Approve" vs "Save
        // Model") and the post-submit flow: with a chain the row goes in as 'Pending'
        // and the admin fires the REST callback on approve; without one there is no
        // gate, so the add-in inserts 'ApprovedBySystem' and fires the REST callback
        // itself (same logic, shared ApprovalCallbackInvoker).
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

        // Naming-rule gate on ENTRY to the approval queue (admin
        // ENFORCE_APPROVAL_BLOCKING_RULES, 2026-07-25). Owned by the form because the
        // check needs the live SCAPI session; the dialog just asks "may I submit?" and
        // passes itself as the owner so the report is modal to THIS window. Returns true
        // to proceed. Null = no gate wired (defensive; the form always supplies it).
        private readonly Func<IWin32Window, bool> _approvalBlockingGate;

        // Blocking-rule verdict computed BEFORE this window opened (the walk needs the live
        // SCAPI session and the owning STA, neither of which this dialog has). Non-null with at
        // least one rule = the window splits and shows the report pane. Null = enforcement off,
        // no rules defined, or a caller that does not gate.
        private readonly Services.ApprovalBlockingGateResult _blockingReport;

        // Latch, not a transient state. ReenableForRetry re-enables the submit button on every
        // failure path, so without this a rule-blocked model would become submittable again the
        // first time anything else went wrong.
        private bool _blockedByRules;


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
            Func<string, System.Threading.Tasks.Task<PromotionSaveOutcome>> promotionSaveCallback = null,
            Func<IWin32Window, bool> approvalBlockingGate = null,
            Services.ApprovalBlockingGateResult blockingReport = null)
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
            _approvalBlockingGate  = approvalBlockingGate;
            _blockingReport        = (blockingReport != null && blockingReport.HasReport) ? blockingReport : null;
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
        /// <summary>
        /// Splitter constraints belong here, not in the constructor: by Load the container has
        /// been docked and has its real width, which is what <c>SplitterDistance</c> is validated
        /// against. See <see cref="ConfigureReportSplit"/>.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                ConfigureReportSplit();
            }
            catch (Exception ex)
            {
                // Layout is cosmetic; the verdict and the button state are already applied. This
                // runs on erwin's UI thread inside an async void handler, where an escaping
                // exception terminates the erwin PROCESS - which is exactly what happened on
                // 2026-07-28 before the constraints moved out of the constructor. Logged, never
                // swallowed silently.
                _log($"DdlApprovalDialog: report splitter layout failed ({ex.GetType().Name}: {ex.Message}) - pane left unconstrained.");
            }
        }

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
            // Own a taskbar button (WP 324). erwin's main window is TopMost, and if the user
            // switches to another app (e.g. Chrome) while the DDL pipeline runs, this review
            // window opens but the foreground lock stops it stealing focus, so it lands behind
            // erwin. Without a taskbar button it was reachable only via Alt+Tab and erwin looked
            // frozen (the modal loop runs on erwin's UI thread). A taskbar button makes it
            // findable AND makes Windows auto-flash it when it cannot come to the front, so the
            // user is alerted and one click brings it forward. This is deliberately preferred
            // over persistent TopMost, which could cover erwin's own "Save Models" dialog that
            // the background From-DB/Review teardown dismisses via mouse-sim (see OnShown).
            ShowInTaskbar    = true;
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
                // No approver chain -> there is no approval queue and the
                // action button reads "Save Model", so the header must describe the
                // save rather than "sending to approval queue". Promotion mode always
                // sends to the (promotion) approval queue regardless.
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
                // One caption for both queue-entering paths (user decision 2026-07-27): a
                // promotion request and an approval request are the same act to the reviewer,
                // and the earlier split wording ("Send to Approval" vs "Send to Approve") only
                // invited the question of which one the rules apply to. Both, always.
                // With no approver chain there is no queue at all - the add-in commits to the
                // Mart itself - so that state keeps its own verb.
                Text = (PromotionMode || _approvalEnabled) ? "Send to Approve"
                     : "Save and Close",
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

                var targets = PromotionPlanner.TargetsOf(_promotion.Routes);
                foreach (var target in targets)
                    _cmbPromoteTarget.Items.Add(new PromotionTargetItem(target));

                // A linear pipeline only ever exposes ONE reachable target (the
                // next environment), so a dropdown to "choose" it is noise: show
                // the fixed destination read-only instead. The combo stays created
                // and selected below so SelectedPromotionRoute() and the whole
                // send/re-derivation pipeline are unchanged; a genuine multi-target
                // config (a Test->Prod second hop offered alongside a Dev->Test
                // re-promote) still gets the real dropdown.
                if (targets.Count == 1)
                {
                    _cmbPromoteTarget.Visible = false;
                    var fixedTarget = targets[0];
                    var lblTargetStatic = new Label
                    {
                        Text = string.Empty,
                        Font = fontBodyBold,
                        ForeColor = clrTextDark,
                        AutoSize = false,
                        Location = new Point(90, 6),
                        Size = new Size(210, 24),
                        TextAlign = ContentAlignment.MiddleLeft,
                    };
                    // Mirror the dropdown's owner-drawn COLOR_HEX dot + name so the
                    // read-only destination looks identical to a selected item.
                    lblTargetStatic.Paint += (s, pe) =>
                    {
                        var lbl = (Label)s;
                        var dot = EnvironmentPipelineDiagram.TryParseHex(fixedTarget.ColorHex)
                                  ?? Color.FromArgb(148, 163, 184);
                        using (var b = new SolidBrush(dot))
                            pe.Graphics.FillEllipse(b, 2, (lbl.Height - 10) / 2, 10, 10);
                        using (var tb = new SolidBrush(lbl.ForeColor))
                            pe.Graphics.DrawString(fixedTarget.Name, lbl.Font, tb,
                                18, (lbl.Height - lbl.Font.Height) / 2f);
                    };
                    promoRow.Controls.Add(lblTargetStatic);
                }

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
            //
            // The blocking-rule report replaces _rtb as the Fill control by wrapping it in a
            // splitter. Deliberately ONE conditional swap here rather than adding the pane
            // later and re-parenting: the reverse-add-order rule above is load-bearing, and
            // Controls.SetChildIndex games against it are how docking layouts break.
            Controls.Add(BuildCenterRegionSafely());
            Controls.Add(bottomDivider);
            Controls.Add(bottomPanel);
            Controls.Add(headerDivider);
            Controls.Add(headerPanel);

            ApplyBlockingRuleVerdict();
        }

        /// <summary>
        /// <see cref="BuildCenterRegion"/> with the DDL view as a fallback.
        ///
        /// <para>This constructor runs on erwin's UI thread from an <c>async void</c> handler, so
        /// an exception escaping it does not raise a dialog - it terminates the erwin PROCESS,
        /// losing the user's unsaved model. That happened on 2026-07-28 over a SplitContainer
        /// property order. The report pane is a REVIEW AID; degrading to the DDL alone is always
        /// better than taking erwin down with it.</para>
        ///
        /// <para>Not a silent fallback: the failure is logged and shown in the status line, and
        /// the submit button is still disabled by <see cref="ApplyBlockingRuleVerdict"/>, which
        /// does not depend on the pane. A rendering failure can never turn a refusal into a
        /// submission.</para>
        /// </summary>
        private Control BuildCenterRegionSafely()
        {
            try
            {
                return BuildCenterRegion();
            }
            catch (Exception ex)
            {
                _log($"DdlApprovalDialog: blocking-rule pane could not be built " +
                     $"({ex.GetType().Name}: {ex.Message}) - showing the DDL alone. " +
                     "The submit decision is unaffected.");
                _reportSplit = null;
                _paneBuildFailed = true;
                return _rtb;
            }
        }

        private bool _paneBuildFailed;

        /// <summary>
        /// The DDL viewer alone, or the viewer beside the blocking-rule report when the config
        /// defines any blocking rule. Split even when nothing is violated: a reviewer being let
        /// through still needs to see WHAT was checked (user decision 2026-07-27).
        /// </summary>
        private Control BuildCenterRegion()
        {
            if (_blockingReport == null) return _rtb;

            // NO Panel1MinSize / Panel2MinSize / SplitterDistance here.
            //
            // A freshly constructed SplitContainer is 150 px wide. Setting a minimum re-validates
            // SplitterDistance against `Width - Panel2MinSize`, so `Panel2MinSize = 300` on a
            // 150 px control throws InvalidOperationException - and this runs on erwin's UI
            // thread from an async void handler, where an unhandled exception takes the whole
            // erwin process down. It did, on the first live run (2026-07-28 11:06:24). The
            // constraints are applied in ConfigureReportSplit once docking has given the control
            // its real width.
            _reportSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(208, 208, 208),
                // The DDL is the primary content and must keep the space when the user resizes
                // the window; the report is a fixed-width sidebar.
                FixedPanel = FixedPanel.Panel2,
            };
            var split = _reportSplit;
            split.Panel1.Controls.Add(_rtb);
            split.Panel2.Controls.Add(new ApprovalReportPane(_blockingReport));

            // Widen for the pane, then clamp to the working area. Without the clamp a
            // CenterParent window that grew past the screen pushes its own right-docked button
            // strip off-view on a 1366 px laptop or a 1024 px RDP session.
            var workArea = (Screen.FromControl(this) ?? Screen.PrimaryScreen).WorkingArea;
            int wanted = Math.Min(ClientSize.Width + ReportPaneWidth, workArea.Width - 40);
            ClientSize = new Size(Math.Max(wanted, ClientSize.Width), ClientSize.Height);
            MinimumSize = new Size(Math.Min(1040, workArea.Width - 40), MinimumSize.Height);

            return split;
        }

        private const int ReportPaneWidth = 440;
        private const int DdlPaneMinWidth = 320;
        private const int ReportPaneMinWidth = 300;

        private SplitContainer _reportSplit;

        /// <summary>
        /// Applies the splitter constraints once docking has given the container a real width.
        /// Called from <c>Load</c>, not from the constructor: every one of these three properties
        /// re-validates <c>SplitterDistance</c> against the CURRENT width, and a
        /// not-yet-parented SplitContainer is 150 px wide, so setting a 300 px minimum there
        /// throws. That exception, on erwin's UI thread inside an async void handler, killed the
        /// erwin process on the first live run.
        /// </summary>
        private void ConfigureReportSplit()
        {
            var split = _reportSplit;
            if (split == null || split.IsDisposed) return;

            int width = split.Width;
            // Too narrow to honour both minimums plus the splitter: leave the container
            // unconstrained and centred rather than throw. A cramped splitter is a cosmetic
            // problem; an exception here is a dead erwin.
            if (width < DdlPaneMinWidth + ReportPaneMinWidth + split.SplitterWidth)
            {
                _log($"DdlApprovalDialog: report split too narrow ({width} px) for the minimums - left unconstrained.");
                return;
            }

            split.Panel1MinSize = DdlPaneMinWidth;
            split.Panel2MinSize = ReportPaneMinWidth;
            split.SplitterDistance = ClampSplitterDistance(
                width, split.SplitterWidth, DdlPaneMinWidth, ReportPaneMinWidth,
                width - ReportPaneWidth);
        }

        /// <summary>
        /// The legal <c>SplitterDistance</c> nearest to <paramref name="desired"/>. Pure so the
        /// arithmetic that crashed erwin is covered by a unit test instead of only by a live run.
        /// </summary>
        internal static int ClampSplitterDistance(
            int width, int splitterWidth, int panel1MinSize, int panel2MinSize, int desired)
        {
            int max = width - panel2MinSize - splitterWidth;
            if (max < panel1MinSize) return panel1MinSize;   // caller must not have got here
            return Math.Min(Math.Max(desired, panel1MinSize), max);
        }

        /// <summary>
        /// Applies the verdict to the submit button. A violated rule, or one that could not be
        /// checked at all, disables it - in EVERY caption state, including "Save and Close",
        /// because that one also writes a governance row and the click-time check refuses it
        /// too; leaving it green would make the button lie.
        ///
        /// <para>The disabled button is an EXPLANATION, never the authority. The check inside
        /// <see cref="BtnSend_Click"/> stays exactly as it was and remains what actually refuses
        /// a submission.</para>
        /// </summary>
        private void ApplyBlockingRuleVerdict()
        {
            if (_blockingReport == null) return;

            _blockedByRules = _blockingReport.Blocked;
            var pane = FindReportPane();

            if (_blockedByRules)
            {
                _btnSend.Enabled = false;
                _btnSend.BackColor = Color.FromArgb(158, 158, 158);
                _lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
            }
            else
            {
                _lblStatus.ForeColor = Color.FromArgb(102, 102, 102);
            }

            if (pane != null) _lblStatus.Text = pane.Summary;
            else if (_paneBuildFailed)
            {
                // The verdict still stands and the button still reflects it; only the report is
                // missing. Say so, rather than leaving an unexplained disabled button.
                _lblStatus.Text = _blockedByRules
                    ? "Blocked by naming rules. The rule report could not be shown - see the Debug Log."
                    : "Blocking rules checked. The rule report could not be shown - see the Debug Log.";
            }
        }

        private ApprovalReportPane FindReportPane()
        {
            foreach (Control top in Controls)
            {
                if (top is SplitContainer split)
                {
                    foreach (Control child in split.Panel2.Controls)
                        if (child is ApprovalReportPane pane) return pane;
                }
            }
            return null;
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

            var approvers = LookupPromotionApprovers();
            bool requiresVote = PromotionPlanner.RequiresApprovalVote(route.Relation, approvers);
            if (requiresVote)
            {
                _lblPromoteDecision.Text = "Approval required";
                _lblPromoteDecision.ForeColor = Color.FromArgb(202, 138, 4);
            }
            else
            {
                // Reaching here means the model's approver catalog is empty - since admin
                // 1795ce3 that is the ONLY reason a promotion auto-approves (the transition's
                // REQUIRES_APPROVAL flag no longer participates, see
                // PromotionPlanner.RequiresApprovalVote). Naming the reason keeps it from
                // reading as a bug when the admin expected an approval step.
                _lblPromoteDecision.Text = "Auto-approve (no approvers set for this model)";
                _lblPromoteDecision.ForeColor = Color.FromArgb(46, 125, 50);
            }
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
        /// The approver chain that gates THIS model's promotion, preloaded once into the
        /// send context. Since admin 1795ce3 the chain is scoped to the MODEL
        /// (CONFIG_ID + MART_PATH) and governs every transition, so - unlike the former
        /// per-transition lists - a route re-derived at send time can never miss its
        /// entry and no live per-relation read is needed.
        /// </summary>
        private System.Collections.Generic.IReadOnlyList<string> LookupPromotionApprovers()
            => _promotion.Approvers ?? (System.Collections.Generic.IReadOnlyList<string>)System.Array.Empty<string>();

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
            // Step 0: naming-rule gate on ENTRY to the approval queue (admin
            // ENFORCE_APPROVAL_BLOCKING_RULES). Runs BEFORE the confirm dialog on purpose -
            // asking the user for a version description and only then refusing the
            // submission would waste their input. Independent of the approval mechanism:
            // whether this send ends up Pending, auto-approved or as a promotion request,
            // a rule the admin marked as blocking must block it.
            if (_approvalBlockingGate != null && !_approvalBlockingGate(this))
            {
                _log("DdlApprovalDialog: submission blocked by naming rules; nothing was saved or queued.");
                return;
            }

            // Step 1: confirm with the user and collect the version
            // description they want stamped on the Mart commit. The
            // description is what erwin's own MCXIncrementalSave_VersionDescriptionDialog
            // would have asked for - we route it programmatically via the
            // native bridge instead so no extra dialog appears.
            string versionDescription;
            // Promotion mode always submits a request (never a plain save), so the
            // confirm step uses the "Submit for Approval" wording even when the model
            // has no approver chain - a promotion is a request either way.
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
            // With an approver chain the row waits for the admin ('Pending'); without
            // one there is no gate, so it is auto-approved by the system and the add-in
            // fires the REST callback itself. (The server refuses to resolve a quorum
            // for a model with an empty catalog, so Pending here would be unresolvable.)
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
                // Gate every add-in timer for the close cascade. "Save Models" and
                // "Mart Offline" are message-pumping modals, so an ungated tick does
                // SCAPI work inside erwin's pump - the same reentrancy class the alter
                // wizard and the Mart commit are already gated for (found 2026-07-26).
                using (Services.AlterWizardGate.Enter("model close cascade"))
                {
                    modelClosed = await System.Threading.Tasks.Task
                        .Run(() => MartMartAutomation.CloseActiveModelFast(_log))
                        .ConfigureAwait(true);
                }
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

            // Lock-support gate BEFORE the Mart save. An UNSET PROMOTION_LOCK_TYPE
            // is not a lock request: it resolves to UNLOCKED here (the deferred
            // lock phase, D1) and the send proceeds. Only an admin who
            // EXPLICITLY configured a lock this build cannot apply yet is
            // rejected - loudly, and before any Mart version is minted.
            PromotionLockType lockType;
            bool lockExplicit;
            var lockService = new UnlockedOnlyMartVersionLockService();
            try
            {
                (lockType, lockExplicit) = PromotionFlow.ResolveLockDecision(lockService);
                if (lockExplicit && !lockService.Supports(lockType))
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
                    route.Relation, LookupPromotionApprovers());

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
                // Gate every add-in timer for the close cascade. "Save Models" and
                // "Mart Offline" are message-pumping modals, so an ungated tick does
                // SCAPI work inside erwin's pump - the same reentrancy class the alter
                // wizard and the Mart commit are already gated for (found 2026-07-26).
                using (Services.AlterWizardGate.Enter("model close cascade"))
                {
                    modelClosed = await System.Threading.Tasks.Task
                        .Run(() => MartMartAutomation.CloseActiveModelFast(_log))
                        .ConfigureAwait(true);
                }
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
            // The blocking-rule verdict is NOT a transient failure and must survive every retry
            // path: the model still violates the rule that refused it, and nothing the user can
            // do in THIS window changes that (fixing it means Cancel, fix, regenerate). Restoring
            // the button here would hand them a green button that the click-time check refuses.
            _btnSend.Enabled = !_blockedByRules;
            _btnCancel.Enabled = true;
            _btnCopy.Enabled = true;
            _txtNote.Enabled = true;
            if (_cmbPromoteTarget != null) _cmbPromoteTarget.Enabled = true;
            if (_cmbPromoteSource != null) _cmbPromoteSource.Enabled = true;
        }
    }
}
