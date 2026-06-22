using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// One allowed datatype for the model's DBMS: the base type token plus whether it
    /// takes a length/precision parameter. Sourced from DATATYPE_LIBRARY (the admin
    /// "Datatype Library" catalog rows for that DBMS).
    /// </summary>
    public sealed class AllowedDatatypeEntry
    {
        /// <summary>DATATYPE_LIBRARY.DATATYPE - the base token only (e.g. "int", "nvarchar", "Numeric").</summary>
        public string Datatype { get; set; }

        /// <summary>
        /// DATATYPE_LIBRARY.IS_PARAMETERIZED. True = the type carries a length/precision
        /// (nvarchar(50), Numeric(10,2)); the whitelist permits the base with ANY length.
        /// False = a bare type (int, date); a length-bearing value is rejected.
        /// </summary>
        public bool IsParameterized { get; set; }
    }

    /// <summary>
    /// Loads the datatypes the admin "Datatype Library" allows for the active model's DBMS and
    /// answers whether a column's Physical_Data_Type is permitted. The allowed set is the DBMS
    /// catalog the admin curated: DATATYPE_LIBRARY rows for the DBMS the model targets, reached
    /// from the config's DBMS_VERSION_ID via DBMS_VERSION.DBMS_ID. This is PER-DBMS (the admin's
    /// own "types for this DBMS" list, user decision 2026-06-19: "DBMS bazinda belirleniyor, config
    /// bazinda degil"), NOT per config (the never-populated ALLOWED_DATATYPE table).
    /// <para>
    /// An empty/absent list is "no restriction" - so a DBMS the admin has not curated yet does
    /// not suddenly reject every type. There is NO DBMS_VERSION.STATUS gate: the admin's own
    /// catalog query does not filter on status, every version is DRAFT in practice, and a hidden
    /// ACTIVE gate silently disabled the whole feature. We enforce whatever the admin defined.
    /// </para>
    /// <para>2026-06-19: corrected twice. First from ALLOWED_DATATYPE (config-level, unpopulated,
    /// no UI) to DATATYPE_VERSION (per-version link); then to DBMS-level DATATYPE_LIBRARY after a
    /// live trace showed the admin defines types at the DBMS level (DATATYPE_VERSION empty, all
    /// versions DRAFT) and the per-version + ACTIVE query loaded an empty set. The add-in is the
    /// only side changed - the admin project is untouched.</para>
    /// </summary>
    public class AllowedDatatypeService
    {
        private static AllowedDatatypeService _instance;
        private static readonly object _lock = new object();

        public static AllowedDatatypeService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null) _instance = new AllowedDatatypeService();
                    }
                }
                return _instance;
            }
        }

        private List<AllowedDatatypeEntry> _allowed = new List<AllowedDatatypeEntry>();
        private bool _isLoaded;
        private string _lastError;

        /// <summary>True once a load attempt completed (success or "no rows"); false on hard failure.</summary>
        public bool IsLoaded => _isLoaded;

        /// <summary>True when a non-empty whitelist is loaded - i.e. the config restricts datatypes.</summary>
        public bool HasRestriction => _isLoaded && _allowed.Count > 0;

        public IReadOnlyList<AllowedDatatypeEntry> Allowed => _allowed;
        public string LastError => _lastError;

        /// <summary>
        /// Load the active model's allowed datatypes = the catalog the admin curated for its DBMS
        /// (DATATYPE_LIBRARY joined to DBMS_VERSION on DBMS_ID), reached from the model's
        /// DBMS_VERSION_ID (ConfigContextService.DbmsVersionId). Returns true on a clean read
        /// (including the legitimate empty result = no restriction); false on a hard error.
        /// No DBMS version on the config yields an empty set = no restriction.
        /// </summary>
        public bool Load()
        {
            try
            {
                _allowed = new List<AllowedDatatypeEntry>();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"AllowedDatatypeService: {_lastError}");
                    return false;
                }

                var ctx = ConfigContextService.Instance;
                if (!ctx.IsInitialized)
                {
                    _lastError = "ConfigContext not initialized.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"AllowedDatatypeService: {_lastError}");
                    return false;
                }

                // No DBMS version mapped -> nothing to restrict against.
                if (ctx.DbmsVersionId == null)
                {
                    _isLoaded = true;
                    System.Diagnostics.Debug.WriteLine("AllowedDatatypeService: config has no DBMS_VERSION_ID - no restriction.");
                    return true;
                }

                string dbType = DatabaseService.Instance.GetDbType();
                string query = GetQuery(dbType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();
                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        var pVer = command.CreateParameter();
                        pVer.ParameterName = SqlDialect.Param(dbType, "verId");
                        pVer.Value = ctx.DbmsVersionId.Value;
                        command.Parameters.Add(pVer);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string name = reader["DATATYPE"]?.ToString()?.Trim() ?? "";
                                if (string.IsNullOrEmpty(name)) continue;
                                bool param = reader["IS_PARAMETERIZED"] != DBNull.Value
                                             && Convert.ToBoolean(reader["IS_PARAMETERIZED"]);
                                _allowed.Add(new AllowedDatatypeEntry { Datatype = name, IsParameterized = param });
                            }
                        }
                    }
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine(
                    $"AllowedDatatypeService: loaded {_allowed.Count} allowed datatype(s) for DBMS version {ctx.DbmsVersionId} " +
                    $"({(_allowed.Count == 0 ? "no restriction (DBMS catalog empty)" : string.Join(", ", _allowed.Select(a => a.Datatype + (a.IsParameterized ? "(n)" : ""))))})");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"AllowedDatatypeService.Load error: {ex.Message}");
                return false;
            }
        }

        // The admin "Datatype Library" catalog for the model's DBMS: DATATYPE_LIBRARY (DATATYPE,
        // IS_PARAMETERIZED) keyed by DBMS_ID, reached from the model's DBMS_VERSION_ID through
        // DBMS_VERSION.DBMS_ID. NO DATATYPE_VERSION link and NO DBMS_VERSION.STATUS filter: the
        // admin defines types at the DBMS level and the per-version link / ACTIVE status are not
        // required for enforcement (verified live 2026-06-19: DATATYPE_VERSION empty, every
        // version DRAFT, yet 'int' defined for the DBMS must be enforced). NO MC_ prefix on these
        // tables (unlike MC_NAMING_STANDARD). Bound to ConfigContextService.DbmsVersionId, NOT a
        // CONFIG/ALLOWED_DATATYPE row.
        private static string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT dl.""DATATYPE"", dl.""IS_PARAMETERIZED""
                            FROM ""DATATYPE_LIBRARY"" dl
                            JOIN ""DBMS_VERSION"" dv ON dv.""DBMS_ID"" = dl.""DBMS_ID""
                            WHERE dv.""ID"" = @verId
                            ORDER BY dl.""DATATYPE""";

                case "ORACLE":
                    return @"SELECT dl.DATATYPE, dl.IS_PARAMETERIZED
                            FROM DATATYPE_LIBRARY dl
                            JOIN DBMS_VERSION dv ON dv.DBMS_ID = dl.DBMS_ID
                            WHERE dv.ID = :verId
                            ORDER BY dl.DATATYPE";

                case "MSSQL":
                default:
                    return @"SELECT dl.[DATATYPE], dl.[IS_PARAMETERIZED]
                            FROM [dbo].[DATATYPE_LIBRARY] dl
                            JOIN [dbo].[DBMS_VERSION] dv ON dv.[DBMS_ID] = dl.[DBMS_ID]
                            WHERE dv.[ID] = @verId
                            ORDER BY dl.[DATATYPE]";
            }
        }

        /// <summary>
        /// True when the type is permitted: no restriction (empty whitelist) OR the value's
        /// base token matches an allowed entry under the parameterized rule.
        /// </summary>
        public bool IsAllowed(string physicalDataType) => IsDatatypeAllowed(physicalDataType, _allowed);

        /// <summary>
        /// A concrete allowed datatype to force a column to when its current value is disallowed
        /// and there is no allowed previous value to restore (e.g. a brand-new column whose first
        /// pick is disallowed). User decision 2026-06-19 ("izinli tipe zorla"): never leave a
        /// disallowed type in the model. Prefers the first non-parameterized entry (a complete,
        /// valid token such as "int"); otherwise the first entry's base token. The load query
        /// orders by name so "first" is deterministic. Null only when nothing is loaded
        /// (HasRestriction false) - callers must null-check.
        /// </summary>
        public string GetFallbackDatatype()
        {
            if (_allowed == null || _allowed.Count == 0) return null;
            // Prefer a non-parameterized entry: its base token (e.g. "int") is a complete, writable
            // Physical_Data_Type on its own.
            var firstComplete = _allowed.FirstOrDefault(a => a != null && !a.IsParameterized && !string.IsNullOrEmpty(a.Datatype));
            if (firstComplete != null) return firstComplete.Datatype;
            // Only parameterized types are allowed (unusual): a bare base like "nvarchar" may be
            // rejected by erwin, so synthesize a minimal valid length. "(1)" is valid for length and
            // for precision/scale types alike (scale defaults to 0), and the matcher accepts it.
            var firstParam = _allowed.FirstOrDefault(a => a != null && !string.IsNullOrEmpty(a.Datatype));
            return firstParam == null ? null : firstParam.Datatype + "(1)";
        }

        /// <summary>
        /// Pure matcher (no DB / no SCAPI) so it is unit-testable. A type is allowed when:
        /// the whitelist is empty (no restriction); OR the parsed base token matches an entry
        /// case-insensitively AND, for a non-parameterized entry, the value carries NO length
        /// (a parameterized entry accepts any length, including none). An empty/unparseable
        /// value is treated as allowed (cannot classify - do not block).
        /// </summary>
        public static bool IsDatatypeAllowed(string physicalDataType, IReadOnlyCollection<AllowedDatatypeEntry> allowed)
        {
            if (allowed == null || allowed.Count == 0) return true; // no restriction

            var parts = DataTypeParser.Parse(physicalDataType);
            if (string.IsNullOrEmpty(parts.Base)) return true;      // cannot classify - do not block

            foreach (var a in allowed)
            {
                if (a == null || string.IsNullOrEmpty(a.Datatype)) continue;
                if (!string.Equals(a.Datatype, parts.Base, StringComparison.OrdinalIgnoreCase)) continue;
                if (a.IsParameterized) return true;   // base matches, any length (incl. none)
                if (!parts.HasLength) return true;    // bare type: base + no length
            }
            return false;
        }

        public void Reload() => Load();

        /// <summary>Test-only seed (mirrors NamingStandardService.SeedForTesting). Production never calls this.</summary>
        public void SeedForTesting(IEnumerable<AllowedDatatypeEntry> entries)
        {
            _allowed = (entries ?? Enumerable.Empty<AllowedDatatypeEntry>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.Datatype))
                .ToList();
            _lastError = null;
            _isLoaded = true;
        }
    }
}
