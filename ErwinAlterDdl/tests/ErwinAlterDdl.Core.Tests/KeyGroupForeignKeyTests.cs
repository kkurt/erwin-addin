using EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;
using EliteSoft.Erwin.AlterDdl.Core.Models;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

public class KeyGroupForeignKeyTests
{
    private static readonly ObjectRef Customer = new("{E1}+0", "CUSTOMER", "Entity");
    private static readonly ObjectRef PkCustomer = new("{K1}+0", "XPKCUSTOMER", "Key_Group");
    private static readonly ObjectRef UqEmail = new("{K2}+0", "XAKCUSTOMER_EMAIL", "Key_Group");
    private static readonly ObjectRef IxEmail = new("{K3}+0", "IX_CUSTOMER_EMAIL", "Key_Group");
    private static readonly ObjectRef FkOrders = new("{R1}+0", "FK_ORDERS_CUSTOMER", "Relationship");

    private static readonly ModelMetadata MssqlMeta = new("{PU}+0", "t", "Physical", "SQL Server", 15, 0);

    private static CompareResult Result(params Change[] changes) =>
        new(MssqlMeta, MssqlMeta, changes, new CompareArtifact("x.xls", 0, 0));

    [Fact]
    public void Mssql_PK_add_emits_ALTER_TABLE_ADD_CONSTRAINT_PRIMARY_KEY()
    {
        var sql = new MssqlEmitter().Emit(Result(new KeyGroupAdded(PkCustomer, Customer, KeyGroupKind.PrimaryKey)))
            .Statements[0].Sql;
        sql.Should().Contain("ALTER TABLE [CUSTOMER] ADD CONSTRAINT [XPKCUSTOMER] PRIMARY KEY");
    }

    [Fact]
    public void Mssql_UQ_drop_emits_DROP_CONSTRAINT()
    {
        var sql = new MssqlEmitter().Emit(Result(new KeyGroupDropped(UqEmail, Customer, KeyGroupKind.UniqueConstraint)))
            .Statements[0].Sql;
        sql.Should().Be("ALTER TABLE [CUSTOMER] DROP CONSTRAINT [XAKCUSTOMER_EMAIL];");
    }

    [Fact]
    public void Mssql_Index_add_emits_CREATE_INDEX()
    {
        var sql = new MssqlEmitter().Emit(Result(new KeyGroupAdded(IxEmail, Customer, KeyGroupKind.Index)))
            .Statements[0].Sql;
        sql.Should().StartWith("CREATE INDEX [IX_CUSTOMER_EMAIL] ON [CUSTOMER]");
    }

    [Fact]
    public void Mssql_Index_drop_emits_DROP_INDEX_ON()
    {
        var sql = new MssqlEmitter().Emit(Result(new KeyGroupDropped(IxEmail, Customer, KeyGroupKind.Index)))
            .Statements[0].Sql;
        sql.Should().Be("DROP INDEX [IX_CUSTOMER_EMAIL] ON [CUSTOMER];");
    }

    [Fact]
    public void Mssql_ForeignKey_add_emits_TODO_marker()
    {
        var sql = new MssqlEmitter().Emit(Result(new ForeignKeyAdded(FkOrders))).Statements[0].Sql;
        sql.Should().Contain("TODO").And.Contain("FOREIGN KEY").And.Contain("FK_ORDERS_CUSTOMER");
    }

    // ---------- Oracle ----------

    [Fact]
    public void Oracle_PK_add_emits_quoted_ALTER_TABLE_ADD_CONSTRAINT()
    {
        var sql = new OracleEmitter().Emit(Result(new KeyGroupAdded(PkCustomer, Customer, KeyGroupKind.PrimaryKey)))
            .Statements[0].Sql;
        sql.Should().StartWith("ALTER TABLE \"CUSTOMER\" ADD CONSTRAINT \"XPKCUSTOMER\" PRIMARY KEY");
    }

    [Fact]
    public void Oracle_Index_rename_emits_ALTER_INDEX_RENAME_TO()
    {
        var ix2 = new ObjectRef(IxEmail.ObjectId, "IX_CUSTOMER_EMAIL_V2", "Key_Group");
        var sql = new OracleEmitter().Emit(Result(new KeyGroupRenamed(
            ix2, Customer, OldName: "IX_CUSTOMER_EMAIL", Kind: KeyGroupKind.Index))).Statements[0].Sql;
        sql.Should().Be("ALTER INDEX \"IX_CUSTOMER_EMAIL\" RENAME TO \"IX_CUSTOMER_EMAIL_V2\";");
    }

    // ---------- Db2 ----------

    [Fact]
    public void Db2_PK_drop_uses_DROP_PRIMARY_KEY()
    {
        var sql = new Db2Emitter().Emit(Result(new KeyGroupDropped(PkCustomer, Customer, KeyGroupKind.PrimaryKey)))
            .Statements[0].Sql;
        sql.Should().Be("ALTER TABLE \"CUSTOMER\" DROP PRIMARY KEY;");
    }

    [Fact]
    public void Db2_Index_rename_uses_RENAME_INDEX()
    {
        var ix2 = new ObjectRef(IxEmail.ObjectId, "IX_CUSTOMER_EMAIL_V2", "Key_Group");
        var sql = new Db2Emitter().Emit(Result(new KeyGroupRenamed(
            ix2, Customer, OldName: "IX_CUSTOMER_EMAIL", Kind: KeyGroupKind.Index))).Statements[0].Sql;
        sql.Should().Be("RENAME INDEX \"IX_CUSTOMER_EMAIL\" TO \"IX_CUSTOMER_EMAIL_V2\";");
    }
}
