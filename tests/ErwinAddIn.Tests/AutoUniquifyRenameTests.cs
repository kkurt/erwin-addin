using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// <see cref="NamingValidationEngine.IsAutoUniquifyRename"/> detects erwin's collision uniquify
/// ("Foo" -> "Foo__NNNN"). When true, the rename sites re-validate the erwin-assigned name as a
/// fresh CREATE (isNew=true) so apply=Create rules re-fire on it. 2026-07-08 bug: a 2nd column the
/// add-in named "Pre_Abc" became "Pre_Abc__1069" and the digit name bypassed the Create-scoped
/// PascalCase rule#1127 because the re-validation ran isNew=false.
/// </summary>
public class AutoUniquifyRenameTests
{
    [Theory]
    [InlineData("Pre_Abc", "Pre_Abc__1069", true)]   // the live repro
    [InlineData("Foo", "Foo__1", true)]
    [InlineData("Foo", "Foo__12345", true)]
    [InlineData("VpOrder", "VpOrder__2", true)]       // table-style
    // NOT a uniquify:
    [InlineData("Foo", "Bar", false)]                 // different base
    [InlineData("Foo", "Foo", false)]                 // unchanged
    [InlineData("Foo", "Foo_1", false)]               // single underscore
    [InlineData("Foo", "Foo__", false)]               // no digits after "__"
    [InlineData("Foo", "Foo__1a", false)]             // tail not all digits
    [InlineData("Foo", "Foo__1_2", false)]            // tail not all digits
    [InlineData("Foo", "FooBar__1", false)]           // "__" must immediately follow the exact base
    [InlineData("Foo", "foo__1", false)]              // Ordinal: base is case-sensitive
    [InlineData("", "Foo__1", false)]                 // empty prev
    [InlineData("Foo", "", false)]                    // empty new
    [InlineData(null, "Foo__1", false)]
    [InlineData("Foo", null, false)]
    public void IsAutoUniquifyRename_matches_only_the_erwin_signature(string prev, string next, bool expected)
    {
        NamingValidationEngine.IsAutoUniquifyRename(prev, next).Should().Be(expected);
    }
}

/// <summary>
/// <see cref="NamingValidationEngine.RenameRequiresRevalidation"/> generalizes the auto-uniquify
/// re-validation to ANY real rename (2026-07-10): a manual Model Explorer / Properties-pane /
/// Column Editor rename of an EXISTING object must re-run apply=Create naming rules on the new
/// name (rule#1127 no-digits etc.). It drives ONLY validation scope; the caller keeps a separate
/// identity flag so a Required-popup Cancel reverts the name instead of deleting the object. This
/// is why the earlier bug (rename 'Pre_Abcd_3' -> 'Pre_Abcd3' passed with a digit) is now caught.
/// </summary>
public class RenameRevalidationTests
{
    // A stand-in for the caller's object-kind placeholder test.
    private static bool IsPlaceholder(string n) =>
        string.IsNullOrEmpty(n) || n == "<default>" || n.StartsWith("%", System.StringComparison.Ordinal);

    [Theory]
    // A real rename of an existing name -> re-validate.
    [InlineData("Pre_Abcd_3", "Pre_Abcd3", true)]   // the live repro (added a digit)
    [InlineData("Pre_Abcd", "Pre_Abcd_3", true)]
    [InlineData("Musteri", "Musteri2", true)]
    [InlineData("Foo", "Bar", true)]
    // Not a rename (unchanged) -> do NOT re-flag an untouched (even nonconforming) name.
    [InlineData("Pre_Abcd3", "Pre_Abcd3", false)]
    [InlineData("Foo", "Foo", false)]
    // Baseline is a placeholder or empty -> this is a new-name commit, handled by the identity
    // flag (isNew/pending), not by the rename path.
    [InlineData("<default>", "Foo3", false)]
    [InlineData("%generated", "Foo3", false)]
    [InlineData("", "Foo3", false)]
    [InlineData(null, "Foo3", false)]
    public void RenameRequiresRevalidation_fires_only_on_a_real_rename(string baseline, string current, bool expected)
    {
        NamingValidationEngine.RenameRequiresRevalidation(baseline, current, IsPlaceholder).Should().Be(expected);
    }

    [Fact]
    public void RenameRequiresRevalidation_is_ordinal_case_sensitive()
    {
        // A case-only change IS a rename the rules should see (e.g. a PascalCase rule).
        NamingValidationEngine.RenameRequiresRevalidation("foo", "Foo", IsPlaceholder).Should().BeTrue();
    }

    [Fact]
    public void RenameRequiresRevalidation_tolerates_a_null_placeholder_probe()
    {
        // With no placeholder probe, only the empty-baseline and equality guards apply.
        NamingValidationEngine.RenameRequiresRevalidation("Foo", "Bar", null).Should().BeTrue();
        NamingValidationEngine.RenameRequiresRevalidation("Foo", "Foo", null).Should().BeFalse();
    }
}
