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
        private static string _lastCCOutput;

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

        private static string TryOpenMartVersionDirectly(dynamic scapi, dynamic currentPU, int version, string feOptionXml, Action<string> log)
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

        private static (string host, string port, string username, string password)? GetMartConnectionInfo(Action<string> log)
        {
            try
            {
                string connStr = GetMartConnectionString(log);
                if (string.IsNullOrEmpty(connStr)) return null;

                // Parse connection string to extract host, port, user, password
                var parts = connStr.Split(';');
                string host = "", port = "", user = "", pass = "";

                foreach (var part in parts)
                {
                    var kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length < 2) continue;
                    string key = kv[0].Trim().ToLower();
                    string val = kv[1].Trim();

                    if (key == "server") { var hp = val.Split(','); host = hp[0]; port = hp.Length > 1 ? hp[1] : ""; }
                    else if (key == "user id") user = val;
                    else if (key == "password") pass = val;
                }

                // We need Mart Server port (not SQL port). Read from CONNECTION_DEF directly.
                if (!DatabaseService.Instance.IsConfigured) return null;

                var config = new RegistryBootstrapService().GetConfig();
                if (config == null) return null;

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();
                    // CONNECTION_DEF ID=4 = Mart Server connection (not DB, not Portal)
                    string query = config.DbType?.ToUpper() switch
                    {
                        "POSTGRESQL" => @"SELECT ""HOST"", ""PORT"", ""USERNAME"", ""PASSWORD"" FROM ""CONNECTION_DEF"" WHERE ""ID"" = 4",
                        _ => @"SELECT [HOST], [PORT], [USERNAME], [PASSWORD] FROM [dbo].[CONNECTION_DEF] WHERE [ID] = 4"
                    };

                    using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            host = reader["HOST"]?.ToString()?.Trim() ?? "";
                            port = reader["PORT"]?.ToString()?.Trim() ?? "";
                            string encUser = reader["USERNAME"]?.ToString()?.Trim() ?? "";
                            string encPass = reader["PASSWORD"]?.ToString()?.Trim() ?? "";

                            user = PasswordEncryptionService.Decrypt(encUser);
                            pass = PasswordEncryptionService.Decrypt(encPass);

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

                if (selectedVer > 0 && selectedVer != currentVer)
                {
                    // Try: version already open as PU?
                    int puCount = scapi.PersistenceUnits.Count;
                    if (puCount >= 2)
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
                                    pu.FEModel_DDL(versionDdlFile, optionArg);
                                    if (File.Exists(versionDdlFile))
                                        versionDdl = File.ReadAllText(versionDdlFile);
                                    break;
                                }
                            }
                            catch { }
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
                        return $"-- No differences between current and v{selectedVer}.";
                    return ComputeDDLDiff(currentDdl, versionDdl, log);
                }

                log?.Invoke("DDL: Could not get version DDL.");
                return null; // Return null to trigger PU watcher fallback
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

        #region Mart Version Listing

        /// <summary>
        /// Get list of model versions from Mart DB (m9Catalog).
        /// Returns list of (version number, version name) tuples.
        /// </summary>
        public static List<(int Version, string Name, int CatalogId)> GetMartVersions(string modelName, Action<string> log)
        {
            var versions = new List<(int Version, string Name, int CatalogId)>();

            try
            {
                // Get Mart DB connection string from CONNECTION_DEF
                string martConnStr = GetMartConnectionString(log);
                if (string.IsNullOrEmpty(martConnStr)) return versions;

                using (var conn = DatabaseService.Instance.CreateConnection("MSSQL", martConnStr))
                {
                    conn.Open();

                    // Step 1: Find the main model entry (Type='D' = directory/model group)
                    string mainQuery = @"
                        SELECT TOP 1 C_Id
                        FROM m9Catalog
                        WHERE C_Name = @ModelName AND C_Type = 'D'
                        ORDER BY C_Id DESC";

                    int mainId = 0;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = mainQuery;
                        var p = cmd.CreateParameter();
                        p.ParameterName = "@ModelName";
                        p.Value = modelName;
                        cmd.Parameters.Add(p);
                        var result = cmd.ExecuteScalar();
                        if (result != null) mainId = Convert.ToInt32(result);
                    }

                    if (mainId == 0)
                    {
                        log?.Invoke($"DDL: Model '{modelName}' not found in m9Catalog (Type='D')");
                        return versions;
                    }
                    log?.Invoke($"DDL: Model directory C_Id={mainId}");

                    // Step 2: Find all versions (Type='V') under this model using C_Container_Id
                    string versionQuery = @"
                        SELECT C_Id, C_Name, C_Version
                        FROM m9Catalog
                        WHERE C_Container_Id = @MainId AND C_Type = 'V'
                        ORDER BY C_Version ASC";

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = versionQuery;
                        var p = cmd.CreateParameter();
                        p.ParameterName = "@MainId";
                        p.Value = mainId;
                        cmd.Parameters.Add(p);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int cId = Convert.ToInt32(reader["C_Id"]);
                                string name = reader["C_Name"]?.ToString()?.Trim() ?? "";
                                int ver = 0;
                                try { ver = Convert.ToInt32(reader["C_Version"]); } catch { }

                                if (ver == 0) ver = versions.Count + 1;
                                versions.Add((ver, name, cId));
                                log?.Invoke($"DDL:   v{ver}: C_Id={cId}, Name='{name}'");
                            }
                        }
                    }

                    log?.Invoke($"DDL: Found {versions.Count} version(s) for '{modelName}'");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: GetMartVersions error: {ex.Message}");
            }

            return versions;
        }

        private static string GetMartConnectionString(Action<string> log)
        {
            try
            {
                if (!DatabaseService.Instance.IsConfigured) return null;

                var config = new RegistryBootstrapService().GetConfig();
                if (config == null) return null;

                string repoDbType = config.DbType;

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();

                    string query = repoDbType?.ToUpper() switch
                    {
                        "POSTGRESQL" => @"SELECT ""ID"", ""DB_TYPE"", ""HOST"", ""PORT"", ""DB_SCHEMA"", ""USERNAME"", ""PASSWORD""
                                         FROM ""CONNECTION_DEF"" WHERE ""DB_SCHEMA"" LIKE '%Portal%'
                                         ORDER BY ""ID"" LIMIT 1",
                        _ => @"SELECT TOP 1 [ID], [DB_TYPE], [HOST], [PORT], [DB_SCHEMA], [USERNAME], [PASSWORD]
                               FROM [dbo].[CONNECTION_DEF] WHERE [DB_SCHEMA] LIKE '%Portal%'
                               ORDER BY [ID]"
                    };

                    using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            string host = reader["HOST"]?.ToString()?.Trim() ?? "";
                            string port = reader["PORT"]?.ToString()?.Trim() ?? "";
                            string schema = reader["DB_SCHEMA"]?.ToString()?.Trim() ?? "";
                            string encUser = reader["USERNAME"]?.ToString()?.Trim() ?? "";
                            string encPass = reader["PASSWORD"]?.ToString()?.Trim() ?? "";

                            string username = PasswordEncryptionService.Decrypt(encUser);
                            string password = PasswordEncryptionService.Decrypt(encPass);

                            if (string.IsNullOrEmpty(username) || (username.Length > 50 && username == encUser))
                            {
                                username = config.Username;
                                password = config.Password;
                            }

                            return $"Server={host},{port};Database={schema};User Id={username};Password={password};TrustServerCertificate=True;Connection Timeout=10;";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"DDL: GetMartConnectionString error: {ex.Message}");
            }

            return null;
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
