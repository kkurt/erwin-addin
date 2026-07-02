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
        ///
        /// Default behavior keeps the window-title fallback active for backward
        /// compatibility (Mart-bound PUs whose PU.Locator returns "" on r10.10
        /// rely on it). Callers iterating over multiple PUs MUST pass
        /// <paramref name="allowWindowTitleFallback"/> = false: the window
        /// title is a GLOBAL erwin state, not per-PU, so using it during
        /// iteration would attribute the active window's locator to every PU
        /// in the collection - including unrelated side-by-side local models -
        /// and poison switch detection.
        /// </summary>
        public static string Read(dynamic pu, Action<string> log = null) =>
            Read(pu, true, log);

        /// <summary>
        /// Overload with explicit control over the window-title fallback.
        /// Pass false when reading locators for multiple PUs in a loop
        /// (known-locator seeding, reconnect-timer iteration, etc.).
        /// </summary>
        public static string Read(dynamic pu, bool allowWindowTitleFallback, Action<string> log = null)
        {
            if (pu == null) return string.Empty;

            string value = ReadDirectProperty(pu, log);
            if (!string.IsNullOrEmpty(value)) return value;

            value = ReadPropertyBag(pu, false, log);
            if (!string.IsNullOrEmpty(value)) return value;

            value = ReadPropertyBag(pu, true, log);
            if (!string.IsNullOrEmpty(value)) return value;

            if (!allowWindowTitleFallback) return string.Empty;

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
                // Timeout-bounded read: ReadFromWindowTitle runs on the STA/UI
                // thread via the 500ms reconnect timer (count>1 tab-switch branch,
                // every tick with 2+ PUs open). A raw GetWindowText to a hung /
                // non-pumping main-frame thread would freeze erwin (same hang class
                // as the 2026-06-03 monitor-heartbeat dump). SMTO_ABORTIFHUNG
                // returns "" instead of blocking.
                var title = Win32Helper.GetWindowTextNoHang(hWnd);
                return ParseLocatorFromCaption(title);
            }
            catch (Exception ex)
            {
                AddinLogger.Log($"PuLocatorReader.ReadFromWindowTitle error: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Reads the locator of erwin's ACTIVE MDI child - the model tab the user is
        /// actually looking at. This is the ground truth for tab-switch detection:
        /// unlike the main-frame title, a modal dialog or compare wizard cannot
        /// overwrite an MDI child's caption, so the active model reads correctly even
        /// while a dialog is up. Returns the full locator plus the active child HWND in
        /// <paramref name="childHwnd"/>, or <see cref="string.Empty"/> / Zero when erwin
        /// is not using a standard MDI frame or no Mart model is active - in which case
        /// the caller falls back to <see cref="ReadFromWindowTitle"/>.
        /// </summary>
        public static string ReadFromActiveMdiChild(out IntPtr childHwnd)
        {
            childHwnd = IntPtr.Zero;
            try
            {
                var main = Win32Helper.GetErwinMainWindow();
                if (main == IntPtr.Zero) return string.Empty;
                IntPtr child = Win32Helper.GetActiveMdiChild(main);
                if (child == IntPtr.Zero) return string.Empty;
                string caption = Win32Helper.GetWindowTextNoHang(child);
                string loc = ParseLocatorFromCaption(caption);
                if (string.IsNullOrEmpty(loc)) return string.Empty;
                childHwnd = child;
                return loc;
            }
            catch (Exception ex)
            {
                AddinLogger.Log($"PuLocatorReader.ReadFromActiveMdiChild error: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Extracts the full Mart locator from an erwin window / MDI-child caption of
        /// the form <c>[Mart://&lt;path with spaces&gt; :  vN  : &lt;diagram&gt;]</c>.
        /// The locator PATH itself contains spaces ("Core Banking/CORE BANKING ..."),
        /// so the match runs from "Mart://" lazily up to the <c> : vN</c> version marker
        /// - the only <c> : v&lt;digits&gt;</c> in a Mart caption. A leading <c>[</c> is
        /// optional so this works on both the bracketed main-frame title and a bare MDI
        /// child caption. Returns <c>&lt;locator&gt;?VNO=N</c>, or <see cref="string.Empty"/>
        /// on no match (e.g. a non-Mart / file-only model is active - not an error).
        /// Public so the parse can be unit-tested against real captions without COM.
        /// </summary>
        public static string ParseLocatorFromCaption(string caption)
        {
            if (string.IsNullOrEmpty(caption)) return string.Empty;
            var m = Regex.Match(caption,
                @"(?<base>[Mm]art://.+?)\s*:\s*v(?<v>\d+)",
                RegexOptions.IgnoreCase);
            if (!m.Success) return string.Empty;
            var basePart = m.Groups["base"].Value.TrimEnd();
            var ver = m.Groups["v"].Value;
            return string.IsNullOrEmpty(ver) ? basePart : $"{basePart}?VNO={ver}";
        }
    }
}
