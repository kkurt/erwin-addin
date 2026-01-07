using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Column validation service - monitors and validates physical column names
    /// Uses lightweight polling with change detection
    /// </summary>
    public class ColumnValidationService : IDisposable
    {
        private readonly dynamic _session;
        private readonly Timer _monitorTimer;
        private readonly Dictionary<string, ColumnSnapshot> _columnSnapshots;
        private readonly List<IColumnNameRule> _validationRules;
        private bool _isMonitoring;
        private bool _disposed;

        /// <summary>
        /// Fired when a column name changes and fails validation
        /// </summary>
        public event EventHandler<ColumnValidationEventArgs> OnValidationFailed;

        /// <summary>
        /// Fired when monitoring detects any column change
        /// </summary>
        public event EventHandler<ColumnChangeEventArgs> OnColumnChanged;

        public ColumnValidationService(dynamic session)
        {
            _session = session;
            _columnSnapshots = new Dictionary<string, ColumnSnapshot>();
            _isMonitoring = false;

            // Initialize validation rules
            _validationRules = new List<IColumnNameRule>
            {
                new PrefixRule("col_", caseSensitive: false)  // col_ prefix required
            };

            // Setup timer for monitoring (lightweight polling)
            _monitorTimer = new Timer();
            _monitorTimer.Interval = 2000; // 2 seconds
            _monitorTimer.Tick += OnMonitorTick;
        }

        /// <summary>
        /// Takes initial snapshot of all column names
        /// </summary>
        public void TakeSnapshot()
        {
            _columnSnapshots.Clear();

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Collect all Entities
                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    string entityName = "";

                    try { entityName = entity.Name ?? ""; } catch { }
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch { tableName = entityName; }

                    // Get attributes of this entity
                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                    if (entityAttrs == null) continue;

                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string objectId = "";
                        string attrName = "";
                        string physicalName = "";

                        try { objectId = attr.ObjectId?.ToString() ?? Guid.NewGuid().ToString(); } catch { objectId = Guid.NewGuid().ToString(); }
                        try { attrName = attr.Name ?? ""; } catch { }
                        try
                        {
                            string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                            physicalName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : attrName;
                        }
                        catch { physicalName = attrName; }

                        _columnSnapshots[objectId] = new ColumnSnapshot
                        {
                            ObjectId = objectId,
                            AttributeName = attrName,
                            PhysicalName = physicalName,
                            TableName = tableName
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TakeSnapshot error: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts real-time monitoring
        /// </summary>
        public void StartMonitoring()
        {
            if (_isMonitoring) return;

            TakeSnapshot();
            _isMonitoring = true;
            _monitorTimer.Start();
            System.Diagnostics.Debug.WriteLine("Monitoring started");
        }

        /// <summary>
        /// Stops monitoring
        /// </summary>
        public void StopMonitoring()
        {
            if (!_isMonitoring) return;

            _monitorTimer.Stop();
            _isMonitoring = false;
            System.Diagnostics.Debug.WriteLine("Monitoring stopped");
        }

        /// <summary>
        /// Timer tick - check for changes
        /// </summary>
        private void OnMonitorTick(object sender, EventArgs e)
        {
            CheckForChanges();
        }

        /// <summary>
        /// Checks for column name changes and validates them
        /// </summary>
        public void CheckForChanges()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Collect all Entities
                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    string entityName = "";

                    try { entityName = entity.Name ?? ""; } catch { }
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch { tableName = entityName; }

                    // Get attributes of this entity
                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                    if (entityAttrs == null) continue;

                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string objectId = "";
                        string attrName = "";
                        string physicalName = "";

                        try { objectId = attr.ObjectId?.ToString() ?? ""; } catch { continue; }
                        try { attrName = attr.Name ?? ""; } catch { }
                        try
                        {
                            string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                            physicalName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : attrName;
                        }
                        catch { physicalName = attrName; }

                        // Check if this is a new or changed column
                        bool isNew = !_columnSnapshots.ContainsKey(objectId);
                        bool isChanged = !isNew && _columnSnapshots[objectId].PhysicalName != physicalName;

                        if (isNew || isChanged)
                        {
                            string previousName = isNew ? null : _columnSnapshots[objectId].PhysicalName;

                            // Fire change event
                            OnColumnChanged?.Invoke(this, new ColumnChangeEventArgs
                            {
                                ObjectId = objectId,
                                TableName = tableName,
                                AttributeName = attrName,
                                OldPhysicalName = previousName,
                                NewPhysicalName = physicalName,
                                IsNew = isNew
                            });

                            // Validate the new name
                            var validationResult = ValidateColumnName(physicalName);
                            if (!validationResult.IsValid)
                            {
                                OnValidationFailed?.Invoke(this, new ColumnValidationEventArgs
                                {
                                    ObjectId = objectId,
                                    TableName = tableName,
                                    AttributeName = attrName,
                                    PhysicalName = physicalName,
                                    PreviousName = previousName,
                                    ValidationMessage = validationResult.Message,
                                    RuleName = validationResult.RuleName
                                });
                            }

                            // Update snapshot
                            _columnSnapshots[objectId] = new ColumnSnapshot
                            {
                                ObjectId = objectId,
                                AttributeName = attrName,
                                PhysicalName = physicalName,
                                TableName = tableName
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckForChanges error: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates a single column name against all rules
        /// </summary>
        public ValidationResult ValidateColumnName(string physicalName)
        {
            if (string.IsNullOrEmpty(physicalName))
            {
                return ValidationResult.Invalid("Column name is empty", "EmptyName");
            }

            // Skip validation for default column names (newly created columns in erwin)
            if (physicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                physicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Valid(); // Skip - user hasn't named it yet
            }

            // Skip validation for "PLEASE CHANGE IT" (already marked as invalid)
            if (physicalName.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Valid(); // Skip - already marked for change
            }

            foreach (var rule in _validationRules)
            {
                var result = rule.Validate(physicalName);
                if (!result.IsValid)
                {
                    return result;
                }
            }

            return ValidationResult.Valid();
        }

        /// <summary>
        /// Validates all columns and returns list of issues
        /// </summary>
        public List<ColumnValidationIssue> ValidateAllColumns()
        {
            var issues = new List<ColumnValidationIssue>();

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return issues;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string tableName = "";
                    string entityName = "";

                    try { entityName = entity.Name ?? ""; } catch { }
                    try
                    {
                        string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
                    }
                    catch { tableName = entityName; }

                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                    if (entityAttrs == null) continue;

                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string attrName = "";
                        string physicalName = "";

                        try { attrName = attr.Name ?? ""; } catch { }
                        try
                        {
                            string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                            physicalName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : attrName;
                        }
                        catch { physicalName = attrName; }

                        var result = ValidateColumnName(physicalName);
                        if (!result.IsValid)
                        {
                            issues.Add(new ColumnValidationIssue
                            {
                                TableName = tableName,
                                AttributeName = attrName,
                                PhysicalName = physicalName,
                                Issue = result.Message,
                                RuleName = result.RuleName
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ValidateAllColumns error: {ex.Message}");
            }

            return issues;
        }

        public bool IsMonitoring => _isMonitoring;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
            _monitorTimer?.Dispose();
        }
    }

    #region Models

    public class ColumnSnapshot
    {
        public string ObjectId { get; set; }
        public string AttributeName { get; set; }
        public string PhysicalName { get; set; }
        public string TableName { get; set; }
    }

    public class ColumnChangeEventArgs : EventArgs
    {
        public string ObjectId { get; set; }
        public string TableName { get; set; }
        public string AttributeName { get; set; }
        public string OldPhysicalName { get; set; }
        public string NewPhysicalName { get; set; }
        public bool IsNew { get; set; }
    }

    public class ColumnValidationEventArgs : EventArgs
    {
        public string ObjectId { get; set; }
        public string TableName { get; set; }
        public string AttributeName { get; set; }
        public string PhysicalName { get; set; }
        public string PreviousName { get; set; }
        public string ValidationMessage { get; set; }
        public string RuleName { get; set; }
    }

    public class ColumnValidationIssue
    {
        public string TableName { get; set; }
        public string AttributeName { get; set; }
        public string PhysicalName { get; set; }
        public string Issue { get; set; }
        public string RuleName { get; set; }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }
        public string RuleName { get; set; }

        public static ValidationResult Valid() => new ValidationResult { IsValid = true };
        public static ValidationResult Invalid(string message, string ruleName) =>
            new ValidationResult { IsValid = false, Message = message, RuleName = ruleName };
    }

    #endregion

    #region Validation Rules

    public interface IColumnNameRule
    {
        string RuleName { get; }
        ValidationResult Validate(string columnName);
    }

    /// <summary>
    /// Requires columns to start with a specific prefix (e.g., "col_")
    /// </summary>
    public class PrefixRule : IColumnNameRule
    {
        private readonly string _prefix;
        private readonly bool _caseSensitive;

        public string RuleName => "PrefixRule";

        public PrefixRule(string prefix, bool caseSensitive = false)
        {
            _prefix = prefix;
            _caseSensitive = caseSensitive;
        }

        public ValidationResult Validate(string columnName)
        {
            if (string.IsNullOrEmpty(columnName))
                return ValidationResult.Valid();

            bool hasPrefix = _caseSensitive
                ? columnName.StartsWith(_prefix)
                : columnName.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

            if (!hasPrefix)
            {
                return ValidationResult.Invalid(
                    $"Column name must start with '{_prefix}' prefix",
                    RuleName);
            }

            return ValidationResult.Valid();
        }
    }

    #endregion
}
