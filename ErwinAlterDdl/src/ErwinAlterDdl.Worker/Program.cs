using System.Runtime.InteropServices;
using System.Text.Json;

using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

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
///   erwin-alter-ddl-worker dump-model --erwin &lt;path&gt; --out &lt;jsonPath&gt;
///                                     (walks ModelObjects and emits an
///                                     ErwinModelMapDto JSON; consumed by
///                                     WorkerJsonModelMapProvider)
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

    [STAThread]
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
                "dump-model" => RunDumpModel(kv),
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
        finally
        {
            KillLingeringErwinProcesses();
        }
    }

    // ---------- subcommands ----------

    private static int RunMetadata(Dictionary<string, string> kv)
    {
        var erwinPath = Require(kv, "--erwin");
        ClearReadOnly(erwinPath);

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
        ClearReadOnly(left);
        ClearReadOnly(right);

        dynamic scapi = CreateScapi();
        var bagType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0", throwOnError: true)!;
        dynamic bag = Activator.CreateInstance(bagType)!;
        dynamic pu = scapi.PersistenceUnits.Create(bag);

        bool ok = pu.CompleteCompare(left, right, outPath, preset, level, "");
        if (!ok) return Fail(ExitOperationFailed, "CompleteCompare returned false");
        if (!File.Exists(outPath)) return Fail(ExitOperationFailed, "xls not produced");

        var info = new FileInfo(outPath);
        WriteJson(new CompareArtifact(outPath, info.Length, 0));
        Console.Out.Flush();

        // SCAPI r10.10 triggers AccessViolation (0xC0000005) during
        // Marshal.FinalReleaseComObject for the CC output bag/pu pair AFTER the
        // XLS is already written. Nothing useful can happen after this point;
        // we skip the managed finally + let the OS reclaim the COM handles.
        // KillLingeringErwinProcesses in Main.finally still runs via atexit-like
        // handlers on normal exit, and OOP session pre-kills on next call.
        Environment.Exit(ExitOk);
        return ExitOk; // unreachable but required for compiler
    }

    private static int RunDdl(Dictionary<string, string> kv)
    {
        var erwinPath = Require(kv, "--erwin");
        var outPath = Require(kv, "--out");
        var feXml = kv.GetValueOrDefault("--fe-option-xml", "");
        ClearReadOnly(erwinPath);

        dynamic scapi = CreateScapi();
        dynamic pu = scapi.PersistenceUnits.Add(erwinPath, "");

        dynamic bag = pu.PropertyBag(null, true);
        string target = SafeBagString(bag, "Target_Server");
        bool ok = pu.FEModel_DDL(outPath, feXml);
        if (!ok) return Fail(ExitOperationFailed, "FEModel_DDL returned false");
        if (!File.Exists(outPath)) return Fail(ExitOperationFailed, "sql not produced");
        WriteJson(new DdlArtifact(outPath, new FileInfo(outPath).Length, target));
        Console.Out.Flush();

        // Same cleanup-AV risk as CC; bail via OS exit to skip the managed
        // finally and dodge Marshal.FinalReleaseComObject on a dirty handle.
        Environment.Exit(ExitOk);
        return ExitOk;
    }

    private static int RunDumpModel(Dictionary<string, string> kv)
    {
        var erwinPath = Require(kv, "--erwin");
        var outPath = Require(kv, "--out");
        // --disposition flags let the caller open a Mart-hosted PU read-only
        // (e.g. "OVM=Yes" for versioned access). Disk-based models ignore it.
        var disposition = kv.GetValueOrDefault("--disposition", "");

        bool isMartLocator = erwinPath.StartsWith("mart://", StringComparison.OrdinalIgnoreCase)
            || erwinPath.StartsWith("erwin://", StringComparison.OrdinalIgnoreCase);
        if (!isMartLocator) ClearReadOnly(erwinPath);

        dynamic scapi = CreateScapi();
        dynamic pu = scapi.PersistenceUnits.Add(erwinPath, disposition);
        dynamic sess = scapi.Sessions.Add();
        try
        {
            sess.Open(pu, 0, 0); // SCD_SL_M0 - data level
            var nodes = new List<ObjectNodeDto>();
            dynamic modelObjects = sess.ModelObjects;
            dynamic root = modelObjects.Root;
            if (root is null)
                return Fail(ExitOperationFailed, "session.ModelObjects.Root returned null");

            CollectTopLevel(modelObjects, root, "Entity", nodes, walkNested: true);
            CollectTopLevel(modelObjects, root, "Relationship", nodes, walkNested: false);
            CollectTopLevel(modelObjects, root, "View", nodes, walkNested: false);
            CollectTopLevel(modelObjects, root, "Trigger_Template", nodes, walkNested: false);
            // erwin exposes sequences under different class names depending
            // on DBMS (Oracle_Sequence / Sequence / ER_Sequence). Try each.
            foreach (var cls in new[] { "Sequence", "Oracle_Sequence", "ER_Sequence" })
                CollectTopLevel(modelObjects, root, cls, nodes, walkNested: false);

            var dto = new ErwinModelMapDto(
                SchemaVersion: ErwinModelMapDto.CurrentSchemaVersion,
                SourceErwinPath: erwinPath,
                Objects: nodes);
            File.WriteAllText(outPath, ModelMapJsonSerializer.Serialize(dto),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            WriteJson(new { path = outPath, objectCount = nodes.Count });
            Console.Out.Flush();

            Environment.Exit(ExitOk);
            return ExitOk;
        }
        finally
        {
            try { sess.Close(); } catch { }
            TryRelease(sess);
            TryRelease(pu);
            TryRelease(scapi);
        }
    }

    private static void CollectTopLevel(
        dynamic modelObjects, dynamic root, string className,
        List<ObjectNodeDto> sink, bool walkNested)
    {
        dynamic items;
        try { items = modelObjects.Collect(root, className); }
        catch { return; }
        if (items is null) return;

        foreach (dynamic obj in items)
        {
            if (obj is null) continue;
            var node = ToNode(obj, owningEntityName: null);
            sink.Add(node);

            if (walkNested && className == "Entity")
            {
                // Nested Attribute + Key_Group under this Entity.
                TryCollectChildren(modelObjects, obj, "Attribute", node.Name, sink);
                TryCollectChildren(modelObjects, obj, "Key_Group", null, sink);
            }
        }
    }

    private static void TryCollectChildren(
        dynamic modelObjects, dynamic parent, string className,
        string? owningEntityName, List<ObjectNodeDto> sink)
    {
        dynamic items;
        try { items = modelObjects.Collect(parent, className); }
        catch { return; }
        if (items is null) return;

        foreach (dynamic obj in items)
        {
            if (obj is null) continue;
            sink.Add(ToNode(obj, owningEntityName));

            // Key_Group has Key_Group_Members (column list composition).
            // Phase 3.F+ may need those for PK/Index column rendering; for
            // now we just record the Key_Group itself.
        }
    }

    private static ObjectNodeDto ToNode(dynamic obj, string? owningEntityName)
    {
        string id = SafeStr(() => obj.ObjectId?.ToString());
        string name = SafeStr(() => obj.Name?.ToString());
        string cls = SafeStr(() => obj.ClassName?.ToString());
        string? parentId = null;
        try
        {
            dynamic ctx = obj.Context;
            if (ctx is not null)
                parentId = SafeStr(() => ctx.ObjectId?.ToString());
        }
        catch { }
        if (string.IsNullOrEmpty(parentId)) parentId = null;
        if (string.IsNullOrEmpty(owningEntityName)) owningEntityName = null;
        return new ObjectNodeDto(id, name, cls, parentId, owningEntityName);
    }

    private static string SafeStr(Func<string?> get)
    {
        try { return get() ?? string.Empty; }
        catch { return string.Empty; }
    }

    // ---------- helpers ----------

    private static object CreateScapi()
    {
        var t = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0", throwOnError: true)!;
        dynamic scapi = Activator.CreateInstance(t)
            ?? throw new InvalidOperationException("CreateInstance returned null for ERwin9.SCAPI.9.0");
        // Clear any stray PUs left over by a previous worker (or by the user
        // GUI) that happen to be attached to the same COM LocalServer. Without
        // this the next Add can trip the r10.10 state-pollution bug.
        try { scapi.PersistenceUnits.Clear(); } catch { /* best effort */ }
        return scapi;
    }

    /// <summary>
    /// Ensure input .erwin file is writable. After a CC run, SCAPI leaves the
    /// file with the Read-Only attribute set, which breaks the next
    /// PersistenceUnits.Add (COM 0x800407DC "File exists and read only").
    /// </summary>
    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch { /* best effort */ }
    }

    private static void KillLingeringErwinProcesses()
    {
        // After we release SCAPI, erwin.exe may linger as an idle COM server
        // and poison the next worker. Kill what we can reach (same user's
        // processes - system-owned ones will silently access-deny).
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("erwin"))
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(2000); }
            catch { /* best effort */ }
            finally { try { p.Dispose(); } catch { } }
        }
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
