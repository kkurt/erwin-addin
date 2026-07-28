#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// The set of table names a generated alter script actually touches.
    ///
    /// <para><b>Why this exists.</b> With "Only Selected Objects" ticked, erwin's own Alter
    /// Script Wizard scopes the DDL to the entities selected on the diagram, and the blocking
    /// rules should be checked over the same set. The add-in cannot ask erwin WHICH entities
    /// those are: SCAPI exposes no selection API, and the only readable surface - erwin's
    /// Overview pane - shows a COUNT ("2 objects") instead of names as soon as more than one is
    /// selected (<see cref="Win32Helper.ParseSelectedEntityFromOverviewText"/> documents exactly
    /// that). So the scope is derived from the artefact that IS available and is the thing being
    /// submitted anyway: the DDL text.</para>
    ///
    /// <para>Deliberately over-inclusive. A name this misses means a table is NOT checked, which
    /// is a silent pass; a name it invents merely matches no model entity and costs nothing. So
    /// every construct that can carry a table name is matched, and anything unparseable makes
    /// the caller fall back to the whole model rather than proceed with a partial set.</para>
    /// </summary>
    internal static class DdlTableScope
    {
        // Bracketed, quoted, or bare identifiers, optionally schema-qualified. Non-capturing
        // alternation on the qualifier so the LAST identifier (the table) is what group 1 holds.
        private const string Ident = @"(?:\[(?<n>[^\]]+)\]|""(?<n>[^""]+)""|`(?<n>[^`]+)`|(?<n>[A-Za-z_][\w$#]*))";

        private static readonly RegexOptions Opts =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // ALTER / CREATE / DROP / TRUNCATE TABLE [schema.]table
        private static readonly Regex TableStatement = new Regex(
            @"\b(?:ALTER|CREATE|DROP|TRUNCATE)\s+TABLE\s+(?:(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#]*)\s*\.\s*)?" + Ident,
            Opts);

        // sp_rename '[schema].[old]', 'new' - the OLD name is the model's current table.
        private static readonly Regex SpRename = new Regex(
            @"\bsp_rename\s+'(?:\[?[^'\].]+\]?\s*\.\s*)?\[?(?<n>[^'\]]+)\]?'",
            Opts);

        // EXEC sp_addextendedproperty ... @level1name = 'TABLE'
        private static readonly Regex Level1Name = new Regex(
            @"@level1name\s*=\s*'(?<n>[^']+)'",
            Opts);

        // COMMENT ON TABLE / ON COLUMN schema.table[.column]
        private static readonly Regex CommentOn = new Regex(
            @"\bCOMMENT\s+ON\s+(?:TABLE|COLUMN)\s+(?:(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#]*)\s*\.\s*)?" + Ident,
            Opts);

        /// <summary>
        /// Every table name the script names, compared case-insensitively. Empty when the script
        /// is empty or names nothing recognisable - the caller MUST treat that as "cannot scope"
        /// and check the whole model, never as "nothing to check".
        /// </summary>
        public static IReadOnlyCollection<string> Extract(string? ddl)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(ddl)) return names;

            foreach (var pattern in new[] { TableStatement, SpRename, Level1Name, CommentOn })
            {
                foreach (Match m in pattern.Matches(ddl))
                {
                    string name = m.Groups["n"].Value.Trim();
                    // An alter script routinely renames a table to a generated placeholder before
                    // recreating it (sp_rename 'E_58Log' -> 'E_58LogA7RE5530000'); both names end
                    // up in the set, and the extra one simply matches no model entity.
                    if (name.Length > 0) names.Add(name);
                }
            }

            return names;
        }
    }
}
