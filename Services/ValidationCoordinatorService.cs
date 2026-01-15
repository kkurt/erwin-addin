using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Centralized validation coordinator with a single timer.
    /// Monitors for changes and triggers appropriate validations:
    /// - Glossary validation: when column Physical_Name changes (new or edited)
    /// - Domain validation: when Parent_Domain_Ref changes OR when Physical_Name changes with existing domain
    /// Results are collected and displayed in a single popup.
    /// </summary>
    public class ValidationCoordinatorService : IDisposable
    {
        private readonly dynamic _session;
        private Timer _monitorTimer;
        private bool _isMonitoring;
        private bool _disposed;
        private bool _isProcessingChange;
        private bool _validationSuspended;
        private bool _popupVisible;

        // Snapshot of all attributes
        private Dictionary<string, AttributeValidationSnapshot> _attributeSnapshots;

        // Cache of Domain ObjectId -> Domain Name
        private Dictionary<string, string> _domainCache;

        // Pending validation results to show in single popup
        private List<CollectedValidationResult> _pendingResults;

        // Monitor interval
        private const int MonitorIntervalMs = 1500;

        // Event for logging
        public event Action<string> OnLog;

        public ValidationCoordinatorService(dynamic session)
        {
            _session = session;
            _attributeSnapshots = new Dictionary<string, AttributeValidationSnapshot>();
            _domainCache = new Dictionary<string, string>();
            _pendingResults = new List<CollectedValidationResult>();

            _monitorTimer = new Timer();
            _monitorTimer.Interval = MonitorIntervalMs;
            _monitorTimer.Tick += MonitorTimer_Tick;
        }

        #region Public Methods

        public void StartMonitoring()
        {
            if (_isMonitoring) return;
            _isMonitoring = true;
            TakeSnapshot();
            _monitorTimer.Start();
            Log("ValidationCoordinatorService: Monitoring started");
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _monitorTimer.Stop();
            Log("ValidationCoordinatorService: Monitoring stopped");
        }

        public void SuspendValidation()
        {
            _validationSuspended = true;
            Log("ValidationCoordinatorService: Validation suspended");
        }

        public void ResumeValidation()
        {
            _validationSuspended = false;
            TakeSnapshot();
            Log("ValidationCoordinatorService: Validation resumed");
        }

        public void TakeSnapshot()
        {
            try
            {
                _attributeSnapshots.Clear();
                _domainCache.Clear();

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Build domain cache first
                BuildDomainCache(modelObjects, root);

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = GetTableName(entity);

                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                    if (entityAttrs == null) continue;

                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string objectId = "";
                        try { objectId = attr.ObjectId?.ToString() ?? ""; } catch { continue; }

                        if (string.IsNullOrEmpty(objectId)) continue;

                        var snapshot = CreateSnapshot(attr, tableName, modelObjects);
                        _attributeSnapshots[objectId] = snapshot;
                    }
                }

                Log($"ValidationCoordinatorService: Snapshot taken - {_attributeSnapshots.Count} attributes");
            }
            catch (Exception ex)
            {
                Log($"ValidationCoordinatorService.TakeSnapshot error: {ex.Message}");
            }
        }

        #endregion

        #region Timer Event

        private void MonitorTimer_Tick(object sender, EventArgs e)
        {
            if (!_isMonitoring || _disposed || _isProcessingChange || _validationSuspended) return;

            try
            {
                CheckForChanges();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ValidationCoordinatorService.MonitorTimer_Tick error: {ex.Message}");
            }
        }

        #endregion

        #region Change Detection

        private void CheckForChanges()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                // Clear pending results for this check cycle
                _pendingResults.Clear();

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = GetTableName(entity);

                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                    if (entityAttrs == null) continue;

                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string objectId = "";
                        try { objectId = attr.ObjectId?.ToString() ?? ""; } catch { continue; }
                        if (string.IsNullOrEmpty(objectId)) continue;

                        var currentState = CreateSnapshot(attr, tableName, modelObjects);
                        bool isNew = !_attributeSnapshots.ContainsKey(objectId);

                        if (isNew)
                        {
                            // New attribute added
                            _attributeSnapshots[objectId] = currentState;
                            ProcessNewAttribute(attr, currentState);
                        }
                        else
                        {
                            var previousState = _attributeSnapshots[objectId];
                            ProcessAttributeChanges(attr, previousState, currentState);
                            _attributeSnapshots[objectId] = currentState;
                        }
                    }
                }

                // Show collected results if any
                if (_pendingResults.Count > 0)
                {
                    ShowConsolidatedPopup();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckForChanges error: {ex.Message}");
            }
        }

        private void ProcessNewAttribute(dynamic attr, AttributeValidationSnapshot currentState)
        {
            _isProcessingChange = true;
            try
            {
                // New attribute - validate both glossary and domain if set
                bool hasValidDomain = IsValidDomain(currentState.DomainParentValue);

                // Glossary validation for new columns
                ValidateGlossary(currentState);

                // Domain validation if domain is set
                if (hasValidDomain)
                {
                    ValidateDomain(attr, currentState, null);
                }
            }
            finally
            {
                _isProcessingChange = false;
            }
        }

        private void ProcessAttributeChanges(dynamic attr, AttributeValidationSnapshot previousState, AttributeValidationSnapshot currentState)
        {
            bool physicalNameChanged = previousState.PhysicalName != currentState.PhysicalName;
            bool domainChanged = previousState.DomainParentValue != currentState.DomainParentValue;
            bool hasValidDomain = IsValidDomain(currentState.DomainParentValue);
            bool hadValidDomain = IsValidDomain(previousState.DomainParentValue);

            // No changes relevant to validation
            if (!physicalNameChanged && !domainChanged) return;

            _isProcessingChange = true;
            try
            {
                // Physical name changed - need glossary validation
                if (physicalNameChanged)
                {
                    Log($"Physical name changed: {previousState.TableName}.{previousState.PhysicalName} -> {currentState.PhysicalName}");
                    ValidateGlossary(currentState);

                    // If has valid domain, also re-validate domain pattern
                    if (hasValidDomain)
                    {
                        ValidateDomain(attr, currentState, previousState.DomainParentValue);
                    }
                }

                // Domain changed (and new domain is valid) - need domain validation
                if (domainChanged && hasValidDomain)
                {
                    Log($"Domain changed: {currentState.TableName}.{currentState.PhysicalName} -> {currentState.DomainParentValue}");
                    ValidateDomain(attr, currentState, previousState.DomainParentValue);
                }
            }
            finally
            {
                _isProcessingChange = false;
            }
        }

        #endregion

        #region Validation Methods

        private void ValidateGlossary(AttributeValidationSnapshot state)
        {
            // Skip special names
            if (string.IsNullOrEmpty(state.PhysicalName) ||
                state.PhysicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                state.PhysicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase) ||
                state.PhysicalName.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var glossary = GlossaryService.Instance;
            if (!glossary.IsLoaded)
            {
                glossary.LoadGlossary();
            }

            if (!glossary.IsLoaded)
            {
                // Can't validate - skip
                return;
            }

            var entry = glossary.GetEntry(state.PhysicalName);
            if (entry == null)
            {
                Log($"Glossary validation FAILED: {state.TableName}.{state.PhysicalName}");
                _pendingResults.Add(new CollectedValidationResult
                {
                    ValidationType = CollectedValidationResultType.Glossary,
                    TableName = state.TableName,
                    ColumnName = state.PhysicalName,
                    Message = "Column name not found in glossary. Please use a column name from the glossary."
                });
            }
            else
            {
                Log($"Glossary validation passed: {state.TableName}.{state.PhysicalName}");
            }
        }

        private void ValidateDomain(dynamic attr, AttributeValidationSnapshot state, string oldDomainValue)
        {
            // Ensure DomainDefService is loaded
            if (!DomainDefService.Instance.IsLoaded)
            {
                DomainDefService.Instance.LoadDomainDefs();
            }

            if (!DomainDefService.Instance.IsLoaded)
            {
                return;
            }

            var validationResult = DomainDefService.Instance.ValidateColumnName(state.DomainParentValue, state.PhysicalName);

            if (validationResult.IsValid)
            {
                Log($"Domain validation passed: {state.TableName}.{state.PhysicalName} -> {state.DomainParentValue}");

                // Auto-set Description and Data Type from domain entry
                if (validationResult.DomainEntry != null)
                {
                    ApplyDomainProperties(attr, validationResult.DomainEntry);
                }
            }
            else
            {
                Log($"Domain validation FAILED: {state.TableName}.{state.PhysicalName} -> {state.DomainParentValue}");

                string message;
                string pattern = null;

                if (validationResult.DomainEntry == null)
                {
                    message = "Domain not found in DOMAIN_DEF table. Please add it to the database or select a different domain.";
                }
                else
                {
                    message = "Column name does not match domain pattern. Please rename the column or select a different domain.";
                    pattern = validationResult.DomainEntry.Regexp;
                }

                _pendingResults.Add(new CollectedValidationResult
                {
                    ValidationType = CollectedValidationResultType.Domain,
                    TableName = state.TableName,
                    ColumnName = state.PhysicalName,
                    DomainName = state.DomainParentValue,
                    Pattern = pattern,
                    Message = message
                });
            }
        }

        private void ApplyDomainProperties(dynamic attr, DomainDefEntry domainEntry)
        {
            try
            {
                bool hasDescription = !string.IsNullOrEmpty(domainEntry.Description);
                bool hasDataType = !string.IsNullOrEmpty(domainEntry.DataType);

                if (!hasDescription && !hasDataType) return;

                int transId = _session.BeginNamedTransaction("ApplyDomainProperties");
                try
                {
                    if (hasDescription)
                    {
                        try
                        {
                            attr.Properties("Definition").Value = domainEntry.Description;
                            Log($"Set Definition to '{domainEntry.Description}'");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error setting Definition: {ex.Message}");
                        }
                    }

                    if (hasDataType)
                    {
                        try
                        {
                            attr.Properties("Physical_Data_Type").Value = domainEntry.DataType;
                            Log($"Set Physical_Data_Type to '{domainEntry.DataType}'");
                        }
                        catch (Exception ex)
                        {
                            Log($"Error setting Physical_Data_Type: {ex.Message}");
                        }
                    }

                    _session.CommitTransaction(transId);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"Error applying domain properties: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"ApplyDomainProperties error: {ex.Message}");
            }
        }

        #endregion

        #region Popup Display

        private void ShowConsolidatedPopup()
        {
            if (_pendingResults.Count == 0 || _popupVisible) return;

            _popupVisible = true;
            try
            {
                var sb = new StringBuilder();

                var domainResults = _pendingResults.FindAll(r => r.ValidationType == CollectedValidationResultType.Domain);
                var glossaryResults = _pendingResults.FindAll(r => r.ValidationType == CollectedValidationResultType.Glossary);

                if (domainResults.Count > 0)
                {
                    sb.AppendLine("=== DOMAIN VALIDATION ===");
                    sb.AppendLine();
                    foreach (var result in domainResults)
                    {
                        sb.AppendLine($"Table: {result.TableName}");
                        sb.AppendLine($"Column: {result.ColumnName}");
                        if (!string.IsNullOrEmpty(result.DomainName))
                        {
                            sb.AppendLine($"Domain: {result.DomainName}");
                        }
                        if (!string.IsNullOrEmpty(result.Pattern))
                        {
                            sb.AppendLine($"Pattern: {result.Pattern}");
                        }
                        sb.AppendLine($"Issue: {result.Message}");
                        sb.AppendLine();
                    }
                }

                if (glossaryResults.Count > 0)
                {
                    if (domainResults.Count > 0)
                    {
                        sb.AppendLine("---");
                        sb.AppendLine();
                    }
                    sb.AppendLine("=== GLOSSARY VALIDATION ===");
                    sb.AppendLine();
                    foreach (var result in glossaryResults)
                    {
                        sb.AppendLine($"Table: {result.TableName}");
                        sb.AppendLine($"Column: {result.ColumnName}");
                        sb.AppendLine($"Issue: {result.Message}");
                        sb.AppendLine();
                    }
                }

                int totalErrors = _pendingResults.Count;
                sb.AppendLine($"Total: {totalErrors} validation error(s)");

                string title = "Validation Warnings";
                if (domainResults.Count > 0 && glossaryResults.Count == 0)
                {
                    title = "Domain Validation Warning";
                }
                else if (glossaryResults.Count > 0 && domainResults.Count == 0)
                {
                    title = "Glossary Validation Warning";
                }

                MessageBox.Show(
                    sb.ToString(),
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                _popupVisible = false;
                _pendingResults.Clear();
            }
        }

        #endregion

        #region Helper Methods

        private AttributeValidationSnapshot CreateSnapshot(dynamic attr, string tableName, dynamic modelObjects)
        {
            string objectId = "";
            string attrName = "";
            string physicalName = "";
            string domainParentValue = "";

            try { objectId = attr.ObjectId?.ToString() ?? ""; } catch { }
            try { attrName = attr.Name ?? ""; } catch { }
            try
            {
                string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                physicalName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : attrName;
            }
            catch { physicalName = attrName; }

            domainParentValue = GetDomainParentValue(attr, modelObjects);

            return new AttributeValidationSnapshot
            {
                ObjectId = objectId,
                AttributeName = attrName,
                PhysicalName = physicalName,
                DomainParentValue = domainParentValue,
                TableName = tableName
            };
        }

        private string GetTableName(dynamic entity)
        {
            string tableName = "";
            string entityName = "";

            try { entityName = entity.Name ?? ""; } catch { }
            try
            {
                string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
            }
            catch { tableName = entityName; }

            return tableName;
        }

        private string GetDomainParentValue(dynamic attr, dynamic modelObjects)
        {
            try
            {
                var parentDomainRefProp = attr.Properties("Parent_Domain_Ref");
                if (parentDomainRefProp != null)
                {
                    string refValue = parentDomainRefProp.Value?.ToString() ?? "";

                    if (!string.IsNullOrEmpty(refValue) && refValue != "Blob")
                    {
                        // If it looks like an ObjectId, try to resolve it
                        if (refValue.StartsWith("{") && refValue.Contains("+"))
                        {
                            if (_domainCache.TryGetValue(refValue, out string domainName))
                            {
                                return domainName;
                            }
                        }
                        else
                        {
                            return refValue;
                        }
                    }
                }
            }
            catch { }

            return "";
        }

        private void BuildDomainCache(dynamic modelObjects, dynamic root)
        {
            try
            {
                dynamic allDomains = modelObjects.Collect(root, "Domain");
                if (allDomains == null) return;

                foreach (dynamic domain in allDomains)
                {
                    if (domain == null) continue;

                    try
                    {
                        string objectId = domain.ObjectId?.ToString() ?? "";
                        string name = domain.Name?.ToString() ?? "";

                        if (!string.IsNullOrEmpty(objectId) && !string.IsNullOrEmpty(name))
                        {
                            _domainCache[objectId] = name;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private bool IsValidDomain(string domainValue)
        {
            return !string.IsNullOrEmpty(domainValue) &&
                   domainValue != "(SELECT)" &&
                   domainValue != "<default>" &&
                   domainValue != "Blob";
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
            _monitorTimer?.Dispose();
        }

        #endregion

        #region Inner Classes

        private class AttributeValidationSnapshot
        {
            public string ObjectId { get; set; }
            public string AttributeName { get; set; }
            public string PhysicalName { get; set; }
            public string DomainParentValue { get; set; }
            public string TableName { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Represents a collected validation result
    /// </summary>
    public class CollectedValidationResult
    {
        public CollectedValidationResultType ValidationType { get; set; }
        public string TableName { get; set; }
        public string ColumnName { get; set; }
        public string Message { get; set; }
        public string DomainName { get; set; }
        public string Pattern { get; set; }
    }

    /// <summary>
    /// Type of validation
    /// </summary>
    public enum CollectedValidationResultType
    {
        Glossary,
        Domain
    }
}
