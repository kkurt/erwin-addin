using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AlterDdl.Core.Emitting;

/// <summary>
/// Turns a <see cref="CompareResult"/> (typed Change list + metadata + optional
/// CREATE DDL artifacts) into a dialect-specific <see cref="AlterDdlScript"/>.
/// </summary>
public interface ISqlEmitter
{
    /// <summary>Dialect label matching the model's Target_Server (e.g. "MSSQL", "Oracle", "Db2").</summary>
    string Dialect { get; }

    /// <summary>Generate the alter script.</summary>
    AlterDdlScript Emit(CompareResult compareResult);
}
