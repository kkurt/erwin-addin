using System;
using System.Data;
using System.Data.Common;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Persists Generate-DDL output to the admin DB's DDL_APPROVAL_QUEUE for
    /// later triage in the admin module. The addin INSERTs only; STATUS
    /// lifecycle (Pending/Approved/Rejected) is owned by the admin side.
    ///
    /// Uses <see cref="DatabaseService"/> for the config-based connection so
    /// the queue lands in the same DB that holds CONFIG / MODEL_CONFIG_MAPPING
    /// (i.e. whichever instance the user's bootstrap registry points at).
    /// </summary>
    public class DdlApprovalService
    {
        private static DdlApprovalService _instance;
        private static readonly object _lock = new object();

        public static DdlApprovalService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null) _instance = new DdlApprovalService();
                    }
                }
                return _instance;
            }
        }

        private DdlApprovalService() { }

        /// <summary>
        /// Insert a DDL approval row and return the new ID. Throws on any DB
        /// failure (no silent fallback per project rule; the caller surfaces
        /// the message in the popup's status strip so the user can retry).
        /// </summary>
        public int Submit(
            int configId,
            string modelName,
            string modelLocator,
            string sourceMode,
            string dbmsType,
            string ddlText,
            string note,
            Action<string> log,
            string status = "Pending")
        {
            if (configId <= 0)
                throw new ArgumentException("configId must be a resolved CONFIG.ID", nameof(configId));
            if (string.IsNullOrWhiteSpace(sourceMode))
                throw new ArgumentException("sourceMode must be set", nameof(sourceMode));
            if (string.IsNullOrEmpty(ddlText))
                throw new ArgumentException("ddlText must be non-empty", nameof(ddlText));
            if (string.IsNullOrWhiteSpace(status))
                throw new ArgumentException("status must be set", nameof(status));

            string dbType = DatabaseService.Instance.GetDbType();
            string submittedBy = SafeUserName();

            log?.Invoke($"DdlApproval.Submit: dbType={dbType}, configId={configId}, source={sourceMode}, status={status}, ddlLen={ddlText.Length}, hasNote={(string.IsNullOrEmpty(note) ? "no" : "yes")}");

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                int newId = dbType?.ToUpper() switch
                {
                    "POSTGRESQL" => InsertPostgres(conn, configId, modelName, modelLocator, sourceMode, dbmsType, ddlText, note, submittedBy, status),
                    "ORACLE"     => InsertOracle  (conn, configId, modelName, modelLocator, sourceMode, dbmsType, ddlText, note, submittedBy, status),
                    _            => InsertMssql   (conn, configId, modelName, modelLocator, sourceMode, dbmsType, ddlText, note, submittedBy, status),
                };
                log?.Invoke($"DdlApproval.Submit: inserted ID={newId}");
                return newId;
            }
        }

        /// <summary>
        /// Stamp the REST-callback outcome onto a queue row's CALLBACK_* columns.
        /// Used by the add-in's "Send" (no-approval) path after it fires the REST
        /// callback - mirrors the admin tool's
        /// <c>DdlApprovalService.RecordCallbackResult</c>. Throws on DB failure
        /// (no silent swallow); the caller surfaces it.
        /// </summary>
        public void RecordCallbackResult(int queueId, string callbackStatus, DateTime callbackAt, string callbackResponse, Action<string> log)
        {
            if (queueId <= 0)
                throw new ArgumentException("queueId must be a valid DDL_APPROVAL_QUEUE.ID", nameof(queueId));

            string dbType = DatabaseService.Instance.GetDbType();
            string sql = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"UPDATE ""DDL_APPROVAL_QUEUE"" SET ""CALLBACK_STATUS""=@s, ""CALLBACK_AT""=@t, ""CALLBACK_RESPONSE""=@r WHERE ""ID""=@id",
                "ORACLE"     => @"UPDATE DDL_APPROVAL_QUEUE SET CALLBACK_STATUS=:s, CALLBACK_AT=:t, CALLBACK_RESPONSE=:r WHERE ID=:id",
                _            => @"UPDATE [dbo].[DDL_APPROVAL_QUEUE] SET [CALLBACK_STATUS]=@s, [CALLBACK_AT]=@t, [CALLBACK_RESPONSE]=@r WHERE [ID]=@id",
            };
            bool oracle = string.Equals(dbType, "ORACLE", StringComparison.OrdinalIgnoreCase);
            string p(string n) => oracle ? ":" + n : "@" + n;

            log?.Invoke($"DdlApproval.RecordCallbackResult: id={queueId}, status={callbackStatus}");

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
                AddParam(cmd, p("s"),  DbType.String,   string.IsNullOrEmpty(callbackStatus)   ? (object)DBNull.Value : callbackStatus);
                AddParam(cmd, p("t"),  DbType.DateTime, callbackAt);
                AddParam(cmd, p("r"),  DbType.String,   string.IsNullOrEmpty(callbackResponse) ? (object)DBNull.Value : callbackResponse);
                AddParam(cmd, p("id"), DbType.Int32,    queueId);
                cmd.ExecuteNonQuery();
            }
        }

        private static int InsertMssql(DbConnection conn, int configId, string modelName, string modelLocator,
            string sourceMode, string dbmsType, string ddlText, string note, string submittedBy, string status)
        {
            // STATUS column is NOT NULL. The CREATE TABLE script defines a
            // DEFAULT 'Pending' constraint (DF_DDL_APPROVAL_QUEUE_STATUS) but
            // production deployments have been observed without that default
            // applied (user-reported 2026-05-30: "Cannot insert NULL into
            // column 'STATUS', table MetaRepoTmp.dbo.DDL_APPROVAL_QUEUE"),
            // so we set the initial status explicitly to be schema-drift
            // independent. Valid values per the table comment + workflow
            // model: 'Pending' / 'Approved' / 'Rejected'.
            // STATUS + SUBMITTED_AT both have DEFAULT constraints in the
            // CREATE TABLE script ('Pending' / SYSUTCDATETIME()) but production
            // deployments have been observed without those defaults applied
            // (user-reported 2026-05-30: NULL-into-STATUS then NULL-into-
            // SUBMITTED_AT). Set both explicitly so the INSERT is schema-drift
            // independent. STATUS literal 'Pending'; SUBMITTED_AT as a parameter
            // (DateTime.UtcNow) to stay dialect-portable.
            const string sql = @"
INSERT INTO [dbo].[DDL_APPROVAL_QUEUE]
    ([CONFIG_ID],[MODEL_NAME],[MODEL_LOCATOR],[SOURCE_MODE],[DBMS_TYPE],[DDL_TEXT],[NOTE],[STATUS],[SUBMITTED_BY],[SUBMITTED_AT])
OUTPUT INSERTED.[ID]
VALUES
    (@configId, @modelName, @modelLocator, @sourceMode, @dbmsType, @ddlText, @note, @status, @submittedBy, @submittedAt);";
            using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
            AddParam(cmd, "@configId",    DbType.Int32,    configId);
            AddParam(cmd, "@modelName",   DbType.String,   (object)modelName    ?? DBNull.Value);
            AddParam(cmd, "@modelLocator",DbType.String,   (object)modelLocator ?? DBNull.Value);
            AddParam(cmd, "@sourceMode",  DbType.String,   sourceMode);
            AddParam(cmd, "@dbmsType",    DbType.String,   (object)dbmsType     ?? DBNull.Value);
            AddParam(cmd, "@ddlText",     DbType.String,   ddlText);
            AddParam(cmd, "@note",        DbType.String,   string.IsNullOrEmpty(note) ? (object)DBNull.Value : note);
            AddParam(cmd, "@status",      DbType.String,   status);
            AddParam(cmd, "@submittedBy", DbType.String,   (object)submittedBy  ?? DBNull.Value);
            AddParam(cmd, "@submittedAt", DbType.DateTime, DateTime.UtcNow);
            var scalar = cmd.ExecuteScalar();
            return Convert.ToInt32(scalar);
        }

        private static int InsertOracle(DbConnection conn, int configId, string modelName, string modelLocator,
            string sourceMode, string dbmsType, string ddlText, string note, string submittedBy, string status)
        {
            // Oracle's RETURNING ... INTO needs an output parameter; the easier
            // cross-version path is to RETURN the new ID via a SELECT after
            // INSERT inside a single anonymous block.
            // Explicit STATUS = 'Pending' for the same schema-drift reason
            // documented in InsertMssql above.
            // Explicit STATUS + SUBMITTED_AT for the schema-drift reason
            // documented in InsertMssql.
            const string sql = @"
INSERT INTO DDL_APPROVAL_QUEUE
    (CONFIG_ID, MODEL_NAME, MODEL_LOCATOR, SOURCE_MODE, DBMS_TYPE, DDL_TEXT, NOTE, STATUS, SUBMITTED_BY, SUBMITTED_AT)
VALUES
    (:configId, :modelName, :modelLocator, :sourceMode, :dbmsType, :ddlText, :note, :status, :submittedBy, :submittedAt)
RETURNING ID INTO :newId";
            using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
            AddParam(cmd, ":configId",    DbType.Int32,    configId);
            AddParam(cmd, ":modelName",   DbType.String,   (object)modelName    ?? DBNull.Value);
            AddParam(cmd, ":modelLocator",DbType.String,   (object)modelLocator ?? DBNull.Value);
            AddParam(cmd, ":sourceMode",  DbType.String,   sourceMode);
            AddParam(cmd, ":dbmsType",    DbType.String,   (object)dbmsType     ?? DBNull.Value);
            AddParam(cmd, ":ddlText",     DbType.String,   ddlText);
            AddParam(cmd, ":note",        DbType.String,   string.IsNullOrEmpty(note) ? (object)DBNull.Value : note);
            AddParam(cmd, ":status",      DbType.String,   status);
            AddParam(cmd, ":submittedBy", DbType.String,   (object)submittedBy  ?? DBNull.Value);
            AddParam(cmd, ":submittedAt", DbType.DateTime, DateTime.UtcNow);
            var outp = cmd.CreateParameter();
            outp.ParameterName = ":newId";
            outp.DbType = DbType.Int32;
            outp.Direction = ParameterDirection.Output;
            cmd.Parameters.Add(outp);
            cmd.ExecuteNonQuery();
            return Convert.ToInt32(outp.Value);
        }

        private static int InsertPostgres(DbConnection conn, int configId, string modelName, string modelLocator,
            string sourceMode, string dbmsType, string ddlText, string note, string submittedBy, string status)
        {
            // Explicit STATUS + SUBMITTED_AT for the schema-drift reason
            // documented in InsertMssql.
            const string sql = @"
INSERT INTO ""DDL_APPROVAL_QUEUE""
    (""CONFIG_ID"",""MODEL_NAME"",""MODEL_LOCATOR"",""SOURCE_MODE"",""DBMS_TYPE"",""DDL_TEXT"",""NOTE"",""STATUS"",""SUBMITTED_BY"",""SUBMITTED_AT"")
VALUES
    (@configId, @modelName, @modelLocator, @sourceMode, @dbmsType, @ddlText, @note, @status, @submittedBy, @submittedAt)
RETURNING ""ID"";";
            using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
            AddParam(cmd, "@configId",    DbType.Int32,    configId);
            AddParam(cmd, "@modelName",   DbType.String,   (object)modelName    ?? DBNull.Value);
            AddParam(cmd, "@modelLocator",DbType.String,   (object)modelLocator ?? DBNull.Value);
            AddParam(cmd, "@sourceMode",  DbType.String,   sourceMode);
            AddParam(cmd, "@dbmsType",    DbType.String,   (object)dbmsType     ?? DBNull.Value);
            AddParam(cmd, "@ddlText",     DbType.String,   ddlText);
            AddParam(cmd, "@note",        DbType.String,   string.IsNullOrEmpty(note) ? (object)DBNull.Value : note);
            AddParam(cmd, "@status",      DbType.String,   status);
            AddParam(cmd, "@submittedBy", DbType.String,   (object)submittedBy  ?? DBNull.Value);
            AddParam(cmd, "@submittedAt", DbType.DateTime, DateTime.UtcNow);
            var scalar = cmd.ExecuteScalar();
            return Convert.ToInt32(scalar);
        }

        private static void AddParam(DbCommand cmd, string name, DbType type, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.DbType = type;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static string SafeUserName()
        {
            try { return Environment.UserName; }
            catch { return null; }
        }
    }
}
