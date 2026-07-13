using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// <see cref="TermTypeLocks"/> is the single source of term-type CHANGEABILITY: which parts of a
/// column's Physical_Data_Type may change per canonical term type. It gates the Datatype Library
/// picker (combo disabled when the base is locked, parameter field when the length is locked, no
/// picker at all when both are) and vets remembered picks. 2026-07-09 bug: the picker was
/// term-type-blind and let the user override a BUSINESS_TERM lock with a free pick.
/// </summary>
public class TermTypeLocksTests
{
    [Theory]
    [InlineData("BUSINESS_TERM", true, true)]
    [InlineData("business_term", true, true)]      // case-insensitive
    [InlineData(" BUSINESS_TERM ", true, true)]    // trimmed
    [InlineData("AMORPH_DATA_TYPE", false, true)]  // base free, length fixed
    [InlineData("AMORPH_DATA_LENGTH", true, false)] // base fixed, length free
    [InlineData("AMORPH", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    [InlineData("SOMETHING_ELSE", false, false)]   // unknown = fail-open (mirrors policy default)
    public void Get_maps_canonical_to_lock_flags(string canonical, bool expectBase, bool expectLength)
    {
        TermTypeLocks.Get(canonical).Should().Be((expectBase, expectLength));
    }

    // ---------- Honors: does a candidate respect the locked parts? ----------

    [Theory]
    // no locks -> anything honors
    [InlineData("int", "NVARCHAR(255)", false, false, true)]
    // base locked: same base (any case) ok, different base not
    [InlineData("nvarchar(100)", "NVARCHAR(255)", true, false, true)]
    [InlineData("varchar(255)", "NVARCHAR(255)", true, false, false)]
    // length locked: same length ok, different not (ordinal - '250 CHAR' style significant)
    [InlineData("varchar(255)", "NVARCHAR(255)", false, true, true)]
    [InlineData("nvarchar(100)", "NVARCHAR(255)", false, true, false)]
    // both locked (BUSINESS_TERM): only the exact composition honors
    [InlineData("NVARCHAR(255)", "NVARCHAR(255)", true, true, true)]
    [InlineData("nvarchar(255)", "NVARCHAR(255)", true, true, true)]   // base case-insensitive
    [InlineData("NUMBER(45)", "VARCHAR2(250 CHAR)", true, true, false)] // the live override attempt
    [InlineData("VARCHAR2(250 CHAR)", "VARCHAR2(250 CHAR)", true, true, true)]
    [InlineData("VARCHAR2(250 char)", "VARCHAR2(250 CHAR)", true, true, false)] // length ordinal
    // bare vs parameterized under length lock
    [InlineData("int", "int", false, true, true)]           // both bare: length "" == ""
    [InlineData("int(5)", "int", false, true, false)]
    public void Honors_checks_locked_parts_only(string candidate, string authoritative, bool lockBase, bool lockLength, bool expected)
    {
        TermTypeLocks.Honors(candidate, authoritative, lockBase, lockLength).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "NVARCHAR(255)")]
    [InlineData("", "NVARCHAR(255)")]
    [InlineData("int", null)]
    [InlineData("int", "")]
    public void Honors_fails_closed_on_missing_values_when_locked(string candidate, string authoritative)
    {
        TermTypeLocks.Honors(candidate, authoritative, true, false).Should().BeFalse();
    }

    /// <summary>
    /// 2026-07-10: the durable fixed length is GLOSSARY-FIRST - the term mapping's own datatype
    /// DEFINES it; the snapshot baseline only anchors when the mapping carries no datatype.
    /// Two field repros pinned here: (a) parameterless BIGINT picked under AMORPH_DATA_TYPE
    /// erased the length from the snapshot (pin came up empty); (b) a poisoned baseline -
    /// 'Numeric(555)' absorbed during a canonical-unresolved window - made every revert restore
    /// 555 instead of the term-fixed 5 ("Restored: NUMERIC(555)" dialog).
    /// </summary>
    [Theory]
    [InlineData("NUMERIC(5)", "NUMERIC(5)", "5")]      // agree - trivially 5
    [InlineData("NUMERIC(555)", "NUMERIC(5)", "5")]    // poisoned snapshot - glossary wins (the field bug)
    [InlineData("nvarchar(55)", "NVARCHAR(15)", "15")] // glossary defines the lock, snapshot drifted
    [InlineData("BIGINT", "NUMERIC(5)", "5")]          // parameterless snapshot - glossary supplies it
    [InlineData("", "NUMERIC(5)", "5")]                // no snapshot at all
    [InlineData(null, "NUMERIC(5)", "5")]
    [InlineData("NUMERIC(10,2)", "BIGINT", "10,2")]    // glossary has no length - snapshot anchors
    [InlineData("BIGINT", "DATE", "")]                 // neither side has a length - nothing to pin
    [InlineData("BIGINT", "", "")]
    [InlineData(null, null, "")]
    public void ResolveLockedLength_prefers_glossary_then_snapshot(string snapshot, string glossary, string expected)
    {
        TermTypeLocks.ResolveLockedLength(snapshot, glossary).Should().Be(expected);
    }
}
