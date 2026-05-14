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
        public static List<NamingValidationResult> ValidateObjectName(string objectType, string objectName, dynamic scapiObject = null, string propertyCode = DefaultPropertyCode)
        {
            var results = new List<NamingValidationResult>();

            if (string.IsNullOrEmpty(objectName)) return results;
            if (!NamingStandardService.Instance.IsLoaded) return results;

            var rules = NamingStandardService.Instance.GetByObjectTypeAndProperty(objectType, propertyCode);

            foreach (var rule in rules)
            {
                // UDP condition check: skip rule if condition doesn't match
                if (!IsRuleApplicable(rule, objectType, scapiObject))
                    continue;

                ValidateRule(rule, objectName, results);
            }

            return results;
        }

        /// <summary>
        /// Apply naming standards (prefix/suffix) to an object name.
        /// When autoOnly=true (default), only rules with AutoApply=true are applied — used by the
        /// silent auto-apply path. When autoOnly=false, ALL applicable rules are applied — used by
        /// the "ask user" path so we can compute what the name would look like with manual rules.
        /// </summary>
        public static string ApplyNamingStandards(string objectType, string objectName, dynamic scapiObject = null, bool autoOnly = true, string propertyCode = DefaultPropertyCode)
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
            IEnumerable<NamingStandardRule> rules = NamingStandardService.Instance.GetByObjectTypeAndProperty(objectType, propertyCode);

            string result = objectName;

            foreach (var rule in rules)
            {
                bool applicable = IsRuleApplicable(rule, objectType, scapiObject);
                bool isConditional = rule.DependsOnUdpId.HasValue && !string.IsNullOrEmpty(rule.DependsOnUdpName);

                if (applicable)
                {
                    // Forward apply: ADD prefix/suffix. Honour autoOnly so
                    // AUTO_APPLY=false rules are deferred to the ask-user
                    // path (the caller invokes us a second time with
                    // autoOnly=false and prompts on diff).
                    if (autoOnly && !rule.AutoApply) continue;

                    if (!string.IsNullOrEmpty(rule.Prefix) &&
                        !result.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result = rule.Prefix + result;
                    }

                    if (!string.IsNullOrEmpty(rule.Suffix) &&
                        !result.EndsWith(rule.Suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result + rule.Suffix;
                    }
                }
                else if (isConditional)
                {
                    // Reverse strip: ALWAYS silent. The conditioning UDP no
                    // longer matches, so any prefix/suffix the rule had
                    // previously added is stale; removing it does not need
                    // user confirmation regardless of AUTO_APPLY. Only
                    // conditional rules strip - a non-conditional baseline
                    // prefix is never auto-removed.
                    if (!string.IsNullOrEmpty(rule.Prefix) &&
                        result.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result.Substring(rule.Prefix.Length);
                    }

                    if (!string.IsNullOrEmpty(rule.Suffix) &&
                        result.EndsWith(rule.Suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result.Substring(0, result.Length - rule.Suffix.Length);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Check if applying naming standards would change the given name.
        /// autoOnly=true → only AUTO_APPLY rules; autoOnly=false → all rules.
        /// </summary>
        public static bool HasAutoApplyChanges(string objectType, string objectName, dynamic scapiObject = null, bool autoOnly = true, string propertyCode = DefaultPropertyCode)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (!NamingStandardService.Instance.IsLoaded) return false;

            string applied = ApplyNamingStandards(objectType, objectName, scapiObject, autoOnly, propertyCode);
            return !string.Equals(applied, objectName, StringComparison.Ordinal);
        }

        #region Private Helpers

        /// <summary>
        /// Check if a rule should be applied based on its UDP condition.
        /// </summary>
        private static bool IsRuleApplicable(NamingStandardRule rule, string objectType, dynamic scapiObject)
        {
            // No UDP condition → rule applies to all objects
            if (!rule.DependsOnUdpId.HasValue || string.IsNullOrEmpty(rule.DependsOnUdpName))
                return true;

            // Has UDP condition but no SCAPI object to check → skip
            if (scapiObject == null)
                return false;

            // Read the UDP value from the erwin object
            string udpValue = ReadUdpValue(scapiObject, objectType, rule.DependsOnUdpName);

            // Compare with condition value (case-insensitive)
            if (string.IsNullOrEmpty(rule.DependsOnUdpValue))
                return !string.IsNullOrEmpty(udpValue); // Any non-empty value matches

            return string.Equals(udpValue, rule.DependsOnUdpValue, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Read a UDP value from an erwin SCAPI object.
        /// </summary>
        private static string ReadUdpValue(dynamic scapiObject, string objectType, string udpName)
        {
            try
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
                return scapiObject.Properties(path)?.Value?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Validate a single rule against an object name and add results.
        /// </summary>
        private static void ValidateRule(NamingStandardRule rule, string objectName, List<NamingValidationResult> results)
        {
            // PREFIX check
            if (!string.IsNullOrEmpty(rule.Prefix))
            {
                if (!objectName.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(NamingValidationResult.Invalid("Prefix",
                        !string.IsNullOrEmpty(rule.ErrorMessage)
                            ? rule.ErrorMessage
                            : $"Name must start with '{rule.Prefix}'", rule));
                }
            }

            // SUFFIX check
            if (!string.IsNullOrEmpty(rule.Suffix))
            {
                if (!objectName.EndsWith(rule.Suffix, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(NamingValidationResult.Invalid("Suffix",
                        !string.IsNullOrEmpty(rule.ErrorMessage)
                            ? rule.ErrorMessage
                            : $"Name must end with '{rule.Suffix}'", rule));
                }
            }

            // LENGTH check
            if (!string.IsNullOrEmpty(rule.LengthOperator) && rule.LengthValue.HasValue)
            {
                bool lengthOk = rule.LengthOperator switch
                {
                    ">=" => objectName.Length >= rule.LengthValue.Value,
                    "<=" => objectName.Length <= rule.LengthValue.Value,
                    ">" => objectName.Length > rule.LengthValue.Value,
                    "<" => objectName.Length < rule.LengthValue.Value,
                    "=" => objectName.Length == rule.LengthValue.Value,
                    _ => true
                };

                if (!lengthOk)
                {
                    results.Add(NamingValidationResult.Invalid("Length",
                        !string.IsNullOrEmpty(rule.ErrorMessage)
                            ? rule.ErrorMessage
                            : $"Name length must be {rule.LengthOperator} {rule.LengthValue}", rule));
                }
            }

            // REGEXP check
            if (!string.IsNullOrEmpty(rule.RegexpPattern))
            {
                try
                {
                    if (!Regex.IsMatch(objectName, rule.RegexpPattern))
                    {
                        results.Add(NamingValidationResult.Invalid("Regexp",
                            !string.IsNullOrEmpty(rule.ErrorMessage)
                                ? rule.ErrorMessage
                                : $"Name does not match pattern '{rule.RegexpPattern}'", rule));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"NamingValidation: Invalid regex '{rule.RegexpPattern}': {ex.Message}");
                }
            }
        }

        #endregion
    }
}
