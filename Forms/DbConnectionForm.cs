using System;
using System.Drawing;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Database connection configuration dialog for Reverse Engineering.
    /// Builds erwin SCAPI RE connection string from user inputs.
    /// </summary>
    public class DbConnectionForm : Form
    {
        // Public results
        public string ConnectionString { get; private set; } = "";
        public string Password { get; private set; } = "";
        public string DisplayLabel { get; private set; } = "";
        public string SchemaFilter { get; private set; } = "";
        public long TargetServerCode { get; private set; }
        public int TargetServerVersion { get; private set; }

        // Raw fields used by the in-process RE pipeline (DSN is created at runtime).
        public string ServerHost { get; private set; } = "";
        public string DatabaseName { get; private set; } = "";
        public string UserName { get; private set; } = "";
        /// <summary>True if the user picked Windows Authentication.</summary>
        public bool UseWindowsAuth { get; private set; }
        /// <summary>The DB type code shown in connStr's SERVER=&lt;Code&gt;:... e.g. 16 for SQL Server.</summary>
        public int DbTypeCode { get; private set; }

        // DB type mapping: display name, RE conn code, default major version, erwin Target_Server constant.
        // MajorVer must be a value the installed erwin accepts at PersistenceUnits.Create() time —
        // doc samples show older numbers but our r10.10 install rejects them with "Key Target_Server
        // has wrong value". Verified working: SQL Server v15. Adjust per DBMS as needed.
        private static readonly (string Name, int Code, int MajorVer, long TargetServer)[] DbTypes =
        {
            ("SQL Server", 16, 15, 1075859016),
            ("PostgreSQL", 35, 16, 1075859493),
            ("Oracle", 10, 12, 1075859011),
            ("DB2", 2, 11, 1075859009),
            ("SQL Azure", 18, 15, 1075859016),
            ("MySQL", 8, 8, 1075859030),
            ("Snowflake", 21, 1, 1075859495),
        };

        // Controls
        private ComboBox cmbDbType;
        private RadioButton rbNative;
        private RadioButton rbOdbc;
        private Panel pnlNative;
        private TextBox txtServer;
        private TextBox txtDatabase;
        private Panel pnlOdbc;
        private TextBox txtDsnName;
        private RadioButton rbWinAuth;
        private RadioButton rbSqlAuth;
        private TextBox txtUsername;
        private TextBox txtPassword;
        private TextBox txtSchemaFilter;
        private Label lblTestResult;
        private Button btnOK;
        private Button btnCancel;

        // Persist last used values across dialog opens
        private static int _lastDbTypeIndex = 0;
        private static bool _lastIsNative = true;
        private static string _lastServer = "localhost";
        private static string _lastDatabase = "MetaRepo";
        private static string _lastDsnName = "";
        private static bool _lastIsSqlAuth = true;
        private static string _lastUsername = "sa";
        private static string _lastPassword = "Elite12345";
        private static string _lastSchemaFilter = "";

        public DbConnectionForm()
        {
            InitializeComponent();
            LoadLastSettings();
        }

        private void LoadLastSettings()
        {
            cmbDbType.SelectedIndex = _lastDbTypeIndex;
            rbNative.Checked = _lastIsNative;
            rbOdbc.Checked = !_lastIsNative;
            txtServer.Text = _lastServer;
            txtDatabase.Text = _lastDatabase;
            txtDsnName.Text = _lastDsnName;
            rbSqlAuth.Checked = _lastIsSqlAuth;
            rbWinAuth.Checked = !_lastIsSqlAuth;
            txtUsername.Text = _lastUsername;
            txtPassword.Text = _lastPassword;
            txtSchemaFilter.Text = _lastSchemaFilter;
            // Force panel visibility update
            pnlNative.Visible = _lastIsNative;
            pnlOdbc.Visible = !_lastIsNative;
            txtUsername.Enabled = _lastIsSqlAuth;
            txtPassword.Enabled = _lastIsSqlAuth;
        }

        private void SaveLastSettings()
        {
            _lastDbTypeIndex = cmbDbType.SelectedIndex;
            _lastIsNative = rbNative.Checked;
            _lastServer = txtServer.Text;
            _lastDatabase = txtDatabase.Text;
            _lastDsnName = txtDsnName.Text;
            _lastIsSqlAuth = rbSqlAuth.Checked;
            _lastUsername = txtUsername.Text;
            _lastPassword = txtPassword.Text;
            _lastSchemaFilter = txtSchemaFilter.Text;
        }

        private void InitializeComponent()
        {
            this.Text = "Database Connection";
            this.Size = new Size(480, 460);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.ShowInTaskbar = false;
            this.Font = new Font("Segoe UI", 9f);
            this.BackColor = Color.White;

            int y = 15;
            int lblX = 20;
            int ctrlX = 150;
            int ctrlW = 290;

            // Database Type
            AddLabel("Database Type:", lblX, y + 3);
            cmbDbType = new ComboBox
            {
                Location = new Point(ctrlX, y),
                Size = new Size(ctrlW, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var db in DbTypes)
                cmbDbType.Items.Add(db.Name);
            cmbDbType.SelectedIndex = 0;
            this.Controls.Add(cmbDbType);

            y += 35;

            // Connection Mode (in own panel to isolate radio group)
            AddLabel("Connection:", lblX, y + 3);
            var pnlConnMode = new Panel
            {
                Location = new Point(ctrlX, y),
                Size = new Size(250, 22)
            };
            rbNative = new RadioButton
            {
                Text = "Native",
                Location = new Point(0, 0),
                Size = new Size(80, 20),
                Checked = true
            };
            rbOdbc = new RadioButton
            {
                Text = "ODBC DSN",
                Location = new Point(90, 0),
                Size = new Size(100, 20)
            };
            rbNative.CheckedChanged += (s, e) => UpdateConnectionMode();
            pnlConnMode.Controls.Add(rbNative);
            pnlConnMode.Controls.Add(rbOdbc);
            this.Controls.Add(pnlConnMode);

            y += 30;

            // Native connection panel
            pnlNative = new Panel
            {
                Location = new Point(0, y),
                Size = new Size(this.ClientSize.Width, 65),
            };

            AddLabel("Server:", lblX, 3, pnlNative);
            txtServer = new TextBox
            {
                Location = new Point(ctrlX, 0),
                Size = new Size(ctrlW, 22),
                PlaceholderText = "localhost\\SQLEXPRESS"
            };
            pnlNative.Controls.Add(txtServer);

            AddLabel("Database:", lblX, 33, pnlNative);
            txtDatabase = new TextBox
            {
                Location = new Point(ctrlX, 30),
                Size = new Size(ctrlW, 22),
                PlaceholderText = "MyDatabase"
            };
            pnlNative.Controls.Add(txtDatabase);
            this.Controls.Add(pnlNative);

            // ODBC DSN panel (hidden by default)
            pnlOdbc = new Panel
            {
                Location = new Point(0, y),
                Size = new Size(this.ClientSize.Width, 35),
                Visible = false
            };

            AddLabel("DSN Name:", lblX, 3, pnlOdbc);
            txtDsnName = new TextBox
            {
                Location = new Point(ctrlX, 0),
                Size = new Size(ctrlW, 22),
                PlaceholderText = "MyOdbcDsn"
            };
            pnlOdbc.Controls.Add(txtDsnName);
            this.Controls.Add(pnlOdbc);

            y += 75;

            // Separator
            var sep = new Label
            {
                BorderStyle = BorderStyle.Fixed3D,
                Location = new Point(20, y),
                Size = new Size(420, 2)
            };
            this.Controls.Add(sep);

            y += 12;

            // Authentication (in own panel to isolate radio group)
            AddLabel("Authentication:", lblX, y + 3);
            var pnlAuthMode = new Panel
            {
                Location = new Point(ctrlX, y),
                Size = new Size(250, 22)
            };
            rbWinAuth = new RadioButton
            {
                Text = "Windows",
                Location = new Point(0, 0),
                Size = new Size(85, 20),
                Checked = true
            };
            rbSqlAuth = new RadioButton
            {
                Text = "SQL Auth",
                Location = new Point(90, 0),
                Size = new Size(100, 20)
            };
            rbSqlAuth.CheckedChanged += (s, e) => UpdateAuthMode();
            pnlAuthMode.Controls.Add(rbWinAuth);
            pnlAuthMode.Controls.Add(rbSqlAuth);
            this.Controls.Add(pnlAuthMode);

            y += 30;

            // Username
            AddLabel("Username:", lblX, y + 3);
            txtUsername = new TextBox
            {
                Location = new Point(ctrlX, y),
                Size = new Size(ctrlW, 22),
                Enabled = false
            };
            this.Controls.Add(txtUsername);

            y += 30;

            // Password
            AddLabel("Password:", lblX, y + 3);
            txtPassword = new TextBox
            {
                Location = new Point(ctrlX, y),
                Size = new Size(ctrlW, 22),
                PasswordChar = '*',
                Enabled = false
            };
            this.Controls.Add(txtPassword);

            y += 38;

            // Separator
            var sep2 = new Label
            {
                BorderStyle = BorderStyle.Fixed3D,
                Location = new Point(20, y),
                Size = new Size(420, 2)
            };
            this.Controls.Add(sep2);

            y += 12;

            // Schema filter
            AddLabel("Schema Filter:", lblX, y + 3);
            txtSchemaFilter = new TextBox
            {
                Location = new Point(ctrlX, y),
                Size = new Size(ctrlW, 22),
                PlaceholderText = "dbo (optional, comma-separated)"
            };
            this.Controls.Add(txtSchemaFilter);

            y += 35;

            // Test Connection button + result label
            var btnTest = new Button
            {
                Text = "Test Connection",
                Location = new Point(lblX, y),
                Size = new Size(120, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(245, 247, 250),
                FlatAppearance = { BorderColor = Color.FromArgb(208, 208, 208) },
                Cursor = Cursors.Hand
            };
            btnTest.Click += BtnTest_Click;
            this.Controls.Add(btnTest);

            lblTestResult = new Label
            {
                Location = new Point(ctrlX, y + 5),
                Size = new Size(ctrlW, 20),
                Text = "",
                ForeColor = Color.Gray
            };
            this.Controls.Add(lblTestResult);

            y += 40;

            // Buttons
            btnOK = new Button
            {
                Text = "OK",
                Location = new Point(this.ClientSize.Width - 180, y),
                Size = new Size(75, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnOK.Click += BtnOK_Click;
            this.Controls.Add(btnOK);

            btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(this.ClientSize.Width - 95, y),
                Size = new Size(75, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                FlatAppearance = { BorderColor = Color.FromArgb(208, 208, 208) }
            };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }

        private void AddLabel(string text, int x, int y, Control parent = null)
        {
            var lbl = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(51, 51, 51)
            };
            (parent ?? this).Controls.Add(lbl);
        }

        private void UpdateConnectionMode()
        {
            pnlNative.Visible = rbNative.Checked;
            pnlOdbc.Visible = !rbNative.Checked;
        }

        private void UpdateAuthMode()
        {
            txtUsername.Enabled = rbSqlAuth.Checked;
            txtPassword.Enabled = rbSqlAuth.Checked;
            if (rbWinAuth.Checked)
            {
                txtUsername.Text = "";
                txtPassword.Text = "";
            }
        }

        private void BtnTest_Click(object sender, EventArgs e)
        {
            lblTestResult.Text = "Testing...";
            lblTestResult.ForeColor = Color.Gray;
            Application.DoEvents();

            try
            {
                string odbcConnStr = BuildOdbcConnectionString();
                if (string.IsNullOrEmpty(odbcConnStr))
                {
                    lblTestResult.Text = "Fill in connection fields first.";
                    lblTestResult.ForeColor = Color.OrangeRed;
                    return;
                }

                using (var conn = new System.Data.Odbc.OdbcConnection(odbcConnStr))
                {
                    conn.Open();
                    lblTestResult.Text = "Connection successful!";
                    lblTestResult.ForeColor = Color.FromArgb(0, 138, 62);
                }
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (msg.Length > 60) msg = msg.Substring(0, 60) + "...";
                lblTestResult.Text = msg;
                lblTestResult.ForeColor = Color.Red;
            }
        }

        /// <summary>
        /// Builds a standard ODBC connection string from the form fields for testing.
        /// </summary>
        private string BuildOdbcConnectionString()
        {
            var dbType = DbTypes[cmbDbType.SelectedIndex];

            if (rbOdbc.Checked)
            {
                if (string.IsNullOrWhiteSpace(txtDsnName.Text)) return null;
                string dsn = $"DSN={txtDsnName.Text.Trim()}";
                if (rbSqlAuth.Checked && !string.IsNullOrWhiteSpace(txtUsername.Text))
                    dsn += $";UID={txtUsername.Text.Trim()};PWD={txtPassword.Text}";
                return dsn;
            }

            // Native connection - build ODBC driver string based on DB type
            if (string.IsNullOrWhiteSpace(txtServer.Text)) return null;

            string driver;
            switch (dbType.Code)
            {
                case 16: // SQL Server
                case 18: // SQL Azure
                    driver = "{ODBC Driver 17 for SQL Server}";
                    break;
                case 35: // PostgreSQL
                    driver = "{PostgreSQL ANSI}";
                    break;
                case 10: // Oracle
                    driver = "{Oracle in OraDB19Home1}";
                    break;
                case 8: // MySQL
                    driver = "{MySQL ODBC 8.0 Unicode Driver}";
                    break;
                default:
                    driver = "{SQL Server}"; // fallback
                    break;
            }

            string connStr = $"Driver={driver};Server={txtServer.Text.Trim()}";
            if (!string.IsNullOrWhiteSpace(txtDatabase.Text))
                connStr += $";Database={txtDatabase.Text.Trim()}";

            if (rbWinAuth.Checked)
                connStr += ";Trusted_Connection=Yes";
            else if (rbSqlAuth.Checked && !string.IsNullOrWhiteSpace(txtUsername.Text))
                connStr += $";UID={txtUsername.Text.Trim()};PWD={txtPassword.Text}";

            return connStr;
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            // Validate
            if (rbNative.Checked && string.IsNullOrWhiteSpace(txtServer.Text))
            {
                MessageBox.Show("Server is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            if (rbOdbc.Checked && string.IsNullOrWhiteSpace(txtDsnName.Text))
            {
                MessageBox.Show("DSN Name is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            if (rbSqlAuth.Checked && string.IsNullOrWhiteSpace(txtUsername.Text))
            {
                MessageBox.Show("Username is required for SQL Auth.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            SaveLastSettings();

            // Build erwin RE connection string
            var dbType = DbTypes[cmbDbType.SelectedIndex];
            int authCode = rbWinAuth.Checked ? 8 : 4;
            string user = rbSqlAuth.Checked ? txtUsername.Text.Trim() : "";

            // SERVER= (single R) per erwin API Reference 15.0 HTML version (bookshelf).
            // A prior note claimed double-R was required, but that came from a PDF-to-text
            // conversion glitch where "SERVER-\nR=..." word-wrap was joined into "SERVERR=".
            // HTML source verified single R is correct.
            string connStr = $"SERVER={dbType.Code}:{dbType.MajorVer}:0|AUTHENTICATION={authCode}|USER={user}";

            if (rbNative.Checked)
            {
                // Native: 1=3, 2=database, 3=server
                connStr += "|1=3";
                if (!string.IsNullOrWhiteSpace(txtDatabase.Text))
                    connStr += $"|2={txtDatabase.Text.Trim()}";
                connStr += $"|3={txtServer.Text.Trim()}";

                DisplayLabel = $"{dbType.Name}: {txtServer.Text.Trim()}";
                if (!string.IsNullOrWhiteSpace(txtDatabase.Text))
                    DisplayLabel += $"/{txtDatabase.Text.Trim()}";
            }
            else
            {
                // ODBC: 1=2, 5=dsnName
                connStr += $"|1=2|5={txtDsnName.Text.Trim()}";
                DisplayLabel = $"{dbType.Name} (ODBC): {txtDsnName.Text.Trim()}";
            }

            ConnectionString = connStr;
            Password = rbSqlAuth.Checked ? txtPassword.Text : "";
            SchemaFilter = txtSchemaFilter.Text.Trim();
            TargetServerCode = dbType.TargetServer;
            TargetServerVersion = dbType.MajorVer;

            // Raw values for the in-process RE pipeline (DSN is created at runtime).
            ServerHost = txtServer.Text.Trim();
            DatabaseName = txtDatabase.Text.Trim();
            UserName = user;
            UseWindowsAuth = rbWinAuth.Checked;
            DbTypeCode = dbType.Code;

            this.DialogResult = DialogResult.OK;
        }
    }
}
