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
    ///   1. XML_OPTION row matching MODEL_ID = active model's id (resolved via MODEL_PATH UDP)
    ///   2. XML_OPTION row matching MODEL_ID = 1 (global "All Projects" fallback)
    ///   3. Embedded default XML bundled inside this assembly
    ///   4. Returns null — caller decides whether to skip the option path argument
    /// </summary>
    public static class XmlOptionLoaderService
    {
        private const int AllProjectsModelId = 1;
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
        /// <param name="modelId">Active-model id (or null to skip step 1).</param>
        /// <param name="type">'RE' or 'DDL'.</param>
        /// <param name="log">Logger.</param>
        public static string LoadAndWriteToTempFile(IDbConnection conn, int? modelId, string type, Action<string> log)
        {
            string xml = ResolveXml(conn, modelId, type, log);
            if (string.IsNullOrEmpty(xml))
            {
                log?.Invoke($"XmlOption: no XML resolved for type='{type}' modelId={modelId}, will skip option path");
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
        public static string ResolveXml(IDbConnection conn, int? modelId, string type, Action<string> log)
        {
            // 1) Active-model specific row
            if (modelId.HasValue)
            {
                string xml = ReadXmlOption(conn, modelId.Value, type, log);
                if (!string.IsNullOrEmpty(xml))
                {
                    log?.Invoke($"XmlOption: matched MODEL_ID={modelId.Value} TYPE='{type}'");
                    return xml;
                }
            }

            // 2) Global "All Projects" fallback (MODEL.ID = 1)
            string globalXml = ReadXmlOption(conn, AllProjectsModelId, type, log);
            if (!string.IsNullOrEmpty(globalXml))
            {
                log?.Invoke($"XmlOption: matched MODEL_ID={AllProjectsModelId} (All Projects) TYPE='{type}'");
                return globalXml;
            }

            // 3) Embedded default — ONLY for RE. DDL embedded default has hardcoded
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

        private static string ReadXmlOption(IDbConnection conn, int modelId, string type, Action<string> log)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT CONTENT FROM XML_OPTION WHERE MODEL_ID = @modelId AND TYPE = @type";
                AddParam(cmd, "@modelId", modelId);
                AddParam(cmd, "@type", type);

                object result = cmd.ExecuteScalar();
                if (result == null || result is DBNull) return null;
                string s = result.ToString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch (Exception ex)
            {
                log?.Invoke($"XmlOption: ReadXmlOption(MODEL_ID={modelId}, TYPE='{type}') error: {ex.Message}");
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
