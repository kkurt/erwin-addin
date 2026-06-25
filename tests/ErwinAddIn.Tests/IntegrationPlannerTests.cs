using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for <see cref="IntegrationPlanner"/>, the pure logic behind the
/// Integrate tab: current-environment detection from the Mart path and the
/// promotion-target build from admin ENVIRONMENT / ENVIRONMENT_RELATION rows.
/// Database I/O lives in <see cref="IntegrationEnvironmentService"/> and is not
/// exercised here on purpose - this guards the decision logic in isolation.
/// </summary>
public class IntegrationPlannerTests
{
    private static IntegrationEnvironment Env(int id, string name, int sortOrder) =>
        new(id, ConfigId: 7, name, sortOrder, Description: null, ColorHex: null);

    private static IntegrationRelation Rel(int id, int fromId, int toId, bool approval) =>
        new(id, ConfigId: 7, fromId, toId, approval);

    // ---- ParseParentFolder ------------------------------------------------

    [Theory]
    [InlineData("Kursat/MetaRepo/Dev/SalesModel", "Dev")]
    [InlineData("Kursat/MetaRepo/Test/SalesModel", "Test")]
    [InlineData("Dev/SalesModel", "Dev")]
    // Trailing separator must not become the model segment.
    [InlineData("Kursat/MetaRepo/Prod/SalesModel/", "Prod")]
    // Backslash separators are accepted alongside forward slashes.
    [InlineData(@"Kursat\MetaRepo\Test\M", "Test")]
    // A leading separator is ignored by empty-entry removal.
    [InlineData("/Dev/SalesModel", "Dev")]
    public void ParseParentFolder_returns_environment_segment(string martPath, string expected)
    {
        IntegrationPlanner.ParseParentFolder(martPath).Should().Be(expected);
    }

    [Theory]
    // A single segment is the model with no parent - not in a managed layout.
    [InlineData("SalesModel")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseParentFolder_returns_null_when_no_parent_segment(string martPath)
    {
        IntegrationPlanner.ParseParentFolder(martPath).Should().BeNull();
    }

    [Fact]
    public void ParseParentFolder_returns_null_for_null_path()
    {
        IntegrationPlanner.ParseParentFolder(null).Should().BeNull();
    }

    // ---- ResolveCurrentEnvironment ----------------------------------------

    [Fact]
    public void ResolveCurrentEnvironment_matches_parent_folder_to_environment_name()
    {
        var envs = new List<IntegrationEnvironment>
        {
            Env(1, "Dev", 1), Env(2, "Test", 2), Env(3, "Prod", 3)
        };

        var current = IntegrationPlanner.ResolveCurrentEnvironment(
            "Kursat/MetaRepo/Test/SalesModel", envs);

        current.Should().NotBeNull();
        current!.Id.Should().Be(2);
        current.Name.Should().Be("Test");
    }

    [Fact]
    public void ResolveCurrentEnvironment_is_case_insensitive()
    {
        var envs = new List<IntegrationEnvironment> { Env(1, "Dev", 1), Env(2, "Test", 2) };

        var current = IntegrationPlanner.ResolveCurrentEnvironment(
            "Kursat/MetaRepo/test/SalesModel", envs);

        current.Should().NotBeNull();
        current!.Id.Should().Be(2);
    }

    [Fact]
    public void ResolveCurrentEnvironment_returns_null_when_no_name_matches()
    {
        var envs = new List<IntegrationEnvironment> { Env(1, "Dev", 1), Env(2, "Prod", 2) };

        IntegrationPlanner.ResolveCurrentEnvironment(
            "Kursat/MetaRepo/Staging/SalesModel", envs).Should().BeNull();
    }

    [Fact]
    public void ResolveCurrentEnvironment_returns_null_when_path_has_no_parent()
    {
        var envs = new List<IntegrationEnvironment> { Env(1, "Dev", 1) };

        IntegrationPlanner.ResolveCurrentEnvironment("SalesModel", envs).Should().BeNull();
    }

    // ---- BuildTargets -----------------------------------------------------

    [Fact]
    public void BuildTargets_returns_only_relations_from_current_ordered_by_target_sort()
    {
        var envs = new List<IntegrationEnvironment>
        {
            Env(1, "Dev", 1), Env(2, "Test", 2), Env(3, "Prod", 3)
        };
        var relations = new List<IntegrationRelation>
        {
            // Intentionally out of SORT_ORDER to prove ordering by destination.
            Rel(10, fromId: 1, toId: 3, approval: true),   // Dev -> Prod (needs approval)
            Rel(11, fromId: 1, toId: 2, approval: false),  // Dev -> Test
            Rel(12, fromId: 2, toId: 3, approval: false)   // Test -> Prod (different source)
        };

        var targets = IntegrationPlanner.BuildTargets(currentEnvironmentId: 1, relations, envs);

        targets.Should().HaveCount(2);
        targets[0].Target.Id.Should().Be(2);          // Test first (SORT_ORDER 2)
        targets[0].RequiresApproval.Should().BeFalse();
        targets[1].Target.Id.Should().Be(3);          // Prod next (SORT_ORDER 3)
        targets[1].RequiresApproval.Should().BeTrue();
    }

    [Fact]
    public void BuildTargets_supports_backward_promotion()
    {
        var envs = new List<IntegrationEnvironment>
        {
            Env(1, "Dev", 1), Env(2, "Test", 2), Env(3, "Prod", 3)
        };
        // Prod -> Test is a legitimate backward transition (separate row).
        var relations = new List<IntegrationRelation> { Rel(20, fromId: 3, toId: 2, approval: false) };

        var targets = IntegrationPlanner.BuildTargets(currentEnvironmentId: 3, relations, envs);

        targets.Should().ContainSingle();
        targets[0].Target.Id.Should().Be(2);
    }

    [Fact]
    public void BuildTargets_skips_relation_whose_destination_is_missing()
    {
        var envs = new List<IntegrationEnvironment> { Env(1, "Dev", 1), Env(2, "Test", 2) };
        var relations = new List<IntegrationRelation>
        {
            Rel(30, fromId: 1, toId: 2, approval: false),   // valid
            Rel(31, fromId: 1, toId: 99, approval: false)   // orphan destination
        };

        var targets = IntegrationPlanner.BuildTargets(currentEnvironmentId: 1, relations, envs);

        targets.Should().ContainSingle();
        targets[0].Target.Id.Should().Be(2);
    }

    [Fact]
    public void BuildTargets_returns_empty_when_no_relations_from_current()
    {
        var envs = new List<IntegrationEnvironment> { Env(1, "Dev", 1), Env(2, "Test", 2) };
        var relations = new List<IntegrationRelation> { Rel(40, fromId: 2, toId: 1, approval: false) };

        IntegrationPlanner.BuildTargets(currentEnvironmentId: 1, relations, envs).Should().BeEmpty();
    }
}
