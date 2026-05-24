using System;
using System.Drawing;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Confirmation popup shown when the user clicks "Sent to Approve" in
    /// the DDL approval review dialog. Carries a dedicated multi-line
    /// description input that the user fills with the version description
    /// they want stamped on the Mart commit. This is DISTINCT from the
    /// "Note (optional)" field on the main review dialog: that note is for
    /// admin-facing context, this description is the Mart version comment
    /// itself.
    ///
    /// After OK, the description is read from <see cref="Description"/>
    /// and passed through to (a) the queue row's DESCRIPTION column and
    /// (b) the Mart save call so erwin's own dialog can be skipped.
    /// </summary>
    public sealed class ConfirmSubmitDialog : Form
    {
        private TextBox _txtDescription;

        /// <summary>
        /// The Mart version description the user typed. Empty string when
        /// the user left the field blank.
        /// </summary>
        public string Description => _txtDescription?.Text ?? string.Empty;

        public ConfirmSubmitDialog()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Text             = "Submit for Approval";
            FormBorderStyle  = FormBorderStyle.FixedDialog;
            StartPosition    = FormStartPosition.CenterParent;
            MaximizeBox      = false;
            MinimizeBox      = false;
            ShowInTaskbar    = false;
            ClientSize       = new Size(560, 340);
            BackColor        = Color.White;
            KeyPreview       = true;

            var fontBody      = new Font("Segoe UI", 9.5f);
            var fontBodyBold  = new Font("Segoe UI", 9.5f, FontStyle.Bold);

            // Warning icon at top-left, text body to the right of it.
            var iconBox = new PictureBox
            {
                Image = SystemIcons.Information.ToBitmap(),
                SizeMode = PictureBoxSizeMode.AutoSize,
                Location = new Point(20, 20),
            };
            Controls.Add(iconBox);

            var lblHeading = new Label
            {
                Text = "Submitting will save the model and bump its version.",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 40),
                AutoSize = false,
                Location = new Point(70, 18),
                Size = new Size(470, 24),
            };
            Controls.Add(lblHeading);

            var lblBody = new Label
            {
                Text =
                    "When you click OK, the model will be saved to the Mart " +
                    "repository and its version will be incremented. The text " +
                    "below is stamped on the new Mart version (separate from " +
                    "the optional admin note on the previous screen).",
                Font = fontBody,
                ForeColor = Color.FromArgb(70, 70, 70),
                AutoSize = false,
                Location = new Point(70, 48),
                Size = new Size(470, 60),
            };
            Controls.Add(lblBody);

            // Version description input (multi-line, mandatory-ish - empty
            // is allowed since erwin itself permits an empty description).
            var lblDesc = new Label
            {
                Text = "Version description:",
                Font = fontBodyBold,
                ForeColor = Color.FromArgb(80, 80, 80),
                AutoSize = true,
                Location = new Point(20, 120),
            };
            Controls.Add(lblDesc);

            _txtDescription = new TextBox
            {
                Font = fontBody,
                Location = new Point(20, 144),
                Size = new Size(ClientSize.Width - 40, 130),
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                MaxLength = 4000,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            };
            Controls.Add(_txtDescription);

            // Bottom-right button row: [Cancel] [OK].
            var btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Size = new Size(110, 32),
                Location = new Point(ClientSize.Width - 130, ClientSize.Height - 50),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(46, 125, 50),
                ForeColor = Color.White,
                Font = fontBodyBold,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            btnOk.FlatAppearance.BorderColor = Color.FromArgb(36, 105, 40);
            Controls.Add(btnOk);
            AcceptButton = btnOk;

            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(100, 32),
                Location = new Point(ClientSize.Width - 240, ClientSize.Height - 50),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(60, 60, 60),
                Font = fontBody,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
        }
    }
}
