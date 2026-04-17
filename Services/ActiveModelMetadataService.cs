using System;
using System.Data;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Reads metadata about the currently-active erwin model: the MODEL_PATH UDP value
    /// and the DB-side MODEL.ID it maps to. Used to look up per-model XML_OPTION rows.
    /// </summary>
    public static class ActiveModelMetadataService
    {
        private const string ModelPathUdpKey = "Model.Physical.MODEL_PATH";

        /// <summary>
        /// Read the MODEL_PATH UDP value from the active erwin model. Returns null if
        /// the UDP isn't set or any access throws.
        /// </summary>
        public static string ReadModelPathUdp(dynamic scapi, dynamic currentPU, Action<string> log)
        {
            if (scapi == null || currentPU == null) return null;

            dynamic sess = null;
            bool ownSession = false;
            try
            {
                bool hasSession = false;
                try { hasSession = currentPU.HasSession(); }
                catch (Exception ex) { log?.Invoke($"ActiveModel: HasSession error: {ex.Message}"); }

                if (hasSession)
                {
                    int sc = 0;
                    try { sc = scapi.Sessions.Count; }
                    catch (Exception ex) { log?.Invoke($"ActiveModel: Sessions.Count error: {ex.Message}"); }

                    string puName = "";
                    try { puName = currentPU.Name?.ToString() ?? ""; } catch { }

                    for (int i = 0; i < sc; i++)
                    {
                        try
                        {
                            dynamic s = scapi.Sessions.Item(i);
                            bool open = false;
                            try { open = s.IsOpen(); } catch { }
                            string sPU = "";
                            try { sPU = s.PersistenceUnit?.Name?.ToString() ?? ""; } catch { }
                            if (open && sPU == puName) { sess = s; break; }
                        }
                        catch { /* keep looking */ }
                    }
                }

                if (sess == null)
                {
                    sess = scapi.Sessions.Add();
                    sess.Open(currentPU, 0, 0);
                    ownSession = true;
                }

                dynamic root = sess.ModelObjects.Root;
                string val = null;
                try { val = root.Properties(ModelPathUdpKey)?.Value?.ToString(); }
                catch (Exception ex) { log?.Invoke($"ActiveModel: read '{ModelPathUdpKey}' error: {ex.Message}"); }

                if (string.IsNullOrWhiteSpace(val))
                {
                    log?.Invoke($"ActiveModel: MODEL_PATH UDP not set on active model");
                    return null;
                }

                log?.Invoke($"ActiveModel: MODEL_PATH = '{val}'");
                return val;
            }
            catch (Exception ex)
            {
                log?.Invoke($"ActiveModel: ReadModelPathUdp outer error: {ex.Message}");
                return null;
            }
            finally
            {
                if (ownSession && sess != null)
                {
                    try { sess.Close(); }
                    catch (Exception ex) { log?.Invoke($"ActiveModel: temp session close error: {ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// Look up MODEL.ID by Path. Returns null if no row matches.
        /// </summary>
        public static int? LookupModelIdByPath(IDbConnection conn, string path, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT TOP 1 ID FROM MODEL WHERE Path = @path";
                var p = cmd.CreateParameter();
                p.ParameterName = "@path";
                p.Value = path;
                cmd.Parameters.Add(p);

                object result = cmd.ExecuteScalar();
                if (result == null || result is DBNull) return null;
                int id = Convert.ToInt32(result);
                log?.Invoke($"ActiveModel: MODEL.ID for Path='{path}' -> {id}");
                return id;
            }
            catch (Exception ex)
            {
                log?.Invoke($"ActiveModel: LookupModelIdByPath error: {ex.Message}");
                return null;
            }
        }
    }
}
