using System.Linq;
using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace ErwinAddIn.Tests
{
    /// <summary>
    /// With "Only Selected Objects" ticked, the blocking rules are checked over the tables the
    /// generated DDL names, because the diagram selection itself is unreadable for more than one
    /// entity (SCAPI has no selection API; erwin's Overview pane shows a count, not names).
    ///
    /// <para>The asymmetry that drives every case here: a name this MISSES means a table goes
    /// unchecked - a silent pass. A name it INVENTS matches no model entity and costs nothing.
    /// So the extractor is deliberately over-inclusive, and an empty result must be read by the
    /// caller as "cannot scope" rather than "nothing to check".</para>
    /// </summary>
    public class DdlTableScopeTests
    {
        [Fact]
        public void AlterTable_BracketedAndSchemaQualified()
        {
            DdlTableScope.Extract("ALTER TABLE [dbo].[E_58Log] ADD [Abc] nvarchar(5000) NULL")
                .Should().BeEquivalentTo(new[] { "E_58Log" });
        }

        [Fact]
        public void AlterTable_WithoutSchema_AsSeenInTheLiveScreenshot()
        {
            DdlTableScope.Extract("ALTER TABLE [Vpkursat]\nADD [Abc]  nvarchar(5000)  NULL\ngo")
                .Should().BeEquivalentTo(new[] { "Vpkursat" });
        }

        [Fact]
        public void CreateDropAndTruncate_AreAllTableStatements()
        {
            var names = DdlTableScope.Extract(
                "CREATE TABLE [dbo].[A] ( [x] int )\ngo\nDROP TABLE B\ngo\nTRUNCATE TABLE \"C\"\ngo");

            names.Should().BeEquivalentTo(new[] { "A", "B", "C" });
        }

        [Fact]
        public void SpRename_ContributesTheOldName()
        {
            // A real alter script renames the table aside before recreating it. Both names land
            // in the set; the generated one simply matches no model entity.
            var names = DdlTableScope.Extract(
                "execute sp_rename '[dbo].[E_58Log]', 'E_58LogA7RE5530000'\ngo\n" +
                "CREATE TABLE [dbo].[E_58Log] ( [CreatedBy] NVARCHAR(64) NOT NULL )\ngo");

            names.Should().Contain("E_58Log");
        }

        [Fact]
        public void ExtendedProperties_ContributeTheirLevel1Name()
        {
            var names = DdlTableScope.Extract(
                "EXEC sp_addextendedproperty\n@name = 'MS_Description', @value = 'x',\n" +
                "@level0type = 'SCHEMA', @level0name = 'dbo',\n" +
                "@level1type = 'TABLE', @level1name = 'E_58Log',\n" +
                "@level2type = 'COLUMN', @level2name = 'CreatedBy'\ngo");

            names.Should().Contain("E_58Log");
        }

        [Fact]
        public void OracleStyleCommentOn_IsRecognised()
        {
            DdlTableScope.Extract("COMMENT ON TABLE MYSCHEMA.CUSTOMER IS 'x';")
                .Should().Contain("CUSTOMER");
        }

        [Fact]
        public void MatchingIsCaseInsensitive()
        {
            var names = DdlTableScope.Extract("alter table [dbo].[Customer] add [x] int");
            names.Contains("CUSTOMER").Should().BeTrue();
        }

        [Fact]
        public void SeveralTables_AreAllCollected_WhichIsTheSelectedObjectsCase()
        {
            var names = DdlTableScope.Extract(
                "ALTER TABLE [dbo].[ORDERS] ADD [x] int\ngo\n" +
                "ALTER TABLE [dbo].[CUSTOMERS] ADD [y] int\ngo");

            names.Should().BeEquivalentTo(new[] { "ORDERS", "CUSTOMERS" });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("-- nothing to do here")]
        public void UnparseableOrEmptyScript_YieldsAnEmptySet(string ddl)
        {
            // The CALLER must turn this into "check the whole model". An empty scope reaching the
            // walk would check zero objects and report a clean model - the silent pass the whole
            // gate exists to prevent.
            DdlTableScope.Extract(ddl).Should().BeEmpty();
        }

        [Fact]
        public void ADuplicateNamedManyTimes_AppearsOnce()
        {
            var names = DdlTableScope.Extract(
                "ALTER TABLE [A] ADD [x] int\ngo\nALTER TABLE [A] ADD [y] int\ngo\nDROP TABLE [a]\ngo");

            names.Count.Should().Be(1);
        }
    }
}
