using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Pure-matcher coverage for the admin "Datatype Library" whitelist
/// (<see cref="AllowedDatatypeService.IsDatatypeAllowed"/>). Rules (user decision
/// 2026-06-19): empty whitelist = no restriction; base token matched
/// case-insensitively; a non-parameterized entry permits the base ONLY without a
/// length; a parameterized entry permits the base with ANY length (incl. none).
/// </summary>
public class AllowedDatatypeMatcherTests
{
    private static AllowedDatatypeEntry T(string name, bool param = false) =>
        new() { Datatype = name, IsParameterized = param };

    private static List<AllowedDatatypeEntry> Set(params AllowedDatatypeEntry[] e) => new(e);

    // ---------- empty whitelist = no restriction ----------

    [Theory]
    [InlineData("int")]
    [InlineData("varchar(50)")]
    [InlineData("anything")]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_whitelist_allows_everything(string? value)
    {
        AllowedDatatypeService.IsDatatypeAllowed(value, Set()).Should().BeTrue();
        AllowedDatatypeService.IsDatatypeAllowed(value, null).Should().BeTrue();
    }

    // ---------- non-parameterized entry (e.g. "int") ----------

    [Theory]
    [InlineData("int", true)]
    [InlineData("INT", true)]          // case-insensitive base
    [InlineData("  int  ", true)]      // parser trims
    [InlineData("int(5)", false)]      // non-param must NOT carry a length
    [InlineData("bigint", false)]      // different base
    [InlineData("varchar(50)", false)]
    public void NonParameterized_int_only(string value, bool expected)
    {
        var allowed = Set(T("int"));
        AllowedDatatypeService.IsDatatypeAllowed(value, allowed).Should().Be(expected);
    }

    // ---------- parameterized entry (e.g. "nvarchar") ----------

    [Theory]
    [InlineData("nvarchar(50)", true)]
    [InlineData("nvarchar(4000)", true)]
    [InlineData("NVARCHAR(200)", true)]   // case-insensitive
    [InlineData("nvarchar", true)]        // param accepts the base even with no length
    [InlineData("int", false)]            // different base
    [InlineData("varchar(50)", false)]    // different base (varchar != nvarchar)
    public void Parameterized_nvarchar(string value, bool expected)
    {
        var allowed = Set(T("nvarchar", param: true));
        AllowedDatatypeService.IsDatatypeAllowed(value, allowed).Should().Be(expected);
    }

    [Theory]
    [InlineData("Numeric(10,2)", true)]   // multi-arg length under a parameterized entry
    [InlineData("Numeric(18)", true)]
    [InlineData("numeric", true)]
    [InlineData("Number(10,2)", false)]   // Oracle 'Number' != 'Numeric'
    public void Parameterized_numeric_with_precision(string value, bool expected)
    {
        AllowedDatatypeService.IsDatatypeAllowed(value, Set(T("Numeric", param: true))).Should().Be(expected);
    }

    // ---------- mixed whitelist (the user's likely real config) ----------

    [Fact]
    public void Mixed_whitelist_int_and_nvarchar()
    {
        var allowed = Set(T("int"), T("nvarchar", param: true), T("bit"), T("DateTime"));

        AllowedDatatypeService.IsDatatypeAllowed("int", allowed).Should().BeTrue();
        AllowedDatatypeService.IsDatatypeAllowed("nvarchar(255)", allowed).Should().BeTrue();
        AllowedDatatypeService.IsDatatypeAllowed("bit", allowed).Should().BeTrue();
        AllowedDatatypeService.IsDatatypeAllowed("datetime", allowed).Should().BeTrue();

        AllowedDatatypeService.IsDatatypeAllowed("varchar(50)", allowed).Should().BeFalse();
        AllowedDatatypeService.IsDatatypeAllowed("float", allowed).Should().BeFalse();
        AllowedDatatypeService.IsDatatypeAllowed("char(18)", allowed).Should().BeFalse();
        AllowedDatatypeService.IsDatatypeAllowed("binary()", allowed).Should().BeFalse();
        AllowedDatatypeService.IsDatatypeAllowed("int(5)", allowed).Should().BeFalse(); // int is non-param
    }

    // ---------- unclassifiable input is not blocked ----------

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Unclassifiable_value_is_allowed(string? value)
    {
        // A non-empty whitelist must not block an empty/blank type (cannot classify) -
        // erwin transiently surfaces "" mid-edit; blocking it would fight the editor.
        AllowedDatatypeService.IsDatatypeAllowed(value, Set(T("int"))).Should().BeTrue();
    }

    // ---------- SeedForTesting reaches the instance matcher ----------

    // ---------- GetFallbackDatatype: the type a disallowed value is forced to ----------

    [Fact]
    public void GetFallbackDatatype_prefers_first_complete_nonparameterized()
    {
        // A parameterized entry first, then a complete non-param one: the non-param wins
        // because its base token is a valid standalone Physical_Data_Type ("int").
        AllowedDatatypeService.Instance.SeedForTesting(new[] { T("nvarchar", param: true), T("int") });
        try
        {
            AllowedDatatypeService.Instance.GetFallbackDatatype().Should().Be("int");
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_single_allowed_type_is_used()
    {
        AllowedDatatypeService.Instance.SeedForTesting(new[] { T("int") });
        try
        {
            AllowedDatatypeService.Instance.GetFallbackDatatype().Should().Be("int");
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_all_parameterized_synthesizes_minimal_length()
    {
        // No non-parameterized type to fall back on: a bare "nvarchar" may be rejected by erwin, so
        // the fallback synthesizes a minimal valid length. Which base wins depends on the load
        // query's ORDER BY dl.DATATYPE (not SeedForTesting's insertion order), so assert either -
        // and assert the synthesized token round-trips through the matcher (no revert loop).
        AllowedDatatypeService.Instance.SeedForTesting(new[] { T("nvarchar", param: true), T("Numeric", param: true) });
        try
        {
            var fb = AllowedDatatypeService.Instance.GetFallbackDatatype();
            fb.Should().BeOneOf("nvarchar(1)", "Numeric(1)");
            AllowedDatatypeService.Instance.IsAllowed(fb).Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_null_when_no_restriction()
    {
        AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>());
        AllowedDatatypeService.Instance.GetFallbackDatatype().Should().BeNull();
    }

    [Fact]
    public void SeedForTesting_drives_instance_IsAllowed()
    {
        AllowedDatatypeService.Instance.SeedForTesting(new[] { T("int") });
        try
        {
            AllowedDatatypeService.Instance.HasRestriction.Should().BeTrue();
            AllowedDatatypeService.Instance.IsAllowed("int").Should().BeTrue();
            AllowedDatatypeService.Instance.IsAllowed("varchar(10)").Should().BeFalse();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>()); }

        // Empty seed -> no restriction.
        AllowedDatatypeService.Instance.HasRestriction.Should().BeFalse();
        AllowedDatatypeService.Instance.IsAllowed("anything").Should().BeTrue();
    }
}
