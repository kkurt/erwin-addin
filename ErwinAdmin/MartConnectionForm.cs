using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using EliteSoft.Erwin.Admin.Models;
using EliteSoft.Erwin.Admin.Services;
using EliteSoft.Erwin.Admin.UI;
using EliteSoft.Erwin.Shared.Models;
using EliteSoft.Erwin.Shared.Services;
using EliteSoft.Erwin.Shared.Data;
using EliteSoft.Erwin.Shared.Data.Entities;
using EliteSoft.Erwin.Shared.Data.Repositories;

namespace EliteSoft.Erwin.Admin
{
    public partial class MartConnectionForm : Form
    {
        #region Services

        private readonly IMartApiClient _martApi;
        private readonly IErwinScapiService _scapiService;
        private readonly IMartCatalogService _catalogService;
        private readonly BootstrapService _bootstrapService;

        #endregion

        #region UI Controls

        // Main TabControl
        private TabControl _mainTabControl = null!;
        private TabPage _tabMartProcesses = null!;
        private TabPage _tabExtensionProps = null!;

        // Extension Properties tab content
        private TabControl _tabExtPropsInner = null!;
        private TabPage _tabRepoProperties = null!;
        private TabPage _tabRepoConnection = null!;

        // Repository Database Properties tab controls
        private CheckBox _chkUseTableTypes = null!;
        private DataGridView _gridTableTypes = null!;
        private Button _btnClearTableTypes = null!;

        // Repository DB Connection tab controls
        private ComboBox _cmbRepoDbType = null!;
        private TextBox _txtRepoHost = null!;
        private TextBox _txtRepoPort = null!;
        private TextBox _txtRepoDatabase = null!;
        private TextBox _txtRepoUsername = null!;
        private TextBox _txtRepoPassword = null!;
        private Button _btnRepoTest = null!;
        private Button _btnRepoSave = null!;
        private Label _lblConnStatus = null!;

        // DB Init tab controls
        private Button _btnRepoInitDb = null!;
        private Label _lblRepoStatus = null!;

        // Mart Processes tab content
        private Panel _panelSidebar = null!;
        private Panel _panelContent = null!;
        private Panel _panelConnect = null!;
        private Panel _panelModels = null!;
        private Panel _panelNaming = null!;

        private Button _btnNavConnect = null!;
        private Button _btnNavModels = null!;
        private Button _btnNavNaming = null!;

        // Status bar (bottom of form)
        private Panel _panelStatus = null!;

        private TextBox _txtServerName = null!;
        private TextBox _txtPort = null!;
        private TextBox _txtUserName = null!;
        private TextBox _txtPassword = null!;
        private TextBox _txtLog = null!;

        private Button _btnConnect = null!;
        private Button _btnLoadFromFile = null!;

        private Label _lblConnectionStatus = null!;
        private Label _lblStatus = null!;
        private ProgressBar _progressBar = null!;
        private TreeView _treeModels = null!;

        // Model Properties tab controls
        private TabControl _tabModelProps = null!;
        private TextBox _txtModelName = null!;
        private ListView _listUdps = null!;
        private ListView _listNamingStandards = null!;
        private Label _lblModelSubtitle = null!;
        private string _currentModelPath = "";

        // Loading overlay
        private Panel _panelLoading = null!;
        private Label _lblLoadingText = null!;

        #endregion

        #region State

        private int _currentPanel;

        #endregion

        public MartConnectionForm()
        {
            _martApi = new MartApiClient();
            _scapiService = new ErwinScapiService();
            _catalogService = new MartCatalogService();
            _bootstrapService = new BootstrapService();

            _martApi.LogMessage += OnApiLogMessage;
            _martApi.AuthenticationStateChanged += OnAuthenticationStateChanged;
            _catalogService.LogMessage += OnApiLogMessage;

            InitializeUI();
            CheckScapiStatus();
        }

        #region Initialization

        private void InitializeUI()
        {
            Text = "Elite Soft erwin Admin";
            Size = new Size(1050, 720);
            MinimumSize = new Size(1050, 720);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = AppTheme.FormBackground;
            Font = AppTheme.DefaultFont;

            InitializeMainTabControl();
            InitializeStatusBar();
            InitializeLoadingOverlay();
            ShowPanel(0);
        }

        private void InitializeMainTabControl()
        {
            // Create main TabControl that spans the form (except status bar)
            _mainTabControl = new TabControl
            {
                Location = new Point(0, 0),
                Size = new Size(ClientSize.Width, ClientSize.Height - 35),
                Font = AppTheme.DefaultFont,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            // Extension Properties tab (FIRST)
            _tabExtensionProps = new TabPage("Elite Soft Erwin Extension Properties")
            {
                UseVisualStyleBackColor = true,
                Padding = new Padding(10)
            };
            InitializeExtensionPropsTab();
            _mainTabControl.TabPages.Add(_tabExtensionProps);

            // Mart Processes tab (SECOND)
            _tabMartProcesses = new TabPage("Mart Processes")
            {
                UseVisualStyleBackColor = true,
                Padding = new Padding(0)
            };
            InitializeMartProcessesTab();
            _mainTabControl.TabPages.Add(_tabMartProcesses);

            Controls.Add(_mainTabControl);
        }

        private void InitializeExtensionPropsTab()
        {
            // Inner TabControl for Repository tabs
            _tabExtPropsInner = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = AppTheme.DefaultFont
            };

            // Repository DB Connection tab (FIRST)
            _tabRepoConnection = new TabPage("Repository Database Connection")
            {
                UseVisualStyleBackColor = true,
                Padding = new Padding(15)
            };
            InitializeRepoConnectionTab();
            _tabExtPropsInner.TabPages.Add(_tabRepoConnection);

            // DB Init tab (SECOND)
            _tabRepoProperties = new TabPage("DB Init")
            {
                UseVisualStyleBackColor = true,
                Padding = new Padding(15)
            };
            InitializeDbInitTab();
            _tabExtPropsInner.TabPages.Add(_tabRepoProperties);

            _tabExtensionProps.Controls.Add(_tabExtPropsInner);
        }

        private void InitializeDbInitTab()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.PanelBackground
            };

            // Use Table Types checkbox
            _chkUseTableTypes = new CheckBox
            {
                Location = new Point(20, 20),
                Size = new Size(300, 24),
                Text = "Use Table Types",
                Font = AppTheme.DefaultFont,
                Checked = true
            };
            _chkUseTableTypes.CheckedChanged += OnUseTableTypesChanged;
            panel.Controls.Add(_chkUseTableTypes);

            // Table Types DataGridView
            _gridTableTypes = new DataGridView
            {
                Location = new Point(20, 55),
                Size = new Size(500, 200),
                Font = AppTheme.DefaultFont,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EditMode = DataGridViewEditMode.EditOnEnter
            };

            // Add columns
            _gridTableTypes.Columns.Add("NAME", "NAME");
            _gridTableTypes.Columns.Add("AFFIX", "AFFIX");

            var locationColumn = new DataGridViewComboBoxColumn
            {
                Name = "NAME_EXTENSION_LOCATION",
                HeaderText = "NAME_EXTENSION_LOCATION",
                Items = { "PREFIX", "SUFFIX" },
                FlatStyle = FlatStyle.Flat
            };
            _gridTableTypes.Columns.Add(locationColumn);

            // Set column widths
            _gridTableTypes.Columns["NAME"].FillWeight = 40;
            _gridTableTypes.Columns["AFFIX"].FillWeight = 25;
            _gridTableTypes.Columns["NAME_EXTENSION_LOCATION"].FillWeight = 35;

            // Load default values
            LoadDefaultTableTypes();

            panel.Controls.Add(_gridTableTypes);

            // Clear button
            _btnClearTableTypes = ControlFactory.CreateButton("Clear All", 420, 265, 100, 30, ButtonStyle.Secondary, OnClearTableTypesClick);
            panel.Controls.Add(_btnClearTableTypes);

            // Initialize button
            _btnRepoInitDb = ControlFactory.CreateButton("Initialize", 420, 305, 100, 30, onClick: OnDbInitClick);
            panel.Controls.Add(_btnRepoInitDb);

            // Status label for DB Init
            _lblRepoStatus = ControlFactory.CreateLabel("", 20, 350, foreColor: AppTheme.TextSecondary);
            _lblRepoStatus.AutoSize = false;
            _lblRepoStatus.Size = new Size(500, 40);
            panel.Controls.Add(_lblRepoStatus);

            _tabRepoProperties.Controls.Add(panel);
        }

        private void OnDbInitClick(object sender, EventArgs e)
        {
            _lblRepoStatus.Text = "Initializing database...";
            _lblRepoStatus.ForeColor = AppTheme.Info;
            Application.DoEvents();

            try
            {
                var config = _bootstrapService.GetConfig();
                if (config == null || !config.IsConfigured)
                {
                    _lblRepoStatus.Text = "Please configure and save Repository DB Connection first.";
                    _lblRepoStatus.ForeColor = AppTheme.Error;
                    return;
                }

                using (var context = new RepoDbContext(config))
                {
                    // Ensure tables are created
                    context.EnsureTablesCreated();

                    // Insert TABLE_TYPE values if they don't exist
                    if (_chkUseTableTypes.Checked)
                    {
                        int insertedCount = 0;
                        foreach (DataGridViewRow row in _gridTableTypes.Rows)
                        {
                            if (row.IsNewRow) continue;

                            var name = row.Cells["NAME"].Value?.ToString();
                            var affix = row.Cells["AFFIX"].Value?.ToString();
                            var location = row.Cells["NAME_EXTENSION_LOCATION"].Value?.ToString();

                            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(affix) && !string.IsNullOrWhiteSpace(location))
                            {
                                // Check if already exists
                                var exists = context.TableTypes.Any(t => t.Name == name);
                                if (!exists)
                                {
                                    context.TableTypes.Add(new TableType
                                    {
                                        Name = name,
                                        Affix = affix,
                                        NameExtensionLocation = location
                                    });
                                    insertedCount++;
                                }
                            }
                        }
                        context.SaveChanges();

                        _lblRepoStatus.Text = $"Database initialized. {insertedCount} table type(s) inserted.";
                    }
                    else
                    {
                        _lblRepoStatus.Text = "Database tables created successfully.";
                    }
                    _lblRepoStatus.ForeColor = AppTheme.Success;
                }
            }
            catch (Exception ex)
            {
                _lblRepoStatus.Text = $"Initialization failed: {ex.Message}";
                _lblRepoStatus.ForeColor = AppTheme.Error;
            }
        }

        private void LoadDefaultTableTypes()
        {
            _gridTableTypes.Rows.Clear();
            var defaults = TableTypeDefaults.GetDefaults();
            foreach (var tt in defaults)
            {
                _gridTableTypes.Rows.Add(tt.Name, tt.Affix, tt.NameExtensionLocation);
            }
        }

        private void OnUseTableTypesChanged(object sender, EventArgs e)
        {
            _gridTableTypes.Enabled = _chkUseTableTypes.Checked;
            _btnClearTableTypes.Enabled = _chkUseTableTypes.Checked;
        }

        private void OnClearTableTypesClick(object sender, EventArgs e)
        {
            _gridTableTypes.Rows.Clear();
        }

        private void InitializeRepoConnectionTab()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.PanelBackground
            };

            panel.Controls.Add(ControlFactory.CreateTitle("Repository Database Connection", 20, 15));
            panel.Controls.Add(ControlFactory.CreateLabel("This is the central database for storing Extension configuration.", 20, 50, AppTheme.SmallFont, AppTheme.TextSecondary));

            // DB_TYPE (ComboBox)
            panel.Controls.Add(ControlFactory.CreateLabel("DB_TYPE:", 20, 90));
            _cmbRepoDbType = new ComboBox
            {
                Location = new Point(150, 87),
                Size = new Size(250, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = AppTheme.DefaultFont
            };
            _cmbRepoDbType.Items.AddRange(DbTypes.All);
            _cmbRepoDbType.SelectedIndex = 0;
            _cmbRepoDbType.SelectedIndexChanged += OnRepoDbTypeChanged;
            panel.Controls.Add(_cmbRepoDbType);

            // HOST
            panel.Controls.Add(ControlFactory.CreateLabel("HOST:", 20, 135));
            _txtRepoHost = ControlFactory.CreateTextBox(150, 132, 250, "");
            panel.Controls.Add(_txtRepoHost);

            // PORT
            panel.Controls.Add(ControlFactory.CreateLabel("PORT:", 20, 180));
            _txtRepoPort = ControlFactory.CreateTextBox(150, 177, 250, "");
            panel.Controls.Add(_txtRepoPort);

            // DATABASE
            panel.Controls.Add(ControlFactory.CreateLabel("DATABASE:", 20, 225));
            _txtRepoDatabase = ControlFactory.CreateTextBox(150, 222, 250, "");
            panel.Controls.Add(_txtRepoDatabase);

            // USERNAME
            panel.Controls.Add(ControlFactory.CreateLabel("USERNAME:", 20, 270));
            _txtRepoUsername = ControlFactory.CreateTextBox(150, 267, 250, "");
            panel.Controls.Add(_txtRepoUsername);

            // PASSWORD
            panel.Controls.Add(ControlFactory.CreateLabel("PASSWORD:", 20, 315));
            _txtRepoPassword = ControlFactory.CreateTextBox(150, 312, 250, "", isPassword: true);
            panel.Controls.Add(_txtRepoPassword);

            // Buttons
            _btnRepoTest = ControlFactory.CreateButton("Test Connection", 150, 370, 140, 35, onClick: OnRepoTestClick);
            panel.Controls.Add(_btnRepoTest);

            _btnRepoSave = ControlFactory.CreateButton("Save", 300, 370, 100, 35, onClick: OnRepoSaveClick);
            panel.Controls.Add(_btnRepoSave);

            // Status label
            _lblConnStatus = ControlFactory.CreateLabel("", 150, 420, foreColor: AppTheme.TextSecondary);
            _lblConnStatus.AutoSize = false;
            _lblConnStatus.Size = new Size(500, 40);
            panel.Controls.Add(_lblConnStatus);

            _tabRepoConnection.Controls.Add(panel);

            // Load existing config if available
            LoadRepoConfig();
        }

        private void OnRepoDbTypeChanged(object sender, EventArgs e)
        {
            // Update default port based on DB type
            string dbType = _cmbRepoDbType.SelectedItem?.ToString() ?? DbTypes.MSSQL;
            _txtRepoPort.Text = DbTypes.GetDefaultPort(dbType);
        }

        private void LoadRepoConfig()
        {
            var config = _bootstrapService.GetConfig();
            if (config != null && config.IsConfigured)
            {
                // Set combo box selection
                for (int i = 0; i < _cmbRepoDbType.Items.Count; i++)
                {
                    if (_cmbRepoDbType.Items[i].ToString().Equals(config.DbType, StringComparison.OrdinalIgnoreCase))
                    {
                        _cmbRepoDbType.SelectedIndex = i;
                        break;
                    }
                }

                _txtRepoHost.Text = config.Host;
                _txtRepoPort.Text = config.Port;
                _txtRepoDatabase.Text = config.Database;
                _txtRepoUsername.Text = config.Username;
                _txtRepoPassword.Text = config.Password;

                _lblConnStatus.Text = "Configuration loaded from local storage.";
                _lblConnStatus.ForeColor = AppTheme.Info;
            }
        }

        private BootstrapConfig GetRepoConfigFromUI()
        {
            return new BootstrapConfig
            {
                DbType = _cmbRepoDbType.SelectedItem?.ToString() ?? DbTypes.MSSQL,
                Host = _txtRepoHost.Text,
                Port = _txtRepoPort.Text,
                Database = _txtRepoDatabase.Text,
                Username = _txtRepoUsername.Text,
                Password = _txtRepoPassword.Text
            };
        }

        private void OnRepoTestClick(object sender, EventArgs e)
        {
            _lblConnStatus.Text = "Testing connection...";
            _lblConnStatus.ForeColor = AppTheme.Info;
            Application.DoEvents();

            try
            {
                var config = GetRepoConfigFromUI();
                var factory = new RepoDbContextFactory();

                if (factory.TestConnection(config, out string errorMessage))
                {
                    _lblConnStatus.Text = "Connection successful!";
                    _lblConnStatus.ForeColor = AppTheme.Success;
                }
                else
                {
                    _lblConnStatus.Text = $"Connection failed: {errorMessage}";
                    _lblConnStatus.ForeColor = AppTheme.Error;
                }
            }
            catch (Exception ex)
            {
                _lblConnStatus.Text = $"Error: {ex.Message}";
                _lblConnStatus.ForeColor = AppTheme.Error;
            }
        }

        private void OnRepoSaveClick(object sender, EventArgs e)
        {
            _lblConnStatus.Text = "Saving configuration...";
            _lblConnStatus.ForeColor = AppTheme.Info;
            Application.DoEvents();

            try
            {
                var config = GetRepoConfigFromUI();
                _bootstrapService.SaveConfig(config);

                _lblConnStatus.Text = "Configuration saved successfully!";
                _lblConnStatus.ForeColor = AppTheme.Success;
            }
            catch (Exception ex)
            {
                _lblConnStatus.Text = $"Save failed: {ex.Message}";
                _lblConnStatus.ForeColor = AppTheme.Error;
            }
        }

        private void InitializeMartProcessesTab()
        {
            // Sidebar - dock to left
            _panelSidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 200,
                BackColor = Color.FromArgb(230, 230, 230),
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblAppTitle = ControlFactory.CreateLabel("Mart Connection", 15, 20, AppTheme.SubtitleFont);
            _panelSidebar.Controls.Add(lblAppTitle);

            _btnNavConnect = ControlFactory.CreateNavButton("1. Connect", 0, true, ShowPanel);
            _btnNavConnect.Location = new Point(10, 70);
            _btnNavConnect.Size = new Size(180, 40);
            _panelSidebar.Controls.Add(_btnNavConnect);

            _btnNavModels = ControlFactory.CreateNavButton("2. Load Model", 1, false, ShowPanel);
            _btnNavModels.Location = new Point(10, 120);
            _btnNavModels.Size = new Size(180, 40);
            _btnNavModels.Enabled = false;
            _panelSidebar.Controls.Add(_btnNavModels);

            _btnNavNaming = ControlFactory.CreateNavButton("3. Model Properties", 2, false, ShowPanel);
            _btnNavNaming.Location = new Point(10, 170);
            _btnNavNaming.Size = new Size(180, 40);
            _btnNavNaming.Enabled = false;
            _panelSidebar.Controls.Add(_btnNavNaming);

            // Content panel - fill remaining space
            _panelContent = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.PanelBackground
            };

            InitializeConnectPanel();
            InitializeModelsPanel();
            InitializeNamingPanel();

            // Add content first, then sidebar (dock order matters)
            _tabMartProcesses.Controls.Add(_panelContent);
            _tabMartProcesses.Controls.Add(_panelSidebar);
        }

        private void InitializeStatusBar()
        {
            _panelStatus = new Panel
            {
                Location = new Point(0, ClientSize.Height - 35),
                Size = new Size(ClientSize.Width, 35),
                BackColor = Color.FromArgb(230, 230, 230),
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            _lblStatus = ControlFactory.CreateLabel("Ready", 10, 8, foreColor: AppTheme.TextSecondary);
            _panelStatus.Controls.Add(_lblStatus);

            _progressBar = ControlFactory.CreateProgressBar(ClientSize.Width - 150, 8, 130, 18);
            _panelStatus.Controls.Add(_progressBar);

            Controls.Add(_panelStatus);
        }

        private void InitializeConnectPanel()
        {
            _panelConnect = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.PanelBackground
            };

            _panelConnect.Controls.Add(ControlFactory.CreateTitle("Connect to Mart Server", 40, 25));
            _panelConnect.Controls.Add(ControlFactory.CreateLabel("Server:", 40, 80));

            _txtServerName = ControlFactory.CreateTextBox(130, 77, 200, "localhost");
            _panelConnect.Controls.Add(_txtServerName);

            _panelConnect.Controls.Add(ControlFactory.CreateLabel("Port:", 350, 80));
            _txtPort = ControlFactory.CreateTextBox(400, 77, 80, "18170");
            _panelConnect.Controls.Add(_txtPort);

            _panelConnect.Controls.Add(ControlFactory.CreateLabel("Username:", 40, 125));
            _txtUserName = ControlFactory.CreateTextBox(130, 122, 350, "Kursat");
            _panelConnect.Controls.Add(_txtUserName);

            _panelConnect.Controls.Add(ControlFactory.CreateLabel("Password:", 40, 170));
            _txtPassword = ControlFactory.CreateTextBox(130, 167, 350, "Elite12345..", isPassword: true);
            _panelConnect.Controls.Add(_txtPassword);

            _btnConnect = ControlFactory.CreateButton("Connect", 350, 220, 130, 40, onClick: OnConnectClick);
            _panelConnect.Controls.Add(_btnConnect);

            _lblConnectionStatus = ControlFactory.CreateLabel("", 40, 228, foreColor: AppTheme.TextSecondary);
            _panelConnect.Controls.Add(_lblConnectionStatus);

            _panelConnect.Controls.Add(ControlFactory.CreateSeparator(40, 280, 440));

            var lnkPortal = ControlFactory.CreateLink("Open Mart Portal in Browser", 40, 295, (s, e) =>
            {
                var url = $"http://{_txtServerName.Text}:{_txtPort.Text}/MartPortal/auth/login";
                try { System.Diagnostics.Process.Start(url); } catch { }
            });
            _panelConnect.Controls.Add(lnkPortal);

            _panelConnect.Controls.Add(ControlFactory.CreateLabel("Log:", 40, 340));
            _txtLog = ControlFactory.CreateLogTextBox(40, 365, 750, 280);
            _panelConnect.Controls.Add(_txtLog);

            _panelContent.Controls.Add(_panelConnect);
        }

        private void InitializeModelsPanel()
        {
            _panelModels = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.PanelBackground,
                Visible = false
            };

            _panelModels.Controls.Add(ControlFactory.CreateTitle("Load Model", 40, 25));

            // Catalog Tree Section (left side)
            _panelModels.Controls.Add(ControlFactory.CreateLabel("Mart Catalog:", 40, 70));
            _panelModels.Controls.Add(ControlFactory.CreateLabel("Double-click a model to load it", 40, 92, AppTheme.SmallFont, AppTheme.TextSecondary));

            _treeModels = ControlFactory.CreateTreeView(40, 115, 480, 450);
            _treeModels.Font = AppTheme.TreeFont;
            _treeModels.ItemHeight = 28;
            _treeModels.BeforeExpand += OnTreeModelExpand;
            _treeModels.NodeMouseDoubleClick += OnTreeModelDoubleClick;
            _panelModels.Controls.Add(_treeModels);

            var btnRefresh = ControlFactory.CreateButton("Refresh Catalog", 380, 575, 140, 35, ButtonStyle.Secondary, async (s, e) => await LoadCatalogAsync());
            _panelModels.Controls.Add(btnRefresh);

            // Right side - Load from file option only
            _panelModels.Controls.Add(ControlFactory.CreateLabel("Load from file:", 560, 115));

            _btnLoadFromFile = ControlFactory.CreateButton("Browse File...", 560, 145, 220, 40, ButtonStyle.Secondary, OnLoadFromFileClick);
            _panelModels.Controls.Add(_btnLoadFromFile);

            _panelContent.Controls.Add(_panelModels);
        }

        private void InitializeNamingPanel()
        {
            _panelNaming = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.PanelBackground,
                Visible = false
            };

            _panelNaming.Controls.Add(ControlFactory.CreateTitle("Model Properties", 40, 25));

            // Model name subtitle
            _lblModelSubtitle = new Label
            {
                Location = new Point(42, 55),
                Size = new Size(700, 20),
                Font = new Font(AppTheme.DefaultFont.FontFamily, 10f, FontStyle.Italic),
                ForeColor = AppTheme.TextSecondary,
                Text = ""
            };
            _panelNaming.Controls.Add(_lblModelSubtitle);

            // Create TabControl with classic style
            _tabModelProps = new TabControl
            {
                Location = new Point(40, 80),
                Size = new Size(760, 530),
                Font = AppTheme.DefaultFont
            };

            // General Tab
            var tabGeneral = new TabPage("General")
            {
                BackColor = AppTheme.PanelBackground,
                Padding = new Padding(15),
                UseVisualStyleBackColor = true
            };
            InitializeGeneralTab(tabGeneral);
            _tabModelProps.TabPages.Add(tabGeneral);

            // UDP Tab
            var tabUdp = new TabPage("UDP")
            {
                BackColor = AppTheme.PanelBackground,
                Padding = new Padding(15),
                UseVisualStyleBackColor = true
            };
            InitializeUdpTab(tabUdp);
            _tabModelProps.TabPages.Add(tabUdp);

            // Naming Conventions Tab
            var tabNaming = new TabPage("Naming Conventions")
            {
                BackColor = AppTheme.PanelBackground,
                Padding = new Padding(15),
                UseVisualStyleBackColor = true
            };
            InitializeNamingConventionsTab(tabNaming);
            _tabModelProps.TabPages.Add(tabNaming);

            _panelNaming.Controls.Add(_tabModelProps);
            _panelContent.Controls.Add(_panelNaming);
        }

        private void InitializeGeneralTab(TabPage tab)
        {
            var lblModelName = ControlFactory.CreateLabel("Model Name:", 20, 25);
            tab.Controls.Add(lblModelName);

            _txtModelName = ControlFactory.CreateTextBox(130, 22, 400, "");
            _txtModelName.ReadOnly = true;
            _txtModelName.Font = AppTheme.MonoFont;
            tab.Controls.Add(_txtModelName);
        }

        private void InitializeUdpTab(TabPage tab)
        {
            var lblUdpTitle = ControlFactory.CreateLabel("Model UDP Values:", 20, 15);
            tab.Controls.Add(lblUdpTitle);

            _listUdps = new ListView
            {
                Location = new Point(20, 45),
                Size = new Size(700, 420),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = AppTheme.InputBackground,
                ForeColor = AppTheme.TextPrimary,
                Font = AppTheme.DefaultFont,
                BorderStyle = BorderStyle.FixedSingle
            };

            _listUdps.Columns.Add("UDP Name", 250);
            _listUdps.Columns.Add("Value", 440);

            tab.Controls.Add(_listUdps);
        }

        private void InitializeNamingConventionsTab(TabPage tab)
        {
            var lblTitle = ControlFactory.CreateLabel("Naming Standards:", 20, 15);
            tab.Controls.Add(lblTitle);

            _listNamingStandards = new ListView
            {
                Location = new Point(20, 45),
                Size = new Size(700, 420),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = AppTheme.InputBackground,
                ForeColor = AppTheme.TextPrimary,
                Font = AppTheme.DefaultFont,
                BorderStyle = BorderStyle.FixedSingle
            };

            _listNamingStandards.Columns.Add("Name", 250);
            _listNamingStandards.Columns.Add("Type", 150);
            _listNamingStandards.Columns.Add("Description", 290);

            tab.Controls.Add(_listNamingStandards);
        }

        private void InitializeLoadingOverlay()
        {
            _panelLoading = new Panel
            {
                Size = new Size(300, 120),
                BackColor = Color.FromArgb(240, 250, 250, 250),
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };

            // Center in form
            _panelLoading.Location = new Point(
                (ClientSize.Width - _panelLoading.Width) / 2,
                (ClientSize.Height - _panelLoading.Height) / 2
            );

            // Spinning indicator using a simple animated label
            var lblSpinner = new Label
            {
                Text = "⏳",
                Font = new Font("Segoe UI", 28F),
                ForeColor = AppTheme.Accent,
                AutoSize = false,
                Size = new Size(60, 50),
                Location = new Point(120, 15),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _panelLoading.Controls.Add(lblSpinner);

            _lblLoadingText = new Label
            {
                Text = "Loading model...",
                Font = AppTheme.SubtitleFont,
                ForeColor = AppTheme.TextPrimary,
                AutoSize = false,
                Size = new Size(280, 30),
                Location = new Point(10, 70),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _panelLoading.Controls.Add(_lblLoadingText);

            // Add animation timer
            var timer = new Timer { Interval = 150 };
            var spinChars = new[] { "⏳", "⌛" };
            var spinIndex = 0;
            timer.Tick += (s, e) =>
            {
                if (_panelLoading.Visible)
                {
                    spinIndex = (spinIndex + 1) % spinChars.Length;
                    lblSpinner.Text = spinChars[spinIndex];
                }
            };
            timer.Start();

            Controls.Add(_panelLoading);
            _panelLoading.BringToFront();
        }

        private void ShowLoading(string message = "Loading model...")
        {
            _lblLoadingText.Text = message;
            _panelLoading.Visible = true;
            _panelLoading.BringToFront();
            Application.DoEvents();
        }

        private void HideLoading()
        {
            _panelLoading.Visible = false;
        }

        private void CheckScapiStatus()
        {
            if (!_scapiService.IsInitialized)
            {
                _btnConnect.Enabled = false;
                SetStatus(_scapiService.InitializationError ?? "SCAPI not available");
            }
        }

        #endregion

        #region Navigation

        private void ShowPanel(int index)
        {
            if (index == 1 && !_martApi.IsAuthenticated) return;
            if (index == 2 && _scapiService.CurrentModel == null) return;

            _currentPanel = index;
            _panelConnect.Visible = index == 0;
            _panelModels.Visible = index == 1;
            _panelNaming.Visible = index == 2;

            // Load model properties when switching to Model Properties panel
            if (index == 2)
            {
                LoadModelProperties();
            }

            UpdateNavButtons(index);
        }

        private void LoadModelProperties()
        {
            Log("[Info] Loading model properties...");

            // Load model name
            var modelName = _scapiService.GetModelName();
            _txtModelName.Text = modelName ?? "(Unknown)";
            Log($"[Info] Model name: {modelName ?? "(null)"}");

            // Update subtitle with path and model name
            if (!string.IsNullOrEmpty(_currentModelPath) && !string.IsNullOrEmpty(modelName))
            {
                _lblModelSubtitle.Text = $"{_currentModelPath}  →  {modelName}";
            }
            else if (!string.IsNullOrEmpty(modelName))
            {
                _lblModelSubtitle.Text = modelName;
            }
            else
            {
                _lblModelSubtitle.Text = _currentModelPath;
            }

            // Check if model is loaded
            if (_scapiService.CurrentModel == null)
            {
                Log("[Warning] CurrentModel is null!");
                SetStatus("No model loaded");
                _lblModelSubtitle.Text = "";
                return;
            }

            // Load Model UDP values with logging
            _listUdps.Items.Clear();
            Log("[Info] Getting Model UDP values...");
            var udpValues = _scapiService.GetModelUdpValues(Log);
            Log($"[Info] Got {udpValues.Count} Model UDP values");

            foreach (var udp in udpValues)
            {
                var item = new ListViewItem(udp.Name);
                item.SubItems.Add(udp.Value);
                _listUdps.Items.Add(item);
                Log($"[Debug] UDP: {udp.Name} = {udp.Value}");
            }

            // Load Naming Standards
            _listNamingStandards.Items.Clear();
            Log("[Info] Getting Naming Standards...");
            var namingStandards = _scapiService.GetNamingStandards(Log);
            Log($"[Info] Got {namingStandards.Count} Naming Standards");

            foreach (var ns in namingStandards)
            {
                var item = new ListViewItem(ns.Name);
                item.SubItems.Add(ns.ObjectType);
                item.SubItems.Add(ns.Description);
                _listNamingStandards.Items.Add(item);
            }

            SetStatus($"Model properties loaded - {udpValues.Count} UDPs, {namingStandards.Count} Naming Standards");
        }

        private void UpdateNavButtons(int activeIndex)
        {
            // Update button colors for active/inactive state
            UpdateNavButtonStyle(_btnNavConnect, activeIndex == 0);
            UpdateNavButtonStyle(_btnNavModels, activeIndex == 1);
            UpdateNavButtonStyle(_btnNavNaming, activeIndex == 2);
        }

        private void UpdateNavButtonStyle(Button btn, bool isActive)
        {
            if (isActive)
            {
                btn.BackColor = AppTheme.Accent;
                btn.ForeColor = Color.White;
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderSize = 0;
            }
            else
            {
                btn.BackColor = Color.FromArgb(230, 230, 230);
                btn.ForeColor = AppTheme.TextPrimary;
                btn.FlatStyle = FlatStyle.Standard;
                btn.UseVisualStyleBackColor = true;
            }
        }

        #endregion

        #region Event Handlers

        private async void OnConnectClick(object sender, EventArgs e)
        {
            if (!ValidateConnectionInputs()) return;

            _btnConnect.Enabled = false;
            SetStatus("Connecting to Mart...", showProgress: true);
            _lblConnectionStatus.Text = "";

            try
            {
                var connectionInfo = new MartConnectionInfo
                {
                    ServerName = _txtServerName.Text.Trim(),
                    Port = int.Parse(_txtPort.Text),
                    Username = _txtUserName.Text.Trim(),
                    Password = _txtPassword.Text
                };

                var result = await _martApi.AuthenticateAsync(connectionInfo);

                if (result.Success)
                {
                    _lblConnectionStatus.Text = "Connected";
                    _lblConnectionStatus.ForeColor = AppTheme.Success;
                    _btnNavModels.Enabled = true;
                    SetStatus($"Connected to {connectionInfo.ServerName}:{connectionInfo.Port}");

                    ShowPanel(1);
                    await LoadCatalogAsync();
                }
                else
                {
                    _lblConnectionStatus.Text = result.ErrorMessage ?? "Authentication failed";
                    _lblConnectionStatus.ForeColor = AppTheme.Error;
                    SetStatus("Connection failed");
                }
            }
            catch (Exception ex)
            {
                _lblConnectionStatus.Text = $"Error: {ex.Message}";
                _lblConnectionStatus.ForeColor = AppTheme.Error;
                SetStatus("Connection error");
            }
            finally
            {
                _btnConnect.Enabled = true;
                _progressBar.Visible = false;
            }
        }

        private void OnLoadFromFileClick(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Select erwin Model File";
                ofd.Filter = "erwin Model Files (*.erwin;*.erwm)|*.erwin;*.erwm|All Files (*.*)|*.*";

                if (ofd.ShowDialog() != DialogResult.OK) return;

                ShowLoading($"Loading {Path.GetFileName(ofd.FileName)}...");
                SetStatus("Loading model...", showProgress: true);

                try
                {
                    var result = _scapiService.LoadFromFile(ofd.FileName);

                    if (result.Success)
                    {
                        _btnNavNaming.Enabled = true;
                        SetStatus($"Model loaded: {Path.GetFileName(ofd.FileName)}");
                        MessageBox.Show("Model loaded successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        SetStatus("Model load failed");
                        var message = result.HResult.HasValue
                            ? $"Failed to load model.\n\nHRESULT: 0x{result.HResult:X8}\n{result.ErrorMessage}"
                            : $"Error: {result.ErrorMessage}";
                        MessageBox.Show(message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                finally
                {
                    HideLoading();
                    _progressBar.Visible = false;
                }
            }
        }

        private void OnTreeModelDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node?.Tag == null) return;

            var nodeTag = e.Node.Tag.ToString() ?? "";
            Log($"[Debug] Double-click on: {e.Node.Text}, Tag: {nodeTag}");

            var parts = nodeTag.Split(new[] { ':' }, 3);

            // Only load if it's a MODEL
            if (parts.Length >= 3 && parts[0] == "MODEL")
            {
                var modelPath = parts[2];
                Log($"[Info] Loading model: {modelPath}");
                LoadModelFromMart(modelPath);
            }
            else
            {
                Log($"[Debug] Not a model node, type: {(parts.Length > 0 ? parts[0] : "unknown")}");
            }
        }

        private async void OnTreeModelExpand(object sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;
            if (node == null) return;

            var nodeTag = node.Tag?.ToString() ?? "";

            if (node.Nodes.Count == 1 && node.Nodes[0].Tag?.ToString() == "PLACEHOLDER")
            {
                var parts = nodeTag.Split(':');
                if (parts.Length >= 2)
                {
                    node.Nodes[0].Text = "Loading...";
                    // parts[0] is node type (Library, Category, MODEL, etc.)
                    // parts[1] is the entry ID from REST API
                    // parts[2] is the path (for model selection)
                    var entryId = parts[1];
                    Log($"[Debug] Expanding node: {node.Text}, EntryID: {entryId}");
                    await LoadCatalogChildrenAsync(node, entryId);
                }
            }
        }

        private void OnApiLogMessage(object sender, LogEventArgs e)
        {
            Log($"[{e.Level}] {e.Message}");
        }

        private void OnAuthenticationStateChanged(object sender, AuthenticationStateChangedEventArgs e)
        {
            if (!e.IsAuthenticated)
            {
                _btnNavModels.Enabled = false;
            }
        }

        #endregion

        #region Helper Methods

        private void SetStatus(string text, bool showProgress = false)
        {
            _lblStatus.Text = text;
            _progressBar.Visible = showProgress;
            Application.DoEvents();
        }

        private void Log(string message)
        {
            if (_txtLog == null || _txtLog.IsDisposed) return;

            if (_txtLog.InvokeRequired)
            {
                _txtLog.Invoke(new Action(() => Log(message)));
                return;
            }

            _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            _txtLog.SelectionStart = _txtLog.Text.Length;
            _txtLog.ScrollToCaret();
        }

        private bool ValidateConnectionInputs()
        {
            if (string.IsNullOrWhiteSpace(_txtServerName.Text))
            {
                ShowValidationError("Server name is required.", _txtServerName);
                return false;
            }

            if (!int.TryParse(_txtPort.Text, out var port) || port < 1 || port > 65535)
            {
                ShowValidationError("Port must be a valid number (1-65535).", _txtPort);
                return false;
            }

            if (string.IsNullOrWhiteSpace(_txtUserName.Text))
            {
                ShowValidationError("Username is required.", _txtUserName);
                return false;
            }

            if (string.IsNullOrWhiteSpace(_txtPassword.Text))
            {
                ShowValidationError("Password is required.", _txtPassword);
                return false;
            }

            return true;
        }

        private static void ShowValidationError(string message, Control control)
        {
            MessageBox.Show(message, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            control.Focus();
        }

        private void LoadModelFromMart(string modelPath)
        {
            ShowLoading($"Loading {modelPath}...");
            SetStatus("Loading model...", showProgress: true);

            try
            {
                var connectionInfo = new MartConnectionInfo
                {
                    ServerName = _txtServerName.Text.Trim(),
                    Port = int.Parse(_txtPort.Text),
                    Username = _txtUserName.Text.Trim(),
                    Password = _txtPassword.Text
                };

                var result = _scapiService.LoadFromMart(connectionInfo, modelPath);

                if (result.Success)
                {
                    _currentModelPath = modelPath;
                    _btnNavNaming.Enabled = true;
                    SetStatus($"Model loaded: {modelPath}");
                    MessageBox.Show($"Model '{modelPath}' loaded successfully!", "Success",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    SetStatus("Model load failed");
                    var message = result.HResult.HasValue
                        ? $"Failed to load model.\n\nHRESULT: 0x{result.HResult:X8}\n{result.ErrorMessage}"
                        : $"Error: {result.ErrorMessage}";
                    MessageBox.Show(message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                HideLoading();
                _progressBar.Visible = false;
            }
        }

        private async Task LoadCatalogAsync()
        {
            _treeModels.Nodes.Clear();
            var rootNode = new TreeNode("Loading...") { Tag = "ROOT" };
            _treeModels.Nodes.Add(rootNode);

            // First try REST API for catalog
            SetStatus("Loading catalog via REST API...", showProgress: true);
            Log("[Info] Attempting to load catalog via REST API...");

            try
            {
                var catalogResult = await _martApi.GetCatalogChildrenAsync(null);

                if (catalogResult.Success && catalogResult.Entries.Count > 0)
                {
                    Log($"[Info] REST API returned {catalogResult.Entries.Count} entries");
                    await LoadCatalogFromRestAsync(rootNode, catalogResult.Entries);
                    return;
                }
                else
                {
                    Log($"[Warning] REST API catalog failed or empty: {catalogResult.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Log($"[Warning] REST API catalog error: {ex.Message}");
            }

            // Fall back to SCAPI
            SetStatus("Loading catalog via SCAPI...", showProgress: true);
            Log("[Info] Falling back to SCAPI for catalog...");

            try
            {
                // Connect to Mart using SCAPI catalog service
                var connectionInfo = new MartConnectionInfo
                {
                    ServerName = _txtServerName.Text.Trim(),
                    Port = int.Parse(_txtPort.Text),
                    Username = _txtUserName.Text.Trim(),
                    Password = _txtPassword.Text
                };

                var connected = await Task.Run(() => _catalogService.Connect(connectionInfo));

                if (!connected)
                {
                    rootNode.Text = "Catalog Unavailable";
                    rootNode.Nodes.Add(new TreeNode($"Error: {_catalogService.LastError}") { ForeColor = AppTheme.Error });
                    rootNode.Nodes.Add(new TreeNode("Enter model path manually: Library/ModelName") { ForeColor = AppTheme.TextSecondary });
                    SetStatus("Catalog unavailable - enter path manually");
                    Log($"[Warning] SCAPI Catalog error: {_catalogService.LastError}");
                    return;
                }

                // Get root catalog (libraries)
                var libraries = await Task.Run(() => _catalogService.GetRootCatalog());

                rootNode.Text = "Mart";
                rootNode.Tag = "MART:root";

                foreach (var lib in libraries)
                {
                    var libNode = new TreeNode(lib.Name)
                    {
                        Tag = $"Library:{lib.Locator}:{lib.Name}",
                        ForeColor = AppTheme.GetCatalogEntryColor("Library")
                    };

                    if (lib.HasChildren)
                    {
                        libNode.Nodes.Add(new TreeNode("Loading...") { Tag = "PLACEHOLDER" });
                    }

                    rootNode.Nodes.Add(libNode);
                }

                if (rootNode.Nodes.Count == 0)
                {
                    rootNode.Nodes.Add(new TreeNode("(No libraries found)") { ForeColor = AppTheme.TextSecondary });
                }

                rootNode.Expand();
                SetStatus($"Catalog loaded - {libraries.Count} libraries found");
            }
            catch (Exception ex)
            {
                rootNode.Text = "Mart (Error)";
                rootNode.Nodes.Add(new TreeNode($"Error: {ex.Message}") { ForeColor = AppTheme.Error });
                SetStatus($"Catalog error: {ex.Message}");
            }
            finally
            {
                _progressBar.Visible = false;
            }
        }

        private async Task LoadCatalogFromRestAsync(TreeNode rootNode, List<CatalogEntry> entries)
        {
            rootNode.Text = "Mart";
            rootNode.Tag = "MART:root";

            // The root API call returns "Mart" entry (id=1) which is the mart root
            // We should skip this and load its children directly to avoid "Mart > Mart" duplication
            if (entries.Count == 1 && entries[0].EntryName == "Mart" && entries[0].HasChildren)
            {
                // Load children of the Mart root entry directly
                var martRootId = entries[0].EntryId;
                Log($"[Info] Skipping Mart root entry, loading children of ID {martRootId}");

                var childResult = await _martApi.GetCatalogChildrenAsync(martRootId);
                if (childResult.Success)
                {
                    entries = childResult.Entries;
                    Log($"[Info] Loaded {entries.Count} libraries from Mart root");
                }
            }

            foreach (var entry in entries)
            {
                var nodeType = entry.Type.ToString();

                // Map to display type
                if (entry.Type == CatalogEntryType.Library)
                    nodeType = "Library";
                else if (entry.Type == CatalogEntryType.Model)
                    nodeType = "MODEL";

                var node = new TreeNode(entry.EntryName)
                {
                    Tag = $"{nodeType}:{entry.EntryId}:{entry.EntryName}",
                    ForeColor = AppTheme.GetCatalogEntryColor(nodeType)
                };

                if (entry.HasChildren)
                {
                    node.Nodes.Add(new TreeNode("Loading...") { Tag = "PLACEHOLDER" });
                }

                rootNode.Nodes.Add(node);
            }

            if (rootNode.Nodes.Count == 0)
            {
                rootNode.Nodes.Add(new TreeNode("(No entries found)") { ForeColor = AppTheme.TextSecondary });
            }

            rootNode.Expand();
            SetStatus($"Catalog loaded via REST API - {entries.Count} libraries found");
            _progressBar.Visible = false;
        }

        private async Task LoadCatalogChildrenAsync(TreeNode parentNode, string parentId)
        {
            try
            {
                // Use REST API to get children (not SCAPI)
                Log($"[Debug] Getting children for parent ID: {parentId}");
                var result = await _martApi.GetCatalogChildrenAsync(parentId);

                // Update UI on UI thread
                if (_treeModels.InvokeRequired)
                {
                    _treeModels.Invoke(new Action(() => UpdateTreeChildren(parentNode, result)));
                }
                else
                {
                    UpdateTreeChildren(parentNode, result);
                }
            }
            catch (Exception ex)
            {
                Log($"[Error] Children load error: {ex.Message}");
                if (_treeModels.InvokeRequired)
                {
                    _treeModels.Invoke(new Action(() =>
                    {
                        parentNode.Nodes.Clear();
                        parentNode.Nodes.Add(new TreeNode($"(Error: {ex.Message})") { ForeColor = AppTheme.Error });
                    }));
                }
                else
                {
                    parentNode.Nodes.Clear();
                    parentNode.Nodes.Add(new TreeNode($"(Error: {ex.Message})") { ForeColor = AppTheme.Error });
                }
            }
        }

        private void UpdateTreeChildren(TreeNode parentNode, CatalogResult result)
        {
            parentNode.Nodes.Clear();

            if (!result.Success)
            {
                Log($"[Warning] REST API failed for children: {result.ErrorMessage}");
                parentNode.Nodes.Add(new TreeNode($"(Error: {result.ErrorMessage})") { ForeColor = AppTheme.Error });
                return;
            }

            Log($"[Info] REST API returned {result.Entries.Count} children for parent");

            foreach (var entry in result.Entries)
            {
                // Skip version entries (V) - we show ModelGroups (D) as loadable models instead
                if (entry.Type == CatalogEntryType.Model)
                {
                    Log($"[Debug] Skipping version entry: {entry.EntryName}");
                    continue;
                }

                // Map entry type to display type
                // ModelGroup (D) = loadable model, shown as MODEL
                // Library (L), Category (C) = containers
                string nodeType;
                switch (entry.Type)
                {
                    case CatalogEntryType.ModelGroup:
                        nodeType = "MODEL"; // ModelGroup is the actual loadable model
                        break;
                    case CatalogEntryType.Library:
                        nodeType = "Library";
                        break;
                    case CatalogEntryType.Category:
                        nodeType = "Category";
                        break;
                    default:
                        nodeType = "Category";
                        break;
                }

                var path = BuildPath(parentNode, entry.EntryName);
                Log($"[Debug] Entry: {entry.EntryName}, RawType: {entry.EntryType}, Type: {entry.Type}, NodeType: {nodeType}, Path: {path}");

                var node = new TreeNode(entry.EntryName)
                {
                    Tag = $"{nodeType}:{entry.EntryId}:{path}",
                    ForeColor = AppTheme.GetCatalogEntryColor(nodeType)
                };

                parentNode.Nodes.Add(node);

                // Only add placeholder for Library and Category (not ModelGroup - it's a leaf now)
                if (entry.HasChildren && entry.Type != CatalogEntryType.ModelGroup)
                {
                    node.Nodes.Add(new TreeNode("Loading...") { Tag = "PLACEHOLDER" });
                }
            }

            if (parentNode.Nodes.Count == 0)
            {
                // No children found - show empty
                parentNode.Nodes.Add(new TreeNode("(empty)") { ForeColor = AppTheme.TextSecondary });
            }
        }

        private static string BuildPath(TreeNode parentNode, string currentName)
        {
            var parts = new System.Collections.Generic.List<string> { currentName };
            var current = parentNode;

            while (current != null)
            {
                var tag = current.Tag?.ToString() ?? "";
                if (tag.StartsWith("Library:") || tag.StartsWith("Category:"))
                {
                    var tagParts = tag.Split(':');
                    if (tagParts.Length >= 3)
                    {
                        var pathParts = tagParts[2].Split('/');
                        parts.Insert(0, pathParts[pathParts.Length - 1]);
                    }
                    else
                    {
                        parts.Insert(0, current.Text);
                    }
                }
                current = current.Parent;
            }

            // Remove duplicates
            var cleanParts = new System.Collections.Generic.List<string>();
            foreach (var part in parts)
            {
                if (!string.IsNullOrEmpty(part) && (cleanParts.Count == 0 || cleanParts[cleanParts.Count - 1] != part))
                {
                    cleanParts.Add(part);
                }
            }

            return string.Join("/", cleanParts);
        }

        #endregion

        #region Cleanup

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            _martApi.Dispose();
            _scapiService.Dispose();
            _catalogService.Dispose();
        }

        #endregion
    }
}
