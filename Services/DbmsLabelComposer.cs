using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Composes erwin's "{Brand} {Version}" DBMS display label from the raw
    /// PropertyBag <c>Target_Server</c> brand name and the
    /// <c>Target_Server_Version</c> engine-major number. Pure and side-effect
    /// free so the model/config DBMS-mismatch label and its comparison key are
    /// unit-testable in isolation - this surface has regressed twice already:
    /// the 2026-05-16 "Oracle 19c on a SQL Server model" small-int mapping bug,
    /// and the 2026-06-22 raw "SQL Server 15" internal-version leak.
    /// </summary>
    /// <remarks>
    /// Both Oracle and SQL Server expose an INTERNAL engine major version in
    /// <c>Target_Server_Version</c> (Oracle 19/21, SQL Server 13/15), NOT the
    /// marketing label erwin's status bar and the admin
    /// <c>DBMS_VERSION.VERSION_CODE</c> use. We normalize to the label erwin
    /// itself shows so the PropertyBag fallback path produces the same string
    /// as the primary status-bar scrape path:
    /// <list type="bullet">
    ///   <item>Oracle 19     -> "Oracle 19c" (era suffix: &lt;=9 i, &lt;=11 g, else c)</item>
    ///   <item>SQL Server 15 -> "SQL Server 2019/2022" (erwin groups engines 15+16)</item>
    ///   <item>SQL Server 13 -> "SQL Server 2016/2017" (erwin groups engines 13+14)</item>
    /// </list>
    /// erwin DM r10.10 groups the newer SQL Server engines into release pairs,
    /// which is exactly what its status bar shows ("SQL Server 2019/2022"). An
    /// unrecognized SQL Server major (a future engine) falls through to raw
    /// passthrough rather than an invented pairing, so a new release surfaces
    /// visibly as "SQL Server &lt;n&gt;" and prompts a one-line map update.
    /// Brands without a comparable convention keep their version as-is.
    /// </remarks>
    public static class DbmsLabelComposer
    {
        /// <summary>
        /// Maps a (brand, engine-major) pair to erwin's display label. Returns
        /// <paramref name="brand"/> unchanged when <paramref name="version"/>
        /// is blank.
        /// </summary>
        public static string Compose(string brand, string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return brand;

            string v = version.Trim();

            if (string.Equals(brand, "Oracle", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(v, out int oracleMajor))
            {
                string suffix = oracleMajor switch
                {
                    <= 9  => "i",  // 8i, 9i
                    <= 11 => "g",  // 10g, 11g
                    _     => "c",  // 12c, 18c, 19c, 21c, 23c, ...
                };
                return $"Oracle {oracleMajor}{suffix}";
            }

            if (string.Equals(brand, "SQL Server", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(v, out int sqlMajor))
            {
                // SQL Server engine major -> erwin DM r10.10 target label. The
                // newer engines are grouped into the release pairs erwin's
                // status bar shows; older standalone targets keep a single year.
                string label = sqlMajor switch
                {
                    8        => "2000",
                    9        => "2005",
                    10       => "2008",
                    11       => "2012",
                    12       => "2014",
                    13 or 14 => "2016/2017",
                    15 or 16 => "2019/2022",
                    _        => null,
                };
                if (label != null) return $"SQL Server {label}";
            }

            return $"{brand} {v}";
        }
    }
}
