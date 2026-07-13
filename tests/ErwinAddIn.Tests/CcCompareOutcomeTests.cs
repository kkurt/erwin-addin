using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// CcCompareOutcome.IsNoDifferenceInfoText classifies the static text of the
/// message box erwin raises after Complete Compare / Review when the compared
/// sides are identical (job-4 incident 2026-07-11: this outcome used to fail
/// the run with a generic timeout and the leftover wizard froze erwin).
/// The classifier must catch erwin's known wording variants and must NOT
/// fire on ordinary error/confirmation popups (a false positive would mark a
/// genuinely failed compare as DONE-no-diff).
/// </summary>
public class CcCompareOutcomeTests
{
    [Theory]
    // Complete Compare outcome variants (wording differs across releases/contexts).
    [InlineData("There are no differences between the selected models.")]
    [InlineData("No differences were detected between the selected objects.")]
    [InlineData("Complete Compare found no differences.")]
    [InlineData("The models are identical.")]
    [InlineData("Left and right models are identical - nothing to resolve.")]
    // Mart > Review refusal (clean checked-out model) - also a no-diff verdict.
    [InlineData("There have been no changes to model since it was checked out.")]
    [InlineData("Nothing to compare.")]
    // Case-insensitivity.
    [InlineData("THERE ARE NO DIFFERENCES BETWEEN THE SELECTED MODELS.")]
    [InlineData("no difference found")]
    public void IsNoDifferenceInfoText_matches_known_no_diff_wordings(string text)
    {
        CcCompareOutcome.IsNoDifferenceInfoText(text).Should().BeTrue();
    }

    [Theory]
    // Null/empty guard.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Ordinary erwin popups that must NOT be classified as no-diff.
    [InlineData("Do you want to save changes to the model?")]
    [InlineData("Use current diagram selections? You have 3 entity selected.")]
    [InlineData("GDM-1001: An unexpected condition was encountered.")]
    [InlineData("The differences could not be resolved automatically.")]
    [InlineData("License validation failed - contact your administrator.")]
    // "changes"/"difference" appearing in a NON-no-diff sentence.
    [InlineData("Some differences require manual resolution.")]
    [InlineData("Save your changes before comparing.")]
    public void IsNoDifferenceInfoText_rejects_non_no_diff_text(string? text)
    {
        CcCompareOutcome.IsNoDifferenceInfoText(text).Should().BeFalse();
    }
}
