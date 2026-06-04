namespace EliteSoft.Erwin.AddIn
{
    partial class ModelConfigForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            // === Design System Colors ===
            var clrPrimary = System.Drawing.Color.FromArgb(0, 102, 204);
            var clrPrimaryDark = System.Drawing.Color.FromArgb(0, 76, 153);
            var clrSuccess = System.Drawing.Color.FromArgb(0, 138, 62);
            var clrError = System.Drawing.Color.FromArgb(204, 0, 0);
            var clrTextPrimary = System.Drawing.Color.FromArgb(26, 26, 26);
            var clrTextSecondary = System.Drawing.Color.FromArgb(102, 102, 102);
            var clrTextDisabled = System.Drawing.Color.FromArgb(153, 153, 153);
            var clrBorder = System.Drawing.Color.FromArgb(208, 208, 208);
            var clrSurface = System.Drawing.Color.FromArgb(245, 247, 250);
            var clrSurfaceAlt = System.Drawing.Color.FromArgb(238, 241, 245);

            var fontBody = new System.Drawing.Font("Segoe UI", 9.5F);
            var fontBodyBold = new System.Drawing.Font("Segoe UI", 9.5F, System.Drawing.FontStyle.Bold);
            var fontCaption = new System.Drawing.Font("Segoe UI", 8.5F);

            // === Control Instantiation ===
            this.tabControl = new System.Windows.Forms.TabControl();
            this.tabGeneral = new System.Windows.Forms.TabPage();
            this.tabValidation = new System.Windows.Forms.TabPage();
            this.tabTableProcesses = new System.Windows.Forms.TabPage();
            this.tabDdlGeneration = new System.Windows.Forms.TabPage();
            this.tabAlterCompare = new System.Windows.Forms.TabPage();
            // Alter Compare tab — inline UI (Phase 3.G)
            this.lblAlterActiveInfo = new System.Windows.Forms.Label();
            this.lblAlterDialectInfo = new System.Windows.Forms.Label();
            this.lblAlterTargetVersionLabel = new System.Windows.Forms.Label();
            this.cmbAlterTargetVersion = new System.Windows.Forms.ComboBox();
            this.btnAlterCompare = new System.Windows.Forms.Button();
            this.progressAlterCompare = new System.Windows.Forms.ProgressBar();
            this.lblAlterCompareStatus = new System.Windows.Forms.Label();
            this.lvAlterChanges = new System.Windows.Forms.ListView();
            this.txtAlterSql = new System.Windows.Forms.TextBox();
            this.btnCopyAlterSql = new System.Windows.Forms.Button();
            this.btnSaveAlterSql = new System.Windows.Forms.Button();

            // Model tab
            this.grpModel = new System.Windows.Forms.GroupBox();
            this.lblActiveModel = new System.Windows.Forms.Label();
            this.lblConnectionStatus = new System.Windows.Forms.Label();
            this.lblModelName = new System.Windows.Forms.Label();
            this.lblPlatformStatus = new System.Windows.Forms.Label();

            // Configuration tab + grpConfig removed: the Database/Schema/Name
            // editor was retired (it duplicated erwin's own model property
            // editors). Glossary status moved into the General tab as a card
            // section (lblGlossaryStatus + lblLastRefreshValue still live on
            // this form; they're created and positioned at runtime by
            // InitializeGeneralTab so no design-time declarations are needed).

            // Validation tab — nested tabs removed, single list + filter
            this.listValidationResults = new System.Windows.Forms.ListView();
            this.btnValidateAll = new System.Windows.Forms.Button();
            this.lblValidationStatus = new System.Windows.Forms.Label();
            this.cmbValidationFilter = new System.Windows.Forms.ComboBox();
            this.chkErrorsOnly = new System.Windows.Forms.CheckBox();
            this.lblFilterLabel = new System.Windows.Forms.Label();

            // Table Processes tab
            this.grpTableProcesses = new System.Windows.Forms.GroupBox();
            this.lblSelectTable = new System.Windows.Forms.Label();
            this.cmbTables = new System.Windows.Forms.ComboBox();
            this.chkArchiveTable = new System.Windows.Forms.CheckBox();
            this.chkIsolatedTable = new System.Windows.Forms.CheckBox();
            this.btnCreateTables = new System.Windows.Forms.Button();
            this.lblTableProcessStatus = new System.Windows.Forms.Label();
            this.lblArchiveSuffix = new System.Windows.Forms.Label();
            this.lblIsolatedSuffix = new System.Windows.Forms.Label();

            // Debug tab

            // Bottom
            this.pnlStatusBar = new System.Windows.Forms.Panel();
            this.pnlStatusSep = new System.Windows.Forms.Panel();
            this.btnClose = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();

            this.tabControl.SuspendLayout();
            this.tabGeneral.SuspendLayout();
            this.tabValidation.SuspendLayout();
            this.tabTableProcesses.SuspendLayout();
            this.grpTableProcesses.SuspendLayout();
            this.grpModel.SuspendLayout();
            this.pnlStatusBar.SuspendLayout();
            this.SuspendLayout();

            // ================================================================
            // tabControl
            // ================================================================
            this.tabControl.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.tabControl.Controls.Add(this.tabGeneral);
            this.tabControl.Controls.Add(this.tabValidation);
            this.tabControl.Controls.Add(this.tabTableProcesses);
            this.tabControl.Controls.Add(this.tabDdlGeneration);
            this.tabControl.Controls.Add(this.tabAlterCompare);
            this.tabControl.Location = new System.Drawing.Point(16, 16);
            this.tabControl.Name = "tabControl";
            this.tabControl.SelectedIndex = 0;
            this.tabControl.Size = new System.Drawing.Size(948, 580);
            this.tabControl.TabIndex = 0;
            this.tabControl.Font = fontBody;
            this.tabControl.SelectedIndexChanged += new System.EventHandler(this.tabControl_SelectedIndexChanged);

            // ================================================================
            // TAB 0: GENERAL
            // ================================================================
            this.tabGeneral.Location = new System.Drawing.Point(4, 26);
            this.tabGeneral.Name = "tabGeneral";
            this.tabGeneral.Padding = new System.Windows.Forms.Padding(20);
            this.tabGeneral.Size = new System.Drawing.Size(940, 550);
            this.tabGeneral.TabIndex = 10;
            this.tabGeneral.Text = "General";
            this.tabGeneral.UseVisualStyleBackColor = true;

            // --- General tab content (built at runtime in InitializeGeneralTab) ---
            // grpModel was the legacy "Active Model" GroupBox; it is no longer
            // added to tabGeneral - the runtime builder lifts its child labels
            // (lblModelName / lblActiveModel / lblConnectionStatus /
            // lblPlatformStatus) out of grpModel and re-hosts them on a section
            // card that matches the Repository / Glossary card chrome. We keep
            // grpModel as a zombie holder so existing references don't break,
            // but it isn't displayed.
            this.grpModel.Controls.Add(this.lblActiveModel);
            this.grpModel.Controls.Add(this.lblConnectionStatus);
            this.grpModel.Controls.Add(this.lblModelName);
            this.grpModel.Controls.Add(this.lblPlatformStatus);
            this.grpModel.Name = "grpModel";
            this.grpModel.Visible = false;

            // lblModelName
            this.lblModelName.AutoSize = true;
            this.lblModelName.Location = new System.Drawing.Point(16, 32);
            this.lblModelName.Name = "lblModelName";
            this.lblModelName.TabIndex = 0;
            this.lblModelName.Text = "Model:";
            this.lblModelName.ForeColor = clrTextPrimary;

            // lblActiveModel
            this.lblActiveModel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.lblActiveModel.Location = new System.Drawing.Point(80, 28);
            this.lblActiveModel.Name = "lblActiveModel";
            this.lblActiveModel.Size = new System.Drawing.Size(400, 25);
            this.lblActiveModel.TabIndex = 1;
            this.lblActiveModel.Text = "(Loading...)";
            this.lblActiveModel.Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
            this.lblActiveModel.ForeColor = clrTextPrimary;
            this.lblActiveModel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

            // lblConnectionStatus
            this.lblConnectionStatus.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.lblConnectionStatus.Location = new System.Drawing.Point(500, 32);
            this.lblConnectionStatus.Name = "lblConnectionStatus";
            this.lblConnectionStatus.Size = new System.Drawing.Size(320, 20);
            this.lblConnectionStatus.TabIndex = 2;
            this.lblConnectionStatus.Text = "(Loading...)";
            this.lblConnectionStatus.ForeColor = clrTextDisabled;
            this.lblConnectionStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

            // lblPlatformStatus — inside grpModel
            this.lblPlatformStatus.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.lblPlatformStatus.Location = new System.Drawing.Point(16, 62);
            this.lblPlatformStatus.Name = "lblPlatformStatus";
            this.lblPlatformStatus.Size = new System.Drawing.Size(800, 20);
            this.lblPlatformStatus.TabIndex = 3;
            this.lblPlatformStatus.Text = "";
            this.lblPlatformStatus.ForeColor = clrTextSecondary;
            this.lblPlatformStatus.Font = fontCaption;

            // ================================================================
            // (Configuration + Glossary tabs were retired - Configuration was a
            // duplicate of erwin's own model property editors, and the Glossary
            // tab's read-only status fields are now rendered as a section on the
            // General tab via InitializeGeneralTab. The Test Connection / Reload
            // Glossary buttons and their handlers were removed; the glossary
            // reloads automatically when the model loads or when DatabaseService
            // reconnects.)
            // ================================================================

            // ================================================================
            // TAB 1: VALIDATION
            // ================================================================
            // Toolbar panel (top strip)
            var pnlValidationToolbar = new System.Windows.Forms.Panel();
            pnlValidationToolbar.Dock = System.Windows.Forms.DockStyle.Top;
            pnlValidationToolbar.Height = 44;
            pnlValidationToolbar.Padding = new System.Windows.Forms.Padding(12, 8, 12, 4);

            // Validate All button
            this.btnValidateAll.Location = new System.Drawing.Point(12, 8);
            this.btnValidateAll.Size = new System.Drawing.Size(110, 30);
            this.btnValidateAll.Text = "Validate All";
            this.btnValidateAll.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnValidateAll.BackColor = clrPrimary;
            this.btnValidateAll.ForeColor = System.Drawing.Color.White;
            this.btnValidateAll.Font = fontBodyBold;
            this.btnValidateAll.FlatAppearance.BorderSize = 0;
            this.btnValidateAll.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnValidateAll.Click += new System.EventHandler(this.BtnValidateAll_Click);

            // Object Type label
            this.lblFilterLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.lblFilterLabel.AutoSize = true;
            this.lblFilterLabel.Text = "Object Type:";
            this.lblFilterLabel.ForeColor = clrTextSecondary;
            this.lblFilterLabel.Font = fontBody;

            // Object Type combo
            this.cmbValidationFilter.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.cmbValidationFilter.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbValidationFilter.Size = new System.Drawing.Size(95, 25);
            this.cmbValidationFilter.Font = fontBody;
            this.cmbValidationFilter.Items.AddRange(new object[] { "All", "Table", "Column", "Index", "View", "Model", "Subject Area" });
            this.cmbValidationFilter.SelectedIndex = 0;
            this.cmbValidationFilter.SelectedIndexChanged += new System.EventHandler(this.CmbValidationFilter_SelectedIndexChanged);

            // Errors Only checkbox
            this.chkErrorsOnly.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.chkErrorsOnly.AutoSize = true;
            this.chkErrorsOnly.Text = "Errors Only";
            this.chkErrorsOnly.Font = fontBody;
            this.chkErrorsOnly.ForeColor = System.Drawing.Color.FromArgb(180, 0, 0);
            this.chkErrorsOnly.CheckedChanged += new System.EventHandler(this.ChkErrorsOnly_CheckedChanged);

            // Position controls in toolbar using right-anchored flow
            // Layout: [Validate All] ............. [x Errors Only]  [Object Type: [combo]]
            pnlValidationToolbar.Controls.Add(this.btnValidateAll);
            pnlValidationToolbar.Controls.Add(this.chkErrorsOnly);
            pnlValidationToolbar.Controls.Add(this.lblFilterLabel);
            pnlValidationToolbar.Controls.Add(this.cmbValidationFilter);

            // Manual right-align positioning via Resize event
            pnlValidationToolbar.Resize += (s, ev) =>
            {
                int right = pnlValidationToolbar.ClientSize.Width - 12;
                this.cmbValidationFilter.Location = new System.Drawing.Point(right - this.cmbValidationFilter.Width, 8);
                this.lblFilterLabel.Location = new System.Drawing.Point(this.cmbValidationFilter.Left - this.lblFilterLabel.Width - 6, 12);
                this.chkErrorsOnly.Location = new System.Drawing.Point(this.lblFilterLabel.Left - this.chkErrorsOnly.Width - 18, 12);
            };

            // Results ListView - GridLines turned off because the per-row
            // grid stripes felt dated next to the modern card chrome on
            // other tabs. The severity icon (added at runtime via SmallImageList
            // in InitializeValidationUI) gives the row a clear visual anchor
            // without needing the cell borders to separate columns.
            this.listValidationResults.Dock = System.Windows.Forms.DockStyle.Fill;
            this.listValidationResults.FullRowSelect = true;
            this.listValidationResults.GridLines = false;
            this.listValidationResults.UseCompatibleStateImageBehavior = false;
            this.listValidationResults.View = System.Windows.Forms.View.Details;
            this.listValidationResults.Font = fontBody;

            // Status bar (bottom strip)
            this.lblValidationStatus.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.lblValidationStatus.Height = 24;
            this.lblValidationStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.lblValidationStatus.Padding = new System.Windows.Forms.Padding(12, 0, 0, 0);
            this.lblValidationStatus.Text = "Click 'Validate All' to run all checks.";
            this.lblValidationStatus.BackColor = System.Drawing.Color.FromArgb(245, 245, 245);

            // Assemble tab — Fill first, then Top/Bottom (WinForms Z-order)
            this.tabValidation.Controls.Add(this.listValidationResults);
            this.tabValidation.Controls.Add(pnlValidationToolbar);
            this.tabValidation.Controls.Add(this.lblValidationStatus);
            this.tabValidation.Location = new System.Drawing.Point(4, 26);
            this.tabValidation.Name = "tabValidation";
            this.tabValidation.Padding = new System.Windows.Forms.Padding(0);
            this.tabValidation.Size = new System.Drawing.Size(860, 460);
            this.tabValidation.TabIndex = 3;
            this.tabValidation.Text = "Validation";
            this.tabValidation.UseVisualStyleBackColor = true;
            this.lblValidationStatus.ForeColor = clrTextSecondary;
            this.lblValidationStatus.Font = fontCaption;

            // ================================================================
            // TAB 5: TABLE PROCESSES
            // ================================================================
            this.tabTableProcesses.Controls.Add(this.grpTableProcesses);
            this.tabTableProcesses.Location = new System.Drawing.Point(4, 26);
            this.tabTableProcesses.Name = "tabTableProcesses";
            this.tabTableProcesses.Padding = new System.Windows.Forms.Padding(12);
            this.tabTableProcesses.Size = new System.Drawing.Size(860, 460);
            this.tabTableProcesses.TabIndex = 5;
            this.tabTableProcesses.Text = "Table Processes";
            this.tabTableProcesses.UseVisualStyleBackColor = true;

            this.grpTableProcesses.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpTableProcesses.Controls.Add(this.lblSelectTable);
            this.grpTableProcesses.Controls.Add(this.cmbTables);
            this.grpTableProcesses.Controls.Add(this.chkArchiveTable);
            this.grpTableProcesses.Controls.Add(this.lblArchiveSuffix);
            this.grpTableProcesses.Controls.Add(this.chkIsolatedTable);
            this.grpTableProcesses.Controls.Add(this.lblIsolatedSuffix);
            this.grpTableProcesses.Controls.Add(this.btnCreateTables);
            this.grpTableProcesses.Controls.Add(this.lblTableProcessStatus);
            this.grpTableProcesses.Location = new System.Drawing.Point(12, 12);
            this.grpTableProcesses.Name = "grpTableProcesses";
            this.grpTableProcesses.Size = new System.Drawing.Size(833, 195);
            this.grpTableProcesses.TabIndex = 0;
            this.grpTableProcesses.TabStop = false;
            this.grpTableProcesses.Text = "Create Table Copies";

            this.lblSelectTable.AutoSize = true;
            this.lblSelectTable.Location = new System.Drawing.Point(16, 32);
            this.lblSelectTable.Text = "Source Table:";
            this.lblSelectTable.ForeColor = clrTextPrimary;

            this.cmbTables.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.cmbTables.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbTables.Location = new System.Drawing.Point(110, 28);
            this.cmbTables.Size = new System.Drawing.Size(400, 25);
            this.cmbTables.TabIndex = 1;

            this.chkArchiveTable.AutoSize = true;
            this.chkArchiveTable.Location = new System.Drawing.Point(110, 68);
            this.chkArchiveTable.Text = "Archive Table";
            this.chkArchiveTable.ForeColor = clrTextPrimary;
            this.chkArchiveTable.UseVisualStyleBackColor = true;

            this.lblArchiveSuffix.AutoSize = true;
            this.lblArchiveSuffix.Location = new System.Drawing.Point(230, 69);
            this.lblArchiveSuffix.Text = "(adds _ARCHIVE suffix)";
            this.lblArchiveSuffix.ForeColor = clrTextSecondary;
            this.lblArchiveSuffix.Font = fontCaption;

            this.chkIsolatedTable.AutoSize = true;
            this.chkIsolatedTable.Location = new System.Drawing.Point(110, 96);
            this.chkIsolatedTable.Text = "Isolated Table";
            this.chkIsolatedTable.ForeColor = clrTextPrimary;
            this.chkIsolatedTable.UseVisualStyleBackColor = true;

            this.lblIsolatedSuffix.AutoSize = true;
            this.lblIsolatedSuffix.Location = new System.Drawing.Point(230, 97);
            this.lblIsolatedSuffix.Text = "(adds _ISOLATED suffix)";
            this.lblIsolatedSuffix.ForeColor = clrTextSecondary;
            this.lblIsolatedSuffix.Font = fontCaption;

            // btnCreateTables — Primary style, right-aligned
            this.btnCreateTables.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnCreateTables.Location = new System.Drawing.Point(710, 130);
            this.btnCreateTables.Size = new System.Drawing.Size(110, 32);
            this.btnCreateTables.Text = "Create Tables";
            this.btnCreateTables.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnCreateTables.BackColor = clrPrimary;
            this.btnCreateTables.ForeColor = System.Drawing.Color.White;
            this.btnCreateTables.Font = fontBodyBold;
            this.btnCreateTables.FlatAppearance.BorderSize = 0;
            this.btnCreateTables.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnCreateTables.Click += new System.EventHandler(this.BtnCreateTables_Click);

            this.lblTableProcessStatus.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.lblTableProcessStatus.Location = new System.Drawing.Point(16, 170);
            this.lblTableProcessStatus.Size = new System.Drawing.Size(800, 20);
            this.lblTableProcessStatus.Text = "";
            this.lblTableProcessStatus.ForeColor = clrSuccess;
            this.lblTableProcessStatus.Font = fontCaption;

            // ================================================================
            // TAB: ALTER COMPARE (Phase 3.G inline UI)
            //   Active PU (dirty allowed) -> selected Mart version -> ALTER SQL.
            //   Pipeline: VersionCompareService (Live + Worker model providers,
            //   CC-aligned correlator). No popup; everything renders in the tab.
            // ================================================================
            this.tabAlterCompare.Location = new System.Drawing.Point(4, 26);
            this.tabAlterCompare.Name = "tabAlterCompare";
            this.tabAlterCompare.Padding = new System.Windows.Forms.Padding(20);
            this.tabAlterCompare.Size = new System.Drawing.Size(860, 460);
            this.tabAlterCompare.Text = "Alter Compare";
            this.tabAlterCompare.UseVisualStyleBackColor = true;

            // Row 1: active model summary
            this.lblAlterActiveInfo.AutoSize = false;
            this.lblAlterActiveInfo.Location = new System.Drawing.Point(20, 18);
            this.lblAlterActiveInfo.Size = new System.Drawing.Size(820, 20);
            this.lblAlterActiveInfo.Font = fontBodyBold;
            this.lblAlterActiveInfo.Text = "Active: (no model loaded)";
            this.lblAlterActiveInfo.ForeColor = clrTextPrimary;
            this.tabAlterCompare.Controls.Add(this.lblAlterActiveInfo);

            // Row 2: dialect / target server
            this.lblAlterDialectInfo.AutoSize = false;
            this.lblAlterDialectInfo.Location = new System.Drawing.Point(20, 40);
            this.lblAlterDialectInfo.Size = new System.Drawing.Size(820, 20);
            this.lblAlterDialectInfo.Font = fontBody;
            this.lblAlterDialectInfo.Text = "Dialect: -";
            this.lblAlterDialectInfo.ForeColor = clrTextSecondary;
            this.tabAlterCompare.Controls.Add(this.lblAlterDialectInfo);

            // Row 3: target version combo + compare button + progress
            this.lblAlterTargetVersionLabel.AutoSize = false;
            this.lblAlterTargetVersionLabel.Location = new System.Drawing.Point(20, 75);
            this.lblAlterTargetVersionLabel.Size = new System.Drawing.Size(150, 24);
            this.lblAlterTargetVersionLabel.Font = fontBody;
            this.lblAlterTargetVersionLabel.Text = "Target Mart version:";
            this.lblAlterTargetVersionLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.tabAlterCompare.Controls.Add(this.lblAlterTargetVersionLabel);

            this.cmbAlterTargetVersion.Location = new System.Drawing.Point(180, 75);
            this.cmbAlterTargetVersion.Name = "cmbAlterTargetVersion";
            this.cmbAlterTargetVersion.Size = new System.Drawing.Size(280, 24);
            this.cmbAlterTargetVersion.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbAlterTargetVersion.Font = fontBody;
            this.tabAlterCompare.Controls.Add(this.cmbAlterTargetVersion);

            this.btnAlterCompare.Location = new System.Drawing.Point(470, 73);
            this.btnAlterCompare.Name = "btnAlterCompare";
            this.btnAlterCompare.Size = new System.Drawing.Size(120, 28);
            this.btnAlterCompare.Text = "Compare";
            this.btnAlterCompare.Font = fontBodyBold;
            this.btnAlterCompare.UseVisualStyleBackColor = true;
            this.btnAlterCompare.Enabled = false;
            this.btnAlterCompare.Click += new System.EventHandler(this.btnAlterCompare_Click);
            this.tabAlterCompare.Controls.Add(this.btnAlterCompare);

            this.progressAlterCompare.Location = new System.Drawing.Point(600, 77);
            this.progressAlterCompare.Name = "progressAlterCompare";
            this.progressAlterCompare.Size = new System.Drawing.Size(240, 18);
            this.progressAlterCompare.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            this.progressAlterCompare.MarqueeAnimationSpeed = 0;
            this.progressAlterCompare.Visible = false;
            this.tabAlterCompare.Controls.Add(this.progressAlterCompare);

            // Row 4: status text
            this.lblAlterCompareStatus.AutoSize = false;
            this.lblAlterCompareStatus.Location = new System.Drawing.Point(20, 108);
            this.lblAlterCompareStatus.Size = new System.Drawing.Size(820, 18);
            this.lblAlterCompareStatus.Font = fontCaption;
            this.lblAlterCompareStatus.Text = "Open a model to begin.";
            this.lblAlterCompareStatus.ForeColor = clrTextSecondary;
            this.tabAlterCompare.Controls.Add(this.lblAlterCompareStatus);

            // Row 5: changes list
            this.lvAlterChanges.Location = new System.Drawing.Point(20, 132);
            this.lvAlterChanges.Name = "lvAlterChanges";
            this.lvAlterChanges.Size = new System.Drawing.Size(820, 130);
            this.lvAlterChanges.View = System.Windows.Forms.View.Details;
            this.lvAlterChanges.FullRowSelect = true;
            this.lvAlterChanges.GridLines = true;
            this.lvAlterChanges.HideSelection = false;
            this.lvAlterChanges.Font = fontBody;
            this.lvAlterChanges.Columns.Add("Change", 180);
            this.lvAlterChanges.Columns.Add("Class", 100);
            this.lvAlterChanges.Columns.Add("Name", 200);
            this.lvAlterChanges.Columns.Add("Detail", 320);
            this.lvAlterChanges.Anchor = System.Windows.Forms.AnchorStyles.Top
                | System.Windows.Forms.AnchorStyles.Left
                | System.Windows.Forms.AnchorStyles.Right;
            this.tabAlterCompare.Controls.Add(this.lvAlterChanges);

            // Row 6: alter SQL output
            // Reverted from RichTextBox to TextBox after a 2026-05-09 erwin
            // crash (coreclr.dll AV at 0x36852a) reproduced when switching
            // into the Alter Compare tab. RefreshAlterCompareTab calls
            // txtAlterSql.Clear() during tab activation; on a RichTextBox
            // that triggers a UIA TextChanged event that erwin's host listener
            // surfaces unsafely (same family as the Debug Log AppendText AV
            // documented in memory/reference_winforms_shown_after_dispose).
            // SQL syntax highlighting via RichTextBox stays in place for the
            // DDL Generation popup (Forms.DdlApprovalDialog) where there has
            // been no observed AV.
            this.txtAlterSql.Location = new System.Drawing.Point(20, 270);
            this.txtAlterSql.Name = "txtAlterSql";
            this.txtAlterSql.Size = new System.Drawing.Size(820, 145);
            this.txtAlterSql.Multiline = true;
            this.txtAlterSql.ReadOnly = true;
            this.txtAlterSql.Font = new System.Drawing.Font("Consolas", 9.5F);
            this.txtAlterSql.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            this.txtAlterSql.WordWrap = false;
            this.txtAlterSql.Anchor = System.Windows.Forms.AnchorStyles.Top
                | System.Windows.Forms.AnchorStyles.Left
                | System.Windows.Forms.AnchorStyles.Right
                | System.Windows.Forms.AnchorStyles.Bottom;
            this.tabAlterCompare.Controls.Add(this.txtAlterSql);

            // Row 7: copy + save buttons (bottom right)
            this.btnCopyAlterSql.Location = new System.Drawing.Point(550, 425);
            this.btnCopyAlterSql.Name = "btnCopyAlterSql";
            this.btnCopyAlterSql.Size = new System.Drawing.Size(140, 28);
            this.btnCopyAlterSql.Text = "Copy SQL";
            this.btnCopyAlterSql.Font = fontBodyBold;
            this.btnCopyAlterSql.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            this.btnCopyAlterSql.Enabled = false;
            this.btnCopyAlterSql.Click += new System.EventHandler(this.btnCopyAlterSql_Click);
            this.tabAlterCompare.Controls.Add(this.btnCopyAlterSql);

            this.btnSaveAlterSql.Location = new System.Drawing.Point(700, 425);
            this.btnSaveAlterSql.Name = "btnSaveAlterSql";
            this.btnSaveAlterSql.Size = new System.Drawing.Size(140, 28);
            this.btnSaveAlterSql.Text = "Save SQL...";
            this.btnSaveAlterSql.Font = fontBodyBold;
            this.btnSaveAlterSql.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            this.btnSaveAlterSql.Enabled = false;
            this.btnSaveAlterSql.Click += new System.EventHandler(this.btnSaveAlterSql_Click);
            this.tabAlterCompare.Controls.Add(this.btnSaveAlterSql);

            // ================================================================
            // TAB 6: DEBUG LOG
            // ================================================================

            // ===== DDL Generation tab — redesigned =====
            this.tabDdlGeneration.Padding = new System.Windows.Forms.Padding(12);
            this.tabDdlGeneration.Size = new System.Drawing.Size(860, 460);
            this.tabDdlGeneration.Text = "DDL Generation";
            this.tabDdlGeneration.UseVisualStyleBackColor = true;

            // ----- Group: Source (left model = active model) -----
            this.grpDdlSource = new System.Windows.Forms.GroupBox();
            this.grpDdlSource.Location = new System.Drawing.Point(12, 12);
            this.grpDdlSource.Size = new System.Drawing.Size(380, 88);
            this.grpDdlSource.Text = "Source (Left)";
            this.grpDdlSource.Font = fontCaption;
            this.grpDdlSource.ForeColor = clrTextSecondary;
            this.tabDdlGeneration.Controls.Add(this.grpDdlSource);

            // Source (Left) display: plain text showing the open model + its
            // "(with last changes)" suffix. The Source is always the currently
            // open model (Faz 3 / Complete Compare was explored 2026-05-29 and
            // found redundant - the user only ever wants to compare the LIVE
            // model against a chosen older version on the RIGHT), so the prior
            // single-entry ComboBox (cmbLeftModel) was pointless UX and was
            // DELETED 2026-05-30 along with its handler chain
            // (OnLeftModelChanged / LeftIsActiveModel / ParseLeftVersion).
            // RebuildRightCombo is now called DIRECTLY from PopulateVersionCombos
            // rather than via a cmbLeftModel.SelectedIndex = 0 -> SIC cascade.
            this.lblOpenedModel = new System.Windows.Forms.Label();
            this.lblOpenedModel.Location = new System.Drawing.Point(12, 22);
            this.lblOpenedModel.Size = new System.Drawing.Size(360, 22);
            this.lblOpenedModel.Text = "(no model loaded)";
            this.lblOpenedModel.Font = fontBodyBold;
            this.lblOpenedModel.ForeColor = clrTextPrimary;
            this.lblOpenedModel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.lblOpenedModel.Visible = true;
            this.grpDdlSource.Controls.Add(this.lblOpenedModel);

            // Restored 2026-05-07 per user request. Triggers erwin's native
            // "Review" toolbar button via Win32 (Win32Helper.InvokeToolbarButton)
            // so the user can reach the built-in Mart compare-with-last-saved
            // dialog directly from the add-in. The smart-routing Generate DDL
            // button does NOT make this obsolete - native Review opens erwin's
            // own diff UI rather than emitting alter DDL.
            this.btnMartReview = new System.Windows.Forms.Button();
            this.btnMartReview.Location = new System.Drawing.Point(12, 48);
            this.btnMartReview.Size = new System.Drawing.Size(110, 24);
            this.btnMartReview.Text = "Review";
            this.btnMartReview.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnMartReview.BackColor = System.Drawing.Color.White;
            this.btnMartReview.ForeColor = clrTextPrimary;
            this.btnMartReview.FlatAppearance.BorderColor = clrBorder;
            this.btnMartReview.Font = fontCaption;
            this.btnMartReview.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnMartReview.Click += new System.EventHandler(this.BtnMartReview_Click);
            this.grpDdlSource.Controls.Add(this.btnMartReview);

            // ----- Group: Target (right side = Mart version OR DB) -----
            this.grpDdlTarget = new System.Windows.Forms.GroupBox();
            this.grpDdlTarget.Location = new System.Drawing.Point(400, 12);
            this.grpDdlTarget.Size = new System.Drawing.Size(444, 88);
            this.grpDdlTarget.Text = "Target (Right)";
            this.grpDdlTarget.Font = fontCaption;
            this.grpDdlTarget.ForeColor = clrTextSecondary;
            this.tabDdlGeneration.Controls.Add(this.grpDdlTarget);

            this.rbFromMart = new System.Windows.Forms.RadioButton();
            this.rbFromMart.Location = new System.Drawing.Point(12, 22);
            this.rbFromMart.Size = new System.Drawing.Size(95, 20);
            this.rbFromMart.Text = "From Mart";
            this.rbFromMart.Font = fontCaption;
            this.rbFromMart.Checked = true;
            this.rbFromMart.CheckedChanged += (s, ev) => OnRightSourceChanged();
            this.grpDdlTarget.Controls.Add(this.rbFromMart);

            this.cmbRightModel = new System.Windows.Forms.ComboBox();
            this.cmbRightModel.Location = new System.Drawing.Point(110, 20);
            this.cmbRightModel.Size = new System.Drawing.Size(322, 24);
            this.cmbRightModel.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbRightModel.Font = fontCaption;
            // Enabled 2026-05-28: multi-version compare. Contents are populated
            // by RebuildRightCombo (called from PopulateVersionCombos at init,
            // after the active Mart version is known). OnRightSourceChanged
            // toggles Enabled based on the From-Mart vs From-DB radio.
            this.cmbRightModel.Enabled = true;
            this.grpDdlTarget.Controls.Add(this.cmbRightModel);

            this.rbFromDB = new System.Windows.Forms.RadioButton();
            this.rbFromDB.Location = new System.Drawing.Point(12, 49);
            this.rbFromDB.Size = new System.Drawing.Size(80, 20);
            this.rbFromDB.Text = "From DB";
            this.rbFromDB.Font = fontCaption;
            this.rbFromDB.CheckedChanged += (s, ev) => OnRightSourceChanged();
            this.grpDdlTarget.Controls.Add(this.rbFromDB);

            this.btnConfigureDB = new System.Windows.Forms.Button();
            this.btnConfigureDB.Location = new System.Drawing.Point(95, 47);
            this.btnConfigureDB.Size = new System.Drawing.Size(95, 24);
            this.btnConfigureDB.Text = "Configure...";
            this.btnConfigureDB.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnConfigureDB.BackColor = System.Drawing.Color.White;
            this.btnConfigureDB.FlatAppearance.BorderColor = clrBorder;
            this.btnConfigureDB.Font = fontCaption;
            this.btnConfigureDB.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnConfigureDB.Visible = false;
            this.btnConfigureDB.Click += new System.EventHandler(this.BtnConfigureDB_Click);
            this.grpDdlTarget.Controls.Add(this.btnConfigureDB);

            // Note (2026-05-30): the "Select Tables..." button + the
            // "(N of M selected)" label were DELETED. The From-DB pipeline
            // now always reverse-engineers EVERY entity in the active model
            // (CollectModelTablePhysicalNames). When the user wants to scope
            // the alter script, they use the "Only Selected Objects" checkbox
            // below + a diagram selection; the DDL is post-filtered by
            // Physical_Name match.

            // ----- Group: Options -----
            this.grpDdlOptions = new System.Windows.Forms.GroupBox();
            this.grpDdlOptions.Location = new System.Drawing.Point(12, 106);
            this.grpDdlOptions.Size = new System.Drawing.Size(832, 56);
            this.grpDdlOptions.Text = "Options";
            this.grpDdlOptions.Font = fontCaption;
            this.grpDdlOptions.ForeColor = clrTextSecondary;
            this.tabDdlGeneration.Controls.Add(this.grpDdlOptions);

            this.chkFilterObjects = new System.Windows.Forms.CheckBox();
            this.chkFilterObjects.Location = new System.Drawing.Point(12, 24);
            this.chkFilterObjects.Size = new System.Drawing.Size(190, 22);
            this.chkFilterObjects.Text = "Only Selected Objects";
            this.chkFilterObjects.Font = fontCaption;
            this.grpDdlOptions.Controls.Add(this.chkFilterObjects);

            // Removed 2026-05-27: 'FE Option XML' label/textbox/Browse button.
            // The textbox value was never read by the production Generate DDL
            // pipelines (RunFromDbDdlPipelineAsync / NativeBridgeService.
            // GenerateAlterDdl / MartMartAutomation cross-version path all
            // ignored it). FE option auto-apply now resolves the active
            // config's XML_OPTION TYPE='DDL' row at Generate DDL click time
            // (see BtnAlterWizardProd_Click). No manual override surface.

            // ----- Action row: Generate DDL + status -----
            // Copy button + inline rtbDDLOutput viewer removed 2026-05-16.
            // Successful DDL now opens Forms.DdlApprovalDialog which carries
            // its own Copy / Cancel / Sent to Approve buttons. Errors surface
            // via AddinMessageDialog; the status label below stays as the
            // one-line summary.
            this.btnAlterWizardProd = new System.Windows.Forms.Button();
            this.btnAlterWizardProd.Location = new System.Drawing.Point(12, 172);
            this.btnAlterWizardProd.Size = new System.Drawing.Size(175, 32);
            this.btnAlterWizardProd.Text = "Generate DDL";
            this.btnAlterWizardProd.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnAlterWizardProd.BackColor = clrSuccess;
            this.btnAlterWizardProd.ForeColor = System.Drawing.Color.White;
            this.btnAlterWizardProd.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
            this.btnAlterWizardProd.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnAlterWizardProd.Click += new System.EventHandler(this.BtnAlterWizardProd_Click);
            this.tabDdlGeneration.Controls.Add(this.btnAlterWizardProd);

#if !PACKAGED
            // Dev-only sibling button: same pipeline as Generate DDL, but
            // runs with DebugMode.Enabled=true so child wizards / dialogs
            // stay visible and the pipeline pauses 5 s at every phase
            // transition. Hidden in packaged builds via #if !PACKAGED so
            // shipping users never see it.
            this.btnAlterWizardProdDebug = new System.Windows.Forms.Button();
            this.btnAlterWizardProdDebug.Location = new System.Drawing.Point(193, 172);
            this.btnAlterWizardProdDebug.Size = new System.Drawing.Size(175, 32);
            this.btnAlterWizardProdDebug.Text = "Generate DDL (debug)";
            this.btnAlterWizardProdDebug.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnAlterWizardProdDebug.BackColor = System.Drawing.Color.FromArgb(204, 102, 0); // amber - "dev only" tint
            this.btnAlterWizardProdDebug.ForeColor = System.Drawing.Color.White;
            this.btnAlterWizardProdDebug.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
            this.btnAlterWizardProdDebug.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnAlterWizardProdDebug.Click += new System.EventHandler(this.BtnAlterWizardProdDebug_Click);
            this.tabDdlGeneration.Controls.Add(this.btnAlterWizardProdDebug);
#endif

            // RDP black-rectangle STEP-MODE toggle. Exists in ALL builds (the
            // checkpoint plumbing is always compiled); in PACKAGED it is created
            // HIDDEN and revealed via Ctrl+Shift+LeftClick on the copyright label
            // (same gesture as the Debug Log tab) so a field user (Emre) can run
            // the diagnostic without a "Step Mode" button shipping visibly. In dev
            // builds it is visible by default. Replaces the old Ctrl+Alt+S hotkey
            // (collided with erwin's Scheduler shortcut).
            this.btnStepMode = new System.Windows.Forms.Button();
            this.btnStepMode.Location = new System.Drawing.Point(12, 210);
            this.btnStepMode.Size = new System.Drawing.Size(175, 26);
            this.btnStepMode.Text = "Step Mode: OFF";
            this.btnStepMode.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnStepMode.BackColor = System.Drawing.Color.FromArgb(96, 96, 96); // gray = off
            this.btnStepMode.ForeColor = System.Drawing.Color.White;
            this.btnStepMode.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
            this.btnStepMode.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnStepMode.Click += new System.EventHandler(this.BtnStepMode_Click);
#if PACKAGED
            this.btnStepMode.Visible = false;   // revealed via Ctrl+Shift+LeftClick on the copyright label
#endif
            this.tabDdlGeneration.Controls.Add(this.btnStepMode);

            this.lblDDLStatus = new System.Windows.Forms.Label();
#if !PACKAGED
            // Debug button sits to the right of the production button; status
            // label starts after it (12 + 175 + 6 gap + 175 + 12 gap = 380).
            this.lblDDLStatus.Location = new System.Drawing.Point(380, 180);
            this.lblDDLStatus.Size = new System.Drawing.Size(465, 20);
#else
            this.lblDDLStatus.Location = new System.Drawing.Point(200, 180);
            this.lblDDLStatus.Size = new System.Drawing.Size(645, 20);
#endif
            this.lblDDLStatus.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.lblDDLStatus.AutoEllipsis = true;
            this.lblDDLStatus.Text = "";
            this.lblDDLStatus.Font = fontCaption;
            this.lblDDLStatus.ForeColor = clrTextSecondary;
            this.tabDdlGeneration.Controls.Add(this.lblDDLStatus);

            // ================================================================
            // STATUS BAR (Bottom)
            // ================================================================
            this.pnlStatusSep.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlStatusSep.Height = 1;
            this.pnlStatusSep.BackColor = clrBorder;

            this.pnlStatusBar.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlStatusBar.Height = 44;
            this.pnlStatusBar.BackColor = clrSurface;
            this.pnlStatusBar.Padding = new System.Windows.Forms.Padding(16, 6, 16, 6);
            this.pnlStatusBar.Controls.Add(this.btnClose);
            this.pnlStatusBar.Controls.Add(this.lblStatus);

            this.lblStatus.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Text = "";
            this.lblStatus.ForeColor = clrTextSecondary;
            this.lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.lblStatus.Font = fontBody;

            // btnClose — Primary style
            this.btnClose.Dock = System.Windows.Forms.DockStyle.Right;
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(100, 32);
            this.btnClose.Text = "Close";
            this.btnClose.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnClose.BackColor = clrPrimary;
            this.btnClose.ForeColor = System.Drawing.Color.White;
            this.btnClose.Font = fontBodyBold;
            this.btnClose.FlatAppearance.BorderSize = 0;
            this.btnClose.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnClose.Click += new System.EventHandler(this.BtnClose_Click);

            // ================================================================
            // FORM
            // ================================================================
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(980, 660);
            this.Controls.Add(this.tabControl);
            this.Controls.Add(this.pnlStatusSep);
            this.Controls.Add(this.pnlStatusBar);
            this.Font = fontBody;
            this.BackColor = System.Drawing.Color.White;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            this.MinimumSize = new System.Drawing.Size(800, 500);
            this.MaximizeBox = true;
            this.MinimizeBox = true;
            this.Name = "ModelConfigForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Elite Soft Erwin Model Configurator";
            this.Load += new System.EventHandler(this.ModelConfigForm_Load);

            this.tabControl.ResumeLayout(false);
            this.tabGeneral.ResumeLayout(false);
            this.tabValidation.ResumeLayout(false);
            this.tabTableProcesses.ResumeLayout(false);
            this.grpTableProcesses.ResumeLayout(false);
            this.grpTableProcesses.PerformLayout();
            this.grpModel.ResumeLayout(false);
            this.grpModel.PerformLayout();
            this.pnlStatusBar.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.TabControl tabControl;
        private System.Windows.Forms.TabPage tabGeneral;
        private System.Windows.Forms.TabPage tabValidation;
        private System.Windows.Forms.TabPage tabDdlGeneration;
        private System.Windows.Forms.TabPage tabAlterCompare;
        // Alter Compare tab — inline UI
        private System.Windows.Forms.Label lblAlterActiveInfo;
        private System.Windows.Forms.Label lblAlterDialectInfo;
        private System.Windows.Forms.Label lblAlterTargetVersionLabel;
        private System.Windows.Forms.ComboBox cmbAlterTargetVersion;
        private System.Windows.Forms.Button btnAlterCompare;
        private System.Windows.Forms.ProgressBar progressAlterCompare;
        private System.Windows.Forms.Label lblAlterCompareStatus;
        private System.Windows.Forms.ListView lvAlterChanges;
        private System.Windows.Forms.TextBox txtAlterSql;
        private System.Windows.Forms.Button btnCopyAlterSql;
        private System.Windows.Forms.Button btnSaveAlterSql;
        private System.Windows.Forms.Label lblOpenedModel;
        private System.Windows.Forms.Button btnMartReview;
        private System.Windows.Forms.Button btnAlterWizardProd;
#if !PACKAGED
        private System.Windows.Forms.Button btnAlterWizardProdDebug;
#endif
        private System.Windows.Forms.Button btnStepMode;
        private System.Windows.Forms.ComboBox cmbRightModel;
        // txtFEOptionXml + btnBrowseFEOption removed 2026-05-27 (dead UI; see
        // matching note in the designer constructor block).
        private System.Windows.Forms.RadioButton rbFromMart;
        private System.Windows.Forms.RadioButton rbFromDB;
        private System.Windows.Forms.Button btnConfigureDB;
        private System.Windows.Forms.GroupBox grpDdlSource;
        private System.Windows.Forms.GroupBox grpDdlTarget;
        private System.Windows.Forms.GroupBox grpDdlOptions;
        private System.Windows.Forms.Label lblDDLStatus;
        private System.Windows.Forms.CheckBox chkFilterObjects;

        private System.Windows.Forms.GroupBox grpModel;
        private System.Windows.Forms.Label lblModelName;
        private System.Windows.Forms.Label lblActiveModel;
        private System.Windows.Forms.Label lblConnectionStatus;
        private System.Windows.Forms.Label lblPlatformStatus;

        // Glossary status fields used by LoadGlossary / UpdateLastRefreshLabel.
        // Created and laid out at runtime by InitializeGeneralTab so they sit
        // inside the General tab's "Glossary" card rather than on a separate
        // tab. Keeping them as form-level fields preserves the existing call
        // sites (lblGlossaryStatus.Text = ..., etc.) without requiring a wider
        // refactor.
        private System.Windows.Forms.Label lblGlossaryStatus;
        private System.Windows.Forms.Label lblLastRefreshValue;

        private System.Windows.Forms.ListView listValidationResults;
        private System.Windows.Forms.Button btnValidateAll;
        private System.Windows.Forms.Label lblValidationStatus;
        private System.Windows.Forms.ComboBox cmbValidationFilter;
        private System.Windows.Forms.CheckBox chkErrorsOnly;
        private System.Windows.Forms.Label lblFilterLabel;


        private System.Windows.Forms.TabPage tabTableProcesses;
        private System.Windows.Forms.GroupBox grpTableProcesses;
        private System.Windows.Forms.Label lblSelectTable;
        private System.Windows.Forms.ComboBox cmbTables;
        private System.Windows.Forms.CheckBox chkArchiveTable;
        private System.Windows.Forms.CheckBox chkIsolatedTable;
        private System.Windows.Forms.Button btnCreateTables;
        private System.Windows.Forms.Label lblTableProcessStatus;
        private System.Windows.Forms.Label lblArchiveSuffix;
        private System.Windows.Forms.Label lblIsolatedSuffix;

        private System.Windows.Forms.Panel pnlStatusBar;
        private System.Windows.Forms.Panel pnlStatusSep;
        private System.Windows.Forms.Button btnClose;
        private System.Windows.Forms.Label lblStatus;
    }
}
