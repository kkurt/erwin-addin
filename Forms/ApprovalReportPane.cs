#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Right-hand pane of the DDL Review window: which blocking rules the open model satisfies
    /// and which it does not.
    ///
    /// <para><b>Shows PASSING rules too.</b> A reviewer who is refused needs to know what was
    /// checked, not only what failed - and a reviewer who is allowed through needs the same
    /// reassurance. The pane therefore appears whenever the config defines at least one blocking
    /// rule, and simply reports "no violations" when there are none.</para>
    ///
    /// <para><b>The verdict is model-wide, and the pane says so.</b> The DDL on the left is an
    /// ALTER script covering what changed; the rules are evaluated over every object in the
    /// model. Those are different object sets, and letting a reviewer assume otherwise would
    /// make an unfixable-looking violation on an untouched legacy table read as a bug.</para>
    ///
    /// <para>A <see cref="Panel"/> subclass rather than a <c>UserControl</c>: this codebase has
    /// no UserControl anywhere, and <c>EnvironmentPipelineDiagram</c> established the Panel
    /// pattern for composite surfaces.</para>
    /// </summary>
    internal sealed class ApprovalReportPane : Panel
    {
        private static readonly Color ClrError = Color.FromArgb(192, 57, 43);
        private static readonly Color ClrWarning = Color.FromArgb(204, 102, 0);
        private static readonly Color ClrMuted = Color.FromArgb(120, 120, 120);
        private static readonly Color ClrOk = Color.FromArgb(46, 125, 50);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);

        private const int SidePadding = 12;
        private const int DetailHeight = 120;

        private readonly ApprovalBlockingGateResult _result;
        private readonly ListView _list;
        private readonly TextBox _detail;

        public ApprovalReportPane(ApprovalBlockingGateResult result)
        {
            _result = result ?? throw new ArgumentNullException(nameof(result));

            Dock = DockStyle.Fill;
            BackColor = Color.White;
            Padding = new Padding(0);

            var fontBody = new Font("Segoe UI", 9f);
            var fontBold = new Font("Segoe UI", 10f, FontStyle.Bold);

            _detail = BuildDetail(fontBody);
            _list = BuildList(fontBody);
            _list.SelectedIndexChanged += (_, _) => ShowSelectedDetail();

            PopulateList();

            // Fill child added FIRST so the docked bands claim their slice, then Bottom, then
            // Top in reverse order - the same rule DdlApprovalDialog.BuildUi documents.
            Controls.Add(_list);
            Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder });
            Controls.Add(_detail);
            Controls.Add(BuildCounters(fontBody));
            Controls.Add(BuildSubtitle(fontBody));
            Controls.Add(BuildTitle(fontBold));

            var banner = BuildGateBanner(fontBody);
            if (banner != null) Controls.Add(banner);

            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        }

        /// <summary>
        /// True when the model must not be submitted: a violated rule, a rule that could not be
        /// checked, or a gate-level failure. NotApplicable and Passed do not block.
        /// </summary>
        public bool Blocks => _result.Blocked;

        /// <summary>One-line verdict for the status label next to the buttons.</summary>
        public string Summary
        {
            get
            {
                int failed = Count(ApprovalRuleStatus.Failed);
                int notChecked = Count(ApprovalRuleStatus.NotChecked);
                if (failed == 0 && notChecked == 0)
                    return $"{_result.RuleReport.Count} blocking rule(s) checked, no violations.";

                var parts = new List<string>();
                if (failed > 0) parts.Add($"{failed} rule(s) violated");
                if (notChecked > 0) parts.Add($"{notChecked} rule(s) could not be checked");
                return string.Join(", ", parts) + " - see the report on the right.";
            }
        }

        private int Count(ApprovalRuleStatus status)
            => _result.RuleReport.Count(r => r.Status == status);

        #region Header

        private static Control BuildTitle(Font font) => new Label
        {
            Text = "Blocking rules",
            Font = font,
            ForeColor = ClrTextPrimary,
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(SidePadding, 0, SidePadding, 0),
            UseMnemonic = false
        };

        private Control BuildSubtitle(Font font) => new Label
        {
            // The scope warning is load-bearing, not decoration - see the class remarks.
            Text = "Evaluated over the whole model, not only the objects in this DDL.",
            Font = font,
            ForeColor = ClrTextSecondary,
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(SidePadding, 0, SidePadding, 6),
            UseMnemonic = false
        };

        /// <summary>
        /// Gate-level failures (<c>RuleId == 0</c>): no resolved configuration, an unreadable
        /// toggle, an unreadable model. They belong to no rule, so they get a banner instead of
        /// being hidden among the rows.
        /// </summary>
        private Control? BuildGateBanner(Font font)
        {
            var gateIssues = _result.GateIssues;
            if (gateIssues.Count == 0) return null;

            return new Label
            {
                Text = string.Join(Environment.NewLine, gateIssues.Select(i => i.Reason)),
                Font = font,
                ForeColor = Color.White,
                BackColor = ClrError,
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 22 + (18 * Math.Min(gateIssues.Count, 3)),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(SidePadding, 4, SidePadding, 4),
                UseMnemonic = false
            };
        }

        /// <summary>
        /// Count chips. A wrapping flow panel rather than fixed positions because the pane is
        /// user-resizable via the splitter and four chips do not always fit on one line.
        /// </summary>
        private Control BuildCounters(Font font)
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(SidePadding - 4, 0, SidePadding - 4, 6),
                BackColor = Color.White
            };

            AddChip(flow, font, Count(ApprovalRuleStatus.Failed), "violated", ClrError);
            AddChip(flow, font, Count(ApprovalRuleStatus.NotChecked), "not checked", ClrWarning);
            AddChip(flow, font, Count(ApprovalRuleStatus.NotApplicable), "not applicable", ClrMuted);
            AddChip(flow, font, Count(ApprovalRuleStatus.Passed), "passed", ClrOk);
            return flow;
        }

        private static void AddChip(FlowLayoutPanel host, Font font, int count, string label, Color color)
        {
            var chip = new Label
            {
                Text = $"{count} {label}",
                Font = font,
                ForeColor = count == 0 ? ClrTextSecondary : Color.White,
                BackColor = count == 0 ? ClrSurface : color,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(4, 2, 0, 2),
                UseMnemonic = false
            };
            host.Controls.Add(chip);
        }

        #endregion

        #region Rule list

        private static ListView BuildList(Font font)
        {
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                GridLines = false,
                Font = font,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White
            };

            // Rule takes the slack: it holds the admin's own label, which is the only column
            // whose useful length is unbounded.
            list.Columns.Add("Status", 96, HorizontalAlignment.Left);
            list.Columns.Add("Rule", 190, HorizontalAlignment.Left);
            list.Columns.Add("Applies to", 130, HorizontalAlignment.Left);
            list.Columns.Add("Objects", 70, HorizontalAlignment.Right);
            return list;
        }

        private void PopulateList()
        {
            _list.BeginUpdate();
            try
            {
                foreach (var row in _result.RuleReport)
                {
                    var item = new ListViewItem(StatusText(row.Status)) { Tag = row };
                    item.ForeColor = StatusColor(row.Status);
                    item.SubItems.Add(row.DisplayName);
                    item.SubItems.Add(row.Scope);
                    // Applicable, not walked: "12 of 8401 objects matched" is what decides
                    // whether a pass means anything, and it is the number a reviewer asks for.
                    item.SubItems.Add(row.Status == ApprovalRuleStatus.NotChecked
                        ? "-"
                        : row.ApplicableCount.ToString("N0"));
                    _list.Items.Add(item);
                }
            }
            finally { _list.EndUpdate(); }
        }

        private static string StatusText(ApprovalRuleStatus status) => status switch
        {
            ApprovalRuleStatus.Failed => "Violated",
            ApprovalRuleStatus.NotChecked => "Not checked",
            ApprovalRuleStatus.NotApplicable => "Not applicable",
            _ => "Passed"
        };

        private static Color StatusColor(ApprovalRuleStatus status) => status switch
        {
            ApprovalRuleStatus.Failed => ClrError,
            ApprovalRuleStatus.NotChecked => ClrWarning,
            ApprovalRuleStatus.NotApplicable => ClrMuted,
            _ => ClrOk
        };

        #endregion

        #region Detail

        /// <summary>
        /// Per-rule detail, in-pane rather than in a nested dialog. Two reasons: stacking a
        /// modal over an already-modal review window is hostile, and long admin ERROR_MESSAGE
        /// text (routinely Turkish sentences) wraps here instead of being clipped in a cell.
        /// </summary>
        private static TextBox BuildDetail(Font font) => new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = DetailHeight,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Font = font,
            BackColor = ClrSurface,
            ForeColor = ClrTextPrimary,
            BorderStyle = BorderStyle.None,
            TabStop = false
        };

        private void ShowSelectedDetail()
        {
            if (_list.SelectedItems.Count == 0) { _detail.Text = ""; return; }
            if (_list.SelectedItems[0].Tag is not ApprovalRuleReportRow row) { _detail.Text = ""; return; }

            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(row.RuleDescription))
                lines.Add(row.RuleDescription);

            if (!string.IsNullOrWhiteSpace(row.RuleSummary))
                lines.Add($"Rule #{row.RuleId}: {row.RuleSummary}");

            switch (row.Status)
            {
                case ApprovalRuleStatus.NotApplicable:
                    lines.Add($"This rule matched none of the {row.ObjectsChecked:N0} " +
                              $"{row.ObjectType} object(s) in the model, so it verified nothing.");
                    break;

                case ApprovalRuleStatus.NotChecked:
                    lines.Add("This rule could not be evaluated, so the model cannot be declared " +
                              "clean for it. This is a rule-definition problem for an administrator " +
                              "to fix, not something you can change in the model.");
                    break;

                case ApprovalRuleStatus.Passed:
                    lines.Add($"{row.ApplicableCount:N0} of {row.ObjectsChecked:N0} " +
                              $"{row.ObjectType} object(s) were applicable and all satisfied the rule.");
                    break;
            }

            if (row.ReportedIssues.Count > 0)
            {
                lines.Add("");
                foreach (var issue in row.ReportedIssues)
                {
                    lines.Add(string.IsNullOrEmpty(issue.ObjectName)
                        ? issue.Reason
                        : $"{issue.ObjectName}: {issue.Reason}");
                }
            }

            // The cap is stated, never silently applied: the row's own count is the truth and
            // the log holds the full set.
            if (row.ViolationCount > row.ReportedIssues.Count)
            {
                lines.Add("");
                lines.Add($"Showing {row.ReportedIssues.Count} of {row.ViolationCount:N0} " +
                          "violation(s); the full list is in the Debug Log.");
            }

            _detail.Lines = lines.ToArray();
        }

        #endregion
    }
}
