using System;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Reads the <c>Locator</c> string of a SCAPI persistence unit through the
    /// fallback chain that has proven necessary on erwin DM r10.10:
    /// <list type="number">
    ///   <item><c>pu.Locator</c> direct property (often "" for fresh Mart PUs).</item>
    ///   <item><c>pu.PropertyBag().Value("Locator")</c>.</item>
    ///   <item><c>pu.PropertyBag(null, true).Value("Locator")</c> (bag with derived strings).</item>
    ///   <item>erwin main window title, e.g.
    ///         <c>erwin DM - [Mart://Mart/&lt;lib&gt;/&lt;model&gt; : vN : ...]</c>.</item>
    /// </list>
    /// All exceptions are logged but never propagated; callers receive
    /// <see cref="string.Empty"/> when every layer fails.
    /// </summary>
    public static class PuLocatorReader
    {
        /// <summary>
        /// Returns the active locator for <paramref name="pu"/>, or
        /// <see cref="string.Empty"/> when nothing is readable. <paramref name="log"/>
        /// receives one line per failed attempt for diagnostics.
        /// </summary>
        public static string Read(dynamic pu, Action<string> log = null)
        {
            if (pu == null) return string.Empty;

            string value = ReadDirectProperty(pu, log);
            if (!string.IsNullOrEmpty(value)) return value;

            value = ReadPropertyBag(pu, false, log);
            if (!string.IsNullOrEmpty(value)) return value;

            value = ReadPropertyBag(pu, true, log);
            if (!string.IsNullOrEmpty(value)) return value;

            value = ReadFromWindowTitle();
            if (!string.IsNullOrEmpty(value))
                log?.Invoke("PuLocatorReader: locator recovered from window title");
            return value ?? string.Empty;
        }

        private static string ReadDirectProperty(dynamic pu, Action<string> log)
        {
            try
            {
                object raw = pu.Locator;
                return raw?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                log?.Invoke($"PuLocatorReader: pu.Locator threw: {ex.GetType().Name}: {ex.Message}");
                return string.Empty;
            }
        }

        private static string ReadPropertyBag(dynamic pu, bool useDerivedValues, Action<string> log)
        {
            try
            {
                dynamic bag = useDerivedValues ? pu.PropertyBag(null, true) : pu.PropertyBag();
                object raw = bag?.Value("Locator");
                return raw?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                string variant = useDerivedValues ? "PropertyBag(null,true)" : "PropertyBag()";
                log?.Invoke($"PuLocatorReader: pu.{variant}.Value(Locator) threw: {ex.GetType().Name}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Last-resort: scrape the erwin main window title for a bracketed
        /// <c>Mart://...</c> stem and append <c>?VNO=N</c> when the version
        /// segment is present. Returns empty when the title doesn't look like
        /// a Mart-hosted model (file-only edits or no model loaded).
        /// </summary>
        public static string ReadFromWindowTitle()
        {
            try
            {
                var hWnd = Win32Helper.GetErwinMainWindow();
                if (hWnd == IntPtr.Zero) return string.Empty;
                var sb = new System.Text.StringBuilder(1024);
                Win32Helper.GetWindowTextPublic(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                var m = Regex.Match(title,
                    @"\[(?<base>(?:[Mm]art://)[^\s\]]+)(?:\s*:\s*v(?<v>\d+))?",
                    RegexOptions.IgnoreCase);
                if (!m.Success) return string.Empty;
                var basePart = m.Groups["base"].Value;
                var ver = m.Groups["v"].Value;
                return string.IsNullOrEmpty(ver) ? basePart : $"{basePart}?VNO={ver}";
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
