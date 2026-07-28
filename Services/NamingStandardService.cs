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

        /// <summary>
        /// GENERATOR (not a validator): renders a target property's value from
        /// a template string and writes it via SCAPI. The template
        /// (<see cref="NamingStandardRule.ValueTemplate"/>) reads properties of
        /// the same object (<c>{PropertyCode}</c>) or a related object
        /// (<c>{Alias.PropertyCode}</c> via <c>MC_OBJECT_RELATION</c>);
        /// <see cref="NamingStandardRule.TemplateFillMode"/> decides Always vs
        /// OnlyIfEmpty. Unlike the other kinds it does not flag a name
        /// violation - it produces a value. Admin 2026-06-23.
        /// </summary>
        Template,
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

        /// <summary>
        /// <c>MC_NAMING_STANDARD.NAME</c> - the admin's own label for this rule, e.g.
        /// "Tablo Ad Standarti" or "TABLE_PHYSICAL_NAME_FORMAT". Admin-authored text, so it
        /// may legitimately be Turkish and must be shown verbatim.
        ///
        /// <para>Frequently EMPTY: the column exists in every admin repo but population is
        /// uneven (23/45 rules in one, 0/103 in another). Anything rendering a rule to a user
        /// must fall back to <c>ApprovalBlockingRuleGate.DescribeRule</c> rather than showing
        /// a blank, which is why this is not the only identity a rule has.</para>
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// <c>MC_NAMING_STANDARD.DESCRIPTION</c> - the admin's longer explanation of what the
        /// rule enforces and why. Also admin-authored, also frequently empty. Distinct from
        /// <see cref="ErrorMessage"/>: this describes the RULE, that one is the text shown to
        /// a user who violated it.
        /// </summary>
        public string Description { get; set; } = "";

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

        /// <summary>
        /// <c>BLOCKS_DDL_APPROVEMENT</c> (admin 2026-07-25): the rule is a HARD gate on
        /// ENTRY TO THE APPROVAL QUEUE. When the config-level
        /// <c>ENFORCE_APPROVAL_BLOCKING_RULES</c> toggle is on,
        /// <see cref="ApprovalBlockingRuleGate"/> checks this rule against the open model
        /// before an Integrate or Promote request is submitted and refuses the submission
        /// while it is violated. DDL GENERATION ITSELF IS NOT GATED - the modeler may
        /// still generate and review DDL for a violating model. Independent of
        /// <see cref="RuleType"/>, <see cref="IsRequired"/> and <see cref="AutoApply"/>.
        /// <para>
        /// NOT populated by <see cref="NamingStandardService.LoadStandards"/>: the flag is
        /// read by the ISOLATED <see cref="NamingStandardService.LoadApprovalBlockingRules"/>
        /// query and stamped onto the cached rules there. Reason: the admin migration for
        /// the column is SQL-Server-only, so putting it in the main SELECT would make the
        /// whole ruleset fail to load on an unmigrated PostgreSQL/Oracle repo - which
        /// silently disables ALL naming enforcement (empty rule set reads as "clean"),
        /// the exact opposite of this feature's intent. Isolating the read keeps that
        /// blast radius inside the gate: a missing column blocks DDL (loudly) and leaves
        /// validation untouched.
        /// </para>
        /// </summary>
        public bool BlocksDdlApprovement { get; set; }

        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
        public int ConfigId { get; set; }

        /// <summary>
        /// VALUE_TEMPLATE: the template string for a
        /// <see cref="NamingRuleKind.Template"/> rule. Tokens are
        /// <c>{PropertyCode}</c> (same object) and <c>{Alias.PropertyCode}</c>
        /// (related object via <c>MC_OBJECT_RELATION</c>); text outside tokens
        /// is kept verbatim. Empty for non-Template rules.
        /// </summary>
        public string ValueTemplate { get; set; }

        /// <summary>
        /// TEMPLATE_FILL_MODE for a <see cref="NamingRuleKind.Template"/> rule:
        /// "Always" (overwrite) or "OnlyIfEmpty" (write only when the target is
        /// empty, never clobber a human value). Empty for non-Template rules.
        /// </summary>
        public string TemplateFillMode { get; set; }

        // --- UDP write target (migration 9, 2026-07-19) ---
        // A Template rule targets EITHER an erwin property (PROPERTY_DEF_ID ->
        // PropertyCode) OR a UDP (TARGET_UDP_ID -> MC_UDP_DEFINITION); the DB
        // check CK_MC_NAMING_TARGET_XOR forbids both. When TargetUdpId is set
        // the rendered value is written to "{OwnerClass}.Physical.{TargetUdpName}"
        // instead of a property code.

        /// <summary>TARGET_UDP_ID (FK to MC_UDP_DEFINITION); null for property-targeted rules.</summary>
        public int? TargetUdpId { get; set; }

        /// <summary>Resolved MC_UDP_DEFINITION.NAME for <see cref="TargetUdpId"/>; "" when property-targeted.</summary>
        public string TargetUdpName { get; set; }

        /// <summary>
        /// Resolved MC_UDP_DEFINITION.OBJECT_TYPE (e.g. "TABLE", "COLUMN",
        /// "MODEL") for <see cref="TargetUdpId"/>; "" when property-targeted.
        /// The applier requires this to match the rule's own object type
        /// (a COLUMN rule may only fill a COLUMN UDP) and skips-with-log on a
        /// mismatch - it never writes to a differently-scoped object.
        /// </summary>
        public string TargetUdpObjectType { get; set; }

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

        // --- Ordered AND/OR condition list (MC_NAMING_RULE_CONDITION) ---

        /// <summary>
        /// The rule's DEPENDS_ON conditions in ORDER_INDEX order, folded strictly
        /// left-to-right (no precedence/parentheses) by <c>NamingValidationEngine</c>.
        /// Empty == the rule is unconditional (always applies). This sub-table is the
        /// SOLE authority; the former flat <c>DEPENDS_ON_*</c> columns on
        /// MC_NAMING_STANDARD were a migration bridge and have been dropped, so there
        /// is no fallback to them.
        /// </summary>
        public List<NamingRuleCondition> Conditions { get; } = new List<NamingRuleCondition>();
    }

    /// <summary>
    /// One term of a naming rule's DEPENDS_ON condition list (a row of
    /// <c>MC_NAMING_RULE_CONDITION</c>). Each term has the SAME shape as the legacy
    /// single condition: a source that is EITHER a UDP (<see cref="DependsOnUdpId"/>)
    /// XOR an erwin built-in property (<see cref="DependsOnPropertyDefId"/>), plus a
    /// CSV of allowed values matched case-insensitively (IN). Terms are joined by
    /// <see cref="Connector"/> ('AND'/'OR'); the first term (ORDER_INDEX 0) has a
    /// NULL connector that is ignored.
    /// </summary>
    public class NamingRuleCondition
    {
        /// <summary>0-based position; term 0 is the left-most, connector ignored.</summary>
        public int OrderIndex { get; set; }

        /// <summary>"AND" / "OR" joining this term to the running result; NULL/"" on
        /// term 0. Unknown/empty values past term 0 default to AND (never loosen).</summary>
        public string Connector { get; set; }

        /// <summary>FK to <c>MC_UDP_DEFINITION</c> (XOR with <see cref="DependsOnPropertyDefId"/>).</summary>
        public int? DependsOnUdpId { get; set; }

        /// <summary>Resolved UDP name (JOIN). Read via the
        /// <c>"&lt;OwnerClass&gt;.Physical.&lt;UdpName&gt;"</c> SCAPI accessor.</summary>
        public string DependsOnUdpName { get; set; }

        /// <summary>FK to <c>MC_PROPERTY_DEF</c> (XOR with <see cref="DependsOnUdpId"/>).</summary>
        public int? DependsOnPropertyDefId { get; set; }

        /// <summary>Resolved <c>PROPERTY_CODE</c> (JOIN), e.g. "Physical_Data_Type".</summary>
        public string DependsOnPropertyCode { get; set; }

        /// <summary>Object type that OWNS the condition property (JOIN
        /// <c>MC_PROPERTY_DEF.OBJECT_TYPE_ID -&gt; MC_OBJECT_TYPE.NAME</c>), e.g. "TABLE"
        /// or "SCHEMA". When it differs from the rule's target object type the property
        /// lives on a RELATED object (a table's owning schema), so the evaluator resolves
        /// it accordingly (SCHEMA.Name = the target's Name_Qualifier). Null for UDP terms.</summary>
        public string DependsOnPropertyObjectType { get; set; }

        /// <summary>CSV the source value must be IN (case-insensitive, trimmed).
        /// Empty + a source set => "any non-empty value matches".</summary>
        public string DependsOnPropertyValues { get; set; }
    }

    /// <summary>
    /// Outcome of <see cref="NamingStandardService.LoadApprovalBlockingRules"/>: the rules the
    /// admin flagged <c>BLOCKS_DDL_APPROVEMENT=1</c> that the loader could resolve, plus
    /// the IDs it could NOT. An unresolved ID means the DB says "this rule gates DDL" but
    /// the add-in never loaded it (inactive, DBMS-version-scoped property, unparseable
    /// RULE_TYPE, target-XOR violation) - i.e. a blocking rule that can never be checked.
    /// The gate treats that as a hard failure rather than a silent pass.
    /// </summary>
    public sealed class ApprovalBlockingRuleSet
    {
        public ApprovalBlockingRuleSet(IReadOnlyList<NamingStandardRule> rules, IReadOnlyList<int> unresolvedRuleIds)
        {
            Rules = rules ?? new List<NamingStandardRule>();
            UnresolvedRuleIds = unresolvedRuleIds ?? new List<int>();
        }

        /// <summary>Blocking rules the add-in holds in full and can evaluate.</summary>
        public IReadOnlyList<NamingStandardRule> Rules { get; }

        /// <summary>MC_NAMING_STANDARD.ID values flagged as blocking but absent from the loaded set.</summary>
        public IReadOnlyList<int> UnresolvedRuleIds { get; }
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
        // CONFIG.ID the cached rules belong to; -1 when nothing is loaded. The cache is
        // config-scoped data with no config stamp of its own, so a consumer that switches
        // models (the DDL queue worker runs job after job against DIFFERENT models, and an
        // MDI tab switch does the same interactively) would otherwise validate model B
        // against model A's rules. Same failure mode the glossary hit in 2026-07 - see
        // EnsureLoadedForConfig.
        private int _loadedConfigId = -1;

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
        /// CONFIG.ID the cached rules were loaded for; -1 when nothing is loaded.
        /// Callers that can be handed a DIFFERENT model than the one the cache was
        /// filled from must gate on this via <see cref="EnsureLoadedForConfig"/>.
        /// </summary>
        public int LoadedConfigId => _loadedConfigId;

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
                                    // Admin's own label and explanation for the rule. Both are
                                    // nullable in every dialect and frequently unpopulated, so
                                    // they are normalised to "" and every consumer needs a
                                    // fallback (see NamingStandardRule.Name).
                                    Name = reader["RULE_NAME"] == DBNull.Value ? "" : reader["RULE_NAME"]?.ToString()?.Trim() ?? "",
                                    Description = reader["RULE_DESCRIPTION"] == DBNull.Value ? "" : reader["RULE_DESCRIPTION"]?.ToString()?.Trim() ?? "",
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
                                    // AUTO_APPLY is meaningful for Prefix/Suffix (silent
                                    // name decoration) and for Template (silent value
                                    // generation). Admin stores 0 on the validate-only
                                    // kinds (Length/Regexp/Required); force false there too
                                    // to defend against legacy or hand-edited rows.
                                    AutoApply = autoApplyRaw && (ruleKind == NamingRuleKind.Prefix
                                        || ruleKind == NamingRuleKind.Suffix
                                        || ruleKind == NamingRuleKind.Template),
                                    IsActive = Convert.ToBoolean(reader["IS_ACTIVE"]),
                                    SortOrder = reader["SORT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["SORT_ORDER"]),
                                    ConfigId = Convert.ToInt32(reader["CONFIG_ID"]),
                                    // DEPENDS_ON conditions are loaded separately from
                                    // MC_NAMING_RULE_CONDITION (see LoadRuleConditions);
                                    // the flat DEPENDS_ON_* columns have been dropped.
                                    ApplyOn = ParseApplyOn(reader["APPLY_ON"]),
                                    // Template-only columns; empty for the other kinds.
                                    // Not trimmed: a template may legitimately rely on
                                    // leading/trailing spaces in the literal text.
                                    ValueTemplate = reader["VALUE_TEMPLATE"] == DBNull.Value ? "" : reader["VALUE_TEMPLATE"]?.ToString() ?? "",
                                    TemplateFillMode = reader["TEMPLATE_FILL_MODE"] == DBNull.Value ? "" : reader["TEMPLATE_FILL_MODE"]?.ToString()?.Trim() ?? "",
                                    // UDP write target (migration 9): resolved via the
                                    // tudp LEFT JOIN; null/"" for property-targeted rules.
                                    TargetUdpId = reader["TARGET_UDP_ID"] == DBNull.Value
                                        ? (int?)null
                                        : Convert.ToInt32(reader["TARGET_UDP_ID"]),
                                    TargetUdpName = reader["TARGET_UDP_NAME"] == DBNull.Value ? "" : reader["TARGET_UDP_NAME"]?.ToString()?.Trim() ?? "",
                                    TargetUdpObjectType = reader["TARGET_UDP_OBJECT_TYPE"] == DBNull.Value ? "" : reader["TARGET_UDP_OBJECT_TYPE"]?.ToString()?.Trim() ?? "",
                                };

                                // CK_MC_NAMING_TARGET_XOR forbids a rule that targets both a
                                // property and a UDP; defend against pre-constraint rows the
                                // same way the condition loader defends its XOR (skip + log).
                                if (rule.PropertyDefId.HasValue && rule.TargetUdpId.HasValue)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"NamingStandardService: skipping rule ID={rule.Id} - both PROPERTY_DEF_ID and TARGET_UDP_ID set (violates CK_MC_NAMING_TARGET_XOR)");
                                    continue;
                                }

                                if (!rule.IsActive) continue;

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

                    // Load each rule's ordered AND/OR condition list from
                    // MC_NAMING_RULE_CONDITION onto the rules just loaded (reusing the
                    // open connection). Inside this try so a condition-load failure
                    // fails the whole load instead of silently treating conditional
                    // rules as unconditional.
                    LoadRuleConditions(connection, dbType, ctx.ActiveConfigId);
                }

                _isLoaded = true;
                _loadedConfigId = ctx.ActiveConfigId;
                var typeSummary = string.Join(", ", _byKey.Select(kv => $"{kv.Key.objectType}.{kv.Key.propertyCode}={kv.Value.Count}"));
                System.Diagnostics.Debug.WriteLine($"NamingStandardService: Loaded {_allRules.Count} active rules for config {_loadedConfigId} ({typeSummary})");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                _loadedConfigId = -1;
                System.Diagnostics.Debug.WriteLine($"NamingStandardService.LoadStandards error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Guarantees the cache holds the rules of <paramref name="configId"/>, loading
        /// them when the cache is empty or belongs to a DIFFERENT config. Returns false
        /// (with <see cref="LastError"/> set) when the load fails.
        /// <para>
        /// Two consumers need this. (1) The DDL-generator build flavor never calls
        /// <c>LoadNamingStandards</c> at connect (it deliberately skips every validation
        /// surface), so the cache is empty there. (2) The DDL queue worker processes jobs
        /// against different models back-to-back, so a cache filled for job 1's config
        /// must not be reused for job 2's. The interactive build reloads per connect
        /// anyway, so for it this is a cheap no-op.
        /// </para>
        /// </summary>
        public bool EnsureLoadedForConfig(int configId)
        {
            if (_isLoaded && _loadedConfigId == configId) return true;
            return LoadStandards();
        }

        /// <summary>
        /// The DDL-blocking subset of the active config's rules, resolved through an
        /// ISOLATED query against <c>MC_NAMING_STANDARD.BLOCKS_DDL_APPROVEMENT</c>.
        /// <para>
        /// Why a second query instead of one more column on <see cref="GetQuery"/>: the
        /// admin migration that adds the column is SQL-Server-only. Naming it in the main
        /// SELECT would make <see cref="LoadStandards"/> throw on an unmigrated
        /// PostgreSQL/Oracle repo, and its catch leaves <see cref="IsLoaded"/> false -
        /// at which point <c>NamingValidationEngine.ValidateObjectName</c> returns an
        /// empty list for every object and ALL naming enforcement is silently off. Here
        /// the same missing column throws out of THIS method only, the caller turns that
        /// into a visible "DDL blocked" error, and validation keeps working.
        /// </para>
        /// <para>
        /// Also reports rules the DB flags as blocking that are NOT in the loaded active
        /// set (dropped by the loader as inactive, DBMS-version-scoped, unparseable
        /// RULE_TYPE or target-XOR violating). Those cannot be evaluated at all, and the
        /// gate's contract is that an unevaluatable blocking rule is a hard failure -
        /// never a silent pass - so they are surfaced rather than dropped.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The base ruleset could not be loaded.</exception>
        /// <exception cref="DbException">The blocking-flag query failed (e.g. the column does not exist).</exception>
        public ApprovalBlockingRuleSet LoadApprovalBlockingRules(int configId)
        {
            if (!EnsureLoadedForConfig(configId))
                throw new InvalidOperationException(
                    $"Naming standards could not be loaded for config {configId}: {_lastError ?? "unknown error"}");

            string dbType = DatabaseService.Instance.GetDbType();
            var flagged = new HashSet<int>();

            using (var connection = DatabaseService.Instance.CreateConnection())
            {
                connection.Open();
                using (var command = DatabaseService.Instance.CreateCommand(GetApprovalBlockingIdsQuery(dbType), connection))
                {
                    var pCfg = command.CreateParameter();
                    pCfg.ParameterName = SqlDialect.Param(dbType, "cfgId");
                    pCfg.Value = configId;
                    command.Parameters.Add(pCfg);

                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            flagged.Add(Convert.ToInt32(reader["ID"]));
                }
            }

            // Stamp every cached rule so the flag is readable off the rule object itself
            // (and so a rule that LOST the flag in admin is cleared on the next gate run).
            foreach (var rule in _allRules)
                rule.BlocksDdlApprovement = flagged.Contains(rule.Id);

            var resolved = _allRules.Where(r => r.BlocksDdlApprovement).ToList();
            var unresolved = flagged.Where(id => !_allRules.Any(r => r.Id == id)).OrderBy(id => id).ToList();

            AddinLogger.Log($"[DDL-GATE] blocking rules for config {configId}: {flagged.Count} flagged, " +
                            $"{resolved.Count} resolved ({string.Join(", ", resolved.Select(r => "#" + r.Id))}), " +
                            $"{unresolved.Count} unresolved ({string.Join(", ", unresolved.Select(id => "#" + id))})");

            return new ApprovalBlockingRuleSet(resolved, unresolved);
        }

        /// <summary>
        /// The blocking subset already stamped by the last
        /// <see cref="LoadApprovalBlockingRules"/> call. Empty before the first call - it is
        /// NOT a substitute for it (the flag never comes off the main loader).
        /// </summary>
        public IReadOnlyList<NamingStandardRule> GetApprovalBlockingRules()
            => _allRules.Where(r => r != null && r.IsActive && r.BlocksDdlApprovement).ToList();

        // Deliberately narrow: IDs only. The rule PAYLOAD already lives in the cache from
        // LoadStandards; this query exists solely to answer "which of them gate DDL", so a
        // repo missing the column fails here and nowhere else.
        private static string GetApprovalBlockingIdsQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ns.""ID"" FROM ""MC_NAMING_STANDARD"" ns
                            WHERE ns.""CONFIG_ID"" = @cfgId
                              AND ns.""IS_ACTIVE"" = true
                              AND ns.""BLOCKS_DDL_APPROVEMENT"" = true";
                case "ORACLE":
                    return @"SELECT ns.ID FROM MC_NAMING_STANDARD ns
                            WHERE ns.CONFIG_ID = :cfgId
                              AND ns.IS_ACTIVE = 1
                              AND ns.BLOCKS_DDL_APPROVEMENT = 1";
                case "MSSQL":
                default:
                    return @"SELECT ns.[ID] FROM [dbo].[MC_NAMING_STANDARD] ns
                            WHERE ns.[CONFIG_ID] = @cfgId
                              AND ns.[IS_ACTIVE] = 1
                              AND ns.[BLOCKS_DDL_APPROVEMENT] = 1";
            }
        }

        // Schema (post 2026-05-17 admin refactor + C3 polymorphic condition):
        //   MC_NAMING_STANDARD.OBJECT_TYPE_ID            -> MC_OBJECT_TYPE.ID
        //   MC_NAMING_STANDARD.PROPERTY_DEF_ID           -> MC_PROPERTY_DEF.ID (the property the rule constrains)
        //   MC_NAMING_STANDARD.TARGET_UDP_ID              -> MC_UDP_DEFINITION.ID (migration 9: Template rule's UDP
        //                                                    write target; CK_MC_NAMING_TARGET_XOR forbids setting
        //                                                    it together with PROPERTY_DEF_ID)
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

        /// <summary>
        /// Loads MC_NAMING_RULE_CONDITION for the active config and attaches each row
        /// (ORDER_INDEX order) to its owning rule's <see cref="NamingStandardRule.Conditions"/>,
        /// reusing the already-open <paramref name="connection"/>. A row that names
        /// neither source or both is skipped + logged (mirrors the admin XOR CHECK).
        /// Lets a query failure propagate so the caller fails the whole load rather
        /// than silently treating conditional rules as unconditional.
        /// </summary>
        private void LoadRuleConditions(DbConnection connection, string dbType, int configId)
        {
            var byId = new Dictionary<int, NamingStandardRule>();
            foreach (var r in _allRules)
                byId[r.Id] = r;
            if (byId.Count == 0) return;

            string query = GetConditionsQuery(dbType);
            using (var command = DatabaseService.Instance.CreateCommand(query, connection))
            {
                var pCfg = command.CreateParameter();
                pCfg.ParameterName = SqlDialect.Param(dbType, "cfgId");
                pCfg.Value = configId;
                command.Parameters.Add(pCfg);

                int loaded = 0, skipped = 0;
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int nsId = Convert.ToInt32(reader["NAMING_STANDARD_ID"]);
                        if (!byId.TryGetValue(nsId, out var rule)) continue; // condition for a rule we did not load (inactive/other config)

                        int? udpId = reader["DEPENDS_ON_UDP_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DEPENDS_ON_UDP_ID"]);
                        int? propDefId = reader["DEPENDS_ON_PROPERTY_DEF_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DEPENDS_ON_PROPERTY_DEF_ID"]);

                        // Mirror admin's CK XOR: each term must name EXACTLY one source.
                        if (udpId.HasValue == propDefId.HasValue)
                        {
                            AddinLogger.Log(
                                $"NAMING_RULE_CONDITION SKIP: rule {nsId} order {reader["ORDER_INDEX"]} names {(udpId.HasValue ? "BOTH" : "NO")} source(s) - term dropped");
                            skipped++;
                            continue;
                        }

                        rule.Conditions.Add(new NamingRuleCondition
                        {
                            OrderIndex = Convert.ToInt32(reader["ORDER_INDEX"]),
                            Connector = reader["CONNECTOR"] == DBNull.Value ? null : reader["CONNECTOR"]?.ToString()?.Trim(),
                            DependsOnUdpId = udpId,
                            DependsOnUdpName = reader["UDP_NAME"] == DBNull.Value ? "" : reader["UDP_NAME"]?.ToString()?.Trim() ?? "",
                            DependsOnPropertyDefId = propDefId,
                            DependsOnPropertyCode = reader["COND_PROPERTY_CODE"] == DBNull.Value ? "" : reader["COND_PROPERTY_CODE"]?.ToString()?.Trim() ?? "",
                            DependsOnPropertyObjectType = reader["COND_PROPERTY_OBJECT_TYPE"] == DBNull.Value ? "" : reader["COND_PROPERTY_OBJECT_TYPE"]?.ToString()?.Trim() ?? "",
                            DependsOnPropertyValues = reader["DEPENDS_ON_PROPERTY_VALUES"] == DBNull.Value ? "" : reader["DEPENDS_ON_PROPERTY_VALUES"]?.ToString()?.Trim() ?? "",
                        });
                        loaded++;
                    }
                }

                // Defensive: the fold relies on ORDER_INDEX order. The query already
                // sorts, but a per-rule sort guarantees it regardless of provider quirks.
                foreach (var rule in byId.Values)
                    rule.Conditions.Sort((a, b) => a.OrderIndex.CompareTo(b.OrderIndex));

                AddinLogger.Log($"NAMING_RULE_CONDITION loaded: {loaded} condition(s) across {byId.Count} rule(s) ({skipped} skipped). Per-rule counts: {string.Join(", ", _allRules.Where(r => r.Conditions.Count > 0).Select(r => $"#{r.Id}={r.Conditions.Count}"))}");
            }
        }

        private static string GetConditionsQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT rc.""NAMING_STANDARD_ID"", rc.""ORDER_INDEX"", rc.""CONNECTOR"",
                            rc.""DEPENDS_ON_UDP_ID"", rc.""DEPENDS_ON_PROPERTY_DEF_ID"", rc.""DEPENDS_ON_PROPERTY_VALUES"",
                            udp.""NAME"" AS ""UDP_NAME"",
                            cond_pd.""PROPERTY_CODE"" AS ""COND_PROPERTY_CODE"",
                            cond_ot.""NAME"" AS ""COND_PROPERTY_OBJECT_TYPE""
                            FROM ""MC_NAMING_RULE_CONDITION"" rc
                            JOIN ""MC_NAMING_STANDARD"" ns ON ns.""ID"" = rc.""NAMING_STANDARD_ID""
                            LEFT JOIN ""MC_UDP_DEFINITION"" udp ON udp.""ID"" = rc.""DEPENDS_ON_UDP_ID""
                            LEFT JOIN ""MC_PROPERTY_DEF"" cond_pd ON cond_pd.""ID"" = rc.""DEPENDS_ON_PROPERTY_DEF_ID""
                            LEFT JOIN ""MC_OBJECT_TYPE"" cond_ot ON cond_ot.""ID"" = cond_pd.""OBJECT_TYPE_ID""
                            WHERE ns.""CONFIG_ID"" = @cfgId
                            ORDER BY rc.""NAMING_STANDARD_ID"", rc.""ORDER_INDEX""";
                case "ORACLE":
                    return @"SELECT rc.NAMING_STANDARD_ID, rc.ORDER_INDEX, rc.CONNECTOR,
                            rc.DEPENDS_ON_UDP_ID, rc.DEPENDS_ON_PROPERTY_DEF_ID, rc.DEPENDS_ON_PROPERTY_VALUES,
                            udp.NAME AS UDP_NAME,
                            cond_pd.PROPERTY_CODE AS COND_PROPERTY_CODE,
                            cond_ot.NAME AS COND_PROPERTY_OBJECT_TYPE
                            FROM MC_NAMING_RULE_CONDITION rc
                            JOIN MC_NAMING_STANDARD ns ON ns.ID = rc.NAMING_STANDARD_ID
                            LEFT JOIN MC_UDP_DEFINITION udp ON udp.ID = rc.DEPENDS_ON_UDP_ID
                            LEFT JOIN MC_PROPERTY_DEF cond_pd ON cond_pd.ID = rc.DEPENDS_ON_PROPERTY_DEF_ID
                            LEFT JOIN MC_OBJECT_TYPE cond_ot ON cond_ot.ID = cond_pd.OBJECT_TYPE_ID
                            WHERE ns.CONFIG_ID = :cfgId
                            ORDER BY rc.NAMING_STANDARD_ID, rc.ORDER_INDEX";
                default: // SQL Server
                    return @"SELECT rc.[NAMING_STANDARD_ID], rc.[ORDER_INDEX], rc.[CONNECTOR],
                            rc.[DEPENDS_ON_UDP_ID], rc.[DEPENDS_ON_PROPERTY_DEF_ID], rc.[DEPENDS_ON_PROPERTY_VALUES],
                            udp.[NAME] AS [UDP_NAME],
                            cond_pd.[PROPERTY_CODE] AS [COND_PROPERTY_CODE],
                            cond_ot.[NAME] AS [COND_PROPERTY_OBJECT_TYPE]
                            FROM [dbo].[MC_NAMING_RULE_CONDITION] rc
                            JOIN [dbo].[MC_NAMING_STANDARD] ns ON ns.[ID] = rc.[NAMING_STANDARD_ID]
                            LEFT JOIN [dbo].[MC_UDP_DEFINITION] udp ON udp.[ID] = rc.[DEPENDS_ON_UDP_ID]
                            LEFT JOIN [dbo].[MC_PROPERTY_DEF] cond_pd ON cond_pd.[ID] = rc.[DEPENDS_ON_PROPERTY_DEF_ID]
                            LEFT JOIN [dbo].[MC_OBJECT_TYPE] cond_ot ON cond_ot.[ID] = cond_pd.[OBJECT_TYPE_ID]
                            WHERE ns.[CONFIG_ID] = @cfgId
                            ORDER BY rc.[NAMING_STANDARD_ID], rc.[ORDER_INDEX]";
            }
        }

        private static string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ns.""ID"", ot.""NAME"" AS ""OBJECT_TYPE"", pd.""PROPERTY_CODE"",
                            ns.""PROPERTY_DEF_ID"", ns.""RULE_TYPE"", ns.""IS_REQUIRED"",
                            ns.""NAME"" AS ""RULE_NAME"", ns.""DESCRIPTION"" AS ""RULE_DESCRIPTION"",
                            ns.""PREFIX"", ns.""SUFFIX"", ns.""LENGTH_OPERATOR"", ns.""LENGTH_VALUE"",
                            ns.""REGEXP_PATTERN"", ns.""ERROR_MESSAGE"", ns.""AUTO_APPLY"", ns.""IS_ACTIVE"", ns.""SORT_ORDER"",
                            ns.""CONFIG_ID"", ns.""APPLY_ON"",
                            ns.""VALUE_TEMPLATE"", ns.""TEMPLATE_FILL_MODE"",
                            ns.""TARGET_UDP_ID"", tudp.""NAME"" AS ""TARGET_UDP_NAME"", tudp.""OBJECT_TYPE"" AS ""TARGET_UDP_OBJECT_TYPE""
                            FROM ""MC_NAMING_STANDARD"" ns
                            JOIN ""MC_OBJECT_TYPE""  ot ON ot.""ID"" = ns.""OBJECT_TYPE_ID""
                            LEFT JOIN ""MC_PROPERTY_DEF"" pd ON pd.""ID"" = ns.""PROPERTY_DEF_ID""
                            LEFT JOIN ""MC_UDP_DEFINITION"" tudp ON tudp.""ID"" = ns.""TARGET_UDP_ID""
                            WHERE ns.""IS_ACTIVE"" = true
                              AND ns.""CONFIG_ID"" = @cfgId
                              AND (ns.""PROPERTY_DEF_ID"" IS NULL OR pd.""DBMS_VERSION_ID"" IS NULL)
                            ORDER BY ot.""NAME"", pd.""PROPERTY_CODE"", ns.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT ns.ID, ot.NAME AS OBJECT_TYPE, pd.PROPERTY_CODE,
                            ns.PROPERTY_DEF_ID, ns.RULE_TYPE, ns.IS_REQUIRED,
                            ns.NAME AS RULE_NAME, ns.DESCRIPTION AS RULE_DESCRIPTION,
                            ns.PREFIX, ns.SUFFIX, ns.LENGTH_OPERATOR, ns.LENGTH_VALUE,
                            ns.REGEXP_PATTERN, ns.ERROR_MESSAGE, ns.AUTO_APPLY, ns.IS_ACTIVE, ns.SORT_ORDER,
                            ns.CONFIG_ID, ns.APPLY_ON,
                            ns.VALUE_TEMPLATE, ns.TEMPLATE_FILL_MODE,
                            ns.TARGET_UDP_ID, tudp.NAME AS TARGET_UDP_NAME, tudp.OBJECT_TYPE AS TARGET_UDP_OBJECT_TYPE
                            FROM MC_NAMING_STANDARD ns
                            JOIN MC_OBJECT_TYPE  ot ON ot.ID = ns.OBJECT_TYPE_ID
                            LEFT JOIN MC_PROPERTY_DEF pd ON pd.ID = ns.PROPERTY_DEF_ID
                            LEFT JOIN MC_UDP_DEFINITION tudp ON tudp.ID = ns.TARGET_UDP_ID
                            WHERE ns.IS_ACTIVE = 1
                              AND ns.CONFIG_ID = :cfgId
                              AND (ns.PROPERTY_DEF_ID IS NULL OR pd.DBMS_VERSION_ID IS NULL)
                            ORDER BY ot.NAME, pd.PROPERTY_CODE, ns.SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT ns.[ID], ot.[NAME] AS [OBJECT_TYPE], pd.[PROPERTY_CODE],
                            ns.[PROPERTY_DEF_ID], ns.[RULE_TYPE], ns.[IS_REQUIRED],
                            ns.[NAME] AS [RULE_NAME], ns.[DESCRIPTION] AS [RULE_DESCRIPTION],
                            ns.[PREFIX], ns.[SUFFIX], ns.[LENGTH_OPERATOR], ns.[LENGTH_VALUE],
                            ns.[REGEXP_PATTERN], ns.[ERROR_MESSAGE], ns.[AUTO_APPLY], ns.[IS_ACTIVE], ns.[SORT_ORDER],
                            ns.[CONFIG_ID], ns.[APPLY_ON],
                            ns.[VALUE_TEMPLATE], ns.[TEMPLATE_FILL_MODE],
                            ns.[TARGET_UDP_ID], tudp.[NAME] AS [TARGET_UDP_NAME], tudp.[OBJECT_TYPE] AS [TARGET_UDP_OBJECT_TYPE]
                            FROM [dbo].[MC_NAMING_STANDARD] ns
                            JOIN [dbo].[MC_OBJECT_TYPE]  ot ON ot.[ID] = ns.[OBJECT_TYPE_ID]
                            LEFT JOIN [dbo].[MC_PROPERTY_DEF] pd ON pd.[ID] = ns.[PROPERTY_DEF_ID]
                            LEFT JOIN [dbo].[MC_UDP_DEFINITION] tudp ON tudp.[ID] = ns.[TARGET_UDP_ID]
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
        /// <param name="rules">The rules to place in the cache.</param>
        /// <param name="configId">CONFIG.ID the seeded set stands for. Defaults to -1,
        /// which is what <c>ConfigContextService.ActiveConfigId</c> reports when no model
        /// is bound, so <see cref="EnsureLoadedForConfig"/> treats a seeded cache as
        /// already-current and does not try to reach a database from a unit test.</param>
        public void SeedForTesting(IEnumerable<NamingStandardRule> rules, int configId = -1)
        {
            _allRules.Clear();
            _byKey.Clear();
            _lastError = null;
            _loadedConfigId = configId;
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
        /// Active <see cref="NamingRuleKind.Template"/> rules for the given
        /// object type (e.g. "Column"), ordered by SortOrder. A Template rule
        /// must target EITHER a property (PropertyCode) OR a UDP (TargetUdpId
        /// with a resolved TargetUdpName - migration 9) and carry a non-empty
        /// ValueTemplate; malformed rows missing both targets (or the template)
        /// are skipped here so the runtime applier never renders against
        /// nothing. The runtime applier uses this instead of the per-property
        /// <c>_byKey</c> buckets because it iterates Template rules by object
        /// type, not by target property.
        /// </summary>
        public IReadOnlyList<NamingStandardRule> GetTemplateRules(string objectType)
        {
            if (string.IsNullOrEmpty(objectType)) return new List<NamingStandardRule>();
            return _allRules
                .Where(r => r != null
                            && r.IsActive
                            && r.RuleType == NamingRuleKind.Template
                            && string.Equals(r.ObjectType, objectType, StringComparison.OrdinalIgnoreCase)
                            && (!string.IsNullOrEmpty(r.PropertyCode)
                                || (r.TargetUdpId.HasValue && !string.IsNullOrEmpty(r.TargetUdpName)))
                            && !string.IsNullOrEmpty(r.ValueTemplate))
                .OrderBy(r => r.SortOrder)
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
                .SelectMany(r => r.Conditions)
                .Where(c => !string.IsNullOrEmpty(c.DependsOnUdpName))
                .Select(c => c.DependsOnUdpName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Reload()
        {
            LoadStandards();
        }
    }
}
