using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// How a whitelisted datatype may carry a parameter (DATATYPE_LIBRARY.PARAMETRIZATION_TYPE).
    /// Replaces the old boolean IS_PARAMETERIZED (dropped by the admin migration 2026-07-08).
    /// </summary>
    public enum DatatypeParametrization
    {
        /// <summary>Bare type only - a parameter is INVALID (e.g. INT, DATE).</summary>
        None,
        /// <summary>Standard length/precision parameter; its format is the DB/erwin standard (not regex-checked).</summary>
        Standard,
        /// <summary>Parameter must match DATATYPE_LIBRARY.REGEX_PATTERN; on failure show REGEX_ERROR.</summary>
        Regex,
    }

    /// <summary>Result of validating a Physical_Data_Type against the config whitelist.
    /// Message is null when valid; on failure it is the reason to surface to the user.</summary>
    public readonly struct DatatypeValidationResult
    {
        public bool IsValid { get; }
        public string Message { get; }
        private DatatypeValidationResult(bool ok, string message) { IsValid = ok; Message = message; }
        public static readonly DatatypeValidationResult Valid = new DatatypeValidationResult(true, null);
        public static DatatypeValidationResult Invalid(string message) => new DatatypeValidationResult(false, message);
    }

    /// <summary>
    /// One allowed datatype for the active config: the base type token plus its parametrization
    /// model. Sourced from DATATYPE_LIBRARY (the admin "Datatype Library" whitelist rows for that
    /// config). 2026-07-08: the boolean IS_PARAMETERIZED was replaced by PARAMETRIZATION_TYPE +
    /// ALLOW_NON_PARAMETRIZED + REGEX_PATTERN + REGEX_ERROR.
    /// </summary>
    public sealed class AllowedDatatypeEntry
    {
        /// <summary>DATATYPE_LIBRARY.DATATYPE - the base token only (e.g. "int", "nvarchar", "Numeric").</summary>
        public string Datatype { get; set; }

        /// <summary>DATATYPE_LIBRARY.PARAMETRIZATION_TYPE: how a parameter may be carried.</summary>
        public DatatypeParametrization ParametrizationType { get; set; }

        /// <summary>DATATYPE_LIBRARY.ALLOW_NON_PARAMETRIZED: the type may ALSO be used bare (no
        /// parameter). Meaningful only for Standard/Regex; None is bare-only by definition.</summary>
        public bool AllowNonParametrized { get; set; }

        /// <summary>DATATYPE_LIBRARY.REGEX_PATTERN (Regex only, nullable): validates the parameter value.</summary>
        public string RegexPattern { get; set; }

        /// <summary>DATATYPE_LIBRARY.REGEX_ERROR (Regex only, nullable): custom message shown when
        /// the parameter fails REGEX_PATTERN. Falls back to a generic message when blank.</summary>
        public string RegexError { get; set; }

        /// <summary>DATATYPE_LIBRARY.DESCRIPTION (nullable): admin-authored explanation shown under
        /// the selected type in the datatype picker. Not shown when null/blank.</summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// Loads the datatypes the admin "Datatype Library" allows for the active CONFIG and answers
    /// whether a column's Physical_Data_Type is permitted. The allowed set is the config's own
    /// whitelist: DATATYPE_LIBRARY rows WHERE CONFIG_ID = the active config's ID
    /// (ConfigContextService.ActiveConfigId). A config's row set IS its datatype whitelist.
    /// <para>
    /// An empty/absent set is "no restriction" - a config the admin has not curated yet does not
    /// suddenly reject every type. There is NO status gate: enforcement is purely "is this base
    /// token in the config's set".
    /// </para>
    /// <para>2026-07-02: DATATYPE_LIBRARY became config-scoped (admin migration applied to the
    /// live MetaRepo DBs); the old DBMS_ID column, the DATATYPE_VERSION and ALLOWED_DATATYPE
    /// tables, and the DBMS_VERSION join are all gone. 2026-07-08: the boolean IS_PARAMETERIZED was
    /// DROPPED and replaced by PARAMETRIZATION_TYPE (NONE|STANDARD|REGEX) + ALLOW_NON_PARAMETRIZED +
    /// REGEX_PATTERN + REGEX_ERROR. Columns read: DATATYPE, PARAMETRIZATION_TYPE,
    /// ALLOW_NON_PARAMETRIZED, REGEX_PATTERN, REGEX_ERROR (filtered by CONFIG_ID). DBMS_VERSION /
    /// DBMS_LIBRARY remain only for CONFIG.DBMS_VERSION_ID + the DBMS-mismatch check.</para>
    /// </summary>
    public class AllowedDatatypeService
    {
        private static AllowedDatatypeService _instance;
        private static readonly object _lock = new object();

        public static AllowedDatatypeService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null) _instance = new AllowedDatatypeService();
                    }
                }
                return _instance;
            }
        }

        private List<AllowedDatatypeEntry> _allowed = new List<AllowedDatatypeEntry>();
        private bool _isLoaded;
        private string _lastError;

        /// <summary>True once a load attempt completed (success or "no rows"); false on hard failure.</summary>
        public bool IsLoaded => _isLoaded;

        /// <summary>True when a non-empty whitelist is loaded - i.e. the config restricts datatypes.</summary>
        public bool HasRestriction => _isLoaded && _allowed.Count > 0;

        /// <summary>Number of whitelist entries currently loaded (diagnostic).</summary>
        public int AllowedCount => _allowed.Count;

        public IReadOnlyList<AllowedDatatypeEntry> Allowed => _allowed;
        public string LastError => _lastError;

        /// <summary>
        /// Load the active config's allowed datatypes = DATATYPE_LIBRARY rows WHERE CONFIG_ID =
        /// ConfigContextService.ActiveConfigId. Returns true on a clean read (including the
        /// legitimate empty result = no restriction); false on a hard error. A config with no
        /// DATATYPE_LIBRARY rows yields an empty set = no restriction.
        /// </summary>
        public bool Load()
        {
            try
            {
                _allowed = new List<AllowedDatatypeEntry>();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"AllowedDatatypeService: {_lastError}");
                    return false;
                }

                var ctx = ConfigContextService.Instance;
                if (!ctx.IsInitialized)
                {
                    _lastError = "ConfigContext not initialized.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"AllowedDatatypeService: {_lastError}");
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();
                string query = GetQuery(dbType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();
                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        var pCfg = command.CreateParameter();
                        pCfg.ParameterName = SqlDialect.Param(dbType, "configId");
                        pCfg.Value = ctx.ActiveConfigId;
                        command.Parameters.Add(pCfg);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string name = reader["DATATYPE"]?.ToString()?.Trim() ?? "";
                                if (string.IsNullOrEmpty(name)) continue;

                                var ptype = ParseParametrization(
                                    reader["PARAMETRIZATION_TYPE"]?.ToString(), name);
                                bool allowBare = reader["ALLOW_NON_PARAMETRIZED"] != DBNull.Value
                                                 && Convert.ToBoolean(reader["ALLOW_NON_PARAMETRIZED"]);
                                string regexPattern = reader["REGEX_PATTERN"] == DBNull.Value
                                    ? null : reader["REGEX_PATTERN"]?.ToString();
                                string regexError = reader["REGEX_ERROR"] == DBNull.Value
                                    ? null : reader["REGEX_ERROR"]?.ToString();
                                string description = reader["DESCRIPTION"] == DBNull.Value
                                    ? null : reader["DESCRIPTION"]?.ToString();

                                // Validate a REGEX pattern's compilability at load so the matcher
                                // never throws on a malformed admin pattern; a broken pattern is
                                // neutralized (logged, treated as "any parameter allowed") rather
                                // than trapping the user. Never swallow silently.
                                if (ptype == DatatypeParametrization.Regex && !string.IsNullOrEmpty(regexPattern))
                                {
                                    try { _ = new Regex(regexPattern); }
                                    catch (Exception rex)
                                    {
                                        System.Diagnostics.Debug.WriteLine(
                                            $"AllowedDatatypeService: DATATYPE_LIBRARY '{name}' has an invalid REGEX_PATTERN '{regexPattern}' - ignoring the pattern (any parameter accepted): {rex.Message}");
                                        regexPattern = null;
                                    }
                                }

                                _allowed.Add(new AllowedDatatypeEntry
                                {
                                    Datatype = name,
                                    ParametrizationType = ptype,
                                    AllowNonParametrized = allowBare,
                                    RegexPattern = regexPattern,
                                    RegexError = regexError,
                                    Description = description,
                                });
                            }
                        }
                    }
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine(
                    $"AllowedDatatypeService: loaded {_allowed.Count} allowed datatype(s) for config {ctx.ActiveConfigId} " +
                    $"({(_allowed.Count == 0 ? "no restriction (config whitelist empty)" : string.Join(", ", _allowed.Select(DescribeEntry)))})");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"AllowedDatatypeService.Load error: {ex.Message}");
                return false;
            }
        }

        // The active config's "Datatype Library" whitelist: DATATYPE_LIBRARY filtered by
        // CONFIG_ID. Config-scoped since the 2026-07-02 admin migration (the old DBMS_ID column +
        // DBMS_VERSION join are gone). 2026-07-08: the boolean IS_PARAMETERIZED was DROPPED and
        // replaced by PARAMETRIZATION_TYPE + ALLOW_NON_PARAMETRIZED + REGEX_PATTERN + REGEX_ERROR
        // - never SELECT IS_PARAMETERIZED again (it errors on migrated repos). No status gate. No
        // MC_ prefix (unlike MC_NAMING_STANDARD). Bound to ConfigContextService.ActiveConfigId.
        private static string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""DATATYPE"", ""PARAMETRIZATION_TYPE"", ""ALLOW_NON_PARAMETRIZED"", ""REGEX_PATTERN"", ""REGEX_ERROR"", ""DESCRIPTION""
                            FROM ""DATATYPE_LIBRARY""
                            WHERE ""CONFIG_ID"" = @configId
                            ORDER BY ""DATATYPE""";

                case "ORACLE":
                    return @"SELECT DATATYPE, PARAMETRIZATION_TYPE, ALLOW_NON_PARAMETRIZED, REGEX_PATTERN, REGEX_ERROR, DESCRIPTION
                            FROM DATATYPE_LIBRARY
                            WHERE CONFIG_ID = :configId
                            ORDER BY DATATYPE";

                case "MSSQL":
                default:
                    return @"SELECT [DATATYPE], [PARAMETRIZATION_TYPE], [ALLOW_NON_PARAMETRIZED], [REGEX_PATTERN], [REGEX_ERROR], [DESCRIPTION]
                            FROM [dbo].[DATATYPE_LIBRARY]
                            WHERE [CONFIG_ID] = @configId
                            ORDER BY [DATATYPE]";
            }
        }

        /// <summary>Parse PARAMETRIZATION_TYPE (STANDARD | REGEX | NONE, case-insensitive).
        /// An unrecognized/blank value defaults to Standard (permissive: a parameter is accepted)
        /// and is logged - the migration always writes a known value, so this is defensive.</summary>
        private static DatatypeParametrization ParseParametrization(string raw, string typeName)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "NONE": return DatatypeParametrization.None;
                case "STANDARD": return DatatypeParametrization.Standard;
                case "REGEX": return DatatypeParametrization.Regex;
                default:
                    System.Diagnostics.Debug.WriteLine(
                        $"AllowedDatatypeService: DATATYPE_LIBRARY '{typeName}' has unrecognized PARAMETRIZATION_TYPE '{raw}' - defaulting to STANDARD.");
                    return DatatypeParametrization.Standard;
            }
        }

        private static string DescribeEntry(AllowedDatatypeEntry a)
        {
            if (a == null) return "?";
            switch (a.ParametrizationType)
            {
                case DatatypeParametrization.None: return a.Datatype;
                case DatatypeParametrization.Regex: return a.Datatype + (a.AllowNonParametrized ? "(re?)" : "(re)");
                default: return a.Datatype + (a.AllowNonParametrized ? "(n?)" : "(n)");
            }
        }

        /// <summary>
        /// True when the type is permitted: no restriction (empty whitelist) OR the value's
        /// base token matches an allowed entry and satisfies that entry's parametrization rule.
        /// </summary>
        public bool IsAllowed(string physicalDataType) => ValidateDatatype(physicalDataType, _allowed).IsValid;

        /// <summary>Validate against the loaded whitelist, returning validity + a reason message
        /// (the REGEX_ERROR / "requires a parameter" / "takes no parameter" text) on failure.</summary>
        public DatatypeValidationResult Validate(string physicalDataType) => ValidateDatatype(physicalDataType, _allowed);

        /// <summary>
        /// A concrete allowed datatype to force a column to when its current value is disallowed
        /// and there is no allowed previous value to restore (e.g. a brand-new column whose first
        /// pick is disallowed). User decision 2026-06-19 ("izinli tipe zorla"): never leave a
        /// disallowed type in the model. Prefers the first non-parameterized entry (a complete,
        /// valid token such as "int"); otherwise the first entry's base token. The load query
        /// orders by name so "first" is deterministic. Null only when nothing is loaded
        /// (HasRestriction false) - callers must null-check.
        /// </summary>
        public string GetFallbackDatatype()
        {
            if (_allowed == null || _allowed.Count == 0) return null;
            // Prefer an entry usable BARE (its base token is a complete, writable Physical_Data_Type
            // on its own): None (bare-only), or Standard/Regex that also allow the bare form.
            var bare = _allowed.FirstOrDefault(a => a != null && !string.IsNullOrEmpty(a.Datatype) && CanBeBare(a));
            if (bare != null) return bare.Datatype;
            // Every allowed type REQUIRES a parameter (unusual). Synthesize a minimal length. "(1)"
            // is valid for a Standard entry (format is DB-standard, not checked here); for a Regex
            // entry it is best-effort (may not match REGEX_PATTERN) - the enforce path logs a
            // warning and offers the picker if the fallback does not round-trip.
            var first = _allowed.FirstOrDefault(a => a != null && !string.IsNullOrEmpty(a.Datatype));
            return first == null ? null : first.Datatype + "(1)";
        }

        /// <summary>An entry may be used with no parameter: None is bare-only; Standard/Regex are
        /// bare-usable only when ALLOW_NON_PARAMETRIZED is set.</summary>
        private static bool CanBeBare(AllowedDatatypeEntry a) =>
            a.ParametrizationType == DatatypeParametrization.None || a.AllowNonParametrized;

        /// <summary>
        /// Pure matcher (no DB / no SCAPI) so it is unit-testable. Backward-compatible bool form
        /// of <see cref="ValidateDatatype"/>: true when the value is permitted by the whitelist.
        /// </summary>
        public static bool IsDatatypeAllowed(string physicalDataType, IReadOnlyCollection<AllowedDatatypeEntry> allowed)
            => ValidateDatatype(physicalDataType, allowed).IsValid;

        /// <summary>
        /// Pure matcher (no DB / no SCAPI), the single source of the whitelist semantics. Since the
        /// 2026-07-14 admin change a config may hold SEVERAL entries with the same DATATYPE name (the
        /// UNIQUE (CONFIG_ID, DATATYPE) index was dropped), each a distinct definition differing in
        /// parametrization / regex / description. ANY-match: a value is valid when the whitelist is
        /// empty (no restriction); OR its parsed base token matches an entry name case-insensitively
        /// AND the value satisfies the parametrization rule (<see cref="ValidateAgainstEntry"/>) of AT
        /// LEAST ONE same-named entry. A base whose name matches one or more entries but satisfies none
        /// of them is a violation; so is a base matching no entry at all. The verdict is independent of
        /// entry order. An empty/unparseable value is treated as valid (cannot classify - do not block).
        /// </summary>
        public static DatatypeValidationResult ValidateDatatype(string physicalDataType, IReadOnlyCollection<AllowedDatatypeEntry> allowed)
        {
            if (allowed == null || allowed.Count == 0) return DatatypeValidationResult.Valid; // no restriction

            var parts = DataTypeParser.Parse(physicalDataType);
            if (string.IsNullOrEmpty(parts.Base)) return DatatypeValidationResult.Valid; // cannot classify - do not block

            // ANY-match: the base name may now appear on several entries (unique index dropped
            // 2026-07-14). The value is valid if it satisfies ANY same-named definition; scan them
            // all rather than trusting the first. Order-independent by construction: a single pass
            // returns Valid on the first satisfied entry, otherwise aggregates the failures.
            DatatypeValidationResult? firstFailure = null;
            int matchCount = 0;
            foreach (var a in allowed)
            {
                if (a == null || string.IsNullOrEmpty(a.Datatype)) continue;
                if (!string.Equals(a.Datatype, parts.Base, StringComparison.OrdinalIgnoreCase)) continue;

                var result = ValidateAgainstEntry(a, parts.HasLength, parts.Length);
                if (result.IsValid) return result; // satisfied by this definition - valid regardless of order
                matchCount++;
                if (firstFailure == null) firstFailure = result;
            }

            if (matchCount == 0)
                return DatatypeValidationResult.Invalid(
                    $"Datatype '{parts.Base}' is not in the allowed datatype list for this configuration.");

            // The base name matched but every same-named definition rejected the value.
            // ONE matching entry: surface its own message verbatim so the single-row behavior and its
            // specific text (e.g. a custom REGEX_ERROR) are byte-for-byte unchanged. TWO OR MORE: no
            // single per-entry message is authoritative, so give a clear aggregate that still explains
            // the failure.
            return matchCount == 1
                ? firstFailure.Value
                : DatatypeValidationResult.Invalid(
                    $"Value '{physicalDataType}' is not valid for any allowed definition of '{parts.Base}'.");
        }

        /// <summary>
        /// Apply ONE whitelist entry's parametrization rule to a base+parameter split (the single
        /// place the NONE / STANDARD / REGEX semantics live; used by both model validation and the
        /// datatype picker). <paramref name="hasParam"/> = a parameter is present;
        /// <paramref name="paramValue"/> = the raw parameter text (e.g. "30", "10,2").
        /// </summary>
        public static DatatypeValidationResult ValidateAgainstEntry(AllowedDatatypeEntry entry, bool hasParam, string paramValue)
        {
            if (entry == null) return DatatypeValidationResult.Valid;
            string type = entry.Datatype;

            switch (entry.ParametrizationType)
            {
                case DatatypeParametrization.None:
                    // Bare-only: a parameter is invalid.
                    return hasParam
                        ? DatatypeValidationResult.Invalid($"Type '{type}' does not take a parameter.")
                        : DatatypeValidationResult.Valid;

                case DatatypeParametrization.Regex:
                    if (hasParam)
                    {
                        bool ok;
                        try
                        {
                            ok = string.IsNullOrEmpty(entry.RegexPattern)
                                 || Regex.IsMatch(paramValue ?? string.Empty, entry.RegexPattern);
                        }
                        catch (Exception ex)
                        {
                            // Defensive: patterns are pre-validated at load, but if one still throws
                            // do not trap the user - accept + log (never swallow silently).
                            System.Diagnostics.Debug.WriteLine(
                                $"AllowedDatatypeService: REGEX_PATTERN for '{type}' failed to evaluate ('{entry.RegexPattern}'): {ex.Message} - accepting parameter.");
                            ok = true;
                        }
                        return ok
                            ? DatatypeValidationResult.Valid
                            : DatatypeValidationResult.Invalid(
                                !string.IsNullOrWhiteSpace(entry.RegexError)
                                    ? entry.RegexError
                                    : $"Parameter '{paramValue}' is not valid for type '{type}'.");
                    }
                    // No parameter: allowed only when the bare form is permitted.
                    return entry.AllowNonParametrized
                        ? DatatypeValidationResult.Valid
                        : DatatypeValidationResult.Invalid($"Type '{type}' requires a parameter.");

                case DatatypeParametrization.Standard:
                default:
                    // Standard length/precision - format is the DB/erwin standard, not re-checked here.
                    if (hasParam) return DatatypeValidationResult.Valid;
                    return entry.AllowNonParametrized
                        ? DatatypeValidationResult.Valid
                        : DatatypeValidationResult.Invalid($"Type '{type}' requires a parameter.");
            }
        }

        public void Reload() => Load();

        /// <summary>Test-only seed (mirrors NamingStandardService.SeedForTesting). Production never calls this.</summary>
        public void SeedForTesting(IEnumerable<AllowedDatatypeEntry> entries)
        {
            _allowed = (entries ?? Enumerable.Empty<AllowedDatatypeEntry>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.Datatype))
                .ToList();
            _lastError = null;
            _isLoaded = true;
        }
    }
}
