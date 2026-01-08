using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Column validation service - monitors and validates physical column names against glossary
    /// </summary>
    public class ColumnValidationService : IDisposable
    {
        private readonly dynamic _session;
        private readonly Timer _monitorTimer;
        private readonly Timer _windowMonitorTimer;
        private readonly Dictionary<string, ColumnSnapshot> _columnSnapshots;
        private readonly List<IColumnNameRule> _validationRules;
        private bool _isMonitoring;
        private bool _disposed;
        private bool _columnEditorWasOpen;

        // Win32 API for window enumeration
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// Fired when a column name changes and fails validation
        /// </summary>
        public event EventHandler<ColumnValidationEventArgs> OnValidationFailed;

        /// <summary>
        /// Fired when a column name is valid and found in glossary
        /// </summary>
        public event EventHandler<ColumnValidationEventArgs> OnValidationPassed;

        /// <summary>
        /// Fired when monitoring detects any column change
        /// </summary>
        public event EventHandler<ColumnChangeEventArgs> OnColumnChanged;

        public ColumnValidationService(dynamic session)
        {
            _session = session;
            _columnSnapshots = new Dictionary<string, ColumnSnapshot>();
            _isMonitoring = false;
            _columnEditorWasOpen = false;

            // Initialize validation rules - now using Glossary validation
            _validationRules = new List<IColumnNameRule>
            {
                new GlossaryRule()  // Validate against GLOSSARY table
            };

            // Setup timer for monitoring (lightweight polling)
            _monitorTimer = new Timer();
            _monitorTimer.Interval = 2000; // 2 seconds
            _monitorTimer.Tick += OnMonitorTick;

            // Setup timer for window monitoring (check if Column Editor closed)
            _windowMonitorTimer = new Timer();
            _windowMonitorTimer.Interval = 500; // 500ms - check frequently
            _windowMonitorTimer.Tick += OnWindowMonitorTick;
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
            _windowMonitorTimer.Start();
        }

        /// <summary>
        /// Stops monitoring
        /// </summary>
        public void StopMonitoring()
        {
            if (!_isMonitoring) return;

            _monitorTimer.Stop();
            _windowMonitorTimer.Stop();
            _isMonitoring = false;
        }

        private void OnMonitorTick(object sender, EventArgs e)
        {
            CheckForChanges();
        }

        /// <summary>
        /// Monitors for Column Editor window close event
        /// </summary>
        private void OnWindowMonitorTick(object sender, EventArgs e)
        {
            bool editorIsOpen = IsColumnEditorOpen();

            // If editor was open and now closed, delete "PLEASE CHANGE IT" columns
            if (_columnEditorWasOpen && !editorIsOpen)
            {
                System.Diagnostics.Debug.WriteLine("Column Editor closed - checking for PLEASE CHANGE IT columns");
                DeletePleaseChangeItColumns();
                TakeSnapshot(); // Refresh snapshot after deletion
            }

            _columnEditorWasOpen = editorIsOpen;
        }

        /// <summary>
        /// Checks if erwin Column Editor window is open
        /// </summary>
        private bool IsColumnEditorOpen()
        {
            bool found = false;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                StringBuilder title = new StringBuilder(256);
                GetWindowText(hWnd, title, 256);
                string windowTitle = title.ToString();

                // Check for Column Editor window pattern: "Oracle Table 'X' Column 'Y' Editor"
                // or similar patterns with "Column" and "Editor"
                if (windowTitle.Contains("Column") && windowTitle.Contains("Editor"))
                {
                    found = true;
                    return false; // Stop enumeration
                }

                return true; // Continue enumeration
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Deletes all columns named "PLEASE CHANGE IT" from the model
        /// </summary>
        public void DeletePleaseChangeItColumns()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                var columnsToDelete = new List<dynamic>();

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    dynamic entityAttrs = null;
                    try { entityAttrs = modelObjects.Collect(entity, "Attribute"); } catch { continue; }
                    if (entityAttrs == null) continue;

                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string physicalName = "";
                        try
                        {
                            string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                            string attrName = attr.Name ?? "";
                            physicalName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : attrName;
                        }
                        catch { continue; }

                        if (physicalName.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
                        {
                            columnsToDelete.Add(attr);
                        }
                    }
                }

                // Delete the columns
                if (columnsToDelete.Count > 0)
                {
                    int transId = _session.BeginNamedTransaction("DeleteInvalidColumns");
                    try
                    {
                        foreach (var attr in columnsToDelete)
                        {
                            try
                            {
                                string attrName = attr.Name ?? "unknown";
                                modelObjects.Remove(attr);
                                System.Diagnostics.Debug.WriteLine($"Deleted column: {attrName}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to delete column: {ex.Message}");
                            }
                        }
                        _session.CommitTransaction(transId);
                        System.Diagnostics.Debug.WriteLine($"Deleted {columnsToDelete.Count} 'PLEASE CHANGE IT' column(s)");
                    }
                    catch
                    {
                        try { _session.RollbackTransaction(transId); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DeletePleaseChangeItColumns error: {ex.Message}");
            }
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

                        bool isNew = !_columnSnapshots.ContainsKey(objectId);
                        bool isChanged = !isNew && _columnSnapshots[objectId].PhysicalName != physicalName;

                        if (isNew || isChanged)
                        {
                            string previousName = isNew ? null : _columnSnapshots[objectId].PhysicalName;

                            OnColumnChanged?.Invoke(this, new ColumnChangeEventArgs
                            {
                                ObjectId = objectId,
                                TableName = tableName,
                                AttributeName = attrName,
                                OldPhysicalName = previousName,
                                NewPhysicalName = physicalName,
                                IsNew = isNew
                            });

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
                                    RuleName = validationResult.RuleName,
                                    GlossaryEntry = null
                                });
                            }
                            else if (validationResult.GlossaryEntry != null)
                            {
                                // Valid and found in glossary - trigger action to set DataType and Owner
                                OnValidationPassed?.Invoke(this, new ColumnValidationEventArgs
                                {
                                    ObjectId = objectId,
                                    TableName = tableName,
                                    AttributeName = attrName,
                                    PhysicalName = physicalName,
                                    PreviousName = previousName,
                                    ValidationMessage = "Found in glossary",
                                    RuleName = "GlossaryRule",
                                    GlossaryEntry = validationResult.GlossaryEntry
                                });
                            }

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
        /// Validates a single column name against glossary
        /// </summary>
        public ValidationResult ValidateColumnName(string physicalName)
        {
            if (string.IsNullOrEmpty(physicalName))
            {
                return ValidationResult.Invalid("Column name is empty", "EmptyName");
            }

            // Skip validation for default column names
            if (physicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                physicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Valid();
            }

            // Skip validation for "PLEASE CHANGE IT"
            if (physicalName.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Valid();
            }

            foreach (var rule in _validationRules)
            {
                var result = rule.Validate(physicalName);
                if (!result.IsValid)
                {
                    return result;
                }
                // If valid and has glossary entry, return it
                if (result.GlossaryEntry != null)
                {
                    return result;
                }
            }

            return ValidationResult.Valid();
        }

        /// <summary>
        /// Validates all columns and returns list of all results (valid and invalid)
        /// </summary>
        public List<ColumnValidationIssue> ValidateAllColumns()
        {
            var results = new List<ColumnValidationIssue>();

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return results;

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

                        // Skip special names
                        if (string.IsNullOrEmpty(physicalName) ||
                            physicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                            physicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase) ||
                            physicalName.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var result = ValidateColumnName(physicalName);

                        results.Add(new ColumnValidationIssue
                        {
                            TableName = tableName,
                            AttributeName = attrName,
                            PhysicalName = physicalName,
                            Issue = result.IsValid ? "Found in glossary" : result.Message,
                            RuleName = result.RuleName ?? "GlossaryRule",
                            IsValid = result.IsValid,
                            GlossaryEntry = result.GlossaryEntry
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ValidateAllColumns error: {ex.Message}");
            }

            return results;
        }

        public bool IsMonitoring => _isMonitoring;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
            _monitorTimer?.Dispose();
            _windowMonitorTimer?.Dispose();
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
        public GlossaryEntry GlossaryEntry { get; set; }
    }

    public class ColumnValidationIssue
    {
        public string TableName { get; set; }
        public string AttributeName { get; set; }
        public string PhysicalName { get; set; }
        public string Issue { get; set; }
        public string RuleName { get; set; }
        public bool IsValid { get; set; }
        public GlossaryEntry GlossaryEntry { get; set; }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }
        public string RuleName { get; set; }
        public GlossaryEntry GlossaryEntry { get; set; }

        public static ValidationResult Valid() => new ValidationResult { IsValid = true };
        public static ValidationResult ValidWithEntry(GlossaryEntry entry) => new ValidationResult { IsValid = true, GlossaryEntry = entry };
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
    /// Validates column names against GLOSSARY table
    /// </summary>
    public class GlossaryRule : IColumnNameRule
    {
        public string RuleName => "GlossaryRule";

        public ValidationResult Validate(string columnName)
        {
            if (string.IsNullOrEmpty(columnName))
                return ValidationResult.Valid();

            var glossary = GlossaryService.Instance;

            // Check if glossary is loaded
            if (!glossary.IsLoaded)
            {
                // Try to load
                glossary.LoadGlossary();
            }

            if (!glossary.IsLoaded)
            {
                // Could not load glossary - allow the column (fail open)
                return ValidationResult.Valid();
            }

            // Check if column name exists in glossary
            var entry = glossary.GetEntry(columnName);
            if (entry == null)
            {
                return ValidationResult.Invalid(
                    $"'{columnName}' not found in glossary. This column name is not allowed.",
                    RuleName);
            }

            // Valid - return with glossary entry for further processing
            return ValidationResult.ValidWithEntry(entry);
        }
    }

    #endregion
}
