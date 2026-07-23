using System;
using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the pure Version Promotion decision logic:
/// <see cref="PromotionPlanner"/> (source-environment derivation, reachable
/// targets, the approval-vs-auto-approve decision, the startup missed-unlock
/// filter), the <see cref="PromotionLockType"/> code round-trip and
/// <see cref="PromotionService.ComposeNote"/>. DB I/O lives in
/// <see cref="PromotionService"/> and is exercised live, not here - same
/// convention as <see cref="IntegrationPlannerTests"/>.
/// </summary>
public class PromotionPlannerTests
{
    private static IntegrationEnvironment Env(int id, string name, int sortOrder) =>
        new(id, ConfigId: 7, name, sortOrder, Description: null, ColorHex: null);

    private static IntegrationRelation Rel(int id, int fromId, int toId, bool approval = false) =>
        new(id, ConfigId: 7, fromId, toId, approval);

    private static PromotionEnvironmentVersion Ver(int envId, int version) =>
        new(envId, version, PromotedBy: "someone", PromotedAt: new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc));

    private static PromotionQueueRowInfo Row(
        int id, string? requestType, string? status, string? lockType, string? submittedBy) =>
        new(id, requestType, status, lockType, submittedBy, ModelLocator: "Kursat/MetaRepo", ModelVersion: 5);

    private static readonly List<IntegrationEnvironment> DevTestProd = new()
    {
        Env(1, "Dev", 1), Env(2, "Test", 2), Env(3, "Prod", 3)
    };

    // ---- BuildRoutes: source derivation rule -------------------------------

    [Fact]
    public void BuildRoutes_source_is_environment_holding_the_promoted_version()
    {
        // Test already holds v5, so Test -> Prod wins over Dev -> Prod (rule 1).
        var relations = new List<IntegrationRelation>
        {
            Rel(10, fromId: 1, toId: 3), Rel(11, fromId: 2, toId: 3), Rel(12, fromId: 1, toId: 2)
        };
        var versions = new List<PromotionEnvironmentVersion> { Ver(envId: 2, version: 5) };

        var routes = PromotionPlanner.BuildRoutes(5, DevTestProd, relations, versions);

        var prodRoutes = PromotionPlanner.RoutesForTarget(routes, targetEnvironmentId: 3);
        prodRoutes.Should().ContainSingle("the holder environment must be the source, not the first environment");
        prodRoutes[0].Source.Id.Should().Be(2);
        prodRoutes[0].Relation.Id.Should().Be(11);
    }

    [Fact]
    public void BuildRoutes_falls_back_to_first_environment_when_no_holder_exists()
    {
        // No MODEL_ENVIRONMENT_VERSION rows at all: the first environment
        // (lowest SORT_ORDER) is always the current model (rule 2).
        var relations = new List<IntegrationRelation> { Rel(10, fromId: 1, toId: 2) };

        var routes = PromotionPlanner.BuildRoutes(5, DevTestProd, relations, new List<PromotionEnvironmentVersion>());

        routes.Should().ContainSingle();
        routes[0].Source.Id.Should().Be(1);
        routes[0].Target.Id.Should().Be(2);
    }

    [Fact]
    public void BuildRoutes_falls_back_to_first_environment_when_holder_lacks_a_transition_to_target()
    {
        // Prod holds v5 but has no transition to Test; rule 1 yields nothing
        // for target Test, so rule 2 (first env) applies.
        var relations = new List<IntegrationRelation> { Rel(10, fromId: 1, toId: 2) };
        var versions = new List<PromotionEnvironmentVersion> { Ver(envId: 3, version: 5) };

        var routes = PromotionPlanner.BuildRoutes(5, DevTestProd, relations, versions);

        var testRoutes = PromotionPlanner.RoutesForTarget(routes, targetEnvironmentId: 2);
        testRoutes.Should().ContainSingle();
        testRoutes[0].Source.Id.Should().Be(1);
    }

    [Fact]
    public void BuildRoutes_version_rows_of_other_versions_do_not_make_an_environment_a_source()
    {
        // Test holds v4, we promote v5: Test is NOT a holder of v5.
        var relations = new List<IntegrationRelation>
        {
            Rel(10, fromId: 1, toId: 3), Rel(11, fromId: 2, toId: 3)
        };
        var versions = new List<PromotionEnvironmentVersion> { Ver(envId: 2, version: 4) };

        var routes = PromotionPlanner.BuildRoutes(5, DevTestProd, relations, versions);

        var prodRoutes = PromotionPlanner.RoutesForTarget(routes, targetEnvironmentId: 3);
        prodRoutes.Should().ContainSingle();
        prodRoutes[0].Source.Id.Should().Be(1, "only a row matching the PROMOTED version makes an env the source");
    }

    [Fact]
    public void BuildRoutes_multiple_holders_produce_multiple_candidates_for_the_user_to_choose()
    {
        // Test and Uat both hold v5 and both can reach Prod: rule 3 says the
        // UI must offer the choice - the planner returns both candidates.
        var envs = new List<IntegrationEnvironment>
        {
            Env(1, "Dev", 1), Env(2, "Test", 2), Env(4, "Uat", 3), Env(3, "Prod", 4)
        };
        var relations = new List<IntegrationRelation>
        {
            Rel(10, fromId: 2, toId: 3), Rel(11, fromId: 4, toId: 3)
        };
        var versions = new List<PromotionEnvironmentVersion> { Ver(2, 5), Ver(4, 5) };

        var routes = PromotionPlanner.BuildRoutes(5, envs, relations, versions);

        var prodRoutes = PromotionPlanner.RoutesForTarget(routes, targetEnvironmentId: 3);
        prodRoutes.Should().HaveCount(2);
        prodRoutes[0].Source.Id.Should().Be(2); // ordered by source SORT_ORDER
        prodRoutes[1].Source.Id.Should().Be(4);
    }

    [Fact]
    public void BuildRoutes_target_without_any_transition_is_not_offered()
    {
        var relations = new List<IntegrationRelation> { Rel(10, fromId: 1, toId: 2) };

        var routes = PromotionPlanner.BuildRoutes(5, DevTestProd, relations, new List<PromotionEnvironmentVersion>());

        var targets = PromotionPlanner.TargetsOf(routes);
        targets.Should().ContainSingle();
        targets[0].Id.Should().Be(2, "Prod has no incoming transition and Dev is only a source here");
    }

    [Fact]
    public void BuildRoutes_orders_by_target_then_source_sort_order()
    {
        var relations = new List<IntegrationRelation>
        {
            Rel(10, fromId: 1, toId: 3), // Dev -> Prod (target sort 3)
            Rel(11, fromId: 1, toId: 2)  // Dev -> Test (target sort 2)
        };

        var routes = PromotionPlanner.BuildRoutes(5, DevTestProd, relations, new List<PromotionEnvironmentVersion>());

        routes.Should().HaveCount(2);
        routes[0].Target.Id.Should().Be(2);
        routes[1].Target.Id.Should().Be(3);
    }

    [Fact]
    public void BuildRoutes_never_offers_the_first_environment_as_a_target()
    {
        // Backward transition INTO the first environment (review finding
        // 2026-07-22): promoting "into" the current model is meaningless and
        // the auto-approve upsert would create the MODEL_ENVIRONMENT_VERSION
        // row the contract forbids for the first environment.
        var relations = new List<IntegrationRelation>
        {
            Rel(10, fromId: 2, toId: 1),  // Test -> Dev (backward, into first env)
            Rel(11, fromId: 2, toId: 3)   // Test -> Prod
        };
        var versions = new List<PromotionEnvironmentVersion> { Ver(envId: 2, version: 5) };

        var routes = PromotionPlanner.BuildRoutes(5, DevTestProd, relations, versions);

        PromotionPlanner.RoutesForTarget(routes, targetEnvironmentId: 1).Should().BeEmpty(
            "the first environment is always the current model and never receives a promotion");
        PromotionPlanner.RoutesForTarget(routes, targetEnvironmentId: 3).Should().ContainSingle();
        PromotionPlanner.TargetsOf(routes).Should().ContainSingle(t => t.Id == 3);
    }

    [Fact]
    public void BuildRoutes_skips_self_transitions_and_unknown_environment_ids()
    {
        var relations = new List<IntegrationRelation>
        {
            Rel(10, fromId: 2, toId: 2),  // self loop - admin data error
            Rel(11, fromId: 1, toId: 2)   // valid
        };
        // Version row for an environment id that no longer exists.
        var versions = new List<PromotionEnvironmentVersion> { Ver(envId: 99, version: 5) };

        var routes = PromotionPlanner.BuildRoutes(5, DevTestProd, relations, versions);

        routes.Should().ContainSingle();
        routes[0].Relation.Id.Should().Be(11);
    }

    [Fact]
    public void BuildRoutes_returns_empty_for_missing_environments_or_relations()
    {
        PromotionPlanner.BuildRoutes(5, new List<IntegrationEnvironment>(), new List<IntegrationRelation>(),
            new List<PromotionEnvironmentVersion>()).Should().BeEmpty();
        PromotionPlanner.BuildRoutes(5, DevTestProd, null!, new List<PromotionEnvironmentVersion>())
            .Should().BeEmpty();
    }

    // ---- RequiresApprovalVote ---------------------------------------------

    [Fact]
    public void RequiresApprovalVote_true_only_when_flag_set_and_approvers_exist()
    {
        var rel = Rel(1, 1, 2, approval: true);
        PromotionPlanner.RequiresApprovalVote(rel, new List<string?> { "alice" }).Should().BeTrue();
    }

    [Fact]
    public void RequiresApprovalVote_false_when_approver_list_is_empty_even_if_flag_set()
    {
        // Spec: REQUIRES_APPROVAL=1 with an empty approver list auto-approves;
        // there is deliberately NO fallback to the config APPROVAL_APPROVER list.
        var rel = Rel(1, 1, 2, approval: true);
        PromotionPlanner.RequiresApprovalVote(rel, new List<string?>()).Should().BeFalse();
        PromotionPlanner.RequiresApprovalVote(rel, null).Should().BeFalse();
    }

    [Fact]
    public void RequiresApprovalVote_ignores_blank_approver_entries()
    {
        var rel = Rel(1, 1, 2, approval: true);
        PromotionPlanner.RequiresApprovalVote(rel, new List<string?> { null, "", "   " }).Should().BeFalse();
    }

    [Fact]
    public void RequiresApprovalVote_false_when_transition_does_not_require_approval()
    {
        var rel = Rel(1, 1, 2, approval: false);
        PromotionPlanner.RequiresApprovalVote(rel, new List<string?> { "alice" }).Should().BeFalse();
    }

    // ---- SelectMissedUnlocks ----------------------------------------------

    [Fact]
    public void SelectMissedUnlocks_selects_own_locked_terminal_promotion_rows()
    {
        var rows = new List<PromotionQueueRowInfo>
        {
            Row(1, "PROMOTION", "Approved", "EXCLUSIVE", "kursat"),
            Row(2, "PROMOTION", "Rejected", "UPDATE", "kursat"),
        };

        var due = PromotionPlanner.SelectMissedUnlocks(rows, "kursat");

        due.Should().HaveCount(2);
    }

    [Fact]
    public void SelectMissedUnlocks_skips_pending_unlocked_and_foreign_rows()
    {
        var rows = new List<PromotionQueueRowInfo>
        {
            Row(1, "PROMOTION", "Pending", "EXCLUSIVE", "kursat"),   // not terminal yet
            Row(2, "PROMOTION", "Approved", "UNLOCKED", "kursat"),   // nothing to release
            Row(3, "PROMOTION", "Approved", null, "kursat"),         // no lock recorded
            Row(4, "DDL", "Approved", "EXCLUSIVE", "kursat"),        // not a promotion row
            Row(5, "PROMOTION", "Approved", "EXCLUSIVE", "someoneelse"), // not ours
        };

        PromotionPlanner.SelectMissedUnlocks(rows, "kursat").Should().BeEmpty();
    }

    [Fact]
    public void SelectMissedUnlocks_matches_username_case_insensitively()
    {
        var rows = new List<PromotionQueueRowInfo> { Row(1, "PROMOTION", "Approved", "EXCLUSIVE", "KURSAT") };

        PromotionPlanner.SelectMissedUnlocks(rows, "kursat").Should().ContainSingle(
            "Windows account names are not case-sensitive");
    }

    [Fact]
    public void SelectMissedUnlocks_requires_exact_status_casing_per_contract()
    {
        // STATUS casing is a spec contract ('Approved'/'Rejected' verbatim);
        // an unexpected casing must not be treated as terminal.
        var rows = new List<PromotionQueueRowInfo> { Row(1, "PROMOTION", "approved", "EXCLUSIVE", "kursat") };

        PromotionPlanner.SelectMissedUnlocks(rows, "kursat").Should().BeEmpty();
    }

    [Fact]
    public void SelectMissedUnlocks_returns_empty_without_a_current_user()
    {
        var rows = new List<PromotionQueueRowInfo> { Row(1, "PROMOTION", "Approved", "EXCLUSIVE", "kursat") };

        PromotionPlanner.SelectMissedUnlocks(rows, null).Should().BeEmpty();
        PromotionPlanner.SelectMissedUnlocks(null!, "kursat").Should().BeEmpty();
    }

    // ---- ParseVersionFromSaveDialogTitle ----------------------------------

    [Theory]
    [InlineData("Description for 'MetaRepo' Version 12", 12)]
    [InlineData("Description for 'M' Version 1", 1)]
    // A model name containing the word "Version" must not confuse the parse:
    // only the TRAILING "Version <N>" names the version the save creates.
    [InlineData("Description for 'Core Version 2 Model' Version 7", 7)]
    [InlineData("Description for 'X' Version 12  ", 12)] // trailing spaces tolerated
    public void ParseVersionFromSaveDialogTitle_reads_the_trailing_version(string title, int expected)
    {
        PromotionPlanner.ParseVersionFromSaveDialogTitle(title).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Description for 'MetaRepo'")]
    [InlineData("Description for 'X' Version")]
    [InlineData("Description for 'X' Version 0")]   // versions are 1-based
    [InlineData("Version 5 of something else")]      // not trailing
    public void ParseVersionFromSaveDialogTitle_returns_null_when_shape_does_not_match(string? title)
    {
        PromotionPlanner.ParseVersionFromSaveDialogTitle(title).Should().BeNull();
    }

    // ---- CandidatePlanningVersion -----------------------------------------

    [Fact]
    public void CandidatePlanningVersion_uses_current_version_only_on_a_positive_clean_reading()
    {
        // Clean model: the send skips the Mart save, so the open version is
        // promoted and its holders can be rule-1 sources (second-hop case).
        PromotionPlanner.CandidatePlanningVersion(titleDirty: false, currentLocatorVersion: 5).Should().Be(5);
    }

    [Theory]
    [InlineData(true, 5)]    // dirty: the save will mint a NEW version
    [InlineData(null, 5)]    // unknown reading: treated as dirty (save runs)
    [InlineData(false, -1)]  // clean but version unreadable: cannot plan on it
    [InlineData(false, 0)]
    public void CandidatePlanningVersion_returns_null_when_a_fresh_or_unknown_version_will_be_promoted(
        bool? titleDirty, int currentVersion)
    {
        PromotionPlanner.CandidatePlanningVersion(titleDirty, currentVersion).Should().BeNull();
    }

    // ---- PromotionLockType codes ------------------------------------------

    [Theory]
    [InlineData(PromotionLockType.Unlocked, "UNLOCKED")]
    [InlineData(PromotionLockType.Existence, "EXISTENCE")]
    [InlineData(PromotionLockType.Shared, "SHARED")]
    [InlineData(PromotionLockType.Update, "UPDATE")]
    [InlineData(PromotionLockType.Exclusive, "EXCLUSIVE")]
    public void LockType_ToCode_renders_the_exact_contract_code(PromotionLockType lockType, string expected)
    {
        PromotionLockTypeCodes.ToCode(lockType).Should().Be(expected);
    }

    [Theory]
    [InlineData("EXCLUSIVE", PromotionLockType.Exclusive)]
    [InlineData("exclusive", PromotionLockType.Exclusive)] // setting parse is case-insensitive
    [InlineData("UNLOCKED", PromotionLockType.Unlocked)]
    [InlineData("EXISTENCE", PromotionLockType.Existence)]
    [InlineData("SHARED", PromotionLockType.Shared)]
    [InlineData("UPDATE", PromotionLockType.Update)]
    public void LockType_setting_value_parses_through_the_effective_enum_parser(string value, PromotionLockType expected)
    {
        ConfigContextService.ParseEffectiveEnum(value, PromotionLockType.Unlocked).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BOGUS")]
    [InlineData("99")]
    public void LockType_unparseable_setting_falls_back_to_the_default(string? value)
    {
        // The built-in default of PROMOTION_LOCK_TYPE is EXCLUSIVE (admin contract).
        ConfigContextService.ParseEffectiveEnum(value, PromotionLockType.Exclusive)
            .Should().Be(PromotionLockType.Exclusive);
    }

    // ---- PromotionFlow.DecideLock -----------------------------------------

    // A lock service that supports every type - stands in for the future
    // lock-capable build so the unset->EXCLUSIVE forward path can be tested.
    private sealed class AllLocksService : IMartVersionLockService
    {
        public bool Supports(PromotionLockType lockType) => true;
        public void ApplyLock(string martPath, int version, PromotionLockType lockType, System.Action<string>? log) { }
        public void ReleaseLock(string martPath, int version, PromotionLockType lockType, System.Action<string>? log) { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DecideLock_unset_maps_to_unlocked_when_the_build_cannot_lock(string? raw)
    {
        // Deferred-lock phase (D1): an unconfigured setting is NOT a lock
        // request, so it must not block - it resolves to UNLOCKED and the send
        // proceeds. This is the bug the live test hit (unset -> EXCLUSIVE ->
        // blocked, 2026-07-22).
        var (effective, explicitly) = PromotionFlow.DecideLock(raw, new UnlockedOnlyMartVersionLockService());
        effective.Should().Be(PromotionLockType.Unlocked);
        explicitly.Should().BeFalse();
    }

    [Fact]
    public void DecideLock_unset_maps_to_exclusive_once_a_lock_capable_build_ships()
    {
        // Forward-compat: when the service can apply EXCLUSIVE, an unset
        // setting resolves to the admin contract default (EXCLUSIVE) with no
        // code change - the same DecideLock adapts to the service.
        var (effective, explicitly) = PromotionFlow.DecideLock(null, new AllLocksService());
        effective.Should().Be(PromotionLockType.Exclusive);
        explicitly.Should().BeFalse();
    }

    [Fact]
    public void DecideLock_explicit_unlocked_proceeds()
    {
        var (effective, explicitly) = PromotionFlow.DecideLock("UNLOCKED", new UnlockedOnlyMartVersionLockService());
        effective.Should().Be(PromotionLockType.Unlocked);
        explicitly.Should().BeTrue();
    }

    [Theory]
    [InlineData("EXCLUSIVE", PromotionLockType.Exclusive)]
    [InlineData("exclusive", PromotionLockType.Exclusive)]
    [InlineData("SHARED", PromotionLockType.Shared)]
    [InlineData("UPDATE", PromotionLockType.Update)]
    [InlineData("EXISTENCE", PromotionLockType.Existence)]
    public void DecideLock_explicit_non_unlocked_is_flagged_explicit_so_the_gate_can_reject_it(
        string raw, PromotionLockType expected)
    {
        // Explicit request the deferred build cannot honor: flagged explicit so
        // the send gate rejects it loudly instead of silently proceeding.
        var (effective, explicitly) = PromotionFlow.DecideLock(raw, new UnlockedOnlyMartVersionLockService());
        effective.Should().Be(expected);
        explicitly.Should().BeTrue();
        new UnlockedOnlyMartVersionLockService().Supports(effective).Should().BeFalse();
    }

    [Fact]
    public void DecideLock_explicit_unparseable_defaults_to_exclusive_and_blocks()
    {
        // A typo'd explicit value must not silently proceed unlocked; it
        // resolves to the contract default (EXCLUSIVE), stays flagged explicit,
        // and the gate rejects it so the admin notices.
        var (effective, explicitly) = PromotionFlow.DecideLock("BOGUS", new UnlockedOnlyMartVersionLockService());
        effective.Should().Be(PromotionLockType.Exclusive);
        explicitly.Should().BeTrue();
    }

    // ---- UnlockedOnlyMartVersionLockService --------------------------------

    [Fact]
    public void UnlockedOnly_lock_service_supports_only_unlocked_and_throws_loudly_otherwise()
    {
        var svc = new UnlockedOnlyMartVersionLockService();

        svc.Supports(PromotionLockType.Unlocked).Should().BeTrue();
        svc.Supports(PromotionLockType.Exclusive).Should().BeFalse();

        svc.Invoking(s => s.ApplyLock("Kursat/MetaRepo", 5, PromotionLockType.Exclusive, null))
            .Should().Throw<NotSupportedException>()
            .WithMessage("*EXCLUSIVE*", "the error must name the unsupported code so the admin can fix the setting");

        // The UNLOCKED path is a legal no-op, not an error.
        svc.Invoking(s => s.ApplyLock("Kursat/MetaRepo", 5, PromotionLockType.Unlocked, null))
            .Should().NotThrow();
        svc.Invoking(s => s.ReleaseLock("Kursat/MetaRepo", 5, PromotionLockType.Unlocked, null))
            .Should().NotThrow();
    }

    // ---- PromotionService.ComposeNote -------------------------------------

    [Fact]
    public void AutoApproveNote_is_the_exact_spec_string()
    {
        PromotionService.AutoApproveNote.Should().Be("Auto-approved (no approval required for this transition)");
    }

    [Fact]
    public void ComposeNote_auto_path_without_user_note_is_the_marker_verbatim()
    {
        PromotionService.ComposeNote(null, autoApprove: true).Should().Be(PromotionService.AutoApproveNote);
        PromotionService.ComposeNote("  ", autoApprove: true).Should().Be(PromotionService.AutoApproveNote);
    }

    [Fact]
    public void ComposeNote_auto_path_preserves_the_user_note_above_the_marker()
    {
        PromotionService.ComposeNote("hotfix push", autoApprove: true)
            .Should().Be("hotfix push" + Environment.NewLine + PromotionService.AutoApproveNote);
    }

    [Fact]
    public void ComposeNote_approval_path_passes_the_user_note_through()
    {
        PromotionService.ComposeNote("hotfix push", autoApprove: false).Should().Be("hotfix push");
        PromotionService.ComposeNote("   ", autoApprove: false).Should().BeNull();
        PromotionService.ComposeNote(null, autoApprove: false).Should().BeNull();
    }
}
