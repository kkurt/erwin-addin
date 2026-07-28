using System.Collections.Generic;
using System.Linq;
using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace ErwinAddIn.Tests
{
    /// <summary>
    /// The rule-level report is what the DDL review pane renders, and it is the one part of the
    /// approval gate that is pure: no SCAPI, no DB. These tests pin the two properties the pane
    /// depends on and that nothing else can catch.
    ///
    /// <para><b>Why the pre-cap rule matters.</b> The issue list handed to the UI is truncated at
    /// <see cref="ApprovalBlockingRuleGate.MaxReportedIssues"/>. If a rule's status were derived
    /// from that truncated slice, a rule whose violations all fell past the cap would have no
    /// reported issues and would render "Passed" beside a model it actually rejects - the exact
    /// silent pass this whole feature exists to prevent.</para>
    /// </summary>
    public class ApprovalRuleReportTests
    {
        private static ApprovalBlockingRuleGate.RuleOutcome Outcome(
            int ruleId,
            int objectsChecked,
            int applicable,
            int violations,
            bool unevaluatable = false,
            string ruleName = "",
            string objectType = "COLUMN",
            string propertyLabel = "Physical Name",
            string summary = "Regexp")
        {
            var outcome = new ApprovalBlockingRuleGate.RuleOutcome
            {
                RuleId = ruleId,
                RuleName = ruleName,
                Summary = summary,
                ObjectType = objectType,
                PropertyLabel = propertyLabel,
                ObjectsChecked = objectsChecked,
                Applicable = applicable,
                Violations = violations
            };
            outcome.Resolve(unevaluatable);
            return outcome;
        }

        private static ApprovalBlockingIssue Violation(int ruleId, string objectName)
            => new ApprovalBlockingIssue(
                ApprovalBlockingIssueKind.Violation, ruleId, "COLUMN", objectName,
                "Physical Name", "Regexp", "admin message");

        [Fact]
        public void CleanRule_OverApplicableObjects_IsPassed()
        {
            Outcome(1, objectsChecked: 250, applicable: 250, violations: 0)
                .Status.Should().Be(ApprovalRuleStatus.Passed);
        }

        [Fact]
        public void RuleThatMatchedNothing_IsNotApplicable_NotPassed()
        {
            // "Condition matched no object" must never read as "verified your model clean".
            Outcome(1, objectsChecked: 250, applicable: 0, violations: 0)
                .Status.Should().Be(ApprovalRuleStatus.NotApplicable);
        }

        [Fact]
        public void AnyViolation_IsFailed_EvenWhenMostObjectsPass()
        {
            Outcome(1, objectsChecked: 250, applicable: 250, violations: 1)
                .Status.Should().Be(ApprovalRuleStatus.Failed);
        }

        [Fact]
        public void Unevaluatable_OutranksEverything()
        {
            // An admin data problem, not a modeller problem - and it still blocks.
            Outcome(1, objectsChecked: 250, applicable: 250, violations: 7, unevaluatable: true)
                .Status.Should().Be(ApprovalRuleStatus.NotChecked);
        }

        [Fact]
        public void NotCheckedRule_ForARuleTheWalkNeverReached()
        {
            var outcome = ApprovalBlockingRuleGate.RuleOutcome.NotCheckedRule(
                1081, "Tablo Ad Standarti", "aciklama", "Regexp", "TABLE", "Physical Name");

            outcome.Status.Should().Be(ApprovalRuleStatus.NotChecked);
            outcome.RuleId.Should().Be(1081);
            outcome.ObjectsChecked.Should().Be(0);
        }

        [Fact]
        public void StatusSurvivesTheCap_WhenEveryViolationWasTruncatedAway()
        {
            // The regression this file exists for: 25 violations, a cap of 20, and this rule's
            // reported slice happens to be EMPTY because other rules filled the cap.
            var outcomes = new List<ApprovalBlockingRuleGate.RuleOutcome>
            {
                Outcome(1081, objectsChecked: 8401, applicable: 8401, violations: 25)
            };

            var report = ApprovalBlockingRuleGate.BuildRuleReport(
                outcomes, new List<ApprovalBlockingIssue>());

            var row = report.Single();
            row.Status.Should().Be(ApprovalRuleStatus.Failed,
                "the verdict comes from the walk, not from the truncated issue list");
            row.ViolationCount.Should().Be(25, "the count is the TRUE one, not the reported one");
            row.ReportedIssues.Should().BeEmpty();
        }

        [Fact]
        public void EachRowGetsOnlyItsOwnIssues()
        {
            var outcomes = new List<ApprovalBlockingRuleGate.RuleOutcome>
            {
                Outcome(10, 100, 100, 2),
                Outcome(20, 100, 100, 1)
            };
            var reported = new List<ApprovalBlockingIssue>
            {
                Violation(10, "T.A"), Violation(20, "T.B"), Violation(10, "T.C")
            };

            var report = ApprovalBlockingRuleGate.BuildRuleReport(outcomes, reported);

            report.Single(r => r.RuleId == 10).ReportedIssues
                  .Select(i => i.ObjectName).Should().BeEquivalentTo(new[] { "T.A", "T.C" });
            report.Single(r => r.RuleId == 20).ReportedIssues
                  .Select(i => i.ObjectName).Should().BeEquivalentTo(new[] { "T.B" });
        }

        [Fact]
        public void GateLevelIssuesAreNotAttachedToAnyRule()
        {
            // RuleId 0 is the gate's own sentinel; it belongs in a banner, not under a rule.
            var outcomes = new List<ApprovalBlockingRuleGate.RuleOutcome> { Outcome(10, 1, 1, 1) };
            var reported = new List<ApprovalBlockingIssue>
            {
                new ApprovalBlockingIssue(ApprovalBlockingIssueKind.Unevaluatable, 0, "", "", "", "", "no config"),
                Violation(10, "T.A")
            };

            var report = ApprovalBlockingRuleGate.BuildRuleReport(outcomes, reported);

            report.Single().ReportedIssues.Should().HaveCount(1);
            report.Single().ReportedIssues[0].ObjectName.Should().Be("T.A");
        }

        [Fact]
        public void RowsAreOrderedMostActionableFirst_ThenStableByRuleId()
        {
            var outcomes = new List<ApprovalBlockingRuleGate.RuleOutcome>
            {
                Outcome(50, 10, 10, 0),                          // Passed
                Outcome(40, 10, 0, 0),                           // NotApplicable
                Outcome(30, 10, 10, 0, unevaluatable: true),     // NotChecked
                Outcome(20, 10, 10, 3),                          // Failed
                Outcome(10, 10, 10, 1)                           // Failed, lower id
            };

            var report = ApprovalBlockingRuleGate.BuildRuleReport(
                outcomes, new List<ApprovalBlockingIssue>());

            report.Select(r => r.RuleId).Should().Equal(10, 20, 30, 40, 50);
            report.Select(r => r.Status).Should().Equal(
                ApprovalRuleStatus.Failed, ApprovalRuleStatus.Failed,
                ApprovalRuleStatus.NotChecked, ApprovalRuleStatus.NotApplicable,
                ApprovalRuleStatus.Passed);
        }

        [Fact]
        public void DisplayName_PrefersTheAdminLabel()
        {
            var report = ApprovalBlockingRuleGate.BuildRuleReport(
                new List<ApprovalBlockingRuleGate.RuleOutcome>
                {
                    Outcome(1081, 10, 10, 0, ruleName: "Tablo Ad Standarti")
                },
                new List<ApprovalBlockingIssue>());

            report.Single().DisplayName.Should().Be("Tablo Ad Standarti");
        }

        [Fact]
        public void DisplayName_FallsBackWhenAdminLeftNameEmpty()
        {
            // MC_NAMING_STANDARD.NAME is unpopulated in most repos (0 of 103 rules in one), so
            // the fallback is the NORMAL path, not an edge case. It must never be blank.
            var report = ApprovalBlockingRuleGate.BuildRuleReport(
                new List<ApprovalBlockingRuleGate.RuleOutcome>
                {
                    Outcome(1081, 10, 10, 0, ruleName: "  ", summary: "Regexp 'nvarchar'")
                },
                new List<ApprovalBlockingIssue>());

            report.Single().DisplayName.Should().Be("#1081 Regexp 'nvarchar'");
        }

        [Fact]
        public void Scope_OmitsTheDotWhenTheRuleHasNoProperty()
        {
            var report = ApprovalBlockingRuleGate.BuildRuleReport(
                new List<ApprovalBlockingRuleGate.RuleOutcome>
                {
                    Outcome(1, 1, 1, 0, objectType: "TABLE", propertyLabel: "")
                },
                new List<ApprovalBlockingIssue>());

            report.Single().Scope.Should().Be("TABLE");
        }

        [Fact]
        public void EmptyOutcomeList_YieldsNoRows()
        {
            ApprovalBlockingRuleGate.BuildRuleReport(
                new List<ApprovalBlockingRuleGate.RuleOutcome>(),
                new List<ApprovalBlockingIssue>()).Should().BeEmpty();
        }

        [Fact]
        public void ReportCap_IsTwenty()
        {
            // Product decision 2026-07-27: the pane shows at most 20 detail rows, down from 200.
            ApprovalBlockingRuleGate.MaxReportedIssues.Should().Be(20);
        }
    }
}
