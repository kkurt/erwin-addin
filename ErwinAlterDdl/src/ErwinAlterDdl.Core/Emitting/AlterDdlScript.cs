namespace EliteSoft.Erwin.AlterDdl.Core.Emitting;

/// <summary>One emitted SQL statement plus an optional explanatory comment.</summary>
public sealed record AlterStatement(string Sql, string? Comment = null);

/// <summary>
/// Ordered list of alter SQL statements for a specific dialect. Call
/// <see cref="ToScript"/> to turn it into a single-file string.
/// </summary>
public sealed record AlterDdlScript(
    string Dialect,
    IReadOnlyList<AlterStatement> Statements)
{
    /// <summary>
    /// Serialize to a newline-separated script. MSSQL uses "GO" separators;
    /// Oracle / Db2 use plain ";" termination and a blank line.
    /// </summary>
    public string ToScript()
    {
        var separator = Dialect.ToUpperInvariant() switch
        {
            "MSSQL" => "GO",
            _ => string.Empty,
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- ALTER DDL ({Dialect}) generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z UTC");
        sb.AppendLine($"-- {Statements.Count} statement(s)");
        sb.AppendLine();
        foreach (var s in Statements)
        {
            if (!string.IsNullOrEmpty(s.Comment)) sb.AppendLine($"-- {s.Comment}");
            sb.AppendLine(s.Sql);
            if (!string.IsNullOrEmpty(separator)) sb.AppendLine(separator);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
