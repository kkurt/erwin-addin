using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ErwinAddIn
{
    public partial class TableCreatorForm : Form
    {
        private dynamic _scapi;
        private dynamic _currentModel;
        private dynamic _session;
        private bool _isConnected = false;
        private List<dynamic> _openModels = new List<dynamic>();

        public TableCreatorForm(dynamic scapi)
        {
            _scapi = scapi;
            InitializeComponent();
        }

        private void TableCreatorForm_Load(object sender, EventArgs e)
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

                if (rootObj != null)
                {
                    // Try to load UDP values - try multiple formats
                    string[] udpNames = { "ES_DatabaseName", "ES_SchemaName", "ES_FullName", "ES_Code" };
                    TextBox[] textBoxes = { txtDatabaseName, txtSchemaName, txtName, txtCode };

                    for (int i = 0; i < udpNames.Length; i++)
                    {
                        string value = null;

                        // Try direct property name first (simple UDP)
                        if (value == null)
                        {
                            try
                            {
                                value = rootObj.Properties(udpNames[i]).Value?.ToString();
                            }
                            catch { }
                        }

                        // Try Logical format (Subject_Area.Logical.ES_DatabaseName)
                        if (string.IsNullOrEmpty(value))
                        {
                            try
                            {
                                value = rootObj.Properties($"Subject_Area.Logical.{udpNames[i]}").Value?.ToString();
                            }
                            catch { }
                        }

                        // Try Physical format (Subject_Area.Physical.ES_DatabaseName)
                        if (string.IsNullOrEmpty(value))
                        {
                            try
                            {
                                value = rootObj.Properties($"Subject_Area.Physical.{udpNames[i]}").Value?.ToString();
                            }
                            catch { }
                        }

                        if (!string.IsNullOrEmpty(value))
                        {
                            textBoxes[i].Text = value;
                        }
                    }

                    // Load Definition field as fallback
                    if (string.IsNullOrEmpty(txtDatabaseName.Text))
                    {
                        string definition = null;
                        try
                        {
                            definition = rootObj.Properties("Definition").Value?.ToString();
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(definition))
                        {
                            var values = ParseDefinitionString(definition);

                            if (values.TryGetValue("DatabaseName", out string dbName))
                                txtDatabaseName.Text = dbName;

                            if (values.TryGetValue("SchemaName", out string schemaName))
                                txtSchemaName.Text = schemaName;

                            if (values.TryGetValue("FullName", out string fullName))
                                txtName.Text = fullName;

                            if (values.TryGetValue("Code", out string code))
                                txtCode.Text = code;
                        }
                    }
                }

                // Try to get model name if not loaded
                if (string.IsNullOrEmpty(txtName.Text))
                {
                    string modelName = GetModelNameFromModel(_currentModel);
                    if (!string.IsNullOrEmpty(modelName))
                    {
                        txtName.Text = modelName;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadExistingValues Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a definition string in format key1=value1;key2=value2;...
        /// </summary>
        private Dictionary<string, string> ParseDefinitionString(string definition)
        {
            var result = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(definition))
                return result;

            var pairs = definition.Split(';');
            foreach (var pair in pairs)
            {
                var parts = pair.Split(new[] { '=' }, 2);
                if (parts.Length == 2)
                {
                    result[parts[0].Trim()] = parts[1].Trim();
                }
            }

            return result;
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
            txtCode.Enabled = enabled;
            btnApply.Enabled = enabled;
        }

        /// <summary>
        /// Called when Database Name or Schema Name changes
        /// Auto-fills Name and Code fields
        /// </summary>
        private void OnConfigChanged(object sender, EventArgs e)
        {
            string dbName = txtDatabaseName.Text.Trim();
            string schemaName = txtSchemaName.Text.Trim();

            // Auto-fill Name: DatabaseName.SchemaName
            if (!string.IsNullOrEmpty(dbName) && !string.IsNullOrEmpty(schemaName))
            {
                txtName.Text = $"{dbName}.{schemaName}";
                txtCode.Text = $"{dbName}_{schemaName}";
            }
            else if (!string.IsNullOrEmpty(dbName))
            {
                txtName.Text = dbName;
                txtCode.Text = dbName;
            }
            else if (!string.IsNullOrEmpty(schemaName))
            {
                txtName.Text = schemaName;
                txtCode.Text = schemaName;
            }
            else
            {
                txtName.Text = "";
                txtCode.Text = "";
            }
        }

        // Session level constants for SCAPI
        private const int SCD_SL_M1 = 1;  // Metadata level (for creating UDP definitions)
        private const int SCD_SL_M0 = 0;  // Normal level (default)

        /// <summary>
        /// Apply button click - saves configuration using multiple approaches
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

            var debugInfo = new List<string>();

            try
            {
                lblStatus.Text = "Kaydediliyor...";
                lblStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                // UDP names and values
                string[] udpNames = { "ES_DatabaseName", "ES_SchemaName", "ES_FullName", "ES_Code" };
                string[] udpValues = {
                    txtDatabaseName.Text.Trim(),
                    txtSchemaName.Text.Trim(),
                    txtName.Text.Trim(),
                    txtCode.Text.Trim()
                };

                bool logicalSaved = false;
                bool physicalSaved = false;

                // ============================================
                // STEP 1: Create UDP Definitions at M1 Level
                // ============================================
                debugInfo.Add("=== M1 Level UDP Creation ===");
                bool udpsCreated = CreateUDPDefinitionsAtM1Level(debugInfo);

                // ============================================
                // STEP 2: Set Logical UDP Values
                // ============================================
                debugInfo.Add("=== Setting Logical UDP Values ===");
                
                try { _session.Close(); } catch { }
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                int logicalTransId = _session.BeginNamedTransaction("SetLogicalUDPs");

                try
                {
                    dynamic modelObjects = _session.ModelObjects;
                    dynamic subjectArea = GetSubjectArea(modelObjects, debugInfo);

                    if (subjectArea != null)
                    {
                        for (int i = 0; i < udpNames.Length; i++)
                        {
                            // Try multiple property name formats for Logical
                            string[] logicalFormats = {
                                $"Subject_Area.Logical.{udpNames[i]}",
                                $"Model.Logical.{udpNames[i]}",
                                udpNames[i]  // Direct property name
                            };

                            foreach (var format in logicalFormats)
                            {
                                try
                                {
                                    subjectArea.Properties(format).Value = udpValues[i];
                                    debugInfo.Add($"Logical {udpNames[i]} ({format}): OK");
                                    logicalSaved = true;
                                    break;
                                }
                                catch { }
                            }
                        }
                    }

                    _session.CommitTransaction(logicalTransId);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(logicalTransId); } catch { }
                    debugInfo.Add($"Logical UDP error: {ex.Message}");
                }

                // ============================================
                // STEP 3: Set Physical UDP Values
                // ============================================
                debugInfo.Add("=== Setting Physical UDP Values ===");

                try { _session.Close(); } catch { }
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                int physicalTransId = _session.BeginNamedTransaction("SetPhysicalUDPs");

                try
                {
                    dynamic modelObjects = _session.ModelObjects;
                    dynamic subjectArea = GetSubjectArea(modelObjects, debugInfo);

                    if (subjectArea != null)
                    {
                        for (int i = 0; i < udpNames.Length; i++)
                        {
                            // Try multiple property name formats for Physical
                            // KEY FIX: Use Subject_Area as owner, not Stored_Display
                            string[] physicalFormats = {
                                $"Subject_Area.Physical.{udpNames[i]}",
                                $"Model.Physical.{udpNames[i]}"
                            };

                            bool thisPropertySet = false;
                            foreach (var format in physicalFormats)
                            {
                                try
                                {
                                    subjectArea.Properties(format).Value = udpValues[i];
                                    debugInfo.Add($"Physical {udpNames[i]} ({format}): OK");
                                    physicalSaved = true;
                                    thisPropertySet = true;
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    debugInfo.Add($"Physical {udpNames[i]} ({format}): {ex.Message.Substring(0, Math.Min(30, ex.Message.Length))}");
                                }
                            }

                            // If still not set, try on Stored_Display objects
                            if (!thisPropertySet)
                            {
                                try
                                {
                                    dynamic storedDisplays = modelObjects.Collect("Stored_Display");
                                    if (storedDisplays != null && storedDisplays.Count > 0)
                                    {
                                        debugInfo.Add($"Trying {storedDisplays.Count} Stored_Display objects...");
                                        
                                        for (int sd = 0; sd < storedDisplays.Count; sd++)
                                        {
                                            try
                                            {
                                                dynamic storedDisplay = storedDisplays.Item(sd);
                                                
                                                // Try direct property name on Stored_Display
                                                storedDisplay.Properties(udpNames[i]).Value = udpValues[i];
                                                debugInfo.Add($"Physical {udpNames[i]} (Stored_Display[{sd}]): OK");
                                                physicalSaved = true;
                                                thisPropertySet = true;
                                                break;
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (!thisPropertySet)
                            {
                                debugInfo.Add($"Physical {udpNames[i]}: Could not set with any method");
                            }
                        }
                    }

                    _session.CommitTransaction(physicalTransId);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(physicalTransId); } catch { }
                    debugInfo.Add($"Physical UDP error: {ex.Message}");
                }

                // ============================================
                // STEP 4: Save to Definition field as backup
                // ============================================
                debugInfo.Add("=== Definition Field ===");

                bool definitionSaved = false;
                try { _session.Close(); } catch { }
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                int defTransId = _session.BeginNamedTransaction("SaveDefinition");

                try
                {
                    dynamic modelObjects = _session.ModelObjects;

                    string configData = $"DatabaseName={txtDatabaseName.Text.Trim()};" +
                                       $"SchemaName={txtSchemaName.Text.Trim()};" +
                                       $"FullName={txtName.Text.Trim()};" +
                                       $"Code={txtCode.Text.Trim()}";

                    dynamic rootObj = GetSubjectArea(modelObjects, null);

                    if (rootObj != null)
                    {
                        try
                        {
                            rootObj.Properties("Definition").Value = configData;
                            definitionSaved = true;
                            debugInfo.Add("Definition: OK");
                        }
                        catch (Exception ex)
                        {
                            debugInfo.Add($"Definition: {ex.Message}");
                        }
                    }

                    _session.CommitTransaction(defTransId);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(defTransId); } catch { }
                    debugInfo.Add($"Definition error: {ex.Message}");
                }

                // Try to save the model
                bool modelSaved = false;
                try
                {
                    _currentModel.Save();
                    modelSaved = true;
                    debugInfo.Add("Model saved: OK");
                }
                catch (Exception ex)
                {
                    debugInfo.Add($"Model save: {ex.Message}");
                }

                // Show result
                bool success = logicalSaved || physicalSaved || definitionSaved;
                string resultMsg = "";

                if (logicalSaved)
                    resultMsg += "✓ Logical UDP değerleri kaydedildi!\n";
                else
                    resultMsg += "✗ Logical UDP değerleri kaydedilemedi!\n";
                    
                if (physicalSaved)
                    resultMsg += "✓ Physical UDP değerleri kaydedildi!\n";
                else
                    resultMsg += "✗ Physical UDP değerleri kaydedilemedi!\n";
                    
                if (definitionSaved)
                    resultMsg += "✓ Definition alanına kaydedildi.\n";
                if (modelSaved)
                    resultMsg += "✓ Model kaydedildi.\n";

                resultMsg += "\n--- Debug ---\n" + string.Join("\n", debugInfo);

                lblStatus.Text = success ? "Kaydedildi!" : "Hata!";
                lblStatus.ForeColor = success ? Color.DarkGreen : Color.Red;

                MessageBox.Show(resultMsg, success ? "Sonuç" : "Uyarı",
                    MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Hata!";
                lblStatus.ForeColor = Color.Red;
                debugInfo.Add($"Exception: {ex.Message}");
                MessageBox.Show($"Hata: {ex.Message}\n\n--- Debug ---\n{string.Join("\n", debugInfo)}", "Hata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Gets Subject_Area from model objects
        /// </summary>
        private dynamic GetSubjectArea(dynamic modelObjects, List<string> debugInfo)
        {
            dynamic subjectArea = null;

            // Method 1: Collect Subject_Area
            try
            {
                dynamic saCollection = modelObjects.Collect("Subject_Area");
                if (saCollection.Count > 0)
                {
                    subjectArea = saCollection.Item(0);
                    debugInfo?.Add($"Found Subject_Area (count: {saCollection.Count})");
                }
            }
            catch { }

            // Method 2: Try Root
            if (subjectArea == null)
            {
                try
                {
                    subjectArea = modelObjects.Root;
                    debugInfo?.Add("Using Root object");
                }
                catch { }
            }

            return subjectArea;
        }

        /// <summary>
        /// Creates UDP definitions at M1 (metadata) level using Property_Type
        /// KEY FIX: Both Logical and Physical UDPs use Subject_Area as owner type
        /// </summary>
        private bool CreateUDPDefinitionsAtM1Level(List<string> debugInfo)
        {
            dynamic m1Session = null;
            try
            {
                // Open session at M1 (metadata) level
                m1Session = _scapi.Sessions.Add();
                m1Session.Open(_currentModel, SCD_SL_M1);
                debugInfo.Add("M1 session opened: OK");

                int transId = m1Session.BeginNamedTransaction("CreateUDPs");
                dynamic modelObjects = m1Session.ModelObjects;

                // First, list ALL existing Property_Type objects to understand structure
                debugInfo.Add("Listing existing Property_Types:");
                var existingUDPs = new HashSet<string>();
                try
                {
                    dynamic propTypes = null;
                    try { propTypes = modelObjects.Collect("Property_Type"); }
                    catch { propTypes = modelObjects.Collect("Property Type"); }

                    if (propTypes != null && propTypes.Count > 0)
                    {
                        debugInfo.Add($"Found {propTypes.Count} Property_Type objects");
                        for (int i = 0; i < propTypes.Count; i++)
                        {
                            try
                            {
                                dynamic pt = propTypes.Item(i);
                                string ptName = pt.Properties("Name").Value?.ToString() ?? "";
                                existingUDPs.Add(ptName);
                                
                                if (i < 10 || ptName.Contains("ES_"))
                                {
                                    debugInfo.Add($"  [{i}]: {ptName}");
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        debugInfo.Add("No Property_Type objects found!");
                    }
                }
                catch (Exception ex)
                {
                    debugInfo.Add($"List Property_Types error: {ex.Message}");
                }

                // UDP definitions to create
                // KEY FIX: Both Logical and Physical use "Subject_Area" as owner type
                // The difference is in the IsLogical flag
                var udpDefs = new[]
                {
                    // Logical UDPs - Subject_Area owner, IsLogical = true
                    new { Name = "ES_DatabaseName", OwnerType = "Subject_Area", IsLogical = true },
                    new { Name = "ES_SchemaName", OwnerType = "Subject_Area", IsLogical = true },
                    new { Name = "ES_FullName", OwnerType = "Subject_Area", IsLogical = true },
                    new { Name = "ES_Code", OwnerType = "Subject_Area", IsLogical = true },
                    // Physical UDPs - Subject_Area owner, IsLogical = false
                    new { Name = "ES_DatabaseName", OwnerType = "Subject_Area", IsLogical = false },
                    new { Name = "ES_SchemaName", OwnerType = "Subject_Area", IsLogical = false },
                    new { Name = "ES_FullName", OwnerType = "Subject_Area", IsLogical = false },
                    new { Name = "ES_Code", OwnerType = "Subject_Area", IsLogical = false }
                };

                int created = 0;
                int order = 100;

                foreach (var udpDef in udpDefs)
                {
                    try
                    {
                        string modelSide = udpDef.IsLogical ? "Logical" : "Physical";
                        string fullName = $"{udpDef.OwnerType}.{modelSide}.{udpDef.Name}";

                        // Check if UDP already exists
                        if (existingUDPs.Contains(fullName))
                        {
                            debugInfo.Add($"UDP {fullName}: Already exists");
                            continue;
                        }

                        dynamic newUDP = null;
                        try { newUDP = modelObjects.Add("Property_Type"); }
                        catch
                        {
                            try { newUDP = modelObjects.Add("Property Type"); }
                            catch { }
                        }

                        if (newUDP != null)
                        {
                            newUDP.Properties("Name").Value = fullName;

                            // Set owner type
                            try { newUDP.Properties("tag_Udp_Owner_Type").Value = udpDef.OwnerType; }
                            catch { try { newUDP.Properties("Udp_Owner_Type").Value = udpDef.OwnerType; } catch { } }

                            // Set Logical/Physical flag - THIS IS THE KEY DIFFERENCE
                            try { newUDP.Properties("tag_Is_Logical").Value = udpDef.IsLogical; }
                            catch { try { newUDP.Properties("Is_Logical").Value = udpDef.IsLogical; } catch { } }

                            // Set data type (2 = Text/String)
                            try { newUDP.Properties("tag_Udp_Data_Type").Value = 2; }
                            catch { try { newUDP.Properties("Udp_Data_Type").Value = 2; } catch { } }

                            // Set order
                            try { newUDP.Properties("tag_Order").Value = order.ToString(); }
                            catch { try { newUDP.Properties("Order").Value = order.ToString(); } catch { } }

                            order++;
                            created++;
                            debugInfo.Add($"UDP {fullName}: Created");
                        }
                    }
                    catch (Exception ex)
                    {
                        debugInfo.Add($"UDP create error: {ex.Message.Substring(0, Math.Min(40, ex.Message.Length))}");
                    }
                }

                m1Session.CommitTransaction(transId);
                m1Session.Close();
                debugInfo.Add($"M1 UDPs committed: {created} created");
                return true;
            }
            catch (Exception ex)
            {
                debugInfo.Add($"M1 Session error: {ex.Message}");
                try { m1Session?.Close(); } catch { }
                return false;
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
