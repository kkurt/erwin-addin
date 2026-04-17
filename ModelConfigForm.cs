using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
        private bool _allowClose = false;
        private readonly List<dynamic> _openModels = new List<dynamic>();
        private string _connectedModelName;
        private bool _globalDataLoaded;

        // Services
        private ColumnValidationService _validationService;
        private TableTypeMonitorService _tableTypeMonitorService;
        private ValidationCoordinatorService _validationCoordinatorService;
        private PropertyApplicatorService _propertyApplicatorService;
        private UdpRuntimeService _udpRuntimeService;
        private DependencySetRuntimeService _dependencySetService;

        // State tracking
        private Timer _glossaryRefreshTimer;
        private Timer _reconnectTimer;
        private DateTime? _lastGlossaryRefreshTime;
        private volatile bool _isRefreshingGlossary;

        #endregion

        #region Constructor

        public ModelConfigForm(dynamic scapi)
        {
            _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
            InitializeComponent();
            InitializeValidationUI();
            InitializeGeneralTab();
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
                _openModels.Clear();
                Application.DoEvents();

                dynamic persistenceUnits = _scapi.PersistenceUnits;

                if (persistenceUnits.Count == 0)
                {
                    lblActiveModel.Text = "(Waiting for model...)";
                    UpdateStatus("No models open. Waiting for a model...", Color.Gray);
                    StartReconnectTimer();
                    return;
                }

                for (int i = 0; i < persistenceUnits.Count; i++)
                {
                    dynamic model = persistenceUnits.Item(i);
                    _openModels.Add(model);
                }

                if (_openModels.Count > 0)
                {
                    ConnectToModel(0);
                }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to load models:\n{ex.Message}", "Connection Error");
            }
        }

        /// <summary>
        /// Switch to a different model by name (called when erwin active model changes).
        /// </summary>
        private void SwitchToModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return;
            if (string.Equals(modelName, _connectedModelName, StringComparison.OrdinalIgnoreCase)) return;

            Log($"Model switch detected: '{_connectedModelName}' -> '{modelName}'");

            // Refresh PersistenceUnits list
            _openModels.Clear();
            try
            {
                dynamic pus = _scapi.PersistenceUnits;
                for (int i = 0; i < pus.Count; i++)
                    _openModels.Add(pus.Item(i));
            }
            catch (Exception ex)
            {
                Log($"SwitchToModel: Failed to refresh models: {ex.Message}");
                return;
            }

            // Find the target model by name
            for (int i = 0; i < _openModels.Count; i++)
            {
                string name = GetModelName(_openModels[i]) ?? "";
                if (name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                {
                    ConnectToModel(i);
                    return;
                }
            }

            Log($"SwitchToModel: Model '{modelName}' not found in PersistenceUnits");
        }

        private void ConnectToModel(int modelIndex)
        {
            Log($">>> ConnectToModel({modelIndex}) called. Stack: {new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name ?? "?"}");
            try
            {
                // Stop old monitoring BEFORE closing session (prevents COM exception race)
                if (_validationCoordinatorService != null)
                {
                    _validationCoordinatorService.OnSessionLost -= HandleSessionLost;
                    _validationCoordinatorService.OnModelChanged -= HandleModelChanged;
                    _validationCoordinatorService.OnModelUdpChanged -= HandleModelUdpChanged;
                    _validationCoordinatorService.StopMonitoring();
                }
                _tableTypeMonitorService?.StopMonitoring();

                CloseCurrentSession();
                _isConnected = false;
                UpdateConnectionStatus(StatusConnecting, Color.Gray);
                EnableControls(false);
                Application.DoEvents();

                _currentModel = _openModels[modelIndex];
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                _connectedModelName = GetModelName(_currentModel) ?? $"Model {modelIndex + 1}";
                lblActiveModel.Text = _connectedModelName;

                _isConnected = true;
                StopReconnectTimer();
                UpdateConnectionStatus(StatusConnected, Color.DarkGreen);
                LoadExistingValues();
                EnableControls(true);
                UpdateStatus("Connected to model.", Color.DarkGreen);

                // Skip validations for RE models (temporary models created by Reverse Engineer)
                // RE models are detected by: no Locator AND model name starts with "Model_"
                // Normal local models get validations via MODEL_PATH UDP matching
                string puLocator = "";
                try { puLocator = _currentModel.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }
                bool isReModel = string.IsNullOrEmpty(puLocator) &&
                    (_connectedModelName.StartsWith("Model_") || _connectedModelName.StartsWith("Model "));

                if (isReModel)
                {
                    Log($"Skipping validations for RE model '{_connectedModelName}'");
                    // Still populate DDL combos for RE models
                    cmbLeftModel.Items.Clear();
                    cmbLeftModel.Items.Add($"Active Model ({_connectedModelName})");
                    cmbLeftModel.SelectedIndex = 0;
                    return;
                }

                Form loadingDialog = null;
                try
                {
                    string splashMessage = _globalDataLoaded ? $"Switching to {_connectedModelName}..." : "Please Wait...";
                    loadingDialog = ShowLoadingDialog(splashMessage);

                    if (_globalDataLoaded)
                    {
                        // Model switch: only reload model-specific services (fast)
                        ReinitializeForModelSwitch();
                    }
                    else
                    {
                        // First connect: full initialization
                        InitializeValidationService();
                        _globalDataLoaded = true;
                    }
                }
                finally
                {
                    if (loadingDialog != null && !loadingDialog.IsDisposed)
                    {
                        loadingDialog.Close();
                        loadingDialog.Dispose();
                    }
                }

                // If ForceClose was triggered during init, stop further processing
                if (_allowClose || this.IsDisposed) return;

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

        #region Reconnect Timer

        private void StartReconnectTimer()
        {
            StopReconnectTimer();
            _reconnectTimer = new Timer { Interval = 500 };
            _reconnectTimer.Tick += ReconnectTimer_Tick;
            _reconnectTimer.Start();
            Log("Reconnect timer started - waiting for model to open...");
        }

        private void StopReconnectTimer()
        {
            if (_reconnectTimer != null)
            {
                _reconnectTimer.Stop();
                _reconnectTimer.Dispose();
                _reconnectTimer = null;
            }
        }

        private void ReconnectTimer_Tick(object sender, EventArgs e)
        {
            if (_isConnected)
            {
                StopReconnectTimer();
                return;
            }

            try
            {
                dynamic persistenceUnits = _scapi.PersistenceUnits;
                int count = persistenceUnits.Count;
                if (count > 0)
                {
                    Log($"Model detected ({count} open). Reconnecting...");
                    StopReconnectTimer();

                    _openModels.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        dynamic model = persistenceUnits.Item(i);
                        _openModels.Add(model);
                    }

                    if (_openModels.Count > 0)
                    {
                        ConnectToModel(0);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Reconnect poll error: {ex.Message}");
            }
        }

        #endregion

        #region Validation Service

        /// <summary>
        /// Full initialization: corporate + global data + model-specific services.
        /// Called on first connect.
        /// </summary>
        private void InitializeValidationService()
        {
            Log("Initializing validation service (full)...");

            // Corporate guard
            var corpContext = CorporateContextService.Instance;
            corpContext.OnLog -= Log;
            corpContext.OnLog += Log;
            if (!corpContext.Initialize())
            {
                ErwinAddIn.ShowTopMostMessage(
                    corpContext.LastError ?? "Active Corporate not configured.\nPlease run Admin panel first.",
                    "Configuration Error");
                Log($"Corporate not configured -- closing extension.");
                this.ForceClose();
                return;
            }
            Log($"Corporate: {corpContext.ActiveCorporateName} (ID={corpContext.ActiveCorporateId}), {corpContext.EffectiveModelIds.Count} effective model(s)");

            // Global data (corporate-scoped, not model-specific)
            DisposeServices();
            GlossaryService.Instance.OnLog -= Log;
            GlossaryService.Instance.OnLog += Log;
            LoadGlossary();
            LoadPredefinedColumns();
            LoadDomainDefs();
            LoadNamingStandards();

            // Model-specific initialization
            InitializeModelServices();
        }

        /// <summary>
        /// Model-only initialization: model-specific services only.
        /// Called on model switch (corporate + global data already loaded).
        /// </summary>
        private void ReinitializeForModelSwitch()
        {
            Log("Reinitializing for model switch (model-only)...");
            DisposeServices();
            InitializeModelServices();
        }

        /// <summary>
        /// Initialize model-specific services: UDPs, PropertyApplicator, monitoring.
        /// Shared by both full init and model switch.
        /// </summary>
        private void InitializeModelServices()
        {
            EnsureAllUdpsExist();
            SetModelPathValue();

            _validationService = new ColumnValidationService(_session);
            btnValidateAll.Enabled = true;

            InitializePropertyApplicator();

            // Load dependency sets BEFORE UdpRuntime (so List UDP options are available during creation)
            _dependencySetService = new DependencySetRuntimeService();
            _dependencySetService.OnLog += Log;
            if (_dependencySetService.Load())
            {
                Log($"Dependency sets loaded: {_dependencySetService.SetCount} set(s), {_dependencySetService.MappingCount} mapping(s)");
            }

            _udpRuntimeService = new UdpRuntimeService(_session, _scapi, _currentModel);
            _udpRuntimeService.OnLog += Log;
            _udpRuntimeService.SetDependencySetService(_dependencySetService);
            if (_udpRuntimeService.Initialize())
            {
                var objectTypes = string.Join(", ", UdpDefinitionService.Instance.GetLoadedObjectTypes());
                Log($"UDP runtime initialized: {UdpDefinitionService.Instance.Count} definitions [{objectTypes}]");
            }
            else
            {
                Log("UDP runtime initialization skipped (no definitions or DB not configured)");
            }

            _tableTypeMonitorService = new TableTypeMonitorService(_session);
            _tableTypeMonitorService.OnLog += Log;
            if (_propertyApplicatorService != null)
                _tableTypeMonitorService.SetPropertyApplicator(_propertyApplicatorService);
            if (_udpRuntimeService.IsInitialized)
                _tableTypeMonitorService.SetUdpRuntimeService(_udpRuntimeService);
            _tableTypeMonitorService.TakeSnapshot();
            _tableTypeMonitorService.StartMonitoring();

            _validationCoordinatorService = new ValidationCoordinatorService(_session, _scapi);
            _validationCoordinatorService.OnLog += Log;
            _validationCoordinatorService.OnSessionLost += HandleSessionLost;
            _validationCoordinatorService.OnModelChanged += HandleModelChanged;
            _validationCoordinatorService.OnModelUdpChanged += HandleModelUdpChanged;
            _validationCoordinatorService.SetTableTypeMonitor(_tableTypeMonitorService);
            if (_udpRuntimeService.IsInitialized)
                _validationCoordinatorService.SetUdpRuntimeService(_udpRuntimeService);
            if (_dependencySetService != null && _dependencySetService.IsLoaded)
                _validationCoordinatorService.SetDependencySetService(_dependencySetService);
            _validationCoordinatorService.StartMonitoring();

            LoadTablesComboBox();
            UpdateValidationStatus();
            Log("Validation service initialized.");
            UpdateGeneralTab();
            PopulateVersionCombos();

            // Save baseline DDL at connect time (FEModel_DDL does NOT corrupt PU)
            // Baseline DDL removed - DdlHelper fetches any version from Mart on demand

        }


        #region General Tab

        // Labels to update after corporate initialization
        private Label _lblCorporateValue;
        private Label _lblDbValue;
        private Label _lblRegistryValue;

        private void InitializeGeneralTab()
        {
            var font = new Font("Segoe UI", 9.5f);
            var fontBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var fontTitle = new Font("Segoe UI", 14f, FontStyle.Bold);
            var clrAccent = Color.FromArgb(0, 120, 212);
            var clrCardBg = Color.White;
            var clrLabelDim = Color.FromArgb(100, 100, 100);

            // --- Header ---
            var lblTitle = new Label
            {
                Text = "Elite Soft Erwin AddIn",
                Font = fontTitle,
                ForeColor = clrAccent,
                AutoSize = true,
                Location = new Point(24, 20)
            };
            tabGeneral.Controls.Add(lblTitle);

            var lblCopyright = new Label
            {
                Text = "\u00A9 2026 Elite Soft. All rights reserved.",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(160, 160, 160),
                AutoSize = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point(24, 430)
            };
            lblCopyright.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && Control.ModifierKeys == (Keys.Control | Keys.Shift))
                    ForceClose();
            };
            tabGeneral.Controls.Add(lblCopyright);

            // --- Info Card ---
            var card = CreateInfoCard("", 24, 60, 812, 106, clrCardBg);
            AddCardRow(card, "Corporate:", "", fontBold, font, 0, out _, out _lblCorporateValue);
            AddCardRow(card, "Database:", "", fontBold, font, 1, out _, out _lblDbValue);
            AddCardRow(card, "Registry:", "", fontBold, font, 2, out _, out _lblRegistryValue);
            tabGeneral.Controls.Add(card);

            // Initial state
            _lblCorporateValue.Text = "(not loaded)";
            _lblDbValue.Text = "(not loaded)";
            _lblRegistryValue.Text = "(not loaded)";
        }

        private Panel CreateInfoCard(string title, int x, int y, int w, int h, Color bgColor)
        {
            var card = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = bgColor,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(16, 12, 16, 12)
            };

            if (!string.IsNullOrEmpty(title))
            {
                var lblTitle = new Label
                {
                    Text = title,
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(60, 60, 60),
                    AutoSize = true,
                    Location = new Point(16, 10)
                };
                card.Controls.Add(lblTitle);
            }

            return card;
        }

        private void AddCardRow(Panel card, string label, string value, Font labelFont, Font valueFont, int row, out Label lblLabel, out Label lblValue)
        {
            int y = 14 + row * 26;

            lblLabel = new Label
            {
                Text = label,
                Font = labelFont,
                ForeColor = Color.FromArgb(80, 80, 80),
                AutoSize = true,
                Location = new Point(16, y)
            };
            card.Controls.Add(lblLabel);

            lblValue = new Label
            {
                Text = value,
                Font = valueFont,
                ForeColor = Color.FromArgb(40, 40, 40),
                AutoSize = true,
                Location = new Point(120, y)
            };
            card.Controls.Add(lblValue);
        }

        /// <summary>
        /// Update General tab with corporate and connection info after initialization.
        /// </summary>
        private void UpdateGeneralTab()
        {
            try
            {
                var corp = CorporateContextService.Instance;
                if (corp.IsInitialized)
                {
                    _lblCorporateValue.Text = corp.ActiveCorporateName;
                }

                var config = DatabaseService.Instance.GetConfig();
                if (config != null && config.IsConfigured)
                {
                    _lblDbValue.Text = $"{config.Host}/{config.Database} ({DatabaseService.Instance.GetDbType()})";
                }

                var bootstrapService = new RegistryBootstrapService();
                _lblRegistryValue.Text = bootstrapService.GetConfigFilePath().StartsWith("HKLM") ? "Machine" : "User";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateGeneralTab error: {ex.Message}");
            }
        }

        #endregion

        private void InitializeValidationUI()
        {
            listValidationResults.Columns.Add("Type", 70);
            listValidationResults.Columns.Add("Object Name", 220);
            listValidationResults.Columns.Add("Rule", 110);
            listValidationResults.Columns.Add("", 36);     // Status icon column (narrow)
            listValidationResults.Columns.Add("Message", 380);

            btnValidateAll.Enabled = false;
        }

        private void BtnValidateAll_Click(object sender, EventArgs e)
        {
            listValidationResults.Items.Clear();

            string filter = cmbValidationFilter.SelectedItem?.ToString() ?? "All";
            int errorCount = 0;
            int totalCount = 0;

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Table validation (Glossary column check + TableType + Naming Standards)
                if (filter == "All" || filter == "Table")
                {
                    dynamic allEntities = modelObjects.Collect(root, "Entity");
                    if (allEntities != null)
                    {
                        foreach (dynamic entity in allEntities)
                        {
                            if (entity == null) continue;
                            string tableName = "";
                            try
                            {
                                string physName = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                                tableName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : (entity.Name ?? "");
                            }
                            catch { try { tableName = entity.Name ?? ""; } catch { continue; } }

                            // Naming standard validation
                            var namingResults = NamingValidationEngine.ValidateObjectName("Table", tableName, entity);
                            foreach (var r in namingResults)
                            {
                                totalCount++;
                                if (!r.IsValid) errorCount++;
                                AddValidationRow("Table", tableName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                            }

                            // TABLE_TYPE validation is now handled by NamingStandard rules with UDP conditions
                        }
                    }
                }

                // Column validation (Glossary + Naming Standards)
                if (filter == "All" || filter == "Column")
                {
                    dynamic allEntities = modelObjects.Collect(root, "Entity");
                    if (allEntities != null)
                    {
                        var glossary = GlossaryService.Instance;
                        foreach (dynamic entity in allEntities)
                        {
                            if (entity == null) continue;
                            string tableName = "";
                            try
                            {
                                string physName = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                                tableName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : (entity.Name ?? "");
                            }
                            catch { try { tableName = entity.Name ?? ""; } catch { continue; } }

                            dynamic attrs = null;
                            try { attrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                            if (attrs == null) continue;

                            foreach (dynamic attr in attrs)
                            {
                                if (attr == null) continue;
                                string colName = "";
                                try
                                {
                                    string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                                    colName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : (attr.Name ?? "");
                                }
                                catch { try { colName = attr.Name ?? ""; } catch { continue; } }

                                // Glossary check
                                if (glossary.IsLoaded)
                                {
                                    bool inGlossary = glossary.HasEntry(colName);
                                    totalCount++;
                                    if (!inGlossary) errorCount++;
                                    AddValidationRow("Column", $"{tableName}.{colName}", "Glossary", inGlossary, inGlossary ? "" : "Not found in glossary");
                                }

                                // Naming standard validation
                                var namingResults = NamingValidationEngine.ValidateObjectName("Column", colName, attr);
                                foreach (var r in namingResults)
                                {
                                    totalCount++;
                                    if (!r.IsValid) errorCount++;
                                    AddValidationRow("Column", $"{tableName}.{colName}", $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                                }
                            }
                        }
                    }
                }

                // Index (Key_Group) validation
                if (filter == "All" || filter == "Index")
                {
                    try
                    {
                        dynamic allKg = modelObjects.Collect(root, "Key_Group");
                        if (allKg != null)
                        {
                            foreach (dynamic kg in allKg)
                            {
                                if (kg == null) continue;
                                string kgName = "";
                                try { kgName = kg.Name ?? ""; } catch { continue; }

                                var namingResults = NamingValidationEngine.ValidateObjectName("Index", kgName, kg);
                                foreach (var r in namingResults)
                                {
                                    totalCount++;
                                    if (!r.IsValid) errorCount++;
                                    AddValidationRow("Index", kgName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // View validation
                if (filter == "All" || filter == "View")
                {
                    try
                    {
                        dynamic allViews = modelObjects.Collect(root, "View");
                        if (allViews != null)
                        {
                            foreach (dynamic view in allViews)
                            {
                                if (view == null) continue;
                                string viewName = "";
                                try
                                {
                                    string physName = view.Properties("Physical_Name").Value?.ToString() ?? "";
                                    viewName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : (view.Name ?? "");
                                }
                                catch { try { viewName = view.Name ?? ""; } catch { continue; } }

                                var namingResults = NamingValidationEngine.ValidateObjectName("View", viewName, view);
                                foreach (var r in namingResults)
                                {
                                    totalCount++;
                                    if (!r.IsValid) errorCount++;
                                    AddValidationRow("View", viewName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Model validation (naming standards)
                if (filter == "All" || filter == "Model")
                {
                    try
                    {
                        string modelName = "";
                        try
                        {
                            dynamic modelRoot = modelObjects.Root;
                            modelName = modelRoot?.Name ?? "";
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(modelName))
                        {
                            var namingResults = NamingValidationEngine.ValidateObjectName("Model", modelName);
                            foreach (var r in namingResults)
                            {
                                totalCount++;
                                if (!r.IsValid) errorCount++;
                                AddValidationRow("Model", modelName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                            }
                        }
                    }
                    catch { }
                }

                // Subject Area validation (naming standards)
                if (filter == "All" || filter == "Subject Area")
                {
                    try
                    {
                        dynamic allSAs = modelObjects.Collect(root, "Subject_Area");
                        if (allSAs != null)
                        {
                            foreach (dynamic sa in allSAs)
                            {
                                if (sa == null) continue;
                                string saName = "";
                                try { saName = sa.Name ?? ""; } catch { continue; }

                                var namingResults = NamingValidationEngine.ValidateObjectName("Subject Area", saName, sa);
                                foreach (var r in namingResults)
                                {
                                    totalCount++;
                                    if (!r.IsValid) errorCount++;
                                    AddValidationRow("Subject Area", saName, $"Naming ({r.RuleName})", r.IsValid, r.ErrorMessage);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Apply "Errors Only" checkbox filter
                if (chkErrorsOnly.Checked)
                {
                    var toRemove = new List<ListViewItem>();
                    foreach (ListViewItem item in listValidationResults.Items)
                    {
                        if (item.SubItems[3].Text == "\u2713") toRemove.Add(item);
                    }
                    foreach (var item in toRemove) listValidationResults.Items.Remove(item);
                }
            }
            catch (Exception ex)
            {
                Log($"ValidateAll error: {ex.Message}");
            }

            ShowValidationSummary(totalCount, errorCount);
        }

        private void AddValidationRow(string objectType, string objectName, string rule, bool isValid, string message)
        {
            var item = new ListViewItem(objectType);
            item.SubItems.Add(objectName);
            item.SubItems.Add(rule);
            item.SubItems.Add(isValid ? "\u2713" : "\u2717");
            item.SubItems.Add(isValid ? "" : message);
            item.ForeColor = isValid ? Color.DarkGreen : Color.Red;
            listValidationResults.Items.Add(item);
        }

        private void ShowValidationSummary(int totalCount, int errorCount)
        {
            if (errorCount == 0)
            {
                lblValidationStatus.Text = $"All validations passed ({totalCount} checks)";
                lblValidationStatus.ForeColor = Color.DarkGreen;
            }
            else
            {
                lblValidationStatus.Text = $"Validation: {errorCount} error(s) / {totalCount} checks";
                lblValidationStatus.ForeColor = Color.Red;
            }
        }

        private void CmbValidationFilter_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbValidationFilter.SelectedIndex < 0) return;

            // Re-run validation with the selected filter
            if (_isConnected && _session != null)
            {
                BtnValidateAll_Click(sender, e);
            }
        }

        private void ChkErrorsOnly_CheckedChanged(object sender, EventArgs e)
        {
            // Re-run validation with current filter + errors only toggle
            if (_isConnected && _session != null)
            {
                BtnValidateAll_Click(sender, e);
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
            int intervalMinutes = GetGlossaryLoadInterval();
            int intervalMs = intervalMinutes * 60 * 1000;

            _glossaryRefreshTimer = new Timer { Interval = intervalMs };
            _glossaryRefreshTimer.Tick += GlossaryRefreshTimer_Tick;
            _glossaryRefreshTimer.Start();
            Log($"Glossary auto-refresh timer started (every {intervalMinutes} minute(s))");
        }

        private int GetGlossaryLoadInterval()
        {
            const int defaultInterval = 1; // 1 minute default
            try
            {
                if (!DatabaseService.Instance.IsConfigured) return defaultInterval;

                var config = new RegistryBootstrapService().GetConfig();
                if (config == null || !config.IsConfigured) return defaultInterval;

                // Read from MODEL_PROPERTY: check effective model first, then All Models (ID=1)
                int modelId = _propertyApplicatorService?.ModelId ?? 0;
                using (var context = new EliteSoft.MetaAdmin.Shared.Data.RepoDbContext(config))
                {
                    var prop = context.ModelProperties
                        .FirstOrDefault(p => p.ModelId == modelId && p.Key == "GLOSSARY_LOAD_INTERVAL");

                    if (prop == null && modelId != 1)
                    {
                        prop = context.ModelProperties
                            .FirstOrDefault(p => p.ModelId == 1 && p.Key == "GLOSSARY_LOAD_INTERVAL");
                    }

                    if (prop != null && int.TryParse(prop.Value, out int minutes) && minutes > 0)
                        return minutes;
                }
            }
            catch (Exception ex)
            {
                Log($"GetGlossaryLoadInterval error: {ex.Message}");
            }
            return defaultInterval;
        }

        private void GlossaryRefreshTimer_Tick(object sender, EventArgs e)
        {
            // Prevent re-entrancy - skip if already refreshing
            if (_isRefreshingGlossary) return;

            // Run on background thread to avoid UI freeze
            Task.Run(() => RefreshGlossarySilently());
        }

        private void RefreshGlossarySilently()
        {
            if (_isRefreshingGlossary) return;
            if (!DatabaseService.Instance.IsConfigured) return;

            try
            {
                _isRefreshingGlossary = true;

                GlossaryService.Instance.Reload();
                _lastGlossaryRefreshTime = DateTime.Now;

                // Update UI on main thread
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        UpdateLastRefreshLabel();
                        if (GlossaryService.Instance.IsLoaded)
                        {
                            Log($"Glossary auto-refreshed: {GlossaryService.Instance.Count} entries");
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() => Log($"Glossary refresh error: {ex.Message}")));
                }
            }
            finally
            {
                _isRefreshingGlossary = false;
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

                // Test glossary loading via DG_TABLE_MAPPING
                var glossary = GlossaryService.Instance;
                glossary.LoadGlossary();

                if (glossary.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Glossary connection successful! ({glossary.Count} entries)";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;
                }
                else
                {
                    lblGlossaryStatus.Text = $"Glossary: {glossary.LastError}";
                    lblGlossaryStatus.ForeColor = Color.Red;
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

                if (!DatabaseService.Instance.IsConfigured)
                {
                    lblGlossaryStatus.Text = "Repository database not configured. Please configure in ErwinAdmin.";
                    lblGlossaryStatus.ForeColor = Color.Red;
                    ClearGlossaryConnectionLabels();
                    return;
                }

                lblGlossaryStatus.Text = "Loading glossary...";
                Application.DoEvents();

                GlossaryService.Instance.Reload();

                if (GlossaryService.Instance.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Glossary loaded: {GlossaryService.Instance.Count} entries";
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
            if (GlossaryService.Instance.IsLoaded)
            {
                lblHostValue.Text = "Configured";
                lblPortValue.Text = "-";
                lblDatabaseValue.Text = $"{GlossaryService.Instance.Count} entries";
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

        private void LoadPredefinedColumns()
        {
            try
            {
                var predefinedColumnService = PredefinedColumnService.Instance;
                predefinedColumnService.LoadPredefinedColumns();

                if (predefinedColumnService.IsLoaded)
                {
                    Log($"PREDEFINED_COLUMN loaded: {predefinedColumnService.Count} entries");
                }
                else
                {
                    Log($"PREDEFINED_COLUMN not loaded: {predefinedColumnService.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log($"LoadPredefinedColumns error: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize PropertyApplicatorService: detect platform, load project standards from DB.
        /// Standards will be auto-applied to new tables when created.
        /// </summary>
        private void InitializePropertyApplicator()
        {
            try
            {
                _propertyApplicatorService?.Dispose();
                _propertyApplicatorService = null;

                var bootstrapService = new RegistryBootstrapService();
                var config = bootstrapService.GetConfig();
                if (config == null || !config.IsConfigured)
                {
                    Log("PropertyApplicator: DB not configured, skipping");
                    return;
                }

                var metadataService = new AddInPropertyMetadataService(bootstrapService);
                _propertyApplicatorService = new PropertyApplicatorService(_session, metadataService);
                _propertyApplicatorService.OnLog += Log;

                if (_propertyApplicatorService.Initialize())
                {
                    var platform = _propertyApplicatorService.DetectedPlatform;
                    int stdCount = _propertyApplicatorService.StandardCount;
                    int qCount = _propertyApplicatorService.QuestionCount;
                    string statusParts = $"Platform: {platform.Name}  |  {stdCount} standard(s)";
                    if (qCount > 0) statusParts += $"  |  {qCount} rule(s) loaded";
                    lblPlatformStatus.Text = statusParts;
                    lblPlatformStatus.ForeColor = Color.DarkGreen;
                    Log($"PropertyApplicator: Ready ({platform.Name}, {stdCount} standards, {qCount} questions)");
                }
                else
                {
                    string targetServer = _propertyApplicatorService.TargetServerValue;
                    var detectedPlatform = _propertyApplicatorService.DetectedPlatform;

                    if (string.IsNullOrEmpty(targetServer))
                    {
                        lblPlatformStatus.Text = "Platform: Target_Server not found in model";
                    }
                    else if (detectedPlatform != null)
                    {
                        // Platform matched but project not found
                        lblPlatformStatus.Text = $"Platform: {detectedPlatform.Name} (OK)  |  Project not found in DB";
                    }
                    else
                    {
                        lblPlatformStatus.Text = $"Platform: No match for '{targetServer}' in MC_PLATFORM";
                    }
                    lblPlatformStatus.ForeColor = Color.OrangeRed;
                    Log("PropertyApplicator: Initialization failed (no platform/project/standards)");
                    _propertyApplicatorService.Dispose();
                    _propertyApplicatorService = null;
                }
            }
            catch (Exception ex)
            {
                Log($"InitializePropertyApplicator error: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures all UDPs exist using a SINGLE metamodel session.
        /// Creates TABLE_TYPE (Entity, List) and OWNER/KVKK/PCIDSS/CLASSIFICATION (Attribute, Text).
        /// </summary>
        private void EnsureAllUdpsExist()
        {
            dynamic metamodelSession = null;
            try
            {
                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // SCD_SL_M1 = Metamodel level

                dynamic mmObjects = metamodelSession.ModelObjects;
                dynamic mmRoot = mmObjects.Root;

                // Collect all existing Property_Type names in one pass
                var existingUdps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        try { existingUdps.Add(pt.Name ?? ""); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Metamodel Property_Type enumeration failed: {ex.Message}");
                }

                Log($"Found {existingUdps.Count} existing Property_Type entries");

                // TABLE_TYPE UDP is now managed by UdpRuntimeService (MC_UDP_DEFINITION)

                // --- MODEL_PATH UDP (hidden, read-only — stores repository path) ---
                // Determine model path BEFORE creating UDP (to set as default value in metamodel)
                string modelPathForUdp = ReadModelPath();

                if (!existingUdps.Contains("Model.Physical.MODEL_PATH"))
                {
                    int transId = metamodelSession.BeginNamedTransaction("CreateModelPathUDP");
                    try
                    {
                        dynamic udpType = mmObjects.Add("Property_Type");
                        udpType.Properties("Name").Value = "Model.Physical.MODEL_PATH";
                        TrySetProperty(udpType, "tag_Udp_Owner_Type", "Model");
                        TrySetProperty(udpType, "tag_Is_Physical", true);
                        TrySetProperty(udpType, "tag_Is_Logical", false);
                        TrySetProperty(udpType, "tag_Udp_Data_Type", 1); // Text type
                        // Set model path as default value — ensures it's embedded even before model session can see the UDP
                        if (!string.IsNullOrEmpty(modelPathForUdp))
                            TrySetProperty(udpType, "tag_Udp_Default_Value", modelPathForUdp);
                        TrySetProperty(udpType, "tag_Order", "999");
                        TrySetProperty(udpType, "tag_Is_Locally_Defined", true);
                        metamodelSession.CommitTransaction(transId);
                        Log($"MODEL_PATH UDP created (default='{modelPathForUdp ?? ""}')");
                    }
                    catch (Exception ex)
                    {
                        try { metamodelSession.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"MODEL_PATH rollback error: {rbEx.Message}"); }
                        if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                            Log("MODEL_PATH UDP already exists (unique constraint)");
                        else
                            Log($"Error creating MODEL_PATH UDP: {ex.Message}");
                    }
                }

                Log("All UDPs ensured");
            }
            catch (Exception ex)
            {
                Log($"EnsureAllUdpsExist error: {ex.Message}");
            }
            finally
            {
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); } catch { }
                }
            }
        }

        /// <summary>
        /// Set MODEL_PATH UDP value from the model's repository or file path.
        /// Only writes if the current value is empty (first time).
        /// </summary>
        private void SetModelPathValue()
        {
            // Use a fresh session to see newly created UDP (main session may have stale schema)
            dynamic freshSession = null;
            try
            {
                // Determine path first (before opening new session)
                string modelPath = ReadModelPath();

                freshSession = _scapi.Sessions.Add();
                freshSession.Open(_currentModel);

                dynamic modelObjects = freshSession.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null)
                {
                    Log("MODEL_PATH: Fresh session root is null");
                    return;
                }

                // Read current MODEL_PATH value
                string currentModelPath = "";
                try
                {
                    currentModelPath = root.Properties("Model.Physical.MODEL_PATH").Value?.ToString() ?? "";
                }
                catch (Exception ex)
                {
                    Log($"MODEL_PATH UDP not readable: {ex.Message}");
                    return;
                }

                if (!string.IsNullOrEmpty(currentModelPath))
                {
                    Log($"MODEL_PATH already set: '{currentModelPath}'");
                    return;
                }

                if (string.IsNullOrEmpty(modelPath))
                {
                    Log("MODEL_PATH: Could not determine model path");
                    return;
                }

                // Write to UDP
                int transId = freshSession.BeginNamedTransaction("SetModelPath");
                try
                {
                    root.Properties("Model.Physical.MODEL_PATH").Value = modelPath;
                    freshSession.CommitTransaction(transId);
                    Log($"MODEL_PATH set to: '{modelPath}'");
                }
                catch (Exception ex)
                {
                    try { freshSession.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"MODEL_PATH rollback error: {rbEx.Message}"); }
                    Log($"MODEL_PATH write failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"SetModelPathValue error: {ex.Message}");
            }
            finally
            {
                if (freshSession != null)
                {
                    try { freshSession.Close(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SetModelPath: Session close error: {ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// Read model full path from PersistenceUnit (repository) or file system.
        /// Probes multiple COM properties to find the complete path including folder structure.
        /// </summary>
        private string ReadModelPath()
        {
            try
            {
                // Phase 1: Probe PersistenceUnit COM object for full path
                if (_currentModel != null)
                {
                    // Try all possible PersistenceUnit properties that might contain full path
                    string[] puProps = { "FullName", "Path", "Location", "ConnectionString",
                                         "Source", "FileName", "FilePath", "URL", "URI" };
                    foreach (var prop in puProps)
                    {
                        try
                        {
                            dynamic val = null;
                            // Try as direct property first
                            try { val = ((dynamic)_currentModel).GetType().GetProperty(prop)?.GetValue(_currentModel); } catch { }
                            // Try as COM late-bound property
                            if (val == null) try { val = ((dynamic)_currentModel).GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, _currentModel, null); } catch { }

                            string strVal = val?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(strVal) && strVal != _currentModel.Name?.ToString())
                            {
                                Log($"MODEL_PATH: PersistenceUnit.{prop} = '{strVal}'");
                                return strVal;
                            }
                        }
                        catch { }
                    }

                    // Log PersistenceUnit.Name for debugging
                    try
                    {
                        string puName = _currentModel.Name?.ToString() ?? "";
                        Log($"MODEL_PATH: PersistenceUnit.Name = '{puName}' (no full path found on PU)");
                    }
                    catch { }
                }

                // Phase 2: Try session/root properties for Mart path
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                // Try Mart-specific properties on root
                string[] rootProps = { "Long_Id", "Source_File_Name", "File_Name", "File_Path",
                                        "Model_File_Name", "Mart_Model_Path", "Repository_Path" };
                foreach (var propName in rootProps)
                {
                    try
                    {
                        string val = root.Properties(propName).Value?.ToString();
                        if (!string.IsNullOrEmpty(val) && !val.StartsWith("%"))
                        {
                            Log($"MODEL_PATH: root.{propName} = '{val}'");
                            // Skip Long_Id as path (it's a GUID), just log it
                            if (propName == "Long_Id") continue;
                            return val;
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MODEL_PATH: root.{propName} error: {ex.Message}"); }
                }

                // Phase 3: Try to get Mart folder path from SCAPI session
                try
                {
                    // Some erwin versions expose path on the session itself
                    string[] sessionProps = { "ModelPath", "FileName", "Source" };
                    foreach (var prop in sessionProps)
                    {
                        try
                        {
                            string val = _session.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, (object)_session, null)?.ToString();
                            if (!string.IsNullOrEmpty(val))
                            {
                                Log($"MODEL_PATH: session.{prop} = '{val}'");
                                return val;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Phase 4: Fallback — use model root Name
                try
                {
                    string modelName = root.Properties("Name").Value?.ToString();
                    if (!string.IsNullOrEmpty(modelName))
                    {
                        Log($"MODEL_PATH: Fallback to root.Name = '{modelName}'");
                        return modelName;
                    }
                }
                catch (Exception ex) { Log($"MODEL_PATH: Name fallback error: {ex.Message}"); }

                return null;
            }
            catch (Exception ex) { Log($"ReadModelPath error: {ex.Message}"); return null; }
        }

        #endregion

        #region Domain Management

        private void LoadDomainDefs()
        {
            try
            {
                var domainDefService = DomainDefService.Instance;
                domainDefService.Reload();

                if (domainDefService.IsLoaded)
                {
                    Log($"DOMAIN_DEF loaded: {domainDefService.Count} entries");
                    Log($"Domain values: {domainDefService.GetNamesAsCommaSeparated()}");
                }
                else
                {
                    Log($"DOMAIN_DEF not loaded: {domainDefService.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log($"LoadDomainDefs error: {ex.Message}");
            }
        }

        private void LoadNamingStandards()
        {
            try
            {
                var service = NamingStandardService.Instance;
                service.LoadStandards();

                if (service.IsLoaded)
                {
                    Log($"NAMING_STANDARD loaded: {service.Count} active rules");
                }
                else
                {
                    Log($"NAMING_STANDARD not loaded: {service.LastError}");
                }
            }
            catch (Exception ex)
            {
                Log($"LoadNamingStandards error: {ex.Message}");
            }
        }

        private void EnsureDomainParentUdpExists()
        {
            try
            {
                var domainDefService = DomainDefService.Instance;
                if (!domainDefService.IsLoaded || domainDefService.Count == 0)
                {
                    Log("DOMAIN_DEF service not loaded - skipping Domain_Parent UDP creation");
                    return;
                }

                if (CheckDomainParentUdpExists())
                {
                    Log("Domain_Parent UDP already exists - skipping creation");
                    return;
                }

                Log("Domain_Parent UDP not found - creating...");
                if (CreateDomainParentUdp(domainDefService.GetNamesAsCommaSeparated()))
                {
                    Log("Domain_Parent UDP created successfully!");
                }
                else
                {
                    Log("Failed to create Domain_Parent UDP (may already exist)");
                }
            }
            catch (Exception ex)
            {
                Log($"EnsureDomainParentUdpExists error: {ex.Message}");
            }
        }

        private bool CheckDomainParentUdpExists()
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

                        if (ptName.Equals("Attribute.Physical.Domain_Parent", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Property_Type enumeration failed: {ex.Message}");
                }

                // Method 2: Try to access UDP on an attribute
                try
                {
                    dynamic entities = modelObjects.Collect(root, "Entity");
                    foreach (dynamic entity in entities)
                    {
                        if (entity == null) continue;

                        dynamic attrs = null;
                        try { attrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                        if (attrs == null) continue;

                        foreach (dynamic attr in attrs)
                        {
                            if (attr == null) continue;
                            try
                            {
                                var udpValue = attr.Properties("Attribute.Physical.Domain_Parent");
                                if (udpValue != null) return true;
                            }
                            catch { }
                            break;
                        }
                        break;
                    }
                }
                catch { }

                return false;
            }
            catch (Exception ex)
            {
                Log($"CheckDomainParentUdpExists error: {ex.Message}");
                return false;
            }
        }

        private bool CreateDomainParentUdp(string listValues)
        {
            dynamic metamodelSession = null;
            try
            {
                Log($"Creating Domain_Parent UDP with values: {listValues}");

                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // SCD_SL_M1 = Metamodel level

                int transId = metamodelSession.BeginNamedTransaction("CreateDomainParentUDP");

                try
                {
                    dynamic mmObjects = metamodelSession.ModelObjects;
                    dynamic udpType = mmObjects.Add("Property_Type");

                    udpType.Properties("Name").Value = "Attribute.Physical.Domain_Parent";

                    TrySetProperty(udpType, "tag_Udp_Owner_Type", "Attribute");
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
                    Log("Domain_Parent UDP transaction committed");
                    return true;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                    {
                        Log("Domain_Parent UDP already exists (detected via unique constraint)");
                        try { metamodelSession.RollbackTransaction(transId); } catch { }
                        return true;
                    }

                    Log($"Error creating Domain_Parent UDP: {ex.Message}");
                    try { metamodelSession.RollbackTransaction(transId); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                {
                    Log("Domain_Parent UDP already exists (detected via unique constraint)");
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

        #region Table Processes

        private void LoadTablesComboBox()
        {
            try
            {
                cmbTables.Items.Clear();

                if (_session == null) return;

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                var tables = new List<string>();

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        string entityName = entity.Name ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch
                    {
                        try { tableName = entity.Name ?? ""; } catch { continue; }
                    }

                    if (!string.IsNullOrEmpty(tableName))
                    {
                        tables.Add(tableName);
                    }
                }

                tables.Sort();
                foreach (var table in tables)
                {
                    cmbTables.Items.Add(table);
                }

                if (cmbTables.Items.Count > 0)
                {
                    cmbTables.SelectedIndex = 0;
                }

                Log($"Loaded {tables.Count} tables into combo box");
            }
            catch (Exception ex)
            {
                Log($"LoadTablesComboBox error: {ex.Message}");
            }
        }

        private void BtnCreateTables_Click(object sender, EventArgs e)
        {
            try
            {
                if (cmbTables.SelectedItem == null)
                {
                    MessageBox.Show("Please select a table first.", "No Table Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!chkArchiveTable.Checked && !chkIsolatedTable.Checked)
                {
                    MessageBox.Show("Please select at least one option (Archive or Isolated).", "No Option Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string sourceTableName = cmbTables.SelectedItem.ToString();
                lblTableProcessStatus.Text = "Processing...";
                lblTableProcessStatus.ForeColor = System.Drawing.Color.DarkBlue;
                Application.DoEvents();

                // Suspend validation during table copy to avoid validation popups
                _validationCoordinatorService?.SuspendValidation();

                int tablesCreated = 0;

                try
                {
                    if (chkArchiveTable.Checked)
                    {
                        string archiveName = sourceTableName + "_ARCHIVE";
                        if (CreateTableCopy(sourceTableName, archiveName))
                        {
                            tablesCreated++;
                            Log($"Created archive table: {archiveName}");
                        }
                    }

                    if (chkIsolatedTable.Checked)
                    {
                        string isolatedName = sourceTableName + "_ISOLATED";
                        if (CreateTableCopy(sourceTableName, isolatedName))
                        {
                            tablesCreated++;
                            Log($"Created isolated table: {isolatedName}");
                        }
                    }
                }
                finally
                {
                    // Resume validation after table copy completes
                    _validationCoordinatorService?.ResumeValidation();
                }

                if (tablesCreated > 0)
                {
                    lblTableProcessStatus.Text = $"Successfully created {tablesCreated} table(s)!";
                    lblTableProcessStatus.ForeColor = System.Drawing.Color.DarkGreen;
                    LoadTablesComboBox(); // Refresh list
                }
                else
                {
                    lblTableProcessStatus.Text = "No tables created. Check log for details.";
                    lblTableProcessStatus.ForeColor = System.Drawing.Color.DarkRed;
                }
            }
            catch (Exception ex)
            {
                Log($"BtnCreateTables_Click error: {ex.Message}");
                lblTableProcessStatus.Text = $"Error: {ex.Message}";
                lblTableProcessStatus.ForeColor = System.Drawing.Color.DarkRed;
            }
        }

        private bool CreateTableCopy(string sourceTableName, string newTableName)
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                // Find source entity
                dynamic sourceEntity = null;
                dynamic allEntities = modelObjects.Collect(root, "Entity");

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        string entityName = entity.Name ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch
                    {
                        try { tableName = entity.Name ?? ""; } catch { continue; }
                    }

                    if (tableName == sourceTableName)
                    {
                        sourceEntity = entity;
                        break;
                    }
                }

                if (sourceEntity == null)
                {
                    Log($"Source table '{sourceTableName}' not found");
                    return false;
                }

                // Check if target already exists
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        string entityName = entity.Name ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch
                    {
                        try { tableName = entity.Name ?? ""; } catch { continue; }
                    }

                    if (tableName == newTableName)
                    {
                        Log($"Table '{newTableName}' already exists - skipping");
                        MessageBox.Show($"Table '{newTableName}' already exists.", "Table Exists", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                }

                // Create new entity
                int transId = _session.BeginNamedTransaction($"CreateTableCopy_{newTableName}");
                try
                {
                    // Create new entity
                    dynamic newEntity = modelObjects.Add("Entity");
                    newEntity.Properties("Name").Value = newTableName;
                    newEntity.Properties("Physical_Name").Value = newTableName;

                    // Copy entity-level properties from source
                    CopyEntityProperties(sourceEntity, newEntity);

                    // Copy all attributes from source entity
                    dynamic sourceAttrs = modelObjects.Collect(sourceEntity, "Attribute");
                    if (sourceAttrs != null)
                    {
                        int attrCount = 0;
                        foreach (dynamic sourceAttr in sourceAttrs)
                        {
                            if (sourceAttr == null) continue;

                            try
                            {
                                // Create new attribute under new entity using Collect().Add() pattern
                                dynamic newAttr = modelObjects.Collect(newEntity).Add("Attribute");

                                if (newAttr != null)
                                {
                                    // Copy attribute properties
                                    CopyAttributeProperties(sourceAttr, newAttr);
                                    attrCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Error copying attribute: {ex.Message}");
                            }
                        }
                        Log($"Copied {attrCount} attributes to {newTableName}");
                    }

                    _session.CommitTransaction(transId);
                    Log($"Successfully created table copy: {newTableName}");
                    return true;
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"Error creating table copy: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"CreateTableCopy error: {ex.Message}");
                return false;
            }
        }

        private void CopyEntityProperties(dynamic source, dynamic target)
        {
            // Copy common entity properties
            string[] propertiesToCopy = { "Definition", "Note", "Owner" };

            foreach (string propName in propertiesToCopy)
            {
                try
                {
                    var value = source.Properties(propName).Value;
                    if (value != null && !string.IsNullOrEmpty(value.ToString()))
                    {
                        target.Properties(propName).Value = value;
                    }
                }
                catch { }
            }
        }

        private void CopyAttributeProperties(dynamic source, dynamic target)
        {
            // Copy attribute name and physical name
            try
            {
                string attrName = source.Name ?? "";
                target.Properties("Name").Value = attrName;
            }
            catch { }

            try
            {
                string physName = source.Properties("Physical_Name").Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%"))
                {
                    target.Properties("Physical_Name").Value = physName;
                }
            }
            catch { }

            // Copy other attribute properties
            string[] propertiesToCopy = {
                "Physical_Data_Type",
                "Logical_Data_Type",
                "Null_Option",
                "Definition",
                "Note",
                "Default_Value",
                "Parent_Domain_Ref"
            };

            foreach (string propName in propertiesToCopy)
            {
                try
                {
                    var value = source.Properties(propName).Value;
                    if (value != null)
                    {
                        string strValue = value.ToString();
                        if (!string.IsNullOrEmpty(strValue) && !strValue.StartsWith("%"))
                        {
                            target.Properties(propName).Value = value;
                        }
                    }
                }
                catch { }
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

        #region Approval / Review

        private void BtnMartReview_Click(object sender, EventArgs e)
        {
            var hWnd = Services.Win32Helper.GetErwinMainWindow();
            if (hWnd == IntPtr.Zero)
            {
                ErwinAddIn.ShowTopMostMessage("erwin window not found.", "Mart Review");
                return;
            }

            Log("[REVIEW] Triggering erwin Mart Review...");
            bool invoked = Services.Win32Helper.InvokeToolbarButton(hWnd, "Review", Log);

            if (invoked)
                Log("[REVIEW] Review triggered.");
            else
            {
                Log("[REVIEW] 'Review' button not found.");
                ErwinAddIn.ShowTopMostMessage("'Review' button not found in erwin.", "Mart Review");
            }
        }

        private void BtnGenerateDDL_Click(object sender, EventArgs e)
        {
            if (!_isConnected || _currentModel == null)
            {
                ErwinAddIn.ShowTopMostMessage("No model connected.", "DDL Generation");
                return;
            }

            string feOptionXml = txtFEOptionXml.Text.Trim();
            bool isFromDB = rbFromDB.Checked;

            if (isFromDB)
            {
                RunFromDbCompare(feOptionXml);
                return;
            }

            // Check if model has unsaved changes (asterisk in erwin window title)
            bool hasUnsavedChanges = HasModelUnsavedChanges();
            bool isDiffMode = cmbRightModel.Items.Count > 0 && cmbRightModel.SelectedIndex >= 0;

            btnGenerateDDL.Enabled = false;
            rtbDDLOutput.Text = "";
            Application.DoEvents();

            _validationCoordinatorService?.SuspendValidation();

            try
            {
                int puCount = 0;
                try { puCount = _scapi.PersistenceUnits.Count; } catch { }

                int selVer = 0;
                string selectedVersion = cmbRightModel.SelectedItem?.ToString() ?? "";
                var verMatch = System.Text.RegularExpressions.Regex.Match(selectedVersion, @"v(\d+)");
                if (verMatch.Success)
                    DdlGenerationService.SetSelectedVersion(int.Parse(verMatch.Groups[1].Value));
                else
                    DdlGenerationService.SetSelectedVersion(0);

                selVer = verMatch.Success ? int.Parse(verMatch.Groups[1].Value) : 0;
                string waitMessage = $"Generating DDL diff (v{_martVersion} vs v{selVer})...\nPlease wait.";

                lblDDLStatus.Text = $"Generating DDL diff (v{_martVersion} vs v{selVer})...";
                lblDDLStatus.ForeColor = Color.Gray;
                Application.DoEvents();

                // Show "Please wait" dialog
                Form waitDialog = null;
                try
                {
                    waitDialog = new Form
                    {
                        Size = new System.Drawing.Size(350, 120),
                        FormBorderStyle = FormBorderStyle.None,
                        StartPosition = FormStartPosition.CenterScreen,
                        TopMost = true,
                        ShowInTaskbar = false,
                        BackColor = Color.White
                    };
                    // Custom title bar (no disabled look)
                    var titlePanel = new Panel
                    {
                        Dock = DockStyle.Top,
                        Height = 30,
                        BackColor = Color.FromArgb(0, 122, 204)
                    };
                    var titleLabel = new Label
                    {
                        Text = "DDL Generation",
                        ForeColor = Color.White,
                        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                        Location = new Point(10, 5),
                        AutoSize = true
                    };
                    titlePanel.Controls.Add(titleLabel);
                    waitDialog.Controls.Add(titlePanel);
                    var waitLabel = new Label
                    {
                        Text = waitMessage,
                        Dock = DockStyle.Fill,
                        TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                        Font = new System.Drawing.Font("Segoe UI", 10f)
                    };
                    waitDialog.Controls.Add(waitLabel);
                    // Border
                    waitDialog.Paint += (s2, e2) =>
                    {
                        using (var pen = new Pen(Color.FromArgb(0, 122, 204), 1))
                            e2.Graphics.DrawRectangle(pen, 0, 0, waitDialog.Width - 1, waitDialog.Height - 1);
                    };
                    waitDialog.Show();
                    Application.DoEvents();
                }
                catch { }

                // From Mart: existing version diff
                string diff = DdlGenerationService.GenerateDiffWithDuplicate(
                    _scapi, _currentModel, feOptionXml, (Action<string>)Log);

                // Close wait dialog
                try { waitDialog?.Close(); waitDialog?.Dispose(); } catch { }

                if (diff != null)
                {
                    ShowDDLResult(diff, "DDL Diff");
                }
                else
                {
                    // Direct open failed - fall back to PU watcher
                    _pendingDDLVersion = selVer;
                    _pendingDDLFeOption = feOptionXml;
                    StartPUWatcher();

                    lblDDLStatus.Text = $"Please open v{selVer} from erwin Mart. DDL will generate automatically.";
                    lblDDLStatus.ForeColor = Color.DarkOrange;
                }
            }
            catch (Exception ex)
            {
                lblDDLStatus.Text = $"Error: {ex.Message}";
                lblDDLStatus.ForeColor = Color.Red;
                Log($"DDL Generation error: {ex.Message}");
            }
            finally
            {
                _validationCoordinatorService?.ResumeValidation();
                btnGenerateDDL.Enabled = true;

                // Force focus back to add-in (DdlHelper may have shifted focus)
                this.TopMost = true;
                this.Activate();
                this.BringToFront();
                this.TopMost = false;
            }
        }

        private bool HasModelUnsavedChanges()
        {
            try
            {
                var hWnd = Services.Win32Helper.GetErwinMainWindow();
                if (hWnd == IntPtr.Zero) return false;

                var sb = new System.Text.StringBuilder(512);
                Services.Win32Helper.GetWindowTextPublic(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                // erwin shows "*" in title when model has unsaved changes
                // e.g., "erwin DM - [Mart://Mart/KKB/KKB_Demo : v4 : ER_Diagram_164 * ]"
                bool hasChanges = title.Contains("* ]") || title.Contains("*]");
                Log($"DDL: Window title='{title}', unsaved changes={hasChanges}");
                return hasChanges;
            }
            catch { return false; }
        }

        private void ShowDDLResult(string content, string label)
        {
            if (string.IsNullOrEmpty(content))
            {
                lblDDLStatus.Text = "No output generated.";
                lblDDLStatus.ForeColor = Color.OrangeRed;
                return;
            }

            string displayContent = content;

            // Check if content is HTML (from CompleteCompare) - parse to text
            if (content.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                displayContent = ParseCompleteCompareHtml(content);

            // Apply diagram selection filter if "Only Selected Objects" is checked
            displayContent = FilterByDiagramSelection(displayContent);

            ApplySqlHighlighting(displayContent);
            int lineCount = displayContent.Split('\n').Length;
            if (!chkFilterObjects.Checked || lblDDLStatus.Text == "")
            {
                lblDDLStatus.Text = $"{label}: {lineCount} lines.";
                lblDDLStatus.ForeColor = Color.DarkGreen;
            }
        }

        private string ParseCompleteCompareHtml(string html)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("-- CompleteCompare Results");
            sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // Parse HTML table rows
            int pos = 0;
            int diffCount = 0;

            while (true)
            {
                int trStart = html.IndexOf("<tr>", pos, StringComparison.OrdinalIgnoreCase);
                if (trStart < 0) break;
                int trEnd = html.IndexOf("</tr>", trStart, StringComparison.OrdinalIgnoreCase);
                if (trEnd < 0) break;

                string trContent = html.Substring(trStart, trEnd - trStart + 5);
                pos = trEnd + 5;

                var cells = new List<string>();
                int tdPos = 0;
                while (true)
                {
                    int tdStart = trContent.IndexOf("<td", tdPos, StringComparison.OrdinalIgnoreCase);
                    if (tdStart < 0) break;
                    int tdContentStart = trContent.IndexOf(">", tdStart) + 1;
                    int tdEnd = trContent.IndexOf("</td>", tdContentStart, StringComparison.OrdinalIgnoreCase);
                    if (tdEnd < 0) break;
                    string cellText = trContent.Substring(tdContentStart, tdEnd - tdContentStart)
                        .Replace("&nbsp;", " ").Replace("&amp;", "&").Trim();
                    cells.Add(cellText);
                    tdPos = tdEnd + 5;
                }

                if (cells.Count < 3) continue;

                string type = cells[0];
                string leftVal = cells.Count > 1 ? cells[1] : "";
                string status = cells.Count > 2 ? cells[2] : "";
                string rightVal = cells.Count > 3 ? cells[3] : "";

                if (status.Equals("Equal", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(status) && string.IsNullOrEmpty(leftVal) && string.IsNullOrEmpty(rightVal)) continue;

                string marker = "";
                if (!string.IsNullOrEmpty(leftVal) && string.IsNullOrEmpty(rightVal))
                    marker = "-- NEW: ";
                else if (string.IsNullOrEmpty(leftVal) && !string.IsNullOrEmpty(rightVal))
                    marker = "-- DROPPED: ";
                else if (status.Contains("Not Equal"))
                    marker = "-- CHANGED: ";

                sb.AppendLine($"{marker}{type}: {leftVal} | {status} | {rightVal}");
                diffCount++;
            }

            sb.Insert(sb.ToString().IndexOf('\n') + 1, $"-- {diffCount} difference(s) found\r\n");
            return sb.ToString();
        }

        private void ApplySqlHighlighting(string sql)
        {
            rtbDDLOutput.SuspendLayout();
            rtbDDLOutput.Clear();
            rtbDDLOutput.Text = sql;

            // Set default color
            rtbDDLOutput.SelectAll();
            rtbDDLOutput.SelectionColor = Color.FromArgb(220, 220, 220);

            // IMPORTANT: Use RichTextBox's own text for regex matching
            // RichTextBox converts \r\n to \n internally, so indices differ from original string
            string rtbText = rtbDDLOutput.Text;

            var clrKeyword = Color.FromArgb(86, 156, 214);     // VS Code blue
            var clrType = Color.FromArgb(78, 201, 176);         // VS Code teal
            var clrComment = Color.FromArgb(106, 153, 85);      // VS Code green
            var clrString = Color.FromArgb(206, 145, 120);      // VS Code orange
            var clrNumber = Color.FromArgb(181, 206, 168);      // VS Code light green
            var clrGo = Color.FromArgb(197, 134, 192);          // VS Code purple
            var clrDiffNew = Color.FromArgb(80, 220, 80);       // Bright green
            var clrDiffDrop = Color.FromArgb(240, 80, 80);      // Bright red
            var clrDiffChange = Color.FromArgb(255, 180, 50);   // Bright orange
            var clrSection = Color.FromArgb(220, 220, 100);     // Yellow

            // 1. Keywords (blue)
            HighlightRegex(rtbText, @"\b(CREATE|ALTER|DROP|TABLE|ADD|COLUMN|CONSTRAINT|PRIMARY|KEY|FOREIGN|REFERENCES|NOT|NULL|DEFAULT|IDENTITY|CLUSTERED|NONCLUSTERED|INDEX|UNIQUE|ON|DELETE|UPDATE|CASCADE|SET|CHECK|WITH|ASC|DESC|BEGIN|END|DECLARE|IF|EXISTS|SELECT|FROM|WHERE|AND|OR|RETURN|GOTO|TRIGGER|FOR|INSERT|AS|RAISERROR|ROLLBACK|TRANSACTION|INTO|ACTION)\b", clrKeyword);

            // 2. Data types (teal)
            HighlightRegex(rtbText, @"\b(int|bigint|smallint|tinyint|bit|varchar|nvarchar|char|nchar|text|ntext|datetime|smalldatetime|date|time|timestamp|decimal|numeric|float|real|money|smallmoney|varbinary|binary|image|uniqueidentifier|VARCHAR2|NUMBER|CLOB|BLOB|COLLATE)\b", clrType);

            // 3. Numbers (light green)
            HighlightRegex(rtbText, @"(?<![a-zA-Z_])\d+(?![a-zA-Z_])", clrNumber);

            // 4. GO (purple)
            HighlightRegex(rtbText, @"(?m)^go$", clrGo);

            // 5. String literals (orange)
            HighlightRegex(rtbText, @"'[^']*'", clrString);

            // 6. Comments (green) - overrides keywords inside comments
            HighlightRegex(rtbText, @"--[^\n]*", clrComment);

            // 7. Diff markers (override comment color)
            HighlightRegex(rtbText, @"-- NEW:.*", clrDiffNew);
            HighlightRegex(rtbText, @"-- DROPPED:.*", clrDiffDrop);
            HighlightRegex(rtbText, @"-- CHANGED:.*", clrDiffChange);
            HighlightRegex(rtbText, @"-- =+.*=+", clrSection);
            HighlightRegex(rtbText, @"-- Summary:.*", clrSection);
            HighlightRegex(rtbText, @"-- WARNING:.*", clrDiffDrop);

            rtbDDLOutput.SelectionStart = 0;
            rtbDDLOutput.SelectionLength = 0;
            rtbDDLOutput.ResumeLayout();
        }

        private void HighlightRegex(string rtbText, string pattern, Color color)
        {
            try
            {
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(rtbText, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.Multiline))
                {
                    rtbDDLOutput.Select(m.Index, m.Length);
                    rtbDDLOutput.SelectionColor = color;
                }
            }
            catch { }
        }

        private void StartPUWatcher()
        {
            StopPUWatcher();
            try { _puWatcherInitialCount = _scapi.PersistenceUnits.Count; } catch { _puWatcherInitialCount = 1; }

            _puWatcherTimer = new Timer { Interval = 500 };
            _puWatcherTimer.Tick += PUWatcher_Tick;
            _puWatcherTimer.Start();
            Log($"DDL: PU watcher started (initial count={_puWatcherInitialCount})");
        }

        private void StopPUWatcher()
        {
            if (_puWatcherTimer != null)
            {
                _puWatcherTimer.Stop();
                _puWatcherTimer.Dispose();
                _puWatcherTimer = null;
            }
        }

        private void PUWatcher_Tick(object sender, EventArgs e)
        {
            try
            {
                int currentCount = _scapi.PersistenceUnits.Count;
                if (currentCount > _puWatcherInitialCount && _pendingDDLVersion > 0)
                {
                    Log($"DDL: PU count changed {_puWatcherInitialCount} -> {currentCount}. Auto-generating DDL...");
                    StopPUWatcher();

                    int ver = _pendingDDLVersion;
                    string feOpt = _pendingDDLFeOption;
                    _pendingDDLVersion = 0;

                    // Set selected version and generate DDL
                    DdlGenerationService.SetSelectedVersion(ver);

                    btnGenerateDDL.Enabled = false;
                    lblDDLStatus.Text = $"v{ver} detected. Generating DDL diff...";
                    lblDDLStatus.ForeColor = Color.Gray;
                    Application.DoEvents();

                    _validationCoordinatorService?.SuspendValidation();
                    try
                    {
                        string diff = DdlGenerationService.GenerateDiffWithDuplicate(
                            _scapi, _currentModel, feOpt, (Action<string>)Log);
                        ShowDDLResult(diff, "DDL Diff");
                    }
                    catch (Exception ex)
                    {
                        lblDDLStatus.Text = $"Error: {ex.Message}";
                        lblDDLStatus.ForeColor = Color.Red;
                    }
                    finally
                    {
                        _validationCoordinatorService?.ResumeValidation();
                        btnGenerateDDL.Enabled = true;
                        this.Activate();
                        this.BringToFront();
                    }
                }
            }
            catch { }

            // Timeout after 60 seconds
            if (_puWatcherTimer != null)
            {
                // Simple timeout: stop after 120 ticks (60s at 500ms)
            }
        }

        /// <summary>
        /// Reads diagram selection from erwin's Overview pane and filters DDL diff content
        /// to only include statements related to the selected entities.
        /// Returns the original content if checkbox is unchecked or no entity is selected.
        /// </summary>
        private string FilterByDiagramSelection(string content)
        {
            if (!chkFilterObjects.Checked || string.IsNullOrEmpty(content))
                return content;

            // Read current diagram selection
            var hWnd = Services.Win32Helper.GetErwinMainWindow();
            if (hWnd == IntPtr.Zero) return content;

            string modelName = "";
            try
            {
                dynamic pu = _scapi.PersistenceUnits.Item(0);
                modelName = pu.Name?.ToString() ?? "";
            }
            catch { }
            if (string.IsNullOrEmpty(modelName))
                modelName = _connectedModelName ?? "";

            if (string.IsNullOrEmpty(modelName)) return content;

            var selectedEntities = Services.Win32Helper.GetDiagramSelectedEntities(hWnd, modelName);
            if (selectedEntities.Count == 0)
            {
                Log("DDL: 'Only Selected Objects' checked but no entity selected in diagram.");
                lblDDLStatus.Text = "No entity selected in diagram. Showing all.";
                lblDDLStatus.ForeColor = Color.OrangeRed;
                return content;
            }

            Log($"DDL: Filtering by diagram selection: {string.Join(", ", selectedEntities)}");

            var selected = new HashSet<string>(selectedEntities, StringComparer.OrdinalIgnoreCase);
            var sb = new System.Text.StringBuilder();
            bool inSelectedBlock = false;
            bool inHeader = true;

            foreach (var line in content.Split('\n'))
            {
                string trimmed = line.Trim();

                // Keep header lines
                if (inHeader && trimmed.StartsWith("--") && !trimmed.StartsWith("-- NEW:") &&
                    !trimmed.StartsWith("-- DROPPED:") && !trimmed.StartsWith("-- CHANGED:") &&
                    !trimmed.StartsWith("-- ===="))
                {
                    sb.AppendLine(line.TrimEnd());
                    continue;
                }

                if (trimmed.StartsWith("-- ===="))
                {
                    inHeader = false;
                    sb.AppendLine(line.TrimEnd());
                    continue;
                }

                // Check diff marker lines
                if (trimmed.StartsWith("-- NEW:") || trimmed.StartsWith("-- DROPPED:") || trimmed.StartsWith("-- CHANGED:"))
                {
                    int colonIdx = trimmed.IndexOf(':', 3);
                    string key = colonIdx > 0 ? trimmed.Substring(colonIdx + 1).Trim() : "";

                    string objName = "";
                    if (key.Contains(":"))
                    {
                        string afterColon = key.Split(':')[1];
                        objName = afterColon.Contains(".") ? afterColon.Split('.')[0] : afterColon;
                    }

                    inSelectedBlock = selected.Contains(objName);

                    // Check trigger naming: tD_TableName, tU_TableName, tI_TableName
                    if (!inSelectedBlock && !string.IsNullOrEmpty(objName))
                    {
                        string triggerTable = objName;
                        if (triggerTable.StartsWith("tD_") || triggerTable.StartsWith("tU_") || triggerTable.StartsWith("tI_"))
                            triggerTable = triggerTable.Substring(3);
                        inSelectedBlock = selected.Contains(triggerTable);
                    }
                }

                if (inSelectedBlock)
                    sb.AppendLine(line.TrimEnd());

                if (trimmed.Equals("go", StringComparison.OrdinalIgnoreCase) && inSelectedBlock)
                    inSelectedBlock = true; // keep going until next marker
            }

            string filtered = sb.ToString();
            lblDDLStatus.Text = $"Filtered: {string.Join(", ", selectedEntities)}";
            lblDDLStatus.ForeColor = Color.DarkGreen;
            return filtered;
        }

        private void BtnBrowseFEOption_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select FE Option Set XML";
                dlg.Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtFEOptionXml.Text = dlg.FileName;
                    txtFEOptionXml.ForeColor = System.Drawing.SystemColors.WindowText;
                }
            }
        }

        private void BtnCopyDDL_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(rtbDDLOutput.Text))
            {
                Clipboard.SetText(rtbDDLOutput.Text);
                lblDDLStatus.Text = "DDL copied to clipboard!";
                lblDDLStatus.ForeColor = Color.DarkGreen;
            }
        }

        // DB connection state (From DB mode)
        private string _dbConnectionString = "";
        private string _dbPassword = "";
        private string _dbLabel = "";
        private long _dbTargetServer = 0;
        private int _dbTargetVersion = 0;
        private string _dbSchema = "";
        private List<string> _dbSelectedTables = new List<string>();
        // Raw fields for in-process RE pipeline (DSN created at runtime).
        private string _dbHost = "";
        private string _dbName = "";
        private string _dbUser = "";
        private bool _dbUseWindowsAuth = false;
        private int _dbTypeCode = 0;

        /// <summary>
        /// "From DB" path: compare active model DDL against a DDL reverse-engineered
        /// from a live database by DdlHelper (separate process, silent, no manual prompts).
        /// Mirrors the Mart flow: validate -> wait dialog -> background call -> ShowDDLResult.
        /// </summary>
        private void RunFromDbCompare(string feOptionXml)
        {
            // Validation: connection
            bool needsConfigure = string.IsNullOrEmpty(_dbConnectionString)
                || _dbConnectionString.Contains("|5=\n") || _dbConnectionString.EndsWith("|5=");
            if (needsConfigure)
            {
                _dbConnectionString = "";
                BtnConfigureDB_Click(null, EventArgs.Empty);
                if (string.IsNullOrEmpty(_dbConnectionString)) return;
            }

            // Tables: default to ALL model tables when user hasn't picked explicitly
            var tableList = new List<string>(_dbSelectedTables ?? new List<string>());
            if (tableList.Count == 0)
            {
                tableList = CollectModelTablePhysicalNames();
                if (tableList.Count == 0)
                {
                    ErwinAddIn.ShowTopMostMessage("Active model has no tables to compare.", "From DB");
                    return;
                }
                Log($"DDL: From DB using default (all {tableList.Count} model tables).");
            }

            // Schema validation: RE returns empty if no schema/owner filter
            if (string.IsNullOrWhiteSpace(_dbSchema))
            {
                ErwinAddIn.ShowTopMostMessage(
                    "DB schema/owner is required for reverse engineering.\n\n" +
                    "Open 'Configure...' and set the schema filter (e.g. dbo).",
                    "From DB", isError: false);
                BtnConfigureDB_Click(null, EventArgs.Empty);
                if (string.IsNullOrWhiteSpace(_dbSchema)) return;
            }

            btnGenerateDDL.Enabled = false;
            rtbDDLOutput.Text = "";
            lblDDLStatus.Text = $"Reverse engineering {tableList.Count} table(s) from DB...";
            lblDDLStatus.ForeColor = Color.Gray;
            Application.DoEvents();

            _validationCoordinatorService?.SuspendValidation();

            Form waitDialog = null;
            try
            {
                try
                {
                    waitDialog = new Form
                    {
                        Size = new System.Drawing.Size(380, 120),
                        FormBorderStyle = FormBorderStyle.None,
                        StartPosition = FormStartPosition.CenterScreen,
                        TopMost = true,
                        ShowInTaskbar = false,
                        BackColor = Color.White
                    };
                    var titlePanel = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(0, 122, 204) };
                    titlePanel.Controls.Add(new Label
                    {
                        Text = "DDL Generation",
                        ForeColor = Color.White,
                        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                        Location = new Point(10, 5),
                        AutoSize = true
                    });
                    waitDialog.Controls.Add(titlePanel);
                    waitDialog.Controls.Add(new Label
                    {
                        Text = $"Reverse engineering from DB\n({tableList.Count} table(s))...\nPlease wait.",
                        Dock = DockStyle.Fill,
                        TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                        Font = new Font("Segoe UI", 10f)
                    });
                    waitDialog.Paint += (s2, e2) =>
                    {
                        using (var pen = new Pen(Color.FromArgb(0, 122, 204), 1))
                            e2.Graphics.DrawRectangle(pen, 0, 0, waitDialog.Width - 1, waitDialog.Height - 1);
                    };
                    waitDialog.Show();
                    Application.DoEvents();
                }
                catch { }

                string diff = DdlGenerationService.GenerateDiffWithDatabase(
                    _scapi,
                    _currentModel,
                    _dbHost,
                    _dbName,
                    _dbUser,
                    _dbPassword,
                    _dbUseWindowsAuth,
                    _dbTypeCode,
                    _dbTargetServer,
                    _dbTargetVersion,
                    _dbSchema,
                    tableList,
                    _dbLabel,
                    feOptionXml,
                    (Action<string>)Log);

                try { waitDialog?.Close(); waitDialog?.Dispose(); } catch { }

                if (!string.IsNullOrEmpty(diff))
                {
                    ShowDDLResult(diff, $"DDL Diff vs {_dbLabel}");
                    lblDDLStatus.Text = $"Diff computed vs {_dbLabel}.";
                    lblDDLStatus.ForeColor = Color.DarkGreen;
                }
                else
                {
                    lblDDLStatus.Text = "From DB comparison failed. See log.";
                    lblDDLStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                try { waitDialog?.Close(); waitDialog?.Dispose(); } catch { }
                lblDDLStatus.Text = $"Error: {ex.Message}";
                lblDDLStatus.ForeColor = Color.Red;
                Log($"DDL From DB error: {ex.Message}");
            }
            finally
            {
                _validationCoordinatorService?.ResumeValidation();
                btnGenerateDDL.Enabled = true;
                this.TopMost = true;
                this.Activate();
                this.BringToFront();
                this.TopMost = false;
            }
        }

        /// <summary>
        /// Returns the physical_name of every entity in the active model. Empty if no session.
        /// </summary>
        private List<string> CollectModelTablePhysicalNames()
        {
            var result = new List<string>();
            if (_currentModel == null) return result;
            dynamic sess = null;
            bool ownSess = false;
            try
            {
                bool hasSess = false;
                try { hasSess = _currentModel.HasSession(); } catch { }
                if (hasSess)
                {
                    int sc = 0; try { sc = _scapi.Sessions.Count; } catch { }
                    for (int i = 0; i < sc; i++)
                    {
                        try
                        {
                            dynamic s = _scapi.Sessions.Item(i);
                            bool open = false; try { open = s.IsOpen(); } catch { }
                            string puN = ""; try { puN = s.PersistenceUnit?.Name?.ToString() ?? ""; } catch { }
                            string curN = ""; try { curN = _currentModel.Name?.ToString() ?? ""; } catch { }
                            if (open && puN == curN) { sess = s; break; }
                        }
                        catch { }
                    }
                }
                if (sess == null)
                {
                    sess = _scapi.Sessions.Add();
                    sess.Open(_currentModel, 0, 0);
                    ownSess = true;
                }
                dynamic mo = sess.ModelObjects;
                dynamic ents = mo.Collect(mo.Root, "Entity");
                foreach (dynamic ent in ents)
                {
                    try
                    {
                        string phys = "";
                        try { phys = ent.Properties("Physical_Name")?.Value?.ToString() ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(phys))
                            try { phys = ent.Name?.ToString() ?? ""; } catch { }
                        if (!string.IsNullOrWhiteSpace(phys) && !result.Contains(phys))
                            result.Add(phys);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log($"DDL: CollectModelTablePhysicalNames error: {ex.Message}"); }
            finally
            {
                if (ownSess && sess != null) { try { sess.Close(); } catch { } }
            }
            return result;
        }

        private void OnRightSourceChanged()
        {
            bool fromMart = rbFromMart.Checked;
            cmbRightModel.Visible = fromMart;
            btnConfigureDB.Visible = !fromMart;
            btnSelectDbTables.Visible = !fromMart;
            lblSelectedTableCount.Visible = !fromMart;
            UpdateSelectedTableCountLabel();

            if (!fromMart)
            {
                if (string.IsNullOrEmpty(_dbConnectionString) || string.IsNullOrEmpty(_dbLabel))
                {
                    lblDDLStatus.Text = "Click 'Configure...' to set database connection.";
                    lblDDLStatus.ForeColor = Color.Gray;
                }
                else
                {
                    lblDDLStatus.Text = $"DB: {_dbLabel}";
                    lblDDLStatus.ForeColor = Color.DarkGreen;
                }
            }
        }

        private void BtnConfigureDB_Click(object sender, EventArgs e)
        {
            using (var dlg = new Forms.DbConnectionForm())
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _dbConnectionString = dlg.ConnectionString;
                    _dbPassword = dlg.Password;
                    _dbLabel = dlg.DisplayLabel;
                    _dbTargetServer = dlg.TargetServerCode;
                    _dbTargetVersion = dlg.TargetServerVersion;
                    _dbSchema = dlg.SchemaFilter;
                    _dbHost = dlg.ServerHost;
                    _dbName = dlg.DatabaseName;
                    _dbUser = dlg.UserName;
                    _dbUseWindowsAuth = dlg.UseWindowsAuth;
                    _dbTypeCode = dlg.DbTypeCode;
                    lblDDLStatus.Text = $"DB configured: {_dbLabel}";
                    lblDDLStatus.ForeColor = Color.DarkGreen;
                    Log($"DDL: DB configured: {_dbLabel}, host={_dbHost}, db={_dbName}, user={_dbUser}, winAuth={_dbUseWindowsAuth}, schema='{_dbSchema}'");
                }
            }
        }

        private void UpdateSelectedTableCountLabel()
        {
            if (_dbSelectedTables != null && _dbSelectedTables.Count > 0)
                lblSelectedTableCount.Text = $"{_dbSelectedTables.Count} table(s) selected";
            else
                lblSelectedTableCount.Text = "(defaults to ALL model tables)";
        }

        /// <summary>
        /// Collect entity physical names from the active model, show a CheckedListBox picker.
        /// User can narrow the RE scope before running Generate DDL.
        /// </summary>
        private void BtnSelectDbTables_Click(object sender, EventArgs e)
        {
            if (!_isConnected || _currentModel == null)
            {
                ErwinAddIn.ShowTopMostMessage("No model connected.", "Select DB Tables");
                return;
            }

            // Pull physical names from the active model
            var modelTables = new List<string>();
            dynamic tempSess = null;
            bool ownSession = false;
            try
            {
                // Reuse existing session if possible
                bool hasSess = false;
                try { hasSess = _currentModel.HasSession(); } catch { }

                if (hasSess)
                {
                    int sessCount = 0;
                    try { sessCount = _scapi.Sessions.Count; } catch { }
                    for (int si = 0; si < sessCount; si++)
                    {
                        try
                        {
                            dynamic s = _scapi.Sessions.Item(si);
                            bool open = false; try { open = s.IsOpen(); } catch { }
                            string puN = ""; try { puN = s.PersistenceUnit?.Name?.ToString() ?? ""; } catch { }
                            string curN = ""; try { curN = _currentModel.Name?.ToString() ?? ""; } catch { }
                            if (open && puN == curN) { tempSess = s; break; }
                        }
                        catch { }
                    }
                }
                if (tempSess == null)
                {
                    tempSess = _scapi.Sessions.Add();
                    tempSess.Open(_currentModel, 0, 0);
                    ownSession = true;
                }

                dynamic mo = tempSess.ModelObjects;
                dynamic root = mo.Root;
                dynamic ents = mo.Collect(root, "Entity");
                foreach (dynamic ent in ents)
                {
                    try
                    {
                        string phys = "";
                        try { phys = ent.Properties("Physical_Name")?.Value?.ToString() ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(phys))
                            try { phys = ent.Name?.ToString() ?? ""; } catch { }
                        if (!string.IsNullOrWhiteSpace(phys) && !modelTables.Contains(phys))
                            modelTables.Add(phys);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"DDL: Failed to enumerate model tables: {ex.Message}");
                ErwinAddIn.ShowTopMostMessage($"Could not enumerate model tables: {ex.Message}", "Select DB Tables");
                return;
            }
            finally
            {
                if (ownSession && tempSess != null) { try { tempSess.Close(); } catch { } }
            }

            if (modelTables.Count == 0)
            {
                ErwinAddIn.ShowTopMostMessage("Active model has no tables.", "Select DB Tables");
                return;
            }

            modelTables.Sort(StringComparer.OrdinalIgnoreCase);

            // Default: if user hasn't selected before, all are checked
            var preChecked = new HashSet<string>(
                (_dbSelectedTables != null && _dbSelectedTables.Count > 0)
                    ? _dbSelectedTables
                    : modelTables,
                StringComparer.OrdinalIgnoreCase);

            var result = ShowTableSelectionDialog(modelTables, preChecked);
            if (result != null)
            {
                _dbSelectedTables = result;
                UpdateSelectedTableCountLabel();
                Log($"DDL: {result.Count} table(s) selected for From DB compare.");
            }
        }

        /// <summary>
        /// Lightweight inline dialog: CheckedListBox + Select All/None + OK/Cancel.
        /// Returns selected table list or null on cancel.
        /// </summary>
        private List<string> ShowTableSelectionDialog(List<string> tables, HashSet<string> preChecked)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Select DB Tables to Compare";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.Size = new System.Drawing.Size(420, 520);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.TopMost = true;

                var lbl = new Label
                {
                    Text = $"Model tables ({tables.Count}). Checked = include in DB reverse engineer.",
                    Location = new System.Drawing.Point(12, 10),
                    Size = new System.Drawing.Size(380, 30)
                };
                dlg.Controls.Add(lbl);

                var clb = new CheckedListBox
                {
                    Location = new System.Drawing.Point(12, 45),
                    Size = new System.Drawing.Size(378, 380),
                    CheckOnClick = true,
                    IntegralHeight = false
                };
                foreach (var t in tables)
                    clb.Items.Add(t, preChecked.Contains(t));
                dlg.Controls.Add(clb);

                var btnAll = new Button
                {
                    Text = "Select All",
                    Location = new System.Drawing.Point(12, 432),
                    Size = new System.Drawing.Size(90, 26)
                };
                btnAll.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, true);
                };
                dlg.Controls.Add(btnAll);

                var btnNone = new Button
                {
                    Text = "Select None",
                    Location = new System.Drawing.Point(108, 432),
                    Size = new System.Drawing.Size(90, 26)
                };
                btnNone.Click += (s, e) =>
                {
                    for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, false);
                };
                dlg.Controls.Add(btnNone);

                var btnOk = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new System.Drawing.Point(208, 432),
                    Size = new System.Drawing.Size(85, 26)
                };
                dlg.Controls.Add(btnOk);
                dlg.AcceptButton = btnOk;

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new System.Drawing.Point(300, 432),
                    Size = new System.Drawing.Size(85, 26)
                };
                dlg.Controls.Add(btnCancel);
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;

                var selected = new List<string>();
                foreach (var item in clb.CheckedItems) selected.Add(item.ToString());
                return selected;
            }
        }

        private int _martVersion = 0;
        private string _martLocator = "";
        private int _pendingDDLVersion = 0;
        private string _pendingDDLFeOption = "";
        private Timer _puWatcherTimer;
        private int _puWatcherInitialCount;

        /// <summary>
        /// Populate Left/Right model version combo boxes.
        /// Version is read from PU locator or erwin window title.
        /// </summary>
        private void PopulateVersionCombos()
        {
            cmbLeftModel.Items.Clear();
            cmbRightModel.Items.Clear();

            try
            {
                // Try PU locator first
                string locator = "";
                try { locator = _currentModel.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }
                _martLocator = locator;

                int version = DdlGenerationService.ParseVersionFromLocator(locator);

                // If locator didn't have version, try erwin window title
                // Format: "erwin DM - [Mart://Mart/KKB/KKB_Demo : v4 : ER_Diagram_164 * ]"
                if (version <= 1)
                {
                    try
                    {
                        var hWnd = Services.Win32Helper.GetErwinMainWindow();
                        if (hWnd != IntPtr.Zero)
                        {
                            var sb = new System.Text.StringBuilder(512);
                            Services.Win32Helper.GetWindowTextPublic(hWnd, sb, sb.Capacity);
                            string title = sb.ToString();

                            var match = System.Text.RegularExpressions.Regex.Match(title, @":\s*v(\d+)\s*:");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int titleVer))
                            {
                                version = titleVer;
                                Log($"DDL: Version from window title = v{version}");
                            }
                        }
                    }
                    catch { }
                }

                _martVersion = version;
                string modelName = _connectedModelName ?? "Model";
                Log($"DDL: Model='{modelName}', Version={version}, Locator='{locator}'");

                // Left model: Active Model
                string leftLabel = version > 1 ? $"Active Model (v{version})" : "Active Model";
                cmbLeftModel.Items.Add(leftLabel);
                cmbLeftModel.SelectedIndex = 0;

                // Right model: list versions from Mart (only for Mart models)
                var versions = DdlGenerationService.GetMartVersions(modelName, (object)_currentModel, (Action<string>)Log);

                if (versions.Count > 0)
                {
                    foreach (var v in versions)
                    {
                        string label = $"v{v.Version}" + (!string.IsNullOrEmpty(v.Name) ? $" ({v.Name})" : "");
                        cmbRightModel.Items.Add(label);
                    }

                    // Default: select the version before current
                    if (versions.Count > 1)
                        cmbRightModel.SelectedIndex = 0; // Most recent after current
                    else
                        cmbRightModel.SelectedIndex = 0;
                }
                else
                {
                    cmbRightModel.Items.Add("Mart Baseline (connect-time)");
                    cmbRightModel.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Log($"PopulateVersionCombos error: {ex.Message}");
                cmbLeftModel.Items.Add("Active Model");
                cmbLeftModel.SelectedIndex = 0;
                cmbRightModel.Items.Add("(Mart Baseline)");
                cmbRightModel.SelectedIndex = 0;
            }
        }

        #endregion

        #region Debug Log

        private void Log(string message)
        {
            if (IsDisposed || txtDebugLog == null || txtDebugLog.IsDisposed) return;

            if (InvokeRequired)
            {
                try { Invoke(new Action(() => Log(message))); } catch { }
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string line = $"[{timestamp}] {message}\r\n";
            _fullLogText += line;

            // Only append if no search filter is active
            string filter = txtLogSearch?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(filter) || line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                txtDebugLog.AppendText(line);
                txtDebugLog.SelectionStart = txtDebugLog.Text.Length;
                txtDebugLog.ScrollToCaret();
            }
        }

        private string _fullLogText = "";


        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            _fullLogText = "";
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

        private void TxtLogSearch_TextChanged(object sender, EventArgs e)
        {
            string filter = txtLogSearch.Text.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                txtDebugLog.Text = _fullLogText;
            }
            else
            {
                var lines = _fullLogText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                var filtered = lines.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                txtDebugLog.Text = string.Join("\r\n", filtered);
            }
        }

        /// <summary>
        /// Manual snapshot of ALL PUs (PropertyBag, model objects, FEModel_DDL).
        /// Press this AFTER manually completing RE in erwin to dump the resulting state.
        /// SAFE MODE: dumps Active_Model PU first, never opens temp Session on already-open PU,
        /// FEModel_DDL is OPT-IN (Ctrl held while clicking) to avoid erwin crashes.
        /// </summary>
        private void BtnCaptureNow_Click(object sender, EventArgs e)
        {
            btnCaptureNow.Enabled = false;
            bool includeDdl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            try
            {
                Log("");
                Log("========================================");
                Log("=== CAPTURE NOW: All PUs Snapshot ===");
                Log("========================================");
                Log($"Timestamp: {DateTime.Now:HH:mm:ss.fff}");
                Log($"Include FEModel_DDL: {includeDdl} (hold Ctrl while clicking to enable)");

                int puCount = 0;
                try { puCount = _scapi.PersistenceUnits.Count; } catch (Exception ex) { Log($"PU count error: {ex.Message}"); }
                int sessCount = 0;
                try { sessCount = _scapi.Sessions.Count; } catch { }
                Log($"PUs: {puCount}, Sessions: {sessCount}");
                Log("");

                // Build session-by-PU map (for safe model inspection without opening NEW sessions)
                var sessionByPuName = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
                Log("--- Sessions Overview ---");
                for (int si = 0; si < sessCount; si++)
                {
                    try
                    {
                        dynamic sess = _scapi.Sessions.Item(si);
                        string sName = ""; try { sName = sess.Name?.ToString() ?? ""; } catch { }
                        bool isOpen = false; try { isOpen = sess.IsOpen(); } catch { }
                        string sPU = ""; try { sPU = sess.PersistenceUnit?.Name?.ToString() ?? ""; } catch { }
                        Log($"  Session[{si}]: '{sName}' isOpen={isOpen} PU='{sPU}'");
                        if (isOpen && !string.IsNullOrEmpty(sPU) && !sessionByPuName.ContainsKey(sPU))
                            sessionByPuName[sPU] = sess;
                    }
                    catch (Exception ex) { Log($"  Session[{si}]: error={ex.Message}"); }
                }
                Log("");

                // Build dump order: Active_Model PUs FIRST, then others
                var order = new List<int>();
                var activeIdx = new List<int>();
                var inactiveIdx = new List<int>();
                for (int i = 0; i < puCount; i++)
                {
                    bool active = false;
                    try
                    {
                        dynamic pu = _scapi.PersistenceUnits.Item(i);
                        string v = pu.PropertyBag().Value("Active_Model")?.ToString() ?? "";
                        active = v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1";
                    }
                    catch { }
                    if (active) activeIdx.Add(i); else inactiveIdx.Add(i);
                }
                order.AddRange(activeIdx);
                order.AddRange(inactiveIdx);
                Log($"Dump order (Active first): [{string.Join(",", order)}]");
                Log("");

                foreach (int i in order)
                {
                    try { DumpPUFull(i, sessionByPuName, includeDdl); }
                    catch (Exception ex) { Log($"  PU[{i}] DUMP CRASH: {ex.Message}"); }
                    Log("");
                }

                Log("========================================");
                Log("=== CAPTURE NOW: COMPLETE ===");
                Log("========================================");
                Log("");
            }
            catch (Exception ex)
            {
                Log($"CAPTURE NOW error: {ex.Message}");
                Log(ex.StackTrace ?? "");
            }
            finally
            {
                btnCaptureNow.Enabled = true;
            }
        }


        // === Live RE Monitor: high-frequency PropertyBag diff ===
        private System.Windows.Forms.Timer _liveMonTimer;
        private Dictionary<string, Dictionary<string, string>> _liveMonLastBags;
        private int _liveMonLastPuCount = -1;
        private int _liveMonLastSessCount = -1;
        private int _liveMonLastMsgLogCount = -1;
        private int _liveMonLastDirCount = -1;
        private Dictionary<int, int> _liveMonLastSessEntCounts = new Dictionary<int, int>();
        private int _liveMonTickNo = 0;
        private bool _liveMonRunning = false;
        private HashSet<string> _liveMonLoggedErrors = new HashSet<string>();

        /// <summary>
        /// Log a caught exception once per (source, type, message) tuple within this monitor session.
        /// Satisfies the "no silent error swallowing" rule without flooding the 100ms tick loop.
        /// </summary>
        private void MonLogOnce(string source, Exception ex)
        {
            string key = $"{source}|{ex.GetType().Name}|{ex.Message}";
            if (_liveMonLoggedErrors.Add(key))
                Log($"  [silent err] {source}: {ex.GetType().Name}: {ex.Message}");
        }

        /// <summary>
        /// Toggle a 100ms poll loop that snapshots every PU's PropertyBag and logs every diff.
        /// Intended to capture exactly which keys/values erwin writes when the USER manually
        /// runs Reverse Engineer through erwin's own wizard.
        /// </summary>
        private void BtnLiveReMon_Click(object sender, EventArgs e)
        {
            if (_liveMonRunning)
            {
                try { _liveMonTimer?.Stop(); _liveMonTimer?.Dispose(); } catch { }
                _liveMonTimer = null;
                _liveMonRunning = false;
                btnLiveReMon.Text = "Live RE Mon";
                btnLiveReMon.BackColor = System.Drawing.Color.FromArgb(255, 220, 100);
                Log("=== LIVE RE MON STOPPED ===");
                return;
            }

            _liveMonLastBags = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _liveMonLastPuCount = -1;
            _liveMonLastSessCount = -1;
            _liveMonLastMsgLogCount = -1;
            _liveMonLastDirCount = -1;
            _liveMonLastSessEntCounts = new Dictionary<int, int>();
            _liveMonLoggedErrors = new HashSet<string>();
            _liveMonTickNo = 0;
            _liveMonRunning = true;
            btnLiveReMon.Text = "STOP Monitor";
            btnLiveReMon.BackColor = System.Drawing.Color.FromArgb(255, 150, 150);

            Log("");
            Log("========================================");
            Log("=== LIVE RE MONITOR STARTED (100ms) ===");
            Log("========================================");
            Log("Now run Reverse Engineer in erwin. Every PropertyBag change will be logged.");
            Log("Press the button again to STOP.");
            Log("");

            _liveMonTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _liveMonTimer.Tick += (s, ev) => LiveMonTick();
            _liveMonTimer.Start();
        }

        private void LiveMonTick()
        {
            _liveMonTickNo++;
            long t = _liveMonTickNo * 100;
            try
            {
                // ----- PU count -----
                int puCount = 0;
                try { puCount = _scapi.PersistenceUnits.Count; }
                catch (Exception ex) { MonLogOnce("PU.Count", ex); }
                if (puCount != _liveMonLastPuCount)
                {
                    if (_liveMonLastPuCount != -1)
                        Log($"[{t}ms] *** PU count: {_liveMonLastPuCount} -> {puCount} ***");
                    _liveMonLastPuCount = puCount;
                }

                // ----- Session count -----
                int sessCount = 0;
                try { sessCount = _scapi.Sessions.Count; }
                catch (Exception ex) { MonLogOnce("Sessions.Count", ex); }
                if (sessCount != _liveMonLastSessCount)
                {
                    if (_liveMonLastSessCount != -1)
                        Log($"[{t}ms] *** Session count: {_liveMonLastSessCount} -> {sessCount} ***");
                    _liveMonLastSessCount = sessCount;
                }

                // ----- ModelDirectories diff (DB/Mart connections live here) -----
                int dirCount = 0;
                try { dirCount = _scapi.ModelDirectories.Count; }
                catch (Exception ex) { MonLogOnce("ModelDirectories.Count", ex); }
                if (dirCount != _liveMonLastDirCount)
                {
                    if (_liveMonLastDirCount != -1)
                    {
                        Log($"[{t}ms] *** ModelDirectories count: {_liveMonLastDirCount} -> {dirCount} ***");
                        for (int di = 0; di < dirCount; di++)
                        {
                            try
                            {
                                dynamic dir = _scapi.ModelDirectories.Item(di);
                                Log($"  Dir[{di}] PropertyBag:");
                                DumpPropertyBagInline(dir.PropertyBag(), "    ");
                            }
                            catch (Exception ex) { MonLogOnce($"Dir[{di}].dump", ex); }
                        }
                    }
                    _liveMonLastDirCount = dirCount;
                }

                // ----- PUs by Persistence_Unit_Id (stable across re-ordering) -----
                var currentBags = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < puCount; i++)
                {
                    dynamic pu;
                    try { pu = _scapi.PersistenceUnits.Item(i); }
                    catch (Exception ex) { MonLogOnce($"PU[{i}].Item", ex); continue; }

                    string name = "";
                    try { name = pu.Name?.ToString() ?? $"PU_{i}"; }
                    catch (Exception ex) { MonLogOnce($"PU[{i}].Name", ex); name = $"PU_{i}"; }

                    var current = SnapshotPropertyBag(pu, $"PU[{i}].PB");

                    string puId = current.TryGetValue("Persistence_Unit_Id", out string pid) ? pid : $"NOID_{i}_{name}";
                    string label = $"PU#{i}({name},id={puId.Substring(0, Math.Min(12, puId.Length))})";
                    currentBags[puId] = current;

                    if (!_liveMonLastBags.TryGetValue(puId, out Dictionary<string, string> prev))
                    {
                        if (current.Count > 0)
                        {
                            Log($"[{t}ms] +NEW PU {label} INITIAL BAG ({current.Count} props):");
                            foreach (var kv in current)
                            {
                                string v = kv.Value; if (v.Length > 220) v = v.Substring(0, 220) + "...";
                                Log($"  + {kv.Key} = {v}");
                            }
                        }
                    }
                    else
                    {
                        foreach (var kv in current)
                        {
                            if (!prev.TryGetValue(kv.Key, out string oldVal))
                            {
                                string v = kv.Value; if (v.Length > 220) v = v.Substring(0, 220) + "...";
                                Log($"[{t}ms] +{label}.{kv.Key} = {v}");
                            }
                            else if (oldVal != kv.Value)
                            {
                                string oV = oldVal; if (oV.Length > 120) oV = oV.Substring(0, 120) + "...";
                                string nV = kv.Value; if (nV.Length > 120) nV = nV.Substring(0, 120) + "...";
                                Log($"[{t}ms] *{label}.{kv.Key}: '{oV}' -> '{nV}'");
                            }
                        }
                        foreach (var kv in prev)
                        {
                            if (!current.ContainsKey(kv.Key))
                                Log($"[{t}ms] -{label}.{kv.Key} (removed)");
                        }
                    }
                }

                foreach (var oldId in _liveMonLastBags.Keys)
                {
                    if (!currentBags.ContainsKey(oldId))
                        Log($"[{t}ms] -PU id={oldId.Substring(0, Math.Min(12, oldId.Length))} REMOVED");
                }
                _liveMonLastBags = currentBags;

                // ----- Sessions: per-session entity count (RE drops entities here) -----
                var sessSnapshot = new Dictionary<int, int>();
                for (int si = 0; si < sessCount; si++)
                {
                    int entCount = -1;
                    bool open = false;
                    string puN = "";
                    try
                    {
                        dynamic sess = _scapi.Sessions.Item(si);
                        try { open = sess.IsOpen(); }
                        catch (Exception ex) { MonLogOnce($"Sess[{si}].IsOpen", ex); }

                        try { puN = sess.PersistenceUnit?.Name?.ToString() ?? ""; }
                        catch (Exception ex) { MonLogOnce($"Sess[{si}].PUName", ex); }

                        if (open)
                        {
                            try
                            {
                                dynamic mo = sess.ModelObjects;
                                dynamic ents = mo.Collect(mo.Root, "Entity");
                                entCount = ents.Count;
                            }
                            catch (Exception ex) { MonLogOnce($"Sess[{si}].EntityCount", ex); }
                        }
                    }
                    catch (Exception ex) { MonLogOnce($"Sess[{si}].Item", ex); continue; }

                    sessSnapshot[si] = entCount;

                    if (_liveMonLastSessEntCounts.TryGetValue(si, out int prevEnt))
                    {
                        if (prevEnt != entCount)
                            Log($"[{t}ms] *** Session[{si}] entity count: {prevEnt} -> {entCount} (PU='{puN}', open={open}) ***");
                    }
                    else if (open && entCount >= 0)
                    {
                        Log($"[{t}ms] +NEW Session[{si}] open={open} PU='{puN}' entities={entCount}");
                    }
                }
                _liveMonLastSessEntCounts = sessSnapshot;

                // ----- erwin internal MessageLog -----
                dynamic msgBag = null;
                try { msgBag = _scapi.ApplicationEnvironment.PropertyBag("Application.API.MessageLog"); }
                catch (Exception ex) { MonLogOnce("MessageLog.fetch", ex); }

                if (msgBag != null)
                {
                    bool isEmpty = true;
                    try { isEmpty = Convert.ToBoolean(msgBag.Value("Is_Empty")); }
                    catch (Exception ex) { MonLogOnce("MessageLog.Is_Empty", ex); }

                    dynamic logArr = null;
                    try { logArr = msgBag.Value("Log"); }
                    catch (Exception ex) { MonLogOnce("MessageLog.Log", ex); }

                    int arrLen = (logArr is Array a0) ? a0.Length : -1;

                    if (!isEmpty && arrLen != _liveMonLastMsgLogCount)
                    {
                        if (_liveMonLastMsgLogCount == -1)
                            Log($"[{t}ms] MessageLog INITIAL: Is_Empty={isEmpty}, Log.Length={arrLen}");
                        else
                            Log($"[{t}ms] MessageLog growth: len {_liveMonLastMsgLogCount} -> {arrLen} (Is_Empty={isEmpty})");

                        if (logArr is Array arr && arr.Length > 0)
                        {
                            int start = Math.Max(0, _liveMonLastMsgLogCount > 0 ? _liveMonLastMsgLogCount : 0);
                            for (int mi = start; mi < arr.Length; mi++)
                            {
                                try { Log($"  MSG[{mi}]: {arr.GetValue(mi)}"); }
                                catch (Exception mvex) { Log($"  MSG[{mi}]: <err: {mvex.Message}>"); }
                            }
                        }
                        _liveMonLastMsgLogCount = arrLen;
                    }
                    else if (_liveMonLastMsgLogCount == -1)
                    {
                        _liveMonLastMsgLogCount = arrLen;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[{t}ms] Monitor tick error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Snapshot a COM PropertyBag into a Dictionary, logging any read errors via MonLogOnce.
        /// </summary>
        private Dictionary<string, string> SnapshotPropertyBag(dynamic objWithPropertyBag, string source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dynamic pb;
            try { pb = objWithPropertyBag.PropertyBag(); }
            catch (Exception ex) { MonLogOnce($"{source}.get", ex); return result; }

            int cnt = 0;
            try { cnt = pb.Count; }
            catch (Exception ex) { MonLogOnce($"{source}.Count", ex); return result; }

            for (int j = 0; j < cnt; j++)
            {
                string pn = "";
                try { pn = pb.Name(j)?.ToString() ?? ""; }
                catch (Exception ex) { MonLogOnce($"{source}.Name[{j}]", ex); continue; }

                string pv = "";
                try { pv = pb.Value(pn)?.ToString() ?? ""; }
                catch (Exception ex) { pv = $"<err: {ex.Message}>"; }
                result[pn] = pv;
            }
            return result;
        }

        /// <summary>
        /// Inline-dump a PropertyBag (already obtained) line by line. Used for ModelDirectories.
        /// </summary>
        private void DumpPropertyBagInline(dynamic pb, string indent)
        {
            int cnt = 0;
            try { cnt = pb.Count; }
            catch (Exception ex) { MonLogOnce("InlineDump.Count", ex); return; }
            for (int j = 0; j < cnt; j++)
            {
                try
                {
                    string pn = pb.Name(j)?.ToString() ?? "";
                    string pv = "";
                    try { pv = pb.Value(pn)?.ToString() ?? ""; }
                    catch (Exception vex) { pv = $"<err: {vex.Message}>"; }
                    if (pv.Length > 220) pv = pv.Substring(0, 220) + "...";
                    Log($"{indent}{pn} = {pv}");
                }
                catch (Exception ex) { MonLogOnce($"InlineDump[{j}]", ex); }
            }
        }

        /// <summary>
        /// Safe full dump of one PU. Reuses existing Session if PU.HasSession()=true (NEVER opens new).
        /// FEModel_DDL is opt-in via includeDdl flag.
        /// </summary>
        private void DumpPUFull(int index, Dictionary<string, dynamic> sessionByPuName, bool includeDdl)
        {
            Log($"=== PU[{index}] ===");
            dynamic pu = null;
            try { pu = _scapi.PersistenceUnits.Item(index); }
            catch (Exception ex) { Log($"  Item({index}) error: {ex.Message}"); return; }

            string puName = ""; try { puName = pu.Name?.ToString() ?? ""; } catch { }
            Log($"  Name: '{puName}'");

            foreach (var prop in new[] { "FullName", "Path", "Source", "URL", "Type", "Modified", "ReadOnly" })
            {
                try
                {
                    object v = pu.GetType().InvokeMember(prop,
                        System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                        null, pu, null);
                    if (v != null) Log($"  {prop}: {v}");
                }
                catch { }
            }

            bool hasSess = false; try { hasSess = pu.HasSession(); } catch { }
            Log($"  HasSession: {hasSess}");

            // PropertyBag full dump (always safe)
            Log("  --- PropertyBag ---");
            try
            {
                dynamic pb = pu.PropertyBag();
                int cnt = 0; try { cnt = pb.Count; } catch { }
                Log($"    Count: {cnt}");
                for (int j = 0; j < cnt; j++)
                {
                    try
                    {
                        string n = pb.Name(j)?.ToString() ?? "";
                        string v = "";
                        try { v = pb.Value(n)?.ToString() ?? ""; }
                        catch (Exception vex) { v = "<err: " + vex.Message + ">"; }
                        if (v.Length > 250) v = v.Substring(0, 250) + "...(+" + (v.Length - 250) + ")";
                        Log($"    [{j}] {n} = {v}");
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log($"    PB error: {ex.Message}"); }

            // Model inspection: REUSE existing session if available, NEVER open new on already-open PU
            Log("  --- Model Inspection ---");
            dynamic sess = null;
            bool ownSession = false;
            try
            {
                if (hasSess && sessionByPuName.TryGetValue(puName, out dynamic existing))
                {
                    sess = existing;
                    Log("    Using existing open session for this PU");
                }
                else if (!hasSess)
                {
                    sess = _scapi.Sessions.Add();
                    sess.Open(pu, 0, 0);
                    ownSession = true;
                    Log("    Opened temp session (PU had no session)");
                }
                else
                {
                    Log("    SKIP: PU has session but none found in Sessions collection (cannot inspect safely)");
                }

                if (sess != null)
                {
                    dynamic mo = sess.ModelObjects;
                    dynamic root = mo.Root;
                    string rootName = ""; try { rootName = root.Name?.ToString() ?? ""; } catch { }
                    Log($"    Root: '{rootName}'");

                    string[] types = { "Entity", "Attribute", "Relationship", "Key_Group", "View", "Schema", "Index", "Domain", "Subject_Area" };
                    foreach (var t in types)
                    {
                        try
                        {
                            dynamic objs = mo.Collect(root, t);
                            int cnt2 = 0; try { cnt2 = objs.Count; } catch { }
                            Log($"    {t}: {cnt2}");
                            if (t == "Entity" && cnt2 > 0)
                            {
                                int li = 0;
                                foreach (dynamic obj in objs)
                                {
                                    if (li >= 30) { Log("      ... (+" + (cnt2 - 30) + " more)"); break; }
                                    string eName = ""; try { eName = obj.Name?.ToString() ?? ""; } catch { }
                                    string ePhys = ""; try { ePhys = obj.Properties("Physical_Name")?.Value?.ToString() ?? ""; } catch { }
                                    string eOwner = ""; try { eOwner = obj.Properties("Owner")?.Value?.ToString() ?? ""; } catch { }
                                    Log($"      - {eName} (Physical: {ePhys}, Owner: {eOwner})");
                                    li++;
                                }
                            }
                        }
                        catch (Exception ex) { Log($"    {t}: error={ex.Message}"); }
                    }

                    Log("    --- Root Properties (key ones) ---");
                    string[] rootProps = {
                        "Target_Server", "Target_Server_Version", "Target_Server_Type",
                        "Database_Name", "Schema_Name", "DBMS",
                        "Author", "Default_Owner", "Connect_String"
                    };
                    foreach (var rp in rootProps)
                    {
                        try
                        {
                            dynamic prop = root.Properties(rp);
                            if (prop == null) continue;
                            string v = ""; try { v = prop?.Value?.ToString() ?? ""; } catch { }
                            Log($"      {rp} = {v}");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log($"    Inspection error: {ex.Message}"); }
            finally
            {
                if (ownSession && sess != null)
                {
                    try { sess.Close(); } catch { }
                }
            }

            // FEModel_DDL: OPT-IN only (caused GDM-1001 crash on Mart PU)
            if (includeDdl)
            {
                Log("  --- FEModel_DDL (opt-in) ---");
                string ddlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"capture_now_{index}_{Guid.NewGuid():N}.sql");
                try
                {
                    pu.FEModel_DDL(ddlPath, "");
                    if (System.IO.File.Exists(ddlPath))
                    {
                        long sz = new System.IO.FileInfo(ddlPath).Length;
                        Log($"    File: {ddlPath} ({sz} bytes)");
                        if (sz > 0)
                        {
                            string content = System.IO.File.ReadAllText(ddlPath);
                            var lines = content.Split('\n');
                            int max = Math.Min(120, lines.Length);
                            Log($"    Showing first {max} of {lines.Length} lines:");
                            for (int li = 0; li < max; li++)
                            {
                                if (!string.IsNullOrWhiteSpace(lines[li]))
                                    Log($"    | {lines[li].TrimEnd()}");
                            }
                            if (lines.Length > max) Log($"    | ... (+" + (lines.Length - max) + " more lines)");
                        }
                    }
                    else
                    {
                        Log("    No DDL file produced");
                    }
                }
                catch (Exception ex) { Log($"    DDL error: {ex.Message}"); }
                finally
                {
                    try { System.IO.File.Delete(ddlPath); } catch { }
                }
            }
            else
            {
                Log("  --- FEModel_DDL: SKIPPED (hold Ctrl while clicking Capture Now to enable) ---");
            }
        }


        #endregion

        #region UI Helpers

        private void BtnClose_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Only block user-initiated close (X button, Alt+F4).
            // Allow: erwin shutdown, Windows shutdown, TaskKill, internal ForceClose.
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.WindowState = FormWindowState.Minimized;
                return;
            }
            base.OnFormClosing(e);
        }


        /// <summary>
        /// Actually closes the form (called when erwin shuts down or corporate check fails).
        /// </summary>
        internal void ForceClose()
        {
            _allowClose = true;
            Close();
        }

        private Form ShowLoadingDialog(string message)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            System.IO.Stream imgStream = null;
            Image splashImage = null;

            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith("erwin-addi-in-splash.png", StringComparison.OrdinalIgnoreCase))
                {
                    imgStream = assembly.GetManifestResourceStream(name);
                    break;
                }
            }

            if (imgStream != null)
                splashImage = Image.FromStream(imgStream);

            int formWidth = 420;
            int formHeight = 220;

            var loadingForm = new Form
            {
                Text = "Elite Soft Erwin Add-In",
                ClientSize = new Size(formWidth, formHeight),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.None,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.FromArgb(208, 208, 208),
                Padding = new Padding(1)
            };

            var innerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };
            loadingForm.Controls.Add(innerPanel);

            if (splashImage != null)
            {
                var pictureBox = new PictureBox
                {
                    Image = splashImage,
                    SizeMode = PictureBoxSizeMode.StretchImage,
                    Dock = DockStyle.Fill
                };
                innerPanel.Controls.Add(pictureBox);

                var label = new Label
                {
                    Text = message,
                    Font = new Font("Segoe UI", 12, FontStyle.Bold),
                    ForeColor = Color.Black,
                    BackColor = Color.Transparent,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Size = new Size(formWidth, 40),
                    Location = new Point(0, formHeight - 45)
                };
                pictureBox.Controls.Add(label);
            }
            else
            {
                var label = new Label
                {
                    Text = message,
                    Font = new Font("Segoe UI", 14, FontStyle.Bold),
                    ForeColor = Color.FromArgb(60, 60, 60),
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill
                };
                innerPanel.Controls.Add(label);
            }

            loadingForm.Show();
            Application.DoEvents();
            return loadingForm;
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
            MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        #endregion

        #region Resource Cleanup

        private void CloseCurrentSession()
        {
            if (_session != null && _isConnected)
            {
                // Only close if we believe the session is still alive.
                // Calling Close() on a dead COM object causes a native crash in erwin.
                try { _session.Close(); } catch { }
            }
            _session = null;
        }

        private void DisposeServices()
        {
            _validationService?.Dispose();
            _tableTypeMonitorService?.Dispose();
            _validationCoordinatorService?.Dispose();
            _propertyApplicatorService?.Dispose();
            _propertyApplicatorService = null;
            _udpRuntimeService?.Dispose();
            _udpRuntimeService = null;
        }

        /// <summary>
        /// Called when the SCAPI session becomes invalid (model closed from erwin).
        /// Safely stops all services without trying to access the dead session.
        /// </summary>
        private void HandleModelUdpChanged(string udpName, string newValue)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, string>(HandleModelUdpChanged), udpName, newValue); } catch { }
                return;
            }

            if (_dependencySetService == null || !_dependencySetService.IsLoaded) return;
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;

            var targets = _dependencySetService.GetAffectedUdps(udpName);
            if (targets.Count == 0) return;

            Log($"Model UDP '{udpName}' = '{newValue}' -> cascade: [{string.Join(", ", targets.Select(t => t.UdpName))}]");

            try
            {
                var modelUdpValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [udpName] = newValue
                };

                // Pre-compute new options for each target
                var updates = new List<(CascadeTarget target, string validValues, int count)>();
                foreach (var target in targets)
                {
                    var opts = _dependencySetService.GetListUdpOptions(target.UdpName, modelUdpValues);
                    if (opts != null && opts.Count > 0)
                        updates.Add((target, string.Join(",", opts), opts.Count));
                }

                if (updates.Count == 0) return;

                // Single metamodel session, single PT scan, single transaction
                dynamic mmSession = null;
                try
                {
                    mmSession = _scapi.Sessions.Add();
                    mmSession.Open(_currentModel, 1);
                    dynamic mmObjects = mmSession.ModelObjects;
                    dynamic mmRoot = mmObjects.Root;

                    // Build UDP name -> (target, validValues, count) lookup
                    var udpUpdateMap = new Dictionary<string, (CascadeTarget target, string validValues, int count)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var u in updates)
                        udpUpdateMap[u.target.UdpName] = u;

                    int remaining = udpUpdateMap.Count;
                    var ptMatches = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);

                    // Single PT scan - match by UDP name suffix (after last dot)
                    dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (remaining == 0) break;
                        if (pt == null) continue;
                        try
                        {
                            string ptName = pt.Name ?? "";
                            int lastDot = ptName.LastIndexOf('.');
                            if (lastDot < 0) continue;
                            string udpSuffix = ptName.Substring(lastDot + 1);

                            if (udpUpdateMap.ContainsKey(udpSuffix) && !ptMatches.ContainsKey(udpSuffix))
                            {
                                ptMatches[udpSuffix] = pt;
                                remaining--;
                            }
                        }
                        catch { }
                    }

                    // Single transaction for all updates
                    int transId = mmSession.BeginNamedTransaction("CascadeUpdate");
                    var updatedNames = new List<string>();
                    try
                    {
                        foreach (var kvp in udpUpdateMap)
                        {
                            if (!ptMatches.TryGetValue(kvp.Key, out var targetPt))
                            {
                                Log($"Cascade: PT not found for '{kvp.Key}'");
                                continue;
                            }

                            targetPt.Properties("tag_Udp_Data_Type").Value = 6;
                            targetPt.Properties("tag_Udp_Values_List").Value = kvp.Value.validValues;
                            updatedNames.Add($"{kvp.Key}({kvp.Value.count})");
                        }
                        mmSession.CommitTransaction(transId);
                    }
                    catch (Exception ex)
                    {
                        try { mmSession.RollbackTransaction(transId); } catch { }
                        Log($"Cascade transaction failed: {ex.Message}");
                    }

                    if (updatedNames.Count > 0)
                        Log($"Cascade updated: {string.Join(", ", updatedNames)}");
                }
                finally
                {
                    try { mmSession?.Close(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"HandleModelUdpChanged error: {ex.Message}");
            }
        }

        private void HandleModelChanged(string newModelName)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(HandleModelChanged), newModelName); } catch { }
                return;
            }

            SwitchToModel(newModelName);
        }

        private void HandleSessionLost()
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(HandleSessionLost)); } catch { }
                return;
            }

            Log("Model closed - session lost. Cleaning up services.");
            DdlGenerationService.ClearBaseline();

            try
            {
                _glossaryRefreshTimer?.Stop();
                _glossaryRefreshTimer?.Dispose();
                _glossaryRefreshTimer = null;

                DisposeServices();
                _validationService = null;
                _tableTypeMonitorService = null;
                _validationCoordinatorService = null;

                // Don't try _session.Close() — session is already dead (native crash risk)
                _session = null;
                _currentModel = null;
                _isConnected = false;

                // Clear stale model references to prevent user from selecting dead COM objects
                _openModels.Clear();
                _connectedModelName = null;
                _globalDataLoaded = false;
                lblActiveModel.Text = "(Waiting for model...)";

                // Reset General tab info
                _lblCorporateValue.Text = "-";
                _lblDbValue.Text = "-";
                _lblRegistryValue.Text = "-";

                // Reset UI to disconnected state
                UpdateConnectionStatus(StatusDisconnected, Color.Red);
                EnableControls(false);
                btnValidateAll.Enabled = false;
                lblPlatformStatus.Text = "";
                UpdateStatus("Model closed. Waiting for a model to open...", Color.Gray);

                // Start reconnect timer to poll for new models
                InitializeGlossaryRefreshTimer();
                StartReconnectTimer();
            }
            catch { }
        }

        private void CleanupResources()
        {
            try
            {
                StopReconnectTimer();

                _glossaryRefreshTimer?.Stop();
                _glossaryRefreshTimer?.Dispose();
                _glossaryRefreshTimer = null;

                DisposeServices();
                _validationService = null;
                _tableTypeMonitorService = null;
                _validationCoordinatorService = null;

                try { _session?.Close(); } catch { }
                try { _scapi?.Sessions?.Clear(); } catch { }
            }
            catch { }
        }

        #endregion
    }
}
