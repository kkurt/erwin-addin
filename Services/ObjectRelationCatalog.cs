#nullable enable

using System;
using System.Collections.Generic;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Loads and caches <c>MC_OBJECT_RELATION</c>, the GLOBAL token dictionary
    /// used by the "Template" naming rule type to navigate between related
    /// objects. Each row is <c>(FROM_OBJECT_TYPE_ID, ALIAS, TO_OBJECT_TYPE_ID,
    /// SORT_ORDER)</c>; the ALIAS is unique within a FROM object type (e.g.
    /// COLUMN.Table -> TABLE means a column's parent table is reachable via the
    /// alias "Table"). A Template token <c>{Alias.PropertyCode}</c> resolves the
    /// alias here, then the runtime walks the erwin object graph to the related
    /// object and reads the property.
    /// <para>
    /// The catalog is NOT config-scoped: it is a global metamodel catalog shared
    /// across configs, so it is loaded once per DB connection and cached. It is
    /// reloaded only when the underlying admin DB changes (corporate switch) via
    /// <see cref="Reload"/>.
    /// </para>
    /// </summary>
    public sealed class ObjectRelationCatalog
    {
        private static ObjectRelationCatalog? _instance;
        private static readonly object _lock = new object();

        // (fromObjectTypeName, alias) -> toObjectTypeName. Case-insensitive on
        // both key parts so callers pass "Column" / "Table" without worrying
        // about the DB casing of MC_OBJECT_TYPE.NAME.
        private Dictionary<(string fromType, string alias), string> _byKey;
        private bool _isLoaded;
        private string? _lastError;

        public static ObjectRelationCatalog Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new ObjectRelationCatalog();
                    }
                }
                return _instance;
            }
        }

        private ObjectRelationCatalog()
        {
            _byKey = new Dictionary<(string, string), string>(KeyComparer.Instance);
        }

        private sealed class KeyComparer : IEqualityComparer<(string fromType, string alias)>
        {
            public static readonly KeyComparer Instance = new KeyComparer();
            public bool Equals((string fromType, string alias) x, (string fromType, string alias) y) =>
                StringComparer.OrdinalIgnoreCase.Equals(x.fromType, y.fromType) &&
                StringComparer.OrdinalIgnoreCase.Equals(x.alias, y.alias);
            public int GetHashCode((string fromType, string alias) obj) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.fromType ?? ""),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.alias ?? ""));
        }

        public bool IsLoaded => _isLoaded;
        public string? LastError => _lastError;

        /// <summary>
        /// Load the catalog if it has not loaded yet. A DB error propagates to
        /// the caller (no swallow) so a genuine database failure surfaces as a
        /// database failure, not as a downstream "unknown alias".
        /// </summary>
        public void EnsureLoaded()
        {
            if (_isLoaded) return;
            Load();
        }

        /// <summary>
        /// Force a fresh read of <c>MC_OBJECT_RELATION</c> (e.g. after the admin
        /// DB connection changed). Throws on a DB error.
        /// </summary>
        public void Reload()
        {
            _isLoaded = false;
            Load();
        }

        private void Load()
        {
            // Let DB/connection errors propagate: a Template rule that cannot
            // resolve its relations because the DB is unreachable must fail
            // loudly, not silently degrade to "no relations defined".
            var map = new Dictionary<(string, string), string>(KeyComparer.Instance);

            string dbType = DatabaseService.Instance.GetDbType();
            string query = GetQuery(dbType);

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string fromType = reader["FROM_NAME"]?.ToString()?.Trim() ?? "";
                        string alias = reader["ALIAS"]?.ToString()?.Trim() ?? "";
                        string toType = reader["TO_NAME"]?.ToString()?.Trim() ?? "";
                        if (fromType.Length == 0 || alias.Length == 0 || toType.Length == 0)
                            continue;
                        // ALIAS is unique within a FROM type (admin constraint);
                        // last-write-wins is harmless if a dev DB has a dup.
                        map[(fromType, alias)] = toType;
                    }
                }
            }

            _byKey = map;
            _isLoaded = true;
            _lastError = null;
            System.Diagnostics.Debug.WriteLine($"ObjectRelationCatalog: loaded {map.Count} relation(s)");
        }

        /// <summary>
        /// Resolve a relation alias for a FROM object type to its TO object type
        /// name, or null when no such alias exists in the catalog. A null return
        /// means the alias is UNKNOWN and the caller must treat it as a hard
        /// error (no-fallback), never a silent skip.
        /// </summary>
        public string? ResolveAlias(string fromObjectTypeName, string alias)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(fromObjectTypeName) || string.IsNullOrEmpty(alias))
                return null;
            return _byKey.TryGetValue((fromObjectTypeName, alias), out var toType) ? toType : null;
        }

        private static string GetQuery(string dbType) => dbType switch
        {
            "POSTGRESQL" => @"SELECT r.""ID"", fot.""NAME"" AS ""FROM_NAME"", r.""ALIAS"",
                              tot.""NAME"" AS ""TO_NAME"", r.""SORT_ORDER""
                              FROM ""MC_OBJECT_RELATION"" r
                              JOIN ""MC_OBJECT_TYPE"" fot ON fot.""ID"" = r.""FROM_OBJECT_TYPE_ID""
                              JOIN ""MC_OBJECT_TYPE"" tot ON tot.""ID"" = r.""TO_OBJECT_TYPE_ID""
                              ORDER BY r.""SORT_ORDER""",
            "ORACLE" => @"SELECT r.ID, fot.NAME AS FROM_NAME, r.ALIAS,
                          tot.NAME AS TO_NAME, r.SORT_ORDER
                          FROM MC_OBJECT_RELATION r
                          JOIN MC_OBJECT_TYPE fot ON fot.ID = r.FROM_OBJECT_TYPE_ID
                          JOIN MC_OBJECT_TYPE tot ON tot.ID = r.TO_OBJECT_TYPE_ID
                          ORDER BY r.SORT_ORDER",
            _ => @"SELECT r.[ID], fot.[NAME] AS [FROM_NAME], r.[ALIAS],
                   tot.[NAME] AS [TO_NAME], r.[SORT_ORDER]
                   FROM [dbo].[MC_OBJECT_RELATION] r
                   JOIN [dbo].[MC_OBJECT_TYPE] fot ON fot.[ID] = r.[FROM_OBJECT_TYPE_ID]
                   JOIN [dbo].[MC_OBJECT_TYPE] tot ON tot.[ID] = r.[TO_OBJECT_TYPE_ID]
                   ORDER BY r.[SORT_ORDER]"
        };
    }
}
