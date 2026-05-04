using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        // Selected version for diff (set by UI before calling GenerateDiff)
        private static int _selectedVersion;

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
                string modelName = currentPU.Name?.ToString() ?? "Model";
                return ComputeDDLDiff(currentDdl, _baselineDdl, log,
                    $"{modelName} (current)", $"{modelName} (connect-time baseline)");
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

        /// <summary>
        /// Try to open a Mart version directly via SCAPI with multiple locator/disposition combos.
        /// Admin project uses: PersistenceUnits.Add(martUrl, "OVM=Yes")
        /// </summary>
        /// <summary>
        /// Load version DDL via DdlHelper.exe (separate .NET process with own SCAPI instance).
        /// </summary>
        private static string LoadVersionViaDdlHelper(dynamic currentPU, int version, string feOptionXml, Action<string> log)
        {
            string outputFile = Path.Combine(TempDir, $"helper_v{version}.sql");

            try
            {
                var martInfo = GetMartConnectionInfo(log);
                if (martInfo == null) { log?.Invoke("DDL: No Mart connection info."); return null; }

                string locator = "";
                try { locator = currentPU.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }
                string modelPath = "";
                var pathMatch = Regex.Match(locator, @"Mart://Mart/(.+?)(\?|$)");
                if (pathMatch.Success) modelPath = pathMatch.Groups[1].Value;
                else modelPath = currentPU.Name?.ToString() ?? "";

                // Find DdlHelper.exe (search multiple locations)
                string asmDir = Path.GetDirectoryName(typeof(DdlGenerationService).Assembly.Location) ?? "";
                string helperExe = "";

                string[] searchPaths = {
                    Path.Combine(asmDir, "tools", "DdlHelper", "DdlHelper.exe"),
                    Path.Combine(AppContext.BaseDirectory, "tools", "DdlHelper", "DdlHelper.exe"),
                    Path.Combine(asmDir, "..", "tools", "DdlHelper", "DdlHelper.exe"),
                };

                foreach (var p in searchPaths)
                {
                    string full = Path.GetFullPath(p);
                    if (File.Exists(full)) { helperExe = full; break; }
                }

                if (string.IsNullOrEmpty(helperExe))
                {
                    log?.Invoke($"DDL: DdlHelper.exe not found. Searched in: {asmDir}");
                    return null;
                }

                CleanupFile(outputFile);

                string args = $"--server={martInfo.Value.host} --port={martInfo.Value.port} " +
                    $"--user={martInfo.Value.username} --pass={martInfo.Value.password} " +
                    $"--model={modelPath} --version={version} --output=\"{outputFile}\"";

                // FE Option XML is NOT passed to helper - it uses erwin defaults
                // CC Option XML != FE Option XML (different formats, causes "not compatible" error)

                log?.Invoke($"DDL: Running DdlHelper (separate process)...");
                log?.Invoke($"DDL: {helperExe}");

                var psi = new ProcessStartInfo
                {
                    FileName = helperExe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(120000);

                    foreach (var line in (stdout ?? "").Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            log?.Invoke($"DDL: [Helper] {line.Trim()}");
                    if (!string.IsNullOrEmpty(stderr))
                        log?.Invoke($"DDL: [Helper ERR] {stderr.Trim()}");

                    if (proc.ExitCode != 0)
                    {
                        log?.Invoke($"DDL: DdlHelper exit code = {proc.ExitCode}");
                        return null;
                    }
                }

                if (File.Exists(outputFile))
                {
                    string ddl = File.ReadAllText(outputFile);
                    log?.Invoke($"DDL: v{version} DDL via helper = {ddl.Length} chars");
                    return ddl;
                }

                return null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: DdlHelper error: {ex.Message}");
                return null;
            }
            finally
            {
                CleanupFile(outputFile);
            }
        }

        /// <summary>
        /// Finds DdlHelper.exe in known locations.
        /// </summary>
        private static string FindDdlHelperExe(Action<string> log)
        {
            string asmDir = Path.GetDirectoryName(typeof(DdlGenerationService).Assembly.Location) ?? "";
            string[] searchPaths = {
                Path.Combine(asmDir, "tools", "DdlHelper", "DdlHelper.exe"),
                Path.Combine(AppContext.BaseDirectory, "tools", "DdlHelper", "DdlHelper.exe"),
                Path.Combine(asmDir, "..", "tools", "DdlHelper", "DdlHelper.exe"),
            };

            foreach (var p in searchPaths)
            {
                string full = Path.GetFullPath(p);
                if (File.Exists(full)) return full;
            }

            log?.Invoke($"DDL: DdlHelper.exe not found. Searched in: {asmDir}");
            return null;
        }

        private static readonly string LogFile = Path.Combine(
            Path.GetTempPath(), "erwin-addin-ddl", "ddl_attempts.log");

        private static void FileLog(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
            }
            catch { }
        }

        public static string TryOpenMartVersionDirectly(dynamic scapi, dynamic currentPU, int version, string feOptionXml, Action<string> log)
        {
            Directory.CreateDirectory(TempDir);
            string ddlFile = Path.Combine(TempDir, $"direct_v{version}.sql");

            // Clear log file
            try { File.WriteAllText(LogFile, $"=== DDL Attempt Log {DateTime.Now} ===\r\n"); } catch { }

            var martInfo = GetMartConnectionInfo(log);

            string locator = "";
            try { locator = currentPU.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }
            string modelPath = "";
            var pathMatch = Regex.Match(locator, @"Mart://Mart/(.+?)(\?|$)");
            if (pathMatch.Success) modelPath = pathMatch.Groups[1].Value;
            else modelPath = currentPU.Name?.ToString() ?? "";

            FileLog($"Model: {modelPath}, Version: {version}");
            FileLog($"Current locator: {locator}");
            log?.Invoke($"DDL: Attempts logged to: {LogFile}");

            // Get modelLongId from locator
            string modelLongId = "";
            var longIdMatch = Regex.Match(locator, @"modelLongId=([^;]+)");
            if (longIdMatch.Success) modelLongId = longIdMatch.Groups[1].Value;
            FileLog($"modelLongId: {modelLongId}");

            // Build attempts - SAFEST FIRST, RISKIEST LAST
            var attempts = new List<(string url, string disposition, string desc)>();

            // GROUP 1: Known safe (return errors, don't crash)
            attempts.Add(($"mart://Mart/{modelPath}?VNO={version}", "", "mart VNO + empty"));
            attempts.Add(($"mart://Mart/{modelPath}?VNO={version}", "RDO=Yes", "mart VNO + RDO"));
            attempts.Add(($"mart://Mart/{modelPath}?VNO={version}", "OVM=Yes", "mart VNO + OVM"));

            // GROUP 2: Add Mart ModelDirectory FIRST, then open model
            // Admin project does: ModelDirectories.Add(martLocator) THEN PersistenceUnits.Add
            // We never tried this! ModelDirectories only has FileSystem, not Mart.
            if (martInfo != null)
            {
                string martDirLocator = $"mart://Mart?TRC=NO;SRV={martInfo.Value.host};PRT={martInfo.Value.port};ASR=MartServer;UID={martInfo.Value.username};PSW={martInfo.Value.password}";
                string martModelUrl = $"mart://Mart/{modelPath}?TRC=NO;SRV={martInfo.Value.host};PRT={martInfo.Value.port};ASR=MartServer;UID={martInfo.Value.username};PSW={martInfo.Value.password};VNO={version}";

                // Try adding Mart directory first
                try
                {
                    string maskedDir = Regex.Replace(martDirLocator, @"PSW=[^;]*", "PSW=***");
                    FileLog($"Adding Mart ModelDirectory: {maskedDir}");
                    log?.Invoke($"DDL: Adding Mart directory...");
                    scapi.ModelDirectories.Add(martDirLocator, "");
                    FileLog("Mart ModelDirectory added!");

                    // Now try opening model
                    string maskedModel = Regex.Replace(martModelUrl, @"PSW=[^;]*", "PSW=***");
                    attempts.Add((martModelUrl, "OVM=Yes", "After MartDir + OVM"));
                    attempts.Add((martModelUrl, "RDO=Yes", "After MartDir + RDO"));
                    attempts.Add((martModelUrl, "", "After MartDir + empty"));
                }
                catch (Exception ex)
                {
                    FileLog($"ModelDirectories.Add failed: {ex.Message}");
                    log?.Invoke($"DDL: ModelDirectories.Add failed: {ex.Message}");
                }
            }

            dynamic versionPU = null;
            int attemptNum = 0;

            foreach (var (url, disp, desc) in attempts)
            {
                attemptNum++;
                string maskedUrl = Regex.Replace(url, @"PSW=[^;]*", "PSW=***");
                string msg = $"Attempt {attemptNum}/{attempts.Count} [{desc}]: {maskedUrl} disp='{disp}'";

                FileLog($">>> {msg}");
                log?.Invoke($"DDL: {msg}");

                try
                {
                    versionPU = scapi.PersistenceUnits.Add(url, disp);

                    string successMsg = $"SUCCESS! v{version} opened with [{desc}]";
                    FileLog(successMsg);
                    log?.Invoke($"DDL: {successMsg}");
                    break;
                }
                catch (Exception ex)
                {
                    string errMsg = $"FAILED: {ex.Message}";
                    FileLog(errMsg);
                    log?.Invoke($"DDL: {errMsg}");
                    versionPU = null;
                }
            }

            if (versionPU == null)
            {
                FileLog("All attempts failed.");
                log?.Invoke("DDL: All direct open attempts failed. See: " + LogFile);
                return null;
            }

            try
            {
                CleanupFile(ddlFile);
                FileLog("Generating DDL via FEModel_DDL...");
                versionPU.FEModel_DDL(ddlFile, string.IsNullOrEmpty(feOptionXml) ? "" : feOptionXml);

                if (File.Exists(ddlFile))
                {
                    string ddl = File.ReadAllText(ddlFile);
                    FileLog($"DDL generated: {ddl.Length} chars");
                    return ddl;
                }
                return null;
            }
            finally
            {
                if (versionPU != null)
                {
                    try { scapi.PersistenceUnits.Remove(versionPU, false); }
                    catch (Exception ex) { FileLog($"PU cleanup: {ex.Message}"); }
                }
                CleanupFile(ddlFile);
            }
        }

        /// <summary>
        /// Load a specific Mart version's DDL via a SEPARATE process (VBScript + cscript.exe).
        /// Separate process = separate SCAPI instance = own Mart connection = no "UI active" error.
        /// erwin's own FEModel_DDL generates the DDL.
        /// </summary>
        private static string LoadVersionDDLExternal(int version, dynamic currentPU, string feOptionXml, Action<string> log)
        {
            string outputFile = Path.Combine(TempDir, $"v{version}.sql");
            string vbsFile = Path.Combine(TempDir, "load_version.vbs");

            try
            {
                var martInfo = GetMartConnectionInfo(log);
                if (martInfo == null) { log?.Invoke("DDL: No Mart connection info."); return null; }

                string locator = "";
                try { locator = currentPU.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }

                string modelPath = "";
                var pathMatch = Regex.Match(locator, @"Mart://Mart/(.+?)(\?|$)");
                if (pathMatch.Success) modelPath = pathMatch.Groups[1].Value;
                else modelPath = currentPU.Name?.ToString() ?? "";

                string martUrl = $"mart://Mart/{modelPath}?TRC=NO;SRV={martInfo.Value.host};PRT={martInfo.Value.port};ASR=MartServer;UID={martInfo.Value.username};PSW={martInfo.Value.password};VER={version}";

                CleanupFile(outputFile);
                CleanupFile(vbsFile);

                string feArg = string.IsNullOrEmpty(feOptionXml) ? "\"\"" : $"\"{feOptionXml.Replace("\\", "\\\\")}\"";
                string outputVbs = outputFile.Replace("\\", "\\\\");

                string vbs = $@"
On Error Resume Next

WScript.Echo ""SCAPI: Creating instance...""
Dim oApi
Set oApi = CreateObject(""ERwin9.SCAPI.9.0"")
If Err.Number <> 0 Then
    WScript.Echo ""ERROR: SCAPI create failed: "" & Err.Number & "" "" & Err.Description
    WScript.Quit 1
End If
WScript.Echo ""SCAPI: OK. Version="" & oApi.Version

WScript.Echo ""MART: Connecting...""
WScript.Echo ""URL: {martUrl.Replace("PSW=" + martInfo.Value.password, "PSW=***")}""

Dim oPU
Set oPU = oApi.PersistenceUnits.Add(""{martUrl}"", ""OVM=Yes"")
If Err.Number <> 0 Then
    WScript.Echo ""ERROR: PU.Add failed: "" & Err.Number & "" "" & Err.Description
    WScript.Quit 2
End If
WScript.Echo ""MODEL: Opened "" & oPU.Name

WScript.Echo ""DDL: Generating to {outputVbs}""
Call oPU.FEModel_DDL(""{outputVbs}"", {feArg})
If Err.Number <> 0 Then
    WScript.Echo ""ERROR: FEModel_DDL failed: "" & Err.Number & "" "" & Err.Description
    oApi.PersistenceUnits.Remove oPU, False
    WScript.Quit 3
End If

WScript.Echo ""CLEANUP: Removing PU...""
oApi.PersistenceUnits.Remove oPU, False
WScript.Echo ""DONE""
WScript.Quit 0
";

                File.WriteAllText(vbsFile, vbs);
                log?.Invoke($"DDL: VBScript created. Mart={martInfo.Value.host}:{martInfo.Value.port}, Path={modelPath}");

                // Log the mart URL (mask password) for debugging
                string maskedUrl = Regex.Replace(martUrl, @"PSW=[^;]*", "PSW=***");
                log?.Invoke($"DDL: Mart URL = {maskedUrl}");

                // Use 64-bit cscript (erwin SCAPI is 64-bit)
                string cscriptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe");
                if (!File.Exists(cscriptPath)) cscriptPath = "cscript.exe";

                var psi = new ProcessStartInfo
                {
                    FileName = cscriptPath,
                    Arguments = $"//NoLogo \"{vbsFile}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                log?.Invoke($"DDL: Running: {cscriptPath} //NoLogo \"{vbsFile}\"");

                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(120000);

                    foreach (var line in (stdout ?? "").Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            log?.Invoke($"DDL: [VBS] {line.Trim()}");
                    if (!string.IsNullOrEmpty(stderr))
                        log?.Invoke($"DDL: [VBS ERR] {stderr.Trim()}");

                    if (proc.ExitCode != 0)
                    {
                        log?.Invoke($"DDL: VBScript exit code = {proc.ExitCode} (0x{((uint)proc.ExitCode):X8})");
                        return null;
                    }
                }

                if (File.Exists(outputFile))
                {
                    string ddl = File.ReadAllText(outputFile);
                    log?.Invoke($"DDL: v{version} DDL loaded ({ddl.Length} chars, {ddl.Split('\n').Length} lines)");
                    return ddl;
                }

                return null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: LoadVersionDDLExternal error: {ex.Message}");
                return null;
            }
            finally
            {
                CleanupFile(vbsFile);
                CleanupFile(outputFile);
            }
        }

        /// <summary>
        /// Run erwin's CompleteCompare between two Mart versions using a SEPARATE process.
        /// Each process creates its own SCAPI instance with its own Mart connection,
        /// bypassing the "Mart user interface is active" restriction.
        ///
        /// VBScript flow:
        /// 1. Connect to Mart
        /// 2. Open version A -> save to left.erwin
        /// 3. Open version B -> save to right.erwin
        /// 4. Create empty PU -> CompleteCompare(left, right, output.xls, option, "P", "")
        /// 5. Clean up
        ///
        /// Returns the HTML content of the CompleteCompare output, or null on failure.
        /// </summary>
        private static string RunCompleteCompareViaExternalProcess(
            int leftVersion, int rightVersion, dynamic currentPU, string ccOptionSet, Action<string> log)
        {
            string leftFile = Path.Combine(TempDir, "cc_left.erwin");
            string rightFile = Path.Combine(TempDir, "cc_right.erwin");
            string outputFile = Path.Combine(TempDir, "cc_output.xls");
            string vbsFile = Path.Combine(TempDir, "complete_compare.vbs");

            try
            {
                var martInfo = GetMartConnectionInfo(log);
                if (martInfo == null)
                {
                    log?.Invoke("DDL: Cannot get Mart connection info.");
                    return null;
                }

                string locator = "";
                try { locator = currentPU.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }

                string modelPath = "";
                var pathMatch = Regex.Match(locator, @"Mart://Mart/(.+?)(\?|$)");
                if (pathMatch.Success)
                    modelPath = pathMatch.Groups[1].Value;
                else
                    modelPath = currentPU.Name?.ToString() ?? "";

                log?.Invoke($"DDL: CompleteCompare v{leftVersion} vs v{rightVersion}");
                log?.Invoke($"DDL: Mart = {martInfo.Value.host}:{martInfo.Value.port}, Path = {modelPath}");

                CleanupFile(leftFile);
                CleanupFile(rightFile);
                CleanupFile(outputFile);
                CleanupFile(vbsFile);

                string martBase = $"TRC=NO;SRV={martInfo.Value.host};PRT={martInfo.Value.port};ASR=MartServer;UID={martInfo.Value.username};PSW={martInfo.Value.password}";
                string leftUrl = $"mart://Mart/{modelPath}?{martBase};VER={leftVersion}";
                string rightUrl = $"mart://Mart/{modelPath}?{martBase};VER={rightVersion}";

                string optionArg = string.IsNullOrEmpty(ccOptionSet) ? "\"Standard\"" : $"\"{ccOptionSet}\"";

                // Escape backslashes for VBScript string literals
                string leftFileVbs = leftFile.Replace("\\", "\\\\");
                string rightFileVbs = rightFile.Replace("\\", "\\\\");
                string outputFileVbs = outputFile.Replace("\\", "\\\\");

                string vbs = $@"
On Error Resume Next

Dim oApi
Set oApi = CreateObject(""ERwin9.SCAPI.9.0"")
If Err.Number <> 0 Then
    WScript.Echo ""ERROR: SCAPI: "" & Err.Description
    WScript.Quit 1
End If

' Open left model (version {leftVersion})
WScript.Echo ""Opening v{leftVersion}...""
Dim oLeft
Set oLeft = oApi.PersistenceUnits.Add(""{leftUrl}"", ""OVM=Yes"")
If Err.Number <> 0 Then
    WScript.Echo ""ERROR: Left model: "" & Err.Description
    WScript.Quit 2
End If
WScript.Echo ""Saving left to file...""
oLeft.Save ""{leftFileVbs}"", """"
oApi.PersistenceUnits.Remove oLeft, False

' Open right model (version {rightVersion})
Err.Clear
WScript.Echo ""Opening v{rightVersion}...""
Dim oRight
Set oRight = oApi.PersistenceUnits.Add(""{rightUrl}"", ""OVM=Yes"")
If Err.Number <> 0 Then
    WScript.Echo ""ERROR: Right model: "" & Err.Description
    WScript.Quit 3
End If
WScript.Echo ""Saving right to file...""
oRight.Save ""{rightFileVbs}"", """"
oApi.PersistenceUnits.Remove oRight, False

' Run CompleteCompare
Err.Clear
WScript.Echo ""Running CompleteCompare...""
Dim oPropBag
Set oPropBag = CreateObject(""ERwin9.SCAPI.PropertyBag.9.0"")
Dim oCompare
Set oCompare = oApi.PersistenceUnits.Create(oPropBag)
Dim bResult
bResult = oCompare.CompleteCompare(""{leftFileVbs}"", ""{rightFileVbs}"", ""{outputFileVbs}"", {optionArg}, ""P"", """")
If Err.Number <> 0 Then
    WScript.Echo ""ERROR: CompleteCompare: "" & Err.Description
    oApi.PersistenceUnits.Remove oCompare, False
    WScript.Quit 4
End If

oApi.PersistenceUnits.Remove oCompare, False
WScript.Echo ""CompleteCompare done. Result="" & bResult
WScript.Quit 0
";

                File.WriteAllText(vbsFile, vbs);
                log?.Invoke("DDL: VBScript created. Launching cscript.exe...");

                var psi = new ProcessStartInfo
                {
                    FileName = "cscript.exe",
                    Arguments = $"//NoLogo \"{vbsFile}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(120000); // 2 min timeout

                    if (!string.IsNullOrEmpty(stdout))
                    {
                        foreach (var line in stdout.Split('\n'))
                            if (!string.IsNullOrWhiteSpace(line))
                                log?.Invoke($"DDL: [VBS] {line.Trim()}");
                    }
                    if (!string.IsNullOrEmpty(stderr))
                        log?.Invoke($"DDL: [VBS ERR] {stderr.Trim()}");

                    if (proc.ExitCode != 0)
                    {
                        log?.Invoke($"DDL: VBScript exit code = {proc.ExitCode}");
                        return null;
                    }
                }

                if (File.Exists(outputFile))
                {
                    string html = File.ReadAllText(outputFile);
                    log?.Invoke($"DDL: CompleteCompare output = {html.Length} chars");
                    return html;
                }

                log?.Invoke("DDL: Output file not found.");
                return null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: External CC error: {ex.Message}");
                return null;
            }
            finally
            {
                CleanupFile(vbsFile);
                CleanupFile(leftFile);
                CleanupFile(rightFile);
                // Keep outputFile for "Open XLS" button
            }
        }

        /// <summary>
        /// Get Mart Server connection info from CONNECTION_DEF ID=4.
        /// No Portal DB dependency - reads from MetaRepo only.
        /// </summary>
        internal static (string host, string port, string username, string password)? GetMartConnectionInfo(Action<string> log)
        {
            try
            {
                if (!DatabaseService.Instance.IsConfigured) return null;

                var config = new RegistryBootstrapService().GetConfig();
                if (config == null) return null;

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();

                    string dbType = config.DbType?.ToUpper() ?? "UNKNOWN";
                    log?.Invoke($"DDL: GetMartConnectionInfo dbType={dbType}");

                    // Debug: list all CONNECTION_DEF entries
                    try
                    {
                        string listQuery = dbType.Contains("POSTGRES")
                            ? @"SELECT ""ID"", ""DB_TYPE"", ""HOST"", ""PORT"" FROM ""CONNECTION_DEF"" ORDER BY ""ID"""
                            : @"SELECT ID, DB_TYPE, HOST, PORT FROM CONNECTION_DEF ORDER BY ID";
                        using (var listCmd = DatabaseService.Instance.CreateCommand(listQuery, conn))
                        using (var listReader = listCmd.ExecuteReader())
                        {
                            while (listReader.Read())
                            {
                                log?.Invoke($"DDL: CONNECTION_DEF ID={listReader["ID"]}, DB_TYPE='{listReader["DB_TYPE"]}', HOST='{listReader["HOST"]}', PORT='{listReader["PORT"]}'");
                            }
                        }
                    }
                    catch (Exception ex) { log?.Invoke($"DDL: CONNECTION_DEF list error: {ex.Message}"); }

                    string query = dbType.Contains("POSTGRES")
                        ? @"SELECT ""HOST"", ""PORT"", ""USERNAME"", ""PASSWORD"" FROM ""CONNECTION_DEF"" WHERE ""DB_TYPE"" = 'MART_API' ORDER BY ""ID"" LIMIT 1"
                        : @"SELECT TOP 1 HOST, PORT, USERNAME, PASSWORD FROM CONNECTION_DEF WHERE DB_TYPE = 'MART_API' ORDER BY ID";

                    log?.Invoke($"DDL: Query = {query}");
                    using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            string host = reader["HOST"]?.ToString()?.Trim() ?? "";
                            string port = reader["PORT"]?.ToString()?.Trim() ?? "";
                            log?.Invoke($"DDL: Mart Server = {host}:{port}");
                            string encUser = reader["USERNAME"]?.ToString()?.Trim() ?? "";
                            string encPass = reader["PASSWORD"]?.ToString()?.Trim() ?? "";

                            string user = PasswordEncryptionService.Decrypt(encUser);
                            string pass = PasswordEncryptionService.Decrypt(encPass);

                            if (string.IsNullOrEmpty(user) || (user.Length > 50 && user == encUser))
                            {
                                user = config.Username;
                                pass = config.Password;
                            }

                            return (host, port, user, pass);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: GetMartConnectionInfo error: {ex.Message}");
            }

            return null;
        }

        public static void SetSelectedVersion(int version)
        {
            _selectedVersion = version;
        }

        public static void ClearBaseline()
        {
            _baselineDdl = null;
        }

        /// <summary>
        /// Generate DDL diff using two open PUs.
        /// If 2+ PUs are open (user opened another version in erwin), compare them.
        /// If only 1 PU, fall back to connect-time baseline.
        /// Both DDLs generated by erwin's FEModel_DDL - safe, no corruption.
        /// </summary>
        public static string GenerateDiffWithDuplicate(dynamic scapi, dynamic currentPU, string feOptionXml, Action<string> log)
        {
            Directory.CreateDirectory(TempDir);
            string currentDdlFile = Path.Combine(TempDir, "current.sql");
            string versionDdlFile = Path.Combine(TempDir, "version.sql");
            string optionArg = string.IsNullOrEmpty(feOptionXml) ? "" : feOptionXml;

            try
            {
                int selectedVer = _selectedVersion;
                int currentVer = ParseVersionFromLocator(
                    currentPU.PropertyBag().Value("Locator")?.ToString() ?? "");

                log?.Invoke($"DDL: Current version = v{currentVer}, Selected version = v{selectedVer}");

                // Step 1: Generate DDL for current model (in-process, safe)
                CleanupFile(currentDdlFile);
                log?.Invoke("DDL: Generating current model DDL (FEModel_DDL)...");
                bool r1 = currentPU.FEModel_DDL(currentDdlFile, optionArg);
                if (!r1 || !File.Exists(currentDdlFile))
                {
                    log?.Invoke("DDL: FEModel_DDL failed for current model.");
                    return null;
                }
                string currentDdl = File.ReadAllText(currentDdlFile);
                log?.Invoke($"DDL: Current DDL = {currentDdl.Length} chars, {currentDdl.Split('\n').Length} lines");

                // Step 2: Get selected version's DDL
                string versionDdl = null;

                if (selectedVer > 0)
                {
                    // Try: version already open as a DIFFERENT PU? (skip if same version = active model)
                    int puCount = scapi.PersistenceUnits.Count;
                    if (puCount >= 2 && selectedVer != currentVer)
                    {
                        for (int i = 0; i < puCount; i++)
                        {
                            try
                            {
                                dynamic pu = scapi.PersistenceUnits.Item(i);
                                int puVer = ParseVersionFromLocator(pu.PropertyBag().Value("Locator")?.ToString() ?? "");
                                if (puVer == selectedVer)
                                {
                                    log?.Invoke($"DDL: v{selectedVer} already open as PU[{i}]. Generating DDL...");
                                    CleanupFile(versionDdlFile);
                                    try
                                    {
                                        bool feResult = pu.FEModel_DDL(versionDdlFile, optionArg);
                                        log?.Invoke($"DDL: FEModel_DDL on PU[{i}] returned {feResult}");
                                        if (feResult && File.Exists(versionDdlFile))
                                        {
                                            versionDdl = File.ReadAllText(versionDdlFile);
                                            log?.Invoke($"DDL: v{selectedVer} DDL from PU = {versionDdl.Length} chars");
                                        }
                                    }
                                    catch (Exception feEx)
                                    {
                                        log?.Invoke($"DDL: FEModel_DDL on PU[{i}] error: {feEx.Message}");
                                    }
                                    break;
                                }
                            }
                            catch (Exception puEx) { log?.Invoke($"DDL: PU[{i}] access error: {puEx.Message}"); }
                        }
                    }

                    // Try: DdlHelper external process (.NET, own SCAPI instance)
                    if (string.IsNullOrEmpty(versionDdl))
                    {
                        versionDdl = LoadVersionViaDdlHelper(currentPU, selectedVer, optionArg, log);
                    }

                    // Fallback: Try SCAPI directly (usually fails with "Mart UI active")
                    if (string.IsNullOrEmpty(versionDdl))
                    {
                        versionDdl = TryOpenMartVersionDirectly(scapi, currentPU, selectedVer, optionArg, log);
                    }
                }

                // Step 3: Diff
                if (!string.IsNullOrEmpty(versionDdl))
                {
                    log?.Invoke($"DDL: v{selectedVer} DDL = {versionDdl.Length} chars");
                    if (currentDdl == versionDdl)
                        return $"-- No differences between v{currentVer} and v{selectedVer}.";
                    string mName = currentPU.Name?.ToString() ?? "Model";
                    return ComputeDDLDiff(currentDdl, versionDdl, log,
                        $"{mName} v{currentVer} (current)", $"{mName} v{selectedVer} (Mart)");
                }

                log?.Invoke("DDL: Could not get version DDL.");
                return "-- ERROR: Mart Server connection not found in CONNECTION_DEF.\n-- Please configure Mart Server connection in Admin panel (Connections section).\n-- DB_TYPE should be 'MART' or DB_SCHEMA should contain 'Mart'.";
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL error: {ex.Message}");
                return $"-- Error: {ex.Message}";
            }
            finally
            {
                CleanupFile(currentDdlFile);
                CleanupFile(versionDdlFile);
            }
        }

        /// <summary>
        /// Silently reverse-engineer a live database into a NEW persistence unit in the
        /// current SCAPI session and keep it loaded. Returns the new PU, which becomes
        /// available as a selectable right-side model (e.g. for the Alter Script Wizard).
        /// </summary>
        public static dynamic ReverseEngineerToSession(
            dynamic scapi,
            dynamic currentPU,
            string host,
            string database,
            string user,
            string password,
            bool useWindowsAuth,
            int dbTypeCode,
            long targetServerCode,
            int targetServerVersion,
            string schema,
            IEnumerable<string> selectedTables,
            Action<string> log,
            string modelType = "Combined")
        {
            string tempDsn = null;
            string tempReOptionXml = null;
            System.Data.IDbConnection sqlConnLong = null;
            try
            {
                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database))
                    throw new InvalidOperationException("DB host/database missing. Click 'Configure DB' first.");

                // Normalize table names: strip schema prefix if present.
                // DbTableBrowserService surfaces tables as "schema.name" so
                // the picker is unambiguous, but RE's Synch_Table_Filter_By_Name
                // expects bare table names (schema is already controlled by
                // Synch_Owned_Only_Name). Without this strip, an empty PU
                // results from the filter never matching.
                var tableList = (selectedTables ?? new string[0])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Select(t =>
                    {
                        int dot = t.IndexOf('.');
                        return dot > 0 ? t.Substring(dot + 1) : t;
                    })
                    .ToList();
                if (tableList.Count == 0)
                    throw new InvalidOperationException("No tables selected for comparison.");
                if (targetServerCode == 0)
                    throw new InvalidOperationException("Target server code not provided.");

                log?.Invoke($"RE: DB = {host}/{database}, {tableList.Count} table(s), schema='{schema}'");
                log?.Invoke($"RE: Synch_Table_Filter_By_Name = '{string.Join(",", tableList)}'");

                // Open SQL connection for XML_OPTION lookup
                sqlConnLong = OpenSqlConnectionForXmlOption(host, database, user, password, useWindowsAuth, log);
                // After the MODEL→CONFIG schema rename, the XML_OPTION row is keyed on
                // CONFIG_ID. ConfigContextService already resolved the active config from
                // the live model's mart locator at addin startup, so we pass that directly
                // instead of re-deriving it from the MODEL_PATH UDP + MODEL.PATH lookup.
                int? activeConfigId = ConfigContextService.Instance.IsInitialized
                    ? ConfigContextService.Instance.ActiveConfigId
                    : (int?)null;

                // ODBC driver preflight + transient DSN
                var drvCheck = OdbcDsnHelper.CheckSqlServerDriver(log);
                if (!drvCheck.IsOk) throw new InvalidOperationException(drvCheck.UserMessage);
                OdbcDsnHelper.CleanupStale(log);
                tempDsn = OdbcDsnHelper.CreateTempSqlServerDsn(host, database, user, log);

                tempReOptionXml = XmlOptionLoaderService.LoadAndWriteToTempFile(sqlConnLong, activeConfigId, "RE", log);

                // Create blank PU for RE
                Type pbType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0");
                if (pbType == null) throw new InvalidOperationException("SCAPI PropertyBag ProgID not registered.");
                dynamic propBag = Activator.CreateInstance(pbType);
                propBag.Add("Model_Type", modelType);
                propBag.Add("Target_Server", (int)targetServerCode);
                propBag.Add("Target_Server_Version", targetServerVersion);
                dynamic rePU = scapi.PersistenceUnits.Create(propBag);
                log?.Invoke($"RE: blank PU created — {rePU.Name} (Model_Type={modelType})");

                // Set RE keys
                propBag.ClearAll();
                propBag.Add("System_Objects", false);
                propBag.Add("Oracle_Use_DBA_Views", false);
                propBag.Add("Synch_Owned_Only", !string.IsNullOrEmpty(schema));
                propBag.Add("Synch_Owned_Only_Name", schema ?? "");
                // Case_Option: 25090=None(preserve), 25091=lower, 25092=Upper
                // (SCAPI doc line 6778). 25091 lowercase'e ceviriyordu ->
                // mart UPPERCASE model ile case-mismatch -> CC compare
                // 819 item false-diff (RD'de "dbo.APPROVEMENT_DEF" vs
                // "dbo.approvement_def" iki ayri obje gibi). 25090 ile
                // DB'deki orijinal case korunur, mart model ile match.
                propBag.Add("Case_Option", 25090);
                propBag.Add("Logical_Case_Option", 25045);
                propBag.Add("Infer_Primary_Keys", false);
                propBag.Add("Infer_Relations", false);
                propBag.Add("Infer_Relations_Indexes", false);
                propBag.Add("Remove_ERwin_Generated_Triggers", false);
                propBag.Add("Force_Physical_Name_Option", false);
                propBag.Add("Synch_Table_Filter_By_Name", string.Join(",", tableList));

                int authCode = useWindowsAuth ? 8 : 4;
                string connStr =
                    $"SERVER={dbTypeCode}:{targetServerVersion}:0|" +
                    $"AUTHENTICATION={authCode}|" +
                    $"USER={user}|1=2|5={tempDsn}";
                string rePassword = useWindowsAuth ? "" : (password ?? "");
                object reOptArg = string.IsNullOrEmpty(tempReOptionXml) ? (object)Type.Missing : tempReOptionXml;

                long t0 = Environment.TickCount64;
                object reResult = rePU.ReverseEngineer(propBag, reOptArg, connStr, rePassword);
                long elapsed = Environment.TickCount64 - t0;
                log?.Invoke($"RE: ReverseEngineer returned {(reResult ?? "<null>")} in {elapsed}ms");
                return rePU;
            }
            finally
            {
                try { sqlConnLong?.Close(); sqlConnLong?.Dispose(); } catch { }
                try { if (tempDsn != null) OdbcDsnHelper.DeleteDsn(tempDsn, log); } catch { }
                try { if (!string.IsNullOrEmpty(tempReOptionXml) && File.Exists(tempReOptionXml)) File.Delete(tempReOptionXml); } catch { }
            }
        }

        /// <summary>
        /// Compare active model with a live database via IN-PROCESS silent ReverseEngineer.
        /// Steps:
        ///   1) FEModel_DDL on active PU -> currentDdl
        ///   2) Create transient User ODBC DSN (HKCU) so SCAPI's RE can connect
        ///   3) Resolve REoption XML from DB XML_OPTION (per-model -> All Projects -> embedded)
        ///   4) PersistenceUnits.Create(propBag) + ClearAll + RE keys
        ///   5) pu.ReverseEngineer(propBag, REoptionPath, connStr, password)
        ///   6) FEModel_DDL on the RE'd PU -> reverseDdl
        ///   7) ComputeDDLDiff(currentDdl, reverseDdl) — same engine as the Mart flow
        ///   8) Cleanup: PU.Remove + DSN delete + temp XML delete
        ///
        /// Required by SCAPI r10 (verified): legacy "SQL Server" ODBC driver, AUTHENTICATION=4 (SQL)
        /// or 8 (Windows), connStr `1=2|5=<DSN>`, REoption file path (not Type.Missing in production).
        /// </summary>
        public static string GenerateDiffWithDatabase(
            dynamic scapi,
            dynamic currentPU,
            string host,
            string database,
            string user,
            string password,
            bool useWindowsAuth,
            int dbTypeCode,
            long targetServerCode,
            int targetServerVersion,
            string schema,
            IEnumerable<string> selectedTables,
            string dbLabel,
            string feOptionXml,
            Action<string> log)
        {
            Directory.CreateDirectory(TempDir);
            string currentDdlFile = Path.Combine(TempDir, "current.sql");
            string dbDdlFile = Path.Combine(TempDir, "db_re.sql");
            string optionArg = string.IsNullOrEmpty(feOptionXml) ? "" : feOptionXml;

            string tempDsn = null;
            string tempReOptionXml = null;
            string tempDdlOptionXml = null;
            dynamic rePU = null;
            System.Data.IDbConnection sqlConnLong = null;

            try
            {
                // Validation
                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database))
                    return "-- ERROR: DB host/database missing. Click 'Configure DB' first.";
                var tableList = (selectedTables ?? new string[0])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .ToList();
                if (tableList.Count == 0)
                    return "-- ERROR: No tables selected for comparison.";
                if (targetServerCode == 0)
                    return "-- ERROR: Target server code not provided.";

                log?.Invoke($"DDL: From DB compare. {tableList.Count} table(s), schema='{schema}', target={targetServerCode} v{targetServerVersion}");
                log?.Invoke($"DDL: DB = {dbLabel}, host={host}, db={database}, user={user}, winAuth={useWindowsAuth}");

                // Step 0: open SQL connection ONCE for both XML_OPTION lookups (RE + DDL).
                sqlConnLong = OpenSqlConnectionForXmlOption(host, database, user, password, useWindowsAuth, log);
                // After the MODEL→CONFIG schema rename, the XML_OPTION row is keyed on
                // CONFIG_ID. ConfigContextService already resolved the active config from
                // the live model's mart locator at addin startup, so we pass that directly
                // instead of re-deriving it from the MODEL_PATH UDP + MODEL.PATH lookup.
                int? activeConfigId = ConfigContextService.Instance.IsInitialized
                    ? ConfigContextService.Instance.ActiveConfigId
                    : (int?)null;

                // If user did NOT pass a feOption path, resolve TYPE='DDL' from XML_OPTION and use
                // the SAME XML for BOTH active-model and RE-d model FEModel_DDL calls so triggers/options
                // don't create false diffs (e.g. triggers on one side but not the other).
                if (string.IsNullOrEmpty(optionArg))
                {
                    tempDdlOptionXml = XmlOptionLoaderService.LoadAndWriteToTempFile(sqlConnLong, activeConfigId, "DDL", log);
                    if (!string.IsNullOrEmpty(tempDdlOptionXml))
                    {
                        optionArg = tempDdlOptionXml;
                        log?.Invoke($"DDL: using FE Option XML (TYPE='DDL') = {tempDdlOptionXml}");
                    }
                }

                // Step 1: active model DDL (in-process)
                CleanupFile(currentDdlFile);
                log?.Invoke("DDL: Generating current model DDL (FEModel_DDL)...");
                bool r1 = currentPU.FEModel_DDL(currentDdlFile, optionArg);
                if (!r1 || !File.Exists(currentDdlFile))
                {
                    log?.Invoke("DDL: FEModel_DDL failed for current model.");
                    return "-- ERROR: FEModel_DDL failed for current model.";
                }
                string currentDdl = File.ReadAllText(currentDdlFile);
                log?.Invoke($"DDL: Current DDL = {currentDdl.Length} chars, {currentDdl.Split('\n').Length} lines");

                // Step 2: ODBC driver preflight + transient DSN
                var drvCheck = OdbcDsnHelper.CheckSqlServerDriver(log);
                if (!drvCheck.IsOk)
                    return $"-- ERROR: {drvCheck.UserMessage}";

                OdbcDsnHelper.CleanupStale(log);
                tempDsn = OdbcDsnHelper.CreateTempSqlServerDsn(host, database, user, log);
                log?.Invoke($"DDL: created transient DSN '{tempDsn}'");

                // Step 3: REoption XML from XML_OPTION (per-model -> ALL=1 -> embedded) — reuse open conn
                tempReOptionXml = XmlOptionLoaderService.LoadAndWriteToTempFile(sqlConnLong, activeConfigId, "RE", log);

                // Step 4: build PropBag for Create
                Type pbType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0");
                if (pbType == null)
                    return "-- ERROR: SCAPI PropertyBag ProgID not registered.";

                dynamic propBag = Activator.CreateInstance(pbType);
                propBag.Add("Model_Type", "Combined");
                propBag.Add("Target_Server", (int)targetServerCode);
                propBag.Add("Target_Server_Version", targetServerVersion);

                rePU = scapi.PersistenceUnits.Create(propBag);
                log?.Invoke($"DDL: blank PU created for RE — {rePU.Name}");

                // Step 5: ClearAll + RE keys (per r10 sample)
                propBag.ClearAll();
                propBag.Add("System_Objects", false);
                propBag.Add("Oracle_Use_DBA_Views", false);
                propBag.Add("Synch_Owned_Only", !string.IsNullOrEmpty(schema));
                propBag.Add("Synch_Owned_Only_Name", schema ?? "");
                // Case_Option: 25090=None(preserve), 25091=lower, 25092=Upper
                // (SCAPI doc line 6778). 25091 lowercase'e ceviriyordu ->
                // mart UPPERCASE model ile case-mismatch -> CC compare
                // 819 item false-diff (RD'de "dbo.APPROVEMENT_DEF" vs
                // "dbo.approvement_def" iki ayri obje gibi). 25090 ile
                // DB'deki orijinal case korunur, mart model ile match.
                propBag.Add("Case_Option", 25090);
                propBag.Add("Logical_Case_Option", 25045);
                propBag.Add("Infer_Primary_Keys", false);
                propBag.Add("Infer_Relations", false);
                propBag.Add("Infer_Relations_Indexes", false);
                propBag.Add("Remove_ERwin_Generated_Triggers", false);
                propBag.Add("Force_Physical_Name_Option", false);
                propBag.Add("Synch_Table_Filter_By_Name", string.Join(",", tableList));

                // Step 6: build connection string and call ReverseEngineer
                int authCode = useWindowsAuth ? 8 : 4;
                int connectionMode = 2; // ODBC
                string connStr =
                    $"SERVER={dbTypeCode}:{targetServerVersion}:0|" +
                    $"AUTHENTICATION={authCode}|" +
                    $"USER={user}|" +
                    $"1={connectionMode}|" +
                    $"5={tempDsn}";
                string rePassword = useWindowsAuth ? "" : (password ?? "");
                log?.Invoke($"DDL: connStr={connStr}");

                object reOptArg = string.IsNullOrEmpty(tempReOptionXml) ? (object)Type.Missing : tempReOptionXml;
                long t0 = Environment.TickCount64;
                object reResult = rePU.ReverseEngineer(propBag, reOptArg, connStr, rePassword);
                long elapsed = Environment.TickCount64 - t0;
                log?.Invoke($"DDL: ReverseEngineer returned {(reResult ?? "<null>")} in {elapsed}ms");

                // Step 7: FEModel_DDL on the RE'd PU
                CleanupFile(dbDdlFile);
                bool r2 = rePU.FEModel_DDL(dbDdlFile, optionArg);
                if (!r2 || !File.Exists(dbDdlFile))
                {
                    log?.Invoke("DDL: FEModel_DDL on RE'd PU failed.");
                    return "-- ERROR: Reverse engineered model produced no DDL. Check schema/tables/credentials.";
                }
                string dbDdl = File.ReadAllText(dbDdlFile);
                if (dbDdl.Length == 0)
                {
                    log?.Invoke("DDL: RE'd PU DDL is empty (no tables imported).");
                    return $"-- ERROR: No tables imported from {dbLabel}. Verify schema='{schema}' and table names.";
                }
                log?.Invoke($"DDL: RE'd DDL = {dbDdl.Length} chars, {dbDdl.Split('\n').Length} lines");

                // Step 8: text diff (same engine as Mart)
                if (currentDdl == dbDdl)
                    return $"-- No differences between active model and {dbLabel}.";

                string mName = currentPU.Name?.ToString() ?? "Model";
                return ComputeDDLDiff(currentDdl, dbDdl, log,
                    $"{mName} (current)", $"{dbLabel} (DB)");
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL DB diff error: {ex.GetType().Name}: {ex.Message}");
                return $"-- Error: {ex.Message}";
            }
            finally
            {
                if (rePU != null)
                {
                    try { scapi.PersistenceUnits.Remove(rePU, false); }
                    catch (Exception ex) { log?.Invoke($"DDL: Remove RE PU error: {ex.Message}"); }
                }
                if (!string.IsNullOrEmpty(tempReOptionXml))
                {
                    try { File.Delete(tempReOptionXml); }
                    catch (Exception ex) { log?.Invoke($"DDL: temp RE XML delete error: {ex.Message}"); }
                }
                if (!string.IsNullOrEmpty(tempDdlOptionXml))
                {
                    try { File.Delete(tempDdlOptionXml); }
                    catch (Exception ex) { log?.Invoke($"DDL: temp DDL XML delete error: {ex.Message}"); }
                }
                if (sqlConnLong != null)
                {
                    try { sqlConnLong.Dispose(); }
                    catch (Exception ex) { log?.Invoke($"DDL: sqlConn dispose error: {ex.Message}"); }
                }
                if (!string.IsNullOrEmpty(tempDsn))
                {
                    try { OdbcDsnHelper.DeleteDsn(tempDsn, log); }
                    catch (Exception ex) { log?.Invoke($"DDL: DSN delete error: {ex.Message}"); }
                }
                CleanupFile(currentDdlFile);
                CleanupFile(dbDdlFile);
            }
        }

        /// <summary>
        /// Open a Microsoft.Data.SqlClient connection used only to read XML_OPTION.
        /// Returns null on failure (caller falls back to embedded XML).
        /// </summary>
        private static System.Data.IDbConnection OpenSqlConnectionForXmlOption(
            string host, string database, string user, string password, bool useWindowsAuth, Action<string> log)
        {
            try
            {
                string sqlConn = useWindowsAuth
                    ? $"Data Source={host};Initial Catalog={database};Integrated Security=True;TrustServerCertificate=True"
                    : $"Data Source={host};Initial Catalog={database};User ID={user};Password={password};TrustServerCertificate=True";
                var c = new Microsoft.Data.SqlClient.SqlConnection(sqlConn);
                c.Open();
                return c;
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: SQL connection for XML_OPTION lookup failed: {ex.Message}");
                return null;
            }
        }

        // Removed: out-of-process DdlHelper RE — superseded by in-process pattern in
        // GenerateDiffWithDatabase (SCAPI r10 RE only works in-process with the live
        // erwin SCAPI instance; out-of-process silently no-ops).

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

        private static string ComputeDDLDiff(string leftDdl, string rightDdl, Action<string> log,
            string leftLabel = null, string rightLabel = null)
        {
            var leftStatements = ParseStatements(leftDdl);
            var rightStatements = ParseStatements(rightDdl);

            string lbl = leftLabel ?? "Current";
            string rbl = rightLabel ?? "Baseline";

            log?.Invoke($"DDL: {lbl} has {leftStatements.Count} statements, {rbl} has {rightStatements.Count} statements");

            var leftByKey = BuildStatementMap(leftStatements);
            var rightByKey = BuildStatementMap(rightStatements);

            var result = new System.Text.StringBuilder();
            result.AppendLine($"-- DDL Diff: {lbl} vs {rbl}");
            result.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            result.AppendLine($"-- {lbl}: {leftStatements.Count} statements, {rbl}: {rightStatements.Count} statements");

            int addedCount = 0, droppedCount = 0, changedCount = 0;
            int suppressedTriggerCount = 0;

            // Built-in triggers (FK delete/update no-action) are auto-generated by erwin's
            // FE depending on FE option toggles. They appear on one side and not the other
            // when the active model and the RE'd model use slightly different defaults.
            // Filter them out to avoid noise in the diff. Custom user triggers don't follow
            // the tD_/tU_/tI_ erwin naming convention so they still surface.
            bool IsBuiltInTrigger(string key) =>
                key != null && key.StartsWith("TRIGGER:t", StringComparison.OrdinalIgnoreCase) &&
                key.Length > "TRIGGER:t".Length &&
                "DUI".IndexOf(char.ToUpperInvariant(key["TRIGGER:t".Length])) >= 0;

            // NEW: In current but not in baseline
            var addedSection = new System.Text.StringBuilder();
            foreach (var kvp in leftByKey)
            {
                if (rightByKey.ContainsKey(kvp.Key)) continue;
                if (IsBuiltInTrigger(kvp.Key)) { suppressedTriggerCount++; continue; }
                addedSection.AppendLine();
                addedSection.AppendLine($"-- NEW: {kvp.Key}");
                addedSection.AppendLine(kvp.Value);
                addedSection.AppendLine("go");
                addedCount++;
            }

            // DROPPED: In baseline but not in current
            var droppedSection = new System.Text.StringBuilder();
            foreach (var kvp in rightByKey)
            {
                if (leftByKey.ContainsKey(kvp.Key)) continue;
                if (IsBuiltInTrigger(kvp.Key)) { suppressedTriggerCount++; continue; }
                droppedSection.AppendLine();
                droppedSection.AppendLine($"-- DROPPED: {kvp.Key}");
                droppedSection.AppendLine($"-- {GenerateDropStatement(kvp.Key)}");
                droppedCount++;
            }

            // CHANGED: In both but different
            var changedSection = new System.Text.StringBuilder();
            foreach (var kvp in leftByKey)
            {
                if (rightByKey.TryGetValue(kvp.Key, out string baselineStmt))
                {
                    if (IsBuiltInTrigger(kvp.Key)) { suppressedTriggerCount++; continue; }
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

            result.AppendLine($"-- Summary: {addedCount} new, {droppedCount} dropped, {changedCount} changed" +
                (suppressedTriggerCount > 0 ? $" ({suppressedTriggerCount} built-in triggers suppressed)" : ""));
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

            log?.Invoke($"DDL: Diff complete - {addedCount} new, {droppedCount} dropped, {changedCount} changed (suppressed {suppressedTriggerCount} built-in triggers)");
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

        /// <summary>
        /// Take an identifier like "[dbo].[Name]", "dbo.Name", "[Name]" or "Name" and
        /// return just the bare last segment without brackets.
        /// </summary>
        private static string StripSchema(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            // Cut at first whitespace / paren / comma / semicolon (in case the regex over-captured).
            int cut = fullName.IndexOfAny(new[] { ' ', '\t', '(', ',', ';' });
            if (cut > 0) fullName = fullName.Substring(0, cut);
            var parts = fullName.Split('.');
            return parts[parts.Length - 1].Trim('[', ']');
        }

        private static string GetStatementKey(string statement)
        {
            string first = statement.Split('\n')[0].Trim();

            // Match "CREATE TABLE [schema].[name]" or "CREATE TABLE name" — capture the FULL ident,
            // then strip schema/brackets in StripSchema.
            var match = Regex.Match(first, @"CREATE\s+TABLE\s+([\[\]\w\.]+)", RegexOptions.IgnoreCase);
            if (match.Success) return $"TABLE:{StripSchema(match.Groups[1].Value)}";

            match = Regex.Match(first, @"ALTER\s+TABLE\s+([\[\]\w\.]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string tableName = StripSchema(match.Groups[1].Value);
                var cm = Regex.Match(statement, @"CONSTRAINT\s+([\[\]\w\.]+)", RegexOptions.IgnoreCase);
                return cm.Success ? $"CONSTRAINT:{tableName}.{StripSchema(cm.Groups[1].Value)}" : $"ALTER:{tableName}";
            }

            match = Regex.Match(first, @"CREATE\s+(UNIQUE\s+)?INDEX\s+([\[\]\w\.]+)", RegexOptions.IgnoreCase);
            if (match.Success) return $"INDEX:{StripSchema(match.Groups[2].Value)}";

            match = Regex.Match(first, @"CREATE\s+TRIGGER\s+([\[\]\w\.]+)", RegexOptions.IgnoreCase);
            if (match.Success) return $"TRIGGER:{StripSchema(match.Groups[1].Value)}";

            match = Regex.Match(first, @"CREATE\s+(VIEW|PROCEDURE|FUNCTION|SCHEMA|TYPE|SEQUENCE|SYNONYM)\s+([\[\]\w\.]+)", RegexOptions.IgnoreCase);
            if (match.Success) return $"{match.Groups[1].Value.ToUpperInvariant()}:{StripSchema(match.Groups[2].Value)}";

            // Generic catch-all (last resort) — schema-aware.
            match = Regex.Match(first, @"CREATE\s+(\w+)\s+([\[\]\w\.]+)", RegexOptions.IgnoreCase);
            if (match.Success) return $"{match.Groups[1].Value.ToUpperInvariant()}:{StripSchema(match.Groups[2].Value)}";

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

        #region Mart Version Listing

        /// <summary>
        /// Get list of model versions from Mart DB (m9Catalog).
        /// Returns list of (version number, version name) tuples.
        /// </summary>
        /// <summary>
        /// Get version list from PU locator (no Portal DB needed).
        /// Current PU locator contains version=N, so we list 1..N.
        /// </summary>
        /// <summary>
        /// Open a specific Mart version of the given model's family as a second
        /// PersistenceUnit. Returns the opened PU (caller must close/remove it
        /// when finished) or null on failure. Used by the CC pipeline to present
        /// the baseline version as a "loaded model" for the compare pipeline.
        /// </summary>
        public static dynamic OpenMartVersionPU(dynamic scapi, dynamic currentPU, int version, Action<string> log)
        {
            if (scapi == null || currentPU == null || version <= 0) return null;
            try
            {
                string locator = "";
                try { locator = currentPU.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }

                var pathMatch = Regex.Match(locator, @"Mart://Mart/(.+?)(\?|$)", RegexOptions.IgnoreCase);
                string modelPath = pathMatch.Success ? pathMatch.Groups[1].Value
                                                    : (currentPU.Name?.ToString() ?? "");
                if (string.IsNullOrEmpty(modelPath))
                {
                    log?.Invoke("OpenMartVersionPU: could not derive model path from current PU locator.");
                    return null;
                }

                // Candidates - short-form first (reuses existing Mart connection),
                // then with explicit Mart credentials if we can fetch them.
                var attempts = new List<(string url, string disp, string desc)>();
                attempts.Add(($"mart://Mart/{modelPath}?VNO={version}", "RDO=Yes", "short + RDO"));
                attempts.Add(($"mart://Mart/{modelPath}?VNO={version}", "OVM=Yes", "short + OVM"));
                attempts.Add(($"mart://Mart/{modelPath}?VNO={version}", "", "short + empty"));

                var martInfo = GetMartConnectionInfo(log);
                if (martInfo != null)
                {
                    string fullUrl = $"mart://Mart/{modelPath}?TRC=NO;SRV={martInfo.Value.host};PRT={martInfo.Value.port};ASR=MartServer;UID={martInfo.Value.username};PSW={martInfo.Value.password};VNO={version}";
                    attempts.Add((fullUrl, "RDO=Yes", "full + RDO"));
                    attempts.Add((fullUrl, "OVM=Yes", "full + OVM"));
                    attempts.Add((fullUrl, "", "full + empty"));
                }

                foreach (var (url, disp, desc) in attempts)
                {
                    string masked = Regex.Replace(url, @"PSW=[^;]*", "PSW=***");
                    log?.Invoke($"OpenMartVersionPU: trying [{desc}] {masked} disp='{disp}'");
                    try
                    {
                        dynamic pu = scapi.PersistenceUnits.Add(url, disp);
                        string name = pu?.Name?.ToString() ?? "";
                        log?.Invoke($"OpenMartVersionPU: success [{desc}] - PU.Name = '{name}'");
                        return pu;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"OpenMartVersionPU: [{desc}] failed: {ex.Message}");
                    }
                }

                log?.Invoke("OpenMartVersionPU: all attempts failed.");
                return null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"OpenMartVersionPU: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public static List<(int Version, string Name, int CatalogId)> GetMartVersions(string modelName, dynamic currentPU, Action<string> log)
        {
            var versions = new List<(int Version, string Name, int CatalogId)>();

            try
            {
                string locator = "";
                try { locator = currentPU.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }

                int currentVer = ParseVersionFromLocator(locator);
                log?.Invoke($"DDL: Current version from locator = v{currentVer}");

                if (currentVer > 0)
                {
                    for (int v = 1; v <= currentVer; v++)
                    {
                        versions.Add((v, $"Version {v}", 0));
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: GetMartVersions error: {ex.Message}");
            }

            log?.Invoke($"DDL: Found {versions.Count} version(s) for '{modelName}'");
            return versions;
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
