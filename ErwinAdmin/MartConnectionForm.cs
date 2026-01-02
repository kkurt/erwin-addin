using System;
using System.Drawing;
using System.Windows.Forms;

namespace EliteSoft.Erwin.Admin
{
    public partial class MartConnectionForm : Form
    {
        private GroupBox grpMartConnection;
        private Label lblServerName;
        private TextBox txtServerName;
        private Label lblPort;
        private TextBox txtPort;
        private Label lblUserName;
        private TextBox txtUserName;
        private Label lblPassword;
        private TextBox txtPassword;

        private GroupBox grpModel;
        private Label lblModelPath;
        private TextBox txtModelPath;
        private Button btnBrowseModel;

        private Button btnConnect;
        private Button btnClose;
        private Label lblStatus;

        private dynamic _scapi;
        private dynamic _currentModel;

        public MartConnectionForm()
        {
            InitializeComponent();
            InitializeSCAPI();
        }

        private void InitializeComponent()
        {
            this.Text = "Elite Soft Erwin Admin - Mart Connection";
            this.Size = new Size(600, 450);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Mart Connection Group
            grpMartConnection = new GroupBox();
            grpMartConnection.Text = "Mart Connection";
            grpMartConnection.Location = new Point(12, 12);
            grpMartConnection.Size = new Size(560, 150);
            this.Controls.Add(grpMartConnection);

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
            txtPort.Size = new Size(165, 23);
            txtPort.Text = "1521";
            grpMartConnection.Controls.Add(txtPort);

            // User Name
            lblUserName = new Label();
            lblUserName.Text = "User Name:";
            lblUserName.Location = new Point(15, 63);
            lblUserName.Size = new Size(90, 20);
            grpMartConnection.Controls.Add(lblUserName);

            txtUserName = new TextBox();
            txtUserName.Location = new Point(110, 60);
            txtUserName.Size = new Size(430, 23);
            grpMartConnection.Controls.Add(txtUserName);

            // Password
            lblPassword = new Label();
            lblPassword.Text = "Password:";
            lblPassword.Location = new Point(15, 98);
            lblPassword.Size = new Size(90, 20);
            grpMartConnection.Controls.Add(lblPassword);

            txtPassword = new TextBox();
            txtPassword.Location = new Point(110, 95);
            txtPassword.Size = new Size(430, 23);
            txtPassword.UseSystemPasswordChar = true;
            grpMartConnection.Controls.Add(txtPassword);

            // Model Group
            grpModel = new GroupBox();
            grpModel.Text = "Model File";
            grpModel.Location = new Point(12, 170);
            grpModel.Size = new Size(560, 90);
            this.Controls.Add(grpModel);

            // Model Path
            lblModelPath = new Label();
            lblModelPath.Text = "Model Path:";
            lblModelPath.Location = new Point(15, 28);
            lblModelPath.Size = new Size(90, 20);
            grpModel.Controls.Add(lblModelPath);

            txtModelPath = new TextBox();
            txtModelPath.Location = new Point(110, 25);
            txtModelPath.Size = new Size(320, 23);
            txtModelPath.ReadOnly = true;
            grpModel.Controls.Add(txtModelPath);

            btnBrowseModel = new Button();
            btnBrowseModel.Text = "Browse...";
            btnBrowseModel.Location = new Point(440, 24);
            btnBrowseModel.Size = new Size(100, 25);
            btnBrowseModel.Click += BtnBrowseModel_Click;
            grpModel.Controls.Add(btnBrowseModel);

            // Buttons
            btnConnect = new Button();
            btnConnect.Text = "Load from Mart";
            btnConnect.Location = new Point(362, 275);
            btnConnect.Size = new Size(120, 35);
            btnConnect.Click += BtnConnect_Click;
            btnConnect.Enabled = false;
            this.Controls.Add(btnConnect);

            btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.Location = new Point(492, 275);
            btnClose.Size = new Size(80, 35);
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);

            // Status Label
            lblStatus = new Label();
            lblStatus.Location = new Point(12, 320);
            lblStatus.Size = new Size(560, 80);
            lblStatus.ForeColor = Color.DarkBlue;
            lblStatus.Text = "";
            this.Controls.Add(lblStatus);
        }

        private void InitializeSCAPI()
        {
            try
            {
                Type scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                if (scapiType != null)
                {
                    _scapi = Activator.CreateInstance(scapiType);
                    lblStatus.Text = "SCAPI initialized successfully.\nEnter Mart connection details and select a model file.";
                    lblStatus.ForeColor = Color.DarkGreen;
                    btnConnect.Enabled = true;
                }
                else
                {
                    lblStatus.Text = "ERROR: Could not initialize SCAPI. Make sure erwin Data Modeler is installed.";
                    lblStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"ERROR initializing SCAPI:\n{ex.Message}";
                lblStatus.ForeColor = Color.Red;
                btnConnect.Enabled = false;
            }
        }

        private void BtnBrowseModel_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "erwin Files (*.erwin;*.erwm)|*.erwin;*.erwm|All Files (*.*)|*.*";
                ofd.Title = "Select erwin Model";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtModelPath.Text = ofd.FileName;
                }
            }
        }

        private void BtnConnect_Click(object sender, EventArgs e)
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

            if (string.IsNullOrWhiteSpace(txtModelPath.Text))
            {
                MessageBox.Show("Please select a model file.", "Validation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                btnBrowseModel.Focus();
                return;
            }

            try
            {
                lblStatus.Text = "Connecting to Mart and loading model...";
                lblStatus.ForeColor = Color.DarkBlue;
                Application.DoEvents();

                // Build Mart connection string
                string martConnectionString = BuildMartConnectionString();

                // Load model from Mart
                // Format: "mart://ServerName:Port/ModelPath"
                string martUrl = $"mart://{txtServerName.Text}:{txtPort.Text}/{txtModelPath.Text}";

                // Connection options with credentials
                string options = $"RDO=No;UID={txtUserName.Text};PWD={txtPassword.Text}";

                _currentModel = _scapi.PersistenceUnits.Add(martUrl, options);

                if (_currentModel != null)
                {
                    lblStatus.Text = $"SUCCESS!\n\nModel loaded from Mart:\n{txtModelPath.Text}\n\nServer: {txtServerName.Text}:{txtPort.Text}\nUser: {txtUserName.Text}";
                    lblStatus.ForeColor = Color.DarkGreen;

                    MessageBox.Show($"Model successfully loaded from Mart!\n\nYou can now work with this model.",
                        "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    lblStatus.Text = "ERROR: Failed to load model from Mart.\nThe model object is null.";
                    lblStatus.ForeColor = Color.Red;
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"ERROR loading model from Mart:\n{ex.Message}";
                lblStatus.ForeColor = Color.Red;

                MessageBox.Show($"Failed to load model from Mart:\n\n{ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string BuildMartConnectionString()
        {
            // Build connection string for Mart
            // Format may vary depending on erwin Mart version
            return $"Server={txtServerName.Text};Port={txtPort.Text};UID={txtUserName.Text};PWD={txtPassword.Text}";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            if (_currentModel != null)
            {
                try { _scapi.PersistenceUnits.Remove(_currentModel); } catch { }
            }

            _scapi = null;
        }
    }
}
