using System;
using System.Drawing;
using System.Windows.Forms;

namespace ErwinAddIn
{
    public partial class TableCreatorForm : Form
    {
        private dynamic oApplication;

        public TableCreatorForm(dynamic scapi)
        {
            oApplication = scapi;
            InitializeComponent();
        }

        private void BtnCreate_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtTableName.Text))
            {
                MessageBox.Show("Please enter a table name.", "Warning",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                CreateEntity(txtTableName.Text.Trim());
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                lblStatus.ForeColor = Color.Red;
                MessageBox.Show("Error: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CreateEntity(string tableName)
        {
            // Get active model from erwin
            dynamic persistenceUnits = oApplication.PersistenceUnits;

            if (persistenceUnits.Count == 0)
            {
                MessageBox.Show("No model is open in erwin. Please open a model first.",
                    "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Get the first (active) model
            dynamic oModel = persistenceUnits.Item(0);

            // Create session
            dynamic oSessions = oApplication.Sessions;
            dynamic oSession = oSessions.Add();
            oSession.Open(oModel);

            try
            {
                // Begin transaction
                int transId = oSession.BeginNamedTransaction("CreateEntity");

                // Add entity
                dynamic oModelObjects = oSession.ModelObjects;
                dynamic oNewEntity = oModelObjects.Add("Entity");

                if (oNewEntity != null)
                {
                    // Set name
                    try { oNewEntity.Properties("Name").Value = tableName; }
                    catch { try { oNewEntity.Name = tableName; } catch { } }

                    // Commit
                    oSession.CommitTransaction(transId);

                    lblStatus.Text = "Entity '" + tableName + "' created!";
                    lblStatus.ForeColor = Color.DarkGreen;
                    MessageBox.Show("Entity '" + tableName + "' created successfully!\n\n" +
                        "You may need to refresh the model view.", "Success");
                    txtTableName.Clear();
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                lblStatus.ForeColor = Color.Red;
                throw;
            }
            finally
            {
                oSession.Close();
                oSessions.Clear();
            }
        }
    }
}
