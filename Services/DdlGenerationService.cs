using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Generates DDL diff between current model and Mart baseline using erwin's FEModel_DDL.
    ///
    /// Multi-phase approach (because Mart API blocks opening a second PU):
    ///
    /// Phase 1 (user clicks Generate DDL):
    ///   - FEModel_DDL on active model -> leftDdl (doesn't corrupt PU)
    ///   - PU.Save(tempFile) -> intentionally corrupts PU, triggers session lost + reconnect
    ///
    /// Phase 2 (after reconnect - fresh Mart model loaded):
    ///   - FEModel_DDL on Mart baseline -> rightDdl
    ///   - Compute diff (leftDdl vs rightDdl)
    ///   - Show results
    ///   - Clean up temp files
    /// </summary>
    public static class DdlGenerationService
    {
        private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "erwin-addin-ddl");

        // Baseline DDL saved at connect time (before user makes changes)
        private static string _baselineDdl;

        public static bool HasBaseline => !string.IsNullOrEmpty(_baselineDdl);

        // Legacy multi-phase (no longer used)
        public static bool IsPhaseInProgress => false;

        /// <summary>
        /// Save baseline DDL at connect time (before user makes changes).
        /// FEModel_DDL does NOT corrupt the PU - safe to call anytime.
        /// </summary>
        public static void SaveBaselineDDL(dynamic currentPU, string feOptionXml, Action<string> log)
        {
            Directory.CreateDirectory(TempDir);
            string ddlFile = Path.Combine(TempDir, "baseline.sql");

            try
            {
                CleanupFile(ddlFile);
                string optionArg = string.IsNullOrEmpty(feOptionXml) ? "" : feOptionXml;

                log?.Invoke("DDL: Saving baseline DDL (connect time snapshot)...");
                bool result = currentPU.FEModel_DDL(ddlFile, optionArg);

                if (result && File.Exists(ddlFile))
                {
                    _baselineDdl = File.ReadAllText(ddlFile);
                    log?.Invoke($"DDL: Baseline DDL saved ({_baselineDdl.Length} chars, {_baselineDdl.Split('\n').Length} lines)");
                }
                else
                {
                    log?.Invoke("DDL: FEModel_DDL failed for baseline.");
                    _baselineDdl = null;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: SaveBaselineDDL error: {ex.Message}");
                _baselineDdl = null;
            }
            finally
            {
                CleanupFile(ddlFile);
            }
        }

        /// <summary>
        /// Generate DDL diff: current model vs baseline (saved at connect time).
        /// Both use erwin's FEModel_DDL - no PU corruption, no reconnect.
        /// </summary>
        public static string GenerateDiff(dynamic currentPU, string feOptionXml, Action<string> log)
        {
            if (!HasBaseline)
            {
                log?.Invoke("DDL: No baseline DDL available.");
                return null;
            }

            Directory.CreateDirectory(TempDir);
            string ddlFile = Path.Combine(TempDir, "current.sql");

            try
            {
                CleanupFile(ddlFile);
                string optionArg = string.IsNullOrEmpty(feOptionXml) ? "" : feOptionXml;

                log?.Invoke("DDL: Generating current model DDL...");
                bool result = currentPU.FEModel_DDL(ddlFile, optionArg);

                if (!result || !File.Exists(ddlFile))
                {
                    log?.Invoke("DDL: FEModel_DDL failed for current model.");
                    return null;
                }

                string currentDdl = File.ReadAllText(ddlFile);
                log?.Invoke($"DDL: Current DDL = {currentDdl.Length} chars, {currentDdl.Split('\n').Length} lines");
                log?.Invoke($"DDL: Baseline DDL = {_baselineDdl.Length} chars, {_baselineDdl.Split('\n').Length} lines");

                if (currentDdl.Length == _baselineDdl.Length && currentDdl == _baselineDdl)
                {
                    log?.Invoke("DDL: Current and baseline are IDENTICAL. No changes detected.");
                    return "-- No differences found. Model has not been modified since connect.";
                }

                log?.Invoke($"DDL: Files differ ({Math.Abs(currentDdl.Length - _baselineDdl.Length)} chars difference). Computing diff...");
                return ComputeDDLDiff(currentDdl, _baselineDdl, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: GenerateDiff error: {ex.Message}");
                return null;
            }
            finally
            {
                CleanupFile(ddlFile);
            }
        }

        public static void ClearBaseline()
        {
            _baselineDdl = null;
        }

        /// <summary>
        /// Generate DDL diff: current model vs connect-time baseline.
        /// Both DDLs are generated by erwin's FEModel_DDL.
        /// Safe: no PU corruption, no Mart save, no reconnect.
        /// </summary>
        public static string GenerateDiffWithDuplicate(dynamic scapi, dynamic currentPU, string feOptionXml, Action<string> log)
        {
            if (!HasBaseline)
            {
                log?.Invoke("DDL: No baseline available.");
                return "-- No baseline DDL available. Reconnect to model first.";
            }

            Directory.CreateDirectory(TempDir);
            string currentDdlFile = Path.Combine(TempDir, "current.sql");

            try
            {
                CleanupFile(currentDdlFile);
                string optionArg = string.IsNullOrEmpty(feOptionXml) ? "" : feOptionXml;

                log?.Invoke("DDL: Generating current model DDL...");
                bool result = currentPU.FEModel_DDL(currentDdlFile, optionArg);
                if (!result || !File.Exists(currentDdlFile))
                {
                    log?.Invoke("DDL: FEModel_DDL failed.");
                    return null;
                }

                string currentDdl = File.ReadAllText(currentDdlFile);
                log?.Invoke($"DDL: Current = {currentDdl.Length} chars, Baseline = {_baselineDdl.Length} chars");

                if (currentDdl == _baselineDdl)
                    return "-- No differences found. Model has not changed since connect.";

                return ComputeDDLDiff(currentDdl, _baselineDdl, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL error: {ex.Message}");
                return $"-- Error: {ex.Message}";
            }
            finally
            {
                CleanupFile(currentDdlFile);
            }
        }

        /// <summary>
        /// Generate DDL for the active model only (no diff, no reconnect).
        /// Safe operation - does not corrupt PU.
        /// </summary>
        public static string GenerateActiveDDL(dynamic currentPU, string feOptionXml, Action<string> log)
        {
            Directory.CreateDirectory(TempDir);
            string ddlFile = Path.Combine(TempDir, "active.sql");

            try
            {
                CleanupFile(ddlFile);
                string optionArg = string.IsNullOrEmpty(feOptionXml) ? "" : feOptionXml;

                log?.Invoke("DDL: Generating DDL for active model...");
                bool result = currentPU.FEModel_DDL(ddlFile, optionArg);

                if (!result || !File.Exists(ddlFile))
                {
                    log?.Invoke("DDL: FEModel_DDL failed.");
                    return null;
                }

                string ddl = File.ReadAllText(ddlFile);
                log?.Invoke($"DDL: Generated {ddl.Length} chars, {ddl.Split('\n').Length} lines");
                return ddl;
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL error: {ex.Message}");
                return null;
            }
            finally
            {
                CleanupFile(ddlFile);
            }
        }

        #region DDL Diff

        private static string ComputeDDLDiff(string leftDdl, string rightDdl, Action<string> log)
        {
            var leftStatements = ParseStatements(leftDdl);
            var rightStatements = ParseStatements(rightDdl);

            log?.Invoke($"DDL: Left (current) has {leftStatements.Count} statements, Right (baseline) has {rightStatements.Count} statements");

            var leftByKey = BuildStatementMap(leftStatements);
            var rightByKey = BuildStatementMap(rightStatements);

            var result = new System.Text.StringBuilder();
            result.AppendLine("-- DDL Diff: Current Model vs Mart Baseline");
            result.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            result.AppendLine($"-- Current: {leftStatements.Count} statements, Baseline: {rightStatements.Count} statements");

            int addedCount = 0, droppedCount = 0, changedCount = 0;

            // NEW: In current but not in baseline
            var addedSection = new System.Text.StringBuilder();
            foreach (var kvp in leftByKey)
            {
                if (!rightByKey.ContainsKey(kvp.Key))
                {
                    addedSection.AppendLine();
                    addedSection.AppendLine($"-- NEW: {kvp.Key}");
                    addedSection.AppendLine(kvp.Value);
                    addedSection.AppendLine("go");
                    addedCount++;
                }
            }

            // DROPPED: In baseline but not in current
            var droppedSection = new System.Text.StringBuilder();
            foreach (var kvp in rightByKey)
            {
                if (!leftByKey.ContainsKey(kvp.Key))
                {
                    droppedSection.AppendLine();
                    droppedSection.AppendLine($"-- DROPPED: {kvp.Key}");
                    droppedSection.AppendLine($"-- {GenerateDropStatement(kvp.Key)}");
                    droppedCount++;
                }
            }

            // CHANGED: In both but different
            var changedSection = new System.Text.StringBuilder();
            foreach (var kvp in leftByKey)
            {
                if (rightByKey.TryGetValue(kvp.Key, out string baselineStmt))
                {
                    if (!NormalizeForCompare(kvp.Value).Equals(NormalizeForCompare(baselineStmt), StringComparison.OrdinalIgnoreCase))
                    {
                        changedSection.AppendLine();
                        changedSection.AppendLine($"-- CHANGED: {kvp.Key}");
                        changedSection.AppendLine(kvp.Value);
                        changedSection.AppendLine("go");
                        changedCount++;
                    }
                }
            }

            result.AppendLine($"-- Summary: {addedCount} new, {droppedCount} dropped, {changedCount} changed");
            result.AppendLine();

            if (addedCount > 0)
            {
                result.AppendLine("-- ========== NEW OBJECTS ==========");
                result.Append(addedSection);
            }

            if (changedCount > 0)
            {
                result.AppendLine();
                result.AppendLine("-- ========== CHANGED OBJECTS ==========");
                result.Append(changedSection);
            }

            if (droppedCount > 0)
            {
                result.AppendLine();
                result.AppendLine("-- ========== DROPPED OBJECTS ==========");
                result.Append(droppedSection);
            }

            if (addedCount == 0 && droppedCount == 0 && changedCount == 0)
            {
                result.AppendLine("-- No differences found.");
            }

            log?.Invoke($"DDL: Diff complete - {addedCount} new, {droppedCount} dropped, {changedCount} changed");
            return result.ToString();
        }

        private static List<string> ParseStatements(string ddl)
        {
            if (string.IsNullOrEmpty(ddl)) return new List<string>();
            var parts = Regex.Split(ddl, @"^\s*go\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return parts.Where(p => !string.IsNullOrWhiteSpace(p) && !p.Trim().StartsWith("--")).Select(p => p.Trim()).ToList();
        }

        private static Dictionary<string, string> BuildStatementMap(List<string> statements)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stmt in statements)
            {
                string key = GetStatementKey(stmt);
                if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
                    map[key] = stmt;
            }
            return map;
        }

        private static string GetStatementKey(string statement)
        {
            string first = statement.Split('\n')[0].Trim();

            var match = Regex.Match(first, @"CREATE\s+TABLE\s+\[?(\w+)\]?", RegexOptions.IgnoreCase);
            if (match.Success) return $"TABLE:{match.Groups[1].Value}";

            match = Regex.Match(first, @"ALTER\s+TABLE\s+\[?(\w+)\]?", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var cm = Regex.Match(statement, @"CONSTRAINT\s+\[?(\w+)\]?", RegexOptions.IgnoreCase);
                return cm.Success ? $"CONSTRAINT:{match.Groups[1].Value}.{cm.Groups[1].Value}" : $"ALTER:{match.Groups[1].Value}";
            }

            match = Regex.Match(first, @"CREATE\s+(UNIQUE\s+)?INDEX\s+\[?(\w+)\]?", RegexOptions.IgnoreCase);
            if (match.Success) return $"INDEX:{match.Groups[2].Value}";

            match = Regex.Match(first, @"CREATE\s+(\w+)\s+\[?(\w+)\]?", RegexOptions.IgnoreCase);
            if (match.Success) return $"{match.Groups[1].Value}:{match.Groups[2].Value}";

            return null;
        }

        private static string GenerateDropStatement(string key)
        {
            var parts = key.Split(':');
            if (parts.Length < 2) return $"DROP {key}";
            return $"DROP {parts[0]} [{parts[1]}]";
        }

        private static string NormalizeForCompare(string stmt)
        {
            return Regex.Replace(stmt, @"\s+", " ").Trim();
        }

        #endregion

        #region Helpers

        public static int ParseVersionFromLocator(string locator)
        {
            if (string.IsNullOrEmpty(locator)) return 1;
            var match = Regex.Match(locator, @"version=(\d+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int ver) ? ver : 1;
        }

        private static void CleanupFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch { }
        }

        #endregion
    }
}
