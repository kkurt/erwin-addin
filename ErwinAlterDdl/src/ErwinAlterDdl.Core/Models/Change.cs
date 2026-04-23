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
[JsonDerivedType(typeof(AttributeNullabilityChanged), nameof(AttributeNullabilityChanged))]
[JsonDerivedType(typeof(AttributeDefaultChanged), nameof(AttributeDefaultChanged))]
[JsonDerivedType(typeof(AttributeIdentityChanged), nameof(AttributeIdentityChanged))]
[JsonDerivedType(typeof(KeyGroupAdded), nameof(KeyGroupAdded))]
[JsonDerivedType(typeof(KeyGroupDropped), nameof(KeyGroupDropped))]
[JsonDerivedType(typeof(KeyGroupRenamed), nameof(KeyGroupRenamed))]
[JsonDerivedType(typeof(ForeignKeyAdded), nameof(ForeignKeyAdded))]
[JsonDerivedType(typeof(ForeignKeyDropped), nameof(ForeignKeyDropped))]
[JsonDerivedType(typeof(ForeignKeyRenamed), nameof(ForeignKeyRenamed))]
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

// ---------- Key_Group-level (Primary Key / Unique Constraint / Index) ----------

public enum KeyGroupKind
{
    Unknown,
    PrimaryKey,
    UniqueConstraint,
    Index,
    InversionEntry,
}

public sealed record KeyGroupAdded(
    ObjectRef Target,
    ObjectRef ParentEntity,
    KeyGroupKind Kind = KeyGroupKind.Unknown) : Change(Target);

public sealed record KeyGroupDropped(
    ObjectRef Target,
    ObjectRef ParentEntity,
    KeyGroupKind Kind = KeyGroupKind.Unknown) : Change(Target);

public sealed record KeyGroupRenamed(
    ObjectRef Target,
    ObjectRef ParentEntity,
    string OldName,
    KeyGroupKind Kind = KeyGroupKind.Unknown) : Change(Target);

// ---------- Relationship (Foreign Key) ----------

public sealed record ForeignKeyAdded(ObjectRef Target) : Change(Target);

public sealed record ForeignKeyDropped(ObjectRef Target) : Change(Target);

public sealed record ForeignKeyRenamed(ObjectRef Target, string OldName) : Change(Target);

/// <summary>
/// NULL / NOT NULL flip on an attribute. The XLS row is "Null Option" with
/// values of the form "Null" or "Not Null" (SCAPI-emitted strings).
/// </summary>
public sealed record AttributeNullabilityChanged(
    ObjectRef Target,
    ObjectRef ParentEntity,
    bool LeftNullable,
    bool RightNullable) : Change(Target);

/// <summary>
/// Column DEFAULT added / dropped / modified. Either side may be empty when
/// the default is being added or removed. <see cref="RightDefault"/> empty
/// means "drop the default".
/// </summary>
public sealed record AttributeDefaultChanged(
    ObjectRef Target,
    ObjectRef ParentEntity,
    string LeftDefault,
    string RightDefault) : Change(Target);

/// <summary>
/// Identity / auto-increment on an attribute. In SQL Server this means the
/// column picks up <c>IDENTITY(seed, step)</c>. Most engines cannot toggle
/// identity with a simple ALTER COLUMN so emitters will usually emit a
/// marker comment plus the recommended drop+recreate approach.
/// </summary>
public sealed record AttributeIdentityChanged(
    ObjectRef Target,
    ObjectRef ParentEntity,
    bool LeftHasIdentity,
    bool RightHasIdentity) : Change(Target);
