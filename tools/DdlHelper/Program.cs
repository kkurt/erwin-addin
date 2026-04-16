using System;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Standalone helper for erwin Mart operations.
/// Runs as a SEPARATE process with its OWN SCAPI instance,
/// bypassing the "Mart user interface is active" restriction.
///
/// Actions:
///   ddl:      Generate DDL for a specific Mart version
///   versions: List all versions for a model
///   redb:     Reverse Engineer from DB, then generate DDL
///
/// Usage:
///   DdlHelper.exe --action=ddl --server=host --port=18170 --user=u --pass=p
///                  --model=lib/name --version=N --output=file.sql
///
///   DdlHelper.exe --action=versions --server=host --port=18170 --user=u --pass=p
///                  --model=lib/name
///
///   DdlHelper.exe --action=redb --connstr="SERVER=16:10:0|..." --dbpass=pass
///                  --output=file.sql --targetserver=1075859016 --targetversion=10
///                  [--feoption=fe.xml] [--tablefilter=T1,T2] [--schemafilter=dbo]
/// </summary>
class Program
{
    [System.STAThread]
    static int Main(string[] args)
    {
        string action = "ddl", server = "", port = "", user = "", pass = "";
        string model = "", version = "", output = "", feOption = "";
        string connStr = "", dbPass = "", targetServer = "", targetVersion = "";
        string tableFilter = "", schemaFilter = "";

        foreach (var arg in args)
        {
            var parts = arg.Split(new[] { '=' }, 2);
            if (parts.Length < 2) continue;
            string key = parts[0].TrimStart('-').ToLower();
            string val = parts[1];

            switch (key)
            {
                case "action": action = val.ToLower(); break;
                case "server": server = val; break;
                case "port": port = val; break;
                case "user": user = val; break;
                case "pass": pass = val; break;
                case "model": model = val; break;
                case "version": version = val; break;
                case "output": output = val; break;
                case "feoption": feOption = val; break;
                case "connstr": connStr = val; break;
                case "dbpass": dbPass = val; break;
                case "targetserver": targetServer = val; break;
                case "targetversion": targetVersion = val; break;
                case "tablefilter": tableFilter = val; break;
                case "schemafilter": schemaFilter = val; break;
            }
        }

        if (action == "probe")
            return ProbeTargetServer.Run();

        if (action == "fedb")
        {
            if (string.IsNullOrEmpty(output))
            {
                Console.Error.WriteLine("Usage: DdlHelper --action=fedb --server=host --port=18170 --user=u --pass=p --model=lib/name --version=N --output=file.sql --connstr=\"SERVER=...\" --dbpass=pass");
                return 1;
            }
            return ForwardEngineerFromDB(server, port, user, pass, model, version, output, feOption, connStr, dbPass);
        }

        if (action == "redb")
        {
            if (string.IsNullOrEmpty(connStr) || string.IsNullOrEmpty(output))
            {
                Console.Error.WriteLine("Usage: DdlHelper --action=redb --connstr=\"SERVER=...\" --dbpass=pass --output=file.sql --targetserver=N --targetversion=N");
                return 1;
            }
            return ReverseEngineerFromDB(connStr, dbPass, output, targetServer, targetVersion, feOption, tableFilter, schemaFilter);
        }

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(model))
        {
            Console.Error.WriteLine("Usage: DdlHelper --action=ddl|versions|redb --server=host --port=18170 --user=u --pass=p --model=lib/name [--version=N --output=file.sql]");
            return 1;
        }

        return action switch
        {
            "versions" => ListVersions(server, port, user, pass, model),
            "ddl" => GenerateDDL(server, port, user, pass, model, version, output, feOption),
            _ => GenerateDDL(server, port, user, pass, model, version, output, feOption)
        };
    }

    static int ListVersions(string server, string port, string user, string pass, string model)
    {
        dynamic scapi = null;
        try
        {
            Type scapiType = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0");
            if (scapiType == null) { Console.Error.WriteLine("ERROR: SCAPI not registered"); return 2; }
            scapi = Activator.CreateInstance(scapiType);

            // Connect to Mart
            string martDir = $"mart://Mart?TRC=NO;SRV={server};PRT={port};ASR=MartServer;UID={user};PSW={pass}";
            try { scapi.ModelDirectories.Add(martDir, ""); } catch { }

            // Locate model versions via ModelDirectories
            dynamic dirs = scapi.ModelDirectories;
            for (int i = 0; i < 3; i++) // Try indices 0, 1, 2
            {
                try
                {
                    dynamic dir = dirs.Item(i);
                    dynamic pb = dir.PropertyBag();

                    // Check if this is a Mart directory (Type=2)
                    int dirType = 0;
                    try { dirType = Convert.ToInt32(pb.Value("Type")); } catch { }
                    if (dirType != 2) continue;

                    // Locate the model
                    string locator = $"mart://Mart/{model}";
                    try
                    {
                        dynamic unitBag = dir.LocateDirectoryUnit(locator, "");
                        if (unitBag != null)
                        {
                            int count = unitBag.Count;
                            for (int j = 0; j < count; j++)
                            {
                                try
                                {
                                    string name = unitBag.Name(j)?.ToString() ?? "";
                                    string val = unitBag.Value(name)?.ToString() ?? "";
                                    Console.WriteLine($"PROP:{name}={val}");
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"LocateDirectoryUnit: {ex.Message}");
                    }

                    // Try to enumerate versions by opening model and checking PU properties
                    // Open model without specific version to get latest
                    string martUrl = $"mart://Mart/{model}?TRC=NO;SRV={server};PRT={port};ASR=MartServer;UID={user};PSW={pass}";
                    try
                    {
                        dynamic pu = scapi.PersistenceUnits.Add(martUrl, "OVM=Yes");
                        string puLocator = "";
                        try { puLocator = pu.PropertyBag().Value("Locator")?.ToString() ?? ""; } catch { }

                        // Extract version from locator
                        var match = System.Text.RegularExpressions.Regex.Match(puLocator, @"version=(\d+)");
                        int latestVer = match.Success ? int.Parse(match.Groups[1].Value) : 1;

                        Console.WriteLine($"LATEST:{latestVer}");

                        // List all versions from 1 to latest
                        for (int v = 1; v <= latestVer; v++)
                        {
                            Console.WriteLine($"VERSION:{v}");
                        }

                        scapi.PersistenceUnits.Remove(pu, false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"PU open: {ex.Message}");
                    }

                    break;
                }
                catch { }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 3;
        }
        finally
        {
            if (scapi != null) try { Marshal.ReleaseComObject(scapi); } catch { }
        }
    }

    static int GenerateDDL(string server, string port, string user, string pass,
        string model, string version, string output, string feOption)
    {
        if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(output))
        {
            Console.Error.WriteLine("ERROR: --version and --output required for DDL action");
            return 1;
        }

        Console.WriteLine($"DdlHelper: server={server}:{port}, model={model}, version={version}");

        dynamic scapi = null;
        dynamic pu = null;

        try
        {
            Console.WriteLine("Creating SCAPI instance...");
            Type scapiType = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0");
            if (scapiType == null) { Console.Error.WriteLine("ERROR: SCAPI not registered"); return 2; }
            scapi = Activator.CreateInstance(scapiType);
            Console.WriteLine($"SCAPI version: {scapi.Version}");

            string martDir = $"mart://Mart?TRC=NO;SRV={server};PRT={port};ASR=MartServer;UID={user};PSW={pass}";
            Console.WriteLine("Connecting to Mart...");
            try { scapi.ModelDirectories.Add(martDir, ""); }
            catch (Exception ex) { Console.WriteLine($"ModelDirectories.Add: {ex.Message}"); }

            string martUrl = $"mart://Mart/{model}?TRC=NO;SRV={server};PRT={port};ASR=MartServer;UID={user};PSW={pass};VNO={version}";
            Console.WriteLine($"Opening v{version}...");
            pu = scapi.PersistenceUnits.Add(martUrl, "OVM=Yes");
            Console.WriteLine($"Model opened: {pu.Name}");

            Console.WriteLine($"Generating DDL to {output}...");
            string feArg = string.IsNullOrEmpty(feOption) ? "" : feOption;
            bool result = pu.FEModel_DDL(output, feArg);

            if (result && File.Exists(output))
            {
                long size = new FileInfo(output).Length;
                Console.WriteLine($"DDL generated: {size} bytes");
                return 0;
            }
            else
            {
                Console.Error.WriteLine("FEModel_DDL returned false or no output");
                return 4;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 3;
        }
        finally
        {
            if (pu != null) try { scapi.PersistenceUnits.Remove(pu, false); } catch { }
            if (scapi != null) try { Marshal.ReleaseComObject(scapi); } catch { }
        }
    }

    static int ForwardEngineerFromDB(string server, string port, string user, string pass,
        string model, string version, string output, string feOption,
        string dbConnStr, string dbPass)
    {
        Console.WriteLine($"DdlHelper FEDB: model={model}, version={version}");
        Console.WriteLine($"DdlHelper FEDB: dbConnStr={dbConnStr}");

        dynamic scapi = null;
        dynamic pu = null;

        try
        {
            Type scapiType = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0");
            if (scapiType == null) { Console.Error.WriteLine("ERROR: SCAPI not registered"); return 2; }
            scapi = Activator.CreateInstance(scapiType);
            Console.WriteLine($"SCAPI version: {scapi.Version}");

            // Open model from Mart (same as GenerateDDL)
            string martDir = $"mart://Mart?TRC=NO;SRV={server};PRT={port};ASR=MartServer;UID={user};PSW={pass}";
            try { scapi.ModelDirectories.Add(martDir, ""); }
            catch (Exception ex) { Console.WriteLine($"ModelDirectories.Add: {ex.Message}"); }

            string martUrl = $"mart://Mart/{model}?TRC=NO;SRV={server};PRT={port};ASR=MartServer;UID={user};PSW={pass}";
            if (!string.IsNullOrEmpty(version))
                martUrl += $";VNO={version}";
            // Open with write access (FEModel_DB may require it)
            Console.WriteLine($"Opening model (write mode)...");
            pu = scapi.PersistenceUnits.Add(martUrl, "");
            Console.WriteLine($"Model opened: {pu.Name}");

            // FEModel_DB: compare model with database
            // NEVER pass null - causes ACCESS_VIOLATION in erwin COM
            string rePass = string.IsNullOrEmpty(dbPass) ? "" : dbPass;
            string feConnStr = dbConnStr.Replace("SERVERR=", "SERVER=");

            // Try FEModel_DB with different option values
            string defaultFeXml = @"c:\work\ErwinCompleteCompareTemplates\DefaultGeneraionOptions.xml";
            string[] feOptions = {
                defaultFeXml, // actual FE option XML file
                string.IsNullOrEmpty(feOption) ? "" : feOption,
                "Standard",
            };

            bool feDbSuccess = false;
            string workingFeOption = "";
            string fo = feOptions[0]; // Use first (actual XML file)
            Console.WriteLine($"FEModel_DB: connStr={feConnStr}, pass='{rePass}', option='{fo}'");

            // Try calling via VBScript COM automation (native COM, no .NET interop issues)
            try
            {
                // Create VBScript to call FEModel_DB
                string vbsPath = Path.Combine(Path.GetTempPath(), "fedb_call.vbs");
                string vbsContent = $@"
On Error Resume Next
Dim oAPI
Set oAPI = GetObject(, ""ERwin9.SCAPI.9.0"")
If Err.Number <> 0 Then
    Set oAPI = CreateObject(""ERwin9.SCAPI.9.0"")
    Err.Clear
End If
Dim oPU
Set oPU = oAPI.PersistenceUnits.Item(0)
WScript.Echo ""PU: "" & oPU.Name
Dim result
result = oPU.FEModel_DB(""{feConnStr}"", ""{rePass}"", ""{fo.Replace("\\", "\\\\")}"")
If Err.Number <> 0 Then
    WScript.Echo ""FEModel_DB ERROR: "" & Err.Description & "" (0x"" & Hex(Err.Number) & "")""
Else
    WScript.Echo ""FEModel_DB OK: "" & result
End If
";
                File.WriteAllText(vbsPath, vbsContent);
                Console.WriteLine("Trying VBScript FEModel_DB...");

                var vbsPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cscript",
                    Arguments = $"//nologo \"{vbsPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var vbsProc = System.Diagnostics.Process.Start(vbsPsi))
                {
                    string vbsOut = vbsProc.StandardOutput.ReadToEnd();
                    string vbsErr = vbsProc.StandardError.ReadToEnd();
                    vbsProc.WaitForExit(60000);
                    Console.WriteLine($"VBS output: {vbsOut.Trim()}");
                    if (!string.IsNullOrEmpty(vbsErr))
                        Console.WriteLine($"VBS error: {vbsErr.Trim()}");

                    if (vbsOut.Contains("FEModel_DB OK"))
                    {
                        feDbSuccess = true;
                        workingFeOption = fo;
                    }
                }
                File.Delete(vbsPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VBS approach: {ex.Message}");
            }

            // Also try .NET InvokeMember as fallback
            if (!feDbSuccess)
            {
                try
                {
                    object result = ((object)pu).GetType().InvokeMember("FEModel_DB",
                        System.Reflection.BindingFlags.InvokeMethod,
                        null, (object)pu,
                        new object[] { feConnStr, rePass, fo });
                    Console.WriteLine($"FEModel_DB InvokeMember: {result}");
                    feDbSuccess = true;
                    workingFeOption = fo;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FEModel_DB InvokeMember: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            if (!feDbSuccess)
                Console.Error.WriteLine("FEModel_DB failed with all options. Generating full DDL.");

            // FEModel_DDL
            string ddlFeOption = feDbSuccess ? workingFeOption : (string.IsNullOrEmpty(feOption) ? "" : feOption);
            Console.WriteLine($"FEModel_DDL to {output} (option='{ddlFeOption}')...");
            pu.FEModel_DDL(output, ddlFeOption);

            if (File.Exists(output))
            {
                long size = new FileInfo(output).Length;
                Console.WriteLine($"DDL output: {size} bytes");
                return size > 0 ? 0 : 4;
            }
            else
            {
                Console.Error.WriteLine("No DDL output");
                return 4;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 3;
        }
        finally
        {
            if (pu != null) try { scapi.PersistenceUnits.Remove(pu, false); } catch { }
            if (scapi != null) try { Marshal.ReleaseComObject(scapi); } catch { }
        }
    }

    static int ReverseEngineerFromDB(string connStr, string dbPass, string output,
        string targetServer, string targetVersion, string feOption,
        string tableFilter, string schemaFilter)
    {
        Console.WriteLine($"DdlHelper REDB: connStr={connStr}");

        dynamic scapi = null;
        dynamic pu = null;

        try
        {
            Console.WriteLine("Creating SCAPI instance...");
            Type scapiType = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0");
            if (scapiType == null) { Console.Error.WriteLine("ERROR: SCAPI not registered"); return 2; }
            scapi = Activator.CreateInstance(scapiType);
            Console.WriteLine($"SCAPI version: {scapi.Version}");

            // Step 1: Create blank model with correct target DB type
            Console.WriteLine("Creating blank model for RE...");
            Type pbType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0");
            if (pbType == null) { Console.Error.WriteLine("ERROR: PropertyBag not registered"); return 2; }
            dynamic createBag = Activator.CreateInstance(pbType);

            // Try creating model with correct Target_Server for this erwin version
            // Constants differ between erwin versions - probe to find the right one
            createBag.Add("Model_Type", "Combined");

            // Extract DB type code from connection string: SERVER=16:10:0 -> 16
            int dbTypeCode = 16; // default SQL Server
            var serverMatch = System.Text.RegularExpressions.Regex.Match(connStr, @"SERVER=(\d+):");
            if (serverMatch.Success) dbTypeCode = int.Parse(serverMatch.Groups[1].Value);

            // Try multiple Target_Server values (differs per erwin version)
            long[] targetServerCandidates;
            if (!string.IsNullOrEmpty(targetServer) && long.TryParse(targetServer, out long tsFromUI))
                targetServerCandidates = new[] { tsFromUI, dbTypeCode, 0 };
            else
                targetServerCandidates = new[] { (long)dbTypeCode, 0L };

            bool modelCreated = false;
            foreach (long tsCandidate in targetServerCandidates)
            {
                try
                {
                    createBag = Activator.CreateInstance(pbType);
                    createBag.Add("Model_Type", "Combined");
                    if (tsCandidate > 0)
                    {
                        createBag.Add("Target_Server", tsCandidate);
                        int tvVal = 10;
                        if (!string.IsNullOrEmpty(targetVersion) && int.TryParse(targetVersion, out int tv))
                            tvVal = tv;
                        createBag.Add("Target_Server_Version", tvVal);
                    }
                    pu = scapi.PersistenceUnits.Create(createBag);
                    modelCreated = true;
                    Console.WriteLine($"Model created with Target_Server={tsCandidate}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Target_Server={tsCandidate} failed: {ex.Message}");
                }
            }

            if (!modelCreated)
            {
                Console.Error.WriteLine("ERROR: Cannot create blank model with any Target_Server value");
                return 3;
            }

            Console.WriteLine($"Model name: {pu.Name}");

            // Step 2: Set up RE property bag
            dynamic reBag = Activator.CreateInstance(pbType);
            reBag.Add("System_Objects", false);
            reBag.Add("Oracle_Use_DBA_Views", false);
            reBag.Add("Synch_Owned_Only", false);
            reBag.Add("Case_Option", 25090); // None
            reBag.Add("Logical_Case_Option", 25045); // None
            reBag.Add("Infer_Primary_Keys", false);
            reBag.Add("Infer_Relations", false);
            reBag.Add("Remove_ERwin_Generated_Triggers", false);
            reBag.Add("Force_Physical_Name_Option", true);

            if (!string.IsNullOrEmpty(tableFilter))
                reBag.Add("Synch_Table_Filter_By_Name", tableFilter);
            if (!string.IsNullOrEmpty(schemaFilter))
                reBag.Add("Synch_Owned_Only_Name", schemaFilter);

            // Open a session on the PU before RE
            Console.WriteLine("Opening session on model...");
            dynamic session = null;
            try
            {
                // Check if PU has session
                bool hasSess = false;
                try { hasSess = pu.HasSession(); } catch { }
                Console.WriteLine($"  HasSession: {hasSess}");

                if (!hasSess)
                {
                    // Open session
                    session = scapi.Sessions;
                    int sessCount = 0;
                    try { sessCount = session.Count; } catch { }
                    Console.WriteLine($"  Sessions.Count: {sessCount}");

                    // Try to open session on PU
                    try
                    {
                        dynamic sess = session.Add();
                        sess.Open(pu, 0, 0);
                        Console.WriteLine("  Session opened on PU");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Session.Open: {ex.Message}");
                        // Try alternative: just proceed without explicit session
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"  Session setup: {ex.Message}"); }

            // Probe: dump PU PropertyBag
            Console.WriteLine("--- PU PropertyBag ---");
            try
            {
                dynamic puBag = pu.PropertyBag();
                int bagCount = puBag.Count;
                for (int b = 0; b < Math.Min(bagCount, 10); b++)
                {
                    try
                    {
                        string bn = puBag.Name(b)?.ToString() ?? "";
                        string bv = puBag.Value(bn)?.ToString() ?? "";
                        Console.WriteLine($"  {bn} = {bv}");
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Console.WriteLine($"  PU PropertyBag: {ex.Message}"); }

            // Step 3: Reverse Engineer from DB
            Console.WriteLine($"RE from DB...");
            string rePassword = string.IsNullOrEmpty(dbPass) ? null : dbPass;

            try
            {
                pu.ReverseEngineer(reBag, "", connStr, rePassword);
                Console.WriteLine("RE completed.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: RE failed: {ex.Message}");
                return 3;
            }

            // Step 4: Save RE'd model, reopen with session (Create doesn't open session, Add does)
            string tempModel = Path.Combine(Path.GetTempPath(), $"erwin_redb_{Guid.NewGuid():N}.erwin");
            try
            {
                pu.Save(tempModel, "OVF=Yes");
                Console.WriteLine($"Model saved: {new FileInfo(tempModel).Length} bytes");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: Save failed: {ex.Message}");
                return 3;
            }

            // Close Create'd PU, reopen with Add (gets session)
            try { scapi.PersistenceUnits.Remove(pu, false); } catch { }
            pu = null;

            // Check model content before reopening
            Console.WriteLine("Checking model file content...");
            try
            {
                string xml = File.ReadAllText(tempModel);
                // Look for Entity/Table markers in erwin XML
                int entityCount = System.Text.RegularExpressions.Regex.Matches(xml, "Entity\\.").Count;
                int tableCount = System.Text.RegularExpressions.Regex.Matches(xml, "Physical_Name").Count;
                Console.WriteLine($"Model XML: {xml.Length} chars, 'Entity.' refs={entityCount}, 'Physical_Name' refs={tableCount}");
                // Show first 500 chars
                Console.WriteLine($"First 500 chars: {xml.Substring(0, Math.Min(500, xml.Length))}");
            }
            catch (Exception ex) { Console.WriteLine($"XML read: {ex.Message}"); }

            Console.WriteLine("Reopening with session...");
            pu = scapi.PersistenceUnits.Add(tempModel, "");
            Console.WriteLine($"Reopened: {pu.Name}, HasSession={pu.HasSession()}");

            // Verify model has content
            try
            {
                dynamic sess = scapi.Sessions.Item(0);
                dynamic mo = sess.ModelObjects;
                dynamic root = mo.Root;
                dynamic entities = mo.Collect(root, "Entity");
                Console.WriteLine($"Entities: {entities.Count}");
                int ei = 0;
                foreach (dynamic ent in entities) { if (ei++ >= 5) break; try { Console.WriteLine($"  - {ent.Name}"); } catch { } }
            }
            catch (Exception ex) { Console.WriteLine($"Content check: {ex.Message}"); }

            // Step 5: Generate DDL
            Console.WriteLine($"Generating DDL...");
            string feArg = string.IsNullOrEmpty(feOption) ? "" : feOption;
            try { pu.FEModel_DDL(output, feArg); }
            catch (Exception ex) { Console.WriteLine($"FEModel_DDL: {ex.Message}"); }

            // Cleanup temp
            try { File.Delete(tempModel); } catch { }

            if (File.Exists(output))
            {
                long size = new FileInfo(output).Length;
                Console.WriteLine($"DDL output: {size} bytes");
                return size > 0 ? 0 : 4;
            }
            else
            {
                Console.Error.WriteLine("No DDL output");
                return 4;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 3;
        }
        finally
        {
            if (pu != null) try { scapi.PersistenceUnits.Remove(pu, false); } catch { }
            if (scapi != null) try { Marshal.ReleaseComObject(scapi); } catch { }
        }
    }
}
