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
            this.tabModel = new System.Windows.Forms.TabPage();
            this.tabConfiguration = new System.Windows.Forms.TabPage();
            this.tabGlossary = new System.Windows.Forms.TabPage();
            this.tabValidation = new System.Windows.Forms.TabPage();
            this.tabTableProcesses = new System.Windows.Forms.TabPage();
            this.tabDebug = new System.Windows.Forms.TabPage();

            // Model tab
            this.grpModel = new System.Windows.Forms.GroupBox();
            this.lblActiveModel = new System.Windows.Forms.Label();
            this.lblConnectionStatus = new System.Windows.Forms.Label();
            this.lblModelName = new System.Windows.Forms.Label();
            this.lblPlatformStatus = new System.Windows.Forms.Label();

            // Configuration tab
            this.grpConfig = new System.Windows.Forms.GroupBox();
            this.lblDatabaseName = new System.Windows.Forms.Label();
            this.txtDatabaseName = new System.Windows.Forms.TextBox();
            this.lblSchemaName = new System.Windows.Forms.Label();
            this.txtSchemaName = new System.Windows.Forms.TextBox();
            this.lblName = new System.Windows.Forms.Label();
            this.txtName = new System.Windows.Forms.TextBox();
            this.btnApply = new System.Windows.Forms.Button();

            // Glossary tab
            this.grpGlossary = new System.Windows.Forms.GroupBox();
            this.lblHost = new System.Windows.Forms.Label();
            this.lblHostValue = new System.Windows.Forms.Label();
            this.lblPort = new System.Windows.Forms.Label();
            this.lblPortValue = new System.Windows.Forms.Label();
            this.lblGlossaryDatabase = new System.Windows.Forms.Label();
            this.lblDatabaseValue = new System.Windows.Forms.Label();
            this.btnTestConnection = new System.Windows.Forms.Button();
            this.btnReloadGlossary = new System.Windows.Forms.Button();
            this.lblGlossaryStatus = new System.Windows.Forms.Label();
            this.lblLastRefresh = new System.Windows.Forms.Label();
            this.lblLastRefreshValue = new System.Windows.Forms.Label();

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
            this.grpDebugLog = new System.Windows.Forms.GroupBox();
            this.txtDebugLog = new System.Windows.Forms.TextBox();
            this.btnCopyLog = new System.Windows.Forms.Button();
            this.btnClearLog = new System.Windows.Forms.Button();
            this.txtLogSearch = new System.Windows.Forms.TextBox();
            this.lblLogSearch = new System.Windows.Forms.Label();

            // Bottom
            this.pnlStatusBar = new System.Windows.Forms.Panel();
            this.pnlStatusSep = new System.Windows.Forms.Panel();
            this.btnClose = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();

            this.tabControl.SuspendLayout();
            this.tabModel.SuspendLayout();
            this.tabConfiguration.SuspendLayout();
            this.tabGlossary.SuspendLayout();
            this.tabValidation.SuspendLayout();
            this.tabTableProcesses.SuspendLayout();
            this.tabDebug.SuspendLayout();
            this.grpTableProcesses.SuspendLayout();
            this.grpModel.SuspendLayout();
            this.grpConfig.SuspendLayout();
            this.grpGlossary.SuspendLayout();
            this.grpDebugLog.SuspendLayout();
            this.pnlStatusBar.SuspendLayout();
            this.SuspendLayout();

            // ================================================================
            // tabControl
            // ================================================================
            this.tabControl.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.tabControl.Controls.Add(this.tabGeneral);
            this.tabControl.Controls.Add(this.tabModel);
            this.tabControl.Controls.Add(this.tabConfiguration);
            this.tabControl.Controls.Add(this.tabGlossary);
            this.tabControl.Controls.Add(this.tabValidation);
            this.tabControl.Controls.Add(this.tabTableProcesses);
            this.tabControl.Controls.Add(this.tabDebug);
            this.tabControl.Location = new System.Drawing.Point(16, 16);
            this.tabControl.Name = "tabControl";
            this.tabControl.SelectedIndex = 0;
            this.tabControl.Size = new System.Drawing.Size(868, 490);
            this.tabControl.TabIndex = 0;
            this.tabControl.Font = fontBody;

            // ================================================================
            // TAB 0: GENERAL
            // ================================================================
            this.tabGeneral.Location = new System.Drawing.Point(4, 26);
            this.tabGeneral.Name = "tabGeneral";
            this.tabGeneral.Padding = new System.Windows.Forms.Padding(20);
            this.tabGeneral.Size = new System.Drawing.Size(860, 460);
            this.tabGeneral.TabIndex = 10;
            this.tabGeneral.Text = "General";
            this.tabGeneral.UseVisualStyleBackColor = true;

            // --- General tab content (built at runtime in InitializeGeneralTab) ---

            // ================================================================
            // TAB 1: MODEL
            // ================================================================
            this.tabModel.Controls.Add(this.grpModel);
            this.tabModel.Location = new System.Drawing.Point(4, 26);
            this.tabModel.Name = "tabModel";
            this.tabModel.Padding = new System.Windows.Forms.Padding(12);
            this.tabModel.Size = new System.Drawing.Size(860, 460);
            this.tabModel.TabIndex = 0;
            this.tabModel.Text = "Model";
            this.tabModel.UseVisualStyleBackColor = true;

            // grpModel — Platform status moved inside
            this.grpModel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpModel.Controls.Add(this.lblActiveModel);
            this.grpModel.Controls.Add(this.lblConnectionStatus);
            this.grpModel.Controls.Add(this.lblModelName);
            this.grpModel.Controls.Add(this.lblPlatformStatus);
            this.grpModel.Location = new System.Drawing.Point(12, 12);
            this.grpModel.Name = "grpModel";
            this.grpModel.Size = new System.Drawing.Size(833, 95);
            this.grpModel.TabIndex = 0;
            this.grpModel.TabStop = false;
            this.grpModel.Text = "Active Model";
            this.grpModel.Font = fontBody;

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
            // TAB 2: CONFIGURATION
            // ================================================================
            this.tabConfiguration.Controls.Add(this.grpConfig);
            this.tabConfiguration.Location = new System.Drawing.Point(4, 26);
            this.tabConfiguration.Name = "tabConfiguration";
            this.tabConfiguration.Padding = new System.Windows.Forms.Padding(12);
            this.tabConfiguration.Size = new System.Drawing.Size(860, 460);
            this.tabConfiguration.TabIndex = 1;
            this.tabConfiguration.Text = "Configuration";
            this.tabConfiguration.UseVisualStyleBackColor = true;

            // grpConfig
            this.grpConfig.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpConfig.Controls.Add(this.lblDatabaseName);
            this.grpConfig.Controls.Add(this.txtDatabaseName);
            this.grpConfig.Controls.Add(this.lblSchemaName);
            this.grpConfig.Controls.Add(this.txtSchemaName);
            this.grpConfig.Controls.Add(this.lblName);
            this.grpConfig.Controls.Add(this.txtName);
            this.grpConfig.Controls.Add(this.btnApply);
            this.grpConfig.Location = new System.Drawing.Point(12, 12);
            this.grpConfig.Name = "grpConfig";
            this.grpConfig.Size = new System.Drawing.Size(833, 175);
            this.grpConfig.TabIndex = 0;
            this.grpConfig.TabStop = false;
            this.grpConfig.Text = "Model Configuration";

            this.lblDatabaseName.AutoSize = true;
            this.lblDatabaseName.Location = new System.Drawing.Point(16, 32);
            this.lblDatabaseName.Text = "Database:";
            this.lblDatabaseName.ForeColor = clrTextPrimary;

            this.txtDatabaseName.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtDatabaseName.Location = new System.Drawing.Point(100, 28);
            this.txtDatabaseName.Size = new System.Drawing.Size(440, 25);
            this.txtDatabaseName.TabIndex = 1;
            this.txtDatabaseName.TextChanged += new System.EventHandler(this.OnConfigChanged);

            this.lblSchemaName.AutoSize = true;
            this.lblSchemaName.Location = new System.Drawing.Point(16, 64);
            this.lblSchemaName.Text = "Schema:";
            this.lblSchemaName.ForeColor = clrTextPrimary;

            this.txtSchemaName.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtSchemaName.Location = new System.Drawing.Point(100, 60);
            this.txtSchemaName.Size = new System.Drawing.Size(440, 25);
            this.txtSchemaName.TabIndex = 3;
            this.txtSchemaName.TextChanged += new System.EventHandler(this.OnConfigChanged);

            this.lblName.AutoSize = true;
            this.lblName.Location = new System.Drawing.Point(16, 96);
            this.lblName.Text = "Name:";
            this.lblName.ForeColor = clrTextPrimary;

            // txtName — read-only computed field styled differently
            this.txtName.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtName.Location = new System.Drawing.Point(100, 92);
            this.txtName.Size = new System.Drawing.Size(440, 25);
            this.txtName.TabIndex = 5;
            this.txtName.ReadOnly = true;
            this.txtName.BackColor = clrSurfaceAlt;
            this.txtName.ForeColor = clrTextSecondary;
            this.txtName.Font = new System.Drawing.Font("Segoe UI", 9.5F, System.Drawing.FontStyle.Italic);
            this.txtName.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;

            // btnApply — Primary button style, right-aligned
            this.btnApply.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnApply.Location = new System.Drawing.Point(710, 132);
            this.btnApply.Name = "btnApply";
            this.btnApply.Size = new System.Drawing.Size(110, 32);
            this.btnApply.TabIndex = 6;
            this.btnApply.Text = "Apply to Model";
            this.btnApply.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnApply.BackColor = clrPrimary;
            this.btnApply.ForeColor = System.Drawing.Color.White;
            this.btnApply.Font = fontBodyBold;
            this.btnApply.FlatAppearance.BorderSize = 0;
            this.btnApply.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnApply.Click += new System.EventHandler(this.BtnApply_Click);

            // ================================================================
            // TAB 3: GLOSSARY
            // ================================================================
            this.tabGlossary.Controls.Add(this.grpGlossary);
            this.tabGlossary.Location = new System.Drawing.Point(4, 26);
            this.tabGlossary.Name = "tabGlossary";
            this.tabGlossary.Padding = new System.Windows.Forms.Padding(12);
            this.tabGlossary.Size = new System.Drawing.Size(860, 460);
            this.tabGlossary.TabIndex = 2;
            this.tabGlossary.Text = "Glossary";
            this.tabGlossary.UseVisualStyleBackColor = true;

            this.grpGlossary.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpGlossary.Controls.Add(this.lblHost);
            this.grpGlossary.Controls.Add(this.lblHostValue);
            this.grpGlossary.Controls.Add(this.lblPort);
            this.grpGlossary.Controls.Add(this.lblPortValue);
            this.grpGlossary.Controls.Add(this.lblGlossaryDatabase);
            this.grpGlossary.Controls.Add(this.lblDatabaseValue);
            this.grpGlossary.Controls.Add(this.btnTestConnection);
            this.grpGlossary.Controls.Add(this.btnReloadGlossary);
            this.grpGlossary.Controls.Add(this.lblGlossaryStatus);
            this.grpGlossary.Controls.Add(this.lblLastRefresh);
            this.grpGlossary.Controls.Add(this.lblLastRefreshValue);
            this.grpGlossary.Location = new System.Drawing.Point(12, 12);
            this.grpGlossary.Name = "grpGlossary";
            this.grpGlossary.Size = new System.Drawing.Size(833, 210);
            this.grpGlossary.TabIndex = 0;
            this.grpGlossary.TabStop = false;
            this.grpGlossary.Text = "Glossary Database Connection";

            this.lblHost.AutoSize = true;
            this.lblHost.Location = new System.Drawing.Point(16, 32);
            this.lblHost.Text = "Host:";
            this.lblHost.ForeColor = clrTextPrimary;

            this.lblHostValue.AutoSize = true;
            this.lblHostValue.Font = fontBodyBold;
            this.lblHostValue.Location = new System.Drawing.Point(100, 32);
            this.lblHostValue.Text = "(not loaded)";
            this.lblHostValue.ForeColor = clrTextDisabled;

            this.lblPort.AutoSize = true;
            this.lblPort.Location = new System.Drawing.Point(320, 32);
            this.lblPort.Text = "Port:";
            this.lblPort.ForeColor = clrTextPrimary;

            this.lblPortValue.AutoSize = true;
            this.lblPortValue.Font = fontBodyBold;
            this.lblPortValue.Location = new System.Drawing.Point(360, 32);
            this.lblPortValue.Text = "-";
            this.lblPortValue.ForeColor = clrTextDisabled;

            this.lblGlossaryDatabase.AutoSize = true;
            this.lblGlossaryDatabase.Location = new System.Drawing.Point(16, 58);
            this.lblGlossaryDatabase.Text = "Database:";
            this.lblGlossaryDatabase.ForeColor = clrTextPrimary;

            this.lblDatabaseValue.AutoSize = true;
            this.lblDatabaseValue.Font = fontBodyBold;
            this.lblDatabaseValue.Location = new System.Drawing.Point(100, 58);
            this.lblDatabaseValue.Text = "(not loaded)";
            this.lblDatabaseValue.ForeColor = clrTextDisabled;

            this.lblLastRefresh.AutoSize = true;
            this.lblLastRefresh.Location = new System.Drawing.Point(16, 84);
            this.lblLastRefresh.Text = "Last Refresh:";
            this.lblLastRefresh.ForeColor = clrTextPrimary;

            this.lblLastRefreshValue.AutoSize = true;
            this.lblLastRefreshValue.Font = fontBodyBold;
            this.lblLastRefreshValue.Location = new System.Drawing.Point(100, 84);
            this.lblLastRefreshValue.Text = "(not yet)";
            this.lblLastRefreshValue.ForeColor = clrTextDisabled;

            // Glossary status
            this.lblGlossaryStatus.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.lblGlossaryStatus.Location = new System.Drawing.Point(16, 112);
            this.lblGlossaryStatus.Size = new System.Drawing.Size(800, 24);
            this.lblGlossaryStatus.Text = "";
            this.lblGlossaryStatus.ForeColor = clrTextSecondary;

            // Glossary buttons — Secondary style
            this.btnTestConnection.Location = new System.Drawing.Point(16, 145);
            this.btnTestConnection.Size = new System.Drawing.Size(120, 32);
            this.btnTestConnection.Text = "Test Connection";
            this.btnTestConnection.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnTestConnection.BackColor = System.Drawing.Color.White;
            this.btnTestConnection.ForeColor = clrTextPrimary;
            this.btnTestConnection.FlatAppearance.BorderColor = clrBorder;
            this.btnTestConnection.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnTestConnection.Click += new System.EventHandler(this.BtnTestConnection_Click);

            this.btnReloadGlossary.Location = new System.Drawing.Point(146, 145);
            this.btnReloadGlossary.Size = new System.Drawing.Size(130, 32);
            this.btnReloadGlossary.Text = "Reload Glossary";
            this.btnReloadGlossary.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnReloadGlossary.BackColor = System.Drawing.Color.White;
            this.btnReloadGlossary.ForeColor = clrTextPrimary;
            this.btnReloadGlossary.FlatAppearance.BorderColor = clrBorder;
            this.btnReloadGlossary.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnReloadGlossary.Click += new System.EventHandler(this.BtnReloadGlossary_Click);

            // ================================================================
            // TAB 4: VALIDATION
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

            // Results ListView
            this.listValidationResults.Dock = System.Windows.Forms.DockStyle.Fill;
            this.listValidationResults.FullRowSelect = true;
            this.listValidationResults.GridLines = true;
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
            // TAB 6: DEBUG LOG
            // ================================================================
            this.tabDebug.Controls.Add(this.grpDebugLog);
            this.tabDebug.Location = new System.Drawing.Point(4, 26);
            this.tabDebug.Name = "tabDebug";
            this.tabDebug.Padding = new System.Windows.Forms.Padding(12);
            this.tabDebug.Size = new System.Drawing.Size(860, 460);
            this.tabDebug.TabIndex = 4;
            this.tabDebug.Text = "Debug Log";
            this.tabDebug.UseVisualStyleBackColor = true;

            this.grpDebugLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.grpDebugLog.Controls.Add(this.btnCopyLog);
            this.grpDebugLog.Controls.Add(this.btnClearLog);
            this.grpDebugLog.Controls.Add(this.lblLogSearch);
            this.grpDebugLog.Controls.Add(this.txtLogSearch);
            this.grpDebugLog.Controls.Add(this.txtDebugLog);
            this.grpDebugLog.Location = new System.Drawing.Point(12, 12);
            this.grpDebugLog.Name = "grpDebugLog";
            this.grpDebugLog.Size = new System.Drawing.Size(833, 432);
            this.grpDebugLog.TabIndex = 0;
            this.grpDebugLog.TabStop = false;
            this.grpDebugLog.Text = "Debug Output";

            // Toolbar row: Copy, Clear, Search
            this.btnCopyLog.Location = new System.Drawing.Point(16, 24);
            this.btnCopyLog.Size = new System.Drawing.Size(70, 28);
            this.btnCopyLog.Text = "Copy";
            this.btnCopyLog.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnCopyLog.BackColor = System.Drawing.Color.White;
            this.btnCopyLog.ForeColor = clrTextSecondary;
            this.btnCopyLog.FlatAppearance.BorderColor = clrBorder;
            this.btnCopyLog.Font = fontCaption;
            this.btnCopyLog.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnCopyLog.Click += new System.EventHandler(this.BtnCopyLog_Click);

            this.btnClearLog.Location = new System.Drawing.Point(94, 24);
            this.btnClearLog.Size = new System.Drawing.Size(70, 28);
            this.btnClearLog.Text = "Clear";
            this.btnClearLog.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnClearLog.BackColor = System.Drawing.Color.White;
            this.btnClearLog.ForeColor = clrTextSecondary;
            this.btnClearLog.FlatAppearance.BorderColor = clrBorder;
            this.btnClearLog.Font = fontCaption;
            this.btnClearLog.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnClearLog.Click += new System.EventHandler(this.BtnClearLog_Click);

            this.lblLogSearch.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.lblLogSearch.AutoSize = true;
            this.lblLogSearch.Location = new System.Drawing.Point(560, 30);
            this.lblLogSearch.Text = "Search:";
            this.lblLogSearch.ForeColor = clrTextSecondary;
            this.lblLogSearch.Font = fontCaption;

            this.txtLogSearch.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.txtLogSearch.Location = new System.Drawing.Point(610, 26);
            this.txtLogSearch.Size = new System.Drawing.Size(207, 25);
            this.txtLogSearch.Font = fontCaption;
            this.txtLogSearch.PlaceholderText = "Filter log...";
            this.txtLogSearch.TextChanged += new System.EventHandler(this.TxtLogSearch_TextChanged);

            // Debug log — dark theme
            this.txtDebugLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.txtDebugLog.Location = new System.Drawing.Point(16, 60);
            this.txtDebugLog.Multiline = true;
            this.txtDebugLog.ReadOnly = true;
            this.txtDebugLog.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            this.txtDebugLog.Size = new System.Drawing.Size(801, 360);
            this.txtDebugLog.Font = new System.Drawing.Font("Consolas", 9F);
            this.txtDebugLog.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.txtDebugLog.ForeColor = System.Drawing.Color.FromArgb(212, 212, 212);
            this.txtDebugLog.WordWrap = false;

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
            this.ClientSize = new System.Drawing.Size(900, 570);
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
            this.tabModel.ResumeLayout(false);
            this.tabConfiguration.ResumeLayout(false);
            this.tabGlossary.ResumeLayout(false);
            this.tabValidation.ResumeLayout(false);
            this.tabTableProcesses.ResumeLayout(false);
            this.tabDebug.ResumeLayout(false);
            this.grpTableProcesses.ResumeLayout(false);
            this.grpTableProcesses.PerformLayout();
            this.grpModel.ResumeLayout(false);
            this.grpModel.PerformLayout();
            this.grpConfig.ResumeLayout(false);
            this.grpConfig.PerformLayout();
            this.grpGlossary.ResumeLayout(false);
            this.grpGlossary.PerformLayout();
            this.grpDebugLog.ResumeLayout(false);
            this.grpDebugLog.PerformLayout();
            this.pnlStatusBar.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.TabControl tabControl;
        private System.Windows.Forms.TabPage tabGeneral;
        private System.Windows.Forms.TabPage tabModel;
        private System.Windows.Forms.TabPage tabConfiguration;
        private System.Windows.Forms.TabPage tabGlossary;
        private System.Windows.Forms.TabPage tabValidation;
        private System.Windows.Forms.TabPage tabDebug;

        private System.Windows.Forms.GroupBox grpModel;
        private System.Windows.Forms.Label lblModelName;
        private System.Windows.Forms.Label lblActiveModel;
        private System.Windows.Forms.Label lblConnectionStatus;
        private System.Windows.Forms.Label lblPlatformStatus;

        private System.Windows.Forms.GroupBox grpConfig;
        private System.Windows.Forms.Label lblDatabaseName;
        private System.Windows.Forms.TextBox txtDatabaseName;
        private System.Windows.Forms.Label lblSchemaName;
        private System.Windows.Forms.TextBox txtSchemaName;
        private System.Windows.Forms.Label lblName;
        private System.Windows.Forms.TextBox txtName;
        private System.Windows.Forms.Button btnApply;

        private System.Windows.Forms.GroupBox grpGlossary;
        private System.Windows.Forms.Label lblHost;
        private System.Windows.Forms.Label lblHostValue;
        private System.Windows.Forms.Label lblPort;
        private System.Windows.Forms.Label lblPortValue;
        private System.Windows.Forms.Label lblGlossaryDatabase;
        private System.Windows.Forms.Label lblDatabaseValue;
        private System.Windows.Forms.Button btnTestConnection;
        private System.Windows.Forms.Button btnReloadGlossary;
        private System.Windows.Forms.Label lblGlossaryStatus;
        private System.Windows.Forms.Label lblLastRefresh;
        private System.Windows.Forms.Label lblLastRefreshValue;

        private System.Windows.Forms.ListView listValidationResults;
        private System.Windows.Forms.Button btnValidateAll;
        private System.Windows.Forms.Label lblValidationStatus;
        private System.Windows.Forms.ComboBox cmbValidationFilter;
        private System.Windows.Forms.CheckBox chkErrorsOnly;
        private System.Windows.Forms.Label lblFilterLabel;

        private System.Windows.Forms.GroupBox grpDebugLog;
        private System.Windows.Forms.TextBox txtDebugLog;
        private System.Windows.Forms.Button btnCopyLog;
        private System.Windows.Forms.Button btnClearLog;
        private System.Windows.Forms.TextBox txtLogSearch;
        private System.Windows.Forms.Label lblLogSearch;

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
