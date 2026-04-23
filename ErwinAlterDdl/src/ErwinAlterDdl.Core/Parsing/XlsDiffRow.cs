namespace EliteSoft.Erwin.AlterDdl.Core.Parsing;

/// <summary>
/// One row from the CompleteCompare "XLS" (actually HTML) report.
/// <see cref="IndentLevel"/> encodes the parent/child hierarchy (every 3 leading
/// non-breaking spaces = one level).
/// </summary>
public sealed record XlsDiffRow(
    int IndentLevel,
    string Type,
    string LeftValue,
    string Status,
    string RightValue)
{
    /// <summary>True when the row represents a difference (not "Equal").</summary>
    public bool IsNotEqual => Status.Equals("Not Equal", StringComparison.OrdinalIgnoreCase);
}
