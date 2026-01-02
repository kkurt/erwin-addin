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

            this.grpModel.SuspendLayout();
            this.grpConfig.SuspendLayout();
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
            // btnApply
            //
            this.btnApply.Location = new System.Drawing.Point(382, 180);
            this.btnApply.Name = "btnApply";
            this.btnApply.Size = new System.Drawing.Size(90, 32);
            this.btnApply.TabIndex = 2;
            this.btnApply.Text = "Apply";
            this.btnApply.UseVisualStyleBackColor = true;
            this.btnApply.Click += new System.EventHandler(this.BtnApply_Click);
            //
            // btnClose
            //
            this.btnClose.Location = new System.Drawing.Point(482, 180);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(90, 32);
            this.btnClose.TabIndex = 3;
            this.btnClose.Text = "Close";
            this.btnClose.UseVisualStyleBackColor = true;
            this.btnClose.Click += new System.EventHandler(this.BtnClose_Click);
            //
            // lblStatus
            //
            this.lblStatus.Location = new System.Drawing.Point(12, 185);
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
            this.ClientSize = new System.Drawing.Size(584, 226);
            this.Controls.Add(this.grpModel);
            this.Controls.Add(this.grpConfig);
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
    }
}
