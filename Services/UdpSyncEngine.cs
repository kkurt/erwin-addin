#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Snapshot of a single UDP definition as it lives in the admin DB
    /// (MC_UDP_DEFINITION + MC_UDP_LIST_OPTION). Unlike
    /// <see cref="UdpDefinitionRuntime"/>, this snapshot includes tombstoned
    /// (IS_DELETED=1) rows so that <see cref="UdpSyncEngine.ComputeDiff"/> can
    /// emit Delete entries when the admin removes a UDP.
    /// </summary>
    public class UdpDefinitionSnapshot
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string ObjectType { get; set; } = "";
        public string UdpType { get; set; } = "";
        public string DefaultValue { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsDeleted { get; set; }
        public List<UdpListOptionSnapshot> ListOptions { get; set; } = new List<UdpListOptionSnapshot>();
    }

    /// <summary>
    /// Single list-option row for a List-type UDP. Mirrors
    /// <see cref="UdpListOption"/> but lives on the snapshot side.
    /// </summary>
    public class UdpListOptionSnapshot
    {
        public string Value { get; set; } = "";
        public string DisplayText { get; set; } = "";
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Snapshot of one erwin metamodel Property_Type as it currently exists in
    /// the active model. Built by <see cref="UdpSyncEngine.WalkModelUdps"/>.
    /// </summary>
    public class ModelUdpSnapshot
    {
        /// <summary>Three-part canonical name, e.g. "Entity.Physical.OWNER".</summary>
        public string FullName { get; set; } = "";

        /// <summary>Owner class as encoded in the Property_Type, e.g. "Entity".</summary>
        public string OwnerClass { get; set; } = "";

        /// <summary>UDP name only (last segment), e.g. "OWNER".</summary>
        public string UdpName { get; set; } = "";

        /// <summary>Current tag_Udp_Data_Type value (1=Integer .. 6=List).</summary>
        public int CurrentDataTypeId { get; set; }

        /// <summary>Current tag_Udp_Default_Value as a string.</summary>
        public string CurrentDefault { get; set; } = "";

        /// <summary>
        /// Current tag_Udp_Values_List string (comma-separated for List type).
        /// Empty for non-list UDPs.
        /// </summary>
        public string CurrentListValues { get; set; } = "";

        /// <summary>Current Definition field (admin description, sentinel-free).</summary>
        public string CurrentDescription { get; set; } = "";
    }

    /// <summary>Diff action emitted by <see cref="UdpSyncEngine.ComputeDiff"/>.</summary>
    public enum UdpDiffAction
    {
        Create,
        Update,
        Delete
    }

    /// <summary>Per-field change flags inside an Update entry.</summary>
    [Flags]
    public enum UdpUpdateChanges
    {
        None = 0,
        Type = 1,
        Default = 2,
        ListValues = 4,
        Description = 8
    }

    /// <summary>
    /// One row of the diff: a single Create / Update / Delete operation.
    /// Carries enough context for the dialog to render a human-readable
    /// description and for the Apply path to mutate the metamodel.
    /// </summary>
    public class UdpDiffEntry
    {
        public UdpDiffAction Action { get; set; }

        /// <summary>Three-part canonical name, e.g. "Entity.Physical.OWNER".</summary>
        public string FullName { get; set; } = "";

        /// <summary>UDP name only (last segment), for popup display.</summary>
        public string UdpName { get; set; } = "";

        /// <summary>Admin's object type string, e.g. "Table", "Column".</summary>
        public string ObjectType { get; set; } = "";

        /// <summary>The admin row driving Create / Update. Null only for orphan-Delete (currently not emitted).</summary>
        public UdpDefinitionSnapshot? AdminUdp { get; set; }

        /// <summary>The existing model UDP being updated/deleted. Null for Create.</summary>
        public ModelUdpSnapshot? ExistingUdp { get; set; }

        /// <summary>Which fields changed (Update only).</summary>
        public UdpUpdateChanges Changes { get; set; }

        /// <summary>
        /// True when an Update requires delete+recreate because the type change
        /// is not in-place compatible. Carries the "values will be lost" warning
        /// to the dialog. Conservative default during Phase 1: any TypeChange
        /// sets this true (see <see cref="ErwinSupportsInPlaceTypeChange"/>).
        /// </summary>
        public bool RecreatesValues { get; set; }

        /// <summary>One-line human-readable summary for the dialog row.</summary>
        public string Details { get; set; } = "";
    }

    /// <summary>Result of a diff computation.</summary>
    public class UdpDiff
    {
        public List<UdpDiffEntry> Creates { get; set; } = new List<UdpDiffEntry>();
        public List<UdpDiffEntry> Updates { get; set; } = new List<UdpDiffEntry>();
        public List<UdpDiffEntry> Deletes { get; set; } = new List<UdpDiffEntry>();

        public int TotalCount => Creates.Count + Updates.Count + Deletes.Count;
        public bool IsEmpty => TotalCount == 0;
        public IEnumerable<UdpDiffEntry> AllEntries => Creates.Concat(Updates).Concat(Deletes);
    }

    /// <summary>
    /// Sync orchestrator for admin UDP definitions vs. the active erwin model.
    /// Three responsibilities:
    ///   1. <see cref="FetchSnapshot"/> - read admin DB (includes IS_DELETED rows).
    ///   2. <see cref="WalkModelUdps"/> - walk metamodel Property_Type entries.
    ///   3. <see cref="ComputeDiff"/> (static, pure) - produce a Create/Update/Delete list.
    /// Apply path (metamodel mutation) lands in Phase 3 - this class only
    /// observes the model in Phase 1.
    /// </summary>
    public class UdpSyncEngine
    {
        private readonly dynamic _session;
        private readonly dynamic _scapi;
        private readonly dynamic _currentModel;
        private readonly int _configId;

        public event Action<string>? OnLog;

        public UdpSyncEngine(dynamic session, dynamic scapi, dynamic currentModel, int configId)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
            _currentModel = currentModel ?? throw new ArgumentNullException(nameof(currentModel));
            _configId = configId;
        }

        #region Object-type / data-type mappings

        /// <summary>
        /// Map admin's MC_UDP_DEFINITION.OBJECT_TYPE string to erwin's SCAPI
        /// owner-class name. Mirrors <c>UdpRuntimeService.GetScapiOwnerClass</c>
        /// so that the diff sees the same fully-qualified Property_Type names.
        /// Unknown object types return null - caller skips them.
        /// </summary>
        public static string? MapObjectTypeToOwnerClass(string objectType)
        {
            if (string.IsNullOrEmpty(objectType)) return null;
            switch (objectType.Trim().ToLowerInvariant())
            {
                case "table": return "Entity";
                case "column": return "Attribute";
                case "view": return "View";
                case "procedure": return "Stored_Procedure";
                case "model": return "Model";
                case "subject area": return "Subject_Area";
                default: return null;
            }
        }

        /// <summary>
        /// Map admin UDP_TYPE to erwin metamodel tag_Udp_Data_Type integer.
        /// 1=Integer, 2=Text, 3=Date, 4=Command, 5=Real, 6=List. Mirrors
        /// <c>UdpRuntimeService.MapUdpTypeToErwinDataTypeId</c>.
        /// </summary>
        public static int MapUdpTypeToErwinDataTypeId(string udpType)
        {
            if (string.IsNullOrEmpty(udpType)) return 2; // Text fallback
            switch (udpType.Trim().ToLowerInvariant())
            {
                case "int":
                case "integer":
                    return 1;
                case "text":
                    return 2;
                case "date":
                case "datetime":
                    return 3;
                case "command":
                    return 4;
                case "real":
                case "float":
                case "decimal":
                    return 5;
                case "list":
                    return 6;
                default:
                    return 2;
            }
        }

        /// <summary>
        /// Whether erwin r10.10 supports an in-place change between two
        /// metamodel datatype ids. Phase 1 always returns false - every type
        /// change is treated as delete+recreate (with the values-lost warning
        /// in the dialog). The Phase 3 spike will widen this once specific
        /// transitions are empirically verified.
        /// </summary>
        public static bool ErwinSupportsInPlaceTypeChange(int fromTypeId, int toTypeId)
        {
            if (fromTypeId == toTypeId) return true;
            return false;
        }

        #endregion

        #region Snapshot fetch (admin DB)

        /// <summary>
        /// Read every MC_UDP_DEFINITION row for the bound config, INCLUDING
        /// soft-deleted rows. Joined with MC_UDP_LIST_OPTION so List UDPs come
        /// back with their options. Returns an empty list (and logs) when the
        /// DB is not configured.
        /// </summary>
        public List<UdpDefinitionSnapshot> FetchSnapshot()
        {
            var result = new List<UdpDefinitionSnapshot>();
            if (!DatabaseService.Instance.IsConfigured)
            {
                Log("UdpSyncEngine.FetchSnapshot: DB not configured - returning empty snapshot");
                return result;
            }

            string dbType = DatabaseService.Instance.GetDbType();
            string query = BuildSnapshotQuery(dbType);

            var byId = new Dictionary<int, UdpDefinitionSnapshot>();

            using (var connection = DatabaseService.Instance.CreateConnection())
            {
                connection.Open();
                using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                {
                    var pCfg = command.CreateParameter();
                    pCfg.ParameterName = dbType?.ToUpperInvariant() == "ORACLE" ? ":cfgId" : "@cfgId";
                    pCfg.Value = _configId;
                    command.Parameters.Add(pCfg);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int defId = Convert.ToInt32(reader["DEF_ID"]);

                            if (!byId.TryGetValue(defId, out var def))
                            {
                                def = new UdpDefinitionSnapshot
                                {
                                    Id = defId,
                                    Name = reader["NAME"]?.ToString()?.Trim() ?? "",
                                    ObjectType = reader["OBJECT_TYPE"]?.ToString()?.Trim() ?? "",
                                    UdpType = reader["UDP_TYPE"]?.ToString()?.Trim() ?? "",
                                    DefaultValue = reader["DEFAULT_VALUE"]?.ToString() ?? "",
                                    Description = reader["DESCRIPTION"]?.ToString() ?? "",
                                    IsDeleted = Convert.ToBoolean(reader["IS_DELETED"]),
                                };
                                byId[defId] = def;
                                result.Add(def);
                            }

                            if (reader["OPT_VALUE"] != DBNull.Value)
                            {
                                string optValue = reader["OPT_VALUE"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(optValue))
                                {
                                    def.ListOptions.Add(new UdpListOptionSnapshot
                                    {
                                        Value = optValue,
                                        DisplayText = reader["OPT_DISPLAY"]?.ToString() ?? optValue,
                                        SortOrder = reader["OPT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["OPT_ORDER"])
                                    });
                                }
                            }
                        }
                    }
                }
            }

            // Stable list-option ordering for downstream comparison.
            foreach (var def in result)
                def.ListOptions = def.ListOptions.OrderBy(o => o.SortOrder).ToList();

            Log($"UdpSyncEngine.FetchSnapshot: {result.Count} definition(s) loaded for config {_configId} (incl. tombstones)");
            return result;
        }

        private static string BuildSnapshotQuery(string? dbType)
        {
            // Mirrors UdpDefinitionService.GetDefinitionQuery shape but WITHOUT
            // the IS_DELETED filter - the sync engine must see tombstones to
            // emit Delete diff entries.
            switch (dbType?.ToUpperInvariant())
            {
                case "POSTGRESQL":
                    return @"SELECT d.""ID"" AS ""DEF_ID"", d.""NAME"", d.""DESCRIPTION"", d.""OBJECT_TYPE"", d.""UDP_TYPE"",
                            d.""DEFAULT_VALUE"", d.""IS_DELETED"",
                            o.""VALUE"" AS ""OPT_VALUE"", o.""DISPLAY_TEXT"" AS ""OPT_DISPLAY"", o.""SORT_ORDER"" AS ""OPT_ORDER""
                            FROM ""MC_UDP_DEFINITION"" d
                            LEFT JOIN ""MC_UDP_LIST_OPTION"" o ON o.""UDP_DEFINITION_ID"" = d.""ID""
                            WHERE d.""CONFIG_ID"" = @cfgId
                            ORDER BY d.""ID"", o.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT d.ID AS DEF_ID, d.NAME, d.DESCRIPTION, d.OBJECT_TYPE, d.UDP_TYPE,
                            d.DEFAULT_VALUE, d.IS_DELETED,
                            o.VALUE AS OPT_VALUE, o.DISPLAY_TEXT AS OPT_DISPLAY, o.SORT_ORDER AS OPT_ORDER
                            FROM MC_UDP_DEFINITION d
                            LEFT JOIN MC_UDP_LIST_OPTION o ON o.UDP_DEFINITION_ID = d.ID
                            WHERE d.CONFIG_ID = :cfgId
                            ORDER BY d.ID, o.SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT d.[ID] AS [DEF_ID], d.[NAME], d.[DESCRIPTION], d.[OBJECT_TYPE], d.[UDP_TYPE],
                            d.[DEFAULT_VALUE], d.[IS_DELETED],
                            o.[VALUE] AS [OPT_VALUE], o.[DISPLAY_TEXT] AS [OPT_DISPLAY], o.[SORT_ORDER] AS [OPT_ORDER]
                            FROM [dbo].[MC_UDP_DEFINITION] d
                            LEFT JOIN [dbo].[MC_UDP_LIST_OPTION] o ON o.[UDP_DEFINITION_ID] = d.[ID]
                            WHERE d.[CONFIG_ID] = @cfgId
                            ORDER BY d.[ID], o.[SORT_ORDER]";
            }
        }

        #endregion

        #region Model walk (erwin metamodel)

        /// <summary>
        /// Open a level-1 metamodel session and read every Property_Type entry
        /// into a <see cref="ModelUdpSnapshot"/> map keyed by FullName.
        /// Caller may pass a pre-collected name set to skip the metamodel walk
        /// when it has already been done in the same connect cycle (e.g. by
        /// <c>ModelConfigForm.EnsureAllUdpsExist</c>).
        /// </summary>
        public Dictionary<string, ModelUdpSnapshot> WalkModelUdps()
        {
            var map = new Dictionary<string, ModelUdpSnapshot>(StringComparer.OrdinalIgnoreCase);
            dynamic? metamodelSession = null;
            try
            {
                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // SCD_SL_M1 = Metamodel level

                dynamic mmObjects = metamodelSession.ModelObjects;
                dynamic mmRoot = mmObjects.Root;

                dynamic propertyTypes;
                try { propertyTypes = mmObjects.Collect(mmRoot, "Property_Type"); }
                catch (Exception ex)
                {
                    Log($"UdpSyncEngine.WalkModelUdps: Property_Type collect failed: {ex.Message}");
                    return map;
                }

                foreach (dynamic pt in propertyTypes)
                {
                    if (pt == null) continue;
                    string fullName;
                    try { fullName = pt.Name ?? ""; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(fullName)) continue;

                    var snap = new ModelUdpSnapshot
                    {
                        FullName = fullName,
                        OwnerClass = ExtractOwnerClass(fullName),
                        UdpName = ExtractUdpName(fullName),
                        CurrentDataTypeId = ReadIntProperty(pt, "tag_Udp_Data_Type"),
                        CurrentDefault = ReadStringProperty(pt, "tag_Udp_Default_Value"),
                        CurrentListValues = ReadStringProperty(pt, "tag_Udp_Values_List"),
                        CurrentDescription = ReadStringProperty(pt, "Definition"),
                    };
                    map[fullName] = snap;
                }

                Log($"UdpSyncEngine.WalkModelUdps: {map.Count} Property_Type entry/entries collected");
            }
            catch (Exception ex)
            {
                Log($"UdpSyncEngine.WalkModelUdps error: {ex.Message}");
            }
            finally
            {
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); }
                    catch (Exception ex) { Log($"UdpSyncEngine.WalkModelUdps: metamodel session close error: {ex.Message}"); }
                }
            }
            return map;
        }

        private static string ExtractOwnerClass(string fullName)
        {
            int dot = fullName.IndexOf('.');
            return dot > 0 ? fullName.Substring(0, dot) : "";
        }

        private static string ExtractUdpName(string fullName)
        {
            int last = fullName.LastIndexOf('.');
            return last >= 0 && last < fullName.Length - 1 ? fullName.Substring(last + 1) : fullName;
        }

        private static int ReadIntProperty(dynamic pt, string propertyName)
        {
            try
            {
                var raw = pt.Properties(propertyName)?.Value;
                if (raw == null) return 0;
                return Convert.ToInt32(raw);
            }
            catch { return 0; }
        }

        private static string ReadStringProperty(dynamic pt, string propertyName)
        {
            try
            {
                var raw = pt.Properties(propertyName)?.Value;
                return raw?.ToString() ?? "";
            }
            catch { return ""; }
        }

        #endregion

        #region Diff (pure)

        /// <summary>
        /// Compute the diff between admin's snapshot and the model's current
        /// state. Pure function: no SCAPI, no DB - safe to unit test.
        /// Matching is by canonical name <c>{OwnerClass}.Physical.{Name}</c>.
        /// </summary>
        /// <remarks>
        /// Trade-offs locked by the plan:
        /// - No owner-tag sentinel: rename is not detectable. Admin must do
        ///   delete+create if they want to rename a UDP.
        /// - Name collisions (user-created UDP vs. admin definition) match by
        ///   name -> diff Update emitted -> Apply may overwrite the user's
        ///   field shape. Prevented by deployment convention.
        /// - Orphans (model UDPs not in admin snapshot) are NEVER emitted as
        ///   Delete - they belong to the user.
        /// </remarks>
        public static UdpDiff ComputeDiff(
            IReadOnlyList<UdpDefinitionSnapshot> snapshot,
            IReadOnlyDictionary<string, ModelUdpSnapshot> modelMap)
        {
            var diff = new UdpDiff();
            if (snapshot == null || snapshot.Count == 0)
                return diff;

            var modelLookup = modelMap ?? new Dictionary<string, ModelUdpSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (var adminUdp in snapshot)
            {
                string? ownerClass = MapObjectTypeToOwnerClass(adminUdp.ObjectType);
                if (ownerClass == null)
                {
                    // Unknown object type - cannot project to a Property_Type
                    // name. Silently skip; admin-side schema additions land
                    // here until the addin supports them.
                    continue;
                }

                string fullName = $"{ownerClass}.Physical.{adminUdp.Name}";
                modelLookup.TryGetValue(fullName, out var match);

                if (adminUdp.IsDeleted)
                {
                    if (match != null)
                    {
                        diff.Deletes.Add(new UdpDiffEntry
                        {
                            Action = UdpDiffAction.Delete,
                            FullName = fullName,
                            UdpName = adminUdp.Name,
                            ObjectType = adminUdp.ObjectType,
                            AdminUdp = adminUdp,
                            ExistingUdp = match,
                            Details = "(admin removed)"
                        });
                    }
                    // else: already absent on both sides - no-op
                    continue;
                }

                if (match == null)
                {
                    diff.Creates.Add(new UdpDiffEntry
                    {
                        Action = UdpDiffAction.Create,
                        FullName = fullName,
                        UdpName = adminUdp.Name,
                        ObjectType = adminUdp.ObjectType,
                        AdminUdp = adminUdp,
                        Details = BuildCreateDetails(adminUdp)
                    });
                    continue;
                }

                int expectedType = MapUdpTypeToErwinDataTypeId(adminUdp.UdpType);
                UdpUpdateChanges changes = UdpUpdateChanges.None;
                var detailParts = new List<string>();

                if (match.CurrentDataTypeId != expectedType)
                {
                    changes |= UdpUpdateChanges.Type;
                    detailParts.Add($"Type: {DataTypeIdToLabel(match.CurrentDataTypeId)} -> {DataTypeIdToLabel(expectedType)}");
                }

                string adminDefault = adminUdp.DefaultValue ?? "";
                string modelDefault = match.CurrentDefault ?? "";
                if (!string.Equals(adminDefault, modelDefault, StringComparison.Ordinal))
                {
                    changes |= UdpUpdateChanges.Default;
                    detailParts.Add($"Default: '{Truncate(modelDefault, 24)}' -> '{Truncate(adminDefault, 24)}'");
                }

                if (string.Equals(adminUdp.UdpType, "List", StringComparison.OrdinalIgnoreCase))
                {
                    string expectedList = string.Join(",", adminUdp.ListOptions.Select(o => o.Value));
                    string modelList = match.CurrentListValues ?? "";
                    if (!string.Equals(expectedList, modelList, StringComparison.Ordinal))
                    {
                        changes |= UdpUpdateChanges.ListValues;
                        detailParts.Add("List options changed");
                    }
                }

                string adminDesc = adminUdp.Description ?? "";
                string modelDesc = match.CurrentDescription ?? "";
                if (!string.Equals(adminDesc, modelDesc, StringComparison.Ordinal))
                {
                    changes |= UdpUpdateChanges.Description;
                    detailParts.Add("Description changed");
                }

                if (changes == UdpUpdateChanges.None)
                    continue;

                bool recreates = (changes & UdpUpdateChanges.Type) != 0
                                 && !ErwinSupportsInPlaceTypeChange(match.CurrentDataTypeId, expectedType);
                if (recreates)
                    detailParts.Add("will recreate, existing values will be lost");

                diff.Updates.Add(new UdpDiffEntry
                {
                    Action = UdpDiffAction.Update,
                    FullName = fullName,
                    UdpName = adminUdp.Name,
                    ObjectType = adminUdp.ObjectType,
                    AdminUdp = adminUdp,
                    ExistingUdp = match,
                    Changes = changes,
                    RecreatesValues = recreates,
                    Details = string.Join("; ", detailParts)
                });
            }

            return diff;
        }

        private static string BuildCreateDetails(UdpDefinitionSnapshot adminUdp)
        {
            string typeLabel = adminUdp.UdpType ?? "Text";
            string defaultPart = string.IsNullOrEmpty(adminUdp.DefaultValue)
                ? ""
                : $", default '{Truncate(adminUdp.DefaultValue, 24)}'";
            string listPart = adminUdp.ListOptions.Count > 0
                ? $", {adminUdp.ListOptions.Count} option(s)"
                : "";
            return $"{typeLabel}{defaultPart}{listPart}";
        }

        private static string DataTypeIdToLabel(int dataTypeId)
        {
            switch (dataTypeId)
            {
                case 1: return "Integer";
                case 2: return "Text";
                case 3: return "Date";
                case 4: return "Command";
                case 5: return "Real";
                case 6: return "List";
                default: return $"Unknown({dataTypeId})";
            }
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen - 1) + "...";
        }

        #endregion

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
