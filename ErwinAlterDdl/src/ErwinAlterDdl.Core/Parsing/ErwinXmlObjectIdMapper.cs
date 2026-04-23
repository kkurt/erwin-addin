using System.Xml.Linq;

using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AlterDdl.Core.Parsing;

/// <summary>
/// Read-only lookup tables built from a single <c>.erwin</c> XML export. Provides
/// class-aware and parent-scoped access to model objects by their ObjectId.
/// </summary>
public sealed class ErwinModelMap
{
    private readonly Dictionary<string, ObjectRef> _byId;
    private readonly Dictionary<string, Dictionary<string, string>> _idByClassThenName;
    private readonly Dictionary<string, string> _attributeIdByEntityDotName;

    internal ErwinModelMap(
        Dictionary<string, ObjectRef> byId,
        Dictionary<string, Dictionary<string, string>> idByClassThenName,
        Dictionary<string, string> attributeIdByEntityDotName)
    {
        _byId = byId;
        _idByClassThenName = idByClassThenName;
        _attributeIdByEntityDotName = attributeIdByEntityDotName;
    }

    public int TotalObjectCount => _byId.Count;

    public bool TryGetById(string objectId, out ObjectRef objectRef)
    {
        return _byId.TryGetValue(objectId, out objectRef!);
    }

    /// <summary>Global name lookup within a class. Entity / Schema / View etc.</summary>
    public bool TryGetId(string className, string name, out string objectId)
    {
        if (_idByClassThenName.TryGetValue(className, out var map) && map.TryGetValue(name, out var id))
        {
            objectId = id;
            return true;
        }
        objectId = string.Empty;
        return false;
    }

    /// <summary>Attribute lookup scoped to its parent entity (names repeat across tables).</summary>
    public bool TryGetAttributeId(string entityName, string attributeName, out string objectId)
    {
        return _attributeIdByEntityDotName.TryGetValue($"{entityName}.{attributeName}", out objectId!);
    }

    public IEnumerable<ObjectRef> ObjectsOfClass(string className) =>
        _idByClassThenName.TryGetValue(className, out var map)
            ? map.Values.Select(id => _byId[id])
            : Enumerable.Empty<ObjectRef>();
}

public static class ErwinXmlObjectIdMapper
{
    /// <summary>
    /// Build a <see cref="ErwinModelMap"/> from a <c>.erwin</c> XML export file.
    /// </summary>
    public static ErwinModelMap ParseFile(string xmlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlPath);
        if (!File.Exists(xmlPath)) throw new FileNotFoundException(xmlPath);
        var doc = XDocument.Load(xmlPath);
        return BuildMap(doc);
    }

    /// <summary>Build from in-memory XML string. Useful for unit tests.</summary>
    public static ErwinModelMap ParseXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        return BuildMap(doc);
    }

    private static ErwinModelMap BuildMap(XDocument doc)
    {
        var byId = new Dictionary<string, ObjectRef>(StringComparer.Ordinal);
        var idByClassThenName = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var attrByEntityDotName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var el in doc.Descendants())
        {
            var id = el.Attribute("id")?.Value;
            var name = el.Attribute("name")?.Value;
            if (id is null || name is null) continue;
            var className = el.Name.LocalName;
            if (byId.ContainsKey(id)) continue;

            var parentId = FindClosestParentId(el);
            byId[id] = new ObjectRef(id, name, className) { ParentObjectId = parentId };

            if (!idByClassThenName.TryGetValue(className, out var nameMap))
            {
                nameMap = new Dictionary<string, string>(StringComparer.Ordinal);
                idByClassThenName[className] = nameMap;
            }
            nameMap.TryAdd(name, id);

            if (className == "Attribute")
            {
                var entity = el.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Entity");
                var entityName = entity?.Attribute("name")?.Value;
                if (entityName is not null)
                {
                    attrByEntityDotName.TryAdd($"{entityName}.{name}", id);
                }
            }
        }

        return new ErwinModelMap(byId, idByClassThenName, attrByEntityDotName);
    }

    private static string? FindClosestParentId(XElement el)
    {
        var p = el.Parent;
        while (p is not null)
        {
            var pid = p.Attribute("id")?.Value;
            if (pid is not null) return pid;
            p = p.Parent;
        }
        return null;
    }
}
