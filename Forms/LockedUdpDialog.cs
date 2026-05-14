using System;
using System.Drawing;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Modal notification shown when the user edits a locked Column UDP. Replaces
    /// the generic <see cref="MessageBox"/> popup so the lock message has the same
    /// visual language as the rest of the add-in (QuestionWizardForm palette,
    /// custom warning header bar, no system beep / system icon). The dialog is
    /// fixed-size, single-button (OK), and always opens centered on the screen
    /// so it lands on top of the erwin Column Editor regardless of focus.
    ///
    /// All consumer-facing copy lives in <see cref="Show"/> arguments - the form
    /// itself owns layout and styling only.
    /// </summary>
    public sealed class LockedUdpDialog : Form
    {
        private static readonly Color ClrAccent = Color.FromArgb(204, 102, 0);   // warning amber
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);

        private LockedUdpDialog(string udpName, string attemptedValue, string keptValue)
        {
            Text = "UDP Locked";
            Size = new Size(440, 220);
            MinimumSize = Size;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5F);
            TopMost = true;

            // Accent strip
            var accentStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = 4,
                BackColor = ClrAccent
            };

            // Header
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = Color.White,
                Padding = new Padding(20, 12, 20, 8)
            };
            var lblTitle = new Label
            {
                Text = "UDP Locked",
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = true,
                Location = new Point(20, 12)
            };
            var lblSubtitle = new Label
            {
                Text = $"The UDP \"{udpName}\" is locked by the administrator.",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(20, 36)
            };
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSubtitle);

            var headerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = ClrBorder };

            // Body
            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(20, 16, 20, 16)
            };
            string attemptedLine = string.IsNullOrEmpty(attemptedValue)
                ? "Your input: (empty)"
                : $"Your input: \"{attemptedValue}\"";
            string keptLine = string.IsNullOrEmpty(keptValue)
                ? "Restored value: (empty)"
                : $"Restored value: \"{keptValue}\"";
            var lblMessage = new Label
            {
                Text = $"Your change was rejected and the previous value has been restored.\n\n{attemptedLine}\n{keptLine}",
                Font = new Font("Segoe UI", 10F),
                ForeColor = ClrTextPrimary,
                Location = new Point(20, 16),
                Size = new Size(390, 90),
                AutoSize = false
            };
            body.Controls.Add(lblMessage);

            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };

            // Footer with single OK button
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = ClrSurface,
                Padding = new Padding(16, 8, 16, 8)
            };
            var btnOk = new Button
            {
                Text = "OK",
                Size = new Size(96, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = ClrAccent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = DialogResult.OK
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Location = new Point(footer.Width - 112, 9);
            footer.Resize += (s, e) => btnOk.Location = new Point(footer.Width - 112, 9);
            footer.Controls.Add(btnOk);

            // Dock order (reverse)
            Controls.Add(body);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            AcceptButton = btnOk;
            CancelButton = btnOk;
        }

        /// <summary>
        /// Show the locked-UDP modal. Blocks the caller until the user clicks OK or
        /// closes the form. Marked synchronous because the caller
        /// (<c>EnforceLockedAttributeUdps</c>) runs inside a MonitorTimer tick that
        /// is reentrancy-guarded by <c>_isCheckingForChanges</c>; the modal pump is
        /// already protected from re-entering the tick.
        /// </summary>
        public static void Show(string udpName, string attemptedValue, string keptValue)
        {
            try
            {
                using var dlg = new LockedUdpDialog(udpName ?? string.Empty, attemptedValue ?? string.Empty, keptValue ?? string.Empty);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                // Fall back to a plain MessageBox if the custom form fails to load
                // for any reason - the user must still see SOME confirmation that
                // the lock blocked them, otherwise the revert looks like a phantom.
                System.Diagnostics.Debug.WriteLine($"LockedUdpDialog.Show failed, falling back: {ex.Message}");
                MessageBox.Show(
                    $"The UDP \"{udpName}\" is locked. Your change was rejected; the value remains \"{keptValue}\".",
                    "UDP Locked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }
}
