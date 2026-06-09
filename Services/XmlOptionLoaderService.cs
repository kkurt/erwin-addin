using System;
using System.Data;
using System.IO;
using System.Reflection;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Resolves and materializes RE/DDL option XML for ReverseEngineer / ForwardEngineer calls.
    ///
    /// Lookup chain (per <see cref="LoadAndWriteToTempFile"/>):
    ///   1. XML_OPTION row matching CONFIG_ID = active config's id
    ///   2. Embedded default XML bundled inside this assembly (RE only)
    ///   3. Returns null — caller decides whether to skip the option path argument
    /// </summary>
    public static class XmlOptionLoaderService
    {
        private const string EmbeddedReResource =
            "EliteSoft.Erwin.AddIn.Resources.DefaultReverseOptions.xml";
        private const string EmbeddedDdlResource =
            "EliteSoft.Erwin.AddIn.Resources.DefaultGenerationOptions.xml";

        /// <summary>
        /// Resolve option XML and write it to a temp file. Returns the temp path, or null
        /// if no XML could be resolved at any layer (caller should treat as "no options").
        /// Caller MUST delete the temp file in a finally block.
        /// </summary>
        /// <param name="conn">Open SQL connection (caller owns lifetime).</param>
        /// <param name="configId">Active config id (or null to skip DB lookup).</param>
        /// <param name="type">'RE' or 'DDL'.</param>
        /// <param name="log">Logger.</param>
        public static string LoadAndWriteToTempFile(IDbConnection conn, int? configId, string type, Action<string> log)
        {
            string xml = ResolveXml(conn, configId, type, log);
            if (string.IsNullOrEmpty(xml))
            {
                log?.Invoke($"XmlOption: no XML resolved for type='{type}' configId={configId}, will skip option path");
                return null;
            }

            string path = Path.Combine(Path.GetTempPath(),
                $"erwin_addin_{type.ToLowerInvariant()}_opt_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(path, xml);
                log?.Invoke($"XmlOption: wrote {xml.Length} chars to {path}");
                return path;
            }
            catch (Exception ex)
            {
                log?.Invoke($"XmlOption: failed to write temp file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Run the lookup chain and return resolved XML (or null if even the embedded fallback fails).
        /// </summary>
        public static string ResolveXml(IDbConnection conn, int? configId, string type, Action<string> log)
        {
            // 1) Active-config specific row
            if (configId.HasValue)
            {
                string xml = ReadXmlOption(conn, configId.Value, type, log);
                if (!string.IsNullOrEmpty(xml))
                {
                    log?.Invoke($"XmlOption: matched CONFIG_ID={configId.Value} TYPE='{type}'");
                    return xml;
                }
            }

            // 2) Embedded default — ONLY for RE. DDL embedded default has hardcoded
            // DBMSVersion which often mismatches the active model's target version
            // and triggers a "XML File is not compatible for Forward Engineering" popup.
            // For DDL, return null and let the caller pass "" so erwin uses its own defaults.
            if (string.Equals(type, "RE", StringComparison.OrdinalIgnoreCase))
            {
                string embedded = LoadEmbeddedDefault(type, log);
                if (!string.IsNullOrEmpty(embedded))
                {
                    log?.Invoke($"XmlOption: using embedded default for TYPE='{type}' ({embedded.Length} chars)");
                    return embedded;
                }
            }
            else
            {
                log?.Invoke($"XmlOption: no DB row for TYPE='{type}'; falling back to erwin defaults (no XML)");
            }

            return null;
        }

        private static string ReadXmlOption(IDbConnection conn, int configId, string type, Action<string> log)
        {
            try
            {
                // Dialect-aware query. The original hard-coded "@" parameter
                // prefix + bare TYPE is MSSQL-only: on Oracle "@cfgId" is not a
                // bind marker (ORA-00936 "missing expression") and TYPE is a
                // reserved word, so the admin's DDL/RE option XML silently fell
                // back to erwin defaults and was never applied (verified
                // 2026-06-08, repeated ORA-00936 in the Generate-DDL log).
                // Mirror the dialect conventions the other repo services use
                // (PredefinedColumnService etc.): Oracle binds with ":" and
                // quotes the reserved TYPE; Postgres quotes every identifier and
                // binds with "@"; MSSQL stays as-is.
                string connType = conn.GetType().Name;
                bool isOracle = connType.IndexOf("Oracle", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isPostgres = connType.IndexOf("Npgsql", StringComparison.OrdinalIgnoreCase) >= 0;

                string sql, pCfg, pType;
                if (isOracle)
                {
                    sql = "SELECT CONTENT FROM XML_OPTION WHERE CONFIG_ID = :cfgId AND \"TYPE\" = :type";
                    pCfg = ":cfgId"; pType = ":type";
                }
                else if (isPostgres)
                {
                    sql = "SELECT \"CONTENT\" FROM \"XML_OPTION\" WHERE \"CONFIG_ID\" = @cfgId AND \"TYPE\" = @type";
                    pCfg = "@cfgId"; pType = "@type";
                }
                else
                {
                    sql = "SELECT CONTENT FROM XML_OPTION WHERE CONFIG_ID = @cfgId AND TYPE = @type";
                    pCfg = "@cfgId"; pType = "@type";
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                AddParam(cmd, pCfg, configId);
                AddParam(cmd, pType, type);

                object result = cmd.ExecuteScalar();
                if (result == null || result is DBNull) return null;
                string s = result.ToString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch (Exception ex)
            {
                log?.Invoke($"XmlOption: ReadXmlOption(CONFIG_ID={configId}, TYPE='{type}') error: {ex.Message}");
                return null;
            }
        }

        private static void AddParam(IDbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        /// <summary>
        /// Read the embedded XML resource shipped inside this assembly.
        /// 'RE' returns the reverse-engineer TreeState; 'DDL' returns the FE generation options.
        /// </summary>
        public static string LoadEmbeddedDefault(string type, Action<string> log)
        {
            string resource = string.Equals(type, "DDL", StringComparison.OrdinalIgnoreCase)
                ? EmbeddedDdlResource
                : EmbeddedReResource;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null)
                {
                    log?.Invoke($"XmlOption: embedded resource '{resource}' NOT found in assembly");
                    return null;
                }
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                log?.Invoke($"XmlOption: LoadEmbeddedDefault('{type}') error: {ex.Message}");
                return null;
            }
        }
    }
}
