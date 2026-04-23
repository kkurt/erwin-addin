using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using EliteSoft.Erwin.AddIn.Services;
using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Phase 3.F add-in UI: compare the active erwin model (optionally with
    /// its dirty buffer) against a selected Mart version of the same model
    /// family, then display the resulting <see cref="Change"/> list and the
    /// emitter-produced ALTER SQL.
    ///
    /// The form is constructed with a live SCAPI handle and the active PU; it
    /// does NOT own either lifetime. <see cref="VersionCompareService"/> does
    /// the heavy lifting.
    /// </summary>
    public partial class CompareVersionsForm : Form
    {
        private readonly VersionCompareService _service;
        private readonly Action<string> _log;
        private readonly List<(int Version, string Name)> _versions = new();

        private string _lastAlterSql = string.Empty;
        private string _lastDialect = "MSSQL";

        public CompareVersionsForm(dynamic scapi, dynamic activePU, Action<string> log = null)
        {
            InitializeComponent();

            _log = log ?? (_ => { });
            _service = new VersionCompareService(scapi, activePU, _log);

            Load += OnLoadPopulate;
        }

        private void OnLoadPopulate(object sender, EventArgs e)
        {
            try
            {
                var dirty = _service.ProbeDirty();
                var (target, major, minor) = _service.ReadActiveTargetServer();
                var dialect = VersionCompareService.ResolveDialect(target);
                _lastDialect = dialect;

                int currentVersion = _service.ReadActiveVersion();
                string dirtyTag = dirty.IsDirty ? "Dirty" : "Clean";
                lblBaseline.Text = $"Active Model (v{currentVersion}) - {dirtyTag} (via {dirty.Source})";
                lblDialect.Text = string.IsNullOrEmpty(target)
                    ? $"{dialect}"
                    : $"{dialect} (from model: {target} v{major}.{minor})";

                PopulateTargetVersions(currentVersion, dirty.IsDirty);

                // Phase 3.F gate: the in-process SCAPI compare flow is
                // intentionally disabled (it destroys the active Mart PU).
                // Keep the form open for metadata inspection + layout
                // validation; the Compare button stays off until the
                // out-of-process Worker pivot lands.
                btnCompare.Enabled = false;
                lblStatus.Text =
                    "Compare is temporarily disabled: in-process SCAPI on r10.10 "
                    + "invalidates the active Mart PU when dumping to temp. "
                    + "Pending Worker-based pivot (see 3.F follow-up).";
            }
            catch (Exception ex)
            {
                _log($"CompareVersionsForm.OnLoadPopulate failed: {ex.Message}");
                lblStatus.Text = $"Init error: {ex.Message}";
                btnCompare.Enabled = false;
            }
        }

        private void PopulateTargetVersions(int currentVersion, bool isDirty)
        {
            _versions.Clear();
            cmbTargetVersion.Items.Clear();

            foreach (var row in VersionCompareService.PlanTargetVersions(currentVersion, isDirty))
            {
                _versions.Add((row.Version, row.Label));
                cmbTargetVersion.Items.Add(row.Label);
            }
            if (cmbTargetVersion.Items.Count > 0) cmbTargetVersion.SelectedIndex = 0;
        }

        private async void btnCompare_Click(object sender, EventArgs e)
        {
            if (cmbTargetVersion.SelectedIndex < 0) return;
            int targetVersion = _versions[cmbTargetVersion.SelectedIndex].Version;

            SetBusy(true, $"Comparing against v{targetVersion}...");
            try
            {
                var outcome = await _service.CompareAsync(targetVersion, CancellationToken.None).ConfigureAwait(true);
                PopulateChanges(outcome);
                lblStatus.Text = $"Done. {outcome.Result.Changes.Count} change(s), {outcome.Script.Statements.Count} statement(s) emitted for {outcome.Dialect}.";
            }
            catch (Exception ex)
            {
                _log($"CompareVersionsForm.Compare failed: {ex.Message}");
                MessageBox.Show(
                    this,
                    $"Compare failed:\n\n{ex.GetType().Name}: {ex.Message}",
                    "Compare Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                lblStatus.Text = "Compare failed. See log for details.";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void PopulateChanges(CompareOutcome outcome)
        {
            lvChanges.BeginUpdate();
            try
            {
                lvChanges.Items.Clear();
                foreach (var change in outcome.Result.Changes)
                {
                    var row = new ListViewItem(new[]
                    {
                        change.GetType().Name,
                        change.Target.Class,
                        change.Target.Name,
                        DescribeDetail(change),
                    });
                    lvChanges.Items.Add(row);
                }
            }
            finally
            {
                lvChanges.EndUpdate();
            }

            var sb = new StringBuilder();
            sb.AppendLine($"-- ALTER SQL for {outcome.Dialect} ({outcome.Script.Statements.Count} statement(s))");
            sb.AppendLine($"-- Baseline: active model (possibly dirty)  Target: Mart version selected above");
            sb.AppendLine();
            foreach (var stmt in outcome.Script.Statements)
            {
                if (!string.IsNullOrWhiteSpace(stmt.Comment))
                    sb.AppendLine("-- " + stmt.Comment);
                sb.AppendLine(stmt.Sql);
                if (outcome.Dialect == "MSSQL") sb.AppendLine("GO");
                sb.AppendLine();
            }
            _lastAlterSql = sb.ToString();
            _lastDialect = outcome.Dialect;
            txtAlterSql.Text = _lastAlterSql;
            btnSaveSql.Enabled = true;
        }

        private static string DescribeDetail(Change change) => change switch
        {
            EntityRenamed er => $"from '{er.OldName}'",
            SchemaMoved sm => $"{sm.OldSchema} -> {sm.NewSchema}",
            AttributeAdded aa => $"in {aa.ParentEntity.Name}",
            AttributeDropped ad => $"from {ad.ParentEntity.Name}",
            AttributeRenamed ar => $"{ar.ParentEntity.Name}: '{ar.OldName}' -> '{ar.Target.Name}'",
            AttributeTypeChanged at => $"{at.ParentEntity.Name}.{at.Target.Name}: {at.LeftType} -> {at.RightType}",
            AttributeNullabilityChanged an => $"{an.ParentEntity.Name}.{an.Target.Name}: {(an.LeftNullable ? "NULL" : "NOT NULL")} -> {(an.RightNullable ? "NULL" : "NOT NULL")}",
            AttributeDefaultChanged ad => $"{ad.ParentEntity.Name}.{ad.Target.Name}: '{ad.LeftDefault}' -> '{ad.RightDefault}'",
            AttributeIdentityChanged ai => $"{ai.ParentEntity.Name}.{ai.Target.Name}: {ai.LeftHasIdentity} -> {ai.RightHasIdentity}",
            KeyGroupAdded ka => $"{ka.Kind} on {ka.ParentEntity.Name}",
            KeyGroupDropped kd => $"{kd.Kind} on {kd.ParentEntity.Name}",
            KeyGroupRenamed kr => $"{kr.Kind} on {kr.ParentEntity.Name}: '{kr.OldName}' -> '{kr.Target.Name}'",
            ForeignKeyRenamed fr => $"from '{fr.OldName}'",
            TriggerRenamed tr => $"from '{tr.OldName}'",
            SequenceRenamed sr => $"from '{sr.OldName}'",
            ViewRenamed vr => $"from '{vr.OldName}'",
            _ => string.Empty,
        };

        private void btnSaveSql_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_lastAlterSql)) return;

            using var dlg = new SaveFileDialog
            {
                Filter = "SQL script (*.sql)|*.sql|All files (*.*)|*.*",
                FileName = $"alter-{_lastDialect.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.sql",
                Title = "Save Alter SQL",
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                // UTF-8 without BOM, per project convention.
                File.WriteAllText(dlg.FileName, _lastAlterSql, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                lblStatus.Text = $"Saved to {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Save failed:\n\n{ex.Message}",
                    "Save Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void SetBusy(bool busy, string status = null)
        {
            btnCompare.Enabled = !busy;
            cmbTargetVersion.Enabled = !busy;
            btnSaveSql.Enabled = !busy && !string.IsNullOrEmpty(_lastAlterSql);
            progressBar.Visible = busy;
            progressBar.MarqueeAnimationSpeed = busy ? 30 : 0;
            if (status != null) lblStatus.Text = status;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
