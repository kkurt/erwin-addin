#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Read-only report shown when <see cref="ApprovalBlockingRuleGate"/> refuses DDL
    /// generation. Lists every failing rule with the object it failed on, so the user can
    /// fix the model and retry - the whole point of the gate is that the message is
    /// actionable, not just "blocked".
    ///
    /// <para>Modelled on <see cref="UdpSyncDialog"/> (borderless chrome, accent strip,
    /// sticky TopMost, drag-by-header, multi-monitor positioning, ListView in Details
    /// mode) rather than <see cref="AddinMessageDialog"/>: that one renders its body as a
    /// single 460px word-wrapped label clamped to 75% of the screen height, which silently
    /// CLIPS long lists - unacceptable for a dialog whose only job is enumerating N
    /// violations.</para>
    ///
    /// <para><b>Language.</b> Every piece of chrome (title, subtitle, column headers,
    /// buttons, counters) is English per the project convention. The Reason column renders
    /// the admin-authored <c>ERROR_MESSAGE</c> VERBATIM - it is frequently Turkish and must
    /// never be translated, reworded or truncated.</para>
    /// </summary>
    public sealed class ApprovalBlockingRulesDialog : Form
    {
        // Design tokens (shared with the other add-in dialogs).
        private static readonly Color ClrPrimary = Color.FromArgb(0, 102, 204);
        private static readonly Color ClrError = Color.FromArgb(192, 57, 43);
        private static readonly Color ClrWarning = Color.FromArgb(204, 102, 0);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);
        private static readonly Color ClrCloseHover = Color.FromArgb(232, 17, 35);

        private const int DialogWidth = 880;
        private const int MinDialogHeight = 380;
        private const int AccentStripHeight = 4;
        private const int HeaderHeight = 46;
        private const int CloseButtonSize = 32;
        private const int SubtitleHeight = 32;
        private const int SummaryHeight = 40;
        private const int FooterHeight = 56;
        private const int SidePadding = 20;

        private const int ColTypeWidth = 100;
        private const int ColObjectWidth = 210;
        private const int ColPropertyWidth = 100;
        private const int ColRuleWidth = 170;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private readonly ApprovalBlockingGateResult _result;
        private readonly IReadOnlyList<ApprovalBlockingIssue> _issues;

        // What the user was trying to do ("Send to Approve", "Integrate to 2_TEST").
        // The report names it so a blocked promote is not mistaken for a blocked save.
        private readonly string _actionName;

        public ApprovalBlockingRulesDialog(ApprovalBlockingGateResult result, string? actionName = null)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));
            _issues = result.Issues;
            _actionName = string.IsNullOrWhiteSpace(actionName) ? "Submission" : actionName;
            if (_issues.Count == 0)
                throw new ArgumentException("ApprovalBlockingRulesDialog should not be opened without issues", nameof(result));

            Text = $"{_actionName} blocked";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5F);
            Padding = new Padding(1);
            Paint += (_, e) =>
            {
                using var pen = new Pen(ClrBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };
            TopMost = true;

            var accentStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = AccentStripHeight,
                BackColor = ClrError
            };

            var header = BuildHeader();
            var headerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = ClrBorder };
            var subtitle = new Label
            {
                Text = BuildSubtitle(),
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextSecondary,
                Dock = DockStyle.Top,
                Height = SubtitleHeight,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(SidePadding, 0, SidePadding, 0),
                UseMnemonic = false
            };

            var summary = BuildSummary();
            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };
            var footer = BuildFooter(out Button close);

            var listView = BuildListView();
            PopulateListView(listView);

            // Fill child first so it claims the middle, then bottom-docked, then
            // top-docked in reverse z-order (same order UdpSyncDialog uses).
            Controls.Add(listView);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(summary);
            Controls.Add(subtitle);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            AcceptButton = close;
            CancelButton = close;
            ActiveControl = close;

            Shown += (_, _) =>
            {
                if (IsDisposed) return;
                try { SetForegroundWindow(Handle); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ApprovalBlockingRulesDialog SetForegroundWindow failed: {ex.Message}");
                }
            };

            ResizeToFitContent();
        }

        private string BuildSubtitle()
        {
            // Says what did NOT happen and what to do. The model was not saved and nothing
            // reached the approval queue - the gate runs before both.
            string baseText = "Nothing was saved or submitted. Fix the model so every rule below passes, then try again.";
            // The overflow really is written to the Debug Log (ApprovalBlockingRuleGate.Finish
            // logs the FULL deduped set before truncating this list), so the pointer is
            // honest rather than a dead end.
            return _result.SuppressedIssueCount > 0
                ? baseText + $" (Showing the first {_issues.Count}; {_result.SuppressedIssueCount} more are in the Debug Log.)"
                : baseText;
        }

        #region Header

        private Panel BuildHeader()
        {
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = HeaderHeight,
                BackColor = Color.White,
                Cursor = Cursors.SizeAll
            };

            var lblTitle = new Label
            {
                Text = $"{_actionName} blocked",
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(SidePadding, 0, CloseButtonSize + 8, 0),
                UseMnemonic = false,
                Cursor = Cursors.SizeAll
            };

            var btnClose = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 10F, FontStyle.Regular),
                ForeColor = ClrTextSecondary,
                BackColor = Color.White,
                AutoSize = false,
                Size = new Size(CloseButtonSize, CloseButtonSize),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(DialogWidth - CloseButtonSize - 2, (HeaderHeight - CloseButtonSize) / 2)
            };
            btnClose.MouseEnter += (_, _) =>
            {
                btnClose.BackColor = ClrCloseHover;
                btnClose.ForeColor = Color.White;
            };
            btnClose.MouseLeave += (_, _) =>
            {
                btnClose.BackColor = Color.White;
                btnClose.ForeColor = ClrTextSecondary;
            };
            btnClose.Click += (_, _) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };

            // FormBorderStyle.None removes the system drag handle; re-add it with the
            // WM_NCLBUTTONDOWN HTCAPTION trick the other add-in dialogs use.
            void StartDrag(object? s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            header.MouseDown += StartDrag;
            lblTitle.MouseDown += StartDrag;

            header.Controls.Add(btnClose);
            header.Controls.Add(lblTitle);
            return header;
        }

        #endregion

        #region Summary

        private Panel BuildSummary()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = SummaryHeight,
                BackColor = Color.White,
                Padding = new Padding(SidePadding, 4, SidePadding, 8)
            };

            int violations = _issues.Count(i => i.Kind == ApprovalBlockingIssueKind.Violation);
            int unevaluatable = _issues.Count - violations;

            int x = SidePadding;
            x = AddCounter(panel, x, violations, "Violation", "Violations", ClrError);
            _ = AddCounter(panel, x, unevaluatable, "Rule that cannot be checked",
                           "Rules that cannot be checked", ClrWarning);

            return panel;
        }

        // One shared instance for both the measurement and the label render, so the two
        // provably agree and no orphaned GDI handle is created per measurement.
        private static readonly Font CounterFont = new Font("Segoe UI", 10F);

        private static int AddCounter(Panel panel, int x, int count, string singular, string plural, Color color)
        {
            if (count == 0) return x;

            var pill = new Label
            {
                Text = $"{count}",
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = color,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(x, 4),
                Size = new Size(34, 24),
                Padding = new Padding(2)
            };
            panel.Controls.Add(pill);

            string text = count == 1 ? singular : plural;
            int labelWidth = TextRenderer.MeasureText(text, CounterFont).Width + 8;

            var lbl = new Label
            {
                Text = text,
                Font = CounterFont,
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(x + 38, 4),
                Size = new Size(labelWidth, 24),
                UseMnemonic = false
            };
            panel.Controls.Add(lbl);

            return x + 38 + labelWidth + 16;
        }

        #endregion

        #region ListView

        private ListView BuildListView()
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5F),
                BackColor = Color.White,
                Margin = new Padding(SidePadding, 0, SidePadding, 0),
                Padding = new Padding(SidePadding, 0, SidePadding, 0)
            };

            lv.Columns.Add("Object type", ColTypeWidth);
            lv.Columns.Add("Object", ColObjectWidth);
            lv.Columns.Add("Property", ColPropertyWidth);
            lv.Columns.Add("Rule", ColRuleWidth);
            lv.Columns.Add("Why it failed", 200); // real width set from the live client area below

            // "Why it failed" carries the admin's message - the only actionable text in the
            // dialog - so it takes ALL remaining space. Sized from the control's real client
            // width rather than from DialogWidth: the ListView is Dock.Fill and its
            // Margin/Padding are ignored by both the layout engine and the native control,
            // so a DialogWidth-based guess left a dead strip on the right and stole pixels
            // from the one column that needs them.
            void SizeReasonColumn()
            {
                if (lv.IsDisposed || !lv.IsHandleCreated) return;
                int used = ColTypeWidth + ColObjectWidth + ColPropertyWidth + ColRuleWidth;
                lv.Columns[4].Width = Math.Max(160, lv.ClientSize.Width - used);
            }
            lv.HandleCreated += (_, _) => SizeReasonColumn();
            lv.Resize += (_, _) => SizeReasonColumn();

            AttachCellToolTip(lv);
            return lv;
        }

        /// <summary>
        /// Per-CELL tooltip over the whole row. <see cref="ListViewItem.ToolTipText"/> +
        /// <c>ShowItemToolTips</c> only raises an ITEM-level infotip, whose hover target in
        /// Details view is the column-0 cell - not the "Why it failed" cell where the text
        /// is actually clipped, so the user's natural gesture (hover the truncated reason)
        /// produced nothing. HitTest-driven so the full admin message is reachable from any
        /// cell in the row.
        /// </summary>
        private static void AttachCellToolTip(ListView lv)
        {
            var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 100 };
            ApprovalBlockingIssue? shown = null;

            lv.MouseMove += (_, e) =>
            {
                var hit = lv.HitTest(e.Location);
                var issue = hit.Item?.Tag as ApprovalBlockingIssue;
                // Re-setting the same text on every mouse move retriggers the popup and
                // makes it flicker, so only act on a row change.
                if (ReferenceEquals(issue, shown)) return;
                shown = issue;
                if (issue == null) tip.SetToolTip(lv, null);
                else tip.SetToolTip(lv, issue.Reason);
            };
            lv.MouseLeave += (_, _) => { shown = null; tip.SetToolTip(lv, null); };
            lv.Disposed += (_, _) => tip.Dispose();
        }

        private void PopulateListView(ListView lv)
        {
            lv.BeginUpdate();
            try
            {
                foreach (var issue in _issues)
                {
                    var item = new ListViewItem(issue.ObjectType)
                    {
                        UseItemStyleForSubItems = false,
                        Tag = issue,
                        // A rule that could not be checked is a different failure class
                        // than a rule that failed - colour the row so the two are not
                        // read as the same kind of problem.
                        ForeColor = issue.Kind == ApprovalBlockingIssueKind.Violation ? ClrTextPrimary : ClrWarning
                    };
                    item.SubItems.Add(issue.ObjectName);
                    item.SubItems.Add(issue.PropertyLabel);
                    item.SubItems.Add(issue.RuleId > 0 ? $"#{issue.RuleId} {issue.RuleSummary}".Trim() : issue.RuleSummary);
                    item.SubItems.Add(issue.Reason);
                    lv.Items.Add(item);
                }
            }
            finally
            {
                lv.EndUpdate();
            }
        }

        #endregion

        #region Footer

        private Panel BuildFooter(out Button close)
        {
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = ClrSurface,
                Padding = new Padding(16, 12, 16, 12)
            };

            var btnClose = new Button
            {
                Text = "Close",
                Size = new Size(104, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = ClrPrimary,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = DialogResult.OK,
                UseMnemonic = false,
                TabStop = true
            };
            btnClose.FlatAppearance.BorderSize = 0;

            var btnCopy = new Button
            {
                Text = "Copy details",
                Size = new Size(120, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                UseMnemonic = false,
                TabStop = true
            };
            btnCopy.FlatAppearance.BorderColor = ClrBorder;
            btnCopy.Click += (_, _) => CopyReportToClipboard();

            void Place()
            {
                btnClose.Location = new Point(footer.ClientSize.Width - 16 - btnClose.Width, 12);
                btnCopy.Location = new Point(btnClose.Left - 8 - btnCopy.Width, 12);
            }
            Place();
            footer.Resize += (_, _) => Place();
            // Added in VISUAL order (Copy sits left of Close) so WinForms' implicit
            // TabIndex follows the eye instead of running right-to-left. Copy is the only
            // way to read an admin message the Reason column clipped, so it must be
            // keyboard-reachable in the obvious direction.
            footer.Controls.Add(btnCopy);
            footer.Controls.Add(btnClose);

            close = btnClose;
            return footer;
        }

        /// <summary>
        /// Flattens one field for the tab-separated export. Only the SEPARATORS are
        /// normalized - a tab or newline inside an admin ERROR_MESSAGE would shift or split
        /// the row and the user would mail a corrupted report. The wording itself is
        /// untouched, so the verbatim-message rule still holds (Copy is the fallback path
        /// for reading a message the Reason column clipped).
        /// </summary>
        private static string Cell(string? value)
            => (value ?? "")
                .Replace("\t", " ", StringComparison.Ordinal)
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal);

        /// <summary>
        /// Puts the report on the clipboard as tab-separated text so the user can paste it
        /// into a mail or a ticket for whoever owns the model. A clipboard failure is
        /// SURFACED, never swallowed - silently doing nothing on a button click is worse
        /// than saying why.
        /// </summary>
        private void CopyReportToClipboard()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Submission blocked by naming rules");
            sb.AppendLine("Object type\tObject\tProperty\tRule\tWhy it failed");
            foreach (var issue in _issues)
            {
                sb.Append(Cell(issue.ObjectType)).Append('\t')
                  .Append(Cell(issue.ObjectName)).Append('\t')
                  .Append(Cell(issue.PropertyLabel)).Append('\t')
                  .Append(Cell(issue.RuleId > 0 ? $"#{issue.RuleId} {issue.RuleSummary}".Trim() : issue.RuleSummary)).Append('\t')
                  .AppendLine(Cell(issue.Reason));
            }
            if (_result.SuppressedIssueCount > 0)
                sb.AppendLine($"({_result.SuppressedIssueCount} further issue(s) not listed - see the add-in Debug Log.)");

            try
            {
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApprovalBlockingRulesDialog clipboard copy failed: {ex.Message}");
                AddinMessageDialog.Show(this,
                    $"The report could not be copied to the clipboard: {ex.Message}",
                    "Copy details", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        #endregion

        #region Sizing + positioning

        /// <summary>
        /// Grow the form to show up to 12 rows without scrolling; beyond that the
        /// ListView scrolls while the dialog caps at ~75% of the working area.
        /// </summary>
        private void ResizeToFitContent()
        {
            const int defaultRowHeight = 22;
            const int listChromeHeight = 28;
            const int desiredRowsVisible = 12;

            int visibleRows = Math.Min(_issues.Count, desiredRowsVisible);
            int listHeight = Math.Max(visibleRows, 3) * defaultRowHeight + listChromeHeight;

            int chromeHeight = AccentStripHeight
                               + HeaderHeight + 1
                               + SubtitleHeight
                               + SummaryHeight
                               + 1 + FooterHeight;
            int totalHeight = Math.Max(MinDialogHeight, chromeHeight + listHeight);

            int maxScreen = (int)((Screen.PrimaryScreen?.WorkingArea.Height ?? 800) * 0.75);
            if (totalHeight > maxScreen) totalHeight = maxScreen;

            ClientSize = new Size(DialogWidth, totalHeight);
        }

        /// <summary>
        /// Place the dialog on the screen the user is working on. Same strategy as
        /// <see cref="UdpSyncDialog.PositionOnActiveScreen"/>: add-in form, then owner,
        /// then foreground window, then primary.
        /// </summary>
        public void PositionOnActiveScreen(IWin32Window? owner)
        {
            Screen target;

            var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
            if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
            {
                target = Screen.FromControl(addinForm);
            }
            else if (owner is Control { IsDisposed: false } ownerCtrl && ownerCtrl.IsHandleCreated)
            {
                target = Screen.FromControl(ownerCtrl);
            }
            else
            {
                IntPtr fg = IntPtr.Zero;
                try { fg = GetForegroundWindow(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ApprovalBlockingRulesDialog GetForegroundWindow failed: {ex.Message}");
                }
                target = fg != IntPtr.Zero
                    ? Screen.FromHandle(fg)
                    : (Screen.PrimaryScreen ?? Screen.AllScreens[0]);
            }

            var area = target.WorkingArea;
            Location = new Point(
                area.Left + Math.Max(0, (area.Width - Width) / 2),
                area.Top + Math.Max(0, (area.Height - Height) / 2));
        }

        #endregion

        /// <summary>
        /// Show the blocked-rule report modally. Pass an explicit
        /// <paramref name="owner"/> (the add-in form): an ownerless add-in popup is
        /// re-parented to erwin's main frame, which makes the Generate DDL modal guard
        /// (<c>Win32Helper.IsErwinMainWindowBlockedByModal</c>) see our own dialog as
        /// "erwin is blocked" on the next attempt.
        /// </summary>
        public static void ShowFor(ApprovalBlockingGateResult result, IWin32Window? owner, string? actionName = null)
        {
            if (result == null || result.Issues.Count == 0) return;

            using var dlg = new ApprovalBlockingRulesDialog(result, actionName);
            dlg.PositionOnActiveScreen(owner);

            IWin32Window? effectiveOwner = owner;
            if (effectiveOwner == null)
            {
                var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
                if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
                    effectiveOwner = addinForm;
            }

            if (effectiveOwner != null) dlg.ShowDialog(effectiveOwner);
            else dlg.ShowDialog();
        }
    }
}
