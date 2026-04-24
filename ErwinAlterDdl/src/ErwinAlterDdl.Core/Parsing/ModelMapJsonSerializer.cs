using System.Text.Json;

using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AlterDdl.Core.Parsing;

/// <summary>
/// Bridges <see cref="ErwinModelMap"/> and the wire-level
/// <see cref="ErwinModelMapDto"/> JSON so the Core pipeline can accept model
/// maps produced by an out-of-process Worker (no <c>.xml</c> sibling needed)
/// alongside in-memory test construction.
/// </summary>
public static class ModelMapJsonSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ErwinModelMapDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public static ErwinModelMapDto DeserializeDto(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var dto = JsonSerializer.Deserialize<ErwinModelMapDto>(json, JsonOptions)
            ?? throw new InvalidDataException("model-map JSON deserialized to null");
        if (!string.Equals(dto.SchemaVersion, ErwinModelMapDto.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new NotSupportedException(
                $"model-map JSON schema '{dto.SchemaVersion}' is not supported (expected '{ErwinModelMapDto.CurrentSchemaVersion}')");
        if (dto.Objects is null)
            throw new InvalidDataException("model-map JSON is missing the 'objects' array");
        return dto;
    }

    public static ErwinModelMap Deserialize(string json) => BuildMap(DeserializeDto(json));

    public static ErwinModelMap DeserializeFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("model-map json missing", path);
        return Deserialize(File.ReadAllText(path));
    }

    /// <summary>
    /// Rebuild an <see cref="ErwinModelMap"/> from a DTO. For attributes we
    /// use the DTO's <see cref="ObjectNodeDto.OwningEntityName"/> if set and
    /// fall back to walking the parent chain up to a node of class
    /// <c>Entity</c> (mirroring the XML parser's behavior).
    /// </summary>
    public static ErwinModelMap BuildMap(ErwinModelMapDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var byId = new Dictionary<string, ObjectRef>(StringComparer.Ordinal);
        var idByClassThenName = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var attrByEntityDotName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in dto.Objects)
        {
            if (string.IsNullOrWhiteSpace(node.ObjectId)) continue;
            if (byId.ContainsKey(node.ObjectId)) continue;

            byId[node.ObjectId] = new ObjectRef(node.ObjectId, node.Name ?? string.Empty, node.Class ?? string.Empty)
            {
                ParentObjectId = node.ParentObjectId,
            };

            if (!idByClassThenName.TryGetValue(node.Class ?? string.Empty, out var nameMap))
            {
                nameMap = new Dictionary<string, string>(StringComparer.Ordinal);
                idByClassThenName[node.Class ?? string.Empty] = nameMap;
            }
            nameMap.TryAdd(node.Name ?? string.Empty, node.ObjectId);
        }

        foreach (var node in dto.Objects)
        {
            if (!string.Equals(node.Class, "Attribute", StringComparison.Ordinal)) continue;
            var entityName = node.OwningEntityName ?? FindEntityAncestorName(byId, node.ParentObjectId);
            if (!string.IsNullOrEmpty(entityName))
                attrByEntityDotName.TryAdd($"{entityName}.{node.Name}", node.ObjectId);
        }

        return new ErwinModelMap(byId, idByClassThenName, attrByEntityDotName);
    }

    /// <summary>
    /// Produce a flat DTO snapshot of an existing <see cref="ErwinModelMap"/>.
    /// Useful for tests / tooling that need to round-trip a map through JSON
    /// without going through the Worker.
    /// </summary>
    public static ErwinModelMapDto ToDto(ErwinModelMap map, string sourceErwinPath)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceErwinPath);

        var nodes = new List<ObjectNodeDto>();
        foreach (var obj in map.AllObjects())
        {
            string? owningEntity = null;
            if (obj.Class == "Attribute" && obj.ParentObjectId is not null && map.TryGetById(obj.ParentObjectId, out var parent))
                owningEntity = parent.Class == "Entity" ? parent.Name : null;
            nodes.Add(new ObjectNodeDto(obj.ObjectId, obj.Name, obj.Class, obj.ParentObjectId, owningEntity));
        }
        return new ErwinModelMapDto(ErwinModelMapDto.CurrentSchemaVersion, sourceErwinPath, nodes);
    }

    private static string? FindEntityAncestorName(Dictionary<string, ObjectRef> byId, string? parentObjectId)
    {
        var current = parentObjectId;
        var guard = 0;
        while (!string.IsNullOrEmpty(current) && guard++ < 100)
        {
            if (!byId.TryGetValue(current, out var node)) return null;
            if (node.Class == "Entity") return node.Name;
            current = node.ParentObjectId;
        }
        return null;
    }
}
