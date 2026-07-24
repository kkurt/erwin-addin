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
        /// <summary>Structured parameter (WP 323): required length/precision bounded by
        /// PARAM_MIN/PARAM_MAX, a per-entry scale rule (SCALE_MODE + SCALE_MIN/SCALE_MAX) and a
        /// per-entry length-semantics suffix rule (SUFFIX_MODE + SUFFIX_VALUES). Composition:
        /// <c>BASE(p[,s][ suffix])</c>, e.g. NUMBER(22,5) or VARCHAR2(15 CHAR).</summary>
        Structured,
    }

    /// <summary>
    /// How a STRUCTURED entry treats one of its optional parts (the scale / the length-semantics
    /// suffix): the DATATYPE_LIBRARY SCALE_MODE and SUFFIX_MODE columns (NONE | OPTIONAL |
    /// REQUIRED; a NULL column is read as NONE, defensively - the columns are only populated on
    /// STRUCTURED rows).
    /// </summary>
    public enum StructuredPartMode
    {
        /// <summary>The part must NOT be present.</summary>
        None,
        /// <summary>The part may be present.</summary>
        Optional,
        /// <summary>The part must be present.</summary>
        Required,
    }

    /// <summary>Which sub-part of a STRUCTURED parameter a validation failure points at - lets
    /// the datatype picker focus the offending field (length box / scale box / semantics combo)
    /// instead of always parking the caret on the length.</summary>
    public enum StructuredParamPart
    {
        /// <summary>Not attributable to a single structured part (valid results, non-structured
        /// failures, admin naming-rule failures).</summary>
        None,
        /// <summary>The length/precision p (also whole-parameter failures: missing/unparseable).</summary>
        Length,
        /// <summary>The scale s.</summary>
        Scale,
        /// <summary>The length-semantics suffix.</summary>
        Suffix,
    }

    /// <summary>Result of validating a Physical_Data_Type against the config whitelist.
    /// Message is null when valid; on failure it is the reason to surface to the user.
    /// <see cref="Part"/> names the structured sub-field at fault (WP 323), None otherwise.</summary>
    public readonly struct DatatypeValidationResult
    {
        public bool IsValid { get; }
        public string Message { get; }
        /// <summary>The structured parameter part an Invalid verdict points at; None when valid,
        /// non-structured, or not attributable to a single part.</summary>
        public StructuredParamPart Part { get; }
        private DatatypeValidationResult(bool ok, string message, StructuredParamPart part)
        { IsValid = ok; Message = message; Part = part; }
        public static readonly DatatypeValidationResult Valid =
            new DatatypeValidationResult(true, null, StructuredParamPart.None);
        public static DatatypeValidationResult Invalid(string message, StructuredParamPart part = StructuredParamPart.None)
            => new DatatypeValidationResult(false, message, part);
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

        /// <summary>DATATYPE_LIBRARY.LABEL (nullable): admin-authored display name for the datatype
        /// picker combo. When set it REPLACES the raw base token (+"(n)") in the dropdown, so two
        /// rows with the SAME base datatype but different rules (e.g. an "nvarchar(max)" row and an
        /// "nvarchar &lt;= 4000" row) are distinguishable. Blank/null -&gt; fall back to the base token.</summary>
        public string Label { get; set; }

        /// <summary>DATATYPE_LIBRARY.PARAM_MIN (Structured only, nullable): inclusive lower bound
        /// of the length/precision p. Null = unbounded (the admin guarantees &gt;= 1 when set).</summary>
        public int? ParamMin { get; set; }

        /// <summary>DATATYPE_LIBRARY.PARAM_MAX (Structured only, nullable): inclusive upper bound
        /// of the length/precision p. Null = unbounded.</summary>
        public int? ParamMax { get; set; }

        /// <summary>DATATYPE_LIBRARY.SCALE_MODE (Structured only): whether the comma-scale part is
        /// forbidden / optional / required. NULL is read as NONE.</summary>
        public StructuredPartMode ScaleMode { get; set; }

        /// <summary>DATATYPE_LIBRARY.SCALE_MIN (Structured only, nullable): inclusive lower bound
        /// of the scale s. May be negative (e.g. Oracle -84). Null = unbounded.</summary>
        public int? ScaleMin { get; set; }

        /// <summary>DATATYPE_LIBRARY.SCALE_MAX (Structured only, nullable): inclusive upper bound
        /// of the scale s. Null = unbounded.</summary>
        public int? ScaleMax { get; set; }

        /// <summary>DATATYPE_LIBRARY.SUFFIX_MODE (Structured only): whether the length-semantics
        /// suffix part is forbidden / optional / required. NULL is read as NONE.</summary>
        public StructuredPartMode SuffixMode { get; set; }

        /// <summary>DATATYPE_LIBRARY.SUFFIX_VALUES (Structured only, nullable): the allowed
        /// length-semantics suffix tokens as CSV ("BYTE,CHAR"). Tokens may contain inner spaces,
        /// never parentheses, and are never digits-only; the server dedupes them
        /// case-insensitively. Matching is OrdinalIgnoreCase; composition uses the admin casing
        /// (<see cref="GetSuffixValueList"/>).</summary>
        public string SuffixValues { get; set; }

        /// <summary><see cref="SuffixValues"/> split into trimmed tokens, admin casing preserved;
        /// empty when the CSV is null/blank.</summary>
        public IReadOnlyList<string> GetSuffixValueList()
        {
            if (string.IsNullOrWhiteSpace(SuffixValues)) return Array.Empty<string>();
            return SuffixValues.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToArray();
        }
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
    /// DROPPED and replaced by PARAMETRIZATION_TYPE (NONE|STANDARD|REGEX|STRUCTURED) +
    /// ALLOW_NON_PARAMETRIZED + REGEX_PATTERN + REGEX_ERROR. 2026-07-23 (WP 323): STRUCTURED rows
    /// add PARAM_MIN/PARAM_MAX + SCALE_MODE/SCALE_MIN/SCALE_MAX + SUFFIX_MODE/SUFFIX_VALUES (all
    /// nullable; the server nulls them on non-STRUCTURED rows). DBMS_VERSION / DBMS_LIBRARY remain
    /// only for CONFIG.DBMS_VERSION_ID + the DBMS-mismatch check.</para>
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
        private bool _libraryEnabled;
        private bool _alwaysAsk;
        private string _lastError;

        /// <summary>True once a load attempt completed (success or "no rows"); false on hard failure.</summary>
        public bool IsLoaded => _isLoaded;

        /// <summary>True when a non-empty whitelist is loaded - i.e. the config restricts datatypes.</summary>
        public bool HasRestriction => _isLoaded && _allowed.Count > 0;

        /// <summary>
        /// CONFIG_PROPERTY "DATATYPE_LIBRARY_ENABLED" (config-scoped, default FALSE / opt-in): the
        /// master switch for datatype-library enforcement. When false the whitelist is NOT enforced
        /// even if it is non-empty (no picker on new/changed columns). Cached at Load() so the
        /// per-column enforce path does not hit the DB.
        /// </summary>
        public bool LibraryEnabled => _libraryEnabled;

        /// <summary>
        /// CONFIG_PROPERTY "DATATYPE_LIBRARY_ALWAYS_ASK" (config-scoped, default FALSE): when true a
        /// NEW column whose datatype is ALREADY allowed still prompts the picker (current type
        /// preselected) so the user explicitly confirms it. Only meaningful while
        /// <see cref="LibraryEnabled"/> is true. Cached at Load().
        /// </summary>
        public bool AlwaysAsk => _alwaysAsk;

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
                _libraryEnabled = false;
                _alwaysAsk = false;
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
                                string label = reader["LABEL"] == DBNull.Value
                                    ? null : reader["LABEL"]?.ToString();

                                // STRUCTURED columns (WP 323): populated only on STRUCTURED rows,
                                // the server nulls them for every other parametrization type. All
                                // reads are DBNull-safe; a NULL mode is read as NONE (defensive).
                                int? paramMin = ReadNullableInt(reader["PARAM_MIN"], name, "PARAM_MIN");
                                int? paramMax = ReadNullableInt(reader["PARAM_MAX"], name, "PARAM_MAX");
                                var scaleMode = ParseStructuredMode(
                                    reader["SCALE_MODE"] == DBNull.Value ? null : reader["SCALE_MODE"]?.ToString(),
                                    name, "SCALE_MODE");
                                int? scaleMin = ReadNullableInt(reader["SCALE_MIN"], name, "SCALE_MIN");
                                int? scaleMax = ReadNullableInt(reader["SCALE_MAX"], name, "SCALE_MAX");
                                var suffixMode = ParseStructuredMode(
                                    reader["SUFFIX_MODE"] == DBNull.Value ? null : reader["SUFFIX_MODE"]?.ToString(),
                                    name, "SUFFIX_MODE");
                                string suffixValues = reader["SUFFIX_VALUES"] == DBNull.Value
                                    ? null : reader["SUFFIX_VALUES"]?.ToString();

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
                                    Label = label,
                                    ParamMin = paramMin,
                                    ParamMax = paramMax,
                                    ScaleMode = scaleMode,
                                    ScaleMin = scaleMin,
                                    ScaleMax = scaleMax,
                                    SuffixMode = suffixMode,
                                    SuffixValues = suffixValues,
                                });
                            }
                        }
                    }
                }

                // Enforcement master switch + always-ask, cached here so the per-column enforce
                // path never hits the DB. DATATYPE_LIBRARY_ENABLED defaults OFF (opt-in): a config
                // with a whitelist does NOT enforce until the admin turns it on.
                _libraryEnabled = ctx.GetEffectiveBool("DATATYPE_LIBRARY_ENABLED", false);
                _alwaysAsk = ctx.GetEffectiveBool("DATATYPE_LIBRARY_ALWAYS_ASK", false);

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine(
                    $"AllowedDatatypeService: loaded {_allowed.Count} allowed datatype(s) for config {ctx.ActiveConfigId} " +
                    $"(enabled={_libraryEnabled}, alwaysAsk={_alwaysAsk}, " +
                    $"{(_allowed.Count == 0 ? "no restriction (config whitelist empty)" : string.Join(", ", _allowed.Select(DescribeEntry)))})");
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
                    return @"SELECT ""DATATYPE"", ""PARAMETRIZATION_TYPE"", ""ALLOW_NON_PARAMETRIZED"", ""REGEX_PATTERN"", ""REGEX_ERROR"", ""DESCRIPTION"", ""LABEL"", ""PARAM_MIN"", ""PARAM_MAX"", ""SCALE_MODE"", ""SCALE_MIN"", ""SCALE_MAX"", ""SUFFIX_MODE"", ""SUFFIX_VALUES""
                            FROM ""DATATYPE_LIBRARY""
                            WHERE ""CONFIG_ID"" = @configId
                            ORDER BY ""DATATYPE""";

                case "ORACLE":
                    return @"SELECT DATATYPE, PARAMETRIZATION_TYPE, ALLOW_NON_PARAMETRIZED, REGEX_PATTERN, REGEX_ERROR, DESCRIPTION, LABEL, PARAM_MIN, PARAM_MAX, SCALE_MODE, SCALE_MIN, SCALE_MAX, SUFFIX_MODE, SUFFIX_VALUES
                            FROM DATATYPE_LIBRARY
                            WHERE CONFIG_ID = :configId
                            ORDER BY DATATYPE";

                case "MSSQL":
                default:
                    return @"SELECT [DATATYPE], [PARAMETRIZATION_TYPE], [ALLOW_NON_PARAMETRIZED], [REGEX_PATTERN], [REGEX_ERROR], [DESCRIPTION], [LABEL], [PARAM_MIN], [PARAM_MAX], [SCALE_MODE], [SCALE_MIN], [SCALE_MAX], [SUFFIX_MODE], [SUFFIX_VALUES]
                            FROM [dbo].[DATATYPE_LIBRARY]
                            WHERE [CONFIG_ID] = @configId
                            ORDER BY [DATATYPE]";
            }
        }

        /// <summary>Parse PARAMETRIZATION_TYPE (STANDARD | REGEX | STRUCTURED | NONE,
        /// case-insensitive). An unrecognized/blank value defaults to Standard (permissive: a
        /// parameter is accepted) and is logged - the migration always writes a known value, so
        /// this is defensive.</summary>
        private static DatatypeParametrization ParseParametrization(string raw, string typeName)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "NONE": return DatatypeParametrization.None;
                case "STANDARD": return DatatypeParametrization.Standard;
                case "REGEX": return DatatypeParametrization.Regex;
                case "STRUCTURED": return DatatypeParametrization.Structured;
                default:
                    System.Diagnostics.Debug.WriteLine(
                        $"AllowedDatatypeService: DATATYPE_LIBRARY '{typeName}' has unrecognized PARAMETRIZATION_TYPE '{raw}' - defaulting to STANDARD.");
                    return DatatypeParametrization.Standard;
            }
        }

        /// <summary>Parse a SCALE_MODE / SUFFIX_MODE column value (NONE | OPTIONAL | REQUIRED,
        /// case-insensitive). NULL/blank is NONE by contract (the columns are only populated on
        /// STRUCTURED rows); an unrecognized value is logged and read as NONE (defensive - the
        /// admin always writes a known value).</summary>
        private static StructuredPartMode ParseStructuredMode(string raw, string typeName, string columnName)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "":
                case "NONE": return StructuredPartMode.None;
                case "OPTIONAL": return StructuredPartMode.Optional;
                case "REQUIRED": return StructuredPartMode.Required;
                default:
                    System.Diagnostics.Debug.WriteLine(
                        $"AllowedDatatypeService: DATATYPE_LIBRARY '{typeName}' has unrecognized {columnName} '{raw}' - reading as NONE.");
                    return StructuredPartMode.None;
            }
        }

        /// <summary>DBNull-safe nullable-int read: the provider may surface the value as int
        /// (MSSQL/PostgreSQL) or decimal (Oracle NUMBER), so convert rather than cast. A value
        /// outside Int32 range (or otherwise non-convertible) is neutralized ROW-LOCALLY to null
        /// (= unbounded) + log, matching how every other malformed per-row field is handled -
        /// one bad bound must never fail-open the whole whitelist via Load()'s outer catch.</summary>
        private static int? ReadNullableInt(object value, string typeName, string columnName)
        {
            if (value == null || value == DBNull.Value) return null;
            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception ex) when (ex is OverflowException || ex is FormatException || ex is InvalidCastException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"AllowedDatatypeService: DATATYPE_LIBRARY '{typeName}' has an out-of-range/non-numeric {columnName} '{value}' - reading as NULL (unbounded): {ex.Message}");
                return null;
            }
        }

        private static string DescribeEntry(AllowedDatatypeEntry a)
        {
            if (a == null) return "?";
            switch (a.ParametrizationType)
            {
                case DatatypeParametrization.None: return a.Datatype;
                case DatatypeParametrization.Regex: return a.Datatype + (a.AllowNonParametrized ? "(re?)" : "(re)");
                case DatatypeParametrization.Structured:
                    return a.Datatype + (a.AllowNonParametrized ? "(st? " : "(st ") + DescribeStructuredRules(a) + ")";
                default: return a.Datatype + (a.AllowNonParametrized ? "(n?)" : "(n)");
            }
        }

        /// <summary>
        /// Compact human-readable summary of a STRUCTURED entry's rules for diagnostics/logs,
        /// e.g. "p 1..38, s? -84..127, sfx! BYTE|CHAR" ("?" = optional part, "!" = required part,
        /// "*" = unbounded). Empty for non-structured entries. Shared by
        /// <see cref="DescribeEntry"/> and the ModelConfigForm datatype-library load log.
        /// </summary>
        public static string DescribeStructuredRules(AllowedDatatypeEntry a)
        {
            if (a == null || a.ParametrizationType != DatatypeParametrization.Structured) return "";

            string Bound(int? b) => b?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "*";
            string ModeMark(StructuredPartMode m) => m == StructuredPartMode.Required ? "!" : "?";

            var parts = new List<string> { $"p {Bound(a.ParamMin)}..{Bound(a.ParamMax)}" };
            if (a.ScaleMode != StructuredPartMode.None)
                parts.Add($"s{ModeMark(a.ScaleMode)} {Bound(a.ScaleMin)}..{Bound(a.ScaleMax)}");
            if (a.SuffixMode != StructuredPartMode.None)
                parts.Add($"sfx{ModeMark(a.SuffixMode)} {string.Join("|", a.GetSuffixValueList())}");
            return string.Join(", ", parts);
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
            // Every allowed type REQUIRES a parameter (unusual). Synthesize a minimal parameter.
            // "(1)" is valid for a Standard entry (format is DB-standard, not checked here); for a
            // Regex entry it is best-effort (may not match REGEX_PATTERN) - the enforce path logs
            // a warning and offers the picker if the fallback does not round-trip. A Structured
            // entry gets a full synthesis that satisfies its own rules (SynthesizeMinimalParam).
            var first = _allowed.FirstOrDefault(a => a != null && !string.IsNullOrEmpty(a.Datatype));
            return first == null ? null : first.Datatype + "(" + SynthesizeMinimalParam(first) + ")";
        }

        /// <summary>
        /// Minimal parameter text for a param-required fallback. Standard/Regex: "1" (unchanged
        /// best-effort). Structured: p = PARAM_MIN ?? 1, plus ",SCALE_MIN ?? 0" when the scale is
        /// REQUIRED, plus " first-allowed-suffix" when the suffix is REQUIRED - OPTIONAL parts are
        /// never added, so the synthesized token round-trips through the entry's own rules. The
        /// null-bound defaults are clamped to the OPPOSITE bound when one exists (e.g. SCALE_MIN
        /// null + SCALE_MAX -1 must seed -1, not 0) so the round-trip holds for one-sided bounds.
        /// </summary>
        private static string SynthesizeMinimalParam(AllowedDatatypeEntry a)
        {
            if (a.ParametrizationType != DatatypeParametrization.Structured) return "1";

            var sb = new System.Text.StringBuilder();
            int p = a.ParamMin ?? Math.Min(1, a.ParamMax ?? 1);
            sb.Append(p.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (a.ScaleMode == StructuredPartMode.Required)
            {
                int scale = a.ScaleMin ?? Math.Min(0, a.ScaleMax ?? 0);
                sb.Append(',').Append(scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (a.SuffixMode == StructuredPartMode.Required)
            {
                var suffixes = a.GetSuffixValueList();
                if (suffixes.Count > 0) sb.Append(' ').Append(suffixes[0]);
            }
            return sb.ToString();
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

                case DatatypeParametrization.Structured:
                    // Same bare-form rule as STANDARD/REGEX; a present parameter must satisfy the
                    // structured grammar + per-entry bounds/modes (REGEX_ERROR is NOT used here -
                    // every STRUCTURED failure message is generated).
                    if (!hasParam)
                        return entry.AllowNonParametrized
                            ? DatatypeValidationResult.Valid
                            : DatatypeValidationResult.Invalid(
                                $"Type '{type}' requires a parameter.", StructuredParamPart.Length);
                    return ValidateStructuredParam(entry, paramValue);

                case DatatypeParametrization.Standard:
                default:
                    // Standard length/precision - format is the DB/erwin standard, not re-checked here.
                    if (hasParam) return DatatypeValidationResult.Valid;
                    return entry.AllowNonParametrized
                        ? DatatypeValidationResult.Valid
                        : DatatypeValidationResult.Invalid($"Type '{type}' requires a parameter.");
            }
        }

        /// <summary>
        /// Validate a present parameter against a STRUCTURED entry: parse the paren content with
        /// <see cref="StructuredParamParser"/>, then check the length/precision bounds
        /// (PARAM_MIN/MAX, null = unbounded), the scale rule (SCALE_MODE + SCALE_MIN/MAX) and the
        /// length-semantics suffix rule (SUFFIX_MODE + SUFFIX_VALUES, matched OrdinalIgnoreCase),
        /// in that order.
        /// </summary>
        private static DatatypeValidationResult ValidateStructuredParam(AllowedDatatypeEntry entry, string paramValue)
        {
            string type = entry.Datatype;

            if (!StructuredParamParser.TryParse(paramValue, out int p, out int? s, out string suffix))
                return DatatypeValidationResult.Invalid(
                    $"Parameter '{paramValue}' is not valid for type '{type}'. Expected format: length[,scale][ semantics].",
                    StructuredParamPart.Length);

            if ((entry.ParamMin.HasValue && p < entry.ParamMin.Value)
                || (entry.ParamMax.HasValue && p > entry.ParamMax.Value))
                return DatatypeValidationResult.Invalid(
                    RangeMessage("Length/precision", entry.ParamMin, entry.ParamMax),
                    StructuredParamPart.Length);

            if (s.HasValue)
            {
                if (entry.ScaleMode == StructuredPartMode.None)
                    return DatatypeValidationResult.Invalid(
                        $"Type '{type}' does not take a scale.", StructuredParamPart.Scale);
                if ((entry.ScaleMin.HasValue && s.Value < entry.ScaleMin.Value)
                    || (entry.ScaleMax.HasValue && s.Value > entry.ScaleMax.Value))
                    return DatatypeValidationResult.Invalid(
                        RangeMessage("Scale", entry.ScaleMin, entry.ScaleMax), StructuredParamPart.Scale);
            }
            else if (entry.ScaleMode == StructuredPartMode.Required)
            {
                return DatatypeValidationResult.Invalid(
                    $"Type '{type}' requires a scale.", StructuredParamPart.Scale);
            }

            // Defensive for BOTH suffix branches below: OPTIONAL/REQUIRED with an empty
            // SUFFIX_VALUES is inconsistent admin data (the admin UI enforces a non-empty list).
            // The rule is unenforceable - nothing could ever satisfy it and the picker cannot even
            // offer a choice - so accept + log instead of trapping the user or breaking the
            // fallback synthesis round-trip. Never swallow silently.
            var allowedSuffixes = entry.GetSuffixValueList();
            bool suffixListEmpty = allowedSuffixes.Count == 0;
            if (suffix.Length > 0)
            {
                if (entry.SuffixMode == StructuredPartMode.None)
                    return DatatypeValidationResult.Invalid(
                        $"Type '{type}' does not take a length semantics suffix.", StructuredParamPart.Suffix);
                if (suffixListEmpty)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"AllowedDatatypeService: DATATYPE_LIBRARY '{type}' has SUFFIX_MODE {entry.SuffixMode} but empty SUFFIX_VALUES - accepting suffix '{suffix}'.");
                }
                else if (!allowedSuffixes.Any(v => string.Equals(v, suffix, StringComparison.OrdinalIgnoreCase)))
                {
                    return DatatypeValidationResult.Invalid(
                        $"Length semantics must be one of: {string.Join(", ", allowedSuffixes)}.",
                        StructuredParamPart.Suffix);
                }
            }
            else if (entry.SuffixMode == StructuredPartMode.Required)
            {
                if (suffixListEmpty)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"AllowedDatatypeService: DATATYPE_LIBRARY '{type}' has SUFFIX_MODE Required but empty SUFFIX_VALUES - suffix rule unenforceable, accepting '{paramValue}'.");
                }
                else
                {
                    return DatatypeValidationResult.Invalid(
                        $"Type '{type}' requires a length semantics suffix ({string.Join(", ", allowedSuffixes)}).",
                        StructuredParamPart.Suffix);
                }
            }

            return DatatypeValidationResult.Valid;
        }

        /// <summary>Bound-violation message with one-sided wording when only one bound is set.
        /// Only called after a violation, so at least one bound is non-null.</summary>
        private static string RangeMessage(string what, int? min, int? max)
        {
            if (min.HasValue && max.HasValue) return $"{what} must be between {min.Value} and {max.Value}.";
            if (min.HasValue) return $"{what} must be at least {min.Value}.";
            return $"{what} must be at most {max.Value}.";
        }

        public void Reload() => Load();

        /// <summary>Test-only seed (mirrors NamingStandardService.SeedForTesting). Production never calls this.</summary>
        public void SeedForTesting(IEnumerable<AllowedDatatypeEntry> entries, bool libraryEnabled = true, bool alwaysAsk = false)
        {
            _allowed = (entries ?? Enumerable.Empty<AllowedDatatypeEntry>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.Datatype))
                .ToList();
            _libraryEnabled = libraryEnabled;
            _alwaysAsk = alwaysAsk;
            _lastError = null;
            _isLoaded = true;
        }
    }
}
