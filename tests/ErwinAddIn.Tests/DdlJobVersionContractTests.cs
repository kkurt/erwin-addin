using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// DDL_GENERATION_QUEUE LEFT/RIGHT contract + the "target version not offered"
/// diagnosis.
///
/// <para>Field incident 2026-07-30: ten consecutive jobs failed because the rows
/// carried LEFT=deployed / RIGHT=newer target - the inverse of what the worker
/// needs - and every one of them reported "enable DDL_COMPARE_PREVIOUS_VERSIONS"
/// although that gate was ON. These tests pin BOTH halves: the row is rejected
/// before erwin is driven, and each cause gets its own operator-facing text.</para>
/// </summary>
public class DdlJobVersionContractTests
{
    // ---- TryValidateRow ----

    [Theory]
    [InlineData(8, 7)]   // normal upgrade script: open v8, compare against v7
    [InlineData(2, 1)]   // the job that produced DDL in the field (id=21)
    [InlineData(7, 7)]   // same version = "dirty vs last saved" (DDL_COMPARE_LAST_SAVED)
    [InlineData(12, 3)]  // any older target is fine, not just the immediate predecessor
    public void TryValidateRow_accepts_right_not_newer_than_left(int left, int right)
    {
        DdlJobVersionContract.TryValidateRow(left, right, out string error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Theory]
    [InlineData(7, 8)]   // job 42
    [InlineData(4, 5)]   // job 43
    [InlineData(5, 6)]   // job 44
    [InlineData(4, 6)]   // job 45 (gap, still inverted)
    [InlineData(1, 3)]   // job 41
    public void TryValidateRow_rejects_inverted_rows(int left, int right)
    {
        DdlJobVersionContract.TryValidateRow(left, right, out string error).Should().BeFalse();
        error.Should().Contain("inverted queue row");
        // The correction must be explicit - this text lands in ERROR_MESSAGE and is
        // the only thing the operator sees in the admin Auto-DDL grid.
        error.Should().Contain($"LEFT_VERSION={right}").And.Contain($"RIGHT_VERSION={left}");
        // It must NOT repeat the old misleading advice.
        error.Should().NotContain("DDL_COMPARE_PREVIOUS_VERSIONS");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    public void TryValidateRow_rejects_non_positive_left(int left, int right)
    {
        DdlJobVersionContract.TryValidateRow(left, right, out string error).Should().BeFalse();
        error.Should().Contain("LEFT_VERSION");
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, -2)]
    public void TryValidateRow_rejects_non_positive_right(int left, int right)
    {
        DdlJobVersionContract.TryValidateRow(left, right, out string error).Should().BeFalse();
        error.Should().Contain("RIGHT_VERSION");
    }

    // ---- ExplainMissingTarget ----

    [Fact]
    public void ExplainMissingTarget_blames_the_inversion_when_requested_is_newer_than_the_open_model()
    {
        // Reproduces job 42 exactly: v7 open, v8 requested, both gates ON.
        string msg = DdlJobVersionContract.ExplainMissingTarget(
            requested: 8, activeVersion: 7,
            allowLastSaved: true, allowPreviousVersions: true,
            targetListCsv: "v7 (Version 7), v6 (Version 6), v1 (Version 1)",
            listHasRealVersions: true);

        msg.Should().Contain("NEWER than the open model");
        msg.Should().Contain("LEFT_VERSION=8").And.Contain("RIGHT_VERSION=7");
        // The gate was ON - never send the operator to that toggle here.
        msg.Should().NotContain("DDL_COMPARE_PREVIOUS_VERSIONS");
    }

    [Fact]
    public void ExplainMissingTarget_blames_the_previous_versions_gate_when_it_is_off()
    {
        string msg = DdlJobVersionContract.ExplainMissingTarget(
            requested: 2, activeVersion: 5,
            allowLastSaved: true, allowPreviousVersions: false,
            targetListCsv: "v5 (Version 5)",
            listHasRealVersions: true);

        msg.Should().Contain("DDL_COMPARE_PREVIOUS_VERSIONS is OFF");
        msg.Should().Contain("v5");
    }

    [Fact]
    public void ExplainMissingTarget_blames_both_gates_when_no_real_version_is_offered()
    {
        string msg = DdlJobVersionContract.ExplainMissingTarget(
            requested: 2, activeVersion: 5,
            allowLastSaved: false, allowPreviousVersions: false,
            targetListCsv: "(no Mart source enabled)",
            listHasRealVersions: false);

        msg.Should().Contain("no Mart compare target is available");
        msg.Should().Contain("DDL_COMPARE_LAST_SAVED=False");
        msg.Should().Contain("DDL_COMPARE_PREVIOUS_VERSIONS=False");
    }

    [Fact]
    public void ExplainMissingTarget_falls_back_to_the_residual_case_without_guessing_a_cause()
    {
        // Gates on, requested older than the open model, yet absent from the list
        // (should not happen - RebuildRightCombo emits every v<=active - so the text
        // must state the facts rather than invent a cause).
        string msg = DdlJobVersionContract.ExplainMissingTarget(
            requested: 3, activeVersion: 5,
            allowLastSaved: true, allowPreviousVersions: true,
            targetListCsv: "v5 (Version 5), v4 (Version 4)",
            listHasRealVersions: true);

        msg.Should().Contain("not in the Target list");
        msg.Should().Contain("open model = v5");
    }

    [Fact]
    public void ExplainMissingTarget_does_not_claim_an_inversion_when_the_open_version_is_unknown()
    {
        // activeVersion 0/-1 = locator not parseable; claiming "newer than the open
        // model" would be a guess.
        string msg = DdlJobVersionContract.ExplainMissingTarget(
            requested: 8, activeVersion: 0,
            allowLastSaved: true, allowPreviousVersions: true,
            targetListCsv: "v7 (Version 7)",
            listHasRealVersions: true);

        msg.Should().NotContain("NEWER than the open model");
        msg.Should().Contain("not in the Target list");
    }
}
