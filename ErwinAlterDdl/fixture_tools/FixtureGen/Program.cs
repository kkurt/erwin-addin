// FixtureGen: take v1.xml + v2.xml + md (case map) and produce v2_generated.xml
// with v1's UIDs transplanted onto v2's structure. Rename cases from md are honored
// (renamed object keeps v1's UID, only name attribute changes).
//
// Usage: dotnet run --project FixtureGen -- <v1.xml> <v2.xml> <md> <out.xml>

using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: FixtureGen <v1.xml> <v2.xml> <md-spec> <output.xml>");
    return 1;
}

var v1Path = Path.GetFullPath(args[0]);
var v2Path = Path.GetFullPath(args[1]);
var mdPath = Path.GetFullPath(args[2]);
var outPath = Path.GetFullPath(args[3]);

Console.WriteLine($"v1 XML:  {v1Path}");
Console.WriteLine($"v2 XML:  {v2Path}");
Console.WriteLine($"md spec: {mdPath}");
Console.WriteLine($"output:  {outPath}");

// ---------- 1. Parse md for renames + augment with expected-alter SQL scan ----------
Console.WriteLine("\n[1] Parse md renames");
var renames = ParseRenames(mdPath);
foreach (var r in renames)
    Console.WriteLine($"    RENAME[{r.Category}] {r.Old} -> {r.New}  (from md)");

// Also scan expected_alter SQL if present. MD case maps occasionally omit COL-level
// rename detail but the alter SQL has it as ground truth (RENAME COLUMN, sp_rename).
var mdDir = Path.GetDirectoryName(mdPath)!;
var mdStem = Path.GetFileNameWithoutExtension(mdPath);
var alterSqlCandidates = new[]
{
    Path.Combine(mdDir, "expected_alters", $"{mdStem}_v1_to_v2.sql"),
    Path.Combine(mdDir, $"{mdStem}_v1_to_v2.sql"),
    Path.Combine(mdDir, "..", "expected_alters", $"{mdStem}_v1_to_v2.sql")
};
foreach (var sqlPath in alterSqlCandidates)
{
    if (!File.Exists(sqlPath)) continue;
    Console.WriteLine($"    (scanning expected-alter SQL: {sqlPath})");
    var sqlRenames = ParseAlterSqlRenames(sqlPath);
    foreach (var r in sqlRenames)
    {
        if (renames.Any(x => x.Category == r.Category && x.Old == r.Old && x.New == r.New)) continue;
        renames.Add(r);
        Console.WriteLine($"    RENAME[{r.Category}] {r.Old} -> {r.New}  (from alter sql)");
    }
    break;
}
Console.WriteLine($"    total renames: {renames.Count}");

// ---------- 2. Load v1 XML, build per-class name->uid maps ----------
Console.WriteLine("\n[2] Load v1 XML, build name->uid maps");
var v1Doc = XDocument.Load(v1Path);
var v1EntityMap = new Dictionary<string, string>(StringComparer.Ordinal);       // name -> id
var v1AttrMap = new Dictionary<string, string>(StringComparer.Ordinal);         // "EntityName.AttrName" -> id
var v1ClassMap = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal); // class -> name -> id

foreach (var el in v1Doc.Descendants())
{
    var id = el.Attribute("id")?.Value;
    var name = el.Attribute("name")?.Value;
    if (id == null || name == null) continue;
    var cls = el.Name.LocalName;

    if (!v1ClassMap.TryGetValue(cls, out var m))
    {
        m = new Dictionary<string, string>(StringComparer.Ordinal);
        v1ClassMap[cls] = m;
    }
    if (!m.ContainsKey(name)) m[name] = id;

    if (cls == "Entity") v1EntityMap.TryAdd(name, id);
    if (cls == "Attribute")
    {
        var parentEntity = el.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Entity");
        var parentName = parentEntity?.Attribute("name")?.Value;
        if (parentName != null) v1AttrMap.TryAdd($"{parentName}.{name}", id);
    }
}
Console.WriteLine($"    v1 classes: {v1ClassMap.Count}, entities: {v1EntityMap.Count}, attrs: {v1AttrMap.Count}");

// ---------- 3. Load v2 XML and rewrite ids by name-match (class-aware) ----------
Console.WriteLine("\n[3] Load v2 XML, rewrite ids with v1 UIDs");
var v2Doc = XDocument.Load(v2Path);
int replacedFromName = 0;
int replacedFromRename = 0;
int kept = 0;
int total = 0;
var renameHits = new List<(string Category, string OldName, string NewName, string V1Id, string V2Id)>();
// Track every UID remap so we can rewrite references elsewhere in the XML.
// erwin's model XML uses the same UID literal in many places: as `id` on the
// defining element, AS CHILD ELEMENT TEXT on references (e.g. <Key_Group_Member>
// contains a reference to its Attribute by UID), and inside arrays. Changing
// only the `id` attribute leaves dangling references.
var uidRemap = new Dictionary<string, string>(StringComparer.Ordinal);

foreach (var el in v2Doc.Descendants())
{
    var idAttr = el.Attribute("id");
    var nameAttr = el.Attribute("name");
    if (idAttr == null || nameAttr == null) continue;
    total++;
    var v2Id = idAttr.Value;
    var name = nameAttr.Value;
    var cls = el.Name.LocalName;

    // 3a. Direct name match in v1 (class-aware)
    if (cls == "Attribute")
    {
        var parentEntity = el.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Entity");
        var parentName = parentEntity?.Attribute("name")?.Value;
        if (parentName != null && v1AttrMap.TryGetValue($"{parentName}.{name}", out var v1Id))
        {
            if (v2Id != v1Id) { idAttr.Value = v1Id; uidRemap[v2Id] = v1Id; replacedFromName++; } else { kept++; }
            continue;
        }
    }
    else if (CompositionSensitive.Contains(cls))
    {
        // Composition-sensitive classes where v1/v2 member lists differ structurally:
        // Key_Group (PK/UK/Index - PK-02 swap, IDX-04 column-add, IDX-06 uniqueness)
        // Key_Group_Member (index members; Index_Members_Order_Ref array consistency)
        // Relationship (FK - cascade change FK-03, column add / drop FK-02/FK-01)
        // Name-matching the UID forces v1 id on top of v2 composition and erwin emits
        // "ESX-112 invalid entries in the order list" when the Index_Members_Order_Ref
        // array no longer resolves. Skip name match. Only explicit rename mapping
        // preserves identity (rare for these classes).
    }
    else
    {
        if (v1ClassMap.TryGetValue(cls, out var m) && m.TryGetValue(name, out var v1Id))
        {
            if (v2Id != v1Id) { idAttr.Value = v1Id; uidRemap[v2Id] = v1Id; replacedFromName++; } else { kept++; }
            continue;
        }
    }

    // 3b. Rename match: this v2 name is the "New" side of a rename -> use v1's Old id
    bool renamed = false;
    foreach (var r in renames.Where(r => r.Category == cls && string.Equals(r.New, name, StringComparison.Ordinal)))
    {
        if (cls == "Attribute")
        {
            var parentEntity = el.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Entity");
            // After entity transplant, v2 entity name is still "entity name from v2";
            // attribute rename scope uses that same parent
            var parentName = parentEntity?.Attribute("name")?.Value;
            if (parentName != null && v1AttrMap.TryGetValue($"{parentName}.{r.Old}", out var v1Id))
            {
                idAttr.Value = v1Id;
                uidRemap[v2Id] = v1Id;
                replacedFromRename++;
                renameHits.Add((cls, r.Old, r.New, v1Id, v2Id));
                renamed = true;
                break;
            }
        }
        else
        {
            if (v1ClassMap.TryGetValue(cls, out var m) && m.TryGetValue(r.Old, out var v1Id))
            {
                idAttr.Value = v1Id;
                uidRemap[v2Id] = v1Id;
                replacedFromRename++;
                renameHits.Add((cls, r.Old, r.New, v1Id, v2Id));
                renamed = true;
                break;
            }
        }
    }

    if (!renamed) kept++;
}

Console.WriteLine($"    total id+name elements: {total}");
Console.WriteLine($"    replaced (direct name match): {replacedFromName}");
Console.WriteLine($"    replaced (rename match): {replacedFromRename}");
Console.WriteLine($"    kept (new or no match): {kept}");
foreach (var h in renameHits)
    Console.WriteLine($"      rename applied: [{h.Category}] {h.OldName} -> {h.NewName}  v1Id={h.V1Id}  (was v2Id={h.V2Id})");

// ---------- 4. Save generated v2 + rewrite UID references across the file ----------
Console.WriteLine($"\n[4] Save to {outPath}");
var settings = new System.Xml.XmlWriterSettings
{
    Encoding = new System.Text.UTF8Encoding(false),
    Indent = false,
    OmitXmlDeclaration = false,
    NewLineHandling = System.Xml.NewLineHandling.None
};
using (var writer = System.Xml.XmlWriter.Create(outPath, settings))
{
    v2Doc.Save(writer);
}

// Post-process: every place in the XML text that still contains an old v2 UID
// (in child element text, property arrays, Branch_Log, etc.) gets rewritten to
// the v1 UID. Without this, Key_Group_Member / Relationship references dangle
// and erwin logs "EBS-1051 Attribute ... not found".
if (uidRemap.Count > 0)
{
    var text = File.ReadAllText(outPath, System.Text.Encoding.UTF8);
    int totalOccurrencesReplaced = 0;
    int distinctUidsThatHadExtraRefs = 0;
    foreach (var kv in uidRemap)
    {
        // Count occurrences of old UID BEFORE replacement (old and new length are equal,
        // so text.Length won't change; must count instances explicitly).
        int occ = 0;
        int pos = 0;
        while ((pos = text.IndexOf(kv.Key, pos, StringComparison.Ordinal)) >= 0)
        {
            occ++;
            pos += kv.Key.Length;
        }
        if (occ == 0) continue;
        text = text.Replace(kv.Key, kv.Value, StringComparison.Ordinal);
        totalOccurrencesReplaced += occ;
        distinctUidsThatHadExtraRefs++;
    }
    File.WriteAllText(outPath, text, new System.Text.UTF8Encoding(false));
    Console.WriteLine($"    uidRemap entries: {uidRemap.Count}");
    Console.WriteLine($"    post-save UID text occurrences replaced: {totalOccurrencesReplaced} (across {distinctUidsThatHadExtraRefs} distinct UIDs)");
}

Console.WriteLine($"    output size: {new FileInfo(outPath).Length} bytes");

Console.WriteLine("\n[OK] done.");
return 0;

// ---------- helpers ----------
static List<Rename> ParseRenames(string mdPath)
{
    var result = new List<Rename>();
    var arrowPattern = new Regex(@"`([^`]+)`\s*(?:→|->)\s*`([^`]+)`");
    var caseIdPattern = new Regex(@"(TBL|COL|IDX|FK|UQ|PK|TRG|SEQ|VW|CHK)-\d+", RegexOptions.IgnoreCase);

    foreach (var line in File.ReadAllLines(mdPath))
    {
        var lower = line.ToLowerInvariant();
        // Only consider lines that clearly talk about a rename
        if (!lower.Contains("rename")) continue;

        var arrows = arrowPattern.Matches(line);
        if (arrows.Count == 0) continue;

        var caseId = caseIdPattern.Match(line);
        string? category = null;
        if (caseId.Success)
        {
            category = caseId.Groups[1].Value.ToUpperInvariant() switch
            {
                "TBL" => "Entity",
                "COL" => "Attribute",
                // Erwin XML represents indexes, PK and unique constraints all under Key_Group class
                "IDX" => "Key_Group",
                "PK"  => "Key_Group",
                "UQ"  => "Key_Group",
                "FK"  => "Relationship",
                "TRG" => "Trigger_Template",
                "SEQ" => "Sequence",
                "VW"  => "View",
                "CHK" => "Validation_Rule",
                _ => null
            };
        }
        // Heuristic fallback: inventory row without case id column but with "rename" and two schema.names
        category ??= "Entity"; // inventory rows are always entity-level

        foreach (Match m in arrows)
        {
            var oldRaw = m.Groups[1].Value.Trim();
            var newRaw = m.Groups[2].Value.Trim();
            var oldName = StripSchema(oldRaw);
            var newName = StripSchema(newRaw);
            // Skip identical (some inventory rows have "X → X" meaning no-op)
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) continue;
            // Deduplicate (inventory + case map may both list the same rename)
            if (result.Any(r => r.Category == category && r.Old == oldName && r.New == newName)) continue;
            result.Add(new Rename(category!, oldName, newName));
        }
    }
    return result;
}

static List<Rename> ParseAlterSqlRenames(string sqlPath)
{
    // ground-truth rename extraction from the expected_alter SQL:
    //  - ALTER TABLE <schema>.<old> RENAME TO <new>         (Oracle, MSSQL sp_rename)
    //  - RENAME TABLE <schema>.<old> TO <new>               (DB2)
    //  - ALTER TABLE <schema>.<t> RENAME COLUMN <old> TO <new>  (all three dialects)
    //  - EXEC sp_rename '<schema>.<old>', '<new>'           (MSSQL)
    //  - EXEC sp_rename '<t>.<old>', '<new>', 'COLUMN'      (MSSQL column)
    var result = new List<Rename>();
    var text = File.ReadAllText(sqlPath);

    var tableRenameOracle = new Regex(
        @"ALTER\s+TABLE\s+(?:[\w\[\]""]+\.)?([\w\[\]""]+)\s+RENAME\s+TO\s+(?:[\w\[\]""]+\.)?([\w\[\]""]+)",
        RegexOptions.IgnoreCase);
    foreach (Match m in tableRenameOracle.Matches(text))
        result.Add(new Rename("Entity", Clean(m.Groups[1].Value), Clean(m.Groups[2].Value)));

    var tableRenameDb2 = new Regex(
        @"RENAME\s+TABLE\s+(?:[\w\[\]""]+\.)?([\w\[\]""]+)\s+TO\s+(?:[\w\[\]""]+\.)?([\w\[\]""]+)",
        RegexOptions.IgnoreCase);
    foreach (Match m in tableRenameDb2.Matches(text))
        result.Add(new Rename("Entity", Clean(m.Groups[1].Value), Clean(m.Groups[2].Value)));

    var colRename = new Regex(
        @"ALTER\s+TABLE\s+(?:[\w\[\]""]+\.)?[\w\[\]""]+\s+RENAME\s+COLUMN\s+([\w\[\]""]+)\s+TO\s+([\w\[\]""]+)",
        RegexOptions.IgnoreCase);
    foreach (Match m in colRename.Matches(text))
        result.Add(new Rename("Attribute", Clean(m.Groups[1].Value), Clean(m.Groups[2].Value)));

    var spRenameCol = new Regex(
        @"EXEC\s+sp_rename\s+'[^']*\.([\w]+)'\s*,\s*'([\w]+)'\s*,\s*'COLUMN'",
        RegexOptions.IgnoreCase);
    foreach (Match m in spRenameCol.Matches(text))
        result.Add(new Rename("Attribute", Clean(m.Groups[1].Value), Clean(m.Groups[2].Value)));

    var spRenameTbl = new Regex(
        @"EXEC\s+sp_rename\s+'(?:[^']*\.)?([\w]+)'\s*,\s*'([\w]+)'\s*(?:,\s*'OBJECT')?\s*;",
        RegexOptions.IgnoreCase);
    foreach (Match m in spRenameTbl.Matches(text))
        result.Add(new Rename("Entity", Clean(m.Groups[1].Value), Clean(m.Groups[2].Value)));

    return result;
    static string Clean(string s) => s.Trim('[', ']', '"', ' ');
}

static string StripSchema(string s)
{
    // Handle "app.CUSTOMER_BACKUP" -> "CUSTOMER_BACKUP"; leave "CUSTOMER_BACKUP" untouched.
    var lastDot = s.LastIndexOf('.');
    return lastDot >= 0 ? s[(lastDot + 1)..] : s;
}

internal sealed record Rename(string Category, string Old, string New);

internal static class CompositionSensitive
{
    private static readonly HashSet<string> _set = new(StringComparer.Ordinal)
    {
        "Key_Group",
        "Key_Group_Member",
        "Relationship"
    };
    public static bool Contains(string cls) => _set.Contains(cls);
}
