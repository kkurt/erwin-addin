using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Result of a single naming standard validation check.
    /// </summary>
    public class NamingValidationResult
    {
        public bool IsValid { get; set; }
        public string RuleName { get; set; }
        public string ErrorMessage { get; set; }
        // Reference to the rule the check came from (Phase-2H, 2026-05-13).
        // Needed by the table-side auto-rename-to-PLEASE_CHANGE_IT path so it
        // can read .AutoApply per failing rule without re-querying the rule
        // store by name (RuleName here is the check kind, not the rule key).
        public NamingStandardRule Rule { get; set; }

        public static NamingValidationResult Valid(string ruleName, NamingStandardRule rule = null) =>
            new NamingValidationResult { IsValid = true, RuleName = ruleName, Rule = rule };

        public static NamingValidationResult Invalid(string ruleName, string message, NamingStandardRule rule = null) =>
            new NamingValidationResult { IsValid = false, RuleName = ruleName, ErrorMessage = message, Rule = rule };
    }

    /// <summary>
    /// Stateless validation engine for object naming standards.
    /// Supports UDP-conditioned rules (DEPENDS_ON_UDP_ID + DEPENDS_ON_UDP_VALUE).
    /// </summary>
    public static class NamingValidationEngine
    {
        // Default property the addin validates for every object type. Schema
        // post-2026-05-04 keys rules on (OBJECT_TYPE, PROPERTY_CODE); the
        // addin's call sites always validate the SCAPI "Physical_Name"
        // equivalent (entity.Name, attribute.PhysicalName, key_group.Name,
        // ...). Other property codes (Logical_Name, Definition) require
        // distinct SCAPI reads and are deferred until a use case appears.
        private const string DefaultPropertyCode = "Physical_Name";

        /// <summary>
        /// Validate an object name against naming standard rules.
        /// Rules with DEPENDS_ON_UDP_ID are only applied if the object's UDP value matches.
        /// </summary>
        /// <param name="objectType">Object type: "Table", "Column", "Index", "View"</param>
        /// <param name="objectName">Physical name of the object</param>
        /// <param name="scapiObject">erwin SCAPI object for reading UDP values (null = skip conditional rules)</param>
        /// <param name="propertyCode">Property whose value is being validated; defaults to "Physical_Name".</param>
        /// <param name="isNew">Set to <c>true</c> when the target object/column was just created
        /// in this validation pass; false (default) for edits to existing ones. Filters rules
        /// by their <see cref="NamingStandardRule.ApplyOn"/> gate.</param>
        public static List<NamingValidationResult> ValidateObjectName(string objectType, string objectName, dynamic scapiObject = null, string propertyCode = DefaultPropertyCode, bool isNew = false)
        {
            var results = new List<NamingValidationResult>();

            // Null is normalised to empty so the IS_REQUIRED gate in
            // EvaluateRule can detect "no value" uniformly. Under the
            // 2026-05-17 semantics an empty value only fires when the
            // rule has IS_REQUIRED=true; otherwise the rule short-
            // circuits and the pattern check is skipped.
            objectName ??= "";
            if (!NamingStandardService.Instance.IsLoaded) return results;

            var rules = NamingStandardService.Instance.GetByObjectTypeAndProperty(objectType, propertyCode);

            foreach (var rule in rules)
            {
                // ApplyOn context gate (2026-05-25): admin can scope a rule
                // to fire only on Create / only on Update / Both. Skip
                // rules that do not match the current context.
                if (!MatchesApplyOn(rule, isNew)) continue;

                // UDP condition check: skip rule if condition doesn't match
                if (!IsRuleApplicable(rule, objectType, scapiObject))
                    continue;

                EvaluateRule(rule, objectName, results);
            }

            // Canonical-form acceptance for affix rules (2026-07-02). With TWO prefix
            // rules on one property the composed name carries both ('VpPF_X'), but the
            // per-rule Prefix check is StartsWith - the inner prefix can NEVER be at
            // position 0, so its violation is unsatisfiable and the Required re-prompt
            // loops forever (user stuck typing the same name). A name that the full
            // canonical apply would leave UNCHANGED already carries every applicable
            // affix in its canonical slot - drop the false affix violations then.
            // Genuinely missing affixes still flag (the apply WOULD change the name).
            if (results.Any(r => !r.IsValid
                    && (r.Rule?.RuleType == NamingRuleKind.Prefix || r.Rule?.RuleType == NamingRuleKind.Suffix)))
            {
                string canonical = ApplyNamingStandards(objectType, objectName, scapiObject,
                    autoOnly: false, propertyCode: propertyCode, isNew: isNew);
                if (string.Equals(canonical, objectName, StringComparison.Ordinal))
                {
                    int dropped = results.RemoveAll(r => !r.IsValid
                        && (r.Rule?.RuleType == NamingRuleKind.Prefix || r.Rule?.RuleType == NamingRuleKind.Suffix));
                    if (dropped > 0)
                        AddinLogger.Log($"NamingValidate: '{objectName}' is already in canonical affix form - dropped {dropped} positional Prefix/Suffix violation(s)");
                }
            }

            return results;
        }

        /// <summary>
        /// Returns true when the rule's <see cref="NamingStandardRule.ApplyOn"/>
        /// gate matches the current evaluation context. <c>Both</c> always
        /// matches; <c>Create</c> matches only when <paramref name="isNew"/>
        /// is true; <c>Update</c> matches only when it is false.
        /// </summary>
        /// <summary>
        /// Strict ApplyOn gate. Create rules fire only when
        /// <paramref name="isNew"/> is true, Update rules only when it
        /// is false, Both always.
        ///
        /// <para>
        /// 2026-05-31 history: a brief detour (commits 2aca8cb +
        /// c50a5be) added a <c>creationGesture</c> override that
        /// widened this gate to fire BOTH Create AND Update rules
        /// during a placeholder commit. That was the wrong semantic -
        /// user explicit rule "ApplyOn=Update rules MUST NEVER fire on
        /// creation". The override was reverted; the bridge
        /// <c>_creationGestureEntityIds</c> in
        /// <c>ValidationCoordinatorService</c> survives but now
        /// propagates <c>isNew=true</c> instead of a separate
        /// widening flag. The engine stays strict.
        /// </para>
        /// </summary>
        public static bool MatchesApplyOn(NamingStandardRule rule, bool isNew)
        {
            if (rule == null) return false;
            switch (rule.ApplyOn)
            {
                case RuleApplyOn.Create: return isNew;
                case RuleApplyOn.Update: return !isNew;
                case RuleApplyOn.Both:
                default: return true;
            }
        }

        /// <summary>
        /// True when <paramref name="newName"/> is erwin's auto-uniquify of
        /// <paramref name="prevName"/>: erwin appends "__NNNN" (double underscore + digits) when a
        /// name it is asked to set collides with an existing object of the same type (e.g. adding a
        /// second column the add-in named "Pre_Abc" yields "Pre_Abc__1069").
        /// <para>
        /// The add-in did NOT choose this name - erwin did, AFTER the add-in's own naming ran - so
        /// the resulting name may violate the naming standard (digits + "__" defeat a PascalCase
        /// regex). Callers re-validate such a rename as a fresh CREATE (isNew=true) so apply=Create
        /// rules re-fire on the erwin-assigned name. Object-type agnostic: the same "__NNNN"
        /// signature is used for columns, tables and views. Ordinal match - the base is identical,
        /// only an appended "__&lt;digits&gt;" differs.
        /// </para>
        /// </summary>
        public static bool IsAutoUniquifyRename(string prevName, string newName)
        {
            if (string.IsNullOrEmpty(prevName) || string.IsNullOrEmpty(newName)) return false;
            const string sep = "__";
            if (newName.Length <= prevName.Length + sep.Length) return false;
            if (!newName.StartsWith(prevName, StringComparison.Ordinal)) return false;
            string tail = newName.Substring(prevName.Length);
            if (!tail.StartsWith(sep, StringComparison.Ordinal)) return false;
            string digits = tail.Substring(sep.Length);
            return digits.Length > 0 && digits.All(char.IsDigit);
        }

        /// <summary>
        /// True when a rename should re-run apply=Create naming rules on the new name: the
        /// baseline is a real (non-empty, non-placeholder) prior name that differs (Ordinal) from
        /// the current one. The user's contract (2026-07-10) is that ANY real rename re-validates
        /// the new name against the same rules a fresh name must pass (rule#1127 no-digits etc.),
        /// so a manual Model Explorer / Properties-pane / Column Editor rename is caught, not just
        /// erwin's auto-uniquify (which <see cref="IsAutoUniquifyRename"/> already handles as a
        /// special case). This drives ONLY validation scope; the caller keeps a separate identity
        /// flag so a Required-popup Cancel reverts the name rather than deleting a pre-existing
        /// object. Not retroactive: false when the name is unchanged, so an untouched (even
        /// already-nonconforming) name is never re-flagged. <paramref name="isPlaceholder"/> lets
        /// the caller inject its object-kind placeholder test (column vs entity defaults differ).
        /// </summary>
        public static bool RenameRequiresRevalidation(string baselineName, string currentName, Func<string, bool> isPlaceholder)
        {
            if (string.IsNullOrEmpty(baselineName)) return false;
            if (isPlaceholder != null && isPlaceholder(baselineName)) return false;
            return !string.Equals(baselineName, currentName ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>
        /// Evaluate a single rule against an object name and append violations.
        /// Public so unit tests can exercise per-RuleType dispatch without
        /// having to load <see cref="NamingStandardService"/> from a real
        /// database. UDP conditioning is NOT applied here - caller is
        /// responsible for filtering on <see cref="NamingStandardRule.DependsOnUdpId"/>
        /// before invoking.
        /// </summary>
        public static List<NamingValidationResult> EvaluateRule(NamingStandardRule rule, string objectName)
        {
            var results = new List<NamingValidationResult>();
            if (rule == null) return results;
            objectName ??= "";
            EvaluateRule(rule, objectName, results);
            return results;
        }

        /// <summary>
        /// Apply naming standards (prefix/suffix) to an object name. Only rules
        /// with <see cref="NamingRuleKind.Prefix"/> or <see cref="NamingRuleKind.Suffix"/>
        /// participate; Required/Length/Regexp rules are validate-only and never
        /// transform a value (per atomic-rule contract 2026-05-16).
        /// <para>
        /// When autoOnly=true (default), only Prefix/Suffix rules whose
        /// <see cref="NamingStandardRule.AutoApply"/> flag is set perform the
        /// forward apply - used by the silent auto-apply path. When
        /// autoOnly=false, ALL applicable Prefix/Suffix rules are applied,
        /// regardless of AutoApply - used by the "ask user" path so we can
        /// compute what the name would look like with manual rules.
        /// </para>
        /// </summary>
        public static string ApplyNamingStandards(string objectType, string objectName, dynamic scapiObject = null, bool autoOnly = true, string propertyCode = DefaultPropertyCode, bool isNew = false)
        {
            if (string.IsNullOrEmpty(objectName)) return objectName;
            if (!NamingStandardService.Instance.IsLoaded) return objectName;

            // Important: do NOT filter the rule set up-front by AutoApply.
            // The autoOnly contract gates only the FORWARD apply branch
            // (adding a prefix/suffix). The reverse-strip branch must run
            // for every conditional rule regardless of AutoApply, so that
            // a now-stale decoration (e.g. user flipped TABLE_TYPE from
            // LOG to HISTORY, leaving an obsolete 'LOG_' prefix) is removed
            // silently as bookkeeping. Re-asking the user every time the
            // conditioning UDP changes was rejected as a UX regression
            // 2026-05-07.
            // Materialise once so we can iterate twice (strip-then-apply).
            var rules = NamingStandardService.Instance
                .GetByObjectTypeAndProperty(objectType, propertyCode)
                .Where(r => r != null
                            && (r.RuleType == NamingRuleKind.Prefix || r.RuleType == NamingRuleKind.Suffix)
                            && MatchesApplyOn(r, isNew))
                .ToList();

            // Evaluate applicability ONCE per rule (SCAPI condition reads are not
            // free) and reuse it across the strip + re-apply passes below.
            var applicable = new Dictionary<NamingStandardRule, bool>();
            foreach (var rule in rules)
            {
                bool a = IsRuleApplicable(rule, objectType, scapiObject);
                applicable[rule] = a;
                if (rule.Conditions != null && rule.Conditions.Count > 0)
                    AddinLogger.Log(
                        $"NamingApply: rule#{rule.Id} [{rule.RuleType}] {rule.ObjectType}.{rule.PropertyCode} " +
                        $"cond=[{rule.Conditions.Count} term(s)] -> applicable={a}");
            }

            string result = objectName;

            // Order-independent, idempotent affix apply (2026-07-01). The old design
            // added each applicable prefix behind a single StartsWith check, so with TWO
            // prefix rules on one property each pushed the other off the front and BOTH
            // re-added on every re-check (a rename by one rule re-fires the scoped check),
            // stacking without bound ('VpPFXC_VpPFXC_Abc...'). Instead: strip every
            // managed prefix/suffix that is either now-stale (rule no longer applies) OR
            // will be re-applied, down to the clean core, then re-apply the currently-
            // applicable affixes exactly once in SORT_ORDER. This supersedes the earlier
            // two-pass strip-then-apply (which fixed the double-SUFFIX case the same way,
            // but only stripped NON-applicable affixes so two front-prefixes still stacked).
            // Applicable-but-deferred (AUTO_APPLY=false in the auto pass) affixes are left
            // untouched - the forward pass will not re-add them, so stripping would drop one.
            bool WillApply(NamingStandardRule r) => applicable[r] && !(autoOnly && !r.AutoApply);

            // Affix matching is CASE-SENSITIVE (Ordinal). The affix 'Date' must match the TOKEN
            // 'Date', not the letters 'date' inside an ordinary word. Under the old case-
            // INsensitive matching, 'UpdateDate' with an applicable Suffix='Date' rule was
            // corrupted to 'UpDate': strip 'Date' -> 'Update', but 'Update' still "ends with
            // date" (ignore-case) so it was stripped AGAIN -> 'Up', and the re-apply appended
            // 'Date' -> 'UpDate'. With Ordinal, 'Update' does not end with 'Date', so a name that
            // already correctly ends in the affix round-trips and a plain word is never eaten.
            // (The affix tokens in real rules - 'Vp', 'PF', 'DM_', 'Date', '_LOG' - are authored
            // in a fixed case, so exact matching is also the truer test of "already applied".)
            //
            // Strip each rule's affix AT MOST ONCE as belt-and-suspenders: the while-loop only
            // exists to let a rule strip AFTER another rule's strip exposed it (two stacked
            // prefixes 'PFVpAbc' -> 'Abc'); a rule must never re-strip its OWN affix from what
            // remains.
            var stripped = new HashSet<NamingStandardRule>();

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var rule in rules)
                {
                    // A brand-new object has no naming history: no rule has ever applied to
                    // it, so a name that merely ends/starts with a NON-applicable rule's affix
                    // is user-typed text, not a stale rule decoration - never strip it. This was
                    // the 'AbcDate' -> 'Abc' false positive: a conditional Suffix='Date' rule
                    // that does not apply to this column stripped the user's meaningful 'Date'.
                    // The legitimate stale-strip (a conditioning UDP flipped, leaving an obsolete
                    // affix) only makes sense on an EXISTING object, where isNew is false, so it
                    // is preserved. Applicable rules still strip-then-reapply for idempotency.
                    if (!applicable[rule] && isNew) continue;

                    // Leave applicable-but-deferred affixes in place (see above).
                    if (applicable[rule] && !WillApply(rule)) continue;

                    // Already stripped this rule's affix once - do not re-strip its own affix
                    // from a coincidental match in the remaining core text.
                    if (stripped.Contains(rule)) continue;

                    if (rule.RuleType == NamingRuleKind.Prefix
                        && !string.IsNullOrEmpty(rule.Prefix)
                        && result.StartsWith(rule.Prefix, StringComparison.Ordinal))
                    {
                        if (!applicable[rule])
                            AddinLogger.Log($"NamingApply: rule#{rule.Id} stale Prefix='{rule.Prefix}' stripped from '{result}'");
                        result = result.Substring(rule.Prefix.Length);
                        stripped.Add(rule);
                        changed = true;
                    }
                    else if (rule.RuleType == NamingRuleKind.Suffix
                             && !string.IsNullOrEmpty(rule.Suffix)
                             && result.EndsWith(rule.Suffix, StringComparison.Ordinal))
                    {
                        if (!applicable[rule])
                            AddinLogger.Log($"NamingApply: rule#{rule.Id} stale Suffix='{rule.Suffix}' stripped from '{result}'");
                        result = result.Substring(0, result.Length - rule.Suffix.Length);
                        stripped.Add(rule);
                        changed = true;
                    }
                }
            }

            // Re-apply currently-applicable affixes once, in rule (SORT_ORDER) order:
            // prefixes prepend so the later rule ends up outermost (preserves the prior
            // stacking order); suffixes append.
            foreach (var rule in rules)
            {
                if (!WillApply(rule)) continue;

                if (rule.RuleType == NamingRuleKind.Prefix
                    && !string.IsNullOrEmpty(rule.Prefix)
                    && !result.StartsWith(rule.Prefix, StringComparison.Ordinal))
                {
                    result = rule.Prefix + result;
                }
                else if (rule.RuleType == NamingRuleKind.Suffix
                         && !string.IsNullOrEmpty(rule.Suffix)
                         && !result.EndsWith(rule.Suffix, StringComparison.Ordinal))
                {
                    result = result + rule.Suffix;
                }
            }

            if (!string.Equals(result, objectName, StringComparison.Ordinal))
                AddinLogger.Log($"NamingApply: affixes '{objectName}' -> '{result}'");

            return result;
        }

        /// <summary>
        /// Check if applying naming standards would change the given name.
        /// autoOnly=true → only AUTO_APPLY rules; autoOnly=false → all rules.
        /// </summary>
        public static bool HasAutoApplyChanges(string objectType, string objectName, dynamic scapiObject = null, bool autoOnly = true, string propertyCode = DefaultPropertyCode, bool isNew = false)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (!NamingStandardService.Instance.IsLoaded) return false;

            string applied = ApplyNamingStandards(objectType, objectName, scapiObject, autoOnly, propertyCode, isNew);
            return !string.Equals(applied, objectName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Map a read-side SCAPI accessor (admin's <c>PROPERTY_CODE</c>) to
        /// the corresponding write-side accessor when the two differ.
        /// <para>
        /// Reason this exists: <c>Name_Qualifier</c> is a read-only derived
        /// projection of an entity's <c>Schema_Ref</c> in erwin's metamodel
        /// (verified 2026-05-16; meta-sync also documents this in
        /// <c>docs/MetaSync-Technical-Internal-EN.md:280-289</c>). A naming
        /// rule on Table.Owner reads <c>Name_Qualifier</c> just fine on
        /// schema-bound entities, but a SCAPI write to
        /// <c>Name_Qualifier</c> is silently dropped - the property bag
        /// accepts the assignment, the transaction commits, but the model
        /// state does not change. To actually set the owner the addin must
        /// write to <c>Schema_Ref</c>; erwin then materialises (or reuses)
        /// the matching Schema object and the derived <c>Name_Qualifier</c>
        /// reflects the new value on the next read.
        /// </para>
        /// <para>
        /// For every other accessor in the addin's current ruleset
        /// (Physical_Name, Definition, Comment, Physical_Data_Type, ...)
        /// the read and write codes are identical, so the default is
        /// pass-through.
        /// </para>
        /// </summary>
        public static string WriteAccessorFor(string readAccessor)
        {
            if (string.IsNullOrEmpty(readAccessor)) return readAccessor;
            if (string.Equals(readAccessor, "Name_Qualifier", StringComparison.OrdinalIgnoreCase))
                return "Schema_Ref";
            return readAccessor;
        }

        /// <summary>
        /// Human-friendly label for a naming rule's PROPERTY_CODE, used in the
        /// Required-field dialog so the user sees "Comment"/"Owner"/"Name" instead
        /// of the raw SCAPI accessor ("Definition"/"Name_Qualifier"/"Physical_Name").
        /// Unknown codes pass through unchanged. (2026-06-06)
        /// </summary>
        public static string FriendlyPropertyLabel(string propertyCode)
        {
            if (string.IsNullOrEmpty(propertyCode)) return "";
            switch (propertyCode)
            {
                case "Definition":         return "Comment";
                case "Name_Qualifier":     return "Owner";
                case "Physical_Name":      return "Name";
                case "Logical_Name":       return "Logical Name";
                case "Physical_Data_Type": return "Data Type";
                default:                   return propertyCode;
            }
        }

        #region Private Helpers

        /// <summary>
        /// Check whether a rule's polymorphic condition matches the live
        /// state of the SCAPI object. The condition source is one of:
        /// <list type="bullet">
        /// <item><description>Neither <see cref="NamingStandardRule.DependsOnUdpId"/>
        /// nor <see cref="NamingStandardRule.DependsOnPropertyDefId"/> set →
        /// unconditional rule, always applicable.</description></item>
        /// <item><description><see cref="NamingStandardRule.DependsOnUdpId"/> set →
        /// read the UDP value through
        /// <c>"&lt;OwnerClass&gt;.Physical.&lt;UdpName&gt;"</c>.</description></item>
        /// <item><description><see cref="NamingStandardRule.DependsOnPropertyDefId"/>
        /// set → read the erwin built-in property directly via its SCAPI
        /// accessor name (<see cref="NamingStandardRule.DependsOnPropertyCode"/>).</description></item>
        /// </list>
        /// The CSV in <see cref="NamingStandardRule.DependsOnPropertyValues"/>
        /// is matched case-insensitively (single-value CSV = back-compat
        /// path; empty CSV with a source set = "any non-empty value matches").
        /// </summary>
        // Condition property codes that mean "is this column a member of the
        // primary key". erwin does NOT surface PK membership as a readable
        // Attribute property (it lives in the Key_Group / Key_Group_Member graph,
        // reached via IsAttributeInPrimaryKey), so a DEPENDS_ON targeting one of
        // these reads empty from a property accessor. The caller resolves PK
        // membership from the Key_Group graph and passes it as the pkMembership
        // argument of the IsRuleApplicable overload below.
        private static readonly HashSet<string> PkMembershipConditionCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "IsPrimaryKey", "Is_PK", "Primary_Key", "PrimaryKey", "Is_Primary_Key" };

        /// <summary>
        /// True when a single condition TERM asks whether the object is a primary-key
        /// member (a property-source term whose code is in
        /// <see cref="PkMembershipConditionCodes"/>). erwin exposes no Attribute
        /// property for it, so such a term is evaluated against a caller-resolved
        /// boolean (the Key_Group_Member walk) rather than a property read.
        /// </summary>
        public static bool IsPkMembershipCondition(NamingRuleCondition c)
        {
            if (c == null) return false;
            bool hasUdpSource = c.DependsOnUdpId.HasValue && !string.IsNullOrEmpty(c.DependsOnUdpName);
            bool hasPropSource = c.DependsOnPropertyDefId.HasValue && !string.IsNullOrEmpty(c.DependsOnPropertyCode);
            return hasPropSource && !hasUdpSource && PkMembershipConditionCodes.Contains(c.DependsOnPropertyCode);
        }

        /// <summary>
        /// True when ANY of the rule's condition terms is a PK-membership check, so the
        /// caller knows it must resolve PK membership and pass it to
        /// <see cref="IsRuleApplicable(NamingStandardRule, string, object, bool?)"/>.
        /// </summary>
        public static bool IsPkMembershipCondition(NamingStandardRule rule)
        {
            if (rule?.Conditions == null) return false;
            foreach (var c in rule.Conditions)
                if (IsPkMembershipCondition(c)) return true;
            return false;
        }

        // Public so the Template runtime applier
        // (ValidationCoordinatorService.ApplyColumnTemplateRules) reuses the
        // exact same DEPENDS_ON condition evaluation as the validate-only path,
        // rather than reimplementing it.
        public static bool IsRuleApplicable(NamingStandardRule rule, string objectType, dynamic scapiObject)
            => IsRuleApplicable(rule, objectType, scapiObject, null);

        /// <summary>
        /// Evaluates the rule's ordered DEPENDS_ON condition list
        /// (<c>MC_NAMING_RULE_CONDITION</c>), folded strictly LEFT-TO-RIGHT with no
        /// precedence and no parentheses: <c>result = term0; result = result AND/OR
        /// termN</c> per each later term's CONNECTOR. An EMPTY list means the rule is
        /// unconditional (always applies). Each term matches exactly as the legacy
        /// single condition did (a UDP or erwin built-in property value IN the CSV,
        /// case-insensitive). When a term is a PK-membership check and
        /// <paramref name="pkMembership"/> has a value, that term uses the caller-
        /// resolved boolean (Key_Group_Member walk) instead of a property read.
        /// </summary>
        public static bool IsRuleApplicable(NamingStandardRule rule, string objectType, dynamic scapiObject, bool? pkMembership)
            => AreConditionsSatisfied(rule?.Conditions, objectType, scapiObject, pkMembership);

        /// <summary>
        /// Evaluate an ordered DEPENDS_ON condition list - the shared applicability
        /// engine for EVERY feature that carries a MC_*_CONDITION child table
        /// (<c>MC_NAMING_RULE_CONDITION</c> for naming rules,
        /// <c>MC_PREDEFINED_COLUMN_CONDITION</c> for predefined columns). The list is
        /// folded strictly LEFT-TO-RIGHT with no precedence and no parentheses:
        /// <c>result = term0; result = result AND/OR termN</c> per each later term's
        /// CONNECTOR. An EMPTY/null list means unconditional (always true). Each term
        /// matches exactly as the legacy single condition did (a UDP or erwin built-in
        /// property value IN the CSV, case-insensitive). <paramref name="objectType"/>
        /// selects the UDP owner class ("Table" -> <c>Entity.Physical.X</c>). Callers
        /// MUST reuse this rather than reimplement the fold, so naming and predefined
        /// stay bit-for-bit identical (WP#280).
        /// </summary>
        public static bool AreConditionsSatisfied(
            IReadOnlyList<NamingRuleCondition> conditions, string objectType, dynamic scapiObject, bool? pkMembership = null)
        {
            if (conditions == null || conditions.Count == 0)
                return true; // unconditional

            bool result = MatchSingleCondition(conditions[0], objectType, scapiObject, pkMembership);
            for (int i = 1; i < conditions.Count; i++)
            {
                var c = conditions[i];
                bool isOr = string.Equals(c.Connector, "OR", StringComparison.OrdinalIgnoreCase);
                // Short-circuit: an OR over an already-true result, or an AND over an
                // already-false result, cannot change it - skip the (COM-touching) match.
                if (isOr == result) continue;
                result = MatchSingleCondition(c, objectType, scapiObject, pkMembership);
            }
            return result;
        }

        /// <summary>
        /// Evaluates one DEPENDS_ON term: PK-membership via the caller-resolved boolean
        /// when applicable, else the UDP / erwin built-in property value IN the CSV
        /// (case-insensitive). A term with no live object to read is treated as not
        /// matching (out-of-band callers, e.g. a static unit-test name check).
        /// </summary>
        private static bool MatchSingleCondition(NamingRuleCondition c, string objectType, dynamic scapiObject, bool? pkMembership)
        {
            bool hasUdpSource = c.DependsOnUdpId.HasValue && !string.IsNullOrEmpty(c.DependsOnUdpName);
            bool hasPropSource = c.DependsOnPropertyDefId.HasValue && !string.IsNullOrEmpty(c.DependsOnPropertyCode);

            // A term that names NO source at all is dropped by both loaders (the XOR-skip),
            // so this is unreachable in practice; treat it as vacuously satisfied (true),
            // the neutral element for AND.
            if (!c.DependsOnUdpId.HasValue && !c.DependsOnPropertyDefId.HasValue)
                return true;

            // A term that DOES name a source (a UDP / property FK) but whose resolved
            // name/code came back empty is a DANGLING reference - the gating UDP or
            // property was deleted while the condition row still points at it (the loader's
            // LEFT JOIN then yields a NULL name). The gate targets a source that no longer
            // exists, so it can never hold: the term does NOT match. Returning the old
            // vacuous-true here would flip a single-term column to "applies to EVERY object"
            // - the pre-WP#280 predefined path guarded exactly this with an empty-name skip,
            // so this keeps predefined bit-for-bit AND hardens naming against the same
            // fail-open (both share this method).
            if (!hasUdpSource && !hasPropSource)
                return false;

            if (pkMembership.HasValue && IsPkMembershipCondition(c))
                return MatchesCsv(pkMembership.Value ? "True" : "False", c.DependsOnPropertyValues);

            if (scapiObject == null)
                return false;

            string sourceValue = hasUdpSource
                ? ReadUdpValue(scapiObject, objectType, c.DependsOnUdpName)
                : ReadConditionPropertyValue(scapiObject, objectType, c);

            return MatchesCsv(sourceValue, c.DependsOnPropertyValues);
        }

        /// <summary>
        /// Read a built-in-property condition value, honouring the condition property's
        /// OWNING object type. When it matches the rule's target object (the usual case)
        /// the property is read directly. When it names a RELATED object type it is
        /// resolved from the target: erwin surfaces a table/view's owning SCHEMA name on
        /// the object itself as <c>Name_Qualifier</c> (the derived projection of
        /// Schema_Ref), so a SCHEMA.Name condition reads the target's Name_Qualifier.
        /// Any other related-object condition is not yet supported and reads empty (the
        /// term simply will not match) - logged once so it is diagnosable.
        /// </summary>
        private static string ReadConditionPropertyValue(dynamic scapiObject, string ruleObjectType, NamingRuleCondition c)
        {
            string condOt = c.DependsOnPropertyObjectType;

            // No owner type, or the property is on the rule's own target object -> direct.
            if (string.IsNullOrEmpty(condOt) || IsSameObjectType(condOt, ruleObjectType))
                return ReadBuiltinPropertyValue(scapiObject, c.DependsOnPropertyCode);

            // Related object type: SCHEMA (the table's owner). Its Name is projected onto
            // the table/view as Name_Qualifier.
            if (string.Equals(condOt.Trim(), "SCHEMA", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.DependsOnPropertyCode, "Name", StringComparison.OrdinalIgnoreCase))
                return ReadBuiltinPropertyValue(scapiObject, "Name_Qualifier");

            AddinLogger.Log($"NamingApply: condition on related object '{condOt}'.{c.DependsOnPropertyCode} not supported for target '{ruleObjectType}' - treated as empty");
            return "";
        }

        /// <summary>True when a condition property's owning object-type name (e.g. "TABLE"
        /// from MC_OBJECT_TYPE) denotes the same object as the rule's runtime target
        /// string (e.g. "Table"). Case- and space/underscore-insensitive.</summary>
        private static bool IsSameObjectType(string objectTypeName, string ruleObjectType)
        {
            string a = objectTypeName?.Trim().ToUpperInvariant().Replace(' ', '_');
            string b = ruleObjectType?.Trim().ToUpperInvariant().Replace(' ', '_');
            return !string.IsNullOrEmpty(a) && a == b;
        }

        /// <summary>
        /// Diagnostic companion to <see cref="IsRuleApplicable"/>: a short human-readable
        /// trace of the multi-term DEPENDS_ON evaluation (each term's live value vs its
        /// allowed CSV, the AND/OR connectors, and the folded result), so a never-firing
        /// conditional rule is debuggable instead of silently skipped. No SCAPI writes.
        /// </summary>
        public static string DescribeApplicability(NamingStandardRule rule, string objectType, dynamic scapiObject, bool? pkMembership = null)
        {
            var conditions = rule?.Conditions;
            if (conditions == null || conditions.Count == 0) return "unconditional";

            // Diagnostic only - never let a describe failure escape into the caller's log.
            try
            {
                var sb = new System.Text.StringBuilder();
                bool result = false;
                for (int i = 0; i < conditions.Count; i++)
                {
                    var c = conditions[i];
                    bool m = MatchSingleCondition(c, objectType, scapiObject, pkMembership);
                    if (i == 0)
                    {
                        result = m;
                    }
                    else
                    {
                        bool isOr = string.Equals(c.Connector, "OR", StringComparison.OrdinalIgnoreCase);
                        result = isOr ? (result || m) : (result && m);
                        sb.Append(isOr ? " OR " : " AND ");
                    }
                    sb.Append(DescribeTerm(c, objectType, scapiObject, pkMembership, m));
                }
                sb.Append(" => ").Append(result ? "applies" : "skip");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"<describe-error: {ex.Message}>";
            }
        }

        private static string DescribeTerm(NamingRuleCondition c, string objectType, dynamic scapiObject, bool? pkMembership, bool matched)
        {
            bool hasUdpSource = c.DependsOnUdpId.HasValue && !string.IsNullOrEmpty(c.DependsOnUdpName);
            bool hasPropSource = c.DependsOnPropertyDefId.HasValue && !string.IsNullOrEmpty(c.DependsOnPropertyCode);
            if (!hasUdpSource && !hasPropSource) return $"[empty-term]={matched}";

            if (pkMembership.HasValue && IsPkMembershipCondition(c))
                return $"pk-membership={pkMembership.Value}={matched}";

            string kind = hasUdpSource ? "udp" : "prop";
            // Qualify a related-object property in the trace, e.g. prop[SCHEMA.Name].
            string name = hasUdpSource
                ? c.DependsOnUdpName
                : (!string.IsNullOrEmpty(c.DependsOnPropertyObjectType) && !IsSameObjectType(c.DependsOnPropertyObjectType, objectType)
                    ? $"{c.DependsOnPropertyObjectType}.{c.DependsOnPropertyCode}"
                    : c.DependsOnPropertyCode);
            string val;
            if (scapiObject == null)
            {
                val = "<no-object>";
            }
            else
            {
                try
                {
                    val = hasUdpSource
                        ? ReadUdpValue(scapiObject, objectType, c.DependsOnUdpName)
                        : ReadConditionPropertyValue(scapiObject, objectType, c);
                }
                catch (Exception ex) { val = "<read-error: " + ex.Message + ">"; }
            }
            string hint = string.IsNullOrEmpty(val) ? "(empty?)" : "";
            return $"{kind}[{name}]='{val}'in[{c.DependsOnPropertyValues}]{hint}={matched}";
        }

        /// <summary>
        /// Case-insensitive IN-match against a CSV list of allowed values.
        /// Empty/whitespace CSV with a non-empty source value passes
        /// ("any non-empty value matches"). Both empty → no match.
        /// Public for unit-test coverage of the C3 condition semantics.
        /// </summary>
        public static bool MatchesCsv(string sourceValue, string csv)
        {
            sourceValue = sourceValue ?? "";
            csv = csv ?? "";

            if (string.IsNullOrWhiteSpace(csv))
                return !string.IsNullOrEmpty(sourceValue);

            foreach (var raw in csv.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                if (string.Equals(token, sourceValue, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Optional accessor to the active model's SCAPI root object, set by the
        /// monitoring layer (<c>ValidationCoordinatorService</c>) at connect. Used to
        /// resolve a condition UDP that is MODEL-scoped rather than owned by the rule's
        /// target object type - e.g. an "Application" model UDP gating a TABLE rule
        /// model-wide. Null when no model is being monitored; model-scoped UDP
        /// conditions then read as empty (the same not-applicable outcome as before).
        /// </summary>
        public static Func<dynamic> ModelRootProvider { get; set; }

        /// <summary>
        /// Read a UDP value from an erwin SCAPI object. UDP path on r10 is
        /// <c>&lt;OwnerClass&gt;.Physical.&lt;UdpName&gt;</c>; SCAPI throws on
        /// unknown owner class so the switch hard-codes the small set the
        /// addin actually validates today.
        /// <para>If the UDP is not owned by the rule's target object type - SCAPI
        /// reports "not a valid class id or class name for object or property" - the
        /// UDP may be MODEL-scoped (it lives on the model, not the entity/column).
        /// The same UDP name can be a table UDP in one model and a model UDP in
        /// another, so we resolve against the LIVE model: on that specific error we
        /// re-read from the model root (<c>Model.Physical.&lt;UdpName&gt;</c>). This
        /// lets a model-level condition (e.g. the model's Application) gate a table
        /// rule model-wide.</para>
        /// </summary>
        /// <summary>
        /// Public wrapper over <see cref="ReadUdpValue"/> for the Template
        /// applier's <c>{Udp:Name}</c> token source (migration 9). Reuses the
        /// exact condition-read semantics on purpose: owner class derived from
        /// the rule's object type, and the <c>Model.Physical</c> fallback so a
        /// column template can read a MODEL-scoped UDP (most admin UDPs are
        /// model-scoped, e.g. ApplicationCode). Returns "" when the UDP is
        /// absent; the renderer's empty-token check turns that into a
        /// <see cref="TemplateResolutionException"/> (never a silent blank).
        /// </summary>
        public static string ReadUdpValueForRule(dynamic scapiObject, string objectType, string udpName)
            => ReadUdpValue(scapiObject, objectType, udpName);

        private static string ReadUdpValue(dynamic scapiObject, string objectType, string udpName)
        {
            string ownerClass = objectType?.ToLower() switch
            {
                "table" => "Entity",
                "column" => "Attribute",
                "view" => "View",
                "index" => "Key_Group",
                "primary key" => "Key_Group",
                _ => "Entity"
            };
            string path = $"{ownerClass}.Physical.{udpName}";
            try
            {
                string value = scapiObject.Properties(path)?.Value?.ToString() ?? "";
                // 2026-05-24: per-read success log dropped. Rule applicability
                // outcome ("rule#N applicable=True/False") is already logged
                // at the calling site and is the useful signal; logging every
                // single read here added ~1200 lines per validation pass.
                return value;
            }
            catch (Exception ex)
            {
                // The UDP is not on the rule's target object class -> it may be a
                // MODEL-scoped UDP. Resolve from the model root (returns null only
                // when we could not even attempt it; "" when attempted-but-absent).
                if (IsNotOnThisClass(ex))
                {
                    string fromModel = TryReadModelUdp(udpName);
                    if (fromModel != null) return fromModel;
                }
                // Keep the error path - sparse-storage / typo / missing UDP
                // diagnostics are valuable.
                AddinLogger.Log($"NamingApply.ReadUdpValue: '{path}' threw {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        /// <summary>True when a SCAPI property read failed because the property is not
        /// defined on the object's class (vs. a transient/other COM error). This is the
        /// signal that the UDP belongs to a different object type (e.g. the model).</summary>
        private static bool IsNotOnThisClass(Exception ex)
            => ex?.Message != null
               && ex.Message.IndexOf("not valid class", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Re-read a UDP from the MODEL root via <see cref="ModelRootProvider"/>.
        /// Returns null when no model root is available (caller logs the original
        /// entity-class error); "" when the model was read but does not carry the UDP
        /// either (genuine typo / absent), so the IN-match short-circuits to not-applicable.
        /// </summary>
        private static string TryReadModelUdp(string udpName)
        {
            var provider = ModelRootProvider;
            if (provider == null) return null;
            dynamic root;
            try { root = provider(); }
            catch { return null; }
            if (root == null) return null;
            try
            {
                return root.Properties($"Model.Physical.{udpName}")?.Value?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                AddinLogger.Log($"NamingApply.ReadUdpValue: model-scope 'Model.Physical.{udpName}' threw {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Read an erwin built-in property value via direct SCAPI access.
        /// Built-in accessors do NOT use the <c>"&lt;Owner&gt;.Physical.X"</c>
        /// path - they are plain <c>scapiObject.Properties("Physical_Data_Type").Value</c>
        /// style calls. SCAPI rejection (e.g. property not surfaced on this
        /// entity instance, the same pattern as Name_Qualifier on a brand-new
        /// table) is treated as empty so the IN-match short-circuits and the
        /// rule skips - the entity simply hasn't reached the state the
        /// condition targets yet.
        /// </summary>
        private static string ReadBuiltinPropertyValue(dynamic scapiObject, string propertyCode)
        {
            try
            {
                return scapiObject.Properties(propertyCode)?.Value?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Evaluate a single atomic rule (one <see cref="NamingRuleKind"/>)
        /// against an object name. Spec (2026-05-17 admin update):
        /// <list type="number">
        /// <item><description>
        /// <b>Empty / IS_REQUIRED gate.</b> If the value is null/whitespace
        /// and <see cref="NamingStandardRule.IsRequired"/> is true, emit one
        /// violation using the rule's <see cref="NamingStandardRule.ErrorMessage"/>
        /// and stop. If it is empty and IS_REQUIRED is false, skip the rule
        /// entirely - empty values do not get pattern-checked unless the
        /// admin opted in via the flag.
        /// </description></item>
        /// <item><description>
        /// <b>Pattern check.</b> For non-empty values, dispatch on
        /// <see cref="NamingStandardRule.RuleType"/> and emit a violation
        /// if the kind-specific check fails. Misconfigured rows (e.g. a
        /// Length rule with NULL <c>LENGTH_VALUE</c>, a Regexp rule with
        /// empty <c>REGEXP_PATTERN</c>) are silently skipped per the admin
        /// contract; admin's <c>NormalizeByRuleType</c> + <c>ValidateByRuleType</c>
        /// already filter most of these out at save time.
        /// </description></item>
        /// </list>
        /// AutoApply is handled by <see cref="ApplyNamingStandards"/>, not
        /// here - this method is the validate-only branch.
        /// </summary>
        private static void EvaluateRule(NamingStandardRule rule, string objectName, List<NamingValidationResult> results)
        {
            // Step 1: empty/IS_REQUIRED gate. ERROR_MESSAGE is the single
            // message field shared by the empty case and the pattern case
            // (spec 2026-05-17). Post-2026-05-25 a rule can ALSO carry
            // RULE_TYPE='Required' as a first-class kind; that implies
            // "must be non-empty" regardless of the IS_REQUIRED flag.
            bool treatAsRequired = rule.IsRequired || rule.RuleType == NamingRuleKind.Required;
            if (string.IsNullOrWhiteSpace(objectName))
            {
                if (treatAsRequired)
                {
                    results.Add(NamingValidationResult.Invalid("Required",
                        !string.IsNullOrEmpty(rule.ErrorMessage)
                            ? rule.ErrorMessage
                            : "Value is required", rule));
                    return;
                }

                // Empty + not required: Prefix / Suffix / Regexp do not
                // usefully match against an empty value (a Prefix rule
                // saying "must start with 'Vp'" on an empty field would
                // emit a useless violation the user can never satisfy
                // without filling the field, which is by definition not
                // required). Length is the exception: "len > 10" on an
                // empty value is `0 > 10 = false`, a real and actionable
                // violation - the admin is saying "if this field is
                // *expected* to have content (even though optional), it
                // must be at least N characters". User reported 2026-05-31
                // that rule#1022 (TABLE.Definition, len > 10, req=False)
                // failed to warn on an empty Comment - the desired
                // semantic. Fall through to the pattern check ONLY for
                // Length; the other kinds still short-circuit.
                if (rule.RuleType != NamingRuleKind.Length)
                    return;
            }

            // Step 3: pattern check (Step 2 - AutoApply - lives in ApplyNamingStandards).
            switch (rule.RuleType)
            {
                case NamingRuleKind.Prefix:
                    if (string.IsNullOrEmpty(rule.Prefix))
                    {
                        // Misconfigured: admin authored a Prefix rule with no
                        // PREFIX value. Skip rather than emit a meaningless
                        // "must start with ''" violation.
                        break;
                    }
                    if (!objectName.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(NamingValidationResult.Invalid("Prefix",
                            !string.IsNullOrEmpty(rule.ErrorMessage)
                                ? rule.ErrorMessage
                                : $"Name must start with '{rule.Prefix}'", rule));
                    }
                    break;

                case NamingRuleKind.Suffix:
                    if (string.IsNullOrEmpty(rule.Suffix))
                        break;
                    if (!objectName.EndsWith(rule.Suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(NamingValidationResult.Invalid("Suffix",
                            !string.IsNullOrEmpty(rule.ErrorMessage)
                                ? rule.ErrorMessage
                                : $"Name must end with '{rule.Suffix}'", rule));
                    }
                    break;

                case NamingRuleKind.Length:
                    if (string.IsNullOrEmpty(rule.LengthOperator) || !rule.LengthValue.HasValue)
                        break;
                    bool lengthOk = rule.LengthOperator switch
                    {
                        ">=" => objectName.Length >= rule.LengthValue.Value,
                        "<=" => objectName.Length <= rule.LengthValue.Value,
                        ">" => objectName.Length > rule.LengthValue.Value,
                        "<" => objectName.Length < rule.LengthValue.Value,
                        "=" => objectName.Length == rule.LengthValue.Value,
                        _ => true,
                    };
                    if (!lengthOk)
                    {
                        results.Add(NamingValidationResult.Invalid("Length",
                            !string.IsNullOrEmpty(rule.ErrorMessage)
                                ? rule.ErrorMessage
                                : $"Name length must be {rule.LengthOperator} {rule.LengthValue}", rule));
                    }
                    break;

                case NamingRuleKind.Required:
                    // Pure Required rule. The non-empty check is done by
                    // Step 1 above; once we reach here the value is
                    // already non-empty, so this rule has nothing else
                    // to enforce. Length / Regexp / Prefix / Suffix
                    // siblings on the same property continue to run as
                    // separate rules.
                    break;

                case NamingRuleKind.Regexp:
                    if (string.IsNullOrEmpty(rule.RegexpPattern))
                        break;
                    try
                    {
                        if (!Regex.IsMatch(objectName, rule.RegexpPattern))
                        {
                            // Diagnostic: surface the exact stored pattern +
                            // the tested name so a future "regex looks right
                            // but rejects every name" bug can be triaged from
                            // the file log.
                            System.Diagnostics.Debug.WriteLine(
                                $"NamingValidation: regex fail rule#{rule.Id} pattern(len={rule.RegexpPattern.Length})='{rule.RegexpPattern}' name(len={objectName.Length})='{objectName}'");

                            results.Add(NamingValidationResult.Invalid("Regexp",
                                !string.IsNullOrEmpty(rule.ErrorMessage)
                                    ? rule.ErrorMessage
                                    : $"Name does not match pattern '{rule.RegexpPattern}'", rule));
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"NamingValidation: Invalid regex rule#{rule.Id} '{rule.RegexpPattern}': {ex.Message}");
                    }
                    break;

                case NamingRuleKind.Template:
                    // Template is a GENERATOR, not a validator: it produces a
                    // target property value and writes it via its own runtime
                    // applier (ValidationCoordinatorService.ApplyColumnTemplateRules).
                    // It must never emit a name/value violation here, so the
                    // validate-only path treats it as a no-op. This case exists
                    // so a Template rule that lands here (e.g. via the Step 3b
                    // per-property sweep) is silently ignored instead of hitting
                    // the unknown-kind warning below.
                    break;

                default:
                    // Unknown enum value - skip defensively. The loader's
                    // Enum.TryParse already filters unparseable RULE_TYPE rows.
                    System.Diagnostics.Debug.WriteLine(
                        $"NamingValidation: rule#{rule.Id} has unhandled RuleType={rule.RuleType}, skipping");
                    break;
            }
        }

        #endregion
    }
}
