using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn
{
    public partial class ModelConfigForm : Form
    {
        private dynamic _scapi;
        private dynamic _currentModel;
        private dynamic _session;
        private bool _isConnected = false;
        private List<dynamic> _openModels = new List<dynamic>();

        public ModelConfigForm(dynamic scapi)
        {
            _scapi = scapi;
            InitializeComponent();
        }

        private void ModelConfigForm_Load(object sender, EventArgs e)
        {
            LoadOpenModels();
        }

        /// <summary>
        /// Loads all open models into the combo box
        /// </summary>
        private void LoadOpenModels()
        {
            try
            {
                lblConnectionStatus.Text = "(Yükleniyor...)";
                lblConnectionStatus.ForeColor = Color.Gray;
                cmbModels.Items.Clear();
                _openModels.Clear();
                Application.DoEvents();

                // Get persistence units (open models)
                dynamic persistenceUnits = _scapi.PersistenceUnits;

                if (persistenceUnits.Count == 0)
                {
                    cmbModels.Items.Add("(Model bulunamadı)");
                    cmbModels.SelectedIndex = 0;
                    cmbModels.Enabled = false;
                    ShowConnectionError("erwin'de açık model bulunamadı.\nLütfen önce bir model açın.");
                    return;
                }

                // Load all open models
                for (int i = 0; i < persistenceUnits.Count; i++)
                {
                    dynamic model = persistenceUnits.Item(i);
                    _openModels.Add(model);

                    string modelName = GetModelNameFromModel(model);
                    if (string.IsNullOrEmpty(modelName))
                    {
                        modelName = $"(Model {i + 1})";
                    }

                    cmbModels.Items.Add(modelName);
                }

                // Select first model
                if (cmbModels.Items.Count > 0)
                {
                    cmbModels.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                ShowConnectionError($"Modeller yüklenemedi:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Called when user selects a different model from combo box
        /// </summary>
        private void CmbModels_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbModels.SelectedIndex < 0 || cmbModels.SelectedIndex >= _openModels.Count)
                return;

            ConnectToSelectedModel(cmbModels.SelectedIndex);
        }

        /// <summary>
        /// Connects to the selected model
        /// </summary>
        private void ConnectToSelectedModel(int modelIndex)
        {
            try
            {
                // Close existing session if any
                if (_session != null)
                {
                    try { _session.Close(); } catch { }
                }

                _isConnected = false;
                lblConnectionStatus.Text = "(Bağlanıyor...)";
                lblConnectionStatus.ForeColor = Color.Gray;
                EnableControls(false);
                Application.DoEvents();

                // Get selected model
                _currentModel = _openModels[modelIndex];

                // Create session
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                _isConnected = true;

                // Update status
                lblConnectionStatus.Text = "Bağlandı";
                lblConnectionStatus.ForeColor = Color.DarkGreen;

                // Load existing values from Definition field
                LoadExistingValues();

                // Enable controls
                EnableControls(true);
                lblStatus.Text = "Model'e bağlandı.";
                lblStatus.ForeColor = Color.DarkGreen;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                lblConnectionStatus.Text = "Bağlanamadı!";
                lblConnectionStatus.ForeColor = Color.Red;
                EnableControls(false);
                lblStatus.Text = $"Hata: {ex.Message}";
                lblStatus.ForeColor = Color.Red;
            }
        }

        /// <summary>
        /// Gets the model name from a specific model (without session)
        /// </summary>
        private string GetModelNameFromModel(dynamic model)
        {
            try
            {
                // Try different ways to get model name
                try { return model.Name; }
                catch { }

                try { return model.Properties("Name").Value; }
                catch { }

                // Try file path as fallback
                try
                {
                    string path = model.FilePath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        return System.IO.Path.GetFileNameWithoutExtension(path);
                    }
                }
                catch { }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Loads existing values from the model's Definition field
        /// </summary>
        private void LoadExistingValues()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;

                // Find Subject_Area to read Definition from
                dynamic rootObj = null;
                try
                {
                    dynamic saCollection = modelObjects.Collect("Subject_Area");
                    if (saCollection.Count > 0)
                    {
                        rootObj = saCollection.Item(0);
                    }
                }
                catch { }

                if (rootObj == null)
                {
                    try { rootObj = modelObjects.Root; }
                    catch { }
                }

                // Load Name property from the model
                if (rootObj != null)
                {
                    try
                    {
                        string modelName = rootObj.Properties("Name").Value?.ToString();
                        if (!string.IsNullOrEmpty(modelName))
                        {
                            txtName.Text = modelName;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadExistingValues Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows connection error and disables controls
        /// </summary>
        private void ShowConnectionError(string message)
        {
            lblConnectionStatus.Text = "Bağlanamadı!";
            lblConnectionStatus.ForeColor = Color.Red;
            _isConnected = false;
            EnableControls(false);

            lblStatus.Text = "Bağlantı başarısız.";
            lblStatus.ForeColor = Color.Red;

            MessageBox.Show(message, "Bağlantı Hatası",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// Enables or disables input controls
        /// </summary>
        private void EnableControls(bool enabled)
        {
            txtDatabaseName.Enabled = enabled;
            txtSchemaName.Enabled = enabled;
            txtName.Enabled = enabled;
            btnApply.Enabled = enabled;
        }

        /// <summary>
        /// Called when Database Name or Schema Name changes
        /// Auto-fills Name field
        /// </summary>
        private void OnConfigChanged(object sender, EventArgs e)
        {
            string dbName = txtDatabaseName.Text.Trim();
            string schemaName = txtSchemaName.Text.Trim();

            // Auto-fill Name: DatabaseName.SchemaName
            if (!string.IsNullOrEmpty(dbName) && !string.IsNullOrEmpty(schemaName))
            {
                txtName.Text = $"{dbName}.{schemaName}";
            }
            else if (!string.IsNullOrEmpty(dbName))
            {
                txtName.Text = dbName;
            }
            else if (!string.IsNullOrEmpty(schemaName))
            {
                txtName.Text = schemaName;
            }
            else
            {
                txtName.Text = "";
            }
        }

        /// <summary>
        /// Apply button click - saves configuration to Definition field and model Name
        /// </summary>
        private void BtnApply_Click(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("Model'e bağlı değilsiniz.", "Uyarı",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Validate inputs
            if (string.IsNullOrWhiteSpace(txtDatabaseName.Text))
            {
                MessageBox.Show("Database Name boş olamaz.", "Uyarı",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtDatabaseName.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(txtSchemaName.Text))
            {
                MessageBox.Show("Schema Name boş olamaz.", "Uyarı",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtSchemaName.Focus();
                return;
            }

            try
            {
                lblStatus.Text = "Kaydediliyor...";
                lblStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                int transId = _session.BeginNamedTransaction("SaveConfig");

                try
                {
                    dynamic modelObjects = _session.ModelObjects;

                    // Find Subject_Area or Root to save Definition
                    dynamic rootObj = null;
                    try
                    {
                        dynamic saCollection = modelObjects.Collect("Subject_Area");
                        if (saCollection.Count > 0)
                        {
                            rootObj = saCollection.Item(0);
                        }
                    }
                    catch { }

                    if (rootObj == null)
                    {
                        try { rootObj = modelObjects.Root; }
                        catch { }
                    }

                    bool nameSaved = false;

                    // Set the calculated Name to the Model's Name property (General tab)
                    if (rootObj != null && !string.IsNullOrWhiteSpace(txtName.Text))
                    {
                        try
                        {
                            rootObj.Properties("Name").Value = txtName.Text.Trim();
                            nameSaved = true;
                        }
                        catch { }
                    }

                    _session.CommitTransaction(transId);

                    // Save the model
                    bool modelSaved = false;
                    try
                    {
                        _currentModel.Save();
                        modelSaved = true;
                    }
                    catch { }

                    // Show result
                    bool success = nameSaved && modelSaved;

                    if (success)
                    {
                        lblStatus.Text = "Kaydedildi!";
                        lblStatus.ForeColor = Color.DarkGreen;
                        MessageBox.Show($"Name başarıyla kaydedildi!\n\nDatabase: {txtDatabaseName.Text}\nSchema: {txtSchemaName.Text}\nName: {txtName.Text}",
                            "Başarılı", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        lblStatus.Text = "Uyarı!";
                        lblStatus.ForeColor = Color.Orange;

                        string msg = "Değerler kaydedilemedi:\n";
                        if (!nameSaved) msg += "- Name özelliği kaydedilemedi\n";
                        if (!modelSaved) msg += "- Model kaydedilemedi\n";

                        MessageBox.Show(msg, "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Hata!";
                lblStatus.ForeColor = Color.Red;
                MessageBox.Show($"Hata: {ex.Message}", "Hata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Close button click
        /// </summary>
        private void BtnClose_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// Clean up on form close
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            try
            {
                _session?.Close();
                _scapi?.Sessions?.Clear();
            }
            catch { }
        }
    }
}
