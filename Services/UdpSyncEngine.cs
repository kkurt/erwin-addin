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

        /// <summary>
        /// False when the model's Property_Type does NOT carry a Definition
        /// property (reading it throws "does not use a property of Definition
        /// type"). erwin's own UDP editor and the addin create UDPs WITH a
        /// Definition, but meta-sync / MIMB imports produce leaner Property_Types
        /// that lack it (verified 2026-06-08 against ZZPROBE.xml + Ek_Kart v2).
        /// When false the diff MUST skip the Description comparison: the model
        /// cannot store a description, so proposing a "Description changed" Update
        /// would loop forever (Apply cannot write Definition either). Defaults to
        /// true so a normal/native UDP is compared as before.
        /// </summary>
        public bool DescriptionSupported { get; set; } = true;
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

        // erwin metamodel namespace GUID shared by every built-in class ID; the numeric
        // suffix after '+' selects the class (see OwnerClassFromOwnerTypeTag).
        private const string OwnerTypeIdBase = "{E7AB3CC0-47AD-4A10-8972-C0FB869EE7CB}+";

        /// <summary>
        /// erwin metamodel class-ID (objectId) for <c>tag_Udp_Owner_Type</c>. erwin accepts the
        /// owner as EITHER a class name OR this class-ID GUID, but the plain class NAME does NOT
        /// round-trip through a Mart save: erwin drops the owner reference on save, the UDP becomes
        /// an unresolvable "internal type" that our metamodel walk can no longer enumerate, and the
        /// diff re-detects it as a missing Create every time - the "UDP sync dialog reappears after
        /// saving to Mart" loop (verified 2026-07-15 via the AdminUdpSync EBS-1057 advisories).
        /// Writing the class-ID GUID persists correctly (MetaSync UdpDefinitionService does the same
        /// and is verified across save/reopen on r10.10). Suffixes mirror
        /// <see cref="OwnerClassFromOwnerTypeTag"/> and MetaSync UdpConstants. Returns null for
        /// classes without a known ID (View, Stored_Procedure) - the caller falls back to the class
        /// name for those (the reader still accepts the name form; only Mart round-trip is at risk).
        /// </summary>
        public static string? MapObjectTypeToOwnerGuid(string objectType)
        {
            if (string.IsNullOrEmpty(objectType)) return null;
            switch (objectType.Trim().ToLowerInvariant())
            {
                case "model":        return OwnerTypeIdBase + "40200002";
                case "table":        return OwnerTypeIdBase + "40200003"; // Entity
                case "column":       return OwnerTypeIdBase + "40200005"; // Attribute
                case "subject area": return OwnerTypeIdBase + "40200026";
                default:             return null; // View / Stored_Procedure: no known class-ID
            }
        }

        /// <summary>
        /// Map admin UDP_TYPE to erwin metamodel tag_Udp_Data_Type integer.
        /// 1=Integer, 2=Text, 3=Date, 4=Command, 5=Real, 6=List. The single source
        /// for this map (UdpRuntimeService delegates here).
        /// <para>Default is Text (2) - the same fallback erwin itself documents
        /// ("Assumes the Text type if it is not specified."). Boolean has no native
        /// erwin datatype; the convention is to store it as Text so 'True'/'False'
        /// stay readable. Admins who want a dropdown should use List in
        /// MC_UDP_DEFINITION with 'True,False' list options instead.</para>
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
                    pCfg.ParameterName = SqlDialect.Param(dbType?.ToUpperInvariant(), "cfgId");
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

                // Admin UDP leaf names (last segment of each expected full
                // path), e.g. "Entity.Physical.TableClass" -> "TableClass". A
                // model UDP whose Property_Type Name is the bare leaf
                // ("TableClass") rather than the full path is the same UDP -
                // erwin's own UDP editor STORES the full path and only DISPLAYS
                // the leaf, while erwin's MIMB importer and MetaSync's
                // RenameCreatedUdpsToLeaf store the bare leaf (verified
                // 2026-06-07). The value accessor is owner+scope+leaf and
                // name-label-independent, so both forms address the same UDP.
                // We therefore also detail-read leaf entries that match an admin
                // leaf and key the map on the canonical "{Owner}.{Scope}.{Leaf}"
                // form (BuildCanonicalKey) so the diff recognises either naming.
                HashSet<string>? leafWanted = filter != null
                    ? new HashSet<string>(filter.Select(ExtractUdpName), StringComparer.OrdinalIgnoreCase)
                    : null;

                int seen = 0;
                int detail = 0;
                int leafMatched = 0;
                foreach (dynamic pt in propertyTypes)
                {
                    seen++;
                    if (pt == null) continue;
                    string fullName;
                    try { fullName = pt.Name ?? ""; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(fullName)) continue;

                    result.AllNames.Add(fullName);

                    // Detail-read when admin asked for this exact full path OR
                    // when this is a bare-leaf entry whose leaf matches an admin
                    // UDP name. The leaf branch carries the cost of one extra
                    // owner-type tag read, but only for the handful of leaf
                    // entries sharing an admin name - built-in Property_Types
                    // that merely collide on the leaf resolve to a null owner
                    // and get skipped below, so cost stays negligible.
                    bool exactWanted = filter == null || filter.Contains(fullName);
                    bool leafCandidate = !exactWanted
                        && leafWanted != null
                        && !IsFullPathName(fullName)
                        && leafWanted.Contains(ExtractUdpName(fullName));

                    if (!exactWanted && !leafCandidate) continue;

                    // Owner + scope come from the Name for full-path entries;
                    // bare-leaf entries need the metamodel tags to reconstruct
                    // the canonical key.
                    string ownerTag = leafCandidate ? ReadStringProperty(pt, "tag_Udp_Owner_Type") : "";
                    string isPhysTag = leafCandidate ? ReadStringProperty(pt, "tag_Is_Physical") : "";
                    string isLogTag = leafCandidate ? ReadStringProperty(pt, "tag_Is_Logical") : "";

                    string? canonicalKey = BuildCanonicalKey(fullName, ownerTag, isPhysTag, isLogTag);
                    if (canonicalKey == null)
                        // Bare leaf that is not a resolvable UDP (a built-in
                        // Property_Type sharing the name) - ignore it so it
                        // cannot fabricate a false match.
                        continue;

                    // Definition is read with explicit support-tracking: a
                    // meta-sync / MIMB import produces a leaner Property_Type that
                    // does NOT carry Definition (the read throws), which the diff
                    // must treat as "cannot compare a description" rather than
                    // "description is empty" - otherwise an admin description loops
                    // forever as a never-appliable "Description changed" Update.
                    string currentDescription = ReadStringPropertyTracked(pt, "Definition", out bool descriptionSupported);
                    var snap = new ModelUdpSnapshot
                    {
                        FullName = fullName,
                        OwnerClass = ExtractOwnerClass(canonicalKey),
                        UdpName = ExtractUdpName(fullName),
                        CurrentDataTypeId = ReadIntProperty(pt, "tag_Udp_Data_Type"),
                        CurrentDefault = ReadStringProperty(pt, "tag_Udp_Default_Value"),
                        CurrentListValues = ReadStringProperty(pt, "tag_Udp_Values_List"),
                        CurrentDescription = currentDescription,
                        DescriptionSupported = descriptionSupported,
                    };

                    // Prefer a full-path entry over a leaf entry if a model
                    // somehow carries both for the same canonical UDP (a
                    // duplicate left by a prior bad Apply). Either resolves the
                    // diff; the full-path one is the canonical record.
                    if (!result.Map.ContainsKey(canonicalKey) || !leafCandidate)
                        result.Map[canonicalKey] = snap;
                    detail++;
                    if (leafCandidate) leafMatched++;
                }

                Log($"UdpSyncEngine.WalkModelUdps: seen={seen}, detailReads={detail}, " +
                    $"leafMatched={leafMatched}, namesCached={result.AllNames.Count}, " +
                    $"filter={(filter != null ? filter.Count.ToString() : "none")}");
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

        /// <summary>
        /// True when <paramref name="name"/> is a three-part UDP path
        /// <c>Owner.Scope.Leaf</c> (at least two dots) rather than a bare leaf.
        /// </summary>
        private static bool IsFullPathName(string name)
        {
            int firstDot = name.IndexOf('.');
            return firstDot > 0 && name.IndexOf('.', firstDot + 1) > firstDot;
        }

        /// <summary>
        /// Resolve a metamodel <c>tag_Udp_Owner_Type</c> value to the canonical
        /// SCAPI owner-class string. Accepts BOTH forms erwin stores:
        /// <list type="bullet">
        /// <item>the objectId/GUID form erwin, MIMB and MetaSync use, e.g.
        /// <c>{E7AB3CC0-47AD-4A10-8972-C0FB869EE7CB}+40200003</c> (only the
        /// numeric suffix is load-bearing); and</item>
        /// <item>the plain class string the addin itself writes, e.g.
        /// <c>Entity</c> (UdpConstants warns the plain string does not round-trip
        /// for Model, so a robust reader must handle the GUID form too).</item>
        /// </list>
        /// Returns null for an empty or unrecognised tag - the caller treats
        /// that as "not a UDP we can canonicalise" and skips the entry, which is
        /// exactly what filters out a built-in Property_Type that merely shares
        /// a leaf name with an admin UDP. GUID suffixes mirror MetaSync
        /// UdpConstants and erwin-admin ParseOwnerCategory.
        /// </summary>
        public static string? OwnerClassFromOwnerTypeTag(string? ownerTypeTag)
        {
            if (string.IsNullOrWhiteSpace(ownerTypeTag)) return null;
            string t = ownerTypeTag.Trim();

            int plus = t.LastIndexOf('+');
            string suffix = plus >= 0 ? t.Substring(plus + 1) : t;
            switch (suffix)
            {
                case "40200002": return "Model";
                case "40200003": return "Entity";
                case "40200004": return "Relationship";
                case "40200005": return "Attribute";
                case "40200006": return "Key_Group";
                case "40200007": return "Domain";
                // Subject_Area owner-type GUID disagrees across tools: MetaSync
                // uses +40200026, erwin-admin uses +40200016. Accept both - the
                // diff only ever projects the classes MapObjectTypeToOwnerClass
                // supports, so over-accepting here is harmless.
                case "40200016":
                case "40200026": return "Subject_Area";
                case "40200102": return "ER_Diagram";
            }

            switch (t.ToLowerInvariant())
            {
                case "model": return "Model";
                case "entity": return "Entity";
                case "attribute": return "Attribute";
                case "subject_area": return "Subject_Area";
                case "view": return "View";
                case "stored_procedure": return "Stored_Procedure";
                case "key_group": return "Key_Group";
            }
            return null;
        }

        /// <summary>
        /// Compute the canonical match key <c>{Owner}.{Scope}.{Leaf}</c> for a
        /// model Property_Type, tolerant of BOTH metamodel naming conventions
        /// erwin accepts for the same UDP:
        /// <list type="bullet">
        /// <item>full path <c>Entity.Physical.TableClass</c> - what erwin's own
        /// UDP editor stores and what the addin creates; and</item>
        /// <item>bare leaf <c>TableClass</c> - what erwin's MIMB importer and
        /// MetaSync's <c>RenameCreatedUdpsToLeaf</c> store (the editor still
        /// DISPLAYS only the leaf either way, verified 2026-06-07).</item>
        /// </list>
        /// The SCAPI value accessor is owner+scope+leaf and independent of the
        /// stored Name label, so the two forms are the same UDP; keying the diff
        /// on this canonical identity stops an imported (leaf-named) model UDP
        /// from being misread as a missing Create. Full-path entries trust the
        /// embedded owner/scope verbatim; bare-leaf entries reconstruct them
        /// from <c>tag_Udp_Owner_Type</c> + the physical/logical flags (default
        /// Physical, the scope admin defines). Returns null when a bare leaf
        /// cannot be resolved to a UDP owner, so the caller skips it rather than
        /// fabricating a match.
        /// </summary>
        public static string? BuildCanonicalKey(
            string? rawName, string? ownerTypeTag, string? isPhysicalTag, string? isLogicalTag)
        {
            if (string.IsNullOrEmpty(rawName)) return null;

            int firstDot = rawName!.IndexOf('.');
            int secondDot = firstDot >= 0 ? rawName.IndexOf('.', firstDot + 1) : -1;
            if (firstDot > 0 && secondDot > firstDot && secondDot < rawName.Length - 1)
            {
                string ownerSeg = rawName.Substring(0, firstDot);
                string scopeSeg = rawName.Substring(firstDot + 1, secondDot - firstDot - 1);
                string leafSeg = rawName.Substring(secondDot + 1);
                return $"{ownerSeg}.{scopeSeg}.{leafSeg}";
            }

            string? owner = OwnerClassFromOwnerTypeTag(ownerTypeTag);
            if (owner == null) return null;
            string scope = ResolveUdpScope(isPhysicalTag, isLogicalTag);
            return $"{owner}.{scope}.{rawName}";
        }

        /// <summary>
        /// Pick the canonical scope segment for a bare-leaf UDP from its
        /// physical/logical flags. A logical-only UDP keeps the Logical scope;
        /// physical and unknown both canonicalise to Physical (the scope the
        /// admin snapshot always uses), so a missing flag never spuriously
        /// diverts a Physical UDP onto the Logical key.
        /// </summary>
        private static string ResolveUdpScope(string? isPhysicalTag, string? isLogicalTag)
        {
            bool logical = ParseErwinBool(isLogicalTag);
            bool physical = ParseErwinBool(isPhysicalTag);
            return (logical && !physical) ? "Logical" : "Physical";
        }

        /// <summary>
        /// Parse an erwin metamodel boolean tag. SCAPI surfaces these as
        /// "True"/"False" strings or VARIANT_BOOL numerics ("1"/"-1"/"0").
        /// </summary>
        private static bool ParseErwinBool(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            string r = raw!.Trim();
            return r.Equals("True", StringComparison.OrdinalIgnoreCase)
                || r == "1"
                || r == "-1";
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

        /// <summary>
        /// Read a string property AND report whether the Property_Type actually
        /// carries it. <paramref name="supported"/> is false when SCAPI throws
        /// "does not use a property of ... type" - i.e. a leaner meta-sync / MIMB
        /// Property_Type that lacks the property. Lets the diff tell "property
        /// absent" apart from "property present but empty".
        /// </summary>
        private static string ReadStringPropertyTracked(dynamic pt, string propertyName, out bool supported)
        {
            try
            {
                var raw = pt.Properties(propertyName)?.Value;
                supported = true;
                return raw?.ToString() ?? "";
            }
            catch
            {
                supported = false;
                return "";
            }
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
                // Same Turkish-I normalisation guard as the list-values
                // branch below - erwin transforms 'İ' -> 'I' on store, so an
                // Ordinal compare would loop the dialog forever on UDPs
                // whose default contains a Turkish dotted I.
                if (!string.Equals(
                        NormalizeForErwinListCompare(adminDefault),
                        NormalizeForErwinListCompare(modelDefault),
                        StringComparison.Ordinal))
                {
                    changes |= UdpUpdateChanges.Default;
                    detailParts.Add($"Default: '{Truncate(modelDefault, 24)}' -> '{Truncate(adminDefault, 24)}'");
                }

                if (string.Equals(adminUdp.UdpType, "List", StringComparison.OrdinalIgnoreCase))
                {
                    // Runtime-managed list guard. A "List type" admin UDP
                    // with zero static options is semantically admin saying
                    // "I delegate content management to runtime" - in this
                    // codebase that runtime is DependencySetRuntimeService
                    // / UdpRuntimeService, which fills tag_Udp_Values_List
                    // from a DB table right after every sync Apply.
                    //
                    // Without this guard, the diff dialog loops forever
                    // (verified 2026-05-31 log on ASSET UDP, MODEL owner):
                    //   admin opts (0)   -> expectedList = ''
                    //   model  20 chars  -> 'Asset1,Asset2,Asset3' (runtime-filled)
                    //   diff "List options changed" -> Apply writes ''
                    //     -> UdpRuntime cascade immediately re-fills with 3
                    //     -> Save persists 3 to Mart
                    //     -> reopen sees model=3 vs admin=0 -> same diff again.
                    //
                    // Empty admin options + non-empty model = no admin
                    // authority to enforce on content; preserve the model
                    // value (Type/Default/Description still sync above and
                    // below this block).
                    bool adminDelegatesListContent = adminUdp.ListOptions.Count == 0;
                    if (adminDelegatesListContent)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"UdpSync: '{adminUdp.Name}' ListValues compare skipped (admin defines 0 static options - runtime-managed UDP).");
                    }
                    else
                    {
                        string expectedList = string.Join(",", adminUdp.ListOptions.Select(o => o.Value));
                        string modelList = match.CurrentListValues ?? "";

                        // Erwin's tag_Udp_Values_List setter normalises Turkish
                        // dotted-I characters (verified 2026-05-23 log diag on
                        // CLASSIFICATION UDP: admin wrote 'Kurum İçi' (U+0130),
                        // erwin stored 'Kurum Içi' (U+0049)). The byte-identical
                        // Ordinal compare therefore always diffs - the dialog
                        // re-appears on every model open even after a successful
                        // Apply round-trip, because we cannot ever write a value
                        // back that survives unchanged. Normalise both sides
                        // through the same transformation erwin performs so the
                        // diff converges. We still log the raw mismatch when
                        // the normalised comparison is what matched, so an
                        // admin staring at a diagnostic line can tell that
                        // erwin corrupted the value rather than think the
                        // Apply silently failed.
                        bool listsDifferOrdinal = !string.Equals(expectedList, modelList, StringComparison.Ordinal);
                        bool listsDifferAfterNormalize = !string.Equals(
                            NormalizeForErwinListCompare(expectedList),
                            NormalizeForErwinListCompare(modelList),
                            StringComparison.Ordinal);

                        if (listsDifferAfterNormalize)
                        {
                            changes |= UdpUpdateChanges.ListValues;
                            detailParts.Add("List options changed");
                        }
                        else if (listsDifferOrdinal)
                        {
                            // Same data, but erwin transformed the bytes on
                            // store. Cannot emit a log line here - this method
                            // is static; the suppression itself is the
                            // breadcrumb (diff is empty -> dialog stays away).
                            // If a session-level diagnostic is needed in the
                            // future, refactor ComputeDiff to accept a log
                            // delegate or pass the suppressed-mismatch count
                            // back through UdpDiff.
                            System.Diagnostics.Debug.WriteLine(
                                $"UdpSync: '{adminUdp.Name}' list options match after Turkish-I normalisation " +
                                $"(erwin stored '{modelList}' for admin's '{expectedList}'); suppressing false-positive diff.");
                        }
                    }
                }

                // Description (Definition) comparison - SKIPPED when the model's
                // Property_Type does not carry a Definition (meta-sync / MIMB
                // imports). Those objects can neither read nor write Definition,
                // so an admin description on such a UDP would otherwise surface as
                // a "Description changed" Update that Apply can never satisfy,
                // looping on every model open. Same authority-to-enforce logic as
                // the runtime-managed list guard above: no writable target => no
                // diff. erwin-native + addin-created UDPs keep Definition and are
                // compared as before.
                if (match.DescriptionSupported)
                {
                    string adminDesc = adminUdp.Description ?? "";
                    string modelDesc = match.CurrentDescription ?? "";
                    if (!string.Equals(
                            NormalizeForErwinListCompare(adminDesc),
                            NormalizeForErwinListCompare(modelDesc),
                            StringComparison.Ordinal))
                    {
                        changes |= UdpUpdateChanges.Description;
                        detailParts.Add("Description changed");
                    }
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

        /// <summary>
        /// Normalise a string the same way erwin's
        /// <c>tag_Udp_Values_List</c> / <c>tag_Udp_Default_Value</c> /
        /// <c>Definition</c> setters silently transform their input on
        /// store. Lets <see cref="ComputeDiff"/> tell "admin's value
        /// after a save+reopen round-trip" apart from a genuine drift.
        /// <para>
        /// Verified store-side transformations (2026-05-23 log diag on
        /// CLASSIFICATION list options):
        /// </para>
        /// <list type="bullet">
        /// <item><description>U+0130 'İ' (Turkish capital dotted I) -> U+0049 'I'
        /// (ASCII capital I). Reason inferred: erwin's MFC layer uses a
        /// Latin-only ToUpperInvariant pass that collapses the Turkish
        /// dotted-I to plain I. The lowercase counterpart (U+0131 'ı')
        /// gets the same treatment in reverse, mapping to U+0069 'i'.</description></item>
        /// </list>
        /// We only normalise the characters we have observed erwin
        /// touch - adding more entries here belongs under "verify in log
        /// first, then mirror".
        /// </summary>
        internal static string NormalizeForErwinListCompare(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // String.Replace(char, char) is O(n) on a fresh char array, so
            // two passes is still ~n cost; cheap relative to the SCAPI
            // reads that produced the string in the first place.
            return s!.Replace('İ', 'I')   // İ -> I
                     .Replace('ı', 'i');  // ı -> i
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
                // keyed by their CANONICAL match key (BuildCanonicalKey) so an
                // Update finds its target whether the model stored the Name as
                // the full path "Entity.Physical.X" or the bare leaf "X" (erwin
                // MIMB / MetaSync style). Without this a leaf-named existing UDP
                // would miss the Update lookup, fall through to Create, and
                // inject a duplicate ".Physical." Property_Type - the exact
                // harm this fix removes. Owner-type tags are read only for the
                // few bare-leaf entries whose leaf matches a pending Update, so
                // the enumeration stays cheap on big metamodels.
                var updateLeaves = new HashSet<string>(
                    diff.Updates.Select(e => ExtractUdpName(e.FullName)),
                    StringComparer.OrdinalIgnoreCase);
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

                        if (IsFullPathName(ptName))
                        {
                            string keyFull = BuildCanonicalKey(ptName, null, null, null) ?? ptName;
                            if (!ptByName.ContainsKey(keyFull))
                                ptByName[keyFull] = pt;
                        }
                        else
                        {
                            // Register bare names under the raw key (cheap) and,
                            // only when the leaf matches a pending Update, also
                            // under the canonical key (one owner-type tag read).
                            if (!ptByName.ContainsKey(ptName))
                                ptByName[ptName] = pt;
                            if (updateLeaves.Contains(ptName))
                            {
                                string? keyLeaf = BuildCanonicalKey(
                                    ptName,
                                    ReadStringProperty(pt, "tag_Udp_Owner_Type"),
                                    ReadStringProperty(pt, "tag_Is_Physical"),
                                    ReadStringProperty(pt, "tag_Is_Logical"));
                                if (keyLeaf != null && !ptByName.ContainsKey(keyLeaf))
                                    ptByName[keyLeaf] = pt;
                            }
                        }
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
            // tag_Udp_Owner_Type MUST be the class-ID GUID, not the plain class name: the plain
            // name does NOT round-trip through a Mart save, so the UDP loses its owner on reload
            // and the sync re-detects it as a missing Create forever (see MapObjectTypeToOwnerGuid).
            // Fall back to the class name only for classes with no known ID (View, Stored_Procedure).
            string? ownerId = MapObjectTypeToOwnerGuid(adminUdp.ObjectType);
            string? ownerValue = ownerId ?? MapObjectTypeToOwnerClass(adminUdp.ObjectType);
            if (ownerValue != null)
            {
                TrySetProperty(pt, "tag_Udp_Owner_Type", ownerValue);
                // Verify erwin actually stored our owner id; a silent normalization/reject here is
                // exactly what caused the persistence loop, so surface any mismatch (MetaSync does
                // the same readback check).
                try
                {
                    string ownerBack = pt.Properties("tag_Udp_Owner_Type")?.Value?.ToString() ?? "";
                    if (ownerBack.Length > 0 && !string.Equals(ownerBack, ownerValue, StringComparison.OrdinalIgnoreCase))
                        Log($"UdpSyncEngine.Apply [{adminUdp.Name}] tag_Udp_Owner_Type readback differs: set='{ownerValue}' readback='{ownerBack}'");
                }
                catch (Exception ex) { Log($"UdpSyncEngine.Apply [{adminUdp.Name}] tag_Udp_Owner_Type readback failed: {ex.Message}"); }
            }
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

                // Read current value first, both for diag and for the
                // runtime-managed-list guard below.
                string beforeWrite = "";
                try { beforeWrite = pt.Properties("tag_Udp_Values_List")?.Value?.ToString() ?? ""; }
                catch (Exception ex) { Log($"UdpSyncEngine.Apply DIAG: pre-write read failed: {ex.Message}"); }

                // Runtime-managed list guard. Mirrors the ComputeDiff guard:
                // admin defines a List UDP with 0 static options => content
                // is owned by DependencySetRuntime / UdpRuntime (DB table
                // fill). Writing "" here would wipe the model value that
                // those services just wrote, then they re-write it on the
                // next tick, and the next FetchSnapshot diffs again - the
                // dialog loops forever (verified 2026-05-31 on ASSET UDP).
                // Skip the write when there is nothing authoritative to
                // overwrite with and the model already has content; Create
                // path (beforeWrite empty) still passes through so a fresh
                // Property_Type starts with the empty list erwin expects.
                bool adminDelegatesListContent = adminUdp.ListOptions.Count == 0;
                if (adminDelegatesListContent && beforeWrite.Length > 0)
                {
                    Log($"UdpSyncEngine.Apply [{adminUdp.Name}] tag_Udp_Values_List write SKIPPED " +
                        $"(admin defines 0 static options; preserving runtime-managed value: {beforeWrite.Length} chars).");
                }
                else
                {
                    // Apply-time diagnostic for the "round-trip mismatch" bug
                    // (admin's ASCII 'I' becomes Turkish 'İ' U+0130 after a
                    // save+reopen cycle, so the diff dialog reappears forever).
                    // We log the value just before the write, the literal we are
                    // about to send, and the value erwin returns from a re-read
                    // inside the same transaction. If write != readback, erwin's
                    // setter is silently transforming the bytes - then the fix
                    // belongs in how we serialize the list, not in callers.
                    TrySetProperty(pt, "tag_Udp_Values_List", validValues);

                    string afterWrite = "";
                    try { afterWrite = pt.Properties("tag_Udp_Values_List")?.Value?.ToString() ?? ""; }
                    catch (Exception ex) { Log($"UdpSyncEngine.Apply DIAG: post-write read failed: {ex.Message}"); }

                    Log($"UdpSyncEngine.Apply DIAG [{adminUdp.Name}] tag_Udp_Values_List:");
                    Log($"  before  ({beforeWrite.Length} chars): '{beforeWrite}'");
                    Log($"  wrote   ({validValues.Length} chars): '{validValues}'");
                    Log($"  readback({afterWrite.Length} chars): '{afterWrite}'");
                    Log($"  wrote    hex: {string.Join(" ", validValues.Select(c => ((int)c).ToString("X4")))}");
                    Log($"  readback hex: {string.Join(" ", afterWrite.Select(c => ((int)c).ToString("X4")))}");
                    if (!string.Equals(validValues, afterWrite, StringComparison.Ordinal))
                        Log($"  *** MISMATCH: erwin setter transformed the value in-transaction ***");
                }
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
