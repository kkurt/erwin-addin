namespace EliteSoft.Erwin.AddIn.Forms
{
    partial class CompareVersionsForm
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.Label lblBaselineCaption;
        private System.Windows.Forms.Label lblBaseline;
        private System.Windows.Forms.Label lblTargetCaption;
        private System.Windows.Forms.ComboBox cmbTargetVersion;
        private System.Windows.Forms.Label lblDialectCaption;
        private System.Windows.Forms.Label lblDialect;
        private System.Windows.Forms.Button btnCompare;
        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.SplitContainer splitResults;
        private System.Windows.Forms.ListView lvChanges;
        private System.Windows.Forms.ColumnHeader colKind;
        private System.Windows.Forms.ColumnHeader colClass;
        private System.Windows.Forms.ColumnHeader colName;
        private System.Windows.Forms.ColumnHeader colDetail;
        private System.Windows.Forms.TextBox txtAlterSql;
        private System.Windows.Forms.Label lblAlterSqlCaption;
        private System.Windows.Forms.Button btnSaveSql;
        private System.Windows.Forms.Button btnClose;
        private System.Windows.Forms.Label lblStatus;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.lblBaselineCaption = new System.Windows.Forms.Label();
            this.lblBaseline = new System.Windows.Forms.Label();
            this.lblTargetCaption = new System.Windows.Forms.Label();
            this.cmbTargetVersion = new System.Windows.Forms.ComboBox();
            this.lblDialectCaption = new System.Windows.Forms.Label();
            this.lblDialect = new System.Windows.Forms.Label();
            this.btnCompare = new System.Windows.Forms.Button();
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.splitResults = new System.Windows.Forms.SplitContainer();
            this.lvChanges = new System.Windows.Forms.ListView();
            this.colKind = new System.Windows.Forms.ColumnHeader();
            this.colClass = new System.Windows.Forms.ColumnHeader();
            this.colName = new System.Windows.Forms.ColumnHeader();
            this.colDetail = new System.Windows.Forms.ColumnHeader();
            this.txtAlterSql = new System.Windows.Forms.TextBox();
            this.lblAlterSqlCaption = new System.Windows.Forms.Label();
            this.btnSaveSql = new System.Windows.Forms.Button();
            this.btnClose = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();
            ((System.ComponentModel.ISupportInitialize)(this.splitResults)).BeginInit();
            this.splitResults.Panel1.SuspendLayout();
            this.splitResults.Panel2.SuspendLayout();
            this.splitResults.SuspendLayout();
            this.SuspendLayout();
            //
            // lblBaselineCaption
            //
            this.lblBaselineCaption.AutoSize = true;
            this.lblBaselineCaption.Location = new System.Drawing.Point(12, 15);
            this.lblBaselineCaption.Name = "lblBaselineCaption";
            this.lblBaselineCaption.Size = new System.Drawing.Size(66, 15);
            this.lblBaselineCaption.Text = "Baseline:";
            //
            // lblBaseline
            //
            this.lblBaseline.AutoSize = true;
            this.lblBaseline.Location = new System.Drawing.Point(110, 15);
            this.lblBaseline.Name = "lblBaseline";
            this.lblBaseline.Size = new System.Drawing.Size(300, 15);
            this.lblBaseline.Text = "Active Model (loading...)";
            this.lblBaseline.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            //
            // lblTargetCaption
            //
            this.lblTargetCaption.AutoSize = true;
            this.lblTargetCaption.Location = new System.Drawing.Point(12, 45);
            this.lblTargetCaption.Name = "lblTargetCaption";
            this.lblTargetCaption.Size = new System.Drawing.Size(54, 15);
            this.lblTargetCaption.Text = "Target version:";
            //
            // cmbTargetVersion
            //
            this.cmbTargetVersion.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbTargetVersion.Location = new System.Drawing.Point(110, 42);
            this.cmbTargetVersion.Name = "cmbTargetVersion";
            this.cmbTargetVersion.Size = new System.Drawing.Size(300, 23);
            //
            // lblDialectCaption
            //
            this.lblDialectCaption.AutoSize = true;
            this.lblDialectCaption.Location = new System.Drawing.Point(12, 75);
            this.lblDialectCaption.Name = "lblDialectCaption";
            this.lblDialectCaption.Size = new System.Drawing.Size(50, 15);
            this.lblDialectCaption.Text = "Dialect:";
            //
            // lblDialect
            //
            this.lblDialect.AutoSize = true;
            this.lblDialect.Location = new System.Drawing.Point(110, 75);
            this.lblDialect.Name = "lblDialect";
            this.lblDialect.Size = new System.Drawing.Size(300, 15);
            this.lblDialect.Text = "(detecting from active model...)";
            //
            // btnCompare
            //
            this.btnCompare.Location = new System.Drawing.Point(440, 40);
            this.btnCompare.Name = "btnCompare";
            this.btnCompare.Size = new System.Drawing.Size(130, 30);
            this.btnCompare.Text = "Compare";
            this.btnCompare.UseVisualStyleBackColor = true;
            this.btnCompare.Click += new System.EventHandler(this.btnCompare_Click);
            //
            // progressBar
            //
            this.progressBar.Location = new System.Drawing.Point(440, 75);
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(130, 16);
            this.progressBar.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            this.progressBar.MarqueeAnimationSpeed = 0;
            this.progressBar.Visible = false;
            //
            // splitResults
            //
            this.splitResults.Dock = System.Windows.Forms.DockStyle.None;
            this.splitResults.Location = new System.Drawing.Point(12, 110);
            this.splitResults.Name = "splitResults";
            this.splitResults.Orientation = System.Windows.Forms.Orientation.Horizontal;
            this.splitResults.Panel1.Controls.Add(this.lvChanges);
            this.splitResults.Panel2.Controls.Add(this.lblAlterSqlCaption);
            this.splitResults.Panel2.Controls.Add(this.txtAlterSql);
            this.splitResults.Size = new System.Drawing.Size(760, 420);
            this.splitResults.SplitterDistance = 200;
            this.splitResults.TabIndex = 8;
            //
            // lvChanges
            //
            this.lvChanges.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
                this.colKind,
                this.colClass,
                this.colName,
                this.colDetail});
            this.colKind.Text = "Kind";
            this.colKind.Width = 140;
            this.colClass.Text = "Class";
            this.colClass.Width = 110;
            this.colName.Text = "Name";
            this.colName.Width = 180;
            this.colDetail.Text = "Detail";
            this.colDetail.Width = 300;
            this.lvChanges.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lvChanges.FullRowSelect = true;
            this.lvChanges.GridLines = true;
            this.lvChanges.HideSelection = false;
            this.lvChanges.UseCompatibleStateImageBehavior = false;
            this.lvChanges.View = System.Windows.Forms.View.Details;
            //
            // lblAlterSqlCaption
            //
            this.lblAlterSqlCaption.AutoSize = true;
            this.lblAlterSqlCaption.Dock = System.Windows.Forms.DockStyle.Top;
            this.lblAlterSqlCaption.Padding = new System.Windows.Forms.Padding(2);
            this.lblAlterSqlCaption.Text = "Alter SQL:";
            //
            // txtAlterSql
            //
            this.txtAlterSql.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtAlterSql.Font = new System.Drawing.Font("Consolas", 9.5F);
            this.txtAlterSql.Multiline = true;
            this.txtAlterSql.ReadOnly = true;
            this.txtAlterSql.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            this.txtAlterSql.WordWrap = false;
            //
            // btnSaveSql
            //
            this.btnSaveSql.Location = new System.Drawing.Point(540, 545);
            this.btnSaveSql.Name = "btnSaveSql";
            this.btnSaveSql.Size = new System.Drawing.Size(120, 30);
            this.btnSaveSql.Text = "Save Alter SQL...";
            this.btnSaveSql.UseVisualStyleBackColor = true;
            this.btnSaveSql.Enabled = false;
            this.btnSaveSql.Click += new System.EventHandler(this.btnSaveSql_Click);
            //
            // btnClose
            //
            this.btnClose.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnClose.Location = new System.Drawing.Point(670, 545);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(100, 30);
            this.btnClose.Text = "Close";
            this.btnClose.UseVisualStyleBackColor = true;
            //
            // lblStatus
            //
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(12, 550);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(400, 15);
            this.lblStatus.Text = "Ready.";
            //
            // CompareVersionsForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.btnClose;
            this.ClientSize = new System.Drawing.Size(784, 586);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnClose);
            this.Controls.Add(this.btnSaveSql);
            this.Controls.Add(this.splitResults);
            this.Controls.Add(this.progressBar);
            this.Controls.Add(this.btnCompare);
            this.Controls.Add(this.lblDialect);
            this.Controls.Add(this.lblDialectCaption);
            this.Controls.Add(this.cmbTargetVersion);
            this.Controls.Add(this.lblTargetCaption);
            this.Controls.Add(this.lblBaseline);
            this.Controls.Add(this.lblBaselineCaption);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            this.MinimumSize = new System.Drawing.Size(700, 500);
            this.Name = "CompareVersionsForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Compare Active Model vs Mart Version";
            this.splitResults.Panel1.ResumeLayout(false);
            this.splitResults.Panel2.ResumeLayout(false);
            this.splitResults.Panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitResults)).EndInit();
            this.splitResults.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
