using System.Runtime.InteropServices;
using System.Text.Json;

using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AlterDdl.Worker;

/// <summary>
/// Short-lived SCAPI worker. Runs exactly one operation, prints the result
/// as JSON on stdout, then exits. Owns its own <c>erwin.exe</c> COM
/// LocalServer for the duration of the process, which guarantees clean
/// isolation between compare/ddl calls (defeats the r10.10 singleton
/// state-pollution bug).
///
/// Invocation (argv style, keeps tests simple):
///   erwin-alter-ddl-worker metadata --erwin &lt;path&gt;
///   erwin-alter-ddl-worker cc       --left &lt;path&gt; --right &lt;path&gt;
///                                   --out &lt;xlsPath&gt;
///                                   [--preset &lt;name-or-xml&gt;]
///                                   [--level LP|L|P|DB]
///   erwin-alter-ddl-worker ddl      --erwin &lt;path&gt; --out &lt;sqlPath&gt;
///                                   [--fe-option-xml &lt;path&gt;]
///
/// stdout = JSON payload (shape depends on subcommand).
/// stderr = diagnostic lines, error messages, exception stack traces.
/// exit   = 0 on success, 1 on bad args, 2 on COM failure, 3 on operation
///          returning false / throwing, 99 on unhandled exception.
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitBadArgs = 1;
    private const int ExitComActivation = 2;
    private const int ExitOperationFailed = 3;
    private const int ExitUnhandled = 99;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("usage: erwin-alter-ddl-worker <metadata|cc|ddl> [...options]");
                return ExitBadArgs;
            }

            var command = args[0].ToLowerInvariant();
            var kv = ParseArgs(args.AsSpan(1));

            return command switch
            {
                "metadata" => RunMetadata(kv),
                "cc" => RunCompleteCompare(kv),
                "ddl" => RunDdl(kv),
                _ => Fail(ExitBadArgs, $"unknown subcommand '{command}'"),
            };
        }
        catch (COMException comEx)
        {
            Console.Error.WriteLine($"COM error 0x{comEx.HResult:X8}: {comEx.Message}");
            Console.Error.WriteLine(comEx.StackTrace);
            return ExitComActivation;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unhandled: {ex}");
            return ExitUnhandled;
        }
    }

    // ---------- subcommands ----------

    private static int RunMetadata(Dictionary<string, string> kv)
    {
        var erwinPath = Require(kv, "--erwin");

        dynamic scapi = CreateScapi();
        dynamic pu = scapi.PersistenceUnits.Add(erwinPath, "");
        try
        {
            dynamic bag = pu.PropertyBag(null, true);
            var metadata = new ModelMetadata(
                PersistenceUnitId: SafeBagString(bag, "Persistence_Unit_Id"),
                Name: (pu.Name?.ToString() ?? string.Empty),
                ModelType: SafeBagString(bag, "Model_Type"),
                TargetServer: SafeBagString(bag, "Target_Server"),
                TargetServerVersion: SafeBagInt(bag, "Target_Server_Version"),
                TargetServerMinorVersion: SafeBagInt(bag, "Target_Server_Minor_Version"));
            WriteJson(metadata);
            return ExitOk;
        }
        finally
        {
            TryRelease(pu);
            TryRelease(scapi);
        }
    }

    private static int RunCompleteCompare(Dictionary<string, string> kv)
    {
        var left = Require(kv, "--left");
        var right = Require(kv, "--right");
        var outPath = Require(kv, "--out");
        var preset = kv.GetValueOrDefault("--preset", "Standard");
        var level = kv.GetValueOrDefault("--level", "LP");

        dynamic scapi = CreateScapi();
        var bagType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0", throwOnError: true)!;
        dynamic bag = Activator.CreateInstance(bagType)!;
        dynamic pu = scapi.PersistenceUnits.Create(bag);
        try
        {
            bool ok = pu.CompleteCompare(left, right, outPath, preset, level, "");
            if (!ok) return Fail(ExitOperationFailed, "CompleteCompare returned false");
            if (!File.Exists(outPath)) return Fail(ExitOperationFailed, "xls not produced");
            var info = new FileInfo(outPath);
            WriteJson(new CompareArtifact(outPath, info.Length, 0));
            return ExitOk;
        }
        finally
        {
            TryRelease(pu);
            TryRelease(bag);
            TryRelease(scapi);
        }
    }

    private static int RunDdl(Dictionary<string, string> kv)
    {
        var erwinPath = Require(kv, "--erwin");
        var outPath = Require(kv, "--out");
        var feXml = kv.GetValueOrDefault("--fe-option-xml", "");

        dynamic scapi = CreateScapi();
        dynamic pu = scapi.PersistenceUnits.Add(erwinPath, "");
        try
        {
            dynamic bag = pu.PropertyBag(null, true);
            string target = SafeBagString(bag, "Target_Server");
            bool ok = pu.FEModel_DDL(outPath, feXml);
            if (!ok) return Fail(ExitOperationFailed, "FEModel_DDL returned false");
            if (!File.Exists(outPath)) return Fail(ExitOperationFailed, "sql not produced");
            WriteJson(new DdlArtifact(outPath, new FileInfo(outPath).Length, target));
            return ExitOk;
        }
        finally
        {
            TryRelease(pu);
            TryRelease(scapi);
        }
    }

    // ---------- helpers ----------

    private static object CreateScapi()
    {
        var t = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0", throwOnError: true)!;
        return Activator.CreateInstance(t)
            ?? throw new InvalidOperationException("CreateInstance returned null for ERwin9.SCAPI.9.0");
    }

    private static Dictionary<string, string> ParseArgs(ReadOnlySpan<string> args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"unexpected positional arg: {args[i]}");
            if (i + 1 >= args.Length)
                throw new ArgumentException($"missing value for {args[i]}");
            dict[args[i]] = args[i + 1];
            i++;
        }
        return dict;
    }

    private static string Require(Dictionary<string, string> kv, string key)
    {
        if (!kv.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"missing required arg {key}");
        return value;
    }

    private static int Fail(int exitCode, string message)
    {
        Console.Error.WriteLine(message);
        return exitCode;
    }

    private static void WriteJson<T>(T value)
    {
        Console.Out.Write(JsonSerializer.Serialize(value, JsonOpts));
    }

    private static string SafeBagString(dynamic bag, string key)
    {
        try { return (string)(bag.Value(key) ?? string.Empty); }
        catch { return string.Empty; }
    }

    private static int SafeBagInt(dynamic bag, string key)
    {
        var s = SafeBagString(bag, key);
        return int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
    }

    private static void TryRelease(object? o)
    {
        try { if (o is not null) Marshal.FinalReleaseComObject(o); }
        catch { /* best effort */ }
    }
}
