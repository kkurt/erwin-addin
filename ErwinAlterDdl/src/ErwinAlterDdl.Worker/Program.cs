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
        if (!IsVirtualLocator(left)) ClearReadOnly(left);
        if (!IsVirtualLocator(right)) ClearReadOnly(right);

        dynamic scapi = CreateScapi();

        // CompleteCompare's BSTR API only accepts disk paths. If either
        // side is a Mart locator we open it as a PU and dump it to a temp
        // .erwin file first. Save() may corrupt the PU but this Worker
        // process is short-lived so we do not care.
        string leftDisk = left;
        string rightDisk = right;
        var tempFiles = new List<string>();
        try
        {
            if (IsVirtualLocator(left))
            {
                leftDisk = DumpMartLocatorToTempErwin(scapi, left, "left");
                tempFiles.Add(leftDisk);
            }
            if (IsVirtualLocator(right))
            {
                rightDisk = DumpMartLocatorToTempErwin(scapi, right, "right");
                tempFiles.Add(rightDisk);
            }

            var bagType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0", throwOnError: true)!;
            dynamic bag = Activator.CreateInstance(bagType)!;
            dynamic pu = scapi.PersistenceUnits.Create(bag);

            // SCAPI's CompleteCompare puts the SECOND argument's values in the
            // XLS "Left Value" column and the FIRST argument's values in the
            // "Right Value" column - which is opposite of what every other
            // file/diff tool does. Swap the args here so the report's Left
            // column lines up with our --left (baseline) and Right with
            // --right (target). Verified empirically against r10.10:
            //   call CompleteCompare(A, B) -> LeftValue=B's side, RightValue=A's side
            // so passing (right, left) flips it back to the intuitive form.
            bool ok = pu.CompleteCompare(rightDisk, leftDisk, outPath, preset, level, "");
            if (!ok) return Fail(ExitOperationFailed, "CompleteCompare returned false");
            if (!File.Exists(outPath)) return Fail(ExitOperationFailed, "xls not produced");

            var info = new FileInfo(outPath);
            WriteJson(new CompareArtifact(outPath, info.Length, 0));
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cc Mart-dump path failed: {ex.GetType().Name}: {ex.Message}");
            return ExitOperationFailed;
        }
        finally
        {
            foreach (var t in tempFiles)
            {
                try { if (File.Exists(t)) { File.SetAttributes(t, FileAttributes.Normal); File.Delete(t); } }
                catch { /* worker exit will release locks */ }
            }
        }

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
        var disposition = kv.GetValueOrDefault("--disposition", "");
        bool isMartLocator = erwinPath.StartsWith("mart://", StringComparison.OrdinalIgnoreCase)
            || erwinPath.StartsWith("erwin://", StringComparison.OrdinalIgnoreCase);
        if (!isMartLocator) ClearReadOnly(erwinPath);

        dynamic scapi = CreateScapi();
        dynamic pu = scapi.PersistenceUnits.Add(erwinPath, disposition);

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

            ApplySchemaPrefix(modelObjects, root, nodes);

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

        // For Entity / View, prefix with the schema (erwin's Table editor
        // exposes a Schema column - readable as a property on the object).
        if (cls == "Entity" || cls == "View")
        {
            var sch = ReadSchemaProperty(obj);
            if (!string.IsNullOrEmpty(sch))
                name = $"{sch}.{name}";
        }

        return new ObjectNodeDto(id, name, cls, parentId, owningEntityName);
    }

    /// <summary>
    /// Probe a small set of erwin metamodel property names that store the
    /// owning schema. Different DBMS adapters use slightly different keys
    /// (Schema / SQL_Server_Schema / Oracle_Owner / ...). The first one to
    /// return non-empty wins.
    /// </summary>
    private static string ReadSchemaProperty(dynamic obj)
    {
        foreach (var key in new[]
        {
            "Schema",
            "SQL_Server_Schema",
            "Oracle_Owner",
            "Owner_Schema",
            "Owner",
            "Physical_Schema",
            "Table_Owner",
            "Owner_Name",
            "Schema_Name",
            "Owner_Schema_Name",
            "DB_Schema_Name",
        })
        {
            try
            {
                var v = SafeStr(() => obj.Properties(key).Value?.ToString());
                if (IsRealSchemaValue(v)) return v;
            }
            catch { /* try next */ }
        }
        foreach (var directProbe in new Func<dynamic, string>[]
        {
            o => SafeStr(() => o.Schema?.Name?.ToString()),
            o => SafeStr(() => o.Owner?.Name?.ToString()),
            o => SafeStr(() => o.OwnerSchema?.Name?.ToString()),
            o => SafeStr(() => o.Schema?.ToString()),
            o => SafeStr(() => o.Owner?.ToString()),
        })
        {
            try
            {
                var v = directProbe(obj);
                if (IsRealSchemaValue(v)) return v;
            }
            catch { }
        }
        return string.Empty;
    }

    private static bool IsRealSchemaValue(string v) =>
        !string.IsNullOrEmpty(v)
        && !v.StartsWith("%", StringComparison.Ordinal)
        && !v.Equals("System.__ComObject", StringComparison.Ordinal);

    private static string SafeStr(Func<string?> get)
    {
        try { return get() ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// erwin's metamodel exposes the entity-owning schema under different
    /// class names depending on target server. We probe a small set
    /// (generic <c>Schema</c> first, then DB-specific variants) and build
    /// an entityId -> schemaName map from whichever responds. Names are
    /// rewritten to "schema.entity" so the emitter's QuoteQualified splits
    /// them back into <c>[schema].[entity]</c>.
    /// </summary>
    private static void ApplySchemaPrefix(dynamic modelObjects, dynamic root, List<ObjectNodeDto> nodes)
    {
        var schemaByObjectId = new Dictionary<string, string>(StringComparer.Ordinal);
        var schemaClassCandidates = new[]
        {
            "Schema",
            "Owner",
            "SQL_Server_Owner_Schema",
            "SQL_Server_Schema",
            "Oracle_Owner",
            "Oracle_Schema",
            "Postgres_Schema",
        };
        foreach (var cls in schemaClassCandidates)
        {
            try
            {
                dynamic schemas = modelObjects.Collect(root, cls);
                if (schemas is null) continue;
                int collected = 0;
                foreach (dynamic sch in schemas)
                {
                    if (sch is null) continue;
                    string schName = SafeStr(() => sch.Name?.ToString());
                    if (string.IsNullOrEmpty(schName)) continue;
                    foreach (var memberClass in new[] { "Entity", "View" })
                    {
                        try
                        {
                            dynamic members = modelObjects.Collect(sch, memberClass);
                            if (members is null) continue;
                            foreach (dynamic m in members)
                            {
                                if (m is null) continue;
                                string mid = SafeStr(() => m.ObjectId?.ToString());
                                if (!string.IsNullOrEmpty(mid))
                                {
                                    schemaByObjectId[mid] = schName;
                                    collected++;
                                }
                            }
                        }
                        catch { /* member class may not exist for this schema */ }
                    }
                }
                if (collected > 0)
                {
                    Console.Error.WriteLine($"dump-model: schema class '{cls}' mapped {collected} entity/view(s)");
                    break;
                }
            }
            catch
            {
                // class doesn't exist in this metamodel - try next candidate
            }
        }

        // Fallback: walk each entity's own owner via Context navigation. Some
        // erwin servicing levels surface the schema only via the parent chain
        // and not through Collect("Schema").
        if (schemaByObjectId.Count == 0)
        {
            try
            {
                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities is not null)
                {
                    foreach (dynamic e in allEntities)
                    {
                        if (e is null) continue;
                        string eid = SafeStr(() => e.ObjectId?.ToString());
                        if (string.IsNullOrEmpty(eid)) continue;
                        string schName = TryReadSchemaName(e);
                        if (!string.IsNullOrEmpty(schName))
                            schemaByObjectId[eid] = schName;
                    }
                }
            }
            catch { /* fallback best-effort */ }
        }

        if (schemaByObjectId.Count == 0) return;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Class != "Entity" && nodes[i].Class != "View") continue;
            // Skip if ToNode already attached a schema prefix via the
            // entity's own Property("Schema") - common case for SQL Server.
            if (nodes[i].Name.Contains('.')) continue;
            if (schemaByObjectId.TryGetValue(nodes[i].ObjectId, out var sch) && !string.IsNullOrEmpty(sch))
                nodes[i] = nodes[i] with { Name = $"{sch}.{nodes[i].Name}" };
        }
    }

    /// <summary>
    /// Walk the entity's parent chain looking for a node whose ClassName
    /// hints at a schema / owner. Returns its Name on first match, "" if
    /// nothing helpful is in the chain.
    /// </summary>
    private static string TryReadSchemaName(dynamic entity)
    {
        try
        {
            dynamic ctx = entity.Context;
            for (int i = 0; ctx is not null && i < 6; i++)
            {
                string cls = SafeStr(() => ctx.ClassName?.ToString());
                if (cls.IndexOf("Schema", StringComparison.OrdinalIgnoreCase) >= 0
                    || cls.IndexOf("Owner", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return SafeStr(() => ctx.Name?.ToString());
                }
                try { ctx = ctx.Context; } catch { break; }
            }
        }
        catch { }
        return string.Empty;
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

    private static bool IsVirtualLocator(string s) =>
        !string.IsNullOrEmpty(s)
        && (s.StartsWith("mart://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("erwin://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Open the given Mart locator as a read-only PU and dump it to a temp
    /// .erwin file so CompleteCompare (which only accepts disk paths) can
    /// consume it. The Worker process is short-lived; whatever damage Save()
    /// does to the live PU is irrelevant by the time we return.
    /// </summary>
    private static string DumpMartLocatorToTempErwin(dynamic scapi, string martLocator, string label)
    {
        string tempPath = Path.Combine(Path.GetTempPath(),
            $"erwin-cc-{label}-{Guid.NewGuid():N}.erwin");
        Console.Error.WriteLine($"cc: dumping Mart locator -> {tempPath}");
        dynamic pu = scapi.PersistenceUnits.Add(martLocator, "OVM=Yes");
        try
        {
            bool saved = pu.Save(tempPath);
            if (!saved || !File.Exists(tempPath))
                throw new InvalidOperationException($"PU.Save({tempPath}) returned false / file missing");
            Console.Error.WriteLine($"cc:   saved {new FileInfo(tempPath).Length} bytes");
            return tempPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
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
