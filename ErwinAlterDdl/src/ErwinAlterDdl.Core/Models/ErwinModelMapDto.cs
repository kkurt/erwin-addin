using System.Text.Json.Serialization;

namespace EliteSoft.Erwin.AlterDdl.Core.Models;

/// <summary>
/// Wire-level JSON representation of an <see cref="Parsing.ErwinModelMap"/>.
/// Produced by an out-of-process Worker that loaded a <c>.erwin</c> file via
/// SCAPI and walked <c>session.ModelObjects()</c>, consumed by the add-in or
/// CLI to rebuild the map without needing a sibling <c>.xml</c> export.
///
/// Kept intentionally flat so that downstream phases can extend the per-node
/// payload (e.g. property values) without breaking existing consumers: unknown
/// fields are ignored by <c>System.Text.Json</c> by default.
/// </summary>
public sealed record ErwinModelMapDto(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("sourceErwinPath")] string SourceErwinPath,
    [property: JsonPropertyName("objects")] IReadOnlyList<ObjectNodeDto> Objects)
{
    /// <summary>The only schema version we emit or accept today.</summary>
    public const string CurrentSchemaVersion = "1";
}

/// <summary>
/// Single object in an <see cref="ErwinModelMapDto"/>. Mirrors the fields that
/// <see cref="ObjectRef"/> carries plus the nearest <c>Entity</c> ancestor
/// name for attributes (fast-path for entity-scoped lookups on the consumer).
/// </summary>
public sealed record ObjectNodeDto(
    [property: JsonPropertyName("objectId")] string ObjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("parentObjectId")] string? ParentObjectId,
    [property: JsonPropertyName("owningEntityName")] string? OwningEntityName = null);
