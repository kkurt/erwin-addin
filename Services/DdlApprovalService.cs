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
            Action<string> log)
        {
            if (configId <= 0)
                throw new ArgumentException("configId must be a resolved CONFIG.ID", nameof(configId));
            if (string.IsNullOrWhiteSpace(sourceMode))
                throw new ArgumentException("sourceMode must be set", nameof(sourceMode));
            if (string.IsNullOrEmpty(ddlText))
                throw new ArgumentException("ddlText must be non-empty", nameof(ddlText));

            string dbType = DatabaseService.Instance.GetDbType();
            string submittedBy = SafeUserName();

            log?.Invoke($"DdlApproval.Submit: dbType={dbType}, configId={configId}, source={sourceMode}, ddlLen={ddlText.Length}, hasNote={(string.IsNullOrEmpty(note) ? "no" : "yes")}");

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                int newId = dbType?.ToUpper() switch
                {
                    "POSTGRESQL" => InsertPostgres(conn, configId, modelName, modelLocator, sourceMode, dbmsType, ddlText, note, submittedBy),
                    "ORACLE"     => InsertOracle  (conn, configId, modelName, modelLocator, sourceMode, dbmsType, ddlText, note, submittedBy),
                    _            => InsertMssql   (conn, configId, modelName, modelLocator, sourceMode, dbmsType, ddlText, note, submittedBy),
                };
                log?.Invoke($"DdlApproval.Submit: inserted ID={newId}");
                return newId;
            }
        }

        private static int InsertMssql(DbConnection conn, int configId, string modelName, string modelLocator,
            string sourceMode, string dbmsType, string ddlText, string note, string submittedBy)
        {
            const string sql = @"
INSERT INTO [dbo].[DDL_APPROVAL_QUEUE]
    ([CONFIG_ID],[MODEL_NAME],[MODEL_LOCATOR],[SOURCE_MODE],[DBMS_TYPE],[DDL_TEXT],[NOTE],[SUBMITTED_BY])
OUTPUT INSERTED.[ID]
VALUES
    (@configId, @modelName, @modelLocator, @sourceMode, @dbmsType, @ddlText, @note, @submittedBy);";
            using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
            AddParam(cmd, "@configId",    DbType.Int32,    configId);
            AddParam(cmd, "@modelName",   DbType.String,   (object)modelName    ?? DBNull.Value);
            AddParam(cmd, "@modelLocator",DbType.String,   (object)modelLocator ?? DBNull.Value);
            AddParam(cmd, "@sourceMode",  DbType.String,   sourceMode);
            AddParam(cmd, "@dbmsType",    DbType.String,   (object)dbmsType     ?? DBNull.Value);
            AddParam(cmd, "@ddlText",     DbType.String,   ddlText);
            AddParam(cmd, "@note",        DbType.String,   string.IsNullOrEmpty(note) ? (object)DBNull.Value : note);
            AddParam(cmd, "@submittedBy", DbType.String,   (object)submittedBy  ?? DBNull.Value);
            var scalar = cmd.ExecuteScalar();
            return Convert.ToInt32(scalar);
        }

        private static int InsertOracle(DbConnection conn, int configId, string modelName, string modelLocator,
            string sourceMode, string dbmsType, string ddlText, string note, string submittedBy)
        {
            // Oracle's RETURNING ... INTO needs an output parameter; the easier
            // cross-version path is to RETURN the new ID via a SELECT after
            // INSERT inside a single anonymous block.
            const string sql = @"
INSERT INTO DDL_APPROVAL_QUEUE
    (CONFIG_ID, MODEL_NAME, MODEL_LOCATOR, SOURCE_MODE, DBMS_TYPE, DDL_TEXT, NOTE, SUBMITTED_BY)
VALUES
    (:configId, :modelName, :modelLocator, :sourceMode, :dbmsType, :ddlText, :note, :submittedBy)
RETURNING ID INTO :newId";
            using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
            AddParam(cmd, ":configId",    DbType.Int32,    configId);
            AddParam(cmd, ":modelName",   DbType.String,   (object)modelName    ?? DBNull.Value);
            AddParam(cmd, ":modelLocator",DbType.String,   (object)modelLocator ?? DBNull.Value);
            AddParam(cmd, ":sourceMode",  DbType.String,   sourceMode);
            AddParam(cmd, ":dbmsType",    DbType.String,   (object)dbmsType     ?? DBNull.Value);
            AddParam(cmd, ":ddlText",     DbType.String,   ddlText);
            AddParam(cmd, ":note",        DbType.String,   string.IsNullOrEmpty(note) ? (object)DBNull.Value : note);
            AddParam(cmd, ":submittedBy", DbType.String,   (object)submittedBy  ?? DBNull.Value);
            var outp = cmd.CreateParameter();
            outp.ParameterName = ":newId";
            outp.DbType = DbType.Int32;
            outp.Direction = ParameterDirection.Output;
            cmd.Parameters.Add(outp);
            cmd.ExecuteNonQuery();
            return Convert.ToInt32(outp.Value);
        }

        private static int InsertPostgres(DbConnection conn, int configId, string modelName, string modelLocator,
            string sourceMode, string dbmsType, string ddlText, string note, string submittedBy)
        {
            const string sql = @"
INSERT INTO ""DDL_APPROVAL_QUEUE""
    (""CONFIG_ID"",""MODEL_NAME"",""MODEL_LOCATOR"",""SOURCE_MODE"",""DBMS_TYPE"",""DDL_TEXT"",""NOTE"",""SUBMITTED_BY"")
VALUES
    (@configId, @modelName, @modelLocator, @sourceMode, @dbmsType, @ddlText, @note, @submittedBy)
RETURNING ""ID"";";
            using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
            AddParam(cmd, "@configId",    DbType.Int32,    configId);
            AddParam(cmd, "@modelName",   DbType.String,   (object)modelName    ?? DBNull.Value);
            AddParam(cmd, "@modelLocator",DbType.String,   (object)modelLocator ?? DBNull.Value);
            AddParam(cmd, "@sourceMode",  DbType.String,   sourceMode);
            AddParam(cmd, "@dbmsType",    DbType.String,   (object)dbmsType     ?? DBNull.Value);
            AddParam(cmd, "@ddlText",     DbType.String,   ddlText);
            AddParam(cmd, "@note",        DbType.String,   string.IsNullOrEmpty(note) ? (object)DBNull.Value : note);
            AddParam(cmd, "@submittedBy", DbType.String,   (object)submittedBy  ?? DBNull.Value);
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
