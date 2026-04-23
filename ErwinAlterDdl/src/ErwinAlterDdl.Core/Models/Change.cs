using System.Text.Json.Serialization;

namespace EliteSoft.Erwin.AlterDdl.Core.Models;

/// <summary>
/// Base for all semantic diff entries produced by the correlator. Uses a sealed
/// hierarchy so SQL emitters and visitors can pattern-match exhaustively.
/// JSON serialization uses a "kind" discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntityAdded), nameof(EntityAdded))]
[JsonDerivedType(typeof(EntityDropped), nameof(EntityDropped))]
[JsonDerivedType(typeof(EntityRenamed), nameof(EntityRenamed))]
[JsonDerivedType(typeof(SchemaMoved), nameof(SchemaMoved))]
[JsonDerivedType(typeof(AttributeAdded), nameof(AttributeAdded))]
[JsonDerivedType(typeof(AttributeDropped), nameof(AttributeDropped))]
[JsonDerivedType(typeof(AttributeRenamed), nameof(AttributeRenamed))]
[JsonDerivedType(typeof(AttributeTypeChanged), nameof(AttributeTypeChanged))]
public abstract record Change(ObjectRef Target);

// ---------- Entity-level ----------

public sealed record EntityAdded(ObjectRef Target) : Change(Target);

public sealed record EntityDropped(ObjectRef Target) : Change(Target);

public sealed record EntityRenamed(ObjectRef Target, string OldName) : Change(Target);

/// <summary>
/// Entity kept its identity (ObjectId) but moved from one schema to another.
/// Examples: MSSQL TBL-04 sales.ORDER_ITEM -> ops.ORDER_ITEM.
/// </summary>
public sealed record SchemaMoved(ObjectRef Target, string OldSchema, string NewSchema) : Change(Target);

// ---------- Attribute-level ----------

public sealed record AttributeAdded(ObjectRef Target, ObjectRef ParentEntity) : Change(Target);

public sealed record AttributeDropped(ObjectRef Target, ObjectRef ParentEntity) : Change(Target);

public sealed record AttributeRenamed(
    ObjectRef Target,
    ObjectRef ParentEntity,
    string OldName) : Change(Target);

public sealed record AttributeTypeChanged(
    ObjectRef Target,
    ObjectRef ParentEntity,
    string LeftType,
    string RightType) : Change(Target);
