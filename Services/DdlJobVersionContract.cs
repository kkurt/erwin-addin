namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// The LEFT/RIGHT version contract of a <c>DDL_GENERATION_QUEUE</c> row, and the
    /// diagnosis of a target version the DDL tab cannot offer.
    ///
    /// <para>The worker opens <c>LEFT_VERSION</c> as erwin's active model and picks
    /// <c>RIGHT_VERSION</c> from the Target (Right) combo. That combo is built from the
    /// OPEN model's version downwards (<c>RebuildRightCombo</c>) - never upwards -
    /// because the alter script is produced by applying the open model's differences
    /// INTO the target, i.e. it upgrades RIGHT to LEFT. So RIGHT must be &lt;= LEFT.</para>
    ///
    /// <para>Both rules used to live inline in the worker and produced one fixed
    /// sentence - "enable DDL_COMPARE_PREVIOUS_VERSIONS" - for every cause. In the
    /// field on 2026-07-30 that gate was already ON and the real cause was an inverted
    /// row (LEFT older than RIGHT), so the operator was sent after an admin toggle that
    /// was already correct while ten consecutive jobs failed. The text lives here,
    /// pure and unit-tested, because it is what the operator reads in the admin
    /// Auto-DDL grid (it is written verbatim into ERROR_MESSAGE).</para>
    /// </summary>
    public static class DdlJobVersionContract
    {
        /// <summary>
        /// Checks a claimed row BEFORE erwin is driven. A row that violates the
        /// contract can never succeed, so failing it here costs ~1 s instead of a full
        /// Mart open + adopt + close cycle (~20 s) that ends in the same failure.
        /// </summary>
        /// <param name="leftVersion">LEFT_VERSION - opened as the active model.</param>
        /// <param name="rightVersion">RIGHT_VERSION - the compare target.</param>
        /// <param name="error">Operator-facing reason, null when the row is valid.</param>
        /// <returns>True when the row can be attempted.</returns>
        public static bool TryValidateRow(int leftVersion, int rightVersion, out string error)
        {
            error = null;

            if (leftVersion <= 0)
            {
                error = $"invalid LEFT_VERSION v{leftVersion} - it must be a Mart version number (>= 1) to open as the active model.";
                return false;
            }
            if (rightVersion <= 0)
            {
                error = $"invalid RIGHT_VERSION v{rightVersion} - it must be a Mart version number (>= 1) to compare against.";
                return false;
            }
            if (rightVersion > leftVersion)
            {
                error = $"inverted queue row: RIGHT_VERSION=v{rightVersion} is NEWER than LEFT_VERSION=v{leftVersion}. " +
                        "The worker opens LEFT as the active model and compares it against RIGHT, and the generated alter script " +
                        "upgrades RIGHT to LEFT - so RIGHT_VERSION must be <= LEFT_VERSION. To script the move from deployed " +
                        $"v{leftVersion} to target v{rightVersion}, write LEFT_VERSION={rightVersion} and RIGHT_VERSION={leftVersion}.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Explains why <paramref name="requested"/> is absent from the Target (Right)
        /// list, naming the ACTUAL cause instead of always blaming one admin gate.
        /// Ordered most-specific first: no source enabled, newer than the open model,
        /// previous-versions gate off, then the residual case.
        /// </summary>
        /// <param name="requested">RIGHT_VERSION the job asked for.</param>
        /// <param name="activeVersion">Version of the model erwin actually has open (0/-1 = unknown).</param>
        /// <param name="allowLastSaved">Effective DDL_COMPARE_LAST_SAVED gate.</param>
        /// <param name="allowPreviousVersions">Effective DDL_COMPARE_PREVIOUS_VERSIONS gate.</param>
        /// <param name="targetListCsv">Verbatim contents of the Target combo.</param>
        /// <param name="listHasRealVersions">
        /// False when the combo only holds a placeholder (no From-Mart source enabled).
        /// </param>
        public static string ExplainMissingTarget(
            int requested,
            int activeVersion,
            bool allowLastSaved,
            bool allowPreviousVersions,
            string targetListCsv,
            bool listHasRealVersions)
        {
            if (!listHasRealVersions)
                return $"no Mart compare target is available for this model: the admin gates are " +
                       $"DDL_COMPARE_LAST_SAVED={allowLastSaved} / DDL_COMPARE_PREVIOUS_VERSIONS={allowPreviousVersions}, " +
                       $"so the Target list is [{targetListCsv}]. Enable at least one From-Mart source for this model/corporate.";

            if (activeVersion > 0 && requested > activeVersion)
                return $"requested RIGHT v{requested} is NEWER than the open model (v{activeVersion}), so it is not offered as a " +
                       "compare target. The Target list only holds versions <= the open one, because the alter script applies the " +
                       "OPEN model's differences INTO the target (it upgrades the target to the open version). To script " +
                       $"v{activeVersion} -> v{requested}, open v{requested} instead: set LEFT_VERSION={requested} and " +
                       $"RIGHT_VERSION={activeVersion} on the queue row. Target list: [{targetListCsv}].";

            if (!allowPreviousVersions && activeVersion > 0 && requested < activeVersion)
                return $"requested RIGHT v{requested} is an earlier version, but DDL_COMPARE_PREVIOUS_VERSIONS is OFF for this " +
                       $"model - only v{activeVersion} (dirty vs last saved) is offered. Enable it in the admin DDL Generation " +
                       $"gates. Target list: [{targetListCsv}].";

            return $"requested RIGHT v{requested} is not in the Target list [{targetListCsv}] (open model = v{activeVersion}) - " +
                   "the version may exist in Mart but was not offered; check this model's DDL Generation gates.";
        }
    }
}
