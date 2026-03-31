using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Result of a single UDP validation check.
    /// </summary>
    public class UdpValidationResult
    {
        public bool IsValid { get; set; }
        public string UdpName { get; set; }
        public string ErrorMessage { get; set; }

        public static UdpValidationResult Valid(string udpName) =>
            new UdpValidationResult { IsValid = true, UdpName = udpName };

        public static UdpValidationResult Invalid(string udpName, string message) =>
            new UdpValidationResult { IsValid = false, UdpName = udpName, ErrorMessage = message };
    }

    /// <summary>
    /// Stateless validation engine for UDP values.
    /// Validates based on UDP type, required flag, min/max, operator, and list options.
    /// </summary>
    public static class UdpValidationEngine
    {
        /// <summary>
        /// Validate all UDP values for a given object type and operation (Create/Update).
        /// </summary>
        public static List<UdpValidationResult> ValidateAll(
            string objectType,
            Dictionary<string, string> udpValues,
            string operation)
        {
            var results = new List<UdpValidationResult>();
            var definitions = UdpDefinitionService.Instance.GetAll();

            foreach (var def in definitions)
            {
                // APPLY_ON check: "Both" always applies, otherwise must match operation
                if (!string.IsNullOrEmpty(def.ApplyOn) &&
                    !def.ApplyOn.Equals("Both", StringComparison.OrdinalIgnoreCase) &&
                    !def.ApplyOn.Equals(operation, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = "";
                if (udpValues != null)
                    udpValues.TryGetValue(def.Name, out value);
                value = value ?? "";

                // IS_REQUIRED check
                if (def.IsRequired && string.IsNullOrEmpty(value))
                {
                    string msg = !string.IsNullOrEmpty(def.ErrorMessage)
                        ? def.ErrorMessage
                        : $"{def.Name} is required.";
                    results.Add(UdpValidationResult.Invalid(def.Name, msg));
                    continue;
                }

                if (string.IsNullOrEmpty(value)) continue;

                // Type-based validation
                switch (def.UdpType?.ToLower())
                {
                    case "int":
                    case "real":
                        ValidateNumeric(def, value, results);
                        break;
                    case "text":
                        ValidateText(def, value, results);
                        break;
                    case "list":
                        ValidateList(def, value, results);
                        break;
                    case "date":
                        ValidateDate(def, value, results);
                        break;
                }
            }

            return results;
        }

        private static void ValidateNumeric(UdpDefinitionRuntime def, string value, List<UdpValidationResult> results)
        {
            if (!decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal numValue))
            {
                results.Add(UdpValidationResult.Invalid(def.Name,
                    !string.IsNullOrEmpty(def.ErrorMessage) ? def.ErrorMessage : $"{def.Name}: '{value}' is not a valid number."));
                return;
            }

            // MinValue / MaxValue range check
            if (def.MinValue.HasValue && numValue < def.MinValue.Value)
            {
                results.Add(UdpValidationResult.Invalid(def.Name,
                    !string.IsNullOrEmpty(def.ErrorMessage) ? def.ErrorMessage : $"{def.Name} must be >= {def.MinValue.Value}."));
                return;
            }
            if (def.MaxValue.HasValue && numValue > def.MaxValue.Value)
            {
                results.Add(UdpValidationResult.Invalid(def.Name,
                    !string.IsNullOrEmpty(def.ErrorMessage) ? def.ErrorMessage : $"{def.Name} must be <= {def.MaxValue.Value}."));
                return;
            }

            // Operator-based validation
            if (!string.IsNullOrEmpty(def.ValidationOperator) && !string.IsNullOrEmpty(def.ValidationValue))
            {
                bool valid = EvaluateNumericOperator(def.ValidationOperator, def.ValidationValue, numValue);
                if (!valid)
                {
                    results.Add(UdpValidationResult.Invalid(def.Name,
                        !string.IsNullOrEmpty(def.ErrorMessage) ? def.ErrorMessage
                            : $"{def.Name}: value {value} does not satisfy {def.ValidationOperator} {def.ValidationValue}."));
                }
            }
        }

        private static bool EvaluateNumericOperator(string op, string validationValue, decimal actual)
        {
            var parts = validationValue.Split(',').Select(v => v.Trim()).ToArray();

            switch (op)
            {
                case "GreaterThan":
                    return decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal gt) && actual > gt;
                case "LessThan":
                    return decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal lt) && actual < lt;
                case "Equals":
                    return decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal eq) && actual == eq;
                case "NotEquals":
                    return decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal ne) && actual != ne;
                case "Between":
                    if (parts.Length >= 2 &&
                        decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal bMin) &&
                        decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal bMax))
                    {
                        return actual >= bMin && actual <= bMax;
                    }
                    return false;
                default:
                    return true;
            }
        }

        private static void ValidateText(UdpDefinitionRuntime def, string value, List<UdpValidationResult> results)
        {
            // MAX_LENGTH check
            if (def.MaxLength.HasValue && value.Length > def.MaxLength.Value)
            {
                results.Add(UdpValidationResult.Invalid(def.Name,
                    !string.IsNullOrEmpty(def.ErrorMessage) ? def.ErrorMessage
                        : $"{def.Name} must be at most {def.MaxLength.Value} characters."));
                return;
            }

            // Operator-based validation
            if (!string.IsNullOrEmpty(def.ValidationOperator) && !string.IsNullOrEmpty(def.ValidationValue))
            {
                bool valid = EvaluateTextOperator(def.ValidationOperator, def.ValidationValue, value);
                if (!valid)
                {
                    results.Add(UdpValidationResult.Invalid(def.Name,
                        !string.IsNullOrEmpty(def.ErrorMessage) ? def.ErrorMessage
                            : $"{def.Name}: value does not satisfy {def.ValidationOperator} '{def.ValidationValue}'."));
                }
            }
        }

        private static bool EvaluateTextOperator(string op, string validationValue, string actual)
        {
            switch (op)
            {
                case "Regexp":
                    try { return Regex.IsMatch(actual, validationValue); }
                    catch { return false; }
                case "MinLength":
                    return int.TryParse(validationValue, out int minLen) && actual.Length >= minLen;
                case "MaxLength":
                    return int.TryParse(validationValue, out int maxLen) && actual.Length <= maxLen;
                case "Contains":
                    return actual.IndexOf(validationValue, StringComparison.OrdinalIgnoreCase) >= 0;
                case "StartsWith":
                    return actual.StartsWith(validationValue, StringComparison.OrdinalIgnoreCase);
                default:
                    return true;
            }
        }

        private static void ValidateList(UdpDefinitionRuntime def, string value, List<UdpValidationResult> results)
        {
            if (def.ListOptions == null || def.ListOptions.Count == 0)
                return;

            bool found = def.ListOptions.Any(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (!found)
            {
                string allowed = string.Join(", ", def.ListOptions.Select(o => o.Value));
                results.Add(UdpValidationResult.Invalid(def.Name,
                    !string.IsNullOrEmpty(def.ErrorMessage) ? def.ErrorMessage
                        : $"{def.Name}: '{value}' is not a valid option. Allowed: {allowed}"));
            }
        }

        private static void ValidateDate(UdpDefinitionRuntime def, string value, List<UdpValidationResult> results)
        {
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                results.Add(UdpValidationResult.Invalid(def.Name,
                    !string.IsNullOrEmpty(def.ErrorMessage) ? def.ErrorMessage
                        : $"{def.Name}: '{value}' is not a valid date."));
            }
        }
    }
}
