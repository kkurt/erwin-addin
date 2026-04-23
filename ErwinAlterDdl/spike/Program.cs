// ErwinAlterDdl Phase 1 Spike
// Intent: PROVE the XLS + XML + FEModel_DDL correlation idea end-to-end on real AchModel files.
// Non-goal: emit alter SQL. That is Phase 3.
// Error handling: NONE by design (NEW_NEED.md section 6). Any failure crashes with full stack trace.
//
// Observed SCAPI quirk (r10.10): two Add'd PUs in the same COM session -> the 2nd FEModel_DDL
// fails with RPC_E_SERVERFAULT. Workaround: one COM session per logical step.

using System.Runtime.InteropServices;
using System.Xml.Linq;
using HtmlAgilityPack;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

if (args.Length != 3)
{
    Log.Fatal("Usage: ErwinAlterDdl.Spike <v1.erwin> <v2.erwin> <outDir>");
    return 1;
}

var v1Path = Path.GetFullPath(args[0]);
var v2Path = Path.GetFullPath(args[1]);
var outDir = Path.GetFullPath(args[2]);
Directory.CreateDirectory(outDir);
var v1Xml = Path.ChangeExtension(v1Path, ".xml");
var v2Xml = Path.ChangeExtension(v2Path, ".xml");

Log.Information("=== ErwinAlterDdl.Spike start ===");
Log.Information("v1: {Path}", v1Path);
Log.Information("v2: {Path}", v2Path);
Log.Information("v1.xml: {Path} (exists={Exists})", v1Xml, File.Exists(v1Xml));
Log.Information("v2.xml: {Path} (exists={Exists})", v2Xml, File.Exists(v2Xml));
Log.Information("out: {Path}", outDir);

var xlsPath = Path.Combine(outDir, "diff.xls");
var reuseXls = Environment.GetEnvironmentVariable("REUSE_XLS");

// NOTE: FEModel_DDL calls deferred to Phase 3. On r10.10 the SCAPI LocalServer is a
// singleton; second FEModel_DDL in same server lifetime fails with RPC_E_SERVERFAULT.
Log.Information("[NOTE] FEModel_DDL calls deferred to Phase 3 (SCAPI singleton bug)");

// SCAPI cleanup (Marshal.FinalReleaseComObject + GC) on r10.10 sometimes trips
// AccessViolation (0xC0000005) in COM interop marshaller AFTER the operation
// completed successfully (XLS is written). REUSE_XLS env var lets the test skip CC
// and use an already-produced XLS (unblocks cases where CC+cleanup crash PowerShell
// but the XLS is on disk).
if (!string.IsNullOrEmpty(reuseXls) && File.Exists(reuseXls))
{
    Log.Information("[REUSE] skipping CC, copying existing XLS {Src} -> {Dst}", reuseXls, xlsPath);
    File.Copy(reuseXls, xlsPath, overwrite: true);
}
else
{
    Log.Information("[SESSION] CompleteCompare v1 vs v2 (disk-based)");
    RunInFreshSession(scapi =>
    {
        var bagType = Type.GetTypeFromProgID("ERwin9.SCAPI.PropertyBag.9.0", throwOnError: true)!;
        dynamic bag = Activator.CreateInstance(bagType)!;
        dynamic pu = scapi.PersistenceUnits.Create(bag);
        bool r = pu.CompleteCompare(v1Path, v2Path, xlsPath, "Standard", "LP", "");
        Log.Information("  CC ret={R} size={S}", r, new FileInfo(xlsPath).Length);
        try { Marshal.FinalReleaseComObject(pu); } catch (Exception ex) { Log.Warning("pu release: {M}", ex.Message); }
        try { Marshal.FinalReleaseComObject(bag); } catch (Exception ex) { Log.Warning("bag release: {M}", ex.Message); }
    });
}

// [5] Parse CompleteCompare XLS as HTML
Log.Information("[5] Parse XLS as HTML");
var xlsHtml = new HtmlDocument();
xlsHtml.Load(xlsPath, System.Text.Encoding.UTF8);
var trNodes = xlsHtml.DocumentNode.SelectNodes("//tr") ?? new HtmlNodeCollection(null);
Log.Information("    total rows: {Count}", trNodes.Count);

var changes = new List<XlsRow>();
foreach (var tr in trNodes)
{
    var tds = tr.SelectNodes("./td");
    if (tds == null || tds.Count < 4) continue;
    var rawType = HtmlEntity.DeEntitize(tds[0].InnerText);
    int indent = rawType.TakeWhile(ch => ch == ' ' || ch == ' ').Count() / 3;
    var typeName = rawType.Trim();
    var left = HtmlEntity.DeEntitize(tds[1].InnerText).Trim();
    var status = HtmlEntity.DeEntitize(tds[2].InnerText).Trim();
    var right = HtmlEntity.DeEntitize(tds[3].InnerText).Trim();
    changes.Add(new XlsRow(indent, typeName, left, status, right));
}
var notEqualCount = changes.Count(c => c.Status == "Not Equal");
Log.Information("    parsed rows: {Count}, Not Equal: {NE}", changes.Count, notEqualCount);

// [6] Parse .erwin XML files - ObjectID <-> Name mapping
Log.Information("[6] Parse .erwin XMLs for ObjectID mapping");
var v1Map = BuildObjectMap(v1Xml);
var v2Map = BuildObjectMap(v2Xml);
Log.Information("    v1 objects: {V1}, v2 objects: {V2}", v1Map.Count, v2Map.Count);

// [7] Entity-level classification via ObjectID set algebra
Log.Information("[7] Entity classification via ObjectID diff");
var v1Entities = FilterClass(v1Map, "Entity");
var v2Entities = FilterClass(v2Map, "Entity");
var entityAdded = v2Entities.Keys.Except(v1Entities.Keys).ToList();
var entityDropped = v1Entities.Keys.Except(v2Entities.Keys).ToList();
var entityCommon = v1Entities.Keys.Intersect(v2Entities.Keys).ToList();

foreach (var id in entityAdded)
    Log.Information("    ENTITY_ADD     name={Name} ObjectID={Id}", v2Entities[id].Name, id);
foreach (var id in entityDropped)
    Log.Information("    ENTITY_DROP    name={Name} ObjectID={Id}", v1Entities[id].Name, id);
foreach (var id in entityCommon)
{
    if (!string.Equals(v1Entities[id].Name, v2Entities[id].Name, StringComparison.Ordinal))
    {
        Log.Information("    ENTITY_RENAME  {Old} -> {New} ObjectID={Id}",
            v1Entities[id].Name, v2Entities[id].Name, id);
    }
}
Log.Information("    entity summary: added={A} dropped={D} common={C}",
    entityAdded.Count, entityDropped.Count, entityCommon.Count);

// [8] Attribute-level classification
Log.Information("[8] Attribute classification via ObjectID diff");
var v1Attrs = FilterClass(v1Map, "Attribute");
var v2Attrs = FilterClass(v2Map, "Attribute");
var attrAdded = v2Attrs.Keys.Except(v1Attrs.Keys).ToList();
var attrDropped = v1Attrs.Keys.Except(v2Attrs.Keys).ToList();
var attrCommon = v1Attrs.Keys.Intersect(v2Attrs.Keys).ToList();
int attrRenameCount = 0;

foreach (var id in attrCommon)
{
    if (!string.Equals(v1Attrs[id].Name, v2Attrs[id].Name, StringComparison.Ordinal))
    {
        string parent = v1Attrs[id].ParentId != null && v1Map.TryGetValue(v1Attrs[id].ParentId!, out var p)
            ? p.Name : "?";
        Log.Information("    ATTR_RENAME    {Parent}.{Old} -> {New} ObjectID={Id}",
            parent, v1Attrs[id].Name, v2Attrs[id].Name, id);
        attrRenameCount++;
    }
}
foreach (var id in attrAdded)
{
    string parent = v2Attrs[id].ParentId != null && v2Map.TryGetValue(v2Attrs[id].ParentId!, out var p)
        ? p.Name : "?";
    Log.Information("    ATTR_ADD       {Parent}.{Name} ObjectID={Id}", parent, v2Attrs[id].Name, id);
}
foreach (var id in attrDropped)
{
    string parent = v1Attrs[id].ParentId != null && v1Map.TryGetValue(v1Attrs[id].ParentId!, out var p)
        ? p.Name : "?";
    Log.Information("    ATTR_DROP      {Parent}.{Name} ObjectID={Id}", parent, v1Attrs[id].Name, id);
}
Log.Information("    attr summary: added={A} dropped={D} renamed={R} common={C}",
    attrAdded.Count, attrDropped.Count, attrRenameCount, attrCommon.Count);

// [9] Type-change detection via XLS traversal (attr property "Physical Data Type" Not Equal)
Log.Information("[9] Type-change detection via XLS hierarchy walk");
string? ctxEntity = null, ctxAttr = null;
int typeChangeCount = 0;
foreach (var c in changes)
{
    if (c.Type == "Entity/Table") ctxEntity = c.Left;
    else if (c.Type == "Attribute/Column") ctxAttr = c.Left;
    else if (c.Type == "Physical Data Type" && c.Status == "Not Equal")
    {
        Log.Information("    ATTR_TYPE      {E}.{C}  {L}  ->  {R}", ctxEntity ?? "?", ctxAttr ?? "?", c.Left, c.Right);
        typeChangeCount++;
    }
}
Log.Information("    type-change rows: {N}", typeChangeCount);

Log.Information("=== ErwinAlterDdl.Spike end OK ===");
Log.CloseAndFlush();
return 0;

// -------- helpers --------
static void RunInFreshSession(Action<dynamic> action)
{
    var scapiType = Type.GetTypeFromProgID("ERwin9.SCAPI.9.0", throwOnError: true)!;
    dynamic? scapi = Activator.CreateInstance(scapiType);
    try
    {
        // erwin.exe is a singleton COM LocalServer; between sessions previous PUs
        // linger server-side and poison the next FEModel_DDL with RPC_E_SERVERFAULT.
        // Clear() purges the PersistenceUnits collection before we add ours.
        try { scapi!.PersistenceUnits.Clear(); Log.Verbose("PersistenceUnits.Clear() pre-action OK"); }
        catch (Exception ex) { Log.Warning("pre-action Clear threw: {Msg}", ex.Message); }

        action(scapi!);
    }
    finally
    {
        try { scapi?.PersistenceUnits.Clear(); Log.Verbose("PersistenceUnits.Clear() post-action OK"); }
        catch (Exception ex) { Log.Warning("post-action Clear threw: {Msg}", ex.Message); }
        if (scapi != null) Marshal.FinalReleaseComObject(scapi);
        scapi = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

static Dictionary<string, ErwinObj> BuildObjectMap(string xmlPath)
{
    var doc = XDocument.Load(xmlPath);
    var map = new Dictionary<string, ErwinObj>(StringComparer.Ordinal);
    foreach (var e in doc.Descendants())
    {
        var id = e.Attribute("id")?.Value;
        var name = e.Attribute("name")?.Value;
        if (id == null || name == null) continue;
        if (map.ContainsKey(id)) continue;
        var parentId = FindClosestParentId(e);
        map[id] = new ErwinObj(id, name, e.Name.LocalName, parentId);
    }
    return map;
}

static string? FindClosestParentId(XElement e)
{
    var p = e.Parent;
    while (p != null)
    {
        var pid = p.Attribute("id")?.Value;
        if (pid != null) return pid;
        p = p.Parent;
    }
    return null;
}

static Dictionary<string, ErwinObj> FilterClass(Dictionary<string, ErwinObj> map, string className)
{
    return map.Values
        .Where(o => string.Equals(o.Class, className, StringComparison.Ordinal))
        .GroupBy(o => o.Id)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
}

internal sealed record XlsRow(int Level, string Type, string Left, string Status, string Right);
internal sealed record ErwinObj(string Id, string Name, string Class, string? ParentId);
