using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Probes the active persistence unit's dirty state. Used by
    /// <see cref="ModelConfigForm"/>'s save-with-description dirty-gate to skip
    /// the Mart save when the model has no unsaved changes, and to verify the
    /// commit actually flushed the dirty buffer afterwards.
    /// </summary>
    public sealed class VersionCompareService
    {
        /// <summary>Dirty-state probe result reused by the UI for labelling.</summary>
        public readonly record struct DirtyProbe(bool IsDirty, string Source);

        private readonly dynamic _activePU;

        public VersionCompareService(dynamic activePU, Action<string> log)
        {
            _activePU = activePU ?? throw new ArgumentNullException(nameof(activePU));
            _ = log; // signature kept for caller compatibility; not needed by ProbeDirty
        }

        public DirtyProbe ProbeDirty()
        {
            foreach (var prop in new[] { "Modified", "IsModified", "IsDirty", "Dirty", "HasChanges" })
            {
                try
                {
                    object target = (object)_activePU;
                    var val = target.GetType().InvokeMember(
                        prop,
                        System.Reflection.BindingFlags.GetProperty,
                        binder: null,
                        target: target,
                        args: null);
                    if (val != null && bool.TryParse(val.ToString(), out var b))
                        return new DirtyProbe(b, prop);
                }
                catch { /* keep probing */ }
            }
            return new DirtyProbe(true, "(unknown)");
        }
    }
}
