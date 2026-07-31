using System;
using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// <see cref="ValidationCoordinatorService.IsPropertyNotSet"/> separates erwin's
/// "this optional property has never been written" from a real read failure, so the
/// three naming-validation read sites can report the ordinary case in one line while
/// keeping the full diagnostic for anything else.
///
/// <para>Added 2026-08-01. The log previously said "SCAPI did not surface
/// 'Model.Definition'" followed by the whole 200-character COM message, three times
/// per validation pass, for a model whose optional Comment field was simply empty.
/// It reads as a defect and cost real triage time.</para>
/// </summary>
public class PropertyNotSetDetectionTests
{
    /// <summary>The message erwin r10.10 actually produced, copied from the log verbatim.</summary>
    private const string LiveErwinMessage =
        "Model Properties Component ! Model object {799A0437-D91A-4CC5-998A-7C23E2130F39}+00000001 "
        + "of Model class does not use a property of Definition type or the property failed to "
        + "satisfy a property collection filter conditions";

    [Fact]
    public void The_live_erwin_unset_property_message_is_recognised()
    {
        ValidationCoordinatorService.IsPropertyNotSet(new InvalidOperationException(LiveErwinMessage))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("Entity object of Entity class does not use a property of Definition type")]
    [InlineData("does not use a property of Name_Qualifier type")]
    [InlineData("DOES NOT USE A PROPERTY of X type")]
    public void The_match_is_on_the_stable_phrase_and_is_case_insensitive(string message)
    {
        ValidationCoordinatorService.IsPropertyNotSet(new InvalidOperationException(message))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("The RPC server is unavailable. (0x800706BA)")]
    [InlineData("not a valid class id or class name for object or property")]
    [InlineData("Access violation reading location 0x00000000")]
    [InlineData("")]
    public void A_real_read_failure_is_not_mistaken_for_an_unset_property(string message)
    {
        // These must keep their full text in the log: reporting a dead session or an
        // AV as "optional property never written" would hide a genuine fault.
        ValidationCoordinatorService.IsPropertyNotSet(new InvalidOperationException(message))
            .Should().BeFalse();
    }

    [Fact]
    public void A_null_exception_is_not_an_unset_property()
    {
        ValidationCoordinatorService.IsPropertyNotSet(null).Should().BeFalse();
    }

    [Fact]
    public void It_does_not_collide_with_the_wrong_owner_class_signal()
    {
        // NamingValidationEngine.IsNotOnThisClass matches "not valid class", which
        // means "this UDP belongs to a DIFFERENT object type" and drives the
        // Model.Physical fallback. The two predicates must stay disjoint, or a
        // misrouted UDP read would be logged as a harmless unset property.
        var wrongClass = new InvalidOperationException(
            "not a valid class id or class name for object or property");

        ValidationCoordinatorService.IsPropertyNotSet(wrongClass).Should().BeFalse();
    }
}
