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

            string result = objectName;

            // Two-pass design (2026-05-24): a single pass that interleaved
            // reverse-strip with forward-apply ordered the operations by
            // SORT_ORDER, so a now-applicable rule (TableClass switched to
            // Log -> rule#20 adds '_LOG') ran BEFORE a now-stale rule
            // (rule#21 _HISTORY strip), leaving the entity with both
            // suffixes ("VpE_281_HISTORY_LOG" - verified 2026-05-24). Pass
            // 1 strips every stale conditional decoration first, pass 2
            // adds the currently-applicable ones to the clean baseline.
            // Per-pass evaluations of IsRuleApplicable are independent (same
            // SCAPI state for both), so the two-pass scan does not flip a
            // decision mid-iteration.

            // Pass 1: reverse-strip for non-applicable conditional rules.
            foreach (var rule in rules)
            {
                bool isConditional = rule.DependsOnUdpId.HasValue && !string.IsNullOrEmpty(rule.DependsOnUdpName);
                if (!isConditional) continue;

                bool applicable = IsRuleApplicable(rule, objectType, scapiObject);
                AddinLogger.Log(
                    $"NamingApply: rule#{rule.Id} [{rule.RuleType}] {rule.ObjectType}.{rule.PropertyCode} " +
                    $"cond=udp[{rule.DependsOnUdpName}] in [{rule.DependsOnPropertyValues ?? ""}] -> applicable={applicable}");

                if (applicable) continue; // forward-apply pass handles it below

                if (rule.RuleType == NamingRuleKind.Prefix
                    && !string.IsNullOrEmpty(rule.Prefix)
                    && result.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    AddinLogger.Log($"NamingApply: rule#{rule.Id} stale Prefix='{rule.Prefix}' stripped from '{result}'");
                    result = result.Substring(rule.Prefix.Length);
                }
                else if (rule.RuleType == NamingRuleKind.Suffix
                         && !string.IsNullOrEmpty(rule.Suffix)
                         && result.EndsWith(rule.Suffix, StringComparison.OrdinalIgnoreCase))
                {
                    AddinLogger.Log($"NamingApply: rule#{rule.Id} stale Suffix='{rule.Suffix}' stripped from '{result}'");
                    result = result.Substring(0, result.Length - rule.Suffix.Length);
                }
            }

            // Pass 2: forward-apply for applicable rules (conditional and
            // unconditional). Honour autoOnly here so AUTO_APPLY=false rules
            // are deferred to the ask-user path.
            foreach (var rule in rules)
            {
                bool applicable = IsRuleApplicable(rule, objectType, scapiObject);
                if (!applicable) continue;
                if (autoOnly && !rule.AutoApply) continue;

                if (rule.RuleType == NamingRuleKind.Prefix
                    && !string.IsNullOrEmpty(rule.Prefix)
                    && !result.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    AddinLogger.Log($"NamingApply: rule#{rule.Id} Prefix='{rule.Prefix}' applied to '{result}'");
                    result = rule.Prefix + result;
                }
                else if (rule.RuleType == NamingRuleKind.Suffix
                         && !string.IsNullOrEmpty(rule.Suffix)
                         && !result.EndsWith(rule.Suffix, StringComparison.OrdinalIgnoreCase))
                {
                    AddinLogger.Log($"NamingApply: rule#{rule.Id} Suffix='{rule.Suffix}' applied to '{result}'");
                    result = result + rule.Suffix;
                }
            }

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
        private static bool IsRuleApplicable(NamingStandardRule rule, string objectType, dynamic scapiObject)
        {
            bool hasUdpSource = rule.DependsOnUdpId.HasValue && !string.IsNullOrEmpty(rule.DependsOnUdpName);
            bool hasPropSource = rule.DependsOnPropertyDefId.HasValue && !string.IsNullOrEmpty(rule.DependsOnPropertyCode);

            // Unconditional rule.
            if (!hasUdpSource && !hasPropSource)
                return true;

            // Has condition but no SCAPI object to evaluate against → skip
            // (the caller is invoking us out of band, e.g. a static name
            // check from a unit test or a Glossary-bound path that does
            // not carry a live entity ref).
            if (scapiObject == null)
                return false;

            string sourceValue = hasUdpSource
                ? ReadUdpValue(scapiObject, objectType, rule.DependsOnUdpName)
                : ReadBuiltinPropertyValue(scapiObject, rule.DependsOnPropertyCode);

            return MatchesCsv(sourceValue, rule.DependsOnPropertyValues);
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
        /// Read a UDP value from an erwin SCAPI object. UDP path on r10 is
        /// <c>&lt;OwnerClass&gt;.Physical.&lt;UdpName&gt;</c>; SCAPI throws on
        /// unknown owner class so the switch hard-codes the small set the
        /// addin actually validates today.
        /// </summary>
        private static string ReadUdpValue(dynamic scapiObject, string objectType, string udpName)
        {
            string ownerClass = objectType?.ToLower() switch
            {
                "table" => "Entity",
                "column" => "Attribute",
                "view" => "View",
                "index" => "Key_Group",
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
                // Keep the error path - sparse-storage / typo / missing UDP
                // diagnostics are valuable.
                AddinLogger.Log($"NamingApply.ReadUdpValue: '{path}' threw {ex.GetType().Name}: {ex.Message}");
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
