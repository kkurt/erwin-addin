#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Snapshot of a single UDP definition as it lives in the admin DB
    /// (MC_UDP_DEFINITION + MC_UDP_LIST_OPTION). Admin-side removal is a
    /// plain DELETE on the row - no tombstone column - so a missing row in
    /// the snapshot does NOT mean "user should remove this from the model".
    /// See <see cref="UdpSyncEngine.ComputeDiff"/> for the orphan-untouched
    /// rule.
    /// </summary>
    public class UdpDefinitionSnapshot
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string ObjectType { get; set; } = "";
        public string UdpType { get; set; } = "";
        public string DefaultValue { get; set; } = "";
        public string Description { get; set; } = "";
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
    /// One row of the diff: a single Create or Update operation. Carries
    /// enough context for the dialog to render a human-readable description
    /// and for the Apply path to mutate the metamodel.
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

        /// <summary>The admin row driving Create / Update.</summary>
        public UdpDefinitionSnapshot? AdminUdp { get; set; }

        /// <summary>The existing model UDP being updated. Null for Create.</summary>
        public ModelUdpSnapshot? ExistingUdp { get; set; }

        /// <summary>Which fields changed (Update only).</summary>
        public UdpUpdateChanges Changes { get; set; }

        /// <summary>One-line human-readable summary for the dialog row.</summary>
        public string Details { get; set; } = "";
    }

    /// <summary>
    /// Outcome of <see cref="UdpSyncEngine.Apply"/>. Carries success flag,
    /// per-action counts (so caller can log "created=N, updated=M"), and an
    /// error message when the transaction had to roll back.
    /// </summary>
    public class ApplyResult
    {
        public bool Success { get; set; }
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public string Error { get; set; } = "";

        public static ApplyResult Ok(int created, int updated) =>
            new ApplyResult { Success = true, CreatedCount = created, UpdatedCount = updated };

        public static ApplyResult Fail(string error) =>
            new ApplyResult { Success = false, Error = error };
    }

    /// <summary>Result of a diff computation. Holds Create + Update entries
    /// only - the sync feature never emits Delete (see <see cref="UdpSyncEngine"/>).</summary>
    public class UdpDiff
    {
        public List<UdpDiffEntry> Creates { get; set; } = new List<UdpDiffEntry>();
        public List<UdpDiffEntry> Updates { get; set; } = new List<UdpDiffEntry>();

        public int TotalCount => Creates.Count + Updates.Count;
        public bool IsEmpty => TotalCount == 0;
        public IEnumerable<UdpDiffEntry> AllEntries => Creates.Concat(Updates);
    }

    /// <summary>
    /// Sync orchestrator for admin UDP definitions vs. the active erwin model.
    /// Four responsibilities:
    ///   1. <see cref="FetchSnapshot"/> - read admin DB.
    ///   2. <see cref="WalkModelUdps"/> - walk metamodel Property_Type entries.
    ///   3. <see cref="ComputeDiff"/> (static, pure) - produce Create + Update entries.
    ///   4. <see cref="Apply"/> - mutate the metamodel inside a single transaction.
    /// Note: Delete entries are never emitted - admin schema has no tombstone
    /// column and silent removal would risk destroying user-authored UDPs.
    /// Users delete UDPs themselves through erwin's UDP editor.
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

        #endregion

        #region Snapshot fetch (admin DB)

        /// <summary>
        /// Read every MC_UDP_DEFINITION row for the bound config, joined with
        /// MC_UDP_LIST_OPTION so List UDPs come back with their options.
        /// Returns an empty list (and logs) when the DB is not configured.
        ///
        /// Note (2026-05-16): the admin schema does NOT carry an IS_DELETED
        /// tombstone column. Admin-side UDP removal is a plain DELETE - the
        /// row simply disappears from the snapshot. We deliberately do NOT
        /// emit Delete entries for "row missing from snapshot but present in
        /// model" because that is indistinguishable from "user manually
        /// created a UDP the admin never knew about" and we would silently
        /// destroy user data on every connect. Deletes are user-driven only.
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

            foreach (var def in result)
            {
                def.ListOptions = def.ListOptions.OrderBy(o => o.SortOrder).ToList();
                NormalizeBooleanToList(def);
            }

            Log($"UdpSyncEngine.FetchSnapshot: {result.Count} definition(s) loaded for config {_configId}");
            return result;
        }

        /// <summary>
        /// Admin's <c>Boolean</c> UDP type has no native erwin counterpart -
        /// the metamodel has no Bit/Boolean tag_Udp_Data_Type. The convention
        /// is to surface it as a two-value List (True / False) so users get a
        /// dropdown in the Column Editor. Normalizing at the snapshot
        /// boundary keeps every downstream layer (ComputeDiff, Apply,
        /// UdpSyncDialog) Boolean-unaware - they only ever see List.
        /// Public so unit tests can exercise it without a live DB.
        /// </summary>
        public static void NormalizeBooleanToList(UdpDefinitionSnapshot def)
        {
            if (!string.Equals(def.UdpType, "Boolean", StringComparison.OrdinalIgnoreCase))
                return;
            def.UdpType = "List";
            // Wipe any list options the DB might have (a Boolean row should
            // not have MC_UDP_LIST_OPTION rows, but defensive) and seed the
            // canonical True/False set.
            def.ListOptions = new List<UdpListOptionSnapshot>
            {
                new UdpListOptionSnapshot { Value = "True",  DisplayText = "True",  SortOrder = 0 },
                new UdpListOptionSnapshot { Value = "False", DisplayText = "False", SortOrder = 1 },
            };
        }

        private static string BuildSnapshotQuery(string? dbType)
        {
            switch (dbType?.ToUpperInvariant())
            {
                case "POSTGRESQL":
                    return @"SELECT d.""ID"" AS ""DEF_ID"", d.""NAME"", d.""DESCRIPTION"", d.""OBJECT_TYPE"", d.""UDP_TYPE"",
                            d.""DEFAULT_VALUE"",
                            o.""VALUE"" AS ""OPT_VALUE"", o.""DISPLAY_TEXT"" AS ""OPT_DISPLAY"", o.""SORT_ORDER"" AS ""OPT_ORDER""
                            FROM ""MC_UDP_DEFINITION"" d
                            LEFT JOIN ""MC_UDP_LIST_OPTION"" o ON o.""UDP_DEFINITION_ID"" = d.""ID""
                            WHERE d.""CONFIG_ID"" = @cfgId
                            ORDER BY d.""ID"", o.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT d.ID AS DEF_ID, d.NAME, d.DESCRIPTION, d.OBJECT_TYPE, d.UDP_TYPE,
                            d.DEFAULT_VALUE,
                            o.VALUE AS OPT_VALUE, o.DISPLAY_TEXT AS OPT_DISPLAY, o.SORT_ORDER AS OPT_ORDER
                            FROM MC_UDP_DEFINITION d
                            LEFT JOIN MC_UDP_LIST_OPTION o ON o.UDP_DEFINITION_ID = d.ID
                            WHERE d.CONFIG_ID = :cfgId
                            ORDER BY d.ID, o.SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT d.[ID] AS [DEF_ID], d.[NAME], d.[DESCRIPTION], d.[OBJECT_TYPE], d.[UDP_TYPE],
                            d.[DEFAULT_VALUE],
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
        /// Carrier for <see cref="UdpSyncEngine.WalkModelUdps"/> output. Bundles
        /// both the filter-resolved model snapshots (for diff computation) and
        /// the full Property_Type name set (so the caller can populate the
        /// connect-level metamodel-names cache without re-walking).
        /// </summary>
        public class ModelWalkResult
        {
            public Dictionary<string, ModelUdpSnapshot> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AllNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Open a level-1 metamodel session and walk Property_Type entries
        /// once, returning both a filter-resolved <see cref="ModelUdpSnapshot"/>
        /// map (for diff) and the full Name set (for the connect-level
        /// metamodel-names cache consumed by ValidationCoordinator).
        /// <para>
        /// When <paramref name="namesOfInterest"/> is supplied, only entries
        /// whose Name appears in that set get the per-tag detail read. Erwin
        /// models can carry ~1500 Property_Type entries while admin defines
        /// only a handful; reading 4 tag_* properties on every entry costs
        /// ~3 ms each via COM marshalling = ~18 s in the wild (verified
        /// 2026-05-16). Filtering drops detail reads to the handful of
        /// matches while still iterating the full collection for the names
        /// cache.
        /// </para>
        /// Orphans (Property_Types in the model that admin doesn't know
        /// about) are intentionally left out of the map - the diff only
        /// considers admin's snapshot and never deletes orphans, so reading
        /// their tags would be wasted work. Orphan Names still appear in
        /// <see cref="ModelWalkResult.AllNames"/> because the cache consumer
        /// (ValidationCoordinator) cares about every UDP in the model.
        /// </para>
        /// </summary>
        public ModelWalkResult WalkModelUdps(IEnumerable<string>? namesOfInterest = null)
        {
            var result = new ModelWalkResult();
            HashSet<string>? filter = namesOfInterest != null
                ? new HashSet<string>(namesOfInterest, StringComparer.OrdinalIgnoreCase)
                : null;

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
                    return result;
                }

                int seen = 0;
                int detail = 0;
                foreach (dynamic pt in propertyTypes)
                {
                    seen++;
                    if (pt == null) continue;
                    string fullName;
                    try { fullName = pt.Name ?? ""; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(fullName)) continue;

                    result.AllNames.Add(fullName);

                    // Cheap filter first: skip detail reads when admin
                    // doesn't care about this name. Names cache still
                    // captures every entry above.
                    if (filter != null && !filter.Contains(fullName)) continue;

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
                    result.Map[fullName] = snap;
                    detail++;
                }

                Log($"UdpSyncEngine.WalkModelUdps: seen={seen}, detailReads={detail}, namesCached={result.AllNames.Count}, filter={(filter != null ? filter.Count.ToString() : "none")}");
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
            return result;
        }

        /// <summary>
        /// Compute the FullName set the engine cares about for a given
        /// snapshot. Used by callers to pre-filter the metamodel walk
        /// (see <see cref="WalkModelUdps"/>) so the COM-heavy property
        /// reads run only against admin-known UDPs.
        /// </summary>
        public static IEnumerable<string> ExpectedFullNames(IEnumerable<UdpDefinitionSnapshot> snapshot)
        {
            if (snapshot == null) yield break;
            foreach (var def in snapshot)
            {
                string? ownerClass = MapObjectTypeToOwnerClass(def.ObjectType);
                if (ownerClass == null) continue;
                yield return $"{ownerClass}.Physical.{def.Name}";
            }
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

                // Delete is intentionally NEVER emitted (2026-05-16). Admin
                // schema has no tombstone column - a model UDP missing from
                // the snapshot is indistinguishable from a user-authored
                // UDP, so silently dropping it would risk destroying user
                // data on every connect. Users remove unwanted UDPs through
                // erwin's own UDP editor; the addin never deletes for them.

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

                diff.Updates.Add(new UdpDiffEntry
                {
                    Action = UdpDiffAction.Update,
                    FullName = fullName,
                    UdpName = adminUdp.Name,
                    ObjectType = adminUdp.ObjectType,
                    AdminUdp = adminUdp,
                    ExistingUdp = match,
                    Changes = changes,
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

        #region Apply (metamodel mutation)

        /// <summary>
        /// Apply a previously-computed diff to the active erwin model.
        /// Opens a level-1 metamodel session, runs Updates then Creates
        /// inside a single named transaction, and either commits the whole
        /// set or rolls back on the first failure.
        ///
        /// Type changes are done in-place - the existing drift sync had
        /// proved this safe before this feature shipped. List option
        /// content (<c>tag_Udp_Values_List</c>) is overwritten with the
        /// admin's full comma-joined value list, so additions and removals
        /// propagate symmetrically - admin is the source of truth.
        ///
        /// Rename is NOT supported (no stable ID linking model -> admin in
        /// the simplified design). An admin-side rename surfaces as
        /// Create(new); the old UDP stays in the model until the user
        /// removes it via erwin's UDP editor.
        /// </summary>
        public ApplyResult Apply(UdpDiff diff)
        {
            if (diff == null) return ApplyResult.Fail("diff is null");
            if (diff.IsEmpty) return ApplyResult.Ok(0, 0);

            dynamic? metamodelSession = null;
            int transId = 0;
            bool transOpen = false;
            int created = 0, updated = 0;

            try
            {
                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // SCD_SL_M1 = Metamodel level

                dynamic mmObjects = metamodelSession.ModelObjects;
                dynamic mmRoot = mmObjects.Root;

                // Build a one-pass lookup of existing Property_Type objects
                // keyed by their canonical Name. Used by Update to find the
                // target without re-collecting on each entry.
                var ptByName = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        string ptName;
                        try { ptName = pt.Name ?? ""; } catch { continue; }
                        if (string.IsNullOrEmpty(ptName)) continue;
                        if (!ptByName.ContainsKey(ptName))
                            ptByName[ptName] = pt;
                    }
                }
                catch (Exception ex)
                {
                    return ApplyResult.Fail($"Property_Type enumeration failed: {ex.Message}");
                }

                transId = metamodelSession.BeginNamedTransaction("AdminUdpSync");
                transOpen = true;

                // === Updates (in-place) ===
                // Every Update is in-place: type/default/list/description
                // are set on the existing Property_Type. If the target
                // happens to be missing (rare race - someone deleted it
                // between the diff and the apply), fall back to Create so
                // the metamodel still ends up at the admin's expected state.
                foreach (var entry in diff.Updates)
                {
                    if (!ptByName.TryGetValue(entry.FullName, out var pt))
                    {
                        Log($"UdpSyncEngine.Apply: Update target '{entry.FullName}' missing - treating as Create");
                        var createdPt = CreatePropertyType(mmObjects, entry);
                        if (createdPt == null)
                            return ApplyResult.Fail($"Update->Create fallback failed for {entry.FullName}");
                        ptByName[entry.FullName] = createdPt;
                        updated++;
                        continue;
                    }

                    try
                    {
                        ApplyUpdateInPlace(pt, entry);
                        updated++;
                        Log($"UdpSyncEngine.Apply: Updated {entry.FullName} ({entry.Changes})");
                    }
                    catch (Exception ex)
                    {
                        return ApplyResult.Fail($"Update failed for {entry.FullName}: {ex.Message}");
                    }
                }

                // === Creates ===
                foreach (var entry in diff.Creates)
                {
                    if (ptByName.ContainsKey(entry.FullName))
                    {
                        Log($"UdpSyncEngine.Apply: Create target '{entry.FullName}' already exists - skipping");
                        continue;
                    }
                    var pt = CreatePropertyType(mmObjects, entry);
                    if (pt == null)
                        return ApplyResult.Fail($"Create failed for {entry.FullName}");
                    ptByName[entry.FullName] = pt;
                    created++;
                    Log($"UdpSyncEngine.Apply: Created {entry.FullName}");
                }

                metamodelSession.CommitTransaction(transId);
                transOpen = false;
                Log($"UdpSyncEngine.Apply: transaction committed (created={created}, updated={updated})");
                return ApplyResult.Ok(created, updated);
            }
            catch (Exception ex)
            {
                Log($"UdpSyncEngine.Apply: unexpected failure: {ex.Message}");
                if (transOpen && metamodelSession != null)
                {
                    try { metamodelSession!.RollbackTransaction(transId); transOpen = false; }
                    catch (Exception rbEx) { Log($"UdpSyncEngine.Apply: rollback failed: {rbEx.Message}"); }
                }
                return ApplyResult.Fail(ex.Message);
            }
            finally
            {
                if (transOpen && metamodelSession != null)
                {
                    // Reached only when an inner return paths out of the try
                    // without committing - rollback to leave the metamodel
                    // clean. The `!` suppresses CS8602: the null analyzer
                    // does not narrow `dynamic?` through the != null check.
                    try { metamodelSession!.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"UdpSyncEngine.Apply: finally-rollback failed: {rbEx.Message}"); }
                }
                if (metamodelSession != null)
                {
                    try { metamodelSession!.Close(); }
                    catch (Exception ex) { Log($"UdpSyncEngine.Apply: session close failed: {ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// Create a Property_Type in the open metamodel session for the
        /// given Create / Update-recreate entry. Returns the new object on
        /// success, null on failure. All tag_Udp_* properties are set via
        /// <see cref="SetPropertyTypeTags"/>.
        /// </summary>
        private dynamic? CreatePropertyType(dynamic mmObjects, UdpDiffEntry entry)
        {
            if (entry.AdminUdp == null) return null;
            try
            {
                dynamic pt = mmObjects.Add("Property_Type");
                pt.Properties("Name").Value = entry.FullName;
                SetPropertyTypeTags(pt, entry.AdminUdp);
                return pt;
            }
            catch (Exception ex)
            {
                // Most common cause: erwin's EBS-1057 "must be unique"
                // constraint when the Property_Type already exists from a
                // previous run (race with the legacy silent path). The
                // UdpRuntimeService.CreateUdpInMetamodel pattern treats
                // EBS-1057 as success-equivalent; we mirror that here so
                // a partial commit from before doesn't poison a fresh
                // Apply.
                if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                {
                    Log($"UdpSyncEngine.CreatePropertyType: '{entry.FullName}' already exists (unique constraint) - treating as success");
                    return null;
                }
                Log($"UdpSyncEngine.CreatePropertyType: '{entry.FullName}' failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Set every tag_Udp_* property + Definition on an existing
        /// Property_Type from the admin snapshot. Used by both Create
        /// (fresh) and Update (in-place) paths so the property set stays
        /// in sync between the two code paths.
        /// </summary>
        private void SetPropertyTypeTags(dynamic pt, UdpDefinitionSnapshot adminUdp)
        {
            string? ownerClass = MapObjectTypeToOwnerClass(adminUdp.ObjectType);
            if (ownerClass != null)
                TrySetProperty(pt, "tag_Udp_Owner_Type", ownerClass);
            TrySetProperty(pt, "tag_Is_Physical", true);
            TrySetProperty(pt, "tag_Is_Logical", false);

            int dataTypeId = MapUdpTypeToErwinDataTypeId(adminUdp.UdpType);
            TrySetProperty(pt, "tag_Udp_Data_Type", dataTypeId);

            // List options - empty list options for List type results in an
            // empty value list (the user will see an empty dropdown until
            // admin adds options). erwin accepts empty string fine.
            if (string.Equals(adminUdp.UdpType, "List", StringComparison.OrdinalIgnoreCase))
            {
                string validValues = adminUdp.ListOptions.Count > 0
                    ? string.Join(",", adminUdp.ListOptions.ConvertAll(o => o.Value))
                    : "";
                TrySetProperty(pt, "tag_Udp_Values_List", validValues);
            }

            // Default value: write empty string to clear when admin removed
            // the default (matches Create + Update parity).
            TrySetProperty(pt, "tag_Udp_Default_Value", adminUdp.DefaultValue ?? "");

            // Description goes into Property_Type.Definition (the field
            // shown in erwin's UDP editor). No sentinel - the simplified
            // design has no owner-tag in the model.
            TrySetProperty(pt, "Definition", adminUdp.Description ?? "");

            TrySetProperty(pt, "tag_Order", "1");
            TrySetProperty(pt, "tag_Is_Locally_Defined", true);
        }

        /// <summary>
        /// In-place update of an existing Property_Type. Re-uses
        /// <see cref="SetPropertyTypeTags"/> so the property set stays
        /// identical to the Create path - admin's snapshot is the single
        /// source of truth for every tag.
        /// </summary>
        private void ApplyUpdateInPlace(dynamic pt, UdpDiffEntry entry)
        {
            if (entry.AdminUdp == null) return;
            SetPropertyTypeTags(pt, entry.AdminUdp);
        }

        private void TrySetProperty(dynamic obj, string propertyName, object value)
        {
            try
            {
                obj.Properties(propertyName).Value = value;
            }
            catch (Exception ex)
            {
                Log($"UdpSyncEngine: Could not set {propertyName}: {ex.Message}");
            }
        }

        #endregion

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
