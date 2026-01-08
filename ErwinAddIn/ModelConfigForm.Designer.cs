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
            this.grpModel = new System.Windows.Forms.GroupBox();
            this.cmbModels = new System.Windows.Forms.ComboBox();
            this.lblConnectionStatus = new System.Windows.Forms.Label();
            this.lblModelName = new System.Windows.Forms.Label();

            this.grpConfig = new System.Windows.Forms.GroupBox();
            this.lblDatabaseName = new System.Windows.Forms.Label();
            this.txtDatabaseName = new System.Windows.Forms.TextBox();
            this.lblSchemaName = new System.Windows.Forms.Label();
            this.txtSchemaName = new System.Windows.Forms.TextBox();
            this.lblName = new System.Windows.Forms.Label();
            this.txtName = new System.Windows.Forms.TextBox();

            this.btnApply = new System.Windows.Forms.Button();
            this.btnClose = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();

            this.grpValidation = new System.Windows.Forms.GroupBox();
            this.chkMonitoring = new System.Windows.Forms.CheckBox();
            this.btnValidateAll = new System.Windows.Forms.Button();
            this.listValidationIssues = new System.Windows.Forms.ListView();
            this.lblValidationStatus = new System.Windows.Forms.Label();

            this.grpDebugLog = new System.Windows.Forms.GroupBox();
            this.txtDebugLog = new System.Windows.Forms.TextBox();
            this.btnCopyLog = new System.Windows.Forms.Button();
            this.btnClearLog = new System.Windows.Forms.Button();

            this.grpModel.SuspendLayout();
            this.grpConfig.SuspendLayout();
            this.grpValidation.SuspendLayout();
            this.grpDebugLog.SuspendLayout();
            this.SuspendLayout();

            //
            // grpModel
            //
            this.grpModel.Controls.Add(this.cmbModels);
            this.grpModel.Controls.Add(this.lblConnectionStatus);
            this.grpModel.Controls.Add(this.lblModelName);
            this.grpModel.Location = new System.Drawing.Point(12, 12);
            this.grpModel.Name = "grpModel";
            this.grpModel.Size = new System.Drawing.Size(560, 55);
            this.grpModel.TabIndex = 0;
            this.grpModel.TabStop = false;
            this.grpModel.Text = "Model";
            //
            // lblModelName
            //
            this.lblModelName.AutoSize = true;
            this.lblModelName.Location = new System.Drawing.Point(15, 25);
            this.lblModelName.Name = "lblModelName";
            this.lblModelName.Size = new System.Drawing.Size(45, 15);
            this.lblModelName.TabIndex = 0;
            this.lblModelName.Text = "Model:";
            //
            // cmbModels
            //
            this.cmbModels.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbModels.Location = new System.Drawing.Point(70, 21);
            this.cmbModels.Name = "cmbModels";
            this.cmbModels.Size = new System.Drawing.Size(380, 23);
            this.cmbModels.TabIndex = 1;
            this.cmbModels.SelectedIndexChanged += new System.EventHandler(this.CmbModels_SelectedIndexChanged);
            //
            // lblConnectionStatus
            //
            this.lblConnectionStatus.AutoSize = true;
            this.lblConnectionStatus.Location = new System.Drawing.Point(460, 25);
            this.lblConnectionStatus.Name = "lblConnectionStatus";
            this.lblConnectionStatus.Size = new System.Drawing.Size(80, 15);
            this.lblConnectionStatus.TabIndex = 2;
            this.lblConnectionStatus.Text = "(Yükleniyor...)";
            this.lblConnectionStatus.ForeColor = System.Drawing.Color.Gray;
            //
            // grpConfig
            //
            this.grpConfig.Controls.Add(this.lblDatabaseName);
            this.grpConfig.Controls.Add(this.txtDatabaseName);
            this.grpConfig.Controls.Add(this.lblSchemaName);
            this.grpConfig.Controls.Add(this.txtSchemaName);
            this.grpConfig.Controls.Add(this.lblName);
            this.grpConfig.Controls.Add(this.txtName);
            this.grpConfig.Location = new System.Drawing.Point(12, 75);
            this.grpConfig.Name = "grpConfig";
            this.grpConfig.Size = new System.Drawing.Size(560, 95);
            this.grpConfig.TabIndex = 1;
            this.grpConfig.TabStop = false;
            this.grpConfig.Text = "Configuration";
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
            this.txtDatabaseName.Location = new System.Drawing.Point(80, 25);
            this.txtDatabaseName.Name = "txtDatabaseName";
            this.txtDatabaseName.Size = new System.Drawing.Size(180, 23);
            this.txtDatabaseName.TabIndex = 1;
            this.txtDatabaseName.TextChanged += new System.EventHandler(this.OnConfigChanged);
            //
            // lblSchemaName
            //
            this.lblSchemaName.AutoSize = true;
            this.lblSchemaName.Location = new System.Drawing.Point(280, 28);
            this.lblSchemaName.Name = "lblSchemaName";
            this.lblSchemaName.Size = new System.Drawing.Size(52, 15);
            this.lblSchemaName.TabIndex = 2;
            this.lblSchemaName.Text = "Schema:";
            //
            // txtSchemaName
            //
            this.txtSchemaName.Location = new System.Drawing.Point(340, 25);
            this.txtSchemaName.Name = "txtSchemaName";
            this.txtSchemaName.Size = new System.Drawing.Size(200, 23);
            this.txtSchemaName.TabIndex = 3;
            this.txtSchemaName.TextChanged += new System.EventHandler(this.OnConfigChanged);
            //
            // lblName
            //
            this.lblName.AutoSize = true;
            this.lblName.Location = new System.Drawing.Point(15, 63);
            this.lblName.Name = "lblName";
            this.lblName.Size = new System.Drawing.Size(42, 15);
            this.lblName.TabIndex = 4;
            this.lblName.Text = "Name:";
            //
            // txtName
            //
            this.txtName.Location = new System.Drawing.Point(80, 60);
            this.txtName.Name = "txtName";
            this.txtName.Size = new System.Drawing.Size(460, 23);
            this.txtName.TabIndex = 5;
            //
            // grpValidation
            //
            this.grpValidation.Controls.Add(this.chkMonitoring);
            this.grpValidation.Controls.Add(this.btnValidateAll);
            this.grpValidation.Controls.Add(this.listValidationIssues);
            this.grpValidation.Controls.Add(this.lblValidationStatus);
            this.grpValidation.Location = new System.Drawing.Point(12, 178);
            this.grpValidation.Name = "grpValidation";
            this.grpValidation.Size = new System.Drawing.Size(560, 200);
            this.grpValidation.TabIndex = 5;
            this.grpValidation.TabStop = false;
            this.grpValidation.Text = "Column Name Validation (Glossary Check)";
            //
            // chkMonitoring
            //
            this.chkMonitoring.AutoSize = true;
            this.chkMonitoring.Location = new System.Drawing.Point(15, 25);
            this.chkMonitoring.Name = "chkMonitoring";
            this.chkMonitoring.Size = new System.Drawing.Size(180, 19);
            this.chkMonitoring.TabIndex = 0;
            this.chkMonitoring.Text = "Enable Real-time Monitoring";
            this.chkMonitoring.UseVisualStyleBackColor = true;
            this.chkMonitoring.CheckedChanged += new System.EventHandler(this.ChkMonitoring_CheckedChanged);
            //
            // btnValidateAll
            //
            this.btnValidateAll.Location = new System.Drawing.Point(440, 20);
            this.btnValidateAll.Name = "btnValidateAll";
            this.btnValidateAll.Size = new System.Drawing.Size(100, 28);
            this.btnValidateAll.TabIndex = 1;
            this.btnValidateAll.Text = "Validate All";
            this.btnValidateAll.UseVisualStyleBackColor = true;
            this.btnValidateAll.Click += new System.EventHandler(this.BtnValidateAll_Click);
            //
            // listValidationIssues
            //
            this.listValidationIssues.FullRowSelect = true;
            this.listValidationIssues.GridLines = true;
            this.listValidationIssues.Location = new System.Drawing.Point(15, 55);
            this.listValidationIssues.Name = "listValidationIssues";
            this.listValidationIssues.Size = new System.Drawing.Size(525, 115);
            this.listValidationIssues.TabIndex = 2;
            this.listValidationIssues.UseCompatibleStateImageBehavior = false;
            this.listValidationIssues.View = System.Windows.Forms.View.Details;
            //
            // lblValidationStatus
            //
            this.lblValidationStatus.Location = new System.Drawing.Point(15, 175);
            this.lblValidationStatus.Name = "lblValidationStatus";
            this.lblValidationStatus.Size = new System.Drawing.Size(525, 20);
            this.lblValidationStatus.TabIndex = 3;
            this.lblValidationStatus.Text = "Monitoring: Off";
            this.lblValidationStatus.ForeColor = System.Drawing.Color.Gray;
            //
            // grpDebugLog
            //
            this.grpDebugLog.Controls.Add(this.txtDebugLog);
            this.grpDebugLog.Controls.Add(this.btnCopyLog);
            this.grpDebugLog.Controls.Add(this.btnClearLog);
            this.grpDebugLog.Location = new System.Drawing.Point(12, 385);
            this.grpDebugLog.Name = "grpDebugLog";
            this.grpDebugLog.Size = new System.Drawing.Size(560, 150);
            this.grpDebugLog.TabIndex = 6;
            this.grpDebugLog.TabStop = false;
            this.grpDebugLog.Text = "Debug Log (copy-paste available)";
            //
            // txtDebugLog
            //
            this.txtDebugLog.Location = new System.Drawing.Point(15, 22);
            this.txtDebugLog.Multiline = true;
            this.txtDebugLog.Name = "txtDebugLog";
            this.txtDebugLog.ReadOnly = true;
            this.txtDebugLog.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            this.txtDebugLog.Size = new System.Drawing.Size(445, 115);
            this.txtDebugLog.TabIndex = 0;
            this.txtDebugLog.Font = new System.Drawing.Font("Consolas", 8F);
            this.txtDebugLog.BackColor = System.Drawing.Color.White;
            //
            // btnCopyLog
            //
            this.btnCopyLog.Location = new System.Drawing.Point(470, 22);
            this.btnCopyLog.Name = "btnCopyLog";
            this.btnCopyLog.Size = new System.Drawing.Size(75, 28);
            this.btnCopyLog.TabIndex = 1;
            this.btnCopyLog.Text = "Copy";
            this.btnCopyLog.UseVisualStyleBackColor = true;
            this.btnCopyLog.Click += new System.EventHandler(this.BtnCopyLog_Click);
            //
            // btnClearLog
            //
            this.btnClearLog.Location = new System.Drawing.Point(470, 56);
            this.btnClearLog.Name = "btnClearLog";
            this.btnClearLog.Size = new System.Drawing.Size(75, 28);
            this.btnClearLog.TabIndex = 2;
            this.btnClearLog.Text = "Clear";
            this.btnClearLog.UseVisualStyleBackColor = true;
            this.btnClearLog.Click += new System.EventHandler(this.BtnClearLog_Click);
            //
            // btnApply
            //
            this.btnApply.Location = new System.Drawing.Point(382, 545);
            this.btnApply.Name = "btnApply";
            this.btnApply.Size = new System.Drawing.Size(90, 32);
            this.btnApply.TabIndex = 2;
            this.btnApply.Text = "Apply";
            this.btnApply.UseVisualStyleBackColor = true;
            this.btnApply.Click += new System.EventHandler(this.BtnApply_Click);
            //
            // btnClose
            //
            this.btnClose.Location = new System.Drawing.Point(482, 545);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(90, 32);
            this.btnClose.TabIndex = 3;
            this.btnClose.Text = "Close";
            this.btnClose.UseVisualStyleBackColor = true;
            this.btnClose.Click += new System.EventHandler(this.BtnClose_Click);
            //
            // lblStatus
            //
            this.lblStatus.Location = new System.Drawing.Point(12, 550);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(360, 25);
            this.lblStatus.TabIndex = 4;
            this.lblStatus.Text = "";
            this.lblStatus.ForeColor = System.Drawing.Color.DarkBlue;
            //
            // ModelConfigForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(584, 590);
            this.Controls.Add(this.grpModel);
            this.Controls.Add(this.grpConfig);
            this.Controls.Add(this.grpValidation);
            this.Controls.Add(this.grpDebugLog);
            this.Controls.Add(this.btnApply);
            this.Controls.Add(this.btnClose);
            this.Controls.Add(this.lblStatus);
            this.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "ModelConfigForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Elite Soft Erwin Model Configurator";
            this.Load += new System.EventHandler(this.ModelConfigForm_Load);
            this.grpModel.ResumeLayout(false);
            this.grpModel.PerformLayout();
            this.grpConfig.ResumeLayout(false);
            this.grpConfig.PerformLayout();
            this.grpValidation.ResumeLayout(false);
            this.grpValidation.PerformLayout();
            this.grpDebugLog.ResumeLayout(false);
            this.grpDebugLog.PerformLayout();
            this.ResumeLayout(false);
        }

        #endregion

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
        private System.Windows.Forms.Button btnClose;
        private System.Windows.Forms.Label lblStatus;

        private System.Windows.Forms.GroupBox grpValidation;
        private System.Windows.Forms.CheckBox chkMonitoring;
        private System.Windows.Forms.Button btnValidateAll;
        private System.Windows.Forms.ListView listValidationIssues;
        private System.Windows.Forms.Label lblValidationStatus;

        private System.Windows.Forms.GroupBox grpDebugLog;
        private System.Windows.Forms.TextBox txtDebugLog;
        private System.Windows.Forms.Button btnCopyLog;
        private System.Windows.Forms.Button btnClearLog;
    }
}
