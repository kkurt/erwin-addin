using System;
using System.Data;
using System.Data.Common;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// A single claimed DDL_GENERATION_QUEUE job.
    /// </summary>
    public sealed class DdlQueueJob
    {
        public int Id { get; set; }
        public string ModelPath { get; set; }
        public int LeftVersion { get; set; }
        public int RightVersion { get; set; }
        /// <summary>How many times this job has already been attempted+requeued (transient failures).</summary>
        public int RetryCount { get; set; }
    }

    /// <summary>
    /// DB access for the unattended DDL worker's job queue (DDL_GENERATION_QUEUE).
    /// Pure DB concern, no COM/SCAPI - the orchestration (open model + run the
    /// pipeline) lives in ModelConfigForm.DdlWorker because it needs the form's
    /// SCAPI session and the Generate-DDL pipeline.
    ///
    /// Mirrors <see cref="DdlApprovalService"/>: dialect-agnostic via
    /// <see cref="DatabaseService"/> (GetDbType switch, per-dialect identifier
    /// quoting and parameter prefix). Throws on DB failure (no silent swallow per
    /// project rule); the worker logs + skips that tick.
    ///
    /// STATUS lifecycle: PENDING -> RUNNING (claim) -> DONE | FAILED (finalize).
    /// </summary>
    public class DdlQueueService
    {
        private static DdlQueueService _instance;
        private static readonly object _lock = new object();

        public static DdlQueueService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null) _instance = new DdlQueueService();
                    }
                }
                return _instance;
            }
        }

        private DdlQueueService() { }

        /// <summary>
        /// Atomically claim the oldest PENDING job: read it, then a conditional
        /// UPDATE (WHERE STATUS='PENDING') flips it to RUNNING. Returns the job if
        /// the claim won (rows-affected == 1), else null (no pending / lost race).
        /// Single worker means the race is effectively impossible, but the
        /// conditional UPDATE keeps it correct regardless.
        /// </summary>
        public DdlQueueJob TryClaimNextPending(Action<string> log)
        {
            string dbType = DatabaseService.Instance.GetDbType();
            bool oracle = string.Equals(dbType, "ORACLE", StringComparison.OrdinalIgnoreCase);
            string p(string n) => oracle ? ":" + n : "@" + n;

            string selectSql = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"SELECT ""ID"",""MODEL_PATH"",""LEFT_VERSION"",""RIGHT_VERSION"",""RETRY_COUNT"" FROM ""DDL_GENERATION_QUEUE"" WHERE ""STATUS""='PENDING' ORDER BY ""CREATED_AT"" ASC, ""ID"" ASC LIMIT 1",
                "ORACLE"     => @"SELECT ID, MODEL_PATH, LEFT_VERSION, RIGHT_VERSION, RETRY_COUNT FROM DDL_GENERATION_QUEUE WHERE STATUS='PENDING' ORDER BY CREATED_AT ASC, ID ASC FETCH FIRST 1 ROWS ONLY",
                _            => @"SELECT TOP 1 [ID],[MODEL_PATH],[LEFT_VERSION],[RIGHT_VERSION],[RETRY_COUNT] FROM [dbo].[DDL_GENERATION_QUEUE] WHERE [STATUS]='PENDING' ORDER BY [CREATED_AT] ASC, [ID] ASC",
            };
            string claimSql = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"UPDATE ""DDL_GENERATION_QUEUE"" SET ""STATUS""='RUNNING', ""CLAIMED_BY""=@by, ""STARTED_AT""=@at WHERE ""ID""=@id AND ""STATUS""='PENDING'",
                "ORACLE"     => @"UPDATE DDL_GENERATION_QUEUE SET STATUS='RUNNING', CLAIMED_BY=:by, STARTED_AT=:at WHERE ID=:id AND STATUS='PENDING'",
                _            => @"UPDATE [dbo].[DDL_GENERATION_QUEUE] SET [STATUS]='RUNNING', [CLAIMED_BY]=@by, [STARTED_AT]=@at WHERE [ID]=@id AND [STATUS]='PENDING'",
            };

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();

                int id;
                string modelPath;
                int leftV;
                int rightV;
                int retryCount;
                using (var sel = DatabaseService.Instance.CreateCommand(selectSql, conn))
                using (var reader = sel.ExecuteReader())
                {
                    if (!reader.Read()) return null; // queue empty
                    id = Convert.ToInt32(reader["ID"]);
                    modelPath = reader["MODEL_PATH"]?.ToString() ?? string.Empty;
                    leftV = Convert.ToInt32(reader["LEFT_VERSION"]);
                    rightV = Convert.ToInt32(reader["RIGHT_VERSION"]);
                    retryCount = reader["RETRY_COUNT"] == DBNull.Value ? 0 : Convert.ToInt32(reader["RETRY_COUNT"]);
                }

                using (var upd = DatabaseService.Instance.CreateCommand(claimSql, conn))
                {
                    AddParam(upd, p("by"), DbType.String, (object)SafeUserName() ?? DBNull.Value);
                    AddParam(upd, p("at"), DbType.DateTime, DateTime.UtcNow);
                    AddParam(upd, p("id"), DbType.Int32, id);
                    int rows = upd.ExecuteNonQuery();
                    if (rows != 1)
                    {
                        log?.Invoke($"DdlQueue: claim lost for id={id} (rows affected={rows}) - another claimer or status changed");
                        return null;
                    }
                }

                log?.Invoke($"DdlQueue: claimed job id={id} model='{modelPath}' left=v{leftV} right=v{rightV} retry={retryCount}");
                return new DdlQueueJob { Id = id, ModelPath = modelPath, LeftVersion = leftV, RightVersion = rightV, RetryCount = retryCount };
            }
        }

        /// <summary>Finalize a job as DONE with the produced DDL.</summary>
        public void WriteResult(int id, string ddl, Action<string> log)
        {
            string sql = StatusUpdateSql(resultColumn: true);
            RunFinalize(id, sql, "DONE", ddlOrError: ddl, log: log);
            log?.Invoke($"DdlQueue: job id={id} -> DONE (ddlLen={ddl?.Length ?? 0})");
        }

        /// <summary>Finalize a job as FAILED with an error message.</summary>
        public void WriteFailure(int id, string error, Action<string> log)
        {
            string sql = StatusUpdateSql(resultColumn: false);
            RunFinalize(id, sql, "FAILED", ddlOrError: error, log: log);
            log?.Invoke($"DdlQueue: job id={id} -> FAILED ({error})");
        }

        /// <summary>
        /// Transient failure: put the job back to PENDING (so it is re-attempted),
        /// record the last error, increment RETRY_COUNT, and release the claim
        /// (STARTED_AT / CLAIMED_BY cleared). Used for environmental failures like
        /// "erwin not connected to Mart" so the job is not lost - it runs once the
        /// condition clears. The worker applies its own backoff before re-claiming.
        /// </summary>
        public void RequeueForRetry(int id, string error, Action<string> log)
        {
            if (id <= 0) throw new ArgumentException("id must be a valid DDL_GENERATION_QUEUE.ID", nameof(id));

            string dbType = DatabaseService.Instance.GetDbType();
            bool oracle = string.Equals(dbType, "ORACLE", StringComparison.OrdinalIgnoreCase);
            string p(string n) => oracle ? ":" + n : "@" + n;

            string sql = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"UPDATE ""DDL_GENERATION_QUEUE"" SET ""STATUS""='PENDING', ""ERROR_MESSAGE""=@err, ""RETRY_COUNT""=""RETRY_COUNT""+1, ""STARTED_AT""=NULL, ""CLAIMED_BY""=NULL WHERE ""ID""=@id",
                "ORACLE"     => @"UPDATE DDL_GENERATION_QUEUE SET STATUS='PENDING', ERROR_MESSAGE=:err, RETRY_COUNT=RETRY_COUNT+1, STARTED_AT=NULL, CLAIMED_BY=NULL WHERE ID=:id",
                _            => @"UPDATE [dbo].[DDL_GENERATION_QUEUE] SET [STATUS]='PENDING', [ERROR_MESSAGE]=@err, [RETRY_COUNT]=[RETRY_COUNT]+1, [STARTED_AT]=NULL, [CLAIMED_BY]=NULL WHERE [ID]=@id",
            };

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
                AddParam(cmd, p("err"), DbType.String, string.IsNullOrEmpty(error) ? (object)DBNull.Value : error);
                AddParam(cmd, p("id"),  DbType.Int32,  id);
                cmd.ExecuteNonQuery();
            }
            log?.Invoke($"DdlQueue: job id={id} -> requeued PENDING (retry; last error: {error})");
        }

        private static string StatusUpdateSql(bool resultColumn)
        {
            string dbType = DatabaseService.Instance.GetDbType();
            // resultColumn==true writes RESULT_DDL (success), false writes ERROR_MESSAGE.
            return dbType?.ToUpper() switch
            {
                "POSTGRESQL" => resultColumn
                    ? @"UPDATE ""DDL_GENERATION_QUEUE"" SET ""STATUS""=@st, ""RESULT_DDL""=@val, ""FINISHED_AT""=@at WHERE ""ID""=@id"
                    : @"UPDATE ""DDL_GENERATION_QUEUE"" SET ""STATUS""=@st, ""ERROR_MESSAGE""=@val, ""FINISHED_AT""=@at WHERE ""ID""=@id",
                "ORACLE" => resultColumn
                    ? @"UPDATE DDL_GENERATION_QUEUE SET STATUS=:st, RESULT_DDL=:val, FINISHED_AT=:at WHERE ID=:id"
                    : @"UPDATE DDL_GENERATION_QUEUE SET STATUS=:st, ERROR_MESSAGE=:val, FINISHED_AT=:at WHERE ID=:id",
                _ => resultColumn
                    ? @"UPDATE [dbo].[DDL_GENERATION_QUEUE] SET [STATUS]=@st, [RESULT_DDL]=@val, [FINISHED_AT]=@at WHERE [ID]=@id"
                    : @"UPDATE [dbo].[DDL_GENERATION_QUEUE] SET [STATUS]=@st, [ERROR_MESSAGE]=@val, [FINISHED_AT]=@at WHERE [ID]=@id",
            };
        }

        private static void RunFinalize(int id, string sql, string status, string ddlOrError, Action<string> log)
        {
            if (id <= 0) throw new ArgumentException("id must be a valid DDL_GENERATION_QUEUE.ID", nameof(id));

            string dbType = DatabaseService.Instance.GetDbType();
            bool oracle = string.Equals(dbType, "ORACLE", StringComparison.OrdinalIgnoreCase);
            string p(string n) => oracle ? ":" + n : "@" + n;

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using var cmd = DatabaseService.Instance.CreateCommand(sql, conn);
                AddParam(cmd, p("st"),  DbType.String,   status);
                AddParam(cmd, p("val"), DbType.String,   string.IsNullOrEmpty(ddlOrError) ? (object)DBNull.Value : ddlOrError);
                AddParam(cmd, p("at"),  DbType.DateTime, DateTime.UtcNow);
                AddParam(cmd, p("id"),  DbType.Int32,    id);
                cmd.ExecuteNonQuery();
            }
        }

        private static void AddParam(DbCommand cmd, string name, DbType type, object value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.DbType = type;
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }

        private static string SafeUserName()
        {
            try { return Environment.UserName; }
            catch { return null; }
        }
    }
}
