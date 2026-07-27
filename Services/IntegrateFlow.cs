#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Everything the Integrate send needs to know, resolved once at DDL-review time.
    /// </summary>
    /// <param name="CurrentEnvironment">The environment the open model sits in.</param>
    /// <param name="TargetEnvironment">The environment it will be merged into (the next one).</param>
    /// <param name="TargetMartPath">Mart path of the target model, <c>{base}/{targetEnv}/{model}</c>.</param>
    /// <param name="TargetFolderPath">Mart folder holding the target model, <c>{base}/{targetEnv}</c>.</param>
    /// <param name="ModelName">Leaf model name, identical in every environment of the pipeline.</param>
    public sealed record IntegrateSendContext(
        IntegrationEnvironment CurrentEnvironment,
        IntegrationEnvironment TargetEnvironment,
        string TargetMartPath,
        string TargetFolderPath,
        string ModelName);

    /// <summary>
    /// Decides whether the DDL review's send is an INTEGRATE (drive erwin's
    /// Mart &gt; Merge onto the next environment) and resolves where it goes.
    ///
    /// <para>Split from the automation on purpose: every judgement here is pure string
    /// and list work over the admin ENVIRONMENT / ENVIRONMENT_RELATION rows, so it is
    /// unit-testable without erwin. <see cref="MartMartAutomation"/> owns the Win32 side.</para>
    ///
    /// <para><b>When it applies.</b> The model must sit in one of its config's
    /// environments AND that environment must have an outgoing transition. The caller
    /// additionally requires an EMPTY approver chain - with approvers the send is a
    /// governed "Send to Approve" instead, because a merge that bypasses the approval
    /// queue would defeat the gate.</para>
    /// </summary>
    public static class IntegrateFlow
    {
        /// <summary>
        /// Resolves the integrate target for the open model, or null when this model is
        /// not integrate-able (not in a managed environment, or the environment has no
        /// outgoing transition).
        ///
        /// <para>When the current environment has SEVERAL outgoing transitions the first
        /// by target SORT_ORDER wins - that is the "next" environment in pipeline order,
        /// which is what <c>BuildTargets</c> already sorts by. A branching topology is an
        /// admin choice the add-in cannot disambiguate from the model alone; the caller
        /// logs the pick so it is visible.</para>
        /// </summary>
        /// <param name="martPath">The open model's Mart path stem, <c>{base}/{env}/{model}</c>.</param>
        /// <param name="environments">All environments of the model's config.</param>
        /// <param name="relations">All transitions of the model's config.</param>
        public static IntegrateSendContext? ResolveTarget(
            string? martPath,
            IReadOnlyList<IntegrationEnvironment>? environments,
            IReadOnlyList<IntegrationRelation>? relations)
        {
            if (string.IsNullOrWhiteSpace(martPath) || environments == null || relations == null)
                return null;

            var current = IntegrationPlanner.ResolveCurrentEnvironment(martPath, environments);
            if (current == null) return null;   // not in a managed environment

            var targets = IntegrationPlanner.BuildTargets(current.Id, relations, environments);
            if (targets.Count == 0) return null; // end of the pipeline - nothing to integrate into

            var target = targets[0].Target;

            // {base}/{env}/{model}: the model leaf is the last segment and the environment
            // the one before it, both already proven present by ResolveCurrentEnvironment.
            var segments = martPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string modelName = segments[segments.Length - 1];
            string basePath = string.Join("/", segments.Take(segments.Length - 2));

            string targetFolder = basePath.Length == 0 ? target.Name : $"{basePath}/{target.Name}";
            string targetModel = $"{targetFolder}/{modelName}";

            return new IntegrateSendContext(current, target, targetModel, targetFolder, modelName);
        }
    }
}
