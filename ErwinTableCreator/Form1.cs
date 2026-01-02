using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ErwinTest1
{
    public partial class Form1 : Form
    {
        private dynamic oApplication;
        private dynamic oCurrentModel;
        private string modelFilePath;
        private Button btnOpenModel;
        private Button btnCreateTable;
        private TextBox txtTableName;
        private Label lblTableName;
        private Label lblStatus;
        private Label lblModel;

        public Form1()
        {
            InitializeComponent();
            SetupCustomControls();
            InitializeSCAPI();
        }

        private void SetupCustomControls()
        {
            this.Text = "Elite Softe - Erwin Addon";
            this.Size = new Size(420, 250);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            // Open Model button
            btnOpenModel = new Button();
            btnOpenModel.Text = "Open Model...";
            btnOpenModel.Location = new Point(20, 20);
            btnOpenModel.Size = new Size(120, 30);
            btnOpenModel.Click += BtnOpenModel_Click;
            this.Controls.Add(btnOpenModel);

            // Model label
            lblModel = new Label();
            lblModel.Text = "No model loaded";
            lblModel.Location = new Point(150, 27);
            lblModel.Size = new Size(240, 20);
            lblModel.ForeColor = Color.Gray;
            this.Controls.Add(lblModel);

            // Label for table name
            lblTableName = new Label();
            lblTableName.Text = "Table Name:";
            lblTableName.Location = new Point(20, 70);
            lblTableName.Size = new Size(80, 23);
            this.Controls.Add(lblTableName);

            // TextBox for table name input
            txtTableName = new TextBox();
            txtTableName.Location = new Point(110, 67);
            txtTableName.Size = new Size(280, 23);
            this.Controls.Add(txtTableName);

            // Button to create table
            btnCreateTable = new Button();
            btnCreateTable.Text = "Create Table";
            btnCreateTable.Location = new Point(110, 110);
            btnCreateTable.Size = new Size(120, 35);
            btnCreateTable.Click += BtnCreateTable_Click;
            btnCreateTable.Enabled = false;
            this.Controls.Add(btnCreateTable);

            // Status label
            lblStatus = new Label();
            lblStatus.Text = "";
            lblStatus.Location = new Point(20, 160);
            lblStatus.Size = new Size(370, 50);
            lblStatus.ForeColor = Color.DarkBlue;
            this.Controls.Add(lblStatus);
        }

        private void InitializeSCAPI()
        {
            try
            {
                Type scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                if (scapiType != null)
                {
                    oApplication = Activator.CreateInstance(scapiType);
                    lblStatus.Text = "SCAPI Ready. Click 'Open Model' to load .erwin file";
                    lblStatus.ForeColor = Color.DarkGreen;
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                lblStatus.ForeColor = Color.Red;
                btnOpenModel.Enabled = false;
            }
        }

        private void BtnOpenModel_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "erwin Files (*.erwin)|*.erwin|All Files (*.*)|*.*";
                ofd.Title = "Select erwin Model";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Save file path for later save operation
                        modelFilePath = ofd.FileName;

                        // Open model via SCAPI (RDO=No for write access)
                        oCurrentModel = oApplication.PersistenceUnits.Add("erwin://" + ofd.FileName, "RDO=No");

                        lblModel.Text = System.IO.Path.GetFileName(ofd.FileName);
                        lblModel.ForeColor = Color.DarkGreen;
                        lblStatus.Text = "Model loaded! Enter table name and click Create.";
                        lblStatus.ForeColor = Color.DarkGreen;
                        btnCreateTable.Enabled = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error opening model: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        lblStatus.Text = "Error: " + ex.Message;
                        lblStatus.ForeColor = Color.Red;
                    }
                }
            }
        }

        private void BtnCreateTable_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtTableName.Text))
            {
                MessageBox.Show("Please enter a table name.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (oCurrentModel == null)
            {
                MessageBox.Show("Please open a model first.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                CreateTable(txtTableName.Text.Trim());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error: " + ex.Message;
                lblStatus.ForeColor = Color.Red;
            }
        }

        private void CreateTable(string tableName)
        {
            dynamic oSession = null;
            dynamic oSessions = null;
            int transId = 0;

            try
            {
                oSessions = oApplication.Sessions;
                oSession = oSessions.Add();
                oSession.Open(oCurrentModel);

                // Begin NAMED transaction - this is the correct SCAPI pattern!
                transId = oSession.BeginNamedTransaction("CreateEntity");

                dynamic oModelObjects = oSession.ModelObjects;

                // Add new entity
                dynamic oNewEntity = oModelObjects.Add("Entity");

                if (oNewEntity != null)
                {
                    // Set entity name using Properties("Name").Value pattern
                    bool nameSet = false;
                    try
                    {
                        oNewEntity.Properties("Name").Value = tableName;
                        nameSet = true;
                    }
                    catch
                    {
                        try { oNewEntity.Name = tableName; nameSet = true; }
                        catch { }
                    }

                    // Commit transaction with transaction ID
                    bool committed = false;
                    try
                    {
                        oSession.CommitTransaction(transId);
                        committed = true;
                    }
                    catch
                    {
                        try { oSession.CommitTransaction(); committed = true; }
                        catch { }
                    }

                    // Close session before saving
                    oSession.Close();

                    // Save model back to file
                    bool saved = false;
                    try
                    {
                        oCurrentModel.Save("erwin://" + modelFilePath, "");
                        saved = true;
                    }
                    catch
                    {
                        try
                        {
                            oCurrentModel.Save(modelFilePath, "");
                            saved = true;
                        }
                        catch
                        {
                            try { oCurrentModel.Save(); saved = true; }
                            catch { }
                        }
                    }

                    string msg = "Entity '" + tableName + "' created!";
                    if (!nameSet) msg = "Entity created (name may not be set)";
                    if (!committed) msg += " (commit issue)";
                    if (saved) msg += " Model saved.";

                    lblStatus.Text = msg;
                    lblStatus.ForeColor = Color.DarkGreen;
                    MessageBox.Show(msg + "\n\nDeğişiklikleri görmek için erwin'de modeli kapatıp tekrar açın.", "Success");
                    txtTableName.Clear();
                }
                else
                {
                    oSession.RollbackTransaction(transId);
                    oSession.Close();
                    lblStatus.Text = "Failed to create entity";
                    lblStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (transId != 0) oSession?.RollbackTransaction(transId);
                    oSession?.Close();
                }
                catch { }

                MessageBox.Show("Error: " + ex.Message, "Error");
                lblStatus.Text = "Error: " + ex.Message;
                lblStatus.ForeColor = Color.Red;
            }
            finally
            {
                try { oSessions?.Clear(); } catch { }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (oCurrentModel != null)
            {
                try { oApplication.PersistenceUnits.Remove(oCurrentModel); } catch { }
            }
            oApplication = null;
        }
    }
}
