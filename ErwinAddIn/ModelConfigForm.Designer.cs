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
            // TabControl and TabPages
            this.tabControl = new System.Windows.Forms.TabControl();
            this.tabModel = new System.Windows.Forms.TabPage();
            this.tabConfiguration = new System.Windows.Forms.TabPage();
            this.tabGlossary = new System.Windows.Forms.TabPage();
            this.tabValidation = new System.Windows.Forms.TabPage();
            this.tabDebug = new System.Windows.Forms.TabPage();

            // Model tab controls
            this.grpModel = new System.Windows.Forms.GroupBox();
            this.cmbModels = new System.Windows.Forms.ComboBox();
            this.lblConnectionStatus = new System.Windows.Forms.Label();
            this.lblModelName = new System.Windows.Forms.Label();

            // Configuration tab controls
            this.grpConfig = new System.Windows.Forms.GroupBox();
            this.lblDatabaseName = new System.Windows.Forms.Label();
            this.txtDatabaseName = new System.Windows.Forms.TextBox();
            this.lblSchemaName = new System.Windows.Forms.Label();
            this.txtSchemaName = new System.Windows.Forms.TextBox();
            this.lblName = new System.Windows.Forms.Label();
            this.txtName = new System.Windows.Forms.TextBox();
            this.btnApply = new System.Windows.Forms.Button();

            // Validation tab controls - nested TabControl for Column and Table validations
            this.tabControlValidation = new System.Windows.Forms.TabControl();
            this.tabColumnValidation = new System.Windows.Forms.TabPage();
            this.tabTableValidation = new System.Windows.Forms.TabPage();
            this.btnValidateAll = new System.Windows.Forms.Button();
            this.listColumnValidation = new System.Windows.Forms.ListView();
            this.listTableValidation = new System.Windows.Forms.ListView();
            this.lblValidationStatus = new System.Windows.Forms.Label();

            // Glossary tab controls
            this.grpGlossary = new System.Windows.Forms.GroupBox();
            this.lblHost = new System.Windows.Forms.Label();
            this.txtHost = new System.Windows.Forms.TextBox();
            this.lblPort = new System.Windows.Forms.Label();
            this.txtPort = new System.Windows.Forms.TextBox();
            this.lblGlossaryDatabase = new System.Windows.Forms.Label();
            this.txtGlossaryDatabase = new System.Windows.Forms.TextBox();
            this.lblUserId = new System.Windows.Forms.Label();
            this.txtUserId = new System.Windows.Forms.TextBox();
            this.lblPassword = new System.Windows.Forms.Label();
            this.txtPassword = new System.Windows.Forms.TextBox();
            this.btnTestConnection = new System.Windows.Forms.Button();
            this.btnReloadGlossary = new System.Windows.Forms.Button();
            this.lblGlossaryStatus = new System.Windows.Forms.Label();

            // Debug tab controls
            this.grpDebugLog = new System.Windows.Forms.GroupBox();
            this.txtDebugLog = new System.Windows.Forms.TextBox();
            this.btnCopyLog = new System.Windows.Forms.Button();
            this.btnClearLog = new System.Windows.Forms.Button();

            // Bottom controls
            this.btnClose = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();

            this.tabControl.SuspendLayout();
            this.tabModel.SuspendLayout();
            this.tabConfiguration.SuspendLayout();
            this.tabGlossary.SuspendLayout();
            this.tabValidation.SuspendLayout();
            this.tabDebug.SuspendLayout();
            this.grpModel.SuspendLayout();
            this.grpConfig.SuspendLayout();
            this.grpGlossary.SuspendLayout();
            this.tabControlValidation.SuspendLayout();
            this.tabColumnValidation.SuspendLayout();
            this.tabTableValidation.SuspendLayout();
            this.grpDebugLog.SuspendLayout();
            this.SuspendLayout();

            //
            // tabControl
            //
            this.tabControl.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.tabControl.Controls.Add(this.tabModel);
            this.tabControl.Controls.Add(this.tabConfiguration);
            this.tabControl.Controls.Add(this.tabGlossary);
            this.tabControl.Controls.Add(this.tabValidation);
            this.tabControl.Controls.Add(this.tabDebug);
            this.tabControl.Location = new System.Drawing.Point(12, 12);
            this.tabControl.Name = "tabControl";
            this.tabControl.SelectedIndex = 0;
            this.tabControl.Size = new System.Drawing.Size(560, 480);
            this.tabControl.TabIndex = 0;

            //
            // tabModel
            //
            this.tabModel.Controls.Add(this.grpModel);
            this.tabModel.Location = new System.Drawing.Point(4, 24);
            this.tabModel.Name = "tabModel";
            this.tabModel.Padding = new System.Windows.Forms.Padding(10);
            this.tabModel.Size = new System.Drawing.Size(552, 452);
            this.tabModel.TabIndex = 0;
            this.tabModel.Text = "Model";
            this.tabModel.UseVisualStyleBackColor = true;

            //
            // grpModel
            //
            this.grpModel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpModel.Controls.Add(this.cmbModels);
            this.grpModel.Controls.Add(this.lblConnectionStatus);
            this.grpModel.Controls.Add(this.lblModelName);
            this.grpModel.Location = new System.Drawing.Point(10, 10);
            this.grpModel.Name = "grpModel";
            this.grpModel.Size = new System.Drawing.Size(525, 70);
            this.grpModel.TabIndex = 0;
            this.grpModel.TabStop = false;
            this.grpModel.Text = "Select Model";

            //
            // lblModelName
            //
            this.lblModelName.AutoSize = true;
            this.lblModelName.Location = new System.Drawing.Point(15, 30);
            this.lblModelName.Name = "lblModelName";
            this.lblModelName.Size = new System.Drawing.Size(45, 15);
            this.lblModelName.TabIndex = 0;
            this.lblModelName.Text = "Model:";

            //
            // cmbModels
            //
            this.cmbModels.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.cmbModels.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbModels.Location = new System.Drawing.Point(70, 27);
            this.cmbModels.Name = "cmbModels";
            this.cmbModels.Size = new System.Drawing.Size(350, 23);
            this.cmbModels.TabIndex = 1;
            this.cmbModels.SelectedIndexChanged += new System.EventHandler(this.CmbModels_SelectedIndexChanged);

            //
            // lblConnectionStatus
            //
            this.lblConnectionStatus.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.lblConnectionStatus.Location = new System.Drawing.Point(430, 30);
            this.lblConnectionStatus.Name = "lblConnectionStatus";
            this.lblConnectionStatus.Size = new System.Drawing.Size(85, 15);
            this.lblConnectionStatus.TabIndex = 2;
            this.lblConnectionStatus.Text = "(Yükleniyor...)";
            this.lblConnectionStatus.ForeColor = System.Drawing.Color.Gray;

            //
            // tabConfiguration
            //
            this.tabConfiguration.Controls.Add(this.grpConfig);
            this.tabConfiguration.Location = new System.Drawing.Point(4, 24);
            this.tabConfiguration.Name = "tabConfiguration";
            this.tabConfiguration.Padding = new System.Windows.Forms.Padding(10);
            this.tabConfiguration.Size = new System.Drawing.Size(552, 452);
            this.tabConfiguration.TabIndex = 1;
            this.tabConfiguration.Text = "Configuration";
            this.tabConfiguration.UseVisualStyleBackColor = true;

            //
            // grpConfig
            //
            this.grpConfig.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpConfig.Controls.Add(this.lblDatabaseName);
            this.grpConfig.Controls.Add(this.txtDatabaseName);
            this.grpConfig.Controls.Add(this.lblSchemaName);
            this.grpConfig.Controls.Add(this.txtSchemaName);
            this.grpConfig.Controls.Add(this.lblName);
            this.grpConfig.Controls.Add(this.txtName);
            this.grpConfig.Controls.Add(this.btnApply);
            this.grpConfig.Location = new System.Drawing.Point(10, 10);
            this.grpConfig.Name = "grpConfig";
            this.grpConfig.Size = new System.Drawing.Size(525, 170);
            this.grpConfig.TabIndex = 0;
            this.grpConfig.TabStop = false;
            this.grpConfig.Text = "Model Configuration";

            //
            // lblDatabaseName
            //
            this.lblDatabaseName.AutoSize = true;
            this.lblDatabaseName.Location = new System.Drawing.Point(15, 28);
            this.lblDatabaseName.Name = "lblDatabaseName";
            this.lblDatabaseName.Size = new System.Drawing.Size(60, 15);
            this.lblDatabaseName.TabIndex = 0;
            this.lblDatabaseName.Text = "Database:";

            //
            // txtDatabaseName
            //
            this.txtDatabaseName.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtDatabaseName.Location = new System.Drawing.Point(100, 25);
            this.txtDatabaseName.Name = "txtDatabaseName";
            this.txtDatabaseName.Size = new System.Drawing.Size(410, 23);
            this.txtDatabaseName.TabIndex = 1;
            this.txtDatabaseName.TextChanged += new System.EventHandler(this.OnConfigChanged);

            //
            // lblSchemaName
            //
            this.lblSchemaName.AutoSize = true;
            this.lblSchemaName.Location = new System.Drawing.Point(15, 60);
            this.lblSchemaName.Name = "lblSchemaName";
            this.lblSchemaName.Size = new System.Drawing.Size(52, 15);
            this.lblSchemaName.TabIndex = 2;
            this.lblSchemaName.Text = "Schema:";

            //
            // txtSchemaName
            //
            this.txtSchemaName.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtSchemaName.Location = new System.Drawing.Point(100, 57);
            this.txtSchemaName.Name = "txtSchemaName";
            this.txtSchemaName.Size = new System.Drawing.Size(410, 23);
            this.txtSchemaName.TabIndex = 3;
            this.txtSchemaName.TextChanged += new System.EventHandler(this.OnConfigChanged);

            //
            // lblName
            //
            this.lblName.AutoSize = true;
            this.lblName.Location = new System.Drawing.Point(15, 93);
            this.lblName.Name = "lblName";
            this.lblName.Size = new System.Drawing.Size(42, 15);
            this.lblName.TabIndex = 4;
            this.lblName.Text = "Name:";

            //
            // txtName
            //
            this.txtName.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtName.Location = new System.Drawing.Point(100, 90);
            this.txtName.Name = "txtName";
            this.txtName.Size = new System.Drawing.Size(410, 23);
            this.txtName.TabIndex = 5;
            this.txtName.ReadOnly = true;
            this.txtName.BackColor = System.Drawing.Color.WhiteSmoke;

            //
            // btnApply
            //
            this.btnApply.Location = new System.Drawing.Point(100, 125);
            this.btnApply.Name = "btnApply";
            this.btnApply.Size = new System.Drawing.Size(120, 28);
            this.btnApply.TabIndex = 6;
            this.btnApply.Text = "Apply to Model";
            this.btnApply.UseVisualStyleBackColor = true;
            this.btnApply.Click += new System.EventHandler(this.BtnApply_Click);

            //
            // tabGlossary
            //
            this.tabGlossary.Controls.Add(this.grpGlossary);
            this.tabGlossary.Location = new System.Drawing.Point(4, 24);
            this.tabGlossary.Name = "tabGlossary";
            this.tabGlossary.Padding = new System.Windows.Forms.Padding(10);
            this.tabGlossary.Size = new System.Drawing.Size(552, 452);
            this.tabGlossary.TabIndex = 2;
            this.tabGlossary.Text = "Glossary";
            this.tabGlossary.UseVisualStyleBackColor = true;

            //
            // grpGlossary
            //
            this.grpGlossary.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.grpGlossary.Controls.Add(this.lblHost);
            this.grpGlossary.Controls.Add(this.txtHost);
            this.grpGlossary.Controls.Add(this.lblPort);
            this.grpGlossary.Controls.Add(this.txtPort);
            this.grpGlossary.Controls.Add(this.lblGlossaryDatabase);
            this.grpGlossary.Controls.Add(this.txtGlossaryDatabase);
            this.grpGlossary.Controls.Add(this.lblUserId);
            this.grpGlossary.Controls.Add(this.txtUserId);
            this.grpGlossary.Controls.Add(this.lblPassword);
            this.grpGlossary.Controls.Add(this.txtPassword);
            this.grpGlossary.Controls.Add(this.btnTestConnection);
            this.grpGlossary.Controls.Add(this.btnReloadGlossary);
            this.grpGlossary.Controls.Add(this.lblGlossaryStatus);
            this.grpGlossary.Location = new System.Drawing.Point(10, 10);
            this.grpGlossary.Name = "grpGlossary";
            this.grpGlossary.Size = new System.Drawing.Size(525, 250);
            this.grpGlossary.TabIndex = 0;
            this.grpGlossary.TabStop = false;
            this.grpGlossary.Text = "Glossary Database Connection";

            //
            // lblHost
            //
            this.lblHost.AutoSize = true;
            this.lblHost.Location = new System.Drawing.Point(15, 30);
            this.lblHost.Name = "lblHost";
            this.lblHost.Size = new System.Drawing.Size(35, 15);
            this.lblHost.TabIndex = 0;
            this.lblHost.Text = "Host:";

            //
            // txtHost
            //
            this.txtHost.Location = new System.Drawing.Point(100, 27);
            this.txtHost.Name = "txtHost";
            this.txtHost.Size = new System.Drawing.Size(280, 23);
            this.txtHost.TabIndex = 1;
            this.txtHost.Text = "localhost";

            //
            // lblPort
            //
            this.lblPort.AutoSize = true;
            this.lblPort.Location = new System.Drawing.Point(390, 30);
            this.lblPort.Name = "lblPort";
            this.lblPort.Size = new System.Drawing.Size(32, 15);
            this.lblPort.TabIndex = 2;
            this.lblPort.Text = "Port:";

            //
            // txtPort
            //
            this.txtPort.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.txtPort.Location = new System.Drawing.Point(430, 27);
            this.txtPort.Name = "txtPort";
            this.txtPort.Size = new System.Drawing.Size(80, 23);
            this.txtPort.TabIndex = 3;
            this.txtPort.Text = "1433";

            //
            // lblGlossaryDatabase
            //
            this.lblGlossaryDatabase.AutoSize = true;
            this.lblGlossaryDatabase.Location = new System.Drawing.Point(15, 62);
            this.lblGlossaryDatabase.Name = "lblGlossaryDatabase";
            this.lblGlossaryDatabase.Size = new System.Drawing.Size(60, 15);
            this.lblGlossaryDatabase.TabIndex = 4;
            this.lblGlossaryDatabase.Text = "Database:";

            //
            // txtGlossaryDatabase
            //
            this.txtGlossaryDatabase.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtGlossaryDatabase.Location = new System.Drawing.Point(100, 59);
            this.txtGlossaryDatabase.Name = "txtGlossaryDatabase";
            this.txtGlossaryDatabase.Size = new System.Drawing.Size(410, 23);
            this.txtGlossaryDatabase.TabIndex = 5;
            this.txtGlossaryDatabase.Text = "Fiba";

            //
            // lblUserId
            //
            this.lblUserId.AutoSize = true;
            this.lblUserId.Location = new System.Drawing.Point(15, 94);
            this.lblUserId.Name = "lblUserId";
            this.lblUserId.Size = new System.Drawing.Size(47, 15);
            this.lblUserId.TabIndex = 6;
            this.lblUserId.Text = "User ID:";

            //
            // txtUserId
            //
            this.txtUserId.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtUserId.Location = new System.Drawing.Point(100, 91);
            this.txtUserId.Name = "txtUserId";
            this.txtUserId.Size = new System.Drawing.Size(410, 23);
            this.txtUserId.TabIndex = 7;
            this.txtUserId.Text = "sa";

            //
            // lblPassword
            //
            this.lblPassword.AutoSize = true;
            this.lblPassword.Location = new System.Drawing.Point(15, 126);
            this.lblPassword.Name = "lblPassword";
            this.lblPassword.Size = new System.Drawing.Size(60, 15);
            this.lblPassword.TabIndex = 8;
            this.lblPassword.Text = "Password:";

            //
            // txtPassword
            //
            this.txtPassword.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.txtPassword.Location = new System.Drawing.Point(100, 123);
            this.txtPassword.Name = "txtPassword";
            this.txtPassword.Size = new System.Drawing.Size(410, 23);
            this.txtPassword.TabIndex = 9;
            this.txtPassword.Text = "Elite12345";
            this.txtPassword.UseSystemPasswordChar = true;

            //
            // btnTestConnection
            //
            this.btnTestConnection.Location = new System.Drawing.Point(100, 160);
            this.btnTestConnection.Name = "btnTestConnection";
            this.btnTestConnection.Size = new System.Drawing.Size(120, 28);
            this.btnTestConnection.TabIndex = 10;
            this.btnTestConnection.Text = "Test Connection";
            this.btnTestConnection.UseVisualStyleBackColor = true;
            this.btnTestConnection.Click += new System.EventHandler(this.BtnTestConnection_Click);

            //
            // btnReloadGlossary
            //
            this.btnReloadGlossary.Location = new System.Drawing.Point(230, 160);
            this.btnReloadGlossary.Name = "btnReloadGlossary";
            this.btnReloadGlossary.Size = new System.Drawing.Size(120, 28);
            this.btnReloadGlossary.TabIndex = 11;
            this.btnReloadGlossary.Text = "Reload Glossary";
            this.btnReloadGlossary.UseVisualStyleBackColor = true;
            this.btnReloadGlossary.Click += new System.EventHandler(this.BtnReloadGlossary_Click);

            //
            // lblGlossaryStatus
            //
            this.lblGlossaryStatus.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.lblGlossaryStatus.Location = new System.Drawing.Point(100, 200);
            this.lblGlossaryStatus.Name = "lblGlossaryStatus";
            this.lblGlossaryStatus.Size = new System.Drawing.Size(410, 40);
            this.lblGlossaryStatus.TabIndex = 12;
            this.lblGlossaryStatus.Text = "";

            //
            // tabValidation
            //
            this.tabValidation.Controls.Add(this.btnValidateAll);
            this.tabValidation.Controls.Add(this.tabControlValidation);
            this.tabValidation.Controls.Add(this.lblValidationStatus);
            this.tabValidation.Location = new System.Drawing.Point(4, 24);
            this.tabValidation.Name = "tabValidation";
            this.tabValidation.Padding = new System.Windows.Forms.Padding(10);
            this.tabValidation.Size = new System.Drawing.Size(552, 452);
            this.tabValidation.TabIndex = 3;
            this.tabValidation.Text = "Validation";
            this.tabValidation.UseVisualStyleBackColor = true;

            //
            // btnValidateAll
            //
            this.btnValidateAll.Location = new System.Drawing.Point(13, 13);
            this.btnValidateAll.Name = "btnValidateAll";
            this.btnValidateAll.Size = new System.Drawing.Size(110, 28);
            this.btnValidateAll.TabIndex = 0;
            this.btnValidateAll.Text = "Validate All";
            this.btnValidateAll.UseVisualStyleBackColor = true;
            this.btnValidateAll.Click += new System.EventHandler(this.BtnValidateAll_Click);

            //
            // tabControlValidation - nested TabControl
            //
            this.tabControlValidation.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.tabControlValidation.Controls.Add(this.tabColumnValidation);
            this.tabControlValidation.Controls.Add(this.tabTableValidation);
            this.tabControlValidation.Location = new System.Drawing.Point(13, 47);
            this.tabControlValidation.Name = "tabControlValidation";
            this.tabControlValidation.SelectedIndex = 0;
            this.tabControlValidation.Size = new System.Drawing.Size(520, 370);
            this.tabControlValidation.TabIndex = 1;

            //
            // tabColumnValidation
            //
            this.tabColumnValidation.Controls.Add(this.listColumnValidation);
            this.tabColumnValidation.Location = new System.Drawing.Point(4, 24);
            this.tabColumnValidation.Name = "tabColumnValidation";
            this.tabColumnValidation.Padding = new System.Windows.Forms.Padding(5);
            this.tabColumnValidation.Size = new System.Drawing.Size(512, 342);
            this.tabColumnValidation.TabIndex = 0;
            this.tabColumnValidation.Text = "Column Validation";
            this.tabColumnValidation.UseVisualStyleBackColor = true;

            //
            // listColumnValidation
            //
            this.listColumnValidation.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.listColumnValidation.FullRowSelect = true;
            this.listColumnValidation.GridLines = true;
            this.listColumnValidation.Location = new System.Drawing.Point(5, 5);
            this.listColumnValidation.Name = "listColumnValidation";
            this.listColumnValidation.Size = new System.Drawing.Size(502, 332);
            this.listColumnValidation.TabIndex = 0;
            this.listColumnValidation.UseCompatibleStateImageBehavior = false;
            this.listColumnValidation.View = System.Windows.Forms.View.Details;

            //
            // tabTableValidation
            //
            this.tabTableValidation.Controls.Add(this.listTableValidation);
            this.tabTableValidation.Location = new System.Drawing.Point(4, 24);
            this.tabTableValidation.Name = "tabTableValidation";
            this.tabTableValidation.Padding = new System.Windows.Forms.Padding(5);
            this.tabTableValidation.Size = new System.Drawing.Size(512, 342);
            this.tabTableValidation.TabIndex = 1;
            this.tabTableValidation.Text = "Table Validation";
            this.tabTableValidation.UseVisualStyleBackColor = true;

            //
            // listTableValidation
            //
            this.listTableValidation.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.listTableValidation.FullRowSelect = true;
            this.listTableValidation.GridLines = true;
            this.listTableValidation.Location = new System.Drawing.Point(5, 5);
            this.listTableValidation.Name = "listTableValidation";
            this.listTableValidation.Size = new System.Drawing.Size(502, 332);
            this.listTableValidation.TabIndex = 0;
            this.listTableValidation.UseCompatibleStateImageBehavior = false;
            this.listTableValidation.View = System.Windows.Forms.View.Details;

            //
            // lblValidationStatus
            //
            this.lblValidationStatus.Anchor = System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.lblValidationStatus.Location = new System.Drawing.Point(13, 422);
            this.lblValidationStatus.Name = "lblValidationStatus";
            this.lblValidationStatus.Size = new System.Drawing.Size(520, 20);
            this.lblValidationStatus.TabIndex = 2;
            this.lblValidationStatus.Text = "Real-time monitoring active";
            this.lblValidationStatus.ForeColor = System.Drawing.Color.DarkGreen;

            //
            // tabDebug
            //
            this.tabDebug.Controls.Add(this.grpDebugLog);
            this.tabDebug.Location = new System.Drawing.Point(4, 24);
            this.tabDebug.Name = "tabDebug";
            this.tabDebug.Padding = new System.Windows.Forms.Padding(10);
            this.tabDebug.Size = new System.Drawing.Size(552, 452);
            this.tabDebug.TabIndex = 4;
            this.tabDebug.Text = "Debug Log";
            this.tabDebug.UseVisualStyleBackColor = true;

            //
            // grpDebugLog
            //
            this.grpDebugLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.grpDebugLog.Controls.Add(this.txtDebugLog);
            this.grpDebugLog.Controls.Add(this.btnCopyLog);
            this.grpDebugLog.Controls.Add(this.btnClearLog);
            this.grpDebugLog.Location = new System.Drawing.Point(10, 10);
            this.grpDebugLog.Name = "grpDebugLog";
            this.grpDebugLog.Size = new System.Drawing.Size(525, 425);
            this.grpDebugLog.TabIndex = 0;
            this.grpDebugLog.TabStop = false;
            this.grpDebugLog.Text = "Debug Output";

            //
            // txtDebugLog
            //
            this.txtDebugLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Bottom;
            this.txtDebugLog.Location = new System.Drawing.Point(15, 25);
            this.txtDebugLog.Multiline = true;
            this.txtDebugLog.Name = "txtDebugLog";
            this.txtDebugLog.ReadOnly = true;
            this.txtDebugLog.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            this.txtDebugLog.Size = new System.Drawing.Size(410, 385);
            this.txtDebugLog.TabIndex = 0;
            this.txtDebugLog.Font = new System.Drawing.Font("Consolas", 9F);
            this.txtDebugLog.BackColor = System.Drawing.Color.White;

            //
            // btnCopyLog
            //
            this.btnCopyLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnCopyLog.Location = new System.Drawing.Point(435, 25);
            this.btnCopyLog.Name = "btnCopyLog";
            this.btnCopyLog.Size = new System.Drawing.Size(75, 28);
            this.btnCopyLog.TabIndex = 1;
            this.btnCopyLog.Text = "Copy";
            this.btnCopyLog.UseVisualStyleBackColor = true;
            this.btnCopyLog.Click += new System.EventHandler(this.BtnCopyLog_Click);

            //
            // btnClearLog
            //
            this.btnClearLog.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            this.btnClearLog.Location = new System.Drawing.Point(435, 60);
            this.btnClearLog.Name = "btnClearLog";
            this.btnClearLog.Size = new System.Drawing.Size(75, 28);
            this.btnClearLog.TabIndex = 2;
            this.btnClearLog.Text = "Clear";
            this.btnClearLog.UseVisualStyleBackColor = true;
            this.btnClearLog.Click += new System.EventHandler(this.BtnClearLog_Click);

            //
            // btnClose
            //
            this.btnClose.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right;
            this.btnClose.Location = new System.Drawing.Point(482, 505);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(90, 32);
            this.btnClose.TabIndex = 1;
            this.btnClose.Text = "Close";
            this.btnClose.UseVisualStyleBackColor = true;
            this.btnClose.Click += new System.EventHandler(this.BtnClose_Click);

            //
            // lblStatus
            //
            this.lblStatus.Anchor = System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            this.lblStatus.Location = new System.Drawing.Point(12, 510);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(460, 25);
            this.lblStatus.TabIndex = 2;
            this.lblStatus.Text = "";
            this.lblStatus.ForeColor = System.Drawing.Color.DarkBlue;

            //
            // ModelConfigForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(584, 551);
            this.Controls.Add(this.tabControl);
            this.Controls.Add(this.btnClose);
            this.Controls.Add(this.lblStatus);
            this.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
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
            this.tabDebug.ResumeLayout(false);
            this.grpModel.ResumeLayout(false);
            this.grpModel.PerformLayout();
            this.grpConfig.ResumeLayout(false);
            this.grpConfig.PerformLayout();
            this.grpGlossary.ResumeLayout(false);
            this.grpGlossary.PerformLayout();
            this.tabControlValidation.ResumeLayout(false);
            this.tabColumnValidation.ResumeLayout(false);
            this.tabTableValidation.ResumeLayout(false);
            this.grpDebugLog.ResumeLayout(false);
            this.grpDebugLog.PerformLayout();
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.TabControl tabControl;
        private System.Windows.Forms.TabPage tabModel;
        private System.Windows.Forms.TabPage tabConfiguration;
        private System.Windows.Forms.TabPage tabGlossary;
        private System.Windows.Forms.TabPage tabValidation;
        private System.Windows.Forms.TabPage tabDebug;

        private System.Windows.Forms.GroupBox grpModel;
        private System.Windows.Forms.Label lblModelName;
        private System.Windows.Forms.ComboBox cmbModels;
        private System.Windows.Forms.Label lblConnectionStatus;

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
        private System.Windows.Forms.TextBox txtHost;
        private System.Windows.Forms.Label lblPort;
        private System.Windows.Forms.TextBox txtPort;
        private System.Windows.Forms.Label lblGlossaryDatabase;
        private System.Windows.Forms.TextBox txtGlossaryDatabase;
        private System.Windows.Forms.Label lblUserId;
        private System.Windows.Forms.TextBox txtUserId;
        private System.Windows.Forms.Label lblPassword;
        private System.Windows.Forms.TextBox txtPassword;
        private System.Windows.Forms.Button btnTestConnection;
        private System.Windows.Forms.Button btnReloadGlossary;
        private System.Windows.Forms.Label lblGlossaryStatus;

        private System.Windows.Forms.TabControl tabControlValidation;
        private System.Windows.Forms.TabPage tabColumnValidation;
        private System.Windows.Forms.TabPage tabTableValidation;
        private System.Windows.Forms.Button btnValidateAll;
        private System.Windows.Forms.ListView listColumnValidation;
        private System.Windows.Forms.ListView listTableValidation;
        private System.Windows.Forms.Label lblValidationStatus;

        private System.Windows.Forms.GroupBox grpDebugLog;
        private System.Windows.Forms.TextBox txtDebugLog;
        private System.Windows.Forms.Button btnCopyLog;
        private System.Windows.Forms.Button btnClearLog;

        private System.Windows.Forms.Button btnClose;
        private System.Windows.Forms.Label lblStatus;
    }
}
