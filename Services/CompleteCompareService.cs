using System;
using System.Collections.Generic;
using System.IO;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Wraps SCAPI ISCPersistenceUnit::CompleteCompare.
    ///
    /// IMPORTANT: PU.Save(filePath) on a Mart model changes the PU's locator
    /// and breaks the Mart connection. This is by design (it's "Save As").
    /// After save, erwin detects session loss and reconnects from Mart.
    ///
    /// Flow:
    /// Phase 1 (user clicks CC): Save current model -> current.erwin. PU corrupts. Return.
    /// Phase 2 (after reconnect): Save Mart version -> baseline.erwin. PU corrupts again. Return.
    /// Phase 3 (after 2nd reconnect): Run CompleteCompare(current, baseline). Show results. Clean up.
    /// </summary>
    public class CompleteCompareService
    {
        private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "erwin-addin-cc");
        private static string _currentFile;
        private static string _baselineFile;
        private static string _optionSet;
        private static int _ccPhase = 0; // 0=idle, 1=current saved, 2=baseline saved

        public event Action<string> OnLog;

        /// <summary>
        /// Check if a CC operation is in progress (waiting for reconnect).
        /// </summary>
        public static bool IsInProgress => _ccPhase > 0;

        /// <summary>
        /// Current phase: 0=idle, 1=current saved (need baseline), 2=baseline saved (ready to compare)
        /// </summary>
        public static int Phase => _ccPhase;

        /// <summary>
        /// Start Phase 1: Save current (modified) model to disk.
        /// WARNING: This will corrupt the PU and trigger session loss + reconnect.
        /// </summary>
        public static bool SaveCurrentModel(dynamic pu, string optionSet, Action<string> log = null)
        {
            try
            {
                Directory.CreateDirectory(TempDir);
                _currentFile = Path.Combine(TempDir, "current.erwin");
                _optionSet = optionSet;

                CleanupFile(_currentFile);

                log?.Invoke("CompleteCompare [Phase 1]: Saving current model...");
                bool saved = pu.Save(_currentFile, "");

                if (saved && File.Exists(_currentFile))
                {
                    long size = new FileInfo(_currentFile).Length;
                    log?.Invoke($"CompleteCompare [Phase 1]: Current saved ({size / 1024} KB). Waiting for reconnect...");
                    _ccPhase = 1;
                    return true;
                }

                log?.Invoke("CompleteCompare [Phase 1]: Failed to save current model.");
                Reset();
                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"CompleteCompare [Phase 1] error: {ex.Message}");
                Reset();
                return false;
            }
        }

        /// <summary>
        /// Phase 2: Save baseline (Mart version, fresh after reconnect) to disk.
        /// WARNING: This will also corrupt the PU and trigger another reconnect.
        /// </summary>
        public static bool SaveBaselineModel(dynamic pu, Action<string> log = null)
        {
            if (_ccPhase != 1) return false;

            try
            {
                _baselineFile = Path.Combine(TempDir, "baseline.erwin");
                CleanupFile(_baselineFile);

                log?.Invoke("CompleteCompare [Phase 2]: Saving Mart baseline...");
                bool saved = pu.Save(_baselineFile, "");

                if (saved && File.Exists(_baselineFile))
                {
                    long size = new FileInfo(_baselineFile).Length;
                    log?.Invoke($"CompleteCompare [Phase 2]: Baseline saved ({size / 1024} KB). Waiting for reconnect...");
                    _ccPhase = 2;
                    return true;
                }

                log?.Invoke("CompleteCompare [Phase 2]: Failed to save baseline.");
                Reset();
                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"CompleteCompare [Phase 2] error: {ex.Message}");
                Reset();
                return false;
            }
        }

        /// <summary>
        /// Phase 3: Run CompleteCompare on saved files. Call after 2nd reconnect.
        /// Returns XLS output path or null.
        /// </summary>
        public static string RunCompare(dynamic scapi, Action<string> log = null)
        {
            if (_ccPhase != 2 || !File.Exists(_currentFile) || !File.Exists(_baselineFile))
            {
                log?.Invoke("CompleteCompare [Phase 3]: Files not ready.");
                Reset();
                return null;
            }

            string outputFile = Path.Combine(TempDir, $"cc_result_{DateTime.Now:yyyyMMdd_HHmmss}.xls");
            string optionSet = _optionSet ?? "Standard";

            try
            {
                log?.Invoke($"CompleteCompare [Phase 3]: Running compare (optionSet='{optionSet}')...");
                log?.Invoke($"  Left (current): {_currentFile} ({new FileInfo(_currentFile).Length} bytes)");
                log?.Invoke($"  Right (baseline): {_baselineFile} ({new FileInfo(_baselineFile).Length} bytes)");

                dynamic propBag = CreatePropertyBag(scapi);
                dynamic comparePU = scapi.PersistenceUnits.Create(propBag);

                try
                {
                    bool result = comparePU.CompleteCompare(
                        _currentFile,    // Left: current (user's modified version)
                        _baselineFile,   // Right: baseline (Mart saved version)
                        outputFile,
                        optionSet,
                        "P",             // Always Physical Only
                        ""
                    );

                    if (!result || !File.Exists(outputFile))
                    {
                        log?.Invoke("CompleteCompare [Phase 3]: No output generated.");
                        return null;
                    }

                    long outputSize = new FileInfo(outputFile).Length;
                    log?.Invoke($"CompleteCompare [Phase 3]: Done! Output: {outputSize} bytes");
                    return outputFile;
                }
                finally
                {
                    CleanupTempPUs(scapi, log);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"CompleteCompare [Phase 3] error: {ex.Message}");
                return null;
            }
            finally
            {
                // Clean up model files
                CleanupFile(_currentFile);
                CleanupFile(_baselineFile);
                Reset();
            }
        }

        /// <summary>
        /// Cancel/reset the CC operation.
        /// </summary>
        public static void Reset()
        {
            _ccPhase = 0;
            _currentFile = null;
            _baselineFile = null;
            _optionSet = null;
        }

        /// <summary>
        /// Clean up all temp files and PUs.
        /// </summary>
        public static void Cleanup()
        {
            Reset();
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, true);
            }
            catch { }
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

        private static void CleanupTempPUs(dynamic scapi, Action<string> log)
        {
            try
            {
                dynamic pus = scapi.PersistenceUnits;
                int count = pus.Count;
                var toRemove = new List<dynamic>();

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic pu = pus.Item(i);
                        string locator = "";
                        try { locator = pu.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }

                        if (locator.Replace('\\', '/').ToLowerInvariant().Contains("erwin-addin-cc"))
                        {
                            toRemove.Add(pu);
                        }
                    }
                    catch { }
                }

                foreach (var pu in toRemove)
                {
                    try { scapi.PersistenceUnits.Remove(pu, false); } catch { }
                }

                if (toRemove.Count > 0)
                    log?.Invoke($"CompleteCompare: Cleaned up {toRemove.Count} temp PU(s).");
            }
            catch { }
        }

        private static dynamic CreatePropertyBag(dynamic scapi)
        {
            try
            {
                Type pbType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0");
                if (pbType != null) return Activator.CreateInstance(pbType);
            }
            catch { }

            try { return scapi.ApplicationEnvironment().PropertyBag(); }
            catch { }

            return null;
        }
    }
}
