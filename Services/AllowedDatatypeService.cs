using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// One allowed datatype for the active config: the base type token plus whether it
    /// takes a length/precision parameter. Sourced from DATATYPE_LIBRARY (the admin
    /// "Datatype Library" whitelist rows for that config).
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
    /// Loads the datatypes the admin "Datatype Library" allows for the active CONFIG and answers
    /// whether a column's Physical_Data_Type is permitted. The allowed set is the config's own
    /// whitelist: DATATYPE_LIBRARY rows WHERE CONFIG_ID = the active config's ID
    /// (ConfigContextService.ActiveConfigId). A config's row set IS its datatype whitelist.
    /// <para>
    /// An empty/absent set is "no restriction" - a config the admin has not curated yet does not
    /// suddenly reject every type. There is NO status gate: enforcement is purely "is this base
    /// token in the config's set".
    /// </para>
    /// <para>2026-07-02: DATATYPE_LIBRARY became config-scoped (admin migration applied to the
    /// live MetaRepo DBs). Columns are now ID, CONFIG_ID, DATATYPE, IS_PARAMETERIZED; the old
    /// DBMS_ID column, the DATATYPE_VERSION and ALLOWED_DATATYPE tables, and the DBMS_VERSION join
    /// are all gone. The add-in filters by CONFIG_ID; DBMS_VERSION / DBMS_LIBRARY remain only for
    /// CONFIG.DBMS_VERSION_ID + the DBMS-mismatch check, not for this whitelist.</para>
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
        /// Load the active config's allowed datatypes = DATATYPE_LIBRARY rows WHERE CONFIG_ID =
        /// ConfigContextService.ActiveConfigId. Returns true on a clean read (including the
        /// legitimate empty result = no restriction); false on a hard error. A config with no
        /// DATATYPE_LIBRARY rows yields an empty set = no restriction.
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

                string dbType = DatabaseService.Instance.GetDbType();
                string query = GetQuery(dbType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();
                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        var pCfg = command.CreateParameter();
                        pCfg.ParameterName = SqlDialect.Param(dbType, "configId");
                        pCfg.Value = ctx.ActiveConfigId;
                        command.Parameters.Add(pCfg);

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
                    $"AllowedDatatypeService: loaded {_allowed.Count} allowed datatype(s) for config {ctx.ActiveConfigId} " +
                    $"({(_allowed.Count == 0 ? "no restriction (config whitelist empty)" : string.Join(", ", _allowed.Select(a => a.Datatype + (a.IsParameterized ? "(n)" : ""))))})");
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

        // The active config's "Datatype Library" whitelist: DATATYPE_LIBRARY (DATATYPE,
        // IS_PARAMETERIZED) filtered by CONFIG_ID. Config-scoped since the 2026-07-02 admin
        // migration (the old DBMS_ID column + DBMS_VERSION join are gone). No status gate. No MC_
        // prefix (unlike MC_NAMING_STANDARD). Bound to ConfigContextService.ActiveConfigId.
        private static string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""DATATYPE"", ""IS_PARAMETERIZED""
                            FROM ""DATATYPE_LIBRARY""
                            WHERE ""CONFIG_ID"" = @configId
                            ORDER BY ""DATATYPE""";

                case "ORACLE":
                    return @"SELECT DATATYPE, IS_PARAMETERIZED
                            FROM DATATYPE_LIBRARY
                            WHERE CONFIG_ID = :configId
                            ORDER BY DATATYPE";

                case "MSSQL":
                default:
                    return @"SELECT [DATATYPE], [IS_PARAMETERIZED]
                            FROM [dbo].[DATATYPE_LIBRARY]
                            WHERE [CONFIG_ID] = @configId
                            ORDER BY [DATATYPE]";
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
        /// case-insensitively AND the length rule holds: a parameterized entry REQUIRES a length
        /// (bare 'varchar2' is rejected when 'varchar2' is parameterized), a non-parameterized
        /// entry requires NO length. An empty/unparseable value is treated as allowed (cannot
        /// classify - do not block).
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
                // A parameterized entry REQUIRES a length: 'varchar2' defined as parameterized
                // means the model must carry 'varchar2(n)', never a bare 'varchar2' (user
                // decision 2026-07-05). A non-parameterized entry requires NO length. Base is
                // unique per config, so the non-matching branch just falls through to false.
                if (a.IsParameterized) { if (parts.HasLength) return true; }
                else if (!parts.HasLength) return true;
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
