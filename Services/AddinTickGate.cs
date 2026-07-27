#nullable enable
namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// The single place that answers "must this add-in UI-thread timer tick bail out?".
    ///
    /// <para>Seven periodic ticks run on erwin's UI thread and every one of them does live
    /// SCAPI/Win32 work. Each previously carried its own copy of the same two-term
    /// condition, so adding a third suspension source meant editing seven files and hoping
    /// none was missed - and one WAS missed, which is how the approval blocking-rule walk
    /// ran for 46 minutes with every timer firing straight through it (see
    /// <see cref="ModelWalkGate"/>). The condition now lives here once.</para>
    ///
    /// <para><b>Deliberately not used by <c>ErwinInputBlock</c>.</b> That class consumes
    /// <see cref="AlterWizardGate.IsOpen"/> and <see cref="MartSaveGate.IsActive"/>
    /// individually as inputs to erwin's input-block policy, which is a different question
    /// from "may a tick run". Routing it through here would make a model walk re-enable
    /// erwin's frame.</para>
    /// </summary>
    internal static class AddinTickGate
    {
        /// <summary>
        /// True when the calling timer tick must return immediately without touching SCAPI.
        /// </summary>
        /// <param name="site">
        /// Stable identifier of the calling tick, used for the suspended-tick census a
        /// model walk logs when it finishes. Keep these values stable - they are read off
        /// the Debug Log when diagnosing a slow walk.
        /// </param>
        public static bool ShouldSkip(string site)
        {
            // ModelWalkGate first: it is a plain volatile read, whereas AlterWizardGate.IsOpen
            // may run a (cached) Win32 title probe. Pure OR, so the order is behaviour-neutral.
            bool walking = ModelWalkGate.IsActive;
            if (!walking && !AlterWizardGate.IsOpen && !MartSaveGate.IsActive) return false;

            // Only a walk keeps a census; the wizard/Mart-save gates are diagnosed by their
            // own log lines and do not need per-tick counting.
            if (walking) ModelWalkGate.CountSkippedTick(site);
            return true;
        }
    }
}
