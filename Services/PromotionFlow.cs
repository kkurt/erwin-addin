#nullable enable

using System;
using System.Collections.Generic;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Everything the Send-to-Approval UI needs to offer a promotion for the
    /// open model: the config's environment topology, the candidate routes for
    /// the version being promoted, and the dirty/version facts the routes were
    /// planned against. Built once right before the review dialog opens.
    /// </summary>
    /// <param name="ConfigId">Resolved CONFIG.ID of the open model.</param>
    /// <param name="MartPath">Canonical root-relative Mart path (the PROMOTION MODEL_LOCATOR).</param>
    /// <param name="Environments">All environments of the config, SORT_ORDER order.</param>
    /// <param name="Relations">All transitions of the config.</param>
    /// <param name="Routes">Candidate promotion routes per <see cref="PromotionPlanner.BuildRoutes"/>; empty = nothing to offer.</param>
    /// <param name="PlanningVersion">The version routes were planned for; null = the send-time save will mint a fresh version (no environment can hold it).</param>
    /// <param name="ModelCleanByTitle">True when the title-asterisk probe read a POSITIVE clean (the send will skip the Mart save and promote the open version).</param>
    /// <param name="ApproversByRelationId">Per-transition approver names (ENVIRONMENT_RELATION_APPROVER, SORT_ORDER order) for every relation appearing in <see cref="Routes"/> - preloaded so the dialog needs no DB read to show the approval/auto-approve indicator.</param>
    public sealed record PromotionSendContext(
        int ConfigId,
        string MartPath,
        IReadOnlyList<IntegrationEnvironment> Environments,
        IReadOnlyList<IntegrationRelation> Relations,
        IReadOnlyList<PromotionRoute> Routes,
        int? PlanningVersion,
        bool ModelCleanByTitle,
        IReadOnlyDictionary<int, IReadOnlyList<string>> ApproversByRelationId);

    /// <summary>
    /// Result of the promotion-aware Mart save step. Unlike the plain DDL push
    /// save, a promotion MUST know the exact version it promotes
    /// (MODEL_VERSION is a spec precondition), so the outcome carries the
    /// version number or a hard failure reason - never a guess.
    /// </summary>
    /// <param name="Ok">The step succeeded and <see cref="PromotedVersion"/> is known.</param>
    /// <param name="PromotedVersion">The Mart version the promotion records; null only when <see cref="Ok"/> is false.</param>
    /// <param name="SavedNewVersion">True when a Mart save ran (dirty model); false when the clean model's open version is promoted as-is.</param>
    /// <param name="FailReason">User-facing reason when <see cref="Ok"/> is false.</param>
    public sealed record PromotionSaveOutcome(
        bool Ok,
        int? PromotedVersion,
        bool SavedNewVersion,
        string? FailReason);

    /// <summary>
    /// Orchestration glue of the Version Promotion send flow: the feature
    /// gate, the effective lock type and the send-context build. Pure decision
    /// logic stays in <see cref="PromotionPlanner"/>, DB writes in
    /// <see cref="PromotionService"/>; this class only composes them so the
    /// form/dialog code stays thin. DB or config failures are NOT swallowed.
    /// </summary>
    public static class PromotionFlow
    {
        /// <summary>CONFIG_PROPERTY / CORPORATE_PROPERTY key of the feature gate.</summary>
        public const string EnabledKey = "VERSION_PROMOTION_ENABLED";

        /// <summary>CONFIG_PROPERTY / CORPORATE_PROPERTY key of the lock setting.</summary>
        public const string LockTypeKey = "PROMOTION_LOCK_TYPE";

        /// <summary>
        /// Master gate of the feature for the open model: initialized Mart
        /// model with a resolved config AND effective VERSION_PROMOTION_ENABLED
        /// (built-in default False). Mirrors the IsIntegrateEnabled pattern;
        /// the two flags are independent by spec.
        /// </summary>
        public static bool IsPromotionEnabled()
        {
            var ctx = ConfigContextService.Instance;
            if (!ctx.IsInitialized || !ctx.IsMartModel || ctx.ActiveConfigId <= 0) return false;
            return ctx.GetEffectiveBool(EnabledKey, false);
        }

        /// <summary>
        /// The lock a send should apply, and whether the admin EXPLICITLY
        /// requested it. Pure and testable (the live read is in
        /// <see cref="ResolveLockDecision"/>).
        ///
        /// Rule: an EXPLICIT value (config or corporate) is honored as-is and
        /// flagged explicit, so the send gate can reject a real lock this build
        /// cannot apply yet (decision D1: locks deferred). An UNSET setting is
        /// NOT a lock request - it maps to the strictest lock the current build
        /// can actually apply. Today that is UNLOCKED (the UnlockedOnly service
        /// supports nothing else), so an unconfigured deployment promotes
        /// without a lock instead of being dead-locked by the EXCLUSIVE contract
        /// default it could never honor anyway. When a lock-capable service
        /// ships and Supports(Exclusive) becomes true, the same code maps unset
        /// to EXCLUSIVE automatically - no behavior guess, no silent downgrade
        /// of an explicit request.
        /// </summary>
        /// <param name="rawSetting">Raw two-level value (ConfigContextService.GetEffective); null/blank = unset.</param>
        /// <param name="lockService">The lock implementation whose capabilities decide the unset fallback.</param>
        public static (PromotionLockType effective, bool explicitlyRequested) DecideLock(
            string rawSetting, IMartVersionLockService lockService)
        {
            if (lockService == null) throw new ArgumentNullException(nameof(lockService));

            if (!string.IsNullOrWhiteSpace(rawSetting))
            {
                // Explicit request. Unparseable -> UNLOCKED (safe, non-blocking)
                // but still flagged explicit is wrong; an unparseable admin
                // value should surface, so keep it explicit and let the parse
                // default to the contract default so the gate rejects it loudly
                // rather than silently proceeding unlocked.
                var parsed = ConfigContextService.ParseEffectiveEnum(rawSetting, PromotionLockType.Exclusive);
                return (parsed, true);
            }

            // Unset: strictest lock this build can actually apply.
            var contractDefault = PromotionLockType.Exclusive;
            return lockService.Supports(contractDefault)
                ? (contractDefault, false)
                : (PromotionLockType.Unlocked, false);
        }

        /// <summary>
        /// Live lock decision for the open model: resolves the two-level
        /// PROMOTION_LOCK_TYPE setting and defers to <see cref="DecideLock"/>.
        /// </summary>
        public static (PromotionLockType effective, bool explicitlyRequested) ResolveLockDecision(
            IMartVersionLockService lockService)
            => DecideLock(ConfigContextService.Instance.GetEffective(LockTypeKey), lockService);

        /// <summary>
        /// Builds the send context for the open model. Routes are planned with
        /// <see cref="PromotionPlanner.CandidatePlanningVersion"/>: a positive
        /// title-clean reading plans for the CURRENT version (so environments
        /// already holding it become rule-1 sources, e.g. Test to Prod second
        /// hops); dirty/unknown plans for a fresh version (first-environment
        /// sources only). Empty <see cref="PromotionSendContext.Routes"/> means
        /// the config has no usable environments/transitions - the caller
        /// surfaces that and falls back to the standard DDL flow.
        /// </summary>
        /// <param name="titleDirty">MartMartAutomation.IsActiveMdiChildDirtyByTitle result.</param>
        /// <param name="currentLocatorVersion">ParseActivePuVersion result (-1 when unknown).</param>
        /// <param name="log">Diagnostic logger.</param>
        public static PromotionSendContext BuildSendContext(
            bool? titleDirty, int currentLocatorVersion, Action<string>? log)
        {
            var ctx = ConfigContextService.Instance;
            int configId = ctx.ActiveConfigId;
            string? martPath = ctx.MartPath;
            if (configId <= 0)
                throw new InvalidOperationException("Version Promotion requires a resolved config (ActiveConfigId).");
            if (string.IsNullOrWhiteSpace(martPath))
                throw new InvalidOperationException("Version Promotion requires the canonical Mart path of the open model.");

            var environments = IntegrationEnvironmentService.GetEnvironments(configId);
            var relations = IntegrationEnvironmentService.GetRelations(configId);

            int? planningVersion = PromotionPlanner.CandidatePlanningVersion(titleDirty, currentLocatorVersion);
            bool clean = titleDirty == false;

            // A fresh version cannot be held by any environment, so the
            // version rows are only relevant when planning for the current one.
            IReadOnlyList<PromotionEnvironmentVersion> versions = planningVersion.HasValue
                ? PromotionService.Instance.GetEnvironmentVersions(martPath)
                : Array.Empty<PromotionEnvironmentVersion>();

            var routes = PromotionPlanner.BuildRoutes(
                planningVersion ?? -1, environments, relations, versions);

            // Preload the per-transition approver lists of every offered route
            // so the dialog can show the approval/auto-approve indicator
            // without further DB reads.
            var approvers = new Dictionary<int, IReadOnlyList<string>>();
            foreach (var route in routes)
            {
                if (!approvers.ContainsKey(route.Relation.Id))
                    approvers[route.Relation.Id] = PromotionService.Instance.GetRelationApprovers(route.Relation.Id, martPath);
            }

            log?.Invoke($"PromotionFlow: context built - config={configId}, path='{martPath}', " +
                        $"cleanByTitle={clean}, planningVersion={(planningVersion?.ToString() ?? "(new)")}, " +
                        $"envs={environments.Count}, relations={relations.Count}, routes={routes.Count}");

            return new PromotionSendContext(
                configId, martPath, environments, relations, routes, planningVersion, clean, approvers);
        }
    }
}
