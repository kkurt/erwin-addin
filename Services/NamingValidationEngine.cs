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
    /// Checks prefix, suffix, length, and regex rules from MC_NAMING_STANDARD.
    /// </summary>
    public static class NamingValidationEngine
    {
        /// <summary>
        /// Validate an object name against all naming standard rules for the given object type.
        /// </summary>
        public static List<NamingValidationResult> ValidateObjectName(string objectType, string objectName)
        {
            var results = new List<NamingValidationResult>();

            if (string.IsNullOrEmpty(objectName)) return results;
            if (!NamingStandardService.Instance.IsLoaded) return results;

            var rules = NamingStandardService.Instance.GetByObjectType(objectType);

            foreach (var rule in rules)
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
                    catch
                    {
                        // Invalid regex pattern — skip
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Apply auto-apply naming standards (prefix/suffix) to an object name.
        /// Only applies rules where AutoApply = true.
        /// Returns the modified name, or the original if no auto-apply rules exist.
        /// </summary>
        public static string ApplyNamingStandards(string objectType, string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return objectName;
            if (!NamingStandardService.Instance.IsLoaded) return objectName;

            var rules = NamingStandardService.Instance.GetByObjectType(objectType)
                .Where(r => r.AutoApply);

            string result = objectName;

            foreach (var rule in rules)
            {
                // Prefix auto-apply
                if (!string.IsNullOrEmpty(rule.Prefix) &&
                    !result.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result = rule.Prefix + result;
                }

                // Suffix auto-apply
                if (!string.IsNullOrEmpty(rule.Suffix) &&
                    !result.EndsWith(rule.Suffix, StringComparison.OrdinalIgnoreCase))
                {
                    result = result + rule.Suffix;
                }
            }

            return result;
        }

        /// <summary>
        /// Check if there are any auto-apply rules that would change the given name.
        /// Returns true if the name would be modified.
        /// </summary>
        public static bool HasAutoApplyChanges(string objectType, string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (!NamingStandardService.Instance.IsLoaded) return false;

            string applied = ApplyNamingStandards(objectType, objectName);
            return !string.Equals(applied, objectName, StringComparison.Ordinal);
        }
    }
}
