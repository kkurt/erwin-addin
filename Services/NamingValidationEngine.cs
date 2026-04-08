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

        public static NamingValidationResult Valid(string ruleName) =>
            new NamingValidationResult { IsValid = true, RuleName = ruleName };

        public static NamingValidationResult Invalid(string ruleName, string message) =>
            new NamingValidationResult { IsValid = false, RuleName = ruleName, ErrorMessage = message };
    }

    /// <summary>
    /// Stateless validation engine for object naming standards.
    /// Supports UDP-conditioned rules (DEPENDS_ON_UDP_ID + DEPENDS_ON_UDP_VALUE).
    /// </summary>
    public static class NamingValidationEngine
    {
        /// <summary>
        /// Validate an object name against naming standard rules.
        /// Rules with DEPENDS_ON_UDP_ID are only applied if the object's UDP value matches.
        /// </summary>
        /// <param name="objectType">Object type: "Table", "Column", "Index", "View"</param>
        /// <param name="objectName">Physical name of the object</param>
        /// <param name="scapiObject">erwin SCAPI object for reading UDP values (null = skip conditional rules)</param>
        public static List<NamingValidationResult> ValidateObjectName(string objectType, string objectName, dynamic scapiObject = null)
        {
            var results = new List<NamingValidationResult>();

            if (string.IsNullOrEmpty(objectName)) return results;
            if (!NamingStandardService.Instance.IsLoaded) return results;

            var rules = NamingStandardService.Instance.GetByObjectType(objectType);

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
        /// Apply auto-apply naming standards (prefix/suffix) to an object name.
        /// Only applies rules where AutoApply = true and UDP condition matches.
        /// </summary>
        public static string ApplyNamingStandards(string objectType, string objectName, dynamic scapiObject = null)
        {
            if (string.IsNullOrEmpty(objectName)) return objectName;
            if (!NamingStandardService.Instance.IsLoaded) return objectName;

            var rules = NamingStandardService.Instance.GetByObjectType(objectType)
                .Where(r => r.AutoApply);

            string result = objectName;

            foreach (var rule in rules)
            {
                if (!IsRuleApplicable(rule, objectType, scapiObject))
                    continue;

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

            return result;
        }

        /// <summary>
        /// Check if any auto-apply rules would change the given name.
        /// </summary>
        public static bool HasAutoApplyChanges(string objectType, string objectName, dynamic scapiObject = null)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (!NamingStandardService.Instance.IsLoaded) return false;

            string applied = ApplyNamingStandards(objectType, objectName, scapiObject);
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
                            : $"Name must start with '{rule.Prefix}'"));
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
                            : $"Name must end with '{rule.Suffix}'"));
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
                            : $"Name length must be {rule.LengthOperator} {rule.LengthValue}"));
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
                                : $"Name does not match pattern '{rule.RegexpPattern}'"));
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
