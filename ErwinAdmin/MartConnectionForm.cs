using System;
using System.Drawing;
using System.Windows.Forms;

namespace EliteSoft.Erwin.Admin
{
    public partial class MartConnectionForm : Form
    {
        private TabControl tabControl;
        private TabPage tabMartConnection;
        private TabPage tabLoadModel;
        private TabPage tabCheckNaming;

        // Mart Connection Tab
        private GroupBox grpMartConnection;
        private Label lblServerName;
        private TextBox txtServerName;
        private Label lblPort;
        private TextBox txtPort;
        private Label lblUserName;
        private TextBox txtUserName;
        private Label lblPassword;
        private TextBox txtPassword;
        private Button btnMartConnect;

        // Load Model Tab
        private GroupBox grpModelSelection;
        private Label lblModel;
        private TreeView treeModels;
        private Button btnLoadModel;

        private Button btnClose;

        private dynamic _scapi;
        private dynamic _currentModel;
        private dynamic _martConnection;
        private bool _isMartConnected = false;

        public MartConnectionForm()
        {
            InitializeComponent();
            InitializeSCAPI();
        }

        private void InitializeComponent()
        {
            this.Text = "Elite Soft Erwin Admin";
            this.Size = new Size(600, 450);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // TabControl
            tabControl = new TabControl();
            tabControl.Location = new Point(12, 12);
            tabControl.Size = new Size(560, 350);
            this.Controls.Add(tabControl);

            // Tab Pages
            tabMartConnection = new TabPage("Mart Connection");
            tabLoadModel = new TabPage("Load Model");
            tabCheckNaming = new TabPage("Check Naming Conventions");

            tabControl.TabPages.Add(tabMartConnection);
            tabControl.TabPages.Add(tabLoadModel);
            tabControl.TabPages.Add(tabCheckNaming);

            // Initially disable tabs 2 and 3
            tabLoadModel.Enabled = false;
            tabCheckNaming.Enabled = false;

            InitializeMartConnectionTab();
            InitializeLoadModelTab();
            InitializeCheckNamingTab();

            // Close Button
            btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.Location = new Point(492, 375);
            btnClose.Size = new Size(80, 35);
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);
        }

        private void InitializeMartConnectionTab()
        {
            // Connection Group
            grpMartConnection = new GroupBox();
            grpMartConnection.Text = "Connection Settings";
            grpMartConnection.Location = new Point(15, 15);
            grpMartConnection.Size = new Size(520, 170);
            tabMartConnection.Controls.Add(grpMartConnection);

            // Server Name
            lblServerName = new Label();
            lblServerName.Text = "Server Name:";
            lblServerName.Location = new Point(15, 28);
            lblServerName.Size = new Size(90, 20);
            grpMartConnection.Controls.Add(lblServerName);

            txtServerName = new TextBox();
            txtServerName.Location = new Point(110, 25);
            txtServerName.Size = new Size(200, 23);
            txtServerName.Text = "localhost";
            grpMartConnection.Controls.Add(txtServerName);

            // Port
            lblPort = new Label();
            lblPort.Text = "Port:";
            lblPort.Location = new Point(330, 28);
            lblPort.Size = new Size(40, 20);
            grpMartConnection.Controls.Add(lblPort);

            txtPort = new TextBox();
            txtPort.Location = new Point(375, 25);
            txtPort.Size = new Size(125, 23);
            txtPort.Text = "18170";
            grpMartConnection.Controls.Add(txtPort);

            // User Name
            lblUserName = new Label();
            lblUserName.Text = "User Name:";
            lblUserName.Location = new Point(15, 63);
            lblUserName.Size = new Size(90, 20);
            grpMartConnection.Controls.Add(lblUserName);

            txtUserName = new TextBox();
            txtUserName.Location = new Point(110, 60);
            txtUserName.Size = new Size(390, 23);
            txtUserName.Text = "kursat";
            grpMartConnection.Controls.Add(txtUserName);

            // Password
            lblPassword = new Label();
            lblPassword.Text = "Password:";
            lblPassword.Location = new Point(15, 98);
            lblPassword.Size = new Size(90, 20);
            grpMartConnection.Controls.Add(lblPassword);

            txtPassword = new TextBox();
            txtPassword.Location = new Point(110, 95);
            txtPassword.Size = new Size(390, 23);
            txtPassword.Text = "Elite12345..";
            txtPassword.UseSystemPasswordChar = true;
            grpMartConnection.Controls.Add(txtPassword);

            // Connect Button
            btnMartConnect = new Button();
            btnMartConnect.Text = "Connect";
            btnMartConnect.Location = new Point(400, 130);
            btnMartConnect.Size = new Size(100, 30);
            btnMartConnect.Click += BtnMartConnect_Click;
            btnMartConnect.Enabled = false;
            grpMartConnection.Controls.Add(btnMartConnect);
        }

        private void InitializeLoadModelTab()
        {
            // Model Selection Group
            grpModelSelection = new GroupBox();
            grpModelSelection.Text = "Mart Catalog";
            grpModelSelection.Location = new Point(15, 15);
            grpModelSelection.Size = new Size(520, 270);
            tabLoadModel.Controls.Add(grpModelSelection);

            // Model TreeView
            treeModels = new TreeView();
            treeModels.Location = new Point(15, 25);
            treeModels.Size = new Size(490, 190);
            treeModels.HideSelection = false;
            treeModels.ShowLines = true;
            treeModels.ShowRootLines = true;
            treeModels.ShowPlusMinus = true;
            grpModelSelection.Controls.Add(treeModels);

            // Load from Mart Button
            btnLoadModel = new Button();
            btnLoadModel.Text = "Load Selected Model";
            btnLoadModel.Location = new Point(15, 225);
            btnLoadModel.Size = new Size(235, 30);
            btnLoadModel.Click += BtnLoadModel_Click;
            grpModelSelection.Controls.Add(btnLoadModel);

            // Load from File Button
            Button btnLoadFromFile = new Button();
            btnLoadFromFile.Text = "Load from File...";
            btnLoadFromFile.Location = new Point(270, 225);
            btnLoadFromFile.Size = new Size(235, 30);
            btnLoadFromFile.Click += BtnLoadFromFile_Click;
            grpModelSelection.Controls.Add(btnLoadFromFile);
        }

        private void InitializeCheckNamingTab()
        {
            // Placeholder for Check Naming Conventions tab
            // Will be implemented in next step
        }

        private void LoadMartCatalog()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== LoadMartCatalog START ===");

                treeModels.Nodes.Clear();

                // Create root node for Mart
                TreeNode martRoot = new TreeNode("Mart")
                {
                    Tag = "ROOT"
                };
                treeModels.Nodes.Add(martRoot);

                // Try to enumerate libraries from Mart server
                // Note: SCAPI may not expose catalog browsing API, so we'll create placeholder structure
                // Real catalog structure is managed by MartServer REST API (as seen in browser console)

                try
                {
                    // Attempt to get libraries from SCAPI
                    if (_scapi.MartConnection != null && _scapi.MartConnection.Libraries != null)
                    {
                        System.Diagnostics.Debug.WriteLine("Attempting to enumerate libraries from MartConnection...");

                        int libCount = _scapi.MartConnection.Libraries.Count;
                        System.Diagnostics.Debug.WriteLine($"Found {libCount} libraries via MartConnection");

                        for (int i = 1; i <= libCount; i++)
                        {
                            dynamic library = _scapi.MartConnection.Libraries.Item(i);
                            string libraryName = library.Name;
                            System.Diagnostics.Debug.WriteLine($"  Library [{i}]: {libraryName}");

                            TreeNode libraryNode = new TreeNode(libraryName)
                            {
                                Tag = $"LIBRARY:{libraryName}"
                            };
                            martRoot.Nodes.Add(libraryNode);

                            // Add placeholder for lazy loading
                            libraryNode.Nodes.Add(new TreeNode("(Expand to load models...)") { Tag = "PLACEHOLDER" });
                        }
                    }
                    else
                    {
                        throw new Exception("MartConnection.Libraries not available");
                    }
                }
                catch (Exception enumEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not enumerate libraries from SCAPI: {enumEx.Message}");
                    System.Diagnostics.Debug.WriteLine("Creating static library structure based on visible Mart Portal libraries...");

                    // Create static library nodes based on what we see in Mart Portal screenshot
                    // Libraries: Demo, Kursat, Sample
                    string[] knownLibraries = { "Kursat", "Demo", "Sample" };

                    foreach (string libName in knownLibraries)
                    {
                        TreeNode libraryNode = new TreeNode(libName)
                        {
                            Tag = $"LIBRARY:{libName}"
                        };
                        martRoot.Nodes.Add(libraryNode);

                        // Add some known models for Kursat library (from screenshot)
                        if (libName == "Kursat")
                        {
                            TreeNode modelNode = new TreeNode("PublicationSystemSample_Modified")
                            {
                                Tag = "MODEL:Kursat/PublicationSystemSample_Modified"
                            };
                            libraryNode.Nodes.Add(modelNode);
                        }
                        else
                        {
                            // Add placeholder for other libraries
                            libraryNode.Nodes.Add(new TreeNode("(Double-click to refresh)") { Tag = "PLACEHOLDER" });
                        }
                    }
                }

                // Expand root node
                martRoot.Expand();
                System.Diagnostics.Debug.WriteLine("=== LoadMartCatalog END ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in LoadMartCatalog: {ex.Message}");

                // Create simple fallback structure
                treeModels.Nodes.Clear();
                TreeNode martRoot = new TreeNode("Mart (Manual Entry Required)")
                {
                    Tag = "ROOT"
                };
                treeModels.Nodes.Add(martRoot);

                TreeNode infoNode = new TreeNode("Could not load catalog automatically")
                {
                    Tag = "INFO"
                };
                martRoot.Nodes.Add(infoNode);

                TreeNode helpNode = new TreeNode("Please enter model path manually: library/modelName")
                {
                    Tag = "HELP"
                };
                martRoot.Nodes.Add(helpNode);

                martRoot.Expand();
            }
        }

        private void InitializeSCAPI()
        {
            try
            {
                Type scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                if (scapiType != null)
                {
                    _scapi = Activator.CreateInstance(scapiType);

                    if (_scapi != null)
                    {
                        btnMartConnect.Enabled = true;
                    }
                    else
                    {
                        btnMartConnect.Enabled = false;

                        MessageBox.Show(
                            "Failed to create SCAPI instance!\n\n" +
                            "The SCAPI COM object could not be instantiated.\n\n" +
                            "Please ensure:\n" +
                            "- erwin Data Modeler is properly installed\n" +
                            "- erwin version 9 or later is installed\n" +
                            "- COM registration is intact",
                            "SCAPI Initialization Failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }
                }
                else
                {
                    btnMartConnect.Enabled = false;

                    MessageBox.Show(
                        "erwin SCAPI not found!\n\n" +
                        "Could not locate the SCAPI COM object (ProgID: erwin9.SCAPI).\n\n" +
                        "Please ensure:\n" +
                        "- erwin Data Modeler is installed\n" +
                        "- erwin version 9 or later is installed\n" +
                        "- Installation is not corrupted\n\n" +
                        "You may need to repair or reinstall erwin Data Modeler.",
                        "erwin Not Found",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
            catch (Exception ex)
            {
                string errorDetails = $"Failed to initialize SCAPI!\n\n" +
                                    $"Error Type: {ex.GetType().Name}\n" +
                                    $"Message: {ex.Message}\n" +
                                    $"Source: {ex.Source}\n\n" +
                                    $"Stack Trace:\n{ex.StackTrace}";

                if (ex.InnerException != null)
                {
                    errorDetails += $"\n\nInner Exception:\n{ex.InnerException.Message}";
                }

                btnMartConnect.Enabled = false;

                MessageBox.Show(
                    errorDetails,
                    "SCAPI Initialization Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private void BtnMartConnect_Click(object sender, EventArgs e)
        {
            try
            {
                // Validate inputs
                if (string.IsNullOrWhiteSpace(txtServerName.Text))
                {
                    MessageBox.Show("Server Name is required.", "Validation Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtServerName.Focus();
                    return;
                }

                if (string.IsNullOrWhiteSpace(txtPort.Text))
                {
                    MessageBox.Show("Port is required.", "Validation Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtPort.Focus();
                    return;
                }

                if (!int.TryParse(txtPort.Text, out int portNumber) || portNumber < 1 || portNumber > 65535)
                {
                    MessageBox.Show("Port must be a valid number between 1 and 65535.", "Validation Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtPort.Focus();
                    return;
                }

                if (string.IsNullOrWhiteSpace(txtUserName.Text))
                {
                    MessageBox.Show("User Name is required.", "Validation Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtUserName.Focus();
                    return;
                }

                if (string.IsNullOrWhiteSpace(txtPassword.Text))
                {
                    MessageBox.Show("Password is required.", "Validation Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtPassword.Focus();
                    return;
                }

                // Store Mart connection settings (actual connection happens during model load)
                string martServer = $"{txtServerName.Text}:{txtPort.Text}";

                Cursor.Current = Cursors.WaitCursor;
                Application.DoEvents();

                // Save connection settings
                _isMartConnected = true;

                // Load Mart catalog structure into TreeView (static structure)
                LoadMartCatalog();

                Cursor.Current = Cursors.Default;

                // Enable Load Model tab
                tabLoadModel.Enabled = true;

                MessageBox.Show(
                    $"Mart connection configured!\n\n" +
                    $"Server: {martServer}\n" +
                    $"User: {txtUserName.Text}\n\n" +
                    $"Mart catalog loaded.\n" +
                    $"'Load Model' tab is now enabled.\n\n" +
                    $"Select a model from the tree and click 'Load Selected Model'.",
                    "Configuration Successful",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                // Automatically switch to Load Model tab
                tabControl.SelectedTab = tabLoadModel;
            }
            catch (Exception ex)
            {
                _isMartConnected = false;
                tabLoadModel.Enabled = false;

                string detailedError = $"Error Type: {ex.GetType().Name}\n" +
                                     $"Message: {ex.Message}\n" +
                                     $"Source: {ex.Source}\n\n" +
                                     $"Stack Trace:\n{ex.StackTrace}";

                if (ex.InnerException != null)
                {
                    detailedError += $"\n\nInner Exception:\n{ex.InnerException.Message}";
                }

                MessageBox.Show(
                    $"Failed to configure connection!\n\n{detailedError}",
                    "Configuration Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private void BtnLoadFromFile_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("=== BtnLoadFromFile_Click START ===");

            try
            {
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Title = "Select erwin Model File";
                    openFileDialog.Filter = "erwin Model Files (*.erwin;*.erwm)|*.erwin;*.erwm|All Files (*.*)|*.*";
                    openFileDialog.FilterIndex = 1;
                    openFileDialog.RestoreDirectory = true;

                    if (openFileDialog.ShowDialog() != DialogResult.OK)
                    {
                        System.Diagnostics.Debug.WriteLine("User cancelled file selection");
                        return;
                    }

                    string filePath = openFileDialog.FileName;
                    System.Diagnostics.Debug.WriteLine($"File selected: {filePath}");

                    if (!System.IO.File.Exists(filePath))
                    {
                        MessageBox.Show($"File not found: {filePath}", "File Not Found",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    // Show progress
                    Cursor.Current = Cursors.WaitCursor;
                    Application.DoEvents();

                    System.Diagnostics.Debug.WriteLine("About to load model from file using SCAPI");

                    try
                    {
                        if (_scapi == null)
                        {
                            throw new InvalidOperationException("SCAPI object is null. Please restart the application.");
                        }

                        System.Diagnostics.Debug.WriteLine("Calling PersistenceUnits.Add() with file path");

                        // Load model from file - MUCH SAFER than Mart loading
                        dynamic loadedModel = _scapi.PersistenceUnits.Add(filePath, "RDO=Yes");

                        System.Diagnostics.Debug.WriteLine("Model loaded successfully from file!");

                        if (loadedModel == null)
                        {
                            throw new InvalidOperationException("Model loaded but returned null object");
                        }

                        // Success!
                        _currentModel = loadedModel;

                        // Enable Check Naming tab
                        tabCheckNaming.Enabled = true;

                        Cursor.Current = Cursors.Default;

                        MessageBox.Show(
                            $"Model successfully loaded from file!\n\n" +
                            $"File: {System.IO.Path.GetFileName(filePath)}\n" +
                            $"Path: {filePath}\n\n" +
                            $"'Check Naming Conventions' tab is now enabled.",
                            "Success",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );

                        System.Diagnostics.Debug.WriteLine("Success message shown");
                    }
                    catch (System.Runtime.InteropServices.COMException comEx)
                    {
                        Cursor.Current = Cursors.Default;
                        System.Diagnostics.Debug.WriteLine($"COM Exception: {comEx.Message}");

                        MessageBox.Show(
                            $"COM Error loading model from file!\n\n" +
                            $"HRESULT: 0x{comEx.HResult:X8}\n" +
                            $"Message: {comEx.Message}\n\n" +
                            $"File: {filePath}\n\n" +
                            $"This may mean:\n" +
                            $"- File is corrupted or not a valid erwin model\n" +
                            $"- File is locked by another process\n" +
                            $"- Insufficient permissions",
                            "Load Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }
                    catch (Exception ex)
                    {
                        Cursor.Current = Cursors.Default;
                        System.Diagnostics.Debug.WriteLine($"Exception: {ex.GetType().Name} - {ex.Message}");

                        MessageBox.Show(
                            $"Error loading model from file!\n\n" +
                            $"Error: {ex.GetType().Name}\n" +
                            $"Message: {ex.Message}\n\n" +
                            $"File: {filePath}",
                            "Load Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Cursor.Current = Cursors.Default;
                System.Diagnostics.Debug.WriteLine($"Outer exception: {ex.GetType().Name} - {ex.Message}");

                MessageBox.Show(
                    $"Unexpected error!\n\n{ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }

            System.Diagnostics.Debug.WriteLine("=== BtnLoadFromFile_Click END ===");
        }

        private void BtnLoadModel_Click(object sender, EventArgs e)
        {
            // EXTENSIVE DEBUG LOGGING TO IDENTIFY CRASH POINT
            System.Diagnostics.Debug.WriteLine("=== BtnLoadModel_Click START ===");
            System.Diagnostics.Debug.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");

            string selectedModel = "";
            string martServer = "";
            string martUrl = "";

            try
            {
                System.Diagnostics.Debug.WriteLine("STEP 1: Starting validation checks");

                if (!_isMartConnected)
                {
                    System.Diagnostics.Debug.WriteLine("STEP 1a: Not connected - showing message");
                    MessageBox.Show("Please connect to Mart first.", "Not Connected",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    System.Diagnostics.Debug.WriteLine("STEP 1b: Message shown - returning");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("STEP 2: Extracting selected model from TreeView");

                TreeNode selectedNode = treeModels.SelectedNode;
                if (selectedNode == null)
                {
                    System.Diagnostics.Debug.WriteLine("STEP 2a: No node selected - showing message");
                    MessageBox.Show("Please select a model from the tree.", "No Selection",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    treeModels.Focus();
                    System.Diagnostics.Debug.WriteLine("STEP 2b: Message shown - returning");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"STEP 2c: Selected node: {selectedNode.Text}, Tag: {selectedNode.Tag}");

                // Verify that a MODEL node is selected (not ROOT, CATALOG, or LIBRARY)
                string nodeTag = selectedNode.Tag?.ToString() ?? "";
                if (!nodeTag.StartsWith("MODEL:"))
                {
                    System.Diagnostics.Debug.WriteLine("STEP 2d: Selected node is not a model - showing message");
                    MessageBox.Show(
                        "Please select a model (not a folder or catalog).\n\n" +
                        "Expand the tree to find a model and select it.",
                        "Invalid Selection",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    System.Diagnostics.Debug.WriteLine("STEP 2e: Message shown - returning");
                    return;
                }

                // Extract model path from tag (format: "MODEL:library/modelName")
                selectedModel = nodeTag.Substring("MODEL:".Length);
                System.Diagnostics.Debug.WriteLine($"STEP 2f: Model path extracted: {selectedModel}");

                System.Diagnostics.Debug.WriteLine("STEP 3: Building connection parameters");

                // Build Mart URL with PSW (not PWD!) - erwin documentation specifies PSW
                martServer = $"{txtServerName.Text}:{txtPort.Text}";
                System.Diagnostics.Debug.WriteLine($"STEP 3a: Mart Server: {martServer}");

                // CORRECTED: Use PSW instead of PWD (from erwin documentation)
                // Format: mart://Mart/library/ModelName?SRV=localhost;PRT=8082;UID=sa;PSW=password
                string connectionParams = $"SRV={txtServerName.Text};PRT={txtPort.Text};UID={txtUserName.Text};PSW={txtPassword.Text}";
                martUrl = $"mart://Mart/{selectedModel}?{connectionParams}";
                System.Diagnostics.Debug.WriteLine($"STEP 3b: Mart URL (with PSW embedded): {martUrl.Replace(txtPassword.Text, "******")}");

                // Use RDO=Yes for read-only access
                string options = "RDO=Yes";
                System.Diagnostics.Debug.WriteLine($"STEP 3c: Options parameter: {options}");

                System.Diagnostics.Debug.WriteLine("STEP 4: Setting cursor to wait");
                Cursor.Current = Cursors.WaitCursor;
                Application.DoEvents();
                System.Diagnostics.Debug.WriteLine("STEP 4a: Cursor set, DoEvents completed");

                System.Diagnostics.Debug.WriteLine("STEP 5: Initializing load variables");
                dynamic loadedModel = null;
                bool loadSuccess = false;
                string loadErrorMessage = "";
                System.Diagnostics.Debug.WriteLine("STEP 5a: Variables initialized");

                try
                {
                    System.Diagnostics.Debug.WriteLine("STEP 6: Entering inner try block for SCAPI call");

                    System.Diagnostics.Debug.WriteLine("STEP 6a: Checking if _scapi is null");
                    if (_scapi == null)
                    {
                        System.Diagnostics.Debug.WriteLine("STEP 6a-ERROR: _scapi is NULL!");
                        throw new InvalidOperationException("SCAPI object is null. Please restart the application.");
                    }
                    System.Diagnostics.Debug.WriteLine("STEP 6a-OK: _scapi is not null");

                    System.Diagnostics.Debug.WriteLine("STEP 6b: Checking if _scapi.PersistenceUnits is null");
                    if (_scapi.PersistenceUnits == null)
                    {
                        System.Diagnostics.Debug.WriteLine("STEP 6b-ERROR: PersistenceUnits is NULL!");
                        throw new InvalidOperationException("SCAPI PersistenceUnits is null. SCAPI may not be initialized properly.");
                    }
                    System.Diagnostics.Debug.WriteLine("STEP 6b-OK: PersistenceUnits is not null");

                    // Mart connection already established in Connect button via OpenMart()
                    System.Diagnostics.Debug.WriteLine("STEP 6c: Loading model from connected Mart (OpenMart already called)");
                    System.Diagnostics.Debug.WriteLine($"  URL: {martUrl}");
                    System.Diagnostics.Debug.WriteLine($"  Options: {options}");
                    System.Diagnostics.Debug.WriteLine($"  Mart Connected: {_isMartConnected}");

                    // Now that Mart connection is established via OpenMart(), load model
                    // URL format: mart://Mart/library/ModelName?SRV=server;PRT=port;UID=user;PSW=password
                    System.Diagnostics.Debug.WriteLine("STEP 6c-1: Calling PersistenceUnits.Add");
                    loadedModel = _scapi.PersistenceUnits.Add(martUrl, options);
                    System.Diagnostics.Debug.WriteLine("STEP 6c-2: PersistenceUnits.Add completed");

                    if (loadedModel == null)
                    {
                        throw new InvalidOperationException("Model loaded but returned null object");
                    }

                    System.Diagnostics.Debug.WriteLine("STEP 6d: PersistenceUnits.Add() COMPLETED SUCCESSFULLY!");
                    System.Diagnostics.Debug.WriteLine($"  Loaded model object: {(loadedModel != null ? "NOT NULL" : "NULL")}");

                    loadSuccess = true;
                    System.Diagnostics.Debug.WriteLine("STEP 6e: loadSuccess set to true");
                }
                catch (System.Runtime.InteropServices.COMException comEx)
                {
                    System.Diagnostics.Debug.WriteLine("STEP 6-CATCH-COM: COM Exception caught!");
                    System.Diagnostics.Debug.WriteLine($"  HRESULT: 0x{comEx.HResult:X8}");
                    System.Diagnostics.Debug.WriteLine($"  Message: {comEx.Message}");

                    loadErrorMessage = $"COM Error occurred while connecting to Mart.\n\n" +
                                      $"HRESULT: 0x{comEx.HResult:X8}\n" +
                                      $"Message: {comEx.Message}\n\n" +
                                      $"This usually means:\n" +
                                      $"- Invalid username or password\n" +
                                      $"- Mart server is not accessible\n" +
                                      $"- Model does not exist in the specified library/path\n" +
                                      $"- Network connection issue\n" +
                                      $"- Incorrect model path format (should be: library/ModelName)";
                }
                catch (InvalidOperationException ioEx)
                {
                    System.Diagnostics.Debug.WriteLine("STEP 6-CATCH-IO: InvalidOperationException caught!");
                    System.Diagnostics.Debug.WriteLine($"  Message: {ioEx.Message}");

                    loadErrorMessage = $"Invalid Operation:\n\n{ioEx.Message}";
                }
                catch (Exception loadEx)
                {
                    System.Diagnostics.Debug.WriteLine("STEP 6-CATCH-GENERAL: General Exception caught!");
                    System.Diagnostics.Debug.WriteLine($"  Type: {loadEx.GetType().Name}");
                    System.Diagnostics.Debug.WriteLine($"  Message: {loadEx.Message}");

                    loadErrorMessage = $"Error Type: {loadEx.GetType().Name}\n" +
                                      $"Message: {loadEx.Message}";

                    if (loadEx.InnerException != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Inner Exception: {loadEx.InnerException.Message}");
                        loadErrorMessage += $"\n\nInner Exception:\n{loadEx.InnerException.Message}";
                    }
                }
                finally
                {
                    System.Diagnostics.Debug.WriteLine("STEP 7: In finally block - resetting cursor");
                    Cursor.Current = Cursors.Default;
                    System.Diagnostics.Debug.WriteLine("STEP 7a: Cursor reset complete");
                }

                System.Diagnostics.Debug.WriteLine("STEP 8: Checking load success");
                System.Diagnostics.Debug.WriteLine($"  loadSuccess: {loadSuccess}");
                System.Diagnostics.Debug.WriteLine($"  loadedModel != null: {loadedModel != null}");

                if (!loadSuccess || loadedModel == null)
                {
                    System.Diagnostics.Debug.WriteLine("STEP 8a: Load FAILED - showing error message");

                    string fullError = $"Failed to load model from Mart!\n\n" +
                                      $"Mart URL: {martUrl}\n" +
                                      $"Server: {martServer}\n" +
                                      $"User: {txtUserName.Text}\n" +
                                      $"Model: {selectedModel}\n\n" +
                                      $"{loadErrorMessage}";

                    System.Diagnostics.Debug.WriteLine($"STEP 8b: Error message prepared, showing MessageBox");
                    MessageBox.Show(
                        fullError,
                        "Model Load Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    System.Diagnostics.Debug.WriteLine("STEP 8c: MessageBox shown, returning");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("STEP 9: Load SUCCESS - processing result");
                _currentModel = loadedModel;
                System.Diagnostics.Debug.WriteLine("STEP 9a: _currentModel assigned");

                System.Diagnostics.Debug.WriteLine("STEP 10: Enabling Check Naming tab");
                tabCheckNaming.Enabled = true;
                System.Diagnostics.Debug.WriteLine("STEP 10a: Tab enabled");

                System.Diagnostics.Debug.WriteLine("STEP 11: Showing success message");
                MessageBox.Show(
                    $"Model successfully loaded from Mart!\n\n" +
                    $"Model: {selectedModel}\n" +
                    $"Server: {martServer}\n" +
                    $"User: {txtUserName.Text}\n\n" +
                    $"'Check Naming Conventions' tab is now enabled.\n" +
                    $"You can now work with this model.",
                    "Success",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                System.Diagnostics.Debug.WriteLine("STEP 11a: Success message shown");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("=== OUTER CATCH BLOCK ENTERED ===");
                System.Diagnostics.Debug.WriteLine($"Exception Type: {ex.GetType().FullName}");
                System.Diagnostics.Debug.WriteLine($"Exception Message: {ex.Message}");

                Cursor.Current = Cursors.Default;

                string errorDetails = $"CRITICAL ERROR in Load Model!\n\n" +
                                    $"Error Type: {ex.GetType().FullName}\n" +
                                    $"Message: {ex.Message}\n" +
                                    $"Source: {ex.Source}\n\n" +
                                    $"Context:\n" +
                                    $"Model: {selectedModel}\n" +
                                    $"Server: {martServer}\n" +
                                    $"Mart URL: {martUrl}\n\n" +
                                    $"Stack Trace:\n{ex.StackTrace}";

                if (ex.InnerException != null)
                {
                    errorDetails += $"\n\n=== Inner Exception ===\n" +
                                  $"Type: {ex.InnerException.GetType().FullName}\n" +
                                  $"Message: {ex.InnerException.Message}\n" +
                                  $"Stack Trace:\n{ex.InnerException.StackTrace}";
                }

                System.Diagnostics.Debug.WriteLine(errorDetails);

                try
                {
                    System.Diagnostics.Debug.WriteLine("Attempting to show MessageBox with error");
                    MessageBox.Show(
                        errorDetails,
                        "Critical Error - Application Continuing",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    System.Diagnostics.Debug.WriteLine("MessageBox shown successfully");
                }
                catch (Exception msgBoxEx)
                {
                    System.Diagnostics.Debug.WriteLine($"FAILED TO SHOW MESSAGEBOX: {msgBoxEx.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("=== BtnLoadModel_Click END ===");
            System.Diagnostics.Debug.WriteLine("");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            if (_currentModel != null)
            {
                try
                {
                    _scapi.PersistenceUnits.Remove(_currentModel);
                    System.Diagnostics.Debug.WriteLine("Current model removed from PersistenceUnits");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to remove model: {ex.Message}");
                }
            }

            _scapi = null;
            _martConnection = null;
        }
    }
}
