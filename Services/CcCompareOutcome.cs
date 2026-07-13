#nullable enable
using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Pure-text classification of erwin Complete Compare / Review outcome
    /// notifications. Kept free of any Win32/COM dependency so it is unit
    /// testable: the automation layer (<c>MartMartAutomation</c>) reads a
    /// message box's static text and asks this class what it means.
    /// </summary>
    public static class CcCompareOutcome
    {
        /// <summary>
        /// True when <paramref name="text"/> is erwin's "the compared sides are
        /// identical" notification (the box erwin raises after Compare instead
        /// of opening Resolve Differences when there is nothing to resolve).
        /// Matching is deliberately substring/case-insensitive: erwin's exact
        /// wording varies across releases and dialog contexts ("There are no
        /// differences...", "No differences were detected...", "...models are
        /// identical...", the Mart Review refusal "There have been no changes
        /// to model since it was checked out."). erwin r10 ships English-only
        /// dialog resources, so no Turkish variants are needed.
        /// </summary>
        /// <param name="text">Concatenated static text of the message box
        /// (null/empty returns false).</param>
        public static bool IsNoDifferenceInfoText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Contains(text, "no difference")            // "There are no differences..." / "No differences were detected..."
                || Contains(text, "are identical")            // "...the models are identical."
                || Contains(text, "there have been no changes") // Mart Review refusal wording (also a no-diff verdict)
                || Contains(text, "nothing to compare");
        }

        private static bool Contains(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
