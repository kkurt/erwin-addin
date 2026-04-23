namespace EliteSoft.Erwin.AlterDdl.Core.Models;

/// <summary>
/// Stable identity handle for any erwin model object. The <see cref="ObjectId"/>
/// is the `id` attribute from the .erwin XML (`{GUID}+N` format).
/// </summary>
public sealed record ObjectRef(string ObjectId, string Name, string Class)
{
    /// <summary>
    /// Parent object id if this object is nested under another addressable object
    /// (e.g. Attribute -> Entity). Null for top-level objects.
    /// </summary>
    public string? ParentObjectId { get; init; }
}
