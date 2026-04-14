using System;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Standalone helper for generating DDL from a specific Mart model version.
/// Runs as a SEPARATE process with its OWN SCAPI instance,
/// bypassing the "Mart user interface is active" restriction.
///
/// Usage: DdlHelper.exe --server=localhost --port=18170 --user=Kursat --pass=xxx
///                       --model=KKB/KKB_Demo --version=4 --output=C:\temp\v4.sql
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        string server = "", port = "", user = "", pass = "";
        string model = "", version = "", output = "", feOption = "";

        foreach (var arg in args)
        {
            var parts = arg.Split(new[] { '=' }, 2);
            if (parts.Length < 2) continue;
            string key = parts[0].TrimStart('-').ToLower();
            string val = parts[1];

            switch (key)
            {
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

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(model) ||
            string.IsNullOrEmpty(version) || string.IsNullOrEmpty(output))
        {
            Console.Error.WriteLine("Usage: DdlHelper --server=host --port=18170 --user=u --pass=p --model=lib/name --version=N --output=file.sql");
            return 1;
        }

        Console.WriteLine($"DdlHelper: server={server}:{port}, model={model}, version={version}");

        dynamic scapi = null;
        dynamic pu = null;

        try
        {
            // Create OWN SCAPI instance (separate process = separate instance)
            Console.WriteLine("Creating SCAPI instance...");
            Type scapiType = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0");
            if (scapiType == null)
            {
                Console.Error.WriteLine("ERROR: ERwin9.SCAPI.9.0 not registered");
                return 2;
            }
            scapi = Activator.CreateInstance(scapiType);
            Console.WriteLine($"SCAPI version: {scapi.Version}");

            // Add Mart ModelDirectory (establish connection)
            string martDir = $"mart://Mart?TRC=NO;SRV={server};PRT={port};ASR=MartServer;UID={user};PSW={pass}";
            Console.WriteLine("Connecting to Mart...");
            try { scapi.ModelDirectories.Add(martDir, ""); }
            catch (Exception ex) { Console.WriteLine($"ModelDirectories.Add: {ex.Message} (may already exist)"); }

            // Open specific version
            string martUrl = $"mart://Mart/{model}?TRC=NO;SRV={server};PRT={port};ASR=MartServer;UID={user};PSW={pass};VNO={version}";
            Console.WriteLine($"Opening v{version}...");
            pu = scapi.PersistenceUnits.Add(martUrl, "OVM=Yes");
            Console.WriteLine($"Model opened: {pu.Name}");

            // Generate DDL
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
            // Cleanup
            if (pu != null)
            {
                try { scapi.PersistenceUnits.Remove(pu, false); } catch { }
            }
            // Release COM
            if (scapi != null)
            {
                try { Marshal.ReleaseComObject(scapi); } catch { }
            }
        }
    }
}
