using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for <see cref="IntegrateFlow"/> - the pure half of the Integrate send
/// (which environment is next, where its model lives, and whether the DDL review should
/// offer "Integrate" at all). The Mart &gt; Merge automation itself needs a live erwin
/// and is not exercised here.
/// </summary>
public class IntegrateFlowTests
{
    private static IntegrationEnvironment Env(int id, string name, int sortOrder) =>
        new(id, ConfigId: 7, name, sortOrder, Description: null, ColorHex: null);

    private static IntegrationRelation Rel(int id, int fromId, int toId, bool approval = false) =>
        new(id, ConfigId: 7, fromId, toId, approval);

    // The live pipeline this was built against: Kursat/Integrate Test/{1_DEV,2_TEST,3_PROD}/MetaRepo
    private static readonly IReadOnlyList<IntegrationEnvironment> Envs = new[]
    {
        Env(1, "1_DEV", 1), Env(2, "2_TEST", 2), Env(3, "3_PROD", 3),
    };

    private static readonly IReadOnlyList<IntegrationRelation> Rels = new[]
    {
        Rel(10, 1, 2), Rel(11, 2, 3),
    };

    // ---- ResolveTarget ----------------------------------------------------

    [Fact]
    public void Target_is_the_next_environment_and_its_model_path()
    {
        var ctx = IntegrateFlow.ResolveTarget("Kursat/Integrate Test/1_DEV/MetaRepo", Envs, Rels);

        ctx.Should().NotBeNull();
        ctx!.CurrentEnvironment.Name.Should().Be("1_DEV");
        ctx.TargetEnvironment.Name.Should().Be("2_TEST");
        ctx.ModelName.Should().Be("MetaRepo");
        ctx.TargetFolderPath.Should().Be("Kursat/Integrate Test/2_TEST");
        ctx.TargetMartPath.Should().Be("Kursat/Integrate Test/2_TEST/MetaRepo");
    }

    [Fact]
    public void Target_walks_one_hop_at_a_time()
    {
        // From the MIDDLE environment the next hop is 3_PROD, not a jump back to 1_DEV.
        var ctx = IntegrateFlow.ResolveTarget("Kursat/Integrate Test/2_TEST/MetaRepo", Envs, Rels);

        ctx!.TargetEnvironment.Name.Should().Be("3_PROD");
        ctx.TargetMartPath.Should().Be("Kursat/Integrate Test/3_PROD/MetaRepo");
    }

    [Fact]
    public void Last_environment_has_nothing_to_integrate_into()
    {
        // 3_PROD has no outgoing transition -> no target -> the button stays "Save Model".
        IntegrateFlow.ResolveTarget("Kursat/Integrate Test/3_PROD/MetaRepo", Envs, Rels)
            .Should().BeNull();
    }

    [Fact]
    public void Model_outside_a_managed_environment_has_no_target()
    {
        IntegrateFlow.ResolveTarget("Kursat/MetaRepo", Envs, Rels).Should().BeNull();
        IntegrateFlow.ResolveTarget("Kursat/Some Folder/MetaRepo", Envs, Rels).Should().BeNull();
    }

    [Fact]
    public void Branching_topology_picks_the_lowest_sort_order_target()
    {
        // 1_DEV -> {3_PROD, 2_TEST}: pipeline order decides, so 2_TEST wins.
        var branching = new[] { Rel(20, 1, 3), Rel(21, 1, 2) };

        var ctx = IntegrateFlow.ResolveTarget("Kursat/Integrate Test/1_DEV/MetaRepo", Envs, branching);

        ctx!.TargetEnvironment.Name.Should().Be("2_TEST");
    }

    [Fact]
    public void A_one_level_base_still_composes_a_valid_target_path()
    {
        var ctx = IntegrateFlow.ResolveTarget("Base/1_DEV/M", Envs, Rels);

        ctx!.TargetFolderPath.Should().Be("Base/2_TEST");
        ctx.TargetMartPath.Should().Be("Base/2_TEST/M");
    }

    [Fact]
    public void Backslash_separators_normalise_to_forward_slashes()
    {
        var ctx = IntegrateFlow.ResolveTarget(@"Kursat\Integrate Test\1_DEV\MetaRepo", Envs, Rels);

        ctx!.TargetMartPath.Should().Be("Kursat/Integrate Test/2_TEST/MetaRepo");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_paths_are_safe(string? martPath)
    {
        IntegrateFlow.ResolveTarget(martPath, Envs, Rels).Should().BeNull();
    }

    [Fact]
    public void Null_admin_data_is_safe()
    {
        IntegrateFlow.ResolveTarget("Kursat/Integrate Test/1_DEV/MetaRepo", null, Rels).Should().BeNull();
        IntegrateFlow.ResolveTarget("Kursat/Integrate Test/1_DEV/MetaRepo", Envs, null).Should().BeNull();
    }
}
