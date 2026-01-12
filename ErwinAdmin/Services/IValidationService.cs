using System.Collections.Generic;
using EliteSoft.Erwin.Admin.Models;

namespace EliteSoft.Erwin.Admin.Services
{
    /// <summary>
    /// Service interface for model validation
    /// </summary>
    public interface IValidationService
    {
        /// <summary>
        /// Runs all validations on the current model
        /// </summary>
        /// <returns>Validation results grouped by type</returns>
        ValidationResults RunAllValidations();

        /// <summary>
        /// Runs model-level validations
        /// </summary>
        List<ValidationResult> RunModelValidations();

        /// <summary>
        /// Runs table-level validations
        /// </summary>
        List<ValidationResult> RunTableValidations();

        /// <summary>
        /// Runs column-level validations
        /// </summary>
        List<ValidationResult> RunColumnValidations();
    }

    /// <summary>
    /// Container for all validation results
    /// </summary>
    public class ValidationResults
    {
        public List<ValidationResult> ModelValidations { get; set; } = new List<ValidationResult>();
        public List<ValidationResult> TableValidations { get; set; } = new List<ValidationResult>();
        public List<ValidationResult> ColumnValidations { get; set; } = new List<ValidationResult>();

        public int TotalCount => ModelValidations.Count + TableValidations.Count + ColumnValidations.Count;
        public int ErrorCount => CountByStatus(ValidationStatus.Error);
        public int WarningCount => CountByStatus(ValidationStatus.Warning);
        public int InfoCount => CountByStatus(ValidationStatus.Info);

        private int CountByStatus(ValidationStatus status)
        {
            int count = 0;
            foreach (var v in ModelValidations)
                if (v.Status == status) count++;
            foreach (var v in TableValidations)
                if (v.Status == status) count++;
            foreach (var v in ColumnValidations)
                if (v.Status == status) count++;
            return count;
        }
    }

    /// <summary>
    /// Single validation result
    /// </summary>
    public class ValidationResult
    {
        public ValidationStatus Status { get; set; }
        public string ObjectName { get; set; }
        public string RuleName { get; set; }
        public string Message { get; set; }
        public string ObjectType { get; set; }

        public string StatusIcon => Status switch
        {
            ValidationStatus.Error => "✗",
            ValidationStatus.Warning => "⚠",
            ValidationStatus.Info => "ℹ",
            ValidationStatus.Success => "✓",
            _ => "?"
        };
    }

    /// <summary>
    /// Validation status levels
    /// </summary>
    public enum ValidationStatus
    {
        Success,
        Info,
        Warning,
        Error
    }
}
