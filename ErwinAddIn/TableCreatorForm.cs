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

                        // Try Logical format first (Model.Logical.ES_DatabaseName)
                        if (value == null)
                        {
                            try
                            {
                                value = rootObj.Properties($"Model.Logical.{udpNames[i]}").Value?.ToString();
                            }
                            catch { }
                        }

                        // Try Physical format (Model.Physical.ES_DatabaseName)
                        if (string.IsNullOrEmpty(value))
                        {
                            try
                            {
                                value = rootObj.Properties($"Model.Physical.{udpNames[i]}").Value?.ToString();
                            }
                            catch { }
                        }

                        // Try simple name as fallback
                        if (string.IsNullOrEmpty(value))
                        {
                            try
                            {
                                value = rootObj.Properties(udpNames[i]).Value?.ToString();
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

                bool udpSaved = false;

                // ============================================
                // STEP 0: Explore EAL.dll Types
                // ============================================
                debugInfo.Add("=== EAL.dll Type Exploration ===");

                try
                {
                    // Try to load and explore EAL assembly
                    // EAL.dll is copied to the output directory during build
                    var ealAssembly = System.Reflection.Assembly.LoadFrom(
                        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(
                            System.Reflection.Assembly.GetExecutingAssembly().Location),
                            "EAL.dll"));

                    if (ealAssembly == null)
                    {
                        // Try current directory as fallback
                        ealAssembly = System.Reflection.Assembly.LoadFrom("EAL.dll");
                    }

                    if (ealAssembly != null)
                    {
                        debugInfo.Add($"EAL Assembly: {ealAssembly.FullName}");

                        var types = ealAssembly.GetExportedTypes();
                        debugInfo.Add($"EAL Types ({types.Length}):");

                        foreach (var t in types.Take(30)) // First 30 types
                        {
                            debugInfo.Add($"  - {t.FullName}");
                        }

                        if (types.Length > 30)
                            debugInfo.Add($"  ... and {types.Length - 30} more");
                    }
                }
                catch (Exception ex)
                {
                    debugInfo.Add($"EAL exploration error: {ex.Message}");
                }

                // ============================================
                // STEP 1: Explore SCAPI Type and Available Methods
                // ============================================
                debugInfo.Add("=== SCAPI Type Exploration ===");

                Type scapiType = _scapi.GetType();
                debugInfo.Add($"SCAPI Type: {scapiType.FullName}");

                // List available methods
                try
                {
                    var methods = scapiType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    var methodNames = new HashSet<string>();
                    foreach (var m in methods)
                    {
                        if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") &&
                            !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_"))
                        {
                            methodNames.Add(m.Name);
                        }
                    }
                    debugInfo.Add($"SCAPI Methods: {string.Join(", ", methodNames)}");
                }
                catch (Exception ex)
                {
                    debugInfo.Add($"Method listing error: {ex.Message}");
                }

                // List available properties
                try
                {
                    var props = scapiType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    var propNames = new List<string>();
                    foreach (var p in props)
                    {
                        propNames.Add(p.Name);
                    }
                    debugInfo.Add($"SCAPI Properties: {string.Join(", ", propNames)}");
                }
                catch (Exception ex)
                {
                    debugInfo.Add($"Property listing error: {ex.Message}");
                }

                // ============================================
                // STEP 2: Try PropertyBag approach
                // ============================================
                debugInfo.Add("=== PropertyBag Approach ===");

                // Try to get model object and its PropertyBag
                try
                {
                    // Get model ID
                    int modelId = 0;
                    try { modelId = _currentModel.ObjectId; debugInfo.Add($"Model.ObjectId: {modelId}"); }
                    catch { }

                    if (modelId == 0)
                    {
                        try { modelId = _currentModel.Id; debugInfo.Add($"Model.Id: {modelId}"); }
                        catch { }
                    }

                    // Try GetPropertyBag on SCAPI
                    try
                    {
                        dynamic propBag = _scapi.GetPropertyBag(modelId);
                        debugInfo.Add($"SCAPI.GetPropertyBag({modelId}): OK, Type={propBag?.GetType().Name}");

                        if (propBag != null)
                        {
                            // List PropertyBag methods
                            try
                            {
                                var pbMethods = propBag.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                                var pbMethodNames = new HashSet<string>();
                                foreach (var m in pbMethods)
                                {
                                    if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                                        pbMethodNames.Add(m.Name);
                                }
                                debugInfo.Add($"PropertyBag Methods: {string.Join(", ", pbMethodNames)}");
                            }
                            catch { }

                            // Try to set UDP values
                            for (int i = 0; i < udpNames.Length; i++)
                            {
                                try
                                {
                                    propBag.SetValue($"UDP::{udpNames[i]}", udpValues[i]);
                                    debugInfo.Add($"SetValue(UDP::{udpNames[i]}): OK");
                                    udpSaved = true;
                                }
                                catch (Exception ex)
                                {
                                    debugInfo.Add($"SetValue(UDP::{udpNames[i]}): {ex.Message.Substring(0, Math.Min(40, ex.Message.Length))}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        debugInfo.Add($"SCAPI.GetPropertyBag: {ex.Message.Substring(0, Math.Min(50, ex.Message.Length))}");
                    }
                }
                catch (Exception ex)
                {
                    debugInfo.Add($"PropertyBag approach error: {ex.Message}");
                }

                // ============================================
                // STEP 3: Try CreateUserProperty / SetUserPropertyValue
                // ============================================
                if (!udpSaved)
                {
                    debugInfo.Add("=== CreateUserProperty Approach ===");

                    // Try on SCAPI
                    for (int i = 0; i < udpNames.Length; i++)
                    {
                        try
                        {
                            _scapi.CreateUserProperty(udpNames[i], "Model", "String", "");
                            debugInfo.Add($"SCAPI.CreateUserProperty({udpNames[i]}): OK");
                        }
                        catch (Exception ex)
                        {
                            debugInfo.Add($"CreateUserProperty({udpNames[i]}): {ex.Message.Substring(0, Math.Min(35, ex.Message.Length))}");
                        }
                    }

                    // Try SetUserPropertyValue
                    int modelId = 0;
                    try { modelId = _currentModel.ObjectId; } catch { }
                    if (modelId == 0) try { modelId = _currentModel.Id; } catch { }

                    if (modelId != 0)
                    {
                        for (int i = 0; i < udpNames.Length; i++)
                        {
                            try
                            {
                                _scapi.SetUserPropertyValue(modelId, udpNames[i], udpValues[i]);
                                debugInfo.Add($"SetUserPropertyValue({modelId}, {udpNames[i]}): OK");
                                udpSaved = true;
                            }
                            catch (Exception ex)
                            {
                                debugInfo.Add($"SetUserPropertyValue: {ex.Message.Substring(0, Math.Min(35, ex.Message.Length))}");
                            }
                        }
                    }
                }

                // ============================================
                // STEP 4: Try UDPManager
                // ============================================
                if (!udpSaved)
                {
                    debugInfo.Add("=== UDPManager Approach ===");

                    try
                    {
                        dynamic udpManager = _scapi.UDPManager;
                        debugInfo.Add($"SCAPI.UDPManager: OK, Type={udpManager?.GetType().Name}");

                        if (udpManager != null)
                        {
                            // List UDPManager methods
                            try
                            {
                                var umMethods = udpManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                                var umMethodNames = new HashSet<string>();
                                foreach (var m in umMethods)
                                {
                                    if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                                        umMethodNames.Add(m.Name);
                                }
                                debugInfo.Add($"UDPManager Methods: {string.Join(", ", umMethodNames)}");
                            }
                            catch { }

                            // Try to set values
                            for (int i = 0; i < udpNames.Length; i++)
                            {
                                try
                                {
                                    udpManager.SetValue(udpNames[i], udpValues[i]);
                                    debugInfo.Add($"UDPManager.SetValue({udpNames[i]}): OK");
                                    udpSaved = true;
                                }
                                catch { }

                                try
                                {
                                    udpManager.SetProperty(udpNames[i], udpValues[i]);
                                    debugInfo.Add($"UDPManager.SetProperty({udpNames[i]}): OK");
                                    udpSaved = true;
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        debugInfo.Add($"UDPManager: {ex.Message.Substring(0, Math.Min(40, ex.Message.Length))}");
                    }
                }

                // ============================================
                // STEP 5: Try Session-based approaches
                // ============================================
                debugInfo.Add("=== Session Approach ===");

                try { _session.Close(); } catch { }
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                // Explore session type
                Type sessionType = _session.GetType();
                debugInfo.Add($"Session Type: {sessionType.FullName}");

                try
                {
                    var sMethods = sessionType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    var sMethodNames = new HashSet<string>();
                    foreach (var m in sMethods)
                    {
                        if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") &&
                            !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_"))
                        {
                            sMethodNames.Add(m.Name);
                        }
                    }
                    debugInfo.Add($"Session Methods: {string.Join(", ", sMethodNames)}");
                }
                catch { }

                // ============================================
                // STEP 6: Try M1 Level UDP Creation
                // ============================================
                if (!udpSaved)
                {
                    debugInfo.Add("=== M1 Level UDP Creation ===");

                    bool udpsCreated = CreateUDPDefinitionsAtM1Level(debugInfo);

                    if (udpsCreated)
                    {
                        // Try to set values on Subject_Area
                        try { _session.Close(); } catch { }
                        _session = _scapi.Sessions.Add();
                        _session.Open(_currentModel);

                        int transId = _session.BeginNamedTransaction("SetUDPValues");

                        try
                        {
                            dynamic modelObjects = _session.ModelObjects;

                            // Try different ways to get Subject_Area
                            dynamic subjectArea = null;

                            // Method 1: Collect Subject_Area
                            try
                            {
                                dynamic saCollection = modelObjects.Collect("Subject_Area");
                                int saCount = saCollection.Count;
                                debugInfo.Add($"Subject_Area count: {saCount}");

                                if (saCount > 0)
                                {
                                    subjectArea = saCollection.Item(0);
                                    string saName = "";
                                    try { saName = subjectArea.Properties("Name").Value?.ToString() ?? ""; }
                                    catch { }
                                    debugInfo.Add($"Subject_Area[0]: {saName}");
                                }
                            }
                            catch (Exception ex)
                            {
                                debugInfo.Add($"Collect Subject_Area: {ex.Message}");
                            }

                            // Method 2: Try Root
                            if (subjectArea == null)
                            {
                                try
                                {
                                    subjectArea = modelObjects.Root;
                                    debugInfo.Add($"Using Root: {subjectArea?.GetType().Name}");
                                }
                                catch (Exception ex)
                                {
                                    debugInfo.Add($"Root: {ex.Message}");
                                }
                            }

                            // Method 3: Try Model object
                            if (subjectArea == null)
                            {
                                try
                                {
                                    dynamic modelCollection = modelObjects.Collect("Model");
                                    if (modelCollection.Count > 0)
                                    {
                                        subjectArea = modelCollection.Item(0);
                                        debugInfo.Add($"Using Model[0]");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    debugInfo.Add($"Collect Model: {ex.Message}");
                                }
                            }

                            if (subjectArea != null)
                            {
                                // List available properties on Subject_Area
                                try
                                {
                                    dynamic props = subjectArea.Properties;
                                    int propCount = props.Count;
                                    debugInfo.Add($"Subject_Area has {propCount} properties");

                                    // Try different ways to list property names
                                    var propNamesList = new List<string>();
                                    var udpFound = new List<string>();

                                    for (int p = 0; p < Math.Min(propCount, 100); p++)
                                    {
                                        try
                                        {
                                            dynamic prop = props.Item(p);

                                            // Try different ways to get property name
                                            string pName = null;
                                            try { pName = prop.Name; } catch { }
                                            if (pName == null) try { pName = prop.PropertyName; } catch { }
                                            if (pName == null) try { pName = prop.ToString(); } catch { }
                                            if (pName == null) pName = $"[{p}]";

                                            // Check if this is one of our UDPs
                                            if (pName != null && pName.Contains("ES_"))
                                            {
                                                udpFound.Add(pName);
                                            }

                                            if (p < 20) propNamesList.Add(pName);
                                        }
                                        catch { }
                                    }

                                    debugInfo.Add($"SA Props (first 20): {string.Join(", ", propNamesList)}");

                                    if (udpFound.Count > 0)
                                    {
                                        debugInfo.Add($"Found ES_ UDPs: {string.Join(", ", udpFound)}");
                                    }
                                    else
                                    {
                                        debugInfo.Add("No ES_ UDPs found in properties!");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    debugInfo.Add($"List props: {ex.Message}");
                                }

                                // Try to check if UDP properties exist by name
                                debugInfo.Add("Checking UDP property existence:");
                                foreach (var udpName in udpNames)
                                {
                                    try
                                    {
                                        dynamic udpProp = subjectArea.Properties(udpName);
                                        if (udpProp != null)
                                        {
                                            string currentVal = udpProp.Value?.ToString() ?? "(null)";
                                            debugInfo.Add($"  {udpName}: EXISTS, current='{currentVal}'");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        debugInfo.Add($"  {udpName}: NOT FOUND ({ex.Message.Substring(0, Math.Min(30, ex.Message.Length))})");
                                    }
                                }

                                // Try to set UDP values - Logical side
                                debugInfo.Add("=== Setting Logical UDP Values ===");
                                for (int i = 0; i < udpNames.Length; i++)
                                {
                                    try
                                    {
                                        string logicalFullName = $"Model.Logical.{udpNames[i]}";
                                        subjectArea.Properties(logicalFullName).Value = udpValues[i];
                                        debugInfo.Add($"Logical {udpNames[i]}: OK");
                                        udpSaved = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        debugInfo.Add($"Logical {udpNames[i]}: {ex.Message.Substring(0, Math.Min(30, ex.Message.Length))}");
                                    }
                                }

                                // Try to set UDP values - Physical side
                                debugInfo.Add("=== Setting Physical UDP Values ===");
                                for (int i = 0; i < udpNames.Length; i++)
                                {
                                    bool physicalSet = false;

                                    // Method 1: Try Model.Physical.PropertyName format on Root
                                    try
                                    {
                                        string physicalFullName = $"Model.Physical.{udpNames[i]}";
                                        subjectArea.Properties(physicalFullName).Value = udpValues[i];
                                        debugInfo.Add($"Physical {udpNames[i]} (Root): OK");
                                        udpSaved = true;
                                        physicalSet = true;
                                    }
                                    catch { }

                                    // Method 2: Try on Table collection (Physical model uses Tables)
                                    if (!physicalSet)
                                    {
                                        try
                                        {
                                            dynamic tables = modelObjects.Collect("Table");
                                            if (tables != null && tables.Count > 0)
                                            {
                                                // Try on first table's parent or model context
                                                dynamic table = tables.Item(0);
                                                string physicalFullName = $"Model.Physical.{udpNames[i]}";
                                                table.Properties(physicalFullName).Value = udpValues[i];
                                                debugInfo.Add($"Physical {udpNames[i]} (Table): OK");
                                                udpSaved = true;
                                                physicalSet = true;
                                            }
                                        }
                                        catch { }
                                    }

                                    // Method 3: Try accessing Physical_Model or Stored_Display
                                    if (!physicalSet)
                                    {
                                        try
                                        {
                                            dynamic storedDisplays = modelObjects.Collect("Stored_Display");
                                            if (storedDisplays != null && storedDisplays.Count > 0)
                                            {
                                                for (int sd = 0; sd < storedDisplays.Count; sd++)
                                                {
                                                    dynamic storedDisplay = storedDisplays.Item(sd);
                                                    string sdName = "";
                                                    try { sdName = storedDisplay.Properties("Name").Value?.ToString() ?? ""; }
                                                    catch { }

                                                    // Check if this is Physical display
                                                    if (sdName.ToLower().Contains("physical"))
                                                    {
                                                        try
                                                        {
                                                            string physicalFullName = $"Model.Physical.{udpNames[i]}";
                                                            storedDisplay.Properties(physicalFullName).Value = udpValues[i];
                                                            debugInfo.Add($"Physical {udpNames[i]} (StoredDisplay): OK");
                                                            udpSaved = true;
                                                            physicalSet = true;
                                                            break;
                                                        }
                                                        catch { }
                                                    }
                                                }
                                            }
                                        }
                                        catch { }
                                    }

                                    if (!physicalSet)
                                    {
                                        debugInfo.Add($"Physical {udpNames[i]}: All methods failed");
                                    }
                                }
                            }
                            else
                            {
                                debugInfo.Add("No Subject_Area found!");
                            }

                            _session.CommitTransaction(transId);
                        }
                        catch (Exception ex)
                        {
                            try { _session.RollbackTransaction(transId); } catch { }
                            debugInfo.Add($"M1 set values error: {ex.Message}");
                        }

                        // Try opening Physical-only session and setting values there
                        debugInfo.Add("=== Physical Session Approach ===");
                        try
                        {
                            try { _session.Close(); } catch { }

                            // Try different session levels for Physical access
                            // SCD_SL_PHYSICAL = 2 might be the Physical session level
                            const int SCD_SL_PHYSICAL = 2;

                            dynamic physicalSession = _scapi.Sessions.Add();

                            // Try opening with Physical session level
                            try
                            {
                                physicalSession.Open(_currentModel, SCD_SL_PHYSICAL);
                                debugInfo.Add($"Physical session (level {SCD_SL_PHYSICAL}): Opened");

                                int physTransId = physicalSession.BeginNamedTransaction("SetPhysicalUDPs");
                                dynamic physModelObjects = physicalSession.ModelObjects;
                                dynamic physRoot = physModelObjects.Root;

                                debugInfo.Add($"Physical Root type: {physRoot?.GetType().Name}");

                                // List properties to see if Physical UDPs are visible
                                try
                                {
                                    int physPropCount = physRoot.Properties.Count;
                                    debugInfo.Add($"Physical Root has {physPropCount} properties");
                                }
                                catch { }

                                // Try setting Physical UDP values
                                for (int i = 0; i < udpNames.Length; i++)
                                {
                                    try
                                    {
                                        string physicalFullName = $"Model.Physical.{udpNames[i]}";
                                        physRoot.Properties(physicalFullName).Value = udpValues[i];
                                        debugInfo.Add($"PhysSession {udpNames[i]}: OK");
                                        udpSaved = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        debugInfo.Add($"PhysSession {udpNames[i]}: {ex.Message.Substring(0, Math.Min(25, ex.Message.Length))}");
                                    }
                                }

                                physicalSession.CommitTransaction(physTransId);
                                physicalSession.Close();
                            }
                            catch (Exception ex)
                            {
                                debugInfo.Add($"Physical session error: {ex.Message.Substring(0, Math.Min(40, ex.Message.Length))}");
                                try { physicalSession?.Close(); } catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            debugInfo.Add($"Physical approach error: {ex.Message}");
                        }

                        // Re-open normal session for subsequent operations
                        _session = _scapi.Sessions.Add();
                        _session.Open(_currentModel);
                    }
                }

                // ============================================
                // STEP 7: Always save to Definition field as backup
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
                bool success = udpSaved || definitionSaved;
                string resultMsg = "";

                if (udpSaved)
                    resultMsg += "UDP değerleri kaydedildi!\n";
                if (definitionSaved)
                    resultMsg += "Definition alanına kaydedildi.\n";
                if (modelSaved)
                    resultMsg += "Model kaydedildi.\n";

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
        /// Creates UDP definitions at M1 (metadata) level using Property_Type
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
                try
                {
                    dynamic propTypes = null;
                    try { propTypes = modelObjects.Collect("Property_Type"); }
                    catch { propTypes = modelObjects.Collect("Property Type"); }

                    if (propTypes != null && propTypes.Count > 0)
                    {
                        debugInfo.Add($"Found {propTypes.Count} Property_Type objects");
                        for (int i = 0; i < Math.Min(propTypes.Count, 10); i++)
                        {
                            try
                            {
                                dynamic pt = propTypes.Item(i);
                                string ptName = pt.Properties("Name").Value?.ToString() ?? "(no name)";
                                debugInfo.Add($"  [{i}]: {ptName}");

                                // Try to get owner type
                                string ownerType = "";
                                try { ownerType = pt.Properties("tag_Udp_Owner_Type").Value?.ToString(); }
                                catch { }
                                if (string.IsNullOrEmpty(ownerType))
                                {
                                    try { ownerType = pt.Properties("Udp_Owner_Type").Value?.ToString(); }
                                    catch { }
                                }
                                if (!string.IsNullOrEmpty(ownerType))
                                {
                                    debugInfo.Add($"       Owner: {ownerType}");
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

                // UDP definitions to create - using "Model" as owner type
                // Create BOTH Logical and Physical versions so UDPs appear in both views
                var udpDefs = new[]
                {
                    // Logical UDPs - appear when viewing Logical model
                    new { Name = "ES_DatabaseName", OwnerType = "Model", IsLogical = true },
                    new { Name = "ES_SchemaName", OwnerType = "Model", IsLogical = true },
                    new { Name = "ES_FullName", OwnerType = "Model", IsLogical = true },
                    new { Name = "ES_Code", OwnerType = "Model", IsLogical = true },
                    // Physical UDPs - appear when viewing Physical model
                    new { Name = "ES_DatabaseName", OwnerType = "Model", IsLogical = false },
                    new { Name = "ES_SchemaName", OwnerType = "Model", IsLogical = false },
                    new { Name = "ES_FullName", OwnerType = "Model", IsLogical = false },
                    new { Name = "ES_Code", OwnerType = "Model", IsLogical = false }
                };

                int created = 0;
                int order = 100;

                foreach (var udpDef in udpDefs)
                {
                    try
                    {
                        // Check if UDP already exists (check for exact match with Model owner)
                        bool exists = false;
                        string modelSideCheck = udpDef.IsLogical ? "Logical" : "Physical";
                        string expectedFullName = $"{udpDef.OwnerType}.{modelSideCheck}.{udpDef.Name}";
                        try
                        {
                            dynamic propTypes = null;
                            try { propTypes = modelObjects.Collect("Property_Type"); }
                            catch { propTypes = modelObjects.Collect("Property Type"); }

                            if (propTypes != null)
                            {
                                for (int i = 0; i < propTypes.Count; i++)
                                {
                                    dynamic pt = propTypes.Item(i);
                                    string ptName = pt.Properties("Name").Value?.ToString() ?? "";
                                    if (ptName == expectedFullName)
                                    {
                                        exists = true;
                                        debugInfo.Add($"UDP {udpDef.Name}: Already exists as {ptName}");
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }

                        if (!exists)
                        {
                            dynamic newUDP = null;
                            try { newUDP = modelObjects.Add("Property_Type"); }
                            catch
                            {
                                try { newUDP = modelObjects.Add("Property Type"); }
                                catch { }
                            }

                            if (newUDP != null)
                            {
                                string modelSide = udpDef.IsLogical ? "Logical" : "Physical";
                                string fullName = $"{udpDef.OwnerType}.{modelSide}.{udpDef.Name}";
                                newUDP.Properties("Name").Value = fullName;

                                try { newUDP.Properties("tag_Udp_Owner_Type").Value = udpDef.OwnerType; }
                                catch { try { newUDP.Properties("Udp_Owner_Type").Value = udpDef.OwnerType; } catch { } }

                                try { newUDP.Properties("tag_Is_Logical").Value = udpDef.IsLogical; }
                                catch { try { newUDP.Properties("IsLogical").Value = udpDef.IsLogical; } catch { } }

                                try { newUDP.Properties("tag_Udp_Data_Type").Value = 2; } // Text
                                catch { try { newUDP.Properties("Udp_Data_Type").Value = 2; } catch { } }

                                try { newUDP.Properties("tag_Order").Value = order.ToString(); }
                                catch { try { newUDP.Properties("Order").Value = order.ToString(); } catch { } }

                                order++;
                                created++;
                                debugInfo.Add($"UDP {udpDef.Name}: Created");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        debugInfo.Add($"UDP {udpDef.Name}: {ex.Message.Substring(0, Math.Min(40, ex.Message.Length))}");
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
