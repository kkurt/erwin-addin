using System;
using System.Runtime.InteropServices;

/// <summary>
/// Standalone probe to find valid Target_Server values for erwin r10.
/// Run: dotnet run -- --action=probe
/// DELETE THIS FILE after finding the value.
/// </summary>
class ProbeTargetServer
{
    public static int Run()
    {
        Console.WriteLine("=== Target_Server Value Probe ===");
        dynamic scapi = null;

        try
        {
            Type scapiType = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0");
            if (scapiType == null) { Console.Error.WriteLine("SCAPI not registered"); return 1; }
            scapi = Activator.CreateInstance(scapiType);
            Console.WriteLine($"SCAPI version: {scapi.Version}");

            Type pbType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0");

            // Try ranges of Target_Server values
            // Known: 1075859016 fails on r10
            // Try small values (1-50), then known erwin type codes
            long[] candidates = {
                // Small integers
                1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
                // Larger values that might be SQL Server codes
                100, 200, 256, 512, 1024, 2048, 4096,
                // Hex patterns
                0x10, 0x20, 0x40, 0x80, 0x100, 0x200, 0x400, 0x800, 0x1000,
                // Known erwin15 values
                1075859016, 1075859011, 1075859009, 1075859030, 1075859493, 1075859495,
                // Possible r10 values (based on patterns)
                1075838536, 1075838537, 1075838538, 1075838539, 1075838540,
                // Try with 0x40200000 base
                0x40200000, 0x40200001, 0x40200010, 0x40200100,
                0x40200900, 0x40200908, 0x40200910, 0x40200918, 0x40200920, 0x40200928,
                0x40200930, 0x40200938, 0x40200940, 0x40200948, 0x40200950,
                // Try with 0x40100000 base (older erwin?)
                0x40100908, 0x40100910, 0x40100918, 0x40100920, 0x40100928,
                0x40100930, 0x40100938, 0x40100940, 0x40100948, 0x40100950,
            };

            // First: create a blank model WITHOUT Target_Server and check what props it has
            Console.WriteLine("\n--- Blank model probe ---");
            try
            {
                dynamic bag0 = Activator.CreateInstance(pbType);
                bag0.Add("Model_Type", "Combined");
                dynamic pu0 = scapi.PersistenceUnits.Create(bag0);
                Console.WriteLine($"Blank model: {pu0.Name}");

                // List all PropertyBag entries
                try
                {
                    dynamic pb = pu0.PropertyBag();
                    int cnt = pb.Count;
                    Console.WriteLine($"PU PropertyBag ({cnt} entries):");
                    for (int b = 0; b < cnt; b++)
                    {
                        try
                        {
                            string bn = pb.Name(b)?.ToString() ?? "";
                            string bv = pb.Value(bn)?.ToString() ?? "";
                            Console.WriteLine($"  {bn} = {bv}");
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"PU PB error: {ex.Message}"); }

                // Try different property names for target server
                string[] propNames = {
                    "Target_Server", "TargetServer", "Target Server",
                    "Target_Database", "TargetDatabase", "Target Database",
                    "DB_Type", "DBType", "DatabaseType", "Database_Type",
                    "Platform", "Server_Type", "ServerType",
                    "Target_DBMS", "DBMS", "Target_Platform"
                };
                Console.WriteLine("\n--- Testing property names on Create ---");
                foreach (var pn in propNames)
                {
                    try
                    {
                        dynamic bag1 = Activator.CreateInstance(pbType);
                        bag1.Add("Model_Type", "Combined");
                        bag1.Add(pn, 16); // SQL Server code
                        dynamic pu1 = scapi.PersistenceUnits.Create(bag1);
                        Console.WriteLine($"  SUCCESS with '{pn}'=16 -> {pu1.Name}");
                        scapi.PersistenceUnits.Remove(pu1, false);
                    }
                    catch (Exception ex)
                    {
                        string msg = ex.Message.Length > 60 ? ex.Message.Substring(0, 60) : ex.Message;
                        Console.WriteLine($"  '{pn}' -> {msg}");
                    }
                }

                // Try string values for Target_Server
                Console.WriteLine("\n--- Testing string values for Target_Server ---");
                string[] strVals = { "SQLServer", "SQL Server", "MSSQL", "SS", "16", "ODBC", "SQL Server 2019" };
                foreach (var sv in strVals)
                {
                    try
                    {
                        dynamic bag2 = Activator.CreateInstance(pbType);
                        bag2.Add("Model_Type", "Combined");
                        bag2.Add("Target_Server", sv);
                        bag2.Add("Target_Server_Version", 10);
                        dynamic pu2 = scapi.PersistenceUnits.Create(bag2);
                        Console.WriteLine($"  SUCCESS with Target_Server='{sv}' -> {pu2.Name}");
                        scapi.PersistenceUnits.Remove(pu2, false);
                    }
                    catch (Exception ex)
                    {
                        string msg = ex.Message.Length > 60 ? ex.Message.Substring(0, 60) : ex.Message;
                        Console.WriteLine($"  '{sv}' -> {msg}");
                    }
                }

                // Try Model_Type variations
                Console.WriteLine("\n--- Testing Model_Type variations ---");
                string[] mtVals = { "Physical", "Logical", "Combined", "Logical/Physical" };
                foreach (var mt in mtVals)
                {
                    try
                    {
                        dynamic bag3 = Activator.CreateInstance(pbType);
                        bag3.Add("Model_Type", mt);
                        dynamic pu3 = scapi.PersistenceUnits.Create(bag3);
                        Console.WriteLine($"  Model_Type='{mt}' -> {pu3.Name}");
                        scapi.PersistenceUnits.Remove(pu3, false);
                    }
                    catch (Exception ex)
                    {
                        string msg = ex.Message.Length > 60 ? ex.Message.Substring(0, 60) : ex.Message;
                        Console.WriteLine($"  '{mt}' -> {msg}");
                    }
                }

                scapi.PersistenceUnits.Remove(pu0, false);
            }
            catch (Exception ex) { Console.WriteLine($"Blank model probe error: {ex.Message}"); }

            // Default Target_Server=1075858979 (erwin r10)
            // Try: create default model, then modify Target_Server via PropertyBag.put
            Console.WriteLine("\n--- Modify Target_Server on existing model ---");
            try
            {
                dynamic bag5 = Activator.CreateInstance(pbType);
                bag5.Add("Model_Type", "Combined");
                dynamic pu5 = scapi.PersistenceUnits.Create(bag5);

                string ts0 = pu5.PropertyBag().Value("Target_Server")?.ToString() ?? "";
                Console.WriteLine($"Default Target_Server: {ts0}");

                // Now try RE with the default model (Target_Server=1075858979)
                Console.WriteLine("\n--- RE test with default model ---");
                dynamic reBag = Activator.CreateInstance(pbType);
                reBag.Add("System_Objects", false);
                reBag.Add("Case_Option", 25090);
                reBag.Add("Force_Physical_Name_Option", true);

                string testConn = "SERVERR=16:10:0|AUTHENTICATION=4|USER=sa|1=3|2=MetaRepo|3=localhost";
                Console.WriteLine($"RE with: {testConn}");
                try
                {
                    pu5.ReverseEngineer(reBag, "", testConn, "Elite12345");
                    Console.WriteLine("RE completed!");

                    // Check PU PropertyBag after RE
                    Console.WriteLine("PU PropertyBag after RE:");
                    try
                    {
                        dynamic pb2 = pu5.PropertyBag();
                        int cnt2 = pb2.Count;
                        for (int b = 0; b < cnt2; b++)
                        {
                            try
                            {
                                string bn = pb2.Name(b)?.ToString() ?? "";
                                string bv = pb2.Value(bn)?.ToString() ?? "";
                                Console.WriteLine($"  {bn} = {bv}");
                            }
                            catch { }
                        }
                    }
                    catch { }

                    // Try: save model to file first, then open with session
                    string tempModel = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "probe_re.erwin");
                    Console.WriteLine($"Saving RE'd model to {tempModel}...");
                    try
                    {
                        pu5.Save(tempModel, "OVF=Yes");
                        long mSize = new System.IO.FileInfo(tempModel).Length;
                        Console.WriteLine($"Model saved: {mSize} bytes");
                    }
                    catch (Exception ex) { Console.WriteLine($"Save: {ex.Message}"); }

                    // Remove old PU, reopen saved model with session
                    try { scapi.PersistenceUnits.Remove(pu5, false); pu5 = null; } catch { }

                    if (System.IO.File.Exists(tempModel))
                    {
                        Console.WriteLine("Reopening saved model...");
                        try
                        {
                            dynamic pu6 = scapi.PersistenceUnits.Add(tempModel, "");
                            Console.WriteLine($"Reopened: {pu6.Name}");

                            bool hasSess = false;
                            try { hasSess = pu6.HasSession(); } catch { }
                            Console.WriteLine($"HasSession: {hasSess}");

                            if (hasSess)
                            {
                                dynamic sess = scapi.Sessions.Item(0);
                                dynamic mo = sess.ModelObjects;
                                dynamic root = mo.Root;
                                string[] types = { "Entity", "Relationship", "View" };
                                foreach (var t in types)
                                {
                                    try
                                    {
                                        dynamic objs = mo.Collect(root, t);
                                        Console.WriteLine($"  {t}: {objs.Count}");
                                        int ei = 0;
                                        foreach (dynamic obj in objs)
                                        {
                                            if (ei >= 10) { Console.WriteLine("    ..."); break; }
                                            try { Console.WriteLine($"    - {obj.Name}"); } catch { }
                                            ei++;
                                        }
                                    }
                                    catch { }
                                }
                            }

                            // Try DDL
                            string testOut = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "probe_ddl.sql");
                            try
                            {
                                pu6.FEModel_DDL(testOut, "");
                                if (System.IO.File.Exists(testOut))
                                {
                                    long fsize = new System.IO.FileInfo(testOut).Length;
                                    Console.WriteLine($"FEModel_DDL: {fsize} bytes");
                                    if (fsize > 0 && fsize < 3000)
                                        Console.WriteLine(System.IO.File.ReadAllText(testOut));
                                }
                            }
                            catch (Exception ex) { Console.WriteLine($"FEModel_DDL: {ex.Message}"); }
                            try { System.IO.File.Delete(testOut); } catch { }

                            scapi.PersistenceUnits.Remove(pu6, false);
                        }
                        catch (Exception ex) { Console.WriteLine($"Reopen: {ex.Message}"); }
                        try { System.IO.File.Delete(tempModel); } catch { }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"RE failed: {ex.Message}"); }

                scapi.PersistenceUnits.Remove(pu5, false);
            }
            catch (Exception ex) { Console.WriteLine($"Modify probe error: {ex.Message}"); }

            Console.WriteLine("\n=== Probe Complete ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
        finally
        {
            if (scapi != null) try { Marshal.ReleaseComObject(scapi); } catch { }
        }
    }
}
