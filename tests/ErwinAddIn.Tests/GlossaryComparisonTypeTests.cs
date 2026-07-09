using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// GLOSSARY_COMPARISON_TYPE (2026-07-09) selects how a model element name is matched against the
/// glossary match-column values: EXACT = ordinal case-sensitive; CASE_INSENSITIVE (default) =
/// INVARIANT case-insensitive (ASCII fold, never tr-TR - Turkish dotted/dotless I are NOT folded).
/// The comparer is resolved once and drives the glossary cache dictionaries, so these tests pin the
/// comparer semantics that the whole match path (HasEntry / GetUdpValues / GetTermTypeCanonical)
/// inherits.
/// </summary>
public class GlossaryComparisonTypeTests
{
    // A dictionary built the way _glossaryCache is, to exercise ContainsKey the way the matcher does.
    private static Dictionary<string, int> Cache(GlossaryComparisonType type, params string[] keys)
    {
        var d = new Dictionary<string, int>(GlossaryService.ResolveMatchComparer(type));
        int i = 0;
        foreach (var k in keys) d[k] = i++;
        return d;
    }

    [Fact]
    public void Exact_matches_only_same_case()
    {
        var d = Cache(GlossaryComparisonType.EXACT, "SUBE_ADI");
        d.ContainsKey("SUBE_ADI").Should().BeTrue();
        d.ContainsKey("sube_adi").Should().BeFalse();
        d.ContainsKey("Sube_Adi").Should().BeFalse();
    }

    [Fact]
    public void CaseInsensitive_matches_regardless_of_case()
    {
        var d = Cache(GlossaryComparisonType.CASE_INSENSITIVE, "SUBE_ADI");
        d.ContainsKey("SUBE_ADI").Should().BeTrue();
        d.ContainsKey("sube_adi").Should().BeTrue();
        d.ContainsKey("Sube_Adi").Should().BeTrue();
    }

    [Fact]
    public void CaseInsensitive_is_INVARIANT_not_turkish()
    {
        // ASCII I/i fold (invariant): 'ISTANBUL' matches 'istanbul'.
        var ascii = Cache(GlossaryComparisonType.CASE_INSENSITIVE, "ISTANBUL");
        ascii.ContainsKey("istanbul").Should().BeTrue();

        // Turkish dotted/dotless I must NOT fold (that would be tr-TR/CurrentCulture behaviour):
        // 'İSTANBUL' (U+0130 dotted capital I) must NOT match ASCII 'istanbul',
        // and ASCII 'ISTANBUL' must NOT match 'ıstanbul' (U+0131 dotless i).
        Cache(GlossaryComparisonType.CASE_INSENSITIVE, "İSTANBUL").ContainsKey("istanbul").Should().BeFalse();
        Cache(GlossaryComparisonType.CASE_INSENSITIVE, "ISTANBUL").ContainsKey("ıstanbul").Should().BeFalse();
    }

    [Fact]
    public void ResolveMatchComparer_maps_the_modes()
    {
        GlossaryService.ResolveMatchComparer(GlossaryComparisonType.EXACT)
            .Should().BeSameAs(System.StringComparer.Ordinal);
        GlossaryService.ResolveMatchComparer(GlossaryComparisonType.CASE_INSENSITIVE)
            .Should().BeSameAs(System.StringComparer.OrdinalIgnoreCase);
    }
}
