#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// A promotion the user can perform from the current environment: the
    /// destination environment plus whether it goes through approval.
    /// </summary>
    /// <param name="Target">Destination environment.</param>
    /// <param name="RequiresApproval">When true the Integrate button is replaced by an approval notice (no direct run).</param>
    public sealed record PromotionTarget(IntegrationEnvironment Target, bool RequiresApproval);

    /// <summary>
    /// Pure (database-free) logic behind the Integrate tab: deriving the
    /// model's current environment from its Mart path and turning the admin
    /// ENVIRONMENT / ENVIRONMENT_RELATION rows into the renderable promotion
    /// targets. Kept separate from <see cref="IntegrationEnvironmentService"/>
    /// (I/O) and the form (rendering) so the decision logic is unit-testable.
    /// </summary>
    public static class IntegrationPlanner
    {
        /// <summary>
        /// Extracts the parent folder of a Mart model path, which by the
        /// Initialize convention "{baseDir}/{envName}/{modelName}" is the
        /// environment name. Returns null when the path has no parent segment
        /// (fewer than two segments) or is blank.
        /// </summary>
        /// <param name="martPath">Mart path stem, e.g. "Kursat/MetaRepo/Dev/SalesModel".</param>
        public static string? ParseParentFolder(string? martPath)
        {
            if (string.IsNullOrWhiteSpace(martPath)) return null;

            var segments = martPath.Split(
                new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            // Need at least {parent}/{model}; the parent is the second-to-last.
            if (segments.Length < 2) return null;

            string parent = segments[segments.Length - 2].Trim();
            return parent.Length == 0 ? null : parent;
        }

        /// <summary>
        /// Resolves the environment the model currently sits in by matching its
        /// parent folder against ENVIRONMENT.NAME. Returns null when the path
        /// has no parent segment or no environment name matches (the model is
        /// not in a managed environment). Names are compared case-insensitively
        /// because Mart folder names are not case-sensitive in practice.
        /// </summary>
        /// <param name="martPath">The open model's Mart path stem.</param>
        /// <param name="environments">All environments of the model's config.</param>
        public static IntegrationEnvironment? ResolveCurrentEnvironment(
            string? martPath, IReadOnlyList<IntegrationEnvironment> environments)
        {
            if (environments == null) return null;

            string? parent = ParseParentFolder(martPath);
            if (parent == null) return null;

            return environments.FirstOrDefault(
                e => string.Equals(e.Name, parent, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Builds the ordered list of promotion targets reachable from the
        /// current environment: every relation whose source is the current
        /// environment, paired with its destination environment, ordered by the
        /// destination SORT_ORDER. Relations whose destination is missing from
        /// <paramref name="environments"/> (an admin data inconsistency) are
        /// skipped because they cannot be rendered.
        /// </summary>
        /// <param name="currentEnvironmentId">ENVIRONMENT.ID the model is in.</param>
        /// <param name="relations">Relations originating from the current environment.</param>
        /// <param name="environments">All environments of the config, for destination lookup.</param>
        public static IReadOnlyList<PromotionTarget> BuildTargets(
            int currentEnvironmentId,
            IReadOnlyList<IntegrationRelation> relations,
            IReadOnlyList<IntegrationEnvironment> environments)
        {
            if (relations == null || environments == null)
                return Array.Empty<PromotionTarget>();

            var byId = environments.ToDictionary(e => e.Id);

            return relations
                .Where(r => r.FromEnvironmentId == currentEnvironmentId)
                .Select(r => byId.TryGetValue(r.ToEnvironmentId, out var target)
                    ? new PromotionTarget(target, r.RequiresApproval)
                    : null)
                .Where(t => t != null)
                .Select(t => t!)
                .OrderBy(t => t.Target.SortOrder)
                .ToList();
        }
    }
}
