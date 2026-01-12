using System;
using System.Collections.Generic;
using EliteSoft.Erwin.Admin.Models;
using EliteSoft.Erwin.Shared.Services;

namespace EliteSoft.Erwin.Admin.Services
{
    /// <summary>
    /// Service implementation for model validation
    /// </summary>
    public class ValidationService : IValidationService
    {
        private readonly IErwinScapiService _scapiService;

        public ValidationService(IErwinScapiService scapiService)
        {
            _scapiService = scapiService ?? throw new ArgumentNullException(nameof(scapiService));
        }

        public ValidationResults RunAllValidations()
        {
            return new ValidationResults
            {
                ModelValidations = RunModelValidations(),
                TableValidations = RunTableValidations(),
                ColumnValidations = RunColumnValidations()
            };
        }

        public List<ValidationResult> RunModelValidations()
        {
            var results = new List<ValidationResult>();

            if (_scapiService.CurrentModel == null)
            {
                results.Add(new ValidationResult
                {
                    Status = ValidationStatus.Warning,
                    ObjectName = "-",
                    RuleName = "Model Loaded",
                    Message = "No model is currently loaded",
                    ObjectType = "Model"
                });
                return results;
            }

            // Check model has name
            var modelName = _scapiService.GetModelName();
            if (string.IsNullOrWhiteSpace(modelName))
            {
                results.Add(new ValidationResult
                {
                    Status = ValidationStatus.Warning,
                    ObjectName = "(unnamed)",
                    RuleName = "Model Name",
                    Message = "Model does not have a name defined",
                    ObjectType = "Model"
                });
            }
            else
            {
                results.Add(new ValidationResult
                {
                    Status = ValidationStatus.Success,
                    ObjectName = modelName,
                    RuleName = "Model Name",
                    Message = "Model name is defined",
                    ObjectType = "Model"
                });
            }

            // Check for UDPs
            try
            {
                var udps = _scapiService.GetModelUdpValuesByLayer(msg => { });
                int totalUdps = udps.LogicalUdps.Count + udps.PhysicalUdps.Count;

                if (totalUdps == 0)
                {
                    results.Add(new ValidationResult
                    {
                        Status = ValidationStatus.Info,
                        ObjectName = modelName ?? "(model)",
                        RuleName = "UDP Count",
                        Message = "Model has no User-Defined Properties",
                        ObjectType = "Model"
                    });
                }
                else
                {
                    results.Add(new ValidationResult
                    {
                        Status = ValidationStatus.Success,
                        ObjectName = modelName ?? "(model)",
                        RuleName = "UDP Count",
                        Message = $"Model has {udps.LogicalUdps.Count} logical and {udps.PhysicalUdps.Count} physical UDPs",
                        ObjectType = "Model"
                    });
                }
            }
            catch (Exception ex)
            {
                results.Add(new ValidationResult
                {
                    Status = ValidationStatus.Warning,
                    ObjectName = modelName ?? "(model)",
                    RuleName = "UDP Access",
                    Message = $"Could not read UDPs: {ex.Message}",
                    ObjectType = "Model"
                });
            }

            return results;
        }

        public List<ValidationResult> RunTableValidations()
        {
            var results = new List<ValidationResult>();

            if (_scapiService.CurrentModel == null)
            {
                results.Add(new ValidationResult
                {
                    Status = ValidationStatus.Warning,
                    ObjectName = "-",
                    RuleName = "Model Loaded",
                    Message = "No model is currently loaded",
                    ObjectType = "Table"
                });
                return results;
            }

            // TODO: Implement table validations
            // Examples:
            // - Table naming conventions
            // - Primary key existence
            // - Table has at least one column
            // - No reserved words in table names

            results.Add(new ValidationResult
            {
                Status = ValidationStatus.Info,
                ObjectName = "-",
                RuleName = "-",
                Message = "Table validation rules not yet implemented",
                ObjectType = "Table"
            });

            return results;
        }

        public List<ValidationResult> RunColumnValidations()
        {
            var results = new List<ValidationResult>();

            if (_scapiService.CurrentModel == null)
            {
                results.Add(new ValidationResult
                {
                    Status = ValidationStatus.Warning,
                    ObjectName = "-",
                    RuleName = "Model Loaded",
                    Message = "No model is currently loaded",
                    ObjectType = "Column"
                });
                return results;
            }

            // TODO: Implement column validations
            // Examples:
            // - Column naming conventions
            // - Data type appropriateness
            // - Nullable flags on PKs
            // - Column definitions exist

            results.Add(new ValidationResult
            {
                Status = ValidationStatus.Info,
                ObjectName = "-",
                RuleName = "-",
                Message = "Column validation rules not yet implemented",
                ObjectType = "Column"
            });

            return results;
        }
    }
}
