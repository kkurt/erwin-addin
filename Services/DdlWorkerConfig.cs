#nullable enable
using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>Mart authentication method for the DDL-generator instance.</summary>
    public enum MartAuthType
    {
        /// <summary>Windows/integrated auth - erwin's Connect dialog needs no credentials filled.</summary>
        Windows,
        /// <summary>Server auth - User Name + Password are typed into the Connect dialog.</summary>
        Server,
    }

    /// <summary>
    /// One active row of DDL_GENERATION_CONF: how the DDL-generator instance
    /// logs into Mart and how often it pings to keep the session alive. Pure
    /// data + pure logic (no DB/COM) so the parsing and keep-alive rules are
    /// unit-testable; <see cref="DdlWorkerConfigService"/> reads the row and
    /// builds this object with the credentials already decrypted.
    /// </summary>
    public sealed class DdlWorkerConfig
    {
        public MartAuthType AuthType { get; init; }

        /// <summary>Decrypted Mart user name (Server auth only; null/empty for Windows auth).</summary>
        public string? UserName { get; init; }

        /// <summary>Decrypted Mart password (Server auth only; null/empty for Windows auth).</summary>
        public string? Password { get; init; }

        /// <summary>Optional server host to type into the Connect dialog; null/empty = keep erwin's remembered value.</summary>
        public string? MartServer { get; init; }

        /// <summary>Optional port to type into the Connect dialog; null = keep erwin's remembered value.</summary>
        public int? MartPort { get; init; }

        /// <summary>Whether the Mart connection uses SSL/HTTPS (drives the "Use SSL" checkbox in the Connect dialog).</summary>
        public bool UseSsl { get; init; }

        /// <summary>Keep-alive ping interval in minutes (>= 1).</summary>
        public int KeepAliveMinutes { get; init; }

        /// <summary>The corporate this config row belongs to (DDL_GENERATION_CONF.CORPORATE_ID) - carried for logging.</summary>
        public int CorporateId { get; init; }

        /// <summary>
        /// Parses the DB MART_AUTH_TYPE text into <see cref="MartAuthType"/>.
        /// Accepts 'WINDOWS'/'SERVER' case-insensitively (the DB CHECK
        /// constraint enforces those two, but the add-in must not crash on a
        /// hand-edited value). Unknown/blank defaults to <see cref="MartAuthType.Windows"/>
        /// (the safe, credential-less path) and reports it via
        /// <paramref name="recognized"/> so the caller can log the fallback.
        /// </summary>
        public static MartAuthType ParseAuthType(string? raw, out bool recognized)
        {
            recognized = true;
            string v = (raw ?? string.Empty).Trim();
            if (v.Equals("SERVER", StringComparison.OrdinalIgnoreCase)) return MartAuthType.Server;
            if (v.Equals("WINDOWS", StringComparison.OrdinalIgnoreCase)) return MartAuthType.Windows;
            recognized = false;
            return MartAuthType.Windows;
        }

        /// <summary>
        /// Clamps a raw KEEPALIVE_MINUTES value to a sane range. The DB CHECK
        /// enforces &gt;= 1, but a hand-edited or NULL value must never produce
        /// a zero/negative interval (which would ping continuously) or an
        /// absurdly large one. Null/&lt;1 -&gt; default 5; capped at 1440 (a day).
        /// </summary>
        public static int NormalizeKeepAliveMinutes(int? raw)
        {
            int v = raw ?? 5;
            if (v < 1) return 5;
            if (v > 1440) return 1440;
            return v;
        }

        /// <summary>
        /// Decides whether a keep-alive ping is due. A ping keeps the Mart
        /// login from timing out; it must run ONLY when the worker is otherwise
        /// idle. Returns true iff nothing is busy AND at least
        /// <paramref name="keepAliveMinutes"/> have elapsed since the last Mart
        /// activity (login, previous ping, or a completed job).
        /// </summary>
        /// <param name="lastActivityUtc">Timestamp of the last Mart activity.</param>
        /// <param name="nowUtc">Current time.</param>
        /// <param name="keepAliveMinutes">Configured interval.</param>
        /// <param name="jobActive">A DDL job/pipeline is running - never ping.</param>
        /// <param name="pingActive">A ping is already in flight - never overlap.</param>
        public static bool IsKeepAliveDue(
            DateTime lastActivityUtc, DateTime nowUtc, int keepAliveMinutes,
            bool jobActive, bool pingActive)
        {
            if (jobActive || pingActive) return false;
            if (keepAliveMinutes < 1) keepAliveMinutes = 5;
            return (nowUtc - lastActivityUtc) >= TimeSpan.FromMinutes(keepAliveMinutes);
        }
    }
}
