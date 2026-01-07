using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn
{
    public partial class ModelConfigForm : Form
    {
        private dynamic _scapi;
        private dynamic _currentModel;
        private dynamic _session;
        private bool _isConnected = false;
        private List<dynamic> _openModels = new List<dynamic>();

        // Validation service
        private ColumnValidationService _validationService;

        public ModelConfigForm(dynamic scapi)
        {
            _scapi = scapi;
            InitializeComponent();
            InitializeValidationUI();
        }

        /// <summary>
        /// Initialize validation ListView columns
        /// </summary>
        private void InitializeValidationUI()
        {
            listValidationIssues.Columns.Add("Table", 120);
            listValidationIssues.Columns.Add("Column", 120);
            listValidationIssues.Columns.Add("Physical Name", 140);
            listValidationIssues.Columns.Add("Issue", 140);

            chkMonitoring.Enabled = false;
            btnValidateAll.Enabled = false;
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

                // Initialize validation service
                InitializeValidationService();
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
        /// Initialize validation service for the connected session
        /// </summary>
        private void InitializeValidationService()
        {
            // Dispose previous service if exists
            _validationService?.Dispose();

            _validationService = new ColumnValidationService(_session);

            // Subscribe to validation events
            _validationService.OnValidationFailed += OnValidationFailed;
            _validationService.OnColumnChanged += OnColumnChanged;

            // Enable validation controls
            chkMonitoring.Enabled = true;
            btnValidateAll.Enabled = true;

            // Take initial snapshot
            _validationService.TakeSnapshot();

            lblValidationStatus.Text = "Ready - Click 'Validate All' or enable monitoring";
            lblValidationStatus.ForeColor = Color.DarkBlue;
        }

        /// <summary>
        /// Called when a column name fails validation (real-time alert)
        /// </summary>
        private void OnValidationFailed(object sender, ColumnValidationEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnValidationFailed(sender, e)));
                return;
            }

            // Add to issues list
            var item = new ListViewItem(e.TableName);
            item.SubItems.Add(e.AttributeName);
            item.SubItems.Add(e.PhysicalName);
            item.SubItems.Add(e.ValidationMessage);
            item.ForeColor = Color.Red;
            listValidationIssues.Items.Insert(0, item);

            // Pause monitoring while showing popup
            _validationService?.StopMonitoring();

            // Show immediate warning
            string message = $"Column Name Validation Warning!\n\n" +
                            $"Table: {e.TableName}\n" +
                            $"Column: {e.AttributeName}\n" +
                            $"Physical Name: {e.PhysicalName}\n\n" +
                            $"Issue: {e.ValidationMessage}";

            MessageBox.Show(message, "Column Validation Warning",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            // After popup closed, change the column name to "PLEASE CHANGE IT"
            ChangeColumnPhysicalName(e.TableName, e.AttributeName, "PLEASE CHANGE IT");

            // Resume monitoring after popup is closed
            if (chkMonitoring.Checked)
            {
                _validationService?.StartMonitoring();
            }

            // Update status
            lblValidationStatus.Text = $"Last issue: {e.PhysicalName} - {e.ValidationMessage}";
            lblValidationStatus.ForeColor = Color.Red;
        }

        /// <summary>
        /// Changes the Physical_Name of a column by searching Entity/Attribute
        /// </summary>
        private void ChangeColumnPhysicalName(string tableName, string attributeName, string newName)
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Find the entity
                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    // Check entity name (logical or physical)
                    string entityName = "";
                    string entityPhysName = "";
                    try { entityName = entity.Name ?? ""; } catch { }
                    try { entityPhysName = entity.Properties("Physical_Name").Value?.ToString() ?? ""; } catch { }

                    // Match by physical name or logical name
                    bool entityMatch = entityPhysName.Equals(tableName, StringComparison.OrdinalIgnoreCase) ||
                                      entityName.Equals(tableName, StringComparison.OrdinalIgnoreCase);
                    if (!entityMatch) continue;

                    // Find the attribute
                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                    if (entityAttrs == null) continue;

                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string attrName = "";
                        try { attrName = attr.Name ?? ""; } catch { }

                        if (attrName.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Found the attribute - change its Physical_Name
                            int transId = _session.BeginNamedTransaction("ChangeColumnName");

                            try
                            {
                                attr.Properties("Physical_Name").Value = newName;
                                _session.CommitTransaction(transId);

                                lblValidationStatus.Text = $"Column renamed to '{newName}'";
                                lblValidationStatus.ForeColor = Color.Orange;
                                return; // Done
                            }
                            catch
                            {
                                try { _session.RollbackTransaction(transId); } catch { }
                                throw;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChangeColumnPhysicalName error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when any column changes (for logging/status)
        /// </summary>
        private void OnColumnChanged(object sender, ColumnChangeEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnColumnChanged(sender, e)));
                return;
            }

            string changeType = e.IsNew ? "New" : "Changed";
            lblValidationStatus.Text = $"[{DateTime.Now:HH:mm:ss}] {changeType}: {e.TableName}.{e.NewPhysicalName}";
        }

        /// <summary>
        /// Toggle real-time monitoring
        /// </summary>
        private void ChkMonitoring_CheckedChanged(object sender, EventArgs e)
        {
            if (_validationService == null) return;

            if (chkMonitoring.Checked)
            {
                _validationService.StartMonitoring();
                lblValidationStatus.Text = "Monitoring: Active (checking every 2 seconds)";
                lblValidationStatus.ForeColor = Color.DarkGreen;
            }
            else
            {
                _validationService.StopMonitoring();
                lblValidationStatus.Text = "Monitoring: Off";
                lblValidationStatus.ForeColor = Color.Gray;
            }
        }

        /// <summary>
        /// Validate all columns on-demand
        /// </summary>
        private void BtnValidateAll_Click(object sender, EventArgs e)
        {
            if (_validationService == null) return;

            listValidationIssues.Items.Clear();

            var issues = _validationService.ValidateAllColumns();

            foreach (var issue in issues)
            {
                var item = new ListViewItem(issue.TableName);
                item.SubItems.Add(issue.AttributeName);
                item.SubItems.Add(issue.PhysicalName);
                item.SubItems.Add(issue.Issue);
                item.ForeColor = Color.Red;
                listValidationIssues.Items.Add(item);
            }

            if (issues.Count == 0)
            {
                lblValidationStatus.Text = "Validation passed - All columns have 'col_' prefix";
                lblValidationStatus.ForeColor = Color.DarkGreen;
                MessageBox.Show("All column names are valid!\nAll columns have the required 'col_' prefix.",
                    "Validation Passed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                lblValidationStatus.Text = $"Validation failed - {issues.Count} issue(s) found";
                lblValidationStatus.ForeColor = Color.Red;
                MessageBox.Show($"Found {issues.Count} column(s) without 'col_' prefix.\nSee the list for details.",
                    "Validation Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                // Dispose validation service
                _validationService?.Dispose();
                _validationService = null;

                _session?.Close();
                _scapi?.Sessions?.Clear();
            }
            catch { }
        }
    }
}
