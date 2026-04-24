namespace EliteSoft.Erwin.AlterDdl.Core.Models;

/// <summary>
/// CompleteCompare invocation options. See NEW_NEED.md section 4.1 for the
/// underlying SCAPI signature.
/// </summary>
public sealed record CompareOptions
{
    /// <summary>
    /// Compare level. Default <see cref="CompareLevel.LogicalAndPhysical"/>
    /// (the "LP" string to SCAPI).
    /// </summary>
    public CompareLevel Level { get; init; } = CompareLevel.LogicalAndPhysical;

    /// <summary>
    /// Preset name ("Standard", "Advance", "Speed") OR absolute path to a custom
    /// CC option set XML file (e.g. `temp1.XML`). Default is "Standard".
    /// </summary>
    public string PresetOrOptionXmlPath { get; init; } = "Standard";

    /// <summary>
    /// Absolute output path for the CC XLS artifact. If null the session picks a
    /// temp path.
    /// </summary>
    public string? OutputXlsPath { get; init; }

    /// <summary>
    /// When true the orchestrator also calls <c>GenerateCreateDdlAsync</c> for
    /// each side, attaching the produced <see cref="DdlArtifact"/> to the
    /// result. Needed by Phase 3 SQL emitters that consult the full CREATE
    /// DDL for body details (datatype strings, constraints).
    /// </summary>
    public bool IncludeCreateDdl { get; init; }

    /// <summary>
    /// When true the orchestrator skips <c>CompleteCompare</c> entirely and
    /// produces the <see cref="Change"/> list purely from the structural diff
    /// of the two <see cref="Parsing.ErwinModelMap"/> providers. Property-level
    /// changes (type / nullability / default / identity) require the XLS and
    /// will NOT be emitted in this mode. Use when CC is unavailable - e.g.
    /// the add-in comparing a live active PU against a Mart-hosted version
    /// where disk-save would corrupt the active PU.
    /// </summary>
    public bool SkipCompleteCompare { get; init; }

    public static CompareOptions Default { get; } = new();
}

public enum CompareLevel
{
    LogicalAndPhysical,
    LogicalOnly,
    PhysicalOnly,
    DatabaseOnly
}

public static class CompareLevelExtensions
{
    public static string ToScapiString(this CompareLevel level) => level switch
    {
        CompareLevel.LogicalAndPhysical => "LP",
        CompareLevel.LogicalOnly => "L",
        CompareLevel.PhysicalOnly => "P",
        CompareLevel.DatabaseOnly => "DB",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "unknown compare level")
    };
}

public sealed record DdlOptions
{
    /// <summary>
    /// Absolute path to FE option XML. Null means "erwin defaults".
    /// </summary>
    public string? FeOptionXmlPath { get; init; }

    public string? OutputSqlPath { get; init; }

    public static DdlOptions Default { get; } = new();
}
