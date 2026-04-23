using EliteSoft.Erwin.AlterDdl.Core.Emitting;
using EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;
using EliteSoft.Erwin.AlterDdl.Core.Models;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

public class DialectEmitterTests
{
    private static readonly ObjectRef Customer = new("{E1}+0", "CUSTOMER", "Entity");
    private static readonly ModelMetadata OracleMeta = new("{PU}+0", "t", "Physical", "Oracle", 19, 0);
    private static readonly ModelMetadata Db2Meta = new("{PU}+0", "t", "Physical", "Db2", 12, 0);

    private static CompareResult Result(ModelMetadata meta, params Change[] changes) =>
        new(meta, meta, changes, new CompareArtifact("x.xls", 0, 0));

    // ---------- Oracle ----------

    private readonly OracleEmitter _oracle = new();

    [Fact]
    public void Oracle_EntityRenamed_uses_ALTER_TABLE_RENAME_TO()
    {
        var r = Result(OracleMeta, new EntityRenamed(new("{E}+0", "NEW", "Entity"), "OLD"));
        _oracle.Emit(r).Statements[0].Sql.Should().Be("ALTER TABLE \"OLD\" RENAME TO \"NEW\";");
    }

    [Fact]
    public void Oracle_AttributeRenamed_uses_RENAME_COLUMN()
    {
        var r = Result(OracleMeta, new AttributeRenamed(
            new("{A}+0", "mobile_no", "Attribute"), Customer, OldName: "mobile_phone"));
        _oracle.Emit(r).Statements[0].Sql
            .Should().Be("ALTER TABLE \"CUSTOMER\" RENAME COLUMN \"mobile_phone\" TO \"mobile_no\";");
    }

    [Fact]
    public void Oracle_AttributeTypeChanged_uses_MODIFY()
    {
        var r = Result(OracleMeta, new AttributeTypeChanged(
            new("{A}+0", "order_amount", "Attribute"), Customer,
            LeftType: "NUMBER(10)", RightType: "NUMBER(19)"));
        _oracle.Emit(r).Statements[0].Sql
            .Should().Be("ALTER TABLE \"CUSTOMER\" MODIFY (\"order_amount\" NUMBER(19));");
    }

    [Fact]
    public void Oracle_AttributeAdded_uses_ADD_with_parens()
    {
        var r = Result(OracleMeta, new AttributeAdded(new("{A}+0", "email_verified", "Attribute"), Customer));
        _oracle.Emit(r).Statements[0].Sql
            .Should().StartWith("ALTER TABLE \"CUSTOMER\" ADD (\"email_verified\"")
            .And.EndWith(");");
    }

    [Fact]
    public void Oracle_EntityDropped_uses_CASCADE_CONSTRAINTS()
    {
        var r = Result(OracleMeta, new EntityDropped(new("{E}+0", "PRODUCT_ARCHIVE", "Entity")));
        _oracle.Emit(r).Statements[0].Sql
            .Should().Be("DROP TABLE \"PRODUCT_ARCHIVE\" CASCADE CONSTRAINTS;");
    }

    [Fact]
    public void Oracle_SchemaMoved_emits_CTAS_TODO_marker()
    {
        var r = Result(OracleMeta, new SchemaMoved(
            new("{E}+0", "ORDER_ITEM", "Entity"), OldSchema: "SALES", NewSchema: "OPS"));
        _oracle.Emit(r).Statements[0].Sql.Should().Contain("TODO").And.Contain("CTAS");
    }

    // ---------- Db2 ----------

    private readonly Db2Emitter _db2 = new();

    [Fact]
    public void Db2_EntityRenamed_uses_RENAME_TABLE()
    {
        var r = Result(Db2Meta, new EntityRenamed(new("{E}+0", "NEW", "Entity"), "OLD"));
        _db2.Emit(r).Statements[0].Sql.Should().Be("RENAME TABLE \"OLD\" TO \"NEW\";");
    }

    [Fact]
    public void Db2_AttributeTypeChanged_uses_SET_DATA_TYPE()
    {
        var r = Result(Db2Meta, new AttributeTypeChanged(
            new("{A}+0", "order_amount", "Attribute"), Customer,
            LeftType: "INT", RightType: "BIGINT"));
        _db2.Emit(r).Statements[0].Sql
            .Should().Be("ALTER TABLE \"CUSTOMER\" ALTER COLUMN \"order_amount\" SET DATA TYPE BIGINT;");
    }

    [Fact]
    public void Db2_AttributeAdded_uses_ADD_COLUMN()
    {
        var r = Result(Db2Meta, new AttributeAdded(new("{A}+0", "email_verified", "Attribute"), Customer));
        _db2.Emit(r).Statements[0].Sql
            .Should().StartWith("ALTER TABLE \"CUSTOMER\" ADD COLUMN \"email_verified\"");
    }

    [Fact]
    public void Db2_AttributeRenamed_uses_RENAME_COLUMN()
    {
        var r = Result(Db2Meta, new AttributeRenamed(
            new("{A}+0", "mobile_no", "Attribute"), Customer, "mobile_phone"));
        _db2.Emit(r).Statements[0].Sql
            .Should().Be("ALTER TABLE \"CUSTOMER\" RENAME COLUMN \"mobile_phone\" TO \"mobile_no\";");
    }

    // ---------- Registry ----------

    [Fact]
    public void Registry_resolves_all_three_dialects_with_aliases()
    {
        var reg = new SqlEmitterRegistry()
            .Register(new MssqlEmitter(), "SQL Server", "MSSQL")
            .Register(new OracleEmitter(), "Oracle")
            .Register(new Db2Emitter(), "Db2", "IBM Db2", "DB2");

        reg.Resolve("SQL Server").Should().BeOfType<MssqlEmitter>();
        reg.Resolve("Oracle").Should().BeOfType<OracleEmitter>();
        reg.Resolve("Db2").Should().BeOfType<Db2Emitter>();
        reg.Resolve("IBM Db2").Should().BeOfType<Db2Emitter>();
    }
}
