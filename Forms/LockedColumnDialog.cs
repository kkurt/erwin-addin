using System;
using System.Drawing;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Action that the user attempted on a locked predefined column.
    /// Drives the dialog copy so the user sees an accurate explanation
    /// of what was undone.
    /// </summary>
    public enum LockedColumnAction
    {
        /// <summary>User renamed the column.</summary>
        Rename,
        /// <summary>User changed a property (datatype, nullable, default, PK).</summary>
        PropertyChange,
        /// <summary>User deleted the column. We re-created it.</summary>
        Delete,
        /// <summary>User inserted/moved a column into the locked block. We push it to the table end (or, for a key/FK column, ask the user to).</summary>
        OrderEnforced
    }

    /// <summary>
    /// Modal notification shown when the user attempts to rename, retype,
    /// or delete a locked predefined column. Mirrors
    /// <see cref="LockedUdpDialog"/> visually so locked-resource messaging
    /// has one consistent look across the add-in.
    ///
    /// All consumer-facing copy lives in <see cref="Show"/> arguments - the
    /// form itself owns layout and styling only.
    /// </summary>
    public sealed class LockedColumnDialog : Form
    {
        private static readonly Color ClrAccent = Color.FromArgb(204, 102, 0);   // warning amber
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);

        private LockedColumnDialog(string columnName, string entityName, LockedColumnAction action, string detail)
        {
            (string title, string subtitle, string bodyHeading) = ActionCopy(action, columnName);

            Text = title;
            Size = new Size(460, 240);
            MinimumSize = Size;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5F);
            TopMost = true;

            var accentStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = 4,
                BackColor = ClrAccent
            };

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = Color.White,
                Padding = new Padding(20, 12, 20, 8)
            };
            var lblTitle = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = true,
                Location = new Point(20, 12)
            };
            var lblSubtitle = new Label
            {
                Text = subtitle,
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(20, 36)
            };
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSubtitle);

            var headerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = ClrBorder };

            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(20, 16, 20, 16)
            };
            string entityLine = string.IsNullOrEmpty(entityName) ? "" : $"Entity: \"{entityName}\"\n";
            string detailLine = string.IsNullOrEmpty(detail) ? "" : $"\n{detail}";
            var lblMessage = new Label
            {
                Text = $"{bodyHeading}\n\n{entityLine}Column: \"{columnName}\"{detailLine}",
                Font = new Font("Segoe UI", 10F),
                ForeColor = ClrTextPrimary,
                Location = new Point(20, 16),
                Size = new Size(410, 120),
                AutoSize = false
            };
            body.Controls.Add(lblMessage);

            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };

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

            Controls.Add(body);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            AcceptButton = btnOk;
            CancelButton = btnOk;

            // Force focus on Shown - erwin's Column Editor close-edge can
            // leave keyboard focus parked on the closing editor's hwnd or
            // on the diagram canvas, so this dialog appears (TopMost) but
            // click events route to whatever erwin window has focus. The
            // user perceives a 3-4 s freeze before clicks register
            // because Windows eventually transfers focus after the editor
            // teardown completes. Activating + focusing the OK button
            // here closes that gap. 2026-05-24 user complaint.
            Shown += (s, e) =>
            {
                try
                {
                    Activate();
                    BringToFront();
                    btnOk.Focus();
                }
                catch { /* best-effort focus - nothing to do on failure */ }
            };
        }

        private static (string title, string subtitle, string bodyHeading) ActionCopy(LockedColumnAction action, string columnName)
        {
            switch (action)
            {
                case LockedColumnAction.Rename:
                    return (
                        "Column Locked",
                        $"The column \"{columnName}\" is locked by the administrator.",
                        "The rename was reverted - locked columns cannot be renamed."
                    );
                case LockedColumnAction.PropertyChange:
                    return (
                        "Column Locked",
                        $"The column \"{columnName}\" is locked by the administrator.",
                        "The property change was reverted - locked columns cannot be modified."
                    );
                case LockedColumnAction.Delete:
                    return (
                        "Column Restored",
                        $"The column \"{columnName}\" is locked by the administrator.",
                        "The deletion was undone - the column was re-created with its original definition."
                    );
                case LockedColumnAction.OrderEnforced:
                    return (
                        "Column Order Locked",
                        "The administrator's predefined columns must stay first, in their defined order.",
                        $"The column \"{columnName}\" was moved after them - new columns are kept at the end of the table."
                    );
                default:
                    return ("Column Locked", $"The column \"{columnName}\" is locked.", "Your change was reverted.");
            }
        }

        /// <summary>
        /// Show the locked-column modal synchronously. <paramref name="detail"/>
        /// is optional extra context shown beneath the column name. Callers
        /// MUST set their own heartbeat-suspension guard before calling this
        /// and the apply path MUST run AFTER ShowDialog returns - both rules
        /// are enforced by the ValidationCoordinatorService helper that
        /// invokes us. See <c>ValidationCoordinatorService.EnqueueLocked-
        /// ColumnDialogAndApply</c> for the canonical call pattern.
        /// </summary>
        public static void Show(string columnName, string entityName, LockedColumnAction action, string detail = null)
        {
            try
            {
                using var dlg = new LockedColumnDialog(columnName ?? string.Empty, entityName ?? string.Empty, action, detail ?? string.Empty);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LockedColumnDialog.Show failed, falling back: {ex.Message}");
                MessageBox.Show(
                    $"The column \"{columnName}\" is locked. Your change was rejected.",
                    "Column Locked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }
}
