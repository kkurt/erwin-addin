using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn
{
    /// <summary>
    /// Main configuration form for the Elite Soft Erwin Add-In.
    /// Provides model selection, configuration, glossary management, and validation features.
    /// </summary>
    public partial class ModelConfigForm : Form
    {
        #region Constants

        private const int GlossaryRefreshIntervalMs = 60000; // 1 minute
        private const string StatusConnected = "Connected";
        private const string StatusDisconnected = "Disconnected";
        private const string StatusConnecting = "Connecting...";
        private const string StatusLoading = "Loading...";

        #endregion

        #region Fields

        private readonly dynamic _scapi;
        private dynamic _currentModel;
        private dynamic _session;
        private bool _isConnected;
        private readonly List<dynamic> _openModels = new List<dynamic>();

        // Services
        private ColumnValidationService _validationService;
        private TableTypeMonitorService _tableTypeMonitorService;

        // State tracking
        private Timer _glossaryRefreshTimer;
        private DateTime? _lastGlossaryRefreshTime;

        #endregion

        #region Constructor

        public ModelConfigForm(dynamic scapi)
        {
            _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
            InitializeComponent();
            InitializeValidationUI();
            InitializeGlossaryRefreshTimer();
        }

        #endregion

        #region Form Lifecycle

        private void ModelConfigForm_Load(object sender, EventArgs e)
        {
            LoadOpenModels();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            CleanupResources();
        }

        #endregion

        #region Model Management

        private void LoadOpenModels()
        {
            try
            {
                UpdateConnectionStatus(StatusLoading, Color.Gray);
                cmbModels.Items.Clear();
                _openModels.Clear();
                Application.DoEvents();

                dynamic persistenceUnits = _scapi.PersistenceUnits;

                if (persistenceUnits.Count == 0)
                {
                    cmbModels.Items.Add("(No models found)");
                    cmbModels.SelectedIndex = 0;
                    cmbModels.Enabled = false;
                    ShowError("No open models found in erwin.\nPlease open a model first.", "Connection Error");
                    return;
                }

                for (int i = 0; i < persistenceUnits.Count; i++)
                {
                    dynamic model = persistenceUnits.Item(i);
                    _openModels.Add(model);

                    string modelName = GetModelName(model) ?? $"(Model {i + 1})";
                    cmbModels.Items.Add(modelName);
                }

                if (cmbModels.Items.Count > 0)
                {
                    cmbModels.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to load models:\n{ex.Message}", "Connection Error");
            }
        }

        private void CmbModels_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbModels.SelectedIndex < 0 || cmbModels.SelectedIndex >= _openModels.Count)
                return;

            ConnectToModel(cmbModels.SelectedIndex);
        }

        private void ConnectToModel(int modelIndex)
        {
            try
            {
                CloseCurrentSession();
                _isConnected = false;
                UpdateConnectionStatus(StatusConnecting, Color.Gray);
                EnableControls(false);
                Application.DoEvents();

                _currentModel = _openModels[modelIndex];
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                _isConnected = true;
                UpdateConnectionStatus(StatusConnected, Color.DarkGreen);
                LoadExistingValues();
                EnableControls(true);
                UpdateStatus("Connected to model.", Color.DarkGreen);

                InitializeValidationService();
            }
            catch (Exception ex)
            {
                _isConnected = false;
                UpdateConnectionStatus(StatusDisconnected, Color.Red);
                EnableControls(false);
                UpdateStatus($"Error: {ex.Message}", Color.Red);
            }
        }

        private string GetModelName(dynamic model)
        {
            try
            {
                try { return model.Name; } catch { }
                try { return model.Properties("Name").Value; } catch { }
                try
                {
                    string path = model.FilePath;
                    if (!string.IsNullOrEmpty(path))
                        return System.IO.Path.GetFileNameWithoutExtension(path);
                }
                catch { }
                return null;
            }
            catch { return null; }
        }

        #endregion

        #region Validation Service

        private void InitializeValidationService()
        {
            Log("Initializing validation service...");

            DisposeServices();
            LoadGlossary();
            LoadTableTypes();
            EnsureTableTypeUdpExists();

            _validationService = new ColumnValidationService(_session);
            btnValidateAll.Enabled = true;

            // Initialize TABLE_TYPE monitor service
            _tableTypeMonitorService = new TableTypeMonitorService(_session);
            _tableTypeMonitorService.OnLog += Log;
            _tableTypeMonitorService.TakeSnapshot();
            _tableTypeMonitorService.StartMonitoring();

            UpdateValidationStatus();
            Log("Validation service initialized.");
        }

        private void InitializeValidationUI()
        {
            listColumnValidation.Columns.Add("Table", 150);
            listColumnValidation.Columns.Add("Physical Name", 250);
            listColumnValidation.Columns.Add("Status", 80);

            listTableValidation.Columns.Add("Table", 250);
            listTableValidation.Columns.Add("Type Selected", 100);

            btnValidateAll.Enabled = false;
        }

        private void BtnValidateAll_Click(object sender, EventArgs e)
        {
            if (_validationService == null) return;

            listColumnValidation.Items.Clear();
            listTableValidation.Items.Clear();

            var allResults = _validationService.ValidateAllColumns();
            var columnResults = allResults.Where(r => r.RuleName != "TableTypeRule").ToList();
            var tableResults = allResults.Where(r => r.RuleName == "TableTypeRule").ToList();

            PopulateColumnValidationResults(columnResults);
            PopulateTableValidationResults(tableResults);
            ShowValidationSummary(columnResults, tableResults);
        }

        private void PopulateColumnValidationResults(List<ColumnValidationIssue> results)
        {
            // Sort: valid first, then invalid
            var sortedResults = results.OrderByDescending(r => r.IsValid).ThenBy(r => r.TableName);

            foreach (var col in sortedResults)
            {
                var item = new ListViewItem(col.TableName);
                item.SubItems.Add(col.PhysicalName);
                item.SubItems.Add(col.IsValid ? "\u2713" : "\u2717");
                item.ForeColor = col.IsValid ? Color.DarkGreen : Color.Red;
                listColumnValidation.Items.Add(item);
            }
        }

        private void PopulateTableValidationResults(List<ColumnValidationIssue> results)
        {
            // Sort: valid first, then invalid
            var sortedResults = results.OrderByDescending(r => r.IsValid).ThenBy(r => r.TableName);

            foreach (var tbl in sortedResults)
            {
                var item = new ListViewItem(tbl.TableName);
                item.SubItems.Add(tbl.IsValid ? "\u2713" : "\u2717");
                item.ForeColor = tbl.IsValid ? Color.DarkGreen : Color.Red;
                listTableValidation.Items.Add(item);
            }
        }

        private void ShowValidationSummary(List<ColumnValidationIssue> columnResults, List<ColumnValidationIssue> tableResults)
        {
            int invalidColumns = columnResults.Count(r => !r.IsValid);
            int invalidTables = tableResults.Count(r => !r.IsValid);
            int validColumns = columnResults.Count(r => r.IsValid);
            int validTables = tableResults.Count(r => r.IsValid);

            if (invalidColumns == 0 && invalidTables == 0)
            {
                lblValidationStatus.Text = $"All validations passed - {validColumns} columns, {validTables} tables OK";
                lblValidationStatus.ForeColor = Color.DarkGreen;
                MessageBox.Show(
                    $"All validations passed!\n\nColumns: {validColumns} found in glossary\nTables: All tables have type selected",
                    "Validation Passed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                lblValidationStatus.Text = $"Validation: {invalidColumns} column errors, {invalidTables} table errors";
                lblValidationStatus.ForeColor = Color.Red;

                string message = "";
                if (invalidColumns > 0) message += $"Column Errors: {invalidColumns} not found in glossary\n";
                if (invalidTables > 0) message += $"Table Errors: {invalidTables} tables without type selected\n";
                message += "\nSee the tabs for details.";

                MessageBox.Show(message, "Validation Result", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateValidationStatus()
        {
            var glossary = GlossaryService.Instance;
            if (glossary.IsLoaded)
            {
                lblValidationStatus.Text = $"Ready - Glossary: {glossary.Count} entries. Click 'Validate All' to check.";
                lblValidationStatus.ForeColor = Color.DarkGreen;
            }
            else
            {
                lblValidationStatus.Text = "Warning: Glossary not loaded";
                lblValidationStatus.ForeColor = Color.Orange;
            }
        }

        #endregion

        #region Glossary Management

        private void InitializeGlossaryRefreshTimer()
        {
            _glossaryRefreshTimer = new Timer { Interval = GlossaryRefreshIntervalMs };
            _glossaryRefreshTimer.Tick += GlossaryRefreshTimer_Tick;
            _glossaryRefreshTimer.Start();
            Log("Glossary auto-refresh timer started (every 1 minute)");
        }

        private void GlossaryRefreshTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                RefreshGlossarySilently();
            }
            catch (Exception ex)
            {
                Log($"Glossary refresh error: {ex.Message}");
            }
        }

        private void RefreshGlossarySilently()
        {
            if (!DatabaseService.Instance.IsConfigured) return;

            GlossaryService.Instance.Reload();
            _lastGlossaryRefreshTime = DateTime.Now;
            UpdateLastRefreshLabel();

            if (GlossaryService.Instance.IsLoaded)
            {
                Log($"Glossary auto-refreshed: {GlossaryService.Instance.Count} entries");
            }
        }

        private void UpdateLastRefreshLabel()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateLastRefreshLabel));
                return;
            }

            if (_lastGlossaryRefreshTime.HasValue)
            {
                lblLastRefreshValue.Text = _lastGlossaryRefreshTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                lblLastRefreshValue.ForeColor = Color.DarkGreen;
            }
        }

        private void BtnTestConnection_Click(object sender, EventArgs e)
        {
            try
            {
                lblGlossaryStatus.Text = "Testing glossary connection...";
                lblGlossaryStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                if (!DatabaseService.Instance.IsConfigured)
                {
                    lblGlossaryStatus.Text = "Repository database not configured. Please configure in ErwinAdmin.";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    return;
                }

                var glossaryConnService = GlossaryConnectionService.Instance;
                glossaryConnService.ClearCache();
                glossaryConnService.LoadConnectionDef();

                if (!glossaryConnService.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Failed to load GLOSSARY_CONNECTION_DEF: {glossaryConnService.LastError}";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    return;
                }

                UpdateGlossaryConnectionLabels();

                string connectionString = glossaryConnService.GetGlossaryConnectionString();
                string dbType = DatabaseService.Instance.GetDbType();

                using (var connection = DatabaseService.Instance.CreateConnection(dbType, connectionString))
                {
                    connection.Open();
                    lblGlossaryStatus.Text = $"Glossary connection successful! ({dbType})";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;
                }
            }
            catch (Exception ex)
            {
                lblGlossaryStatus.Text = $"Connection failed: {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.Red;
            }
        }

        private void BtnReloadGlossary_Click(object sender, EventArgs e)
        {
            try
            {
                lblGlossaryStatus.Text = "Reconnecting to glossary database...";
                lblGlossaryStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                DatabaseService.Instance.ClearCache();
                GlossaryConnectionService.Instance.ClearCache();

                if (!DatabaseService.Instance.IsConfigured)
                {
                    lblGlossaryStatus.Text = "Repository database not configured. Please configure in ErwinAdmin.";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    ClearGlossaryConnectionLabels();
                    return;
                }

                lblGlossaryStatus.Text = "Reading GLOSSARY_CONNECTION_DEF from repository...";
                Application.DoEvents();

                var glossaryConnService = GlossaryConnectionService.Instance;
                if (!glossaryConnService.LoadConnectionDef())
                {
                    lblGlossaryStatus.Text = $"Failed to read connection def: {glossaryConnService.LastError}";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    ClearGlossaryConnectionLabels();
                    return;
                }

                UpdateGlossaryConnectionLabels();

                lblGlossaryStatus.Text = "Loading glossary entries...";
                Application.DoEvents();

                GlossaryService.Instance.Reload();

                if (GlossaryService.Instance.IsLoaded)
                {
                    var connDef = GlossaryService.Instance.ConnectionDef;
                    lblGlossaryStatus.Text = $"Glossary loaded: {GlossaryService.Instance.Count} entries from {connDef?.Host}/{connDef?.DbSchema}";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;

                    _lastGlossaryRefreshTime = DateTime.Now;
                    UpdateLastRefreshLabel();
                    UpdateValidationStatus();
                }
                else
                {
                    lblGlossaryStatus.Text = $"Failed to load glossary: {GlossaryService.Instance.LastError}";
                    lblGlossaryStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                lblGlossaryStatus.Text = $"Error: {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.Red;
            }
        }

        private void LoadGlossary()
        {
            try
            {
                if (!DatabaseService.Instance.IsConfigured)
                {
                    lblGlossaryStatus.Text = "Repository database not configured. Please configure in ErwinAdmin.";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    ClearGlossaryConnectionLabels();
                    return;
                }

                var glossary = GlossaryService.Instance;
                if (!glossary.IsLoaded)
                {
                    glossary.LoadGlossary();
                }

                UpdateGlossaryConnectionLabels();

                if (glossary.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Glossary loaded: {glossary.Count} entries";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;
                }
                else
                {
                    lblGlossaryStatus.Text = $"Glossary not loaded: {glossary.LastError}";
                    lblGlossaryStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                Log($"LoadGlossary error: {ex.Message}");
                lblGlossaryStatus.Text = $"Error: {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.Red;
            }
        }

        private void UpdateGlossaryConnectionLabels()
        {
            var connDef = GlossaryConnectionService.Instance.ConnectionDef;
            if (connDef != null)
            {
                lblHostValue.Text = connDef.Host ?? "(not set)";
                lblPortValue.Text = connDef.Port ?? "-";
                lblDatabaseValue.Text = connDef.DbSchema ?? "(not set)";
            }
            else
            {
                ClearGlossaryConnectionLabels();
            }
        }

        private void ClearGlossaryConnectionLabels()
        {
            lblHostValue.Text = "(not loaded)";
            lblPortValue.Text = "-";
            lblDatabaseValue.Text = "(not loaded)";
        }

        #endregion

        #region Table Type Management

        private void LoadTableTypes()
        {
            try
            {
                var tableTypeService = TableTypeService.Instance;
                tableTypeService.Reload();

                if (tableTypeService.IsLoaded)
                {
                    Log($"TABLE_TYPE loaded: {tableTypeService.Count} entries");
                    Log($"TABLE_TYPE values: {tableTypeService.GetNamesAsCommaSeparated()}");

                    var predefinedColumnService = PredefinedColumnService.Instance;
                    predefinedColumnService.Reload();

                    if (predefinedColumnService.IsLoaded)
                    {
                        Log($"PREDEFINED_COLUMN loaded: {predefinedColumnService.Count} entries");
                    }
                    else
                    {
                        Log($"PREDEFINED_COLUMN not loaded: {predefinedColumnService.LastError}");
                    }
                }
                else
                {
                    Log($"TABLE_TYPE not loaded: {tableTypeService.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log($"LoadTableTypes error: {ex.Message}");
            }
        }

        private void EnsureTableTypeUdpExists()
        {
            try
            {
                var tableTypeService = TableTypeService.Instance;
                if (!tableTypeService.IsLoaded || tableTypeService.Count == 0)
                {
                    Log("TABLE_TYPE service not loaded - skipping UDP creation");
                    return;
                }

                if (CheckTableTypeUdpExists())
                {
                    Log("TABLE_TYPE UDP already exists - skipping creation");
                    return;
                }

                Log("TABLE_TYPE UDP not found - creating...");
                if (CreateTableTypeUdp(tableTypeService.GetNamesAsCommaSeparated()))
                {
                    Log("TABLE_TYPE UDP created successfully!");
                }
                else
                {
                    Log("Failed to create TABLE_TYPE UDP (may already exist)");
                }
            }
            catch (Exception ex)
            {
                Log($"EnsureTableTypeUdpExists error: {ex.Message}");
            }
        }

        private bool CheckTableTypeUdpExists()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                // Method 1: Check Property_Type objects
                try
                {
                    dynamic propertyTypes = modelObjects.Collect(root, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        string ptName = "";
                        try { ptName = pt.Name ?? ""; } catch { continue; }

                        if (ptName.Equals("Entity.Physical.TABLE_TYPE", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Property_Type enumeration failed: {ex.Message}");
                }

                // Method 2: Try to access UDP on an entity
                try
                {
                    dynamic entities = modelObjects.Collect(root, "Entity");
                    foreach (dynamic entity in entities)
                    {
                        if (entity == null) continue;
                        try
                        {
                            var udpValue = entity.Properties("Entity.Physical.TABLE_TYPE");
                            if (udpValue != null) return true;
                        }
                        catch { }
                        break;
                    }
                }
                catch { }

                return false;
            }
            catch (Exception ex)
            {
                Log($"CheckTableTypeUdpExists error: {ex.Message}");
                return false;
            }
        }

        private bool CreateTableTypeUdp(string listValues)
        {
            dynamic metamodelSession = null;
            try
            {
                Log($"Creating TABLE_TYPE UDP with values: {listValues}");

                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // SCD_SL_M1 = Metamodel level

                int transId = metamodelSession.BeginNamedTransaction("CreateTableTypeUDP");

                try
                {
                    dynamic mmObjects = metamodelSession.ModelObjects;
                    dynamic udpType = mmObjects.Add("Property_Type");

                    udpType.Properties("Name").Value = "Entity.Physical.TABLE_TYPE";

                    TrySetProperty(udpType, "tag_Udp_Owner_Type", "Entity");
                    TrySetProperty(udpType, "tag_Is_Physical", true);
                    TrySetProperty(udpType, "tag_Is_Logical", false);
                    TrySetProperty(udpType, "tag_Udp_Data_Type", 6); // List type
                    TrySetProperty(udpType, "tag_Udp_Values_List", listValues);

                    string defaultValue = listValues.Split(',').FirstOrDefault()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(defaultValue))
                    {
                        TrySetProperty(udpType, "tag_Udp_Default_Value", defaultValue);
                    }

                    TrySetProperty(udpType, "tag_Order", "1");
                    TrySetProperty(udpType, "tag_Is_Locally_Defined", true);

                    metamodelSession.CommitTransaction(transId);
                    Log("TABLE_TYPE UDP transaction committed");
                    return true;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                    {
                        Log("TABLE_TYPE UDP already exists (detected via unique constraint)");
                        try { metamodelSession.RollbackTransaction(transId); } catch { }
                        return true;
                    }

                    Log($"Error creating TABLE_TYPE UDP: {ex.Message}");
                    try { metamodelSession.RollbackTransaction(transId); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                {
                    Log("TABLE_TYPE UDP already exists (detected via unique constraint)");
                    return true;
                }

                Log($"Metamodel session error: {ex.Message}");
                return false;
            }
            finally
            {
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); } catch { }
                }
            }
        }

        #endregion

        #region Configuration Tab

        private void LoadExistingValues()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic rootObj = GetRootObject(modelObjects);

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
                Log($"LoadExistingValues error: {ex.Message}");
            }
        }

        private void OnConfigChanged(object sender, EventArgs e)
        {
            string dbName = txtDatabaseName.Text.Trim();
            string schemaName = txtSchemaName.Text.Trim();

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

        private void BtnApply_Click(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("Not connected to a model.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtDatabaseName.Text))
            {
                MessageBox.Show("Database Name cannot be empty.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtDatabaseName.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(txtSchemaName.Text))
            {
                MessageBox.Show("Schema Name cannot be empty.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtSchemaName.Focus();
                return;
            }

            try
            {
                UpdateStatus("Saving...", Color.DarkBlue);
                Application.DoEvents();

                int transId = _session.BeginNamedTransaction("SaveConfig");

                try
                {
                    dynamic modelObjects = _session.ModelObjects;
                    dynamic rootObj = GetRootObject(modelObjects);

                    bool nameSaved = false;

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

                    bool modelSaved = false;
                    try
                    {
                        _currentModel.Save();
                        modelSaved = true;
                    }
                    catch { }

                    if (nameSaved && modelSaved)
                    {
                        UpdateStatus("Saved!", Color.DarkGreen);
                        MessageBox.Show(
                            $"Configuration saved successfully!\n\nDatabase: {txtDatabaseName.Text}\nSchema: {txtSchemaName.Text}\nName: {txtName.Text}",
                            "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        UpdateStatus("Warning!", Color.Orange);
                        string msg = "Values could not be saved:\n";
                        if (!nameSaved) msg += "- Name property could not be saved\n";
                        if (!modelSaved) msg += "- Model could not be saved\n";
                        MessageBox.Show(msg, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("Error!", Color.Red);
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region Debug Log

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Log(message)));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            txtDebugLog.AppendText($"[{timestamp}] {message}\r\n");
            txtDebugLog.SelectionStart = txtDebugLog.Text.Length;
            txtDebugLog.ScrollToCaret();
            Application.DoEvents();
        }

        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            txtDebugLog.Clear();
        }

        private void BtnCopyLog_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(txtDebugLog.Text))
            {
                Clipboard.SetText(txtDebugLog.Text);
                UpdateStatus("Log copied to clipboard!", Color.DarkGreen);
            }
        }

        #endregion

        #region UI Helpers

        private void BtnClose_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void UpdateConnectionStatus(string status, Color color)
        {
            lblConnectionStatus.Text = status;
            lblConnectionStatus.ForeColor = color;
        }

        private void UpdateStatus(string message, Color color)
        {
            lblStatus.Text = message;
            lblStatus.ForeColor = color;
        }

        private void EnableControls(bool enabled)
        {
            txtDatabaseName.Enabled = enabled;
            txtSchemaName.Enabled = enabled;
            txtName.Enabled = enabled;
            btnApply.Enabled = enabled;
        }

        private void ShowError(string message, string title)
        {
            UpdateConnectionStatus(StatusDisconnected, Color.Red);
            _isConnected = false;
            EnableControls(false);
            UpdateStatus("Connection failed.", Color.Red);
            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        #endregion

        #region Helper Methods

        private dynamic GetRootObject(dynamic modelObjects)
        {
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
                try { rootObj = modelObjects.Root; } catch { }
            }

            return rootObj;
        }

        private void TrySetProperty(dynamic obj, string propertyName, object value)
        {
            try
            {
                obj.Properties(propertyName).Value = value;
            }
            catch (Exception ex)
            {
                Log($"Could not set {propertyName}: {ex.Message}");
            }
        }

        private bool TrySetOwnerUdp(dynamic attr, string ownerValue, string attributeName)
        {
            try
            {
                attr.Properties("Attribute.Physical.OWNER").Value = ownerValue;
                Log($"Set OWNER UDP = '{ownerValue}' for {attributeName}");
                return true;
            }
            catch { }

            try
            {
                var props = attr.CollectProperties("Attribute.Physical.OWNER");
                if (props != null && props.Count > 0)
                {
                    props.Item(0).Value = ownerValue;
                    Log($"Set OWNER via CollectProperties = '{ownerValue}' for {attributeName}");
                    return true;
                }
            }
            catch { }

            return false;
        }

        #endregion

        #region Resource Cleanup

        private void CloseCurrentSession()
        {
            if (_session != null)
            {
                try { _session.Close(); } catch { }
            }
        }

        private void DisposeServices()
        {
            _validationService?.Dispose();
            _tableTypeMonitorService?.Dispose();
        }

        private void CleanupResources()
        {
            try
            {
                _glossaryRefreshTimer?.Stop();
                _glossaryRefreshTimer?.Dispose();
                _glossaryRefreshTimer = null;

                DisposeServices();
                _validationService = null;
                _tableTypeMonitorService = null;

                _session?.Close();
                _scapi?.Sessions?.Clear();
            }
            catch { }
        }

        #endregion
    }
}
