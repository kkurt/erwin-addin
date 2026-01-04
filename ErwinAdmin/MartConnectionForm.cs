using System;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

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

        private HttpClient _httpClient;
        private string _bearerToken;
        private string _martApiBaseUrl;

        public MartConnectionForm()
        {
            InitializeComponent();
            InitializeSCAPI();
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
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

        private TextBox txtModelPath;

        private void InitializeLoadModelTab()
        {
            // Model Selection Group
            grpModelSelection = new GroupBox();
            grpModelSelection.Text = "Load Model from Mart";
            grpModelSelection.Location = new Point(15, 15);
            grpModelSelection.Size = new Size(520, 150);
            tabLoadModel.Controls.Add(grpModelSelection);

            // Label
            Label lblModelPath = new Label();
            lblModelPath.Text = "Model Path (Library/ModelName):";
            lblModelPath.Location = new Point(15, 30);
            lblModelPath.Size = new Size(200, 20);
            grpModelSelection.Controls.Add(lblModelPath);

            // TextBox for model path
            txtModelPath = new TextBox();
            txtModelPath.Location = new Point(15, 55);
            txtModelPath.Size = new Size(490, 23);
            txtModelPath.Text = "Kursat/PublicationSystemSample_Modified";
            txtModelPath.Font = new Font("Consolas", 10F);
            grpModelSelection.Controls.Add(txtModelPath);

            // Example label
            Label lblExample = new Label();
            lblExample.Text = "Example: Kursat/ModelName or Library/Folder/ModelName";
            lblExample.Location = new Point(15, 85);
            lblExample.Size = new Size(490, 20);
            lblExample.ForeColor = Color.Gray;
            grpModelSelection.Controls.Add(lblExample);

            // Load from Mart Button
            btnLoadModel = new Button();
            btnLoadModel.Text = "Load from Mart";
            btnLoadModel.Location = new Point(15, 110);
            btnLoadModel.Size = new Size(235, 30);
            btnLoadModel.Click += BtnLoadModel_Click;
            grpModelSelection.Controls.Add(btnLoadModel);

            // Load from File Group
            GroupBox grpFileLoad = new GroupBox();
            grpFileLoad.Text = "Or Load Model from File";
            grpFileLoad.Location = new Point(15, 180);
            grpFileLoad.Size = new Size(520, 80);
            tabLoadModel.Controls.Add(grpFileLoad);

            // Load from File Button
            Button btnLoadFromFile = new Button();
            btnLoadFromFile.Text = "Browse and Load from File...";
            btnLoadFromFile.Location = new Point(15, 30);
            btnLoadFromFile.Size = new Size(490, 35);
            btnLoadFromFile.Click += BtnLoadFromFile_Click;
            grpFileLoad.Controls.Add(btnLoadFromFile);

            // Info TreeView (shows connection info and catalog browsing limitation)
            treeModels = new TreeView();
            treeModels.Location = new Point(15, 275);
            treeModels.Size = new Size(520, 180);
            treeModels.Font = new Font("Consolas", 9F);
            treeModels.AfterSelect += TreeModels_AfterSelect;
            tabLoadModel.Controls.Add(treeModels);
        }

        private void TreeModels_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node != null && e.Node.Tag != null)
            {
                string nodeTag = e.Node.Tag.ToString();
                if (nodeTag.StartsWith("MODEL:"))
                {
                    string modelPath = nodeTag.Substring("MODEL:".Length);
                    txtModelPath.Text = modelPath;
                }
            }
        }

        private void InitializeCheckNamingTab()
        {
            // Placeholder for Check Naming Conventions tab
            // Will be implemented in next step
        }

        private void TreeModels_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            try
            {
                TreeNode expandingNode = e.Node;
                string nodeTag = expandingNode.Tag?.ToString() ?? "";

                // Check if this is a LIBRARY or CATEGORY node with placeholder children
                if (nodeTag.StartsWith("LIBRARY:") || nodeTag.StartsWith("CATEGORY:"))
                {
                    if (expandingNode.Nodes.Count == 1 &&
                        expandingNode.Nodes[0].Tag?.ToString() == "PLACEHOLDER")
                    {
                        string path = "";
                        if (nodeTag.StartsWith("LIBRARY:"))
                        {
                            path = nodeTag.Substring("LIBRARY:".Length);
                        }
                        else if (nodeTag.StartsWith("CATEGORY:"))
                        {
                            path = nodeTag.Substring("CATEGORY:".Length);
                        }

                        // Remove placeholder
                        expandingNode.Nodes.Clear();

                        // Try to load models from Mart via SCAPI
                        bool loaded = TryLoadModelsFromPath(path, expandingNode);

                        if (!loaded)
                        {
                            TreeNode noModelsNode = new TreeNode("(No models found)")
                            {
                                Tag = "INFO",
                                ForeColor = Color.Gray
                            };
                            expandingNode.Nodes.Add(noModelsNode);
                        }
                    }
                }
            }
            catch { }
        }

        private bool TryLoadModelsFromPath(string path, TreeNode parentNode)
        {
            try
            {
                // Try to enumerate models from SCAPI MartConnection
                if (_scapi.MartConnection != null && _scapi.MartConnection.Libraries != null)
                {
                    // Split path to get library name and category/folder
                    string[] pathParts = path.Split('/');
                    string libraryName = pathParts[0];
                    string category = pathParts.Length > 1 ? pathParts[1] : null;

                    for (int i = 1; i <= _scapi.MartConnection.Libraries.Count; i++)
                    {
                        dynamic lib = _scapi.MartConnection.Libraries.Item(i);
                        if (lib.Name == libraryName && lib.Models != null)
                        {
                            int modelCount = lib.Models.Count;

                            for (int j = 1; j <= modelCount; j++)
                            {
                                dynamic model = lib.Models.Item(j);
                                string modelName = model.Name;

                                // If category specified, filter models by category
                                // Otherwise, add all models
                                TreeNode modelNode = new TreeNode(modelName)
                                {
                                    Tag = $"MODEL:{path}/{modelName}"
                                };
                                parentNode.Nodes.Add(modelNode);
                            }

                            return modelCount > 0;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private string _lastApiError = "";

        private async Task<string> AuthenticateMartApiAsync()
        {
            try
            {
                // Try MartServerCloud JWT endpoint first (erwin 10+)
                var authUrl = $"http://{txtServerName.Text}:{txtPort.Text}/MartServerCloud/jwt/authenticate/login";

                var authData = new
                {
                    username = txtUserName.Text,
                    password = txtPassword.Text,
                    versionString = "10.1",
                    startNewSession = "true",
                    resetPassword = "false",
                    isWindowsAuthentication = "false",
                    deviceInfo = "{\"deviceId\":\"erwin-admin-tool\",\"deviceData\":\"Windows|ErwinAdmin\"}"
                };

                string jsonBody = System.Text.Json.JsonSerializer.Serialize(authData);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(authUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var jsonDoc = System.Text.Json.JsonDocument.Parse(responseContent);

                    // Try different token property names
                    if (jsonDoc.RootElement.TryGetProperty("id_token", out var idTokenElement))
                    {
                        return idTokenElement.GetString();
                    }
                    else if (jsonDoc.RootElement.TryGetProperty("token", out var tokenElement))
                    {
                        return tokenElement.GetString();
                    }
                    else if (jsonDoc.RootElement.TryGetProperty("jwtToken", out var jwtElement))
                    {
                        return jwtElement.GetString();
                    }
                    else if (jsonDoc.RootElement.TryGetProperty("bearerToken", out var bearerElement))
                    {
                        return bearerElement.GetString();
                    }

                    // If no token found, return whole response for debugging
                    _lastApiError = $"Auth response OK but no token found. Response: {responseContent.Substring(0, Math.Min(200, responseContent.Length))}";
                    return null;
                }

                _lastApiError = $"Auth failed: HTTP {(int)response.StatusCode} - {response.ReasonPhrase}";
                return null;
            }
            catch (Exception ex)
            {
                _lastApiError = $"Auth exception: {ex.Message}";
                return null;
            }
        }

        private async Task<System.Text.Json.JsonDocument> GetMartCatalogJsonAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_bearerToken))
                {
                    _bearerToken = await AuthenticateMartApiAsync();
                    if (string.IsNullOrEmpty(_bearerToken))
                        return null;
                }

                var catalogUrl = $"http://{txtServerName.Text}:{txtPort.Text}/MartServer/service/catalog/getCatalogChildren";

                var catalogData = new
                {
                    d = "0",  // Root level
                    state = "false",  // No encryption for now
                    getHiddenVersions = "N",
                    connector = "false"
                };

                string jsonBody = System.Text.Json.JsonSerializer.Serialize(catalogData);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                // Add JWT token as bearer
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _bearerToken);

                var response = await _httpClient.PostAsync(catalogUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();

                    // Response might start with "0|" prefix
                    if (responseContent.StartsWith("0|"))
                    {
                        responseContent = responseContent.Substring(2);
                    }

                    return System.Text.Json.JsonDocument.Parse(responseContent);
                }

                _lastApiError = $"Catalog failed: HTTP {(int)response.StatusCode}";
                return null;
            }
            catch (Exception ex)
            {
                _lastApiError = $"Catalog exception: {ex.Message}";
                return null;
            }
        }

        private async void LoadMartCatalog()
        {
            treeModels.Nodes.Clear();
            TreeNode martRoot = new TreeNode("Mart - Loading...") { Tag = "ROOT" };
            treeModels.Nodes.Add(martRoot);
            martRoot.Expand();

            try
            {
                Cursor.Current = Cursors.WaitCursor;
                Application.DoEvents();

                var catalogJson = await GetMartCatalogJsonAsync();

                if (catalogJson == null)
                {
                    martRoot.Text = "Mart Connected (Catalog API unavailable)";

                    if (!string.IsNullOrEmpty(_lastApiError))
                    {
                        TreeNode errorDetailNode = new TreeNode(_lastApiError)
                        {
                            Tag = "ERROR",
                            ForeColor = Color.Red
                        };
                        martRoot.Nodes.Add(errorDetailNode);
                    }

                    TreeNode infoNode = new TreeNode("REST API authentication failed or not available")
                    {
                        Tag = "INFO",
                        ForeColor = Color.Red
                    };
                    martRoot.Nodes.Add(infoNode);

                    TreeNode instructionNode = new TreeNode("Enter model path manually: Library/ModelName")
                    {
                        Tag = "INFO",
                        ForeColor = Color.DarkGreen
                    };
                    martRoot.Nodes.Add(instructionNode);

                    TreeNode exampleNode = new TreeNode("Example: Kursat/PublicationSystemSample_Modified")
                    {
                        Tag = "INFO",
                        ForeColor = Color.Gray
                    };
                    martRoot.Nodes.Add(exampleNode);

                    martRoot.Expand();
                    return;
                }

                martRoot.Text = "Mart Catalog";
                martRoot.Nodes.Clear();

                // Parse catalog JSON
                if (catalogJson.RootElement.TryGetProperty("getChildCatalogs", out var catalogs))
                {
                    foreach (var catalog in catalogs.EnumerateArray())
                    {
                        string entryName = catalog.GetProperty("entryname").GetString();
                        string entryType = catalog.GetProperty("entrytype").GetString();
                        string entryId = catalog.GetProperty("entryid").GetString();

                        // For now, just show libraries (will need recursive loading for full catalog)
                        var catalogNode = new TreeNode(entryName)
                        {
                            Tag = $"CATALOG:{entryId}:{entryName}",
                            ForeColor = Color.DarkBlue
                        };
                        martRoot.Nodes.Add(catalogNode);

                        // Add placeholder for children (will implement lazy loading later)
                        catalogNode.Nodes.Add(new TreeNode("Loading...") { Tag = "PLACEHOLDER" });
                    }
                }

                if (martRoot.Nodes.Count == 0)
                {
                    TreeNode emptyNode = new TreeNode("No catalogs found")
                    {
                        Tag = "INFO",
                        ForeColor = Color.Gray
                    };
                    martRoot.Nodes.Add(emptyNode);
                }

                martRoot.Expand();
            }
            catch (Exception ex)
            {
                martRoot.Text = "Mart Connected (Error loading catalog)";

                TreeNode errorNode = new TreeNode($"Error: {ex.Message}")
                {
                    Tag = "ERROR",
                    ForeColor = Color.Red
                };
                martRoot.Nodes.Add(errorNode);

                TreeNode instructionNode = new TreeNode("Enter model path manually: Library/ModelName")
                {
                    Tag = "INFO",
                    ForeColor = Color.DarkGreen
                };
                martRoot.Nodes.Add(instructionNode);

                martRoot.Expand();
            }
            finally
            {
                Cursor.Current = Cursors.Default;
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

                // Set Mart REST API base URL
                _martApiBaseUrl = $"http://{txtServerName.Text}:{txtPort.Text}/MartServer/api";

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
                    $"'Load Model' tab is now enabled.\n\n" +
                    $"Loading catalog via REST API...\n" +
                    $"You can select a model from the tree or enter path manually.",
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
            try
            {
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Title = "Select erwin Model File";
                    openFileDialog.Filter = "erwin Model Files (*.erwin;*.erwm)|*.erwin;*.erwm|All Files (*.*)|*.*";
                    openFileDialog.FilterIndex = 1;
                    openFileDialog.RestoreDirectory = true;

                    if (openFileDialog.ShowDialog() != DialogResult.OK) return;

                    string filePath = openFileDialog.FileName;

                    if (!System.IO.File.Exists(filePath))
                    {
                        MessageBox.Show($"File not found: {filePath}", "File Not Found",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    Cursor.Current = Cursors.WaitCursor;
                    Application.DoEvents();

                    try
                    {
                        if (_scapi == null)
                            throw new InvalidOperationException("SCAPI object is null. Please restart the application.");

                        dynamic loadedModel = _scapi.PersistenceUnits.Add(filePath, "RDO=Yes");

                        if (loadedModel == null)
                            throw new InvalidOperationException("Model loaded but returned null object");

                        _currentModel = loadedModel;
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
                    }
                    catch (System.Runtime.InteropServices.COMException comEx)
                    {
                        Cursor.Current = Cursors.Default;
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
                MessageBox.Show(
                    $"Unexpected error!\n\n{ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private void BtnLoadModel_Click(object sender, EventArgs e)
        {
            string selectedModel = "";
            string martServer = "";
            string martUrl = "";

            try
            {
                if (!_isMartConnected)
                {
                    MessageBox.Show("Please connect to Mart first.", "Not Connected",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // First, check if a model is selected in TreeView
                TreeNode selectedNode = treeModels.SelectedNode;
                string modelPath = "";

                if (selectedNode != null && selectedNode.Tag != null)
                {
                    string nodeTag = selectedNode.Tag.ToString();
                    if (nodeTag.StartsWith("MODEL:"))
                    {
                        modelPath = nodeTag.Substring("MODEL:".Length);
                    }
                    else if (!nodeTag.StartsWith("LIBRARY:") && !nodeTag.StartsWith("INFO") && !nodeTag.StartsWith("ERROR") && !nodeTag.StartsWith("ROOT"))
                    {
                        MessageBox.Show(
                            "Please select a model from the tree (not a library or folder).\n\n" +
                            "Expand the library nodes to see available models.",
                            "Invalid Selection",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }
                }

                // If no model selected from tree, use manual TextBox input
                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    modelPath = txtModelPath.Text.Trim();
                    if (string.IsNullOrWhiteSpace(modelPath))
                    {
                        MessageBox.Show(
                            "Please select a model from the tree or enter a model path.\n\n" +
                            "Format: Library/ModelName\n" +
                            "Example: Kursat/PublicationSystemSample_Modified",
                            "No Model Selected",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        txtModelPath.Focus();
                        return;
                    }
                }

                selectedModel = modelPath;
                martServer = $"{txtServerName.Text}:{txtPort.Text}";
                string connectionParams = $"SRV={txtServerName.Text};PRT={txtPort.Text};UID={txtUserName.Text};PSW={txtPassword.Text}";
                martUrl = $"mart://Mart/{selectedModel}?{connectionParams}";

                Cursor.Current = Cursors.WaitCursor;
                Application.DoEvents();

                dynamic loadedModel = null;
                string loadErrorMessage = "";

                try
                {
                    if (_scapi?.PersistenceUnits == null)
                        throw new InvalidOperationException("SCAPI not initialized. Please restart the application.");

                    loadedModel = _scapi.PersistenceUnits.Add(martUrl, "RDO=Yes");

                    if (loadedModel == null)
                        throw new InvalidOperationException("Model loaded but returned null object");

                    _currentModel = loadedModel;
                    tabCheckNaming.Enabled = true;
                    Cursor.Current = Cursors.Default;

                    MessageBox.Show(
                        $"Model successfully loaded from Mart!\n\n" +
                        $"Model: {selectedModel}\n" +
                        $"Server: {martServer}\n" +
                        $"User: {txtUserName.Text}\n\n" +
                        $"'Check Naming Conventions' tab is now enabled.",
                        "Success",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                catch (System.Runtime.InteropServices.COMException comEx)
                {
                    Cursor.Current = Cursors.Default;
                    MessageBox.Show(
                        $"Failed to load model from Mart!\n\n" +
                        $"Mart URL: {martUrl}\n" +
                        $"Server: {martServer}\n" +
                        $"User: {txtUserName.Text}\n" +
                        $"Model: {selectedModel}\n\n" +
                        $"HRESULT: 0x{comEx.HResult:X8}\n" +
                        $"Message: {comEx.Message}\n\n" +
                        $"This usually means:\n" +
                        $"- Invalid username or password\n" +
                        $"- Mart server is not accessible\n" +
                        $"- Model does not exist in the specified library/path\n" +
                        $"- Network connection issue",
                        "Model Load Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
                catch (Exception loadEx)
                {
                    Cursor.Current = Cursors.Default;
                    MessageBox.Show(
                        $"Failed to load model from Mart!\n\n" +
                        $"Model: {selectedModel}\n" +
                        $"Server: {martServer}\n\n" +
                        $"Error: {loadEx.Message}",
                        "Model Load Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
            catch (Exception ex)
            {
                Cursor.Current = Cursors.Default;
                MessageBox.Show(
                    $"Unexpected error!\n\n" +
                    $"Error: {ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            if (_currentModel != null)
            {
                try
                {
                    _scapi.PersistenceUnits.Remove(_currentModel);
                }
                catch { }
            }

            _httpClient?.Dispose();
            _scapi = null;
            _martConnection = null;
        }
    }
}
