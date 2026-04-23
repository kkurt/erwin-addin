using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

public class CreateDdlParserTests
{
    [Fact]
    public void Parses_MSSQL_CREATE_TABLE()
    {
        const string ddl = """
            CREATE TABLE [app].[CUSTOMER] (
                [customer_id] INT NOT NULL,
                [full_name] VARCHAR(150) NOT NULL,
                [email] VARCHAR(200) NULL,
                [tax_no] VARCHAR(20) NULL,
                [created_at] DATETIME2 NOT NULL
            );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TableCount.Should().Be(1);
        map.TryGetType("CUSTOMER", "customer_id", out var t).Should().BeTrue();
        t.Should().Be("INT");
        map.TryGetType("CUSTOMER", "full_name", out t).Should().BeTrue();
        t.Should().Be("VARCHAR(150)");
    }

    [Fact]
    public void Parses_Oracle_CREATE_TABLE_without_brackets()
    {
        const string ddl = """
            CREATE TABLE APP.CUSTOMER (
                customer_id  NUMBER(10)  NOT NULL,
                full_name    VARCHAR2(150)  NOT NULL,
                address      VARCHAR2(250),
                created_at   TIMESTAMP  DEFAULT SYSTIMESTAMP
            );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TryGetType("CUSTOMER", "customer_id", out var t1).Should().BeTrue();
        t1.Should().Be("NUMBER(10)");
        map.TryGetType("CUSTOMER", "address", out var t2).Should().BeTrue();
        t2.Should().Be("VARCHAR2(250)");
    }

    [Fact]
    public void Parses_Db2_CREATE_TABLE()
    {
        const string ddl = """
            CREATE TABLE APP.ORDERS (
                order_id     INTEGER NOT NULL,
                customer_id  INTEGER NOT NULL,
                order_amount BIGINT NOT NULL,
                order_date   DATE NOT NULL
            );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TryGetType("ORDERS", "order_amount", out var t).Should().BeTrue();
        t.Should().Be("BIGINT");
    }

    [Fact]
    public void Handles_datatype_with_comma_inside_parens()
    {
        const string ddl = """
            CREATE TABLE T (
                [price] DECIMAL(18,4) NOT NULL,
                [rate]  NUMERIC(5,2) NULL
            );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TryGetType("T", "price", out var p).Should().BeTrue();
        p.Should().Be("DECIMAL(18,4)");
        map.TryGetType("T", "rate", out var r).Should().BeTrue();
        r.Should().Be("NUMERIC(5,2)");
    }

    [Fact]
    public void Skips_table_level_constraints()
    {
        const string ddl = """
            CREATE TABLE T (
                id INT NOT NULL,
                code VARCHAR(10) NOT NULL,
                PRIMARY KEY (id),
                CONSTRAINT UQ_T UNIQUE (code)
            );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TryGetType("T", "id", out _).Should().BeTrue();
        // table-level PK/UNIQUE lines must not register as a column
        map.TryGetType("T", "PRIMARY", out _).Should().BeFalse();
        map.TryGetType("T", "CONSTRAINT", out _).Should().BeFalse();
    }

    [Fact]
    public void Multiple_CREATE_TABLE_statements_are_all_parsed()
    {
        const string ddl = """
            CREATE TABLE A ( x INT );
            CREATE TABLE B ( y VARCHAR(50) );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TableCount.Should().Be(2);
    }

    [Fact]
    public void Ignores_non_CREATE_TABLE_noise()
    {
        const string ddl = """
            -- some comment
            GO
            CREATE VIEW V AS SELECT 1;
            CREATE TABLE T ( c INT );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TableCount.Should().Be(1);
        map.TryGetType("T", "c", out _).Should().BeTrue();
    }
}
