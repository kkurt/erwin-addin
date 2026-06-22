using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Atomic rule kind. Each <see cref="NamingStandardRule"/> row carries
    /// exactly one of these and exposes only the fields that kind needs.
    /// Multiple rules on the same (ObjectType, PropertyCode) combine via
    /// AND across separate rows; the engine dispatches by
    /// <see cref="NamingStandardRule.RuleType"/> and ignores the inactive
    /// fields.
    /// <para>
    /// "Required" is intentionally NOT a kind here - the spec models the
    /// non-empty requirement as the orthogonal <see cref="NamingStandardRule.IsRequired"/>
    /// flag that can layer on top of any of the four kinds. The DB CHECK
    /// constraint (admin migration 2026-05-16/2026-05-17) enforces the
    /// same closed set.
    /// </para>
    /// </summary>
    /// <summary>
    /// When a naming-standard rule should fire (2026-05-25 admin schema).
    /// Stored in <c>MC_NAMING_STANDARD.APPLY_ON</c> as <c>nvarchar NOT NULL
    /// DEFAULT 'Both'</c>. The engine filters rules by this flag against
    /// the current evaluation context so admin can scope "rule applies
    /// only when a column / entity is first created" vs "only when an
    /// existing one is edited" - useful for grandfathering legacy data
    /// that would otherwise fail a newly-tightened rule.
    /// </summary>
    public enum RuleApplyOn
    {
        /// <summary>Fire only on initial creation (new entity / new column).</summary>
        Create,
        /// <summary>Fire only when an existing object is being edited.</summary>
        Update,
        /// <summary>Fire in both contexts (default). Back-compat with pre-2026-05-25 rows.</summary>
        Both,
    }

    public enum NamingRuleKind
    {
        /// <summary>Value must start with <see cref="NamingStandardRule.Prefix"/>; auto-apply optional.</summary>
        Prefix,
        /// <summary>Value must end with <see cref="NamingStandardRule.Suffix"/>; auto-apply optional.</summary>
        Suffix,
        /// <summary>Length comparison <c>LENGTH_OPERATOR LENGTH_VALUE</c>; validate only.</summary>
        Length,
        /// <summary>Value must match <see cref="NamingStandardRule.RegexpPattern"/>; validate only.</summary>
        Regexp,
        /// <summary>
        /// First-class "value must not be empty / selection mandatory" rule.
        /// Admin 2026-05-25: pre-existing Length&gt;0 rows were migrated to
        /// this dedicated type so the engine can dispatch on RuleType alone
        /// without parsing length-operator+value semantics. Carries no
        /// extra fields - the orthogonal <see cref="NamingStandardRule.IsRequired"/>
        /// flag is implied by this type (engine treats empty values as
        /// violations regardless of how the flag is stored). Other length
        /// rules (e.g. <c>length &lt;= 128</c>) keep using
        /// <see cref="NamingRuleKind.Length"/> and run alongside.
        /// </summary>
        Required,
    }

    /// <summary>
    /// Represents a naming standard rule loaded from MC_NAMING_STANDARD.
    /// <para>
    /// Schema (post 2026-05-17 admin refactor): each row carries exactly
    /// one rule kind via <c>RULE_TYPE NVARCHAR(20) NOT NULL</c> (CHECK in
    /// {Prefix, Suffix, Length, Regexp}) and only the fields belonging to
    /// that kind are non-NULL. The orthogonal <c>IS_REQUIRED bit NOT NULL</c>
    /// flag layers a "value must be non-empty" gate on top of any kind -
    /// "Required" is no longer a separate rule type. Multiple rules on
    /// the same (ObjectType, PropertyCode) combine with AND semantics; the
    /// engine dispatches by <see cref="RuleType"/>. <see cref="AutoApply"/>
    /// is only meaningful for Prefix/Suffix; admin DB stores it as 0 on
    /// other kinds and the addin masks it defensively at load time.
    /// </para>
    /// <para>
    /// Condition (since 2026-05-17 C3) is polymorphic across two sources:
    /// either a user-defined property (<see cref="DependsOnUdpId"/>) or an
    /// erwin built-in property (<see cref="DependsOnPropertyDefId"/>). The
    /// DB CHECK constraint <c>CK_MC_NAMING_COND_XOR</c> enforces that at
    /// most one is set. The condition values live in
    /// <see cref="DependsOnPropertyValues"/> as a CSV; the engine matches
    /// case-insensitively (single-value CSV = back-compat path).
    /// </para>
    /// </summary>
    public class NamingStandardRule
    {
        public int Id { get; set; }
        public string ObjectType { get; set; }      // From MC_OBJECT_TYPE.NAME (e.g. "TABLE", "COLUMN")
        public string PropertyCode { get; set; }    // From MC_PROPERTY_DEF.PROPERTY_CODE (e.g. "Physical_Name"); "" for an object-type-only Required rule
        // Null ONLY for an object-type-only Required rule (Property "(none)" =
        // "an object of this type must exist"). Every other rule targets a
        // property. Admin enforces this (only Required may omit PROPERTY_DEF_ID).
        public int? PropertyDefId { get; set; }
        public NamingRuleKind RuleType { get; set; }
        /// <summary>
        /// IS_REQUIRED gate (orthogonal to <see cref="RuleType"/>). When true,
        /// an empty/whitespace value emits a violation using this rule's
        /// <see cref="ErrorMessage"/> and the pattern check is skipped. When
        /// false, an empty value short-circuits the rule entirely (no
        /// violation, no pattern check).
        /// </summary>
        public bool IsRequired { get; set; }
        public string Prefix { get; set; }
        public string Suffix { get; set; }
        public string LengthOperator { get; set; }
        public int? LengthValue { get; set; }
        public string RegexpPattern { get; set; }
        public string ErrorMessage { get; set; }
        public bool AutoApply { get; set; }
        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
        public int ConfigId { get; set; }

        /// <summary>
        /// Context gate (2026-05-25 admin schema): "Create" fires only when
        /// the rule's target object/column was just newly created in this
        /// validation pass; "Update" fires only when an existing one is
        /// being edited; "Both" (the default) fires either way. Lets admin
        /// grandfather legacy data that pre-dates a newly-tightened rule
        /// by setting <see cref="RuleApplyOn.Create"/> so old entities
        /// keep validating clean while new ones must comply.
        /// </summary>
        public RuleApplyOn ApplyOn { get; set; } = RuleApplyOn.Both;

        // --- Polymorphic condition (mutually exclusive sources) ---

        /// <summary>FK to <c>MC_UDP_DEFINITION</c>. When set, the rule fires only
        /// when the entity's UDP value is in <see cref="DependsOnPropertyValues"/>.</summary>
        public int? DependsOnUdpId { get; set; }

        /// <summary>FK to <c>MC_PROPERTY_DEF</c>. When set, the rule fires only
        /// when the entity's erwin built-in property value (read via SCAPI
        /// using <see cref="DependsOnPropertyCode"/>) is in
        /// <see cref="DependsOnPropertyValues"/>.</summary>
        public int? DependsOnPropertyDefId { get; set; }

        /// <summary>CSV of values the source property must match (case-insensitive).
        /// Empty + a source set → "any non-empty value matches". Both FKs null
        /// → rule is unconditional regardless of this field's contents.</summary>
        public string DependsOnPropertyValues { get; set; }

        /// <summary>Resolved UDP name when <see cref="DependsOnUdpId"/> is set
        /// (JOIN result). The UDP value is read via the
        /// <c>"&lt;OwnerClass&gt;.Physical.&lt;UdpName&gt;"</c> SCAPI accessor.</summary>
        public string DependsOnUdpName { get; set; }

        /// <summary>Resolved <c>PROPERTY_CODE</c> when
        /// <see cref="DependsOnPropertyDefId"/> is set (JOIN result). Read as
        /// a direct SCAPI accessor (e.g. <c>"Physical_Data_Type"</c>).</summary>
        public string DependsOnPropertyCode { get; set; }
    }

    /// <summary>
    /// Service for loading and caching naming standard rules from MC_NAMING_STANDARD.
    /// Uses DatabaseService for multi-database support (MSSQL, PostgreSQL, Oracle).
    /// </summary>
    public class NamingStandardService
    {
        private static NamingStandardService _instance;
        private static readonly object _lock = new object();

        private List<NamingStandardRule> _allRules;
        // Keyed on (ObjectType, PropertyCode) - case-insensitive on both parts.
        private Dictionary<(string objectType, string propertyCode), List<NamingStandardRule>> _byKey;
        private bool _isLoaded;
        private string _lastError;

        public static NamingStandardService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new NamingStandardService();
                    }
                }
                return _instance;
            }
        }

        private NamingStandardService()
        {
            _allRules = new List<NamingStandardRule>();
            _byKey = new Dictionary<(string, string), List<NamingStandardRule>>(KeyComparer.Instance);
        }

        // Composite (objectType, propertyCode) comparer that ignores case on both
        // parts so callers can pass "Table" / "Column" / "Physical_Name" without
        // worrying about the exact DB casing.
        private sealed class KeyComparer : IEqualityComparer<(string objectType, string propertyCode)>
        {
            public static readonly KeyComparer Instance = new KeyComparer();
            public bool Equals((string objectType, string propertyCode) x, (string objectType, string propertyCode) y) =>
                StringComparer.OrdinalIgnoreCase.Equals(x.objectType, y.objectType) &&
                StringComparer.OrdinalIgnoreCase.Equals(x.propertyCode, y.propertyCode);
            public int GetHashCode((string objectType, string propertyCode) obj) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.objectType ?? ""),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.propertyCode ?? ""));
        }

        public bool IsLoaded => _isLoaded;
        public string LastError => _lastError;
        public int Count => _allRules.Count;

        /// <summary>
        /// Read-only view of every active rule the service is holding. Used
        /// by <c>ModelConfigForm.LoadNamingStandards</c> to emit a per-rule
        /// diagnostic dump into the file log so a future "regex looks fine
        /// in admin UI but rejects every name" bug can be triaged from the
        /// log without running ad-hoc SQL. Returns an empty list when the
        /// service has not loaded yet.
        /// </summary>
        public IReadOnlyList<NamingStandardRule> AllRules => _allRules;

        /// <summary>
        /// Load all active naming standard rules from MC_NAMING_STANDARD.
        /// </summary>
        public bool LoadStandards()
        {
            try
            {
                _allRules.Clear();
                _byKey.Clear();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"NamingStandardService: {_lastError}");
                    return false;
                }

                var ctx = ConfigContextService.Instance;
                if (!ctx.IsInitialized)
                {
                    _lastError = "ConfigContext not initialized.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"NamingStandardService: {_lastError}");
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
                        pCfg.ParameterName = SqlDialect.Param(dbType, "cfgId");
                        pCfg.Value = ctx.ActiveConfigId;
                        command.Parameters.Add(pCfg);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string ruleTypeRaw = reader["RULE_TYPE"]?.ToString()?.Trim() ?? "";
                                if (!Enum.TryParse<NamingRuleKind>(ruleTypeRaw, ignoreCase: true, out var ruleKind))
                                {
                                    // Admin CHECK constraint guards this column, but defend
                                    // against pre-migration rows surviving in dev/test DBs.
                                    System.Diagnostics.Debug.WriteLine(
                                        $"NamingStandardService: skipping rule ID={reader["ID"]} - unknown RULE_TYPE '{ruleTypeRaw}'");
                                    continue;
                                }

                                bool autoApplyRaw = reader["AUTO_APPLY"] != DBNull.Value && Convert.ToBoolean(reader["AUTO_APPLY"]);

                                var rule = new NamingStandardRule
                                {
                                    Id = Convert.ToInt32(reader["ID"]),
                                    ObjectType = reader["OBJECT_TYPE"]?.ToString()?.Trim() ?? "",
                                    // PROPERTY_CODE is NULL for an object-type-only Required
                                    // rule (LEFT JOIN, no property) - normalised to "".
                                    PropertyCode = reader["PROPERTY_CODE"]?.ToString()?.Trim() ?? "",
                                    // Nullable: an object-type-only Required rule has no property.
                                    PropertyDefId = reader["PROPERTY_DEF_ID"] == DBNull.Value
                                        ? (int?)null
                                        : Convert.ToInt32(reader["PROPERTY_DEF_ID"]),
                                    RuleType = ruleKind,
                                    IsRequired = reader["IS_REQUIRED"] != DBNull.Value && Convert.ToBoolean(reader["IS_REQUIRED"]),
                                    Prefix = reader["PREFIX"] == DBNull.Value ? "" : reader["PREFIX"]?.ToString()?.Trim() ?? "",
                                    Suffix = reader["SUFFIX"] == DBNull.Value ? "" : reader["SUFFIX"]?.ToString()?.Trim() ?? "",
                                    LengthOperator = reader["LENGTH_OPERATOR"] == DBNull.Value ? "" : reader["LENGTH_OPERATOR"]?.ToString()?.Trim() ?? "",
                                    LengthValue = reader["LENGTH_VALUE"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["LENGTH_VALUE"]),
                                    // Defensive trim: 2026-05-15 we observed admin's UI
                                    // saving '^.{0,3}$\n' (trailing newline) which made
                                    // Regex.IsMatch reject every name because the literal
                                    // \n at the end required a newline in the input.
                                    // Trim removes any leading/trailing whitespace before
                                    // the regex compiler sees the pattern.
                                    RegexpPattern = reader["REGEXP_PATTERN"] == DBNull.Value ? "" : (reader["REGEXP_PATTERN"]?.ToString() ?? "").Trim(),
                                    ErrorMessage = reader["ERROR_MESSAGE"] == DBNull.Value ? "" : reader["ERROR_MESSAGE"]?.ToString() ?? "",
                                    // AUTO_APPLY is only meaningful for Prefix/Suffix; admin
                                    // stores 0 on other kinds, but defend against legacy or
                                    // hand-edited rows by forcing it to false here too.
                                    AutoApply = autoApplyRaw && (ruleKind == NamingRuleKind.Prefix || ruleKind == NamingRuleKind.Suffix),
                                    IsActive = Convert.ToBoolean(reader["IS_ACTIVE"]),
                                    SortOrder = reader["SORT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["SORT_ORDER"]),
                                    ConfigId = Convert.ToInt32(reader["CONFIG_ID"]),
                                    DependsOnUdpId = reader["DEPENDS_ON_UDP_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DEPENDS_ON_UDP_ID"]),
                                    DependsOnPropertyDefId = reader["DEPENDS_ON_PROPERTY_DEF_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DEPENDS_ON_PROPERTY_DEF_ID"]),
                                    DependsOnPropertyValues = reader["DEPENDS_ON_PROPERTY_VALUES"] == DBNull.Value ? "" : reader["DEPENDS_ON_PROPERTY_VALUES"]?.ToString()?.Trim() ?? "",
                                    DependsOnUdpName = reader["UDP_NAME"] == DBNull.Value ? "" : reader["UDP_NAME"]?.ToString()?.Trim() ?? "",
                                    DependsOnPropertyCode = reader["COND_PROPERTY_CODE"] == DBNull.Value ? "" : reader["COND_PROPERTY_CODE"]?.ToString()?.Trim() ?? "",
                                    ApplyOn = ParseApplyOn(reader["APPLY_ON"]),
                                };

                                if (!rule.IsActive) continue;

                                // Defence-in-depth mirror of admin's CK_MC_NAMING_COND_XOR: a
                                // hand-edited row that points at both sources would otherwise
                                // produce confusing dual-read behaviour. Skip and log.
                                if (rule.DependsOnUdpId.HasValue && rule.DependsOnPropertyDefId.HasValue)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"NamingStandardService: skipping rule ID={rule.Id} - both DEPENDS_ON_UDP_ID and DEPENDS_ON_PROPERTY_DEF_ID set");
                                    continue;
                                }

                                _allRules.Add(rule);

                                var key = (rule.ObjectType, rule.PropertyCode);
                                if (!_byKey.TryGetValue(key, out var bucket))
                                {
                                    bucket = new List<NamingStandardRule>();
                                    _byKey[key] = bucket;
                                }
                                bucket.Add(rule);
                            }
                        }
                    }
                }

                _isLoaded = true;
                var typeSummary = string.Join(", ", _byKey.Select(kv => $"{kv.Key.objectType}.{kv.Key.propertyCode}={kv.Value.Count}"));
                System.Diagnostics.Debug.WriteLine($"NamingStandardService: Loaded {_allRules.Count} active rules ({typeSummary})");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"NamingStandardService.LoadStandards error: {ex.Message}");
                return false;
            }
        }

        // Schema (post 2026-05-17 admin refactor + C3 polymorphic condition):
        //   MC_NAMING_STANDARD.OBJECT_TYPE_ID            -> MC_OBJECT_TYPE.ID
        //   MC_NAMING_STANDARD.PROPERTY_DEF_ID           -> MC_PROPERTY_DEF.ID (the property the rule constrains)
        //   MC_NAMING_STANDARD.RULE_TYPE                  -> one of Prefix/Suffix/Length/Regexp
        //   MC_NAMING_STANDARD.IS_REQUIRED                -> orthogonal non-empty gate
        //   MC_NAMING_STANDARD.DEPENDS_ON_UDP_ID          -> MC_UDP_DEFINITION.ID (XOR with PROPERTY_DEF_ID below)
        //   MC_NAMING_STANDARD.DEPENDS_ON_PROPERTY_DEF_ID -> MC_PROPERTY_DEF.ID (erwin built-in property source)
        //   MC_NAMING_STANDARD.DEPENDS_ON_PROPERTY_VALUES -> CSV of allowed source values (case-insensitive IN)
        // Filter: pd.DBMS_VERSION_ID IS NULL means "erwin built-in property" -
        // those are the only RULE TARGETS the addin validates today (Physical_Name,
        // Logical_Name, Name_Qualifier, ...). The CONDITION property (cond_pd JOIN)
        // is unfiltered because the spec allows conditioning on DBMS-version-
        // specific built-ins (e.g. Oracle-only Identity_Type).
        /// <summary>
        /// Parse the APPLY_ON column with safe defaulting to
        /// <see cref="RuleApplyOn.Both"/>. NULL / empty / unparseable
        /// values fall back to Both so a hand-edited DB or a partially
        /// migrated row never silently disables a rule. 2026-05-25.
        /// </summary>
        private static RuleApplyOn ParseApplyOn(object dbValue)
        {
            if (dbValue == null || dbValue == DBNull.Value) return RuleApplyOn.Both;
            string raw = dbValue.ToString()?.Trim() ?? "";
            if (raw.Length == 0) return RuleApplyOn.Both;
            if (Enum.TryParse<RuleApplyOn>(raw, ignoreCase: true, out var parsed))
                return parsed;
            System.Diagnostics.Debug.WriteLine($"NamingStandardService: unknown APPLY_ON '{raw}', defaulting to Both");
            return RuleApplyOn.Both;
        }

        private static string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ns.""ID"", ot.""NAME"" AS ""OBJECT_TYPE"", pd.""PROPERTY_CODE"",
                            ns.""PROPERTY_DEF_ID"", ns.""RULE_TYPE"", ns.""IS_REQUIRED"",
                            ns.""PREFIX"", ns.""SUFFIX"", ns.""LENGTH_OPERATOR"", ns.""LENGTH_VALUE"",
                            ns.""REGEXP_PATTERN"", ns.""ERROR_MESSAGE"", ns.""AUTO_APPLY"", ns.""IS_ACTIVE"", ns.""SORT_ORDER"",
                            ns.""CONFIG_ID"", ns.""DEPENDS_ON_UDP_ID"", ns.""DEPENDS_ON_PROPERTY_DEF_ID"",
                            ns.""DEPENDS_ON_PROPERTY_VALUES"", ns.""APPLY_ON"",
                            udp.""NAME"" AS ""UDP_NAME"",
                            cond_pd.""PROPERTY_CODE"" AS ""COND_PROPERTY_CODE""
                            FROM ""MC_NAMING_STANDARD"" ns
                            JOIN ""MC_OBJECT_TYPE""  ot ON ot.""ID"" = ns.""OBJECT_TYPE_ID""
                            LEFT JOIN ""MC_PROPERTY_DEF"" pd ON pd.""ID"" = ns.""PROPERTY_DEF_ID""
                            LEFT JOIN ""MC_UDP_DEFINITION"" udp ON udp.""ID"" = ns.""DEPENDS_ON_UDP_ID""
                            LEFT JOIN ""MC_PROPERTY_DEF"" cond_pd ON cond_pd.""ID"" = ns.""DEPENDS_ON_PROPERTY_DEF_ID""
                            WHERE ns.""IS_ACTIVE"" = true
                              AND ns.""CONFIG_ID"" = @cfgId
                              AND (ns.""PROPERTY_DEF_ID"" IS NULL OR pd.""DBMS_VERSION_ID"" IS NULL)
                            ORDER BY ot.""NAME"", pd.""PROPERTY_CODE"", ns.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT ns.ID, ot.NAME AS OBJECT_TYPE, pd.PROPERTY_CODE,
                            ns.PROPERTY_DEF_ID, ns.RULE_TYPE, ns.IS_REQUIRED,
                            ns.PREFIX, ns.SUFFIX, ns.LENGTH_OPERATOR, ns.LENGTH_VALUE,
                            ns.REGEXP_PATTERN, ns.ERROR_MESSAGE, ns.AUTO_APPLY, ns.IS_ACTIVE, ns.SORT_ORDER,
                            ns.CONFIG_ID, ns.DEPENDS_ON_UDP_ID, ns.DEPENDS_ON_PROPERTY_DEF_ID,
                            ns.DEPENDS_ON_PROPERTY_VALUES, ns.APPLY_ON,
                            udp.NAME AS UDP_NAME,
                            cond_pd.PROPERTY_CODE AS COND_PROPERTY_CODE
                            FROM MC_NAMING_STANDARD ns
                            JOIN MC_OBJECT_TYPE  ot ON ot.ID = ns.OBJECT_TYPE_ID
                            LEFT JOIN MC_PROPERTY_DEF pd ON pd.ID = ns.PROPERTY_DEF_ID
                            LEFT JOIN MC_UDP_DEFINITION udp ON udp.ID = ns.DEPENDS_ON_UDP_ID
                            LEFT JOIN MC_PROPERTY_DEF cond_pd ON cond_pd.ID = ns.DEPENDS_ON_PROPERTY_DEF_ID
                            WHERE ns.IS_ACTIVE = 1
                              AND ns.CONFIG_ID = :cfgId
                              AND (ns.PROPERTY_DEF_ID IS NULL OR pd.DBMS_VERSION_ID IS NULL)
                            ORDER BY ot.NAME, pd.PROPERTY_CODE, ns.SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT ns.[ID], ot.[NAME] AS [OBJECT_TYPE], pd.[PROPERTY_CODE],
                            ns.[PROPERTY_DEF_ID], ns.[RULE_TYPE], ns.[IS_REQUIRED],
                            ns.[PREFIX], ns.[SUFFIX], ns.[LENGTH_OPERATOR], ns.[LENGTH_VALUE],
                            ns.[REGEXP_PATTERN], ns.[ERROR_MESSAGE], ns.[AUTO_APPLY], ns.[IS_ACTIVE], ns.[SORT_ORDER],
                            ns.[CONFIG_ID], ns.[DEPENDS_ON_UDP_ID], ns.[DEPENDS_ON_PROPERTY_DEF_ID],
                            ns.[DEPENDS_ON_PROPERTY_VALUES], ns.[APPLY_ON],
                            udp.[NAME] AS [UDP_NAME],
                            cond_pd.[PROPERTY_CODE] AS [COND_PROPERTY_CODE]
                            FROM [dbo].[MC_NAMING_STANDARD] ns
                            JOIN [dbo].[MC_OBJECT_TYPE]  ot ON ot.[ID] = ns.[OBJECT_TYPE_ID]
                            LEFT JOIN [dbo].[MC_PROPERTY_DEF] pd ON pd.[ID] = ns.[PROPERTY_DEF_ID]
                            LEFT JOIN [dbo].[MC_UDP_DEFINITION] udp ON udp.[ID] = ns.[DEPENDS_ON_UDP_ID]
                            LEFT JOIN [dbo].[MC_PROPERTY_DEF] cond_pd ON cond_pd.[ID] = ns.[DEPENDS_ON_PROPERTY_DEF_ID]
                            WHERE ns.[IS_ACTIVE] = 1
                              AND ns.[CONFIG_ID] = @cfgId
                              AND (ns.[PROPERTY_DEF_ID] IS NULL OR pd.[DBMS_VERSION_ID] IS NULL)
                            ORDER BY ot.[NAME], pd.[PROPERTY_CODE], ns.[SORT_ORDER]";
            }
        }

        /// <summary>
        /// Get active rules for a specific (object type, property code) pair.
        /// Both arguments are case-insensitive. The DB stores object types in
        /// upper case ("TABLE"/"COLUMN"/...) but callers may pass "Table"/"Column".
        /// Property codes follow erwin's PascalCase ("Physical_Name", "Logical_Name").
        /// </summary>
        public IEnumerable<NamingStandardRule> GetByObjectTypeAndProperty(string objectType, string propertyCode)
        {
            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(propertyCode))
                return Enumerable.Empty<NamingStandardRule>();
            if (_byKey.TryGetValue((objectType, propertyCode), out var list))
                return list.OrderBy(r => r.SortOrder);
            return Enumerable.Empty<NamingStandardRule>();
        }

        /// <summary>
        /// Test-only seed. Replaces the rule cache with the supplied set and
        /// marks the service as loaded so <c>ApplyNamingStandards</c> /
        /// <c>ValidateObjectName</c> orchestration tests can exercise the
        /// full engine without hitting the admin DB or mocking SCAPI. The
        /// dictionary keying / sort behaviour matches
        /// <see cref="LoadStandards"/> so a test-seeded ruleset behaves
        /// identically to a DB-loaded one. Production code paths never call
        /// this; only the test project (<c>tests/ErwinAddIn.Tests</c>) does.
        /// </summary>
        public void SeedForTesting(IEnumerable<NamingStandardRule> rules)
        {
            _allRules.Clear();
            _byKey.Clear();
            _lastError = null;
            if (rules != null)
            {
                foreach (var r in rules)
                {
                    if (r == null) continue;
                    _allRules.Add(r);
                    var key = (r.ObjectType ?? "", r.PropertyCode ?? "");
                    if (!_byKey.TryGetValue(key, out var list))
                    {
                        list = new List<NamingStandardRule>();
                        _byKey[key] = list;
                    }
                    list.Add(r);
                }
            }
            _isLoaded = true;
        }

        /// <summary>
        /// Distinct PROPERTY_CODE values that have at least one active rule
        /// for the given object type. Used by the new-entity validation path
        /// in <c>TableTypeMonitorService</c> so it can iterate every property
        /// the admin defined a rule against, not just Physical_Name.
        /// Returns empty when nothing is loaded or no rules target the type.
        /// </summary>
        public IReadOnlyList<string> GetPropertyCodes(string objectType)
        {
            if (string.IsNullOrEmpty(objectType)) return Array.Empty<string>();
            return _byKey
                .Where(kv => string.Equals(kv.Key.objectType, objectType, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key.propertyCode)
                .Where(pc => !string.IsNullOrEmpty(pc))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Distinct PROPERTY_CODE values that carry at least one rule with
        /// <c>IS_REQUIRED=true</c> for the given object type. The caller
        /// uses this to decide whether a non-Required violation (Length /
        /// Regexp) should still be enforced through the modal input popup
        /// instead of the consolidated warning - when admin opted into
        /// the required-fill UX for a property, every rule on that
        /// property inherits the "user must fix" treatment so a short
        /// non-empty value cannot slip through with just an OK click.
        /// Returns empty when nothing is loaded or no required rules
        /// target the type. Case-insensitive on both keys.
        /// </summary>
        public IReadOnlyCollection<string> GetRequiredPropertyCodes(string objectType)
        {
            if (string.IsNullOrEmpty(objectType))
                return Array.Empty<string>();
            // A property counts as "required" if EITHER the legacy
            // IS_REQUIRED flag is set on any active rule OR there is a
            // dedicated RULE_TYPE='Required' row. Both shapes coexist
            // post-2026-05-25 migration.
            return _allRules
                .Where(r => r != null
                            && r.IsActive
                            && !string.IsNullOrEmpty(r.PropertyCode)
                            && (r.IsRequired || r.RuleType == NamingRuleKind.Required)
                            && string.Equals(r.ObjectType, objectType, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.PropertyCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Active object-type-only Required rules (Property "(none)"): each
        /// asserts "an object of this type must exist in the model". They carry
        /// no property, so they never enter the per-property <c>_byKey</c>
        /// buckets and are invisible to <see cref="GetByObjectTypeAndProperty"/>
        /// / <see cref="GetRequiredPropertyCodes"/> (which filter out empty
        /// PropertyCode); the model-open existence check retrieves them here.
        /// 2026-06-15.
        /// </summary>
        public IReadOnlyList<NamingStandardRule> GetObjectExistenceRules()
        {
            return _allRules
                .Where(r => r != null
                            && r.IsActive
                            && r.RuleType == NamingRuleKind.Required
                            && string.IsNullOrEmpty(r.PropertyCode))
                .ToList();
        }

        /// <summary>
        /// Distinct UDP names referenced by any active rule's
        /// <c>DEPENDS_ON_UDP_NAME</c> condition. Used by the live Entity
        /// Editor watcher to know which UDPs to snapshot/diff each tick;
        /// returns an empty list if no conditional rules are loaded so
        /// the watcher can short-circuit cheaply.
        /// </summary>
        public IReadOnlyList<string> GetRelevantUdpNames()
        {
            return _allRules
                .Where(r => !string.IsNullOrEmpty(r.DependsOnUdpName))
                .Select(r => r.DependsOnUdpName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Reload()
        {
            LoadStandards();
        }
    }
}
