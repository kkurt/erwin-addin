#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using EliteSoft.MetaAdmin.Shared.Data.Entities;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// A MODEL_ENVIRONMENT_VERSION row projected for planner/UI use: which Mart
    /// version an environment currently holds for ONE model (the loader scopes
    /// rows by the model's canonical MART_PATH before they reach this type).
    /// The first environment (lowest SORT_ORDER) never has a row by contract:
    /// it always represents the current model.
    /// </summary>
    /// <param name="EnvironmentId">ENVIRONMENT.ID holding the version.</param>
    /// <param name="Version">Promoted Mart version number (C_Version).</param>
    /// <param name="PromotedBy">SUBMITTED_BY of the promoting queue row; null on legacy rows.</param>
    /// <param name="PromotedAt">Finalize moment (UTC).</param>
    public sealed record PromotionEnvironmentVersion(
        int EnvironmentId,
        int Version,
        string? PromotedBy,
        DateTime PromotedAt);

    /// <summary>
    /// One concrete promotion candidate: a source environment, a target
    /// environment and the ENVIRONMENT_RELATION row that connects them.
    /// </summary>
    public sealed record PromotionRoute(
        IntegrationEnvironment Source,
        IntegrationEnvironment Target,
        IntegrationRelation Relation);

    /// <summary>
    /// A DDL_APPROVAL_QUEUE row projected for the missed-unlock recovery
    /// decision (the DB reader returns the caller's own PROMOTION rows; the
    /// pure filter below decides which ones still owe a lock release).
    /// </summary>
    /// <param name="Id">DDL_APPROVAL_QUEUE.ID.</param>
    /// <param name="RequestType">REQUEST_TYPE ('DDL' or 'PROMOTION').</param>
    /// <param name="Status">STATUS ('Pending' / 'Approved' / 'Rejected', exact casing by contract).</param>
    /// <param name="LockType">LOCK_TYPE code applied at submit; null on DDL rows.</param>
    /// <param name="SubmittedBy">SUBMITTED_BY of the row.</param>
    /// <param name="ModelLocator">Canonical root-relative Mart path (PROMOTION contract).</param>
    /// <param name="ModelVersion">Promoted Mart version.</param>
    public sealed record PromotionQueueRowInfo(
        int Id,
        string? RequestType,
        string? Status,
        string? LockType,
        string? SubmittedBy,
        string? ModelLocator,
        int? ModelVersion);

    /// <summary>
    /// Pure (database-free, COM-free) decision logic of the Version Promotion
    /// feature: source-environment derivation, reachable-target computation,
    /// the approval-vs-auto-approve decision and the startup missed-unlock
    /// filter. Kept separate from <see cref="PromotionService"/> (I/O) and the
    /// form (rendering) so every rule is unit-testable, mirroring
    /// <see cref="IntegrationPlanner"/>.
    ///
    /// Unlike the Integrate feature there is NO folder-per-environment copy:
    /// a single model exists and MODEL_ENVIRONMENT_VERSION says which version
    /// each environment holds, so the source environment is derived from those
    /// rows, never from the Mart folder name.
    /// </summary>
    public static class PromotionPlanner
    {
        /// <summary>
        /// Builds every promotion candidate for <paramref name="promotedVersion"/>
        /// per the spec's source-derivation rule, evaluated per target:
        /// 1. environments currently holding the promoted version
        ///    (MODEL_ENVIRONMENT_VERSION match) that have a transition to the
        ///    target are the sources;
        /// 2. otherwise the first environment (lowest SORT_ORDER; always the
        ///    current model, never has a version row) is the source when it has
        ///    a transition to the target;
        /// 3. a target left with more than one candidate route is presented to
        ///    the user for an explicit source choice (the UI's job; this method
        ///    returns all of them).
        /// Targets without any usable transition are not returned at all
        /// (sending to them is blocked; the server would also reject).
        /// The FIRST environment is never offered as a target either: it always
        /// represents the current model and MODEL_ENVIRONMENT_VERSION must
        /// never hold a row for it (spec invariant), so promoting "into" it is
        /// meaningless and would persist the forbidden row on the auto-approve
        /// branch. Self-transitions and relations pointing at environments
        /// missing from <paramref name="environments"/> (admin data
        /// inconsistencies) are skipped. Routes are ordered by target then
        /// source SORT_ORDER.
        /// </summary>
        /// <param name="promotedVersion">Mart version being promoted (the just-saved model version).</param>
        /// <param name="environments">All environments of the config.</param>
        /// <param name="relations">All transitions of the config.</param>
        /// <param name="environmentVersions">MODEL_ENVIRONMENT_VERSION rows of THIS model.</param>
        public static IReadOnlyList<PromotionRoute> BuildRoutes(
            int promotedVersion,
            IReadOnlyList<IntegrationEnvironment> environments,
            IReadOnlyList<IntegrationRelation> relations,
            IReadOnlyList<PromotionEnvironmentVersion> environmentVersions)
        {
            if (environments == null || environments.Count == 0 || relations == null)
                return Array.Empty<PromotionRoute>();

            var byId = environments.ToDictionary(e => e.Id);
            var first = environments.OrderBy(e => e.SortOrder).First();

            IReadOnlyList<PromotionEnvironmentVersion> versions =
                environmentVersions ?? Array.Empty<PromotionEnvironmentVersion>();
            var holderIds = versions
                .Where(v => v.Version == promotedVersion)
                .Select(v => v.EnvironmentId)
                .Where(byId.ContainsKey)
                .Distinct()
                .ToHashSet();

            var routes = new List<PromotionRoute>();

            foreach (var target in environments)
            {
                // The first environment is the current model by definition; a
                // backward transition INTO it must not be offered because the
                // resulting MODEL_ENVIRONMENT_VERSION upsert would create the
                // row the contract forbids for the first environment (and that
                // phantom row would corrupt rule-1 holder derivation later).
                if (target.Id == first.Id) continue;

                // Rule 1: an environment already holding the promoted version
                // with a defined transition to this target is the source.
                var candidates = relations
                    .Where(r => r.ToEnvironmentId == target.Id
                             && r.FromEnvironmentId != r.ToEnvironmentId
                             && holderIds.Contains(r.FromEnvironmentId))
                    .ToList();

                // Rule 2: no holder can reach the target - fall back to the
                // first environment (it always carries the current model).
                if (candidates.Count == 0)
                {
                    candidates = relations
                        .Where(r => r.ToEnvironmentId == target.Id
                                 && r.FromEnvironmentId != r.ToEnvironmentId
                                 && r.FromEnvironmentId == first.Id)
                        .ToList();
                }

                foreach (var rel in candidates)
                {
                    if (!byId.TryGetValue(rel.FromEnvironmentId, out var source))
                        continue; // orphan source env - admin data inconsistency
                    routes.Add(new PromotionRoute(source, target, rel));
                }
            }

            return routes
                .OrderBy(r => r.Target.SortOrder)
                .ThenBy(r => r.Source.SortOrder)
                .ToList();
        }

        /// <summary>
        /// Distinct reachable target environments of <paramref name="routes"/>,
        /// ordered by SORT_ORDER. Convenience for the target picker.
        /// </summary>
        public static IReadOnlyList<IntegrationEnvironment> TargetsOf(IReadOnlyList<PromotionRoute> routes)
        {
            if (routes == null) return Array.Empty<IntegrationEnvironment>();
            return routes
                .Select(r => r.Target)
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .OrderBy(t => t.SortOrder)
                .ToList();
        }

        /// <summary>
        /// The candidate routes for one chosen target. One route = submit
        /// directly; several = the UI must ask the user which source applies
        /// (spec derivation rule 3).
        /// </summary>
        public static IReadOnlyList<PromotionRoute> RoutesForTarget(
            IReadOnlyList<PromotionRoute> routes, int targetEnvironmentId)
        {
            if (routes == null) return Array.Empty<PromotionRoute>();
            return routes.Where(r => r.Target.Id == targetEnvironmentId).ToList();
        }

        /// <summary>
        /// The approval decision of the spec: a submit goes to voting
        /// (STATUS='Pending') only when the chosen transition has
        /// REQUIRES_APPROVAL=1 AND its own approver list is non-empty (blank
        /// entries do not count). Everything else auto-approves. There is
        /// deliberately NO fallback to the config-wide APPROVAL_APPROVER list.
        /// </summary>
        /// <param name="relation">The chosen transition.</param>
        /// <param name="approvers">ENVIRONMENT_RELATION_APPROVER names of that transition.</param>
        public static bool RequiresApprovalVote(
            IntegrationRelation relation, IReadOnlyList<string?>? approvers)
        {
            if (relation == null || !relation.RequiresApproval) return false;
            return approvers != null && approvers.Any(a => !string.IsNullOrWhiteSpace(a));
        }

        /// <summary>
        /// Startup missed-unlock recovery filter: from the caller's own queue
        /// rows, the PROMOTION rows that still carry a real lock
        /// (LOCK_TYPE present and not 'UNLOCKED') but already reached a
        /// terminal STATUS ('Approved'/'Rejected', exact casing per contract) -
        /// i.e. the add-in was closed when the decision landed and the unlock
        /// never ran. Usernames compare case-insensitively (Windows account
        /// names; the Marts are Windows-auth per project decision 2026-07-22).
        /// </summary>
        /// <param name="rows">Candidate queue rows (typically pre-filtered to the user's PROMOTION rows).</param>
        /// <param name="currentUser">The active user's account name.</param>
        public static IReadOnlyList<PromotionQueueRowInfo> SelectMissedUnlocks(
            IReadOnlyList<PromotionQueueRowInfo> rows, string? currentUser)
        {
            if (rows == null || string.IsNullOrWhiteSpace(currentUser))
                return Array.Empty<PromotionQueueRowInfo>();

            return rows.Where(r =>
                    string.Equals(r.RequestType, DdlApprovalQueue.RequestTypes.Promotion, StringComparison.Ordinal)
                 && string.Equals(r.SubmittedBy, currentUser, StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(r.LockType)
                 && !string.Equals(r.LockType, PromotionLockTypeCodes.Unlocked, StringComparison.OrdinalIgnoreCase)
                 && (string.Equals(r.Status, DdlApprovalQueue.Statuses.Approved, StringComparison.Ordinal)
                  || string.Equals(r.Status, DdlApprovalQueue.Statuses.Rejected, StringComparison.Ordinal)))
                .ToList();
        }
    }
}
