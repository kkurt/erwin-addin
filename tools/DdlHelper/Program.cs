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
///
/// Usage:
///   DdlHelper.exe --action=ddl --server=host --port=18170 --user=u --pass=p
///                  --model=lib/name --version=N --output=file.sql
///
///   DdlHelper.exe --action=versions --server=host --port=18170 --user=u --pass=p
///                  --model=lib/name
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        string action = "ddl", server = "", port = "", user = "", pass = "";
        string model = "", version = "", output = "", feOption = "";

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
            }
        }

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(model))
        {
            Console.Error.WriteLine("Usage: DdlHelper --action=ddl|versions --server=host --port=18170 --user=u --pass=p --model=lib/name [--version=N --output=file.sql]");
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
}
