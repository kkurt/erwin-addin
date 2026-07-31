using System;
using EliteSoft.Erwin.AddIn.Services;
using Xunit;

namespace ErwinAddIn.Tests
{
    /// <summary>
    /// <see cref="AddinLogger.Describe(Exception)"/> exists because several
    /// framework exceptions say nothing in their own message and put the whole
    /// cause in the inner one. These tests pin the flattening, since a
    /// regression here is invisible until the next incident.
    /// </summary>
    public class AddinLoggerDescribeTests
    {
        [Fact]
        public void Describe_flattens_the_whole_chain()
        {
            var ex = new InvalidOperationException(
                "outer",
                new ArgumentException("middle", new FormatException("root cause")));

            Assert.Equal(
                "InvalidOperationException: outer -> ArgumentException: middle -> FormatException: root cause",
                AddinLogger.Describe(ex));
        }

        [Fact]
        public void Describe_keeps_the_inner_cause_of_a_useless_outer_message()
        {
            // The real 2026-07-30 shape: EF Core's DbUpdateException carries no
            // information at all, the SqlException underneath names the column.
            var ex = new InvalidOperationException(
                "An error occurred while saving the entity changes. See the inner exception for details.",
                new Exception("Invalid column name 'IS_AUTO_DDL_GENERATOR'."));

            Assert.Contains("IS_AUTO_DDL_GENERATOR", AddinLogger.Describe(ex), StringComparison.Ordinal);
        }

        [Fact]
        public void Describe_unwraps_a_single_child_aggregate()
        {
            var ex = new AggregateException(new TimeoutException("repo unreachable"));

            Assert.Equal("TimeoutException: repo unreachable", AddinLogger.Describe(ex));
        }

        [Fact]
        public void Describe_keeps_a_multi_child_aggregate_as_is()
        {
            var ex = new AggregateException(
                new TimeoutException("first"),
                new InvalidOperationException("second"));

            string text = AddinLogger.Describe(ex);

            Assert.StartsWith("AggregateException: ", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Describe_caps_a_pathological_chain()
        {
            Exception ex = new Exception("level 0");
            for (int i = 1; i <= 20; i++)
                ex = new Exception($"level {i}", ex);

            string text = AddinLogger.Describe(ex);

            Assert.EndsWith(" -> ...", text, StringComparison.Ordinal);
            // 8 links plus the truncation marker, never the full 21.
            Assert.Equal(8, text.Split(new[] { " -> " }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("level 0", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Describe_survives_a_null_exception()
        {
            Assert.Equal("(no exception)", AddinLogger.Describe(null));
        }
    }
}
