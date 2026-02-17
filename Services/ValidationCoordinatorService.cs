using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
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
        private readonly dynamic _scapi;
        private Timer _monitorTimer;
        private Timer _windowMonitorTimer;
        private TableTypeMonitorService _tableTypeMonitor;
        private bool _isMonitoring;
        private bool _disposed;
        private bool _isProcessingChange;
        private bool _validationSuspended;
        private bool _popupVisible;
        private volatile bool _isCheckingForChanges;
        private bool _columnEditorWasOpen;
        private bool _sessionLost;

        // Batch processing state - process entities in small chunks to avoid UI blocking
        private int _scanEntityIndex;
        private bool _scanCycleActive;

        // Win32 API for window enumeration
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // Snapshot of all attributes
        private Dictionary<string, AttributeValidationSnapshot> _attributeSnapshots;

        // Cache of Domain ObjectId -> Domain Name
        private Dictionary<string, string> _domainCache;

        // Pending validation results to show in single popup
        private List<CollectedValidationResult> _pendingResults;

        // Monitor interval - short interval for smooth batch processing (5 entities per tick)
        private const int MonitorIntervalMs = 500;
        private const int MaxEntitiesPerTick = 5;

        // Event for logging
        public event Action<string> OnLog;

        // Event fired when session becomes invalid (model closed)
        public event Action OnSessionLost;

        public ValidationCoordinatorService(dynamic session, dynamic scapi)
        {
            _session = session;
            _scapi = scapi;
            _attributeSnapshots = new Dictionary<string, AttributeValidationSnapshot>();
            _domainCache = new Dictionary<string, string>();
            _pendingResults = new List<CollectedValidationResult>();

            _monitorTimer = new Timer();
            _monitorTimer.Interval = MonitorIntervalMs;
            _monitorTimer.Tick += MonitorTimer_Tick;

            _windowMonitorTimer = new Timer();
            _windowMonitorTimer.Interval = 2000;
            _windowMonitorTimer.Tick += WindowMonitorTimer_Tick;
        }

        #region Public Methods

        public void SetTableTypeMonitor(TableTypeMonitorService monitor)
        {
            _tableTypeMonitor = monitor;
        }

        public void StartMonitoring()
        {
            if (_isMonitoring) return;
            _isMonitoring = true;
            TakeSnapshot();
            _monitorTimer.Start();
            _windowMonitorTimer.Start();
            Log("ValidationCoordinatorService: Monitoring started");
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _monitorTimer.Stop();
            _windowMonitorTimer.Stop();
            Log("ValidationCoordinatorService: Monitoring stopped");
        }

        public void SuspendValidation()
        {
            _validationSuspended = true;
            _scanCycleActive = false;
            Log("ValidationCoordinatorService: Validation suspended");
        }

        public void ResumeValidation()
        {
            _validationSuspended = false;
            _scanCycleActive = false;
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

                try
                {
                    foreach (dynamic entity in allEntities)
                    {
                        if (entity == null) continue;

                        string tableName = GetTableName(entity);

                        dynamic entityAttrs = null;
                        try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                        catch (Exception ex) { Log($"TakeSnapshot: Failed to collect attributes for entity: {ex.Message}"); continue; }
                        if (entityAttrs == null) continue;

                        try
                        {
                            foreach (dynamic attr in entityAttrs)
                            {
                                if (attr == null) continue;

                                string objectId = "";
                                try { objectId = attr.ObjectId?.ToString() ?? ""; }
                                catch (Exception ex) { Log($"TakeSnapshot: Failed to get ObjectId: {ex.Message}"); continue; }

                                if (string.IsNullOrEmpty(objectId)) continue;

                                var snapshot = CreateSnapshot(attr, tableName, modelObjects);
                                _attributeSnapshots[objectId] = snapshot;
                            }
                        }
                        finally { ReleaseCom(entityAttrs); }
                    }
                }
                finally { ReleaseCom(allEntities); }

                Log($"ValidationCoordinatorService: Snapshot taken - {_attributeSnapshots.Count} attributes");
            }
            catch (Exception ex)
            {
                Log($"ValidationCoordinatorService.TakeSnapshot error: {ex.Message}");
            }
        }

        #endregion

        #region Timer Event

        /// <summary>
        /// Check if any model is still open via SCAPI root (safe — doesn't touch the session).
        /// Returns false if model was closed, triggering session lost without crashing.
        /// </summary>
        private bool IsModelStillOpen()
        {
            try
            {
                return _scapi.PersistenceUnits.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private void HandleSessionLost()
        {
            if (_sessionLost) return;
            _sessionLost = true;
            _isMonitoring = false;
            // Stop timers directly — don't call any COM methods
            try { _monitorTimer?.Stop(); } catch { }
            try { _windowMonitorTimer?.Stop(); } catch { }
            Log("Session lost - model was closed. Monitoring stopped.");
            try { OnSessionLost?.Invoke(); } catch { }
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private void MonitorTimer_Tick(object sender, EventArgs e)
        {
            if (_sessionLost || !_isMonitoring || _disposed || _isProcessingChange || _validationSuspended || _isCheckingForChanges) return;

            // Safety: check if model is still open BEFORE touching the session.
            // This avoids calling methods on a dead COM object (which causes native crash in erwin).
            if (!IsModelStillOpen()) { HandleSessionLost(); return; }

            try
            {
                _isCheckingForChanges = true;

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                try
                {
                    int entityCount = 0;
                    try { entityCount = allEntities.Count; }
                    catch (Exception ex) { Log($"MonitorTimer_Tick: Failed to get entity count: {ex.Message}"); return; }

                    // Start new scan cycle if not in progress
                    if (!_scanCycleActive)
                    {
                        _scanEntityIndex = 0;
                        _scanCycleActive = true;
                        _pendingResults.Clear();
                    }

                    // Process a small batch of entities using foreach with skip/limit
                    // (SCAPI Collect() collections don't support Item(i) indexed access)
                    int endIndex = Math.Min(_scanEntityIndex + MaxEntitiesPerTick, entityCount);
                    int currentIndex = 0;

                    foreach (dynamic entity in allEntities)
                    {
                        if (currentIndex >= endIndex) break;

                        if (currentIndex >= _scanEntityIndex)
                        {
                            if (entity != null)
                            {
                                CheckEntityForChanges(entity, modelObjects);
                            }
                        }

                        currentIndex++;
                    }

                    _scanEntityIndex = endIndex;

                    // Scan cycle complete - show results and run table type check
                    if (_scanEntityIndex >= entityCount)
                    {
                        _scanCycleActive = false;

                        if (_pendingResults.Count > 0)
                        {
                            ShowConsolidatedPopup();
                        }

                        // TABLE_TYPE check runs once per full cycle (entity-only, lightweight)
                        try { _tableTypeMonitor?.CheckForTableTypeChanges(allEntities); }
                        catch (Exception ex) { Log($"MonitorTimer_Tick: TableType check error: {ex.Message}"); }
                    }
                }
                finally
                {
                    ReleaseCom(allEntities);
                }
            }
            catch (COMException) { HandleSessionLost(); }
            catch (InvalidComObjectException) { HandleSessionLost(); }
            catch (Exception ex)
            {
                // Any COM-related error means session is dead
                if (ex is System.Runtime.InteropServices.ExternalException ||
                    ex.Message.Contains("RPC") || ex.Message.Contains("COM") ||
                    ex.Message.Contains("disconnected") || ex.Message.Contains("0x800"))
                    HandleSessionLost();
                else
                    System.Diagnostics.Debug.WriteLine($"ValidationCoordinatorService.MonitorTimer_Tick error: {ex.Message}");
            }
            finally
            {
                _isCheckingForChanges = false;
            }
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private void WindowMonitorTimer_Tick(object sender, EventArgs e)
        {
            if (_sessionLost || !_isMonitoring || _disposed) return;

            // Safety: check if model is still open BEFORE touching the session.
            if (!IsModelStillOpen()) { HandleSessionLost(); return; }

            try
            {
                bool editorIsOpen = IsColumnEditorOpen();

                if (_columnEditorWasOpen && !editorIsOpen)
                {
                    Log("Column Editor closed - checking for PLEASE CHANGE IT columns");
                    DeletePleaseChangeItColumns();
                    TakeSnapshot();
                }

                _columnEditorWasOpen = editorIsOpen;
            }
            catch (COMException) { HandleSessionLost(); }
            catch (InvalidComObjectException) { HandleSessionLost(); }
            catch (Exception ex)
            {
                if (ex is System.Runtime.InteropServices.ExternalException ||
                    ex.Message.Contains("RPC") || ex.Message.Contains("0x800"))
                    HandleSessionLost();
            }
        }

        private bool IsColumnEditorOpen()
        {
            bool found = false;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                StringBuilder title = new StringBuilder(256);
                GetWindowText(hWnd, title, 256);
                string windowTitle = title.ToString();

                if (windowTitle.Contains("Column") && windowTitle.Contains("Editor"))
                {
                    found = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private void DeletePleaseChangeItColumns()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                var columnsToDelete = new List<dynamic>();

                try
                {
                    foreach (dynamic entity in allEntities)
                    {
                        if (entity == null) continue;

                        dynamic entityAttrs = null;
                        try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                        catch (Exception ex) { Log($"DeletePleaseChangeIt: Failed to collect attributes: {ex.Message}"); continue; }
                        if (entityAttrs == null) continue;

                        try
                        {
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
                                catch (Exception ex) { Log($"DeletePleaseChangeIt: Failed to read attr name: {ex.Message}"); continue; }

                                if (physicalName.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
                                {
                                    columnsToDelete.Add(attr);
                                }
                            }
                        }
                        finally { ReleaseCom(entityAttrs); }
                    }
                }
                finally { ReleaseCom(allEntities); }

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
                                Log($"Deleted 'PLEASE CHANGE IT' column: {attrName}");
                            }
                            catch (Exception ex)
                            {
                                Log($"Failed to delete column: {ex.Message}");
                            }
                        }
                        _session.CommitTransaction(transId);
                        Log($"Deleted {columnsToDelete.Count} 'PLEASE CHANGE IT' column(s)");
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); }
                        catch (Exception rbEx) { Log($"DeletePleaseChangeIt: Rollback failed: {rbEx.Message}"); }
                        Log($"DeletePleaseChangeIt: Transaction failed: {ex.Message}");
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DeletePleaseChangeItColumns error: {ex.Message}");
            }
        }

        #endregion

        #region Change Detection

        private void CheckEntityForChanges(dynamic entity, dynamic modelObjects)
        {
            try
            {
                string tableName = GetTableName(entity);

                // Get predefined column names for this entity's TABLE_TYPE (to skip glossary validation)
                var predefinedColumnNames = GetPredefinedColumnNames(entity);

                dynamic entityAttrs = null;
                try { entityAttrs = modelObjects.Collect(entity, "Attribute"); }
                catch (Exception ex) { Log($"CheckEntityForChanges: Failed to collect attributes: {ex.Message}"); return; }
                if (entityAttrs == null) return;

                try
                {
                    foreach (dynamic attr in entityAttrs)
                    {
                        if (attr == null) continue;

                        string objectId = "";
                        try { objectId = attr.ObjectId?.ToString() ?? ""; }
                        catch (Exception ex) { Log($"CheckEntityForChanges: Failed to get ObjectId: {ex.Message}"); continue; }
                        if (string.IsNullOrEmpty(objectId)) continue;

                        var currentState = CreateSnapshot(attr, tableName, modelObjects);
                        bool isNew = !_attributeSnapshots.ContainsKey(objectId);

                        if (isNew)
                        {
                            // New attribute added
                            _attributeSnapshots[objectId] = currentState;
                            ProcessNewAttribute(attr, currentState, predefinedColumnNames);
                        }
                        else
                        {
                            var previousState = _attributeSnapshots[objectId];
                            ProcessAttributeChanges(attr, previousState, currentState, predefinedColumnNames);
                            _attributeSnapshots[objectId] = currentState;
                        }
                    }
                }
                finally { ReleaseCom(entityAttrs); }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckEntityForChanges error: {ex.Message}");
            }
        }

        private void ProcessNewAttribute(dynamic attr, AttributeValidationSnapshot currentState, HashSet<string> predefinedColumnNames)
        {
            _isProcessingChange = true;
            try
            {
                // New attribute
                bool hasValidDomain = IsValidDomain(currentState.DomainParentValue);

                if (hasValidDomain)
                {
                    // Domain is set -> only domain validation (skip glossary)
                    ValidateDomain(attr, currentState, null);
                }
                else
                {
                    // No domain -> glossary validation
                    ValidateGlossary(attr, currentState, predefinedColumnNames);
                }
            }
            finally
            {
                _isProcessingChange = false;
            }
        }

        private void ProcessAttributeChanges(dynamic attr, AttributeValidationSnapshot previousState, AttributeValidationSnapshot currentState, HashSet<string> predefinedColumnNames)
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
                if (physicalNameChanged)
                {
                    Log($"Physical name changed: {previousState.TableName}.{previousState.PhysicalName} -> {currentState.PhysicalName}");

                    if (hasValidDomain)
                    {
                        // Domain is set -> only domain validation (skip glossary)
                        ValidateDomain(attr, currentState, previousState.DomainParentValue);
                    }
                    else
                    {
                        // No domain -> glossary validation
                        ValidateGlossary(attr, currentState, predefinedColumnNames);
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

        private void ValidateGlossary(dynamic attr, AttributeValidationSnapshot state, HashSet<string> predefinedColumnNames)
        {
            // Skip special names
            if (string.IsNullOrEmpty(state.PhysicalName) ||
                state.PhysicalName.Equals("<default>", StringComparison.OrdinalIgnoreCase) ||
                state.PhysicalName.StartsWith("<default>", StringComparison.OrdinalIgnoreCase) ||
                state.PhysicalName.Equals("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Skip predefined columns - they are auto-added by TABLE_TYPE and don't need glossary validation
            if (predefinedColumnNames != null && predefinedColumnNames.Contains(state.PhysicalName))
            {
                Log($"Glossary validation skipped (predefined column): {state.TableName}.{state.PhysicalName}");
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
                    Message = "Column name not found in glossary. Please use a column name from the glossary.",
                    Attribute = attr,
                    ObjectId = state.ObjectId
                });
            }
            else
            {
                Log($"Glossary validation passed: {state.TableName}.{state.PhysicalName}");

                // Apply DataType and Owner from glossary entry
                if (attr != null)
                {
                    ApplyGlossaryProperties(attr, entry);
                }
            }
        }

        private void ApplyGlossaryProperties(dynamic attr, GlossaryEntry glossaryEntry)
        {
            try
            {
                int transId = _session.BeginNamedTransaction("ApplyGlossaryProperties");
                try
                {
                    // Physical_Data_Type
                    if (!string.IsNullOrEmpty(glossaryEntry.DataType))
                    {
                        TrySetProperty(attr, "Physical_Data_Type", glossaryEntry.DataType);
                    }

                    // UDP'ler: OWNER, KVKK, PCIDSS, CLASSIFICATION (internal erwin property)
                    TrySetUdp(attr, "OWNER", glossaryEntry.Owner);
                    TrySetUdp(attr, "KVKK", glossaryEntry.Kvkk ? "True" : "False");
                    TrySetUdp(attr, "PCIDSS", glossaryEntry.Pcidss ? "True" : "False");
                    TrySetUdp(attr, "CLASSIFICATION", glossaryEntry.Classification);

                    _session.CommitTransaction(transId);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"ApplyGlossaryProperties: Rollback failed: {rbEx.Message}"); }
                    Log($"Error applying glossary properties: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"ApplyGlossaryProperties error: {ex.Message}");
            }
        }

        private void TrySetProperty(dynamic attr, string propertyName, string value)
        {
            try
            {
                attr.Properties(propertyName).Value = value;
                Log($"Set {propertyName} to '{value}' from glossary");
            }
            catch (Exception ex)
            {
                Log($"Error setting {propertyName}: {ex.Message}");
            }
        }

        private void TrySetUdp(dynamic attr, string udpName, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            string[] formats = {
                $"Attribute.Physical.{udpName}",
                udpName,
                $"Attribute.{udpName}",
                $"UDP.{udpName}"
            };

            foreach (var format in formats)
            {
                try
                {
                    attr.Properties(format).Value = value;
                    Log($"Set {udpName} to '{value}' using '{format}'");
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TrySetUdp: Format '{format}' failed: {ex.Message}");
                }
            }

            Log($"Could not set {udpName} - no valid property format found");
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
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"ApplyDomainProperties: Rollback failed: {rbEx.Message}"); }
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

                // After user clicks OK, rename glossary-failed columns to "PLEASE CHANGE IT"
                RenameInvalidGlossaryColumns(glossaryResults);
            }
            finally
            {
                _popupVisible = false;
                _pendingResults.Clear();
            }
        }

        private void RenameInvalidGlossaryColumns(List<CollectedValidationResult> glossaryResults)
        {
            if (glossaryResults.Count == 0) return;

            try
            {
                int transId = _session.BeginNamedTransaction("RenameInvalidColumns");
                try
                {
                    foreach (var result in glossaryResults)
                    {
                        if (result.Attribute == null) continue;

                        try
                        {
                            // Both logical and physical name must be set,
                            // otherwise CheckForChanges reads stale Physical_Name and loops
                            result.Attribute.Properties("Name").Value = "PLEASE CHANGE IT";
                            try { result.Attribute.Properties("Physical_Name").Value = "PLEASE CHANGE IT"; }
                            catch (Exception phEx) { Log($"RenameInvalidGlossary: Failed to set Physical_Name: {phEx.Message}"); }
                            Log($"Renamed column {result.TableName}.{result.ColumnName} to 'PLEASE CHANGE IT'");

                            // Update snapshot so next cycle doesn't re-trigger validation
                            if (!string.IsNullOrEmpty(result.ObjectId) && _attributeSnapshots.ContainsKey(result.ObjectId))
                            {
                                _attributeSnapshots[result.ObjectId].PhysicalName = "PLEASE CHANGE IT";
                                _attributeSnapshots[result.ObjectId].AttributeName = "PLEASE CHANGE IT";
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error renaming column {result.ColumnName}: {ex.Message}");
                        }
                    }

                    _session.CommitTransaction(transId);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"RenameInvalidGlossary: Rollback failed: {rbEx.Message}"); }
                    Log($"RenameInvalidGlossaryColumns transaction error: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"RenameInvalidGlossaryColumns error: {ex.Message}");
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

            try { objectId = attr.ObjectId?.ToString() ?? ""; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CreateSnapshot: ObjectId error: {ex.Message}"); }
            try { attrName = attr.Name ?? ""; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CreateSnapshot: Name error: {ex.Message}"); }
            try
            {
                string physCol = attr.Properties("Physical_Name").Value?.ToString() ?? "";
                physicalName = (!string.IsNullOrEmpty(physCol) && !physCol.StartsWith("%")) ? physCol : attrName;
            }
            catch (Exception ex) { physicalName = attrName; System.Diagnostics.Debug.WriteLine($"CreateSnapshot: Physical_Name error: {ex.Message}"); }

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

            try { entityName = entity.Name ?? ""; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetTableName: Name error: {ex.Message}"); }
            try
            {
                string physTable = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                tableName = (!string.IsNullOrEmpty(physTable) && !physTable.StartsWith("%")) ? physTable : entityName;
            }
            catch (Exception ex) { tableName = entityName; System.Diagnostics.Debug.WriteLine($"GetTableName: Physical_Name error: {ex.Message}"); }

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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetDomainParentValue error: {ex.Message}"); }

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
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildDomainCache: Domain entry error: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log($"BuildDomainCache error: {ex.Message}"); }
        }

        /// <summary>
        /// Gets predefined column names for the entity's TABLE_TYPE.
        /// Returns null if entity has no TABLE_TYPE or predefined columns not loaded.
        /// </summary>
        private HashSet<string> GetPredefinedColumnNames(dynamic entity)
        {
            try
            {
                string tableTypeValue = "";
                try { tableTypeValue = entity.Properties("Entity.Physical.TABLE_TYPE").Value?.ToString() ?? ""; }
                catch { return null; }

                if (string.IsNullOrEmpty(tableTypeValue) || tableTypeValue == "(SELECT)")
                    return null;

                var tableTypeEntry = TableTypeService.Instance.GetByName(tableTypeValue);
                if (tableTypeEntry == null)
                    return null;

                if (!PredefinedColumnService.Instance.IsLoaded)
                    PredefinedColumnService.Instance.LoadPredefinedColumns();

                var predefinedColumns = PredefinedColumnService.Instance.GetByTableTypeId(tableTypeEntry.Id);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in predefinedColumns)
                {
                    names.Add(col.Name);
                }

                return names.Count > 0 ? names : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetPredefinedColumnNames error: {ex.Message}");
                return null;
            }
        }

        private bool IsValidDomain(string domainValue)
        {
            return !string.IsNullOrEmpty(domainValue) &&
                   domainValue != "(SELECT)" &&
                   domainValue != "<default>" &&
                   domainValue != "Blob";
        }

        private static void ReleaseCom(object comObject)
        {
            if (comObject != null)
            {
                try { Marshal.ReleaseComObject(comObject); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReleaseCom error: {ex.Message}"); }
            }
        }

        private void Log(string message)
        {
            if (_disposed) return;
            try { OnLog?.Invoke(message); } catch { }
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
            _windowMonitorTimer?.Dispose();
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
        public dynamic Attribute { get; set; }
        public string ObjectId { get; set; }
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
