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

        // TABLE_TYPE monitor service
        private TableTypeMonitorService _tableTypeMonitorService;

        // Flag to track if OWNER UDP exists in model
        private bool _ownerUdpExists = false;

        // Suppress validation popups during startup (first 5 seconds)
        private bool _suppressValidationPopups = true;
        private Timer _suppressionTimer;

        public ModelConfigForm(dynamic scapi)
        {
            _scapi = scapi;
            InitializeComponent();
            InitializeValidationUI();
        }

        /// <summary>
        /// Logs a message to the debug log panel
        /// </summary>
        private void Log(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Log(message)));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logLine = $"[{timestamp}] {message}\r\n";
            txtDebugLog.AppendText(logLine);
            txtDebugLog.SelectionStart = txtDebugLog.Text.Length;
            txtDebugLog.ScrollToCaret();
            Application.DoEvents();
        }

        /// <summary>
        /// Clear debug log button click
        /// </summary>
        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            txtDebugLog.Clear();
        }

        /// <summary>
        /// Copy debug log button click - copies all log text to clipboard
        /// </summary>
        private void BtnCopyLog_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(txtDebugLog.Text))
            {
                Clipboard.SetText(txtDebugLog.Text);
                lblStatus.Text = "Log copied to clipboard!";
                lblStatus.ForeColor = Color.DarkGreen;
            }
        }

        /// <summary>
        /// Initialize validation ListView columns
        /// </summary>
        private void InitializeValidationUI()
        {
            // Column validation ListView columns
            listColumnValidation.Columns.Add("Table", 120);
            listColumnValidation.Columns.Add("Column", 120);
            listColumnValidation.Columns.Add("Physical Name", 140);
            listColumnValidation.Columns.Add("Status", 140);

            // Table validation ListView columns
            listTableValidation.Columns.Add("Table", 180);
            listTableValidation.Columns.Add("Issue", 280);

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
            Log("InitializeValidationService - Step 1: Starting");

            // Dispose previous services if exist
            _validationService?.Dispose();
            _tableTypeMonitorService?.Dispose();

            Log("InitializeValidationService - Step 2: Before LoadGlossary");

            // Load glossary from database
            LoadGlossary();

            Log("InitializeValidationService - Step 3: Glossary loaded");

            // Load TABLE_TYPE from database and ensure UDP exists
            LoadTableTypes();
            EnsureTableTypeUdpExists();

            Log("InitializeValidationService - Step 4: TABLE_TYPE loaded and UDP checked");

            _validationService = new ColumnValidationService(_session);

            // Subscribe to validation events
            _validationService.OnValidationFailed += OnValidationFailed;
            _validationService.OnValidationPassed += OnValidationPassed;
            _validationService.OnColumnChanged += OnColumnChanged;

            // Enable validation controls
            btnValidateAll.Enabled = true;

            // Take initial snapshot and auto-start monitoring
            _validationService.TakeSnapshot();
            _validationService.StartMonitoring();

            // Initialize TABLE_TYPE monitor service
            _tableTypeMonitorService = new TableTypeMonitorService(_session);
            _tableTypeMonitorService.OnLog += (msg) => Log(msg);
            _tableTypeMonitorService.TakeSnapshot();
            _tableTypeMonitorService.StartMonitoring();
            Log("TableTypeMonitorService initialized and monitoring");

            // Suppress popups for first 5 seconds to avoid flood on startup
            _suppressValidationPopups = true;
            _suppressionTimer = new Timer();
            _suppressionTimer.Interval = 5000;  // 5 seconds
            _suppressionTimer.Tick += (s, ev) =>
            {
                _suppressValidationPopups = false;
                _suppressionTimer.Stop();
                _suppressionTimer.Dispose();
                _suppressionTimer = null;
                Log("Validation popup suppression ended - popups now active");
            };
            _suppressionTimer.Start();
            Log("Validation popup suppression started (5 seconds)");

            var glossary = GlossaryService.Instance;
            if (glossary.IsLoaded)
            {
                lblValidationStatus.Text = $"Monitoring active - Glossary: {glossary.Count} entries";
                lblValidationStatus.ForeColor = Color.DarkGreen;
            }
            else
            {
                lblValidationStatus.Text = $"Monitoring active - Warning: Glossary not loaded";
                lblValidationStatus.ForeColor = Color.Orange;
            }
        }

        /// <summary>
        /// Check and create OWNER UDP at startup if needed
        /// </summary>
        private void EnsureOwnerUdpExistsOnStartup()
        {
            Log("EnsureOwnerUdpExistsOnStartup called");

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                Log("Got ModelObjects");

                dynamic root = modelObjects.Root;
                Log($"Got Root: {root?.Name ?? "(null)"}");

                // Check if OWNER UDP definition exists by looking at Property_Type objects
                Log("Checking if OWNER UDP definition exists via Property_Type...");

                bool udpExists = false;

                try
                {
                    // Try to collect Property_Type objects from root
                    Log("Trying modelObjects.Collect(root, \"Property_Type\")...");
                    dynamic propertyTypes = modelObjects.Collect(root, "Property_Type");

                    int ptCount = 0;
                    try { ptCount = propertyTypes?.Count ?? 0; } catch { }
                    Log($"Property_Type count: {ptCount}");

                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;

                        string ptName = "";
                        try { ptName = pt.Name ?? ""; } catch { continue; }

                        Log($"Found Property_Type: {ptName}");

                        // Check for OWNER UDP - erwin stores it as "Attribute.Physical.OWNER"
                        if (ptName.Equals("Attribute.Physical.OWNER", StringComparison.OrdinalIgnoreCase))
                        {
                            udpExists = true;
                            Log("OWNER UDP DEFINITION EXISTS!");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error checking Property_Type: {ex.Message}");
                }

                // Alternative: Scan all attributes for UDP value
                Log("Scanning all attributes for OWNER UDP value...");
                try
                {
                    dynamic allEntities = modelObjects.Collect(root, "Entity");
                    int checkedCount = 0;

                    foreach (dynamic entity in allEntities)
                    {
                        if (entity == null) continue;
                        if (udpExists) break;

                        dynamic entityAttrs = null;
                        try { entityAttrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                        if (entityAttrs == null) continue;

                        foreach (dynamic attr in entityAttrs)
                        {
                            if (attr == null) continue;
                            checkedCount++;

                            try
                            {
                                // Try to READ the UDP value
                                var ownerValue = attr.Properties("Attribute.Physical.OWNER").Value;
                                string ownerStr = ownerValue?.ToString() ?? "";

                                if (!string.IsNullOrEmpty(ownerStr))
                                {
                                    udpExists = true;
                                    string attrName = "";
                                    try { attrName = attr.Name ?? ""; } catch { }
                                    Log($"OWNER UDP FOUND! Attribute '{attrName}' has OWNER='{ownerStr}'");
                                    break;
                                }
                            }
                            catch
                            {
                                // Property not accessible on this attribute - continue checking
                            }
                        }
                    }

                    Log($"Checked {checkedCount} attributes");
                }
                catch (Exception ex2)
                {
                    Log($"Error scanning attributes: {ex2.Message}");
                }

                Log($"UDP exists: {udpExists}");
                _ownerUdpExists = udpExists;

                if (udpExists)
                {
                    Log("OWNER UDP already exists - ready to use");
                }
                else
                {
                    // UDP not found - try to create it via metamodel session
                    Log("OWNER UDP not found - attempting to create...");
                    bool created = CreateOwnerUdpViaMetamodel(modelObjects);
                    if (created)
                    {
                        _ownerUdpExists = true;
                        Log("OWNER UDP created successfully!");
                    }
                    else
                    {
                        Log("Could not create UDP automatically.");
                        Log("Please create manually: Model > UDPs > Add > Class: Column, Name: OWNER, Type: Text");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"OUTER ERROR: {ex.Message}");
                Log($"Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Tries to set OWNER UDP value on an attribute
        /// </summary>
        private bool TrySetOwnerUdp(dynamic attr, string ownerValue, string attributeName)
        {
            // Try to set OWNER UDP value directly
            try
            {
                attr.Properties("Attribute.Physical.OWNER").Value = ownerValue;
                _ownerUdpExists = true; // Mark as exists since it worked
                Log($"Set OWNER UDP = '{ownerValue}' for {attributeName}");
                return true;
            }
            catch
            {
                // Direct property access failed
            }

            // Try CollectProperties approach
            try
            {
                var props = attr.CollectProperties("Attribute.Physical.OWNER");
                if (props != null && props.Count > 0)
                {
                    props.Item(0).Value = ownerValue;
                    _ownerUdpExists = true;
                    Log($"Set OWNER via CollectProperties = '{ownerValue}' for {attributeName}");
                    return true;
                }
            }
            catch
            {
                // CollectProperties also failed
            }

            return false;
        }

        /// <summary>
        /// Creates the OWNER UDP via metamodel-level session
        /// </summary>
        private bool CreateOwnerUdpViaMetamodel(dynamic modelObjects)
        {
            dynamic metamodelSession = null;
            try
            {
                Log("Opening metamodel session (SCD_SL_M1)...");

                // Create a new session with metamodel access level (SCD_SL_M1 = 1)
                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // 1 = SCD_SL_M1 (Metamodel level)

                Log("Metamodel session opened");

                // Begin transaction
                int transId = metamodelSession.BeginNamedTransaction("CreateOwnerUDP");
                Log("Transaction started");

                try
                {
                    // Create Property_Type for the UDP
                    dynamic mmObjects = metamodelSession.ModelObjects;
                    dynamic udpType = mmObjects.Add("Property_Type");

                    Log("Property_Type object created");

                    // Set UDP properties
                    // Name format: <ObjectClassName>.<Logical/Physical>.<Name>
                    udpType.Properties("Name").Value = "Attribute.Physical.OWNER";
                    Log("Set Name = Attribute.Physical.OWNER");

                    // tag_Udp_Owner_Type - the class this UDP applies to
                    // Attribute class ID in erwin
                    try { udpType.Properties("tag_Udp_Owner_Type").Value = "Attribute"; }
                    catch { Log("Could not set tag_Udp_Owner_Type"); }

                    // Physical only
                    try { udpType.Properties("tag_Is_Physical").Value = true; }
                    catch { Log("Could not set tag_Is_Physical"); }

                    try { udpType.Properties("tag_Is_Logical").Value = false; }
                    catch { Log("Could not set tag_Is_Logical"); }

                    // Data type: 2 = Text
                    try { udpType.Properties("tag_Udp_Data_Type").Value = 2; }
                    catch { Log("Could not set tag_Udp_Data_Type"); }

                    try { udpType.Properties("Data_Type").Value = 2; }
                    catch { Log("Could not set Data_Type"); }

                    // Order
                    try { udpType.Properties("tag_Order").Value = "1"; }
                    catch { Log("Could not set tag_Order"); }

                    // Locally defined
                    try { udpType.Properties("tag_Is_Locally_Defined").Value = true; }
                    catch { Log("Could not set tag_Is_Locally_Defined"); }

                    // Commit transaction
                    metamodelSession.CommitTransaction(transId);
                    Log("Transaction committed");

                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Error creating UDP: {ex.Message}");
                    try { metamodelSession.RollbackTransaction(transId); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"Metamodel session error: {ex.Message}");
                return false;
            }
            finally
            {
                // Close metamodel session
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); } catch { }
                }
            }
        }

        /// <summary>
        /// Test database connection button click
        /// </summary>
        private void BtnTestConnection_Click(object sender, EventArgs e)
        {
            try
            {
                lblGlossaryStatus.Text = "Testing connection...";
                lblGlossaryStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                string connStr = BuildConnectionString();
                using (var connection = new System.Data.SqlClient.SqlConnection(connStr))
                {
                    connection.Open();
                    lblGlossaryStatus.Text = "Connection successful!";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;
                }
            }
            catch (Exception ex)
            {
                lblGlossaryStatus.Text = $"Connection failed: {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.Red;
            }
        }

        /// <summary>
        /// Reload glossary button click
        /// </summary>
        private void BtnReloadGlossary_Click(object sender, EventArgs e)
        {
            try
            {
                lblGlossaryStatus.Text = "Reloading glossary...";
                lblGlossaryStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                // Update GlossaryService connection string
                string connStr = BuildConnectionString();
                GlossaryService.Instance.SetConnectionString(connStr);
                GlossaryService.Instance.Reload();

                if (GlossaryService.Instance.IsLoaded)
                {
                    lblGlossaryStatus.Text = $"Glossary loaded: {GlossaryService.Instance.Count} entries";
                    lblGlossaryStatus.ForeColor = Color.DarkGreen;

                    // Update validation status if available
                    if (lblValidationStatus != null)
                    {
                        lblValidationStatus.Text = $"Monitoring active - Glossary: {GlossaryService.Instance.Count} entries";
                        lblValidationStatus.ForeColor = Color.DarkGreen;
                    }
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

        /// <summary>
        /// Build connection string from form fields
        /// </summary>
        private string BuildConnectionString()
        {
            string server = txtHost.Text.Trim();
            string port = txtPort.Text.Trim();

            // Combine host and port
            if (!string.IsNullOrEmpty(port) && port != "1433")
            {
                server = $"{server},{port}";
            }
            else if (!string.IsNullOrEmpty(port))
            {
                server = $"{server},{port}";
            }

            return $"Server={server};Database={txtGlossaryDatabase.Text};User Id={txtUserId.Text};Password={txtPassword.Text};Connection Timeout=5;";
        }

        /// <summary>
        /// Load glossary from database
        /// </summary>
        private void LoadGlossary()
        {
            try
            {
                // Update connection string from form fields
                string connStr = BuildConnectionString();
                GlossaryService.Instance.SetConnectionString(connStr);

                var glossary = GlossaryService.Instance;
                if (!glossary.IsLoaded)
                {
                    glossary.LoadGlossary();
                }

                // Update glossary status label
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
                System.Diagnostics.Debug.WriteLine($"LoadGlossary error: {ex.Message}");
                lblGlossaryStatus.Text = $"Error: {ex.Message}";
                lblGlossaryStatus.ForeColor = Color.Red;
            }
        }

        /// <summary>
        /// Load TABLE_TYPE entries from database
        /// </summary>
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

        /// <summary>
        /// Ensures the TABLE_TYPE UDP exists in the model as a List type
        /// Creates it with values from TABLE_TYPE table if it doesn't exist
        /// </summary>
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

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                // Check if TABLE_TYPE UDP already exists
                bool udpExists = false;
                try
                {
                    dynamic propertyTypes = modelObjects.Collect(root, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        string ptName = "";
                        try { ptName = pt.Name ?? ""; } catch { continue; }

                        // Check for Entity.Physical.TABLE_TYPE (Table-level UDP)
                        if (ptName.Equals("Entity.Physical.TABLE_TYPE", StringComparison.OrdinalIgnoreCase))
                        {
                            udpExists = true;
                            Log("TABLE_TYPE UDP already exists");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error checking for TABLE_TYPE UDP: {ex.Message}");
                }

                if (!udpExists)
                {
                    // Create TABLE_TYPE UDP via metamodel session
                    Log("TABLE_TYPE UDP not found - creating...");
                    bool created = CreateTableTypeUdp(tableTypeService.GetNamesAsCommaSeparated());
                    if (created)
                    {
                        Log("TABLE_TYPE UDP created successfully!");
                    }
                    else
                    {
                        Log("Failed to create TABLE_TYPE UDP");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"EnsureTableTypeUdpExists error: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates the TABLE_TYPE UDP as a List type with values from database
        /// </summary>
        private bool CreateTableTypeUdp(string listValues)
        {
            dynamic metamodelSession = null;
            try
            {
                Log($"Creating TABLE_TYPE UDP with values: {listValues}");

                // Create a new session with metamodel access level (SCD_SL_M1 = 1)
                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // 1 = SCD_SL_M1 (Metamodel level)

                Log("Metamodel session opened for TABLE_TYPE UDP");

                // Begin transaction
                int transId = metamodelSession.BeginNamedTransaction("CreateTableTypeUDP");
                Log("Transaction started");

                try
                {
                    // Create Property_Type for the UDP
                    dynamic mmObjects = metamodelSession.ModelObjects;
                    dynamic udpType = mmObjects.Add("Property_Type");

                    Log("Property_Type object created for TABLE_TYPE");

                    // Set UDP properties
                    // Name format: <ObjectClassName>.<Logical/Physical>.<Name>
                    // Entity = Table in erwin terminology
                    udpType.Properties("Name").Value = "Entity.Physical.TABLE_TYPE";
                    Log("Set Name = Entity.Physical.TABLE_TYPE");

                    // tag_Udp_Owner_Type - the class this UDP applies to (Entity for tables)
                    try { udpType.Properties("tag_Udp_Owner_Type").Value = "Entity"; }
                    catch { Log("Could not set tag_Udp_Owner_Type"); }

                    // Physical only
                    try { udpType.Properties("tag_Is_Physical").Value = true; }
                    catch { Log("Could not set tag_Is_Physical"); }

                    try { udpType.Properties("tag_Is_Logical").Value = false; }
                    catch { Log("Could not set tag_Is_Logical"); }

                    // Data type: 6 = List (enumeration type)
                    // erwin UDP data types: 1=Integer, 2=Text, 3=Date, 4=Command, 5=Real, 6=List
                    try { udpType.Properties("tag_Udp_Data_Type").Value = 6; }
                    catch { Log("Could not set tag_Udp_Data_Type"); }

                    // Set the list values using tag_Udp_Values_List (comma-separated)
                    // This is the correct property name for List type UDP values
                    try
                    {
                        udpType.Properties("tag_Udp_Values_List").Value = listValues;
                        Log($"Set tag_Udp_Values_List = {listValues}");
                    }
                    catch (Exception ex) { Log($"Could not set tag_Udp_Values_List: {ex.Message}"); }

                    // Set default value (first item in list)
                    string defaultValue = listValues.Split(',').FirstOrDefault()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(defaultValue))
                    {
                        try { udpType.Properties("tag_Udp_Default_Value").Value = defaultValue; }
                        catch { Log("Could not set tag_Udp_Default_Value"); }
                    }

                    // Order
                    try { udpType.Properties("tag_Order").Value = "1"; }
                    catch { Log("Could not set tag_Order"); }

                    // Locally defined
                    try { udpType.Properties("tag_Is_Locally_Defined").Value = true; }
                    catch { Log("Could not set tag_Is_Locally_Defined"); }

                    // Commit transaction
                    metamodelSession.CommitTransaction(transId);
                    Log("Transaction committed for TABLE_TYPE UDP");

                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Error creating TABLE_TYPE UDP: {ex.Message}");
                    try { metamodelSession.RollbackTransaction(transId); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"Metamodel session error for TABLE_TYPE: {ex.Message}");
                return false;
            }
            finally
            {
                // Close metamodel session
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); } catch { }
                }
            }
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

            // Add to column validation list (always, even during suppression)
            var item = new ListViewItem(e.TableName);
            item.SubItems.Add(e.AttributeName);
            item.SubItems.Add(e.PhysicalName);
            item.SubItems.Add(e.ValidationMessage);
            item.ForeColor = Color.Red;
            listColumnValidation.Items.Insert(0, item);

            // During startup suppression period, don't show popup
            if (_suppressValidationPopups)
            {
                Log($"Popup suppressed for: {e.TableName}.{e.AttributeName}");
                return;
            }

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
            _validationService?.StartMonitoring();

            // Update status
            lblValidationStatus.Text = $"Last issue: {e.PhysicalName} - {e.ValidationMessage}";
            lblValidationStatus.ForeColor = Color.Red;
        }

        /// <summary>
        /// Called when a column name is valid and found in glossary
        /// Sets the Data Type and OWNER UDP from glossary
        /// </summary>
        private void OnValidationPassed(object sender, ColumnValidationEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnValidationPassed(sender, e)));
                return;
            }

            if (e.GlossaryEntry == null) return;

            System.Diagnostics.Debug.WriteLine($"OnValidationPassed: Table={e.TableName}, Attr={e.AttributeName}, DataType={e.GlossaryEntry.DataType}, Owner={e.GlossaryEntry.Owner}");

            // Apply glossary entry - set Data Type and OWNER UDP
            ApplyGlossaryEntry(e.TableName, e.AttributeName, e.GlossaryEntry);

            // Update status
            lblValidationStatus.Text = $"[{DateTime.Now:HH:mm:ss}] {e.PhysicalName} - DataType: {e.GlossaryEntry.DataType}, Owner: {e.GlossaryEntry.Owner}";
            lblValidationStatus.ForeColor = Color.DarkGreen;
        }

        /// <summary>
        /// Applies glossary entry to a column - sets Data Type and OWNER UDP
        /// </summary>
        private void ApplyGlossaryEntry(string tableName, string attributeName, GlossaryEntry entry)
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string entityName = "";
                    string entityPhysName = "";
                    try { entityName = entity.Name ?? ""; } catch { }
                    try { entityPhysName = entity.Properties("Physical_Name").Value?.ToString() ?? ""; } catch { }

                    bool entityMatch = entityPhysName.Equals(tableName, StringComparison.OrdinalIgnoreCase) ||
                                      entityName.Equals(tableName, StringComparison.OrdinalIgnoreCase);
                    if (!entityMatch) continue;

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
                            int transId = _session.BeginNamedTransaction("ApplyGlossary");

                            try
                            {
                                // Set Physical Data Type from glossary
                                if (!string.IsNullOrEmpty(entry.DataType))
                                {
                                    try
                                    {
                                        attr.Properties("Physical_Data_Type").Value = entry.DataType;
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"Set Physical_Data_Type error: {ex.Message}");
                                    }
                                }

                                // Set OWNER UDP from glossary
                                if (!string.IsNullOrEmpty(entry.Owner))
                                {
                                    bool ownerSet = TrySetOwnerUdp(attr, entry.Owner, attributeName);

                                    // If setting failed and UDP doesn't exist, create it and retry
                                    if (!ownerSet && !_ownerUdpExists)
                                    {
                                        Log($"OWNER UDP not set - attempting to create UDP for {attributeName}...");

                                        // Commit current transaction first
                                        _session.CommitTransaction(transId);

                                        // Create UDP via metamodel
                                        bool created = CreateOwnerUdpViaMetamodel(modelObjects);
                                        if (created)
                                        {
                                            _ownerUdpExists = true;
                                            Log("OWNER UDP created successfully! Retrying set...");

                                            // Start a new transaction to set the value
                                            transId = _session.BeginNamedTransaction("ApplyGlossaryRetry");
                                            ownerSet = TrySetOwnerUdp(attr, entry.Owner, attributeName);
                                        }
                                        else
                                        {
                                            Log("Could not create OWNER UDP automatically.");
                                            // Start a new transaction to continue
                                            transId = _session.BeginNamedTransaction("ApplyGlossaryContinue");
                                        }
                                    }

                                    if (!ownerSet)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"OWNER UDP not set for {attributeName}");
                                    }
                                }

                                _session.CommitTransaction(transId);
                                return;
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
                System.Diagnostics.Debug.WriteLine($"ApplyGlossaryEntry error: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures the OWNER UDP exists in the model, creates it if not
        /// Creates Property_Type and required Association_Type objects
        /// </summary>
        private void EnsureOwnerUdpExists(dynamic modelObjects)
        {
            try
            {
                // Check if UDP already exists by looking for Property_Type with name "Column.Physical.OWNER"
                dynamic root = modelObjects.Root;
                dynamic propertyTypes = modelObjects.Collect(root, "Property_Type");

                if (propertyTypes != null)
                {
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        try
                        {
                            string name = pt.Name ?? "";
                            if (name.Equals("Column.Physical.OWNER", StringComparison.OrdinalIgnoreCase))
                            {
                                // UDP already exists
                                return;
                            }
                        }
                        catch { }
                    }
                }

                // Step 1: Create the Property_Type (UDP definition)
                dynamic newUdp = modelObjects.Add("Property_Type");

                // Get the numeric ID (not GUID ObjectId)
                string udpId = "";
                try { udpId = newUdp.Properties("Id").Value?.ToString() ?? ""; } catch { }
                if (string.IsNullOrEmpty(udpId))
                {
                    try { udpId = newUdp.Id?.ToString() ?? ""; } catch { }
                }
                if (string.IsNullOrEmpty(udpId))
                {
                    // Use ObjectId as fallback
                    udpId = newUdp.ObjectId?.ToString() ?? "";
                }

                newUdp.Properties("Name").Value = "Column.Physical.OWNER";
                newUdp.Properties("tag_Is_Physical").Value = true;
                newUdp.Properties("tag_Order").Value = "1";
                newUdp.Properties("tag_Udp_Owner_Type").Value = "1075838981";  // Attribute class ID
                newUdp.Properties("tag_Column_Width").Value = "-3";
                newUdp.Properties("tag_Udp_Data_Type").Value = "2";  // Text type
                newUdp.Properties("Data_Type").Value = "2";
                newUdp.Properties("tag_Is_Locally_Defined").Value = true;

                System.Diagnostics.Debug.WriteLine($"Created Property_Type: Column.Physical.OWNER (id={udpId})");

                // Step 2: Create Association_Type for Column_has_Physical_OWNER
                dynamic attrAssoc = modelObjects.Add("Association_Type");
                attrAssoc.Properties("Name").Value = "Column_has_Physical_OWNER";
                attrAssoc.Properties("tag_Is_Physical").Value = true;
                attrAssoc.Properties("tag_Is_Prefetch").Value = true;
                attrAssoc.Properties("Participating_Property_Ref").Value = udpId;
                attrAssoc.Properties("Participating_Object_Ref").Value = "1075838981";  // Attribute class ID
                attrAssoc.Properties("tag_Is_Locally_Defined").Value = true;

                System.Diagnostics.Debug.WriteLine("Created Association_Type: Column_has_Physical_OWNER");

                // Step 3: Create Association_Type for Domain_has_Physical_OWNER
                dynamic domainAssoc = modelObjects.Add("Association_Type");
                domainAssoc.Properties("Name").Value = "Domain_has_Physical_OWNER";
                domainAssoc.Properties("tag_Is_Physical").Value = true;
                domainAssoc.Properties("tag_Is_Prefetch").Value = true;
                domainAssoc.Properties("Participating_Property_Ref").Value = udpId;
                domainAssoc.Properties("Participating_Object_Ref").Value = "1075838983";  // Domain class ID
                domainAssoc.Properties("tag_Is_Locally_Defined").Value = true;

                System.Diagnostics.Debug.WriteLine("Created Association_Type: Domain_has_Physical_OWNER");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EnsureOwnerUdpExists error: {ex.Message}");
                throw;
            }
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
        /// Validate all columns and tables on-demand
        /// </summary>
        private void BtnValidateAll_Click(object sender, EventArgs e)
        {
            if (_validationService == null) return;

            // Clear both lists
            listColumnValidation.Items.Clear();
            listTableValidation.Items.Clear();

            var allResults = _validationService.ValidateAllColumns();

            // Separate column issues from table issues
            var columnResults = allResults.Where(r => r.RuleName != "TableTypeRule").ToList();
            var tableResults = allResults.Where(r => r.RuleName == "TableTypeRule").ToList();

            // Process column validations
            var validColumns = columnResults.Where(r => r.IsValid).ToList();
            var invalidColumns = columnResults.Where(r => !r.IsValid).ToList();

            // First add valid columns (green)
            foreach (var col in validColumns)
            {
                var item = new ListViewItem(col.TableName);
                item.SubItems.Add(col.AttributeName);
                item.SubItems.Add(col.PhysicalName);
                item.SubItems.Add("OK - Found in glossary");
                item.ForeColor = Color.DarkGreen;
                listColumnValidation.Items.Add(item);
            }

            // Then add invalid columns (red)
            foreach (var col in invalidColumns)
            {
                var item = new ListViewItem(col.TableName);
                item.SubItems.Add(col.AttributeName);
                item.SubItems.Add(col.PhysicalName);
                item.SubItems.Add(col.Issue);
                item.ForeColor = Color.Red;
                listColumnValidation.Items.Add(item);
            }

            // Process table validations
            var validTables = tableResults.Where(r => r.IsValid).ToList();
            var invalidTables = tableResults.Where(r => !r.IsValid).ToList();

            // Add valid tables (green) - TABLE_TYPE selected
            foreach (var tbl in validTables)
            {
                var item = new ListViewItem(tbl.TableName);
                item.SubItems.Add(tbl.Issue);
                item.ForeColor = Color.DarkGreen;
                listTableValidation.Items.Add(item);
            }

            // Add invalid tables (red) - TABLE_TYPE not selected
            foreach (var tbl in invalidTables)
            {
                var item = new ListViewItem(tbl.TableName);
                item.SubItems.Add(tbl.Issue);
                item.ForeColor = Color.Red;
                listTableValidation.Items.Add(item);
            }

            // Update status
            int totalIssues = invalidColumns.Count + invalidTables.Count;
            if (totalIssues == 0)
            {
                lblValidationStatus.Text = $"All validations passed - {validColumns.Count} columns, {validTables.Count} tables OK";
                lblValidationStatus.ForeColor = Color.DarkGreen;
                MessageBox.Show($"All validations passed!\n\nColumns: {validColumns.Count} found in glossary\nTables: All tables have type selected",
                    "Validation Passed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                lblValidationStatus.Text = $"Validation: {invalidColumns.Count} column errors, {invalidTables.Count} table errors";
                lblValidationStatus.ForeColor = Color.Red;

                string message = "";
                if (invalidColumns.Count > 0)
                    message += $"Column Errors: {invalidColumns.Count} not found in glossary\n";
                if (invalidTables.Count > 0)
                    message += $"Table Errors: {invalidTables.Count} tables without type selected\n";
                message += "\nSee the tabs for details.";

                MessageBox.Show(message, "Validation Result", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

                // Dispose TABLE_TYPE monitor service
                _tableTypeMonitorService?.Dispose();
                _tableTypeMonitorService = null;

                _session?.Close();
                _scapi?.Sessions?.Clear();
            }
            catch { }
        }
    }
}
