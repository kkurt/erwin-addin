using EliteSoft.Erwin.AlterDdl.Core.Emitting;
using EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;
using EliteSoft.Erwin.AlterDdl.Core.Models;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

public class MssqlEmitterTests
{
    private static readonly ObjectRef Customer = new("{E1}+0", "CUSTOMER", "Entity");
    private static readonly ObjectRef Orders = new("{E2}+0", "ORDERS", "Entity");

    private static readonly ModelMetadata Meta = new(
        PersistenceUnitId: "{PU}+0",
        Name: "test",
        ModelType: "Physical",
        TargetServer: "SQL Server",
        TargetServerVersion: 15,
        TargetServerMinorVersion: 0);

    private static CompareResult Result(params Change[] changes) =>
        new(Meta, Meta, changes, new CompareArtifact("x.xls", 0, 0));

    private readonly MssqlEmitter _emitter = new();

    [Fact]
    public void EntityAdded_emits_TODO_placeholder()
    {
        var r = Result(new EntityAdded(new("{E3}+0", "CAMPAIGN", "Entity")));
        var script = _emitter.Emit(r);
        script.Statements.Should().ContainSingle()
            .Which.Sql.Should().Contain("[CAMPAIGN]").And.Contain("TODO");
    }

    [Fact]
    public void EntityDropped_emits_drop_table()
    {
        var r = Result(new EntityDropped(new("{E3}+0", "PRODUCT_ARCHIVE", "Entity")));
        var sql = _emitter.Emit(r).Statements[0].Sql;
        sql.Should().Be("DROP TABLE [PRODUCT_ARCHIVE];");
    }

    [Fact]
    public void EntityRenamed_emits_sp_rename()
    {
        var r = Result(new EntityRenamed(
            new("{E3}+0", "CUSTOMER_HISTORY", "Entity"),
            OldName: "CUSTOMER_BACKUP"));
        var sql = _emitter.Emit(r).Statements[0].Sql;
        sql.Should().Be("EXEC sp_rename 'CUSTOMER_BACKUP', 'CUSTOMER_HISTORY';");
    }

    [Fact]
    public void SchemaMoved_emits_transfer()
    {
        var r = Result(new SchemaMoved(
            new("{E3}+0", "ORDER_ITEM", "Entity"), OldSchema: "sales", NewSchema: "ops"));
        var sql = _emitter.Emit(r).Statements[0].Sql;
        sql.Should().Be("ALTER SCHEMA [ops] TRANSFER [sales].[ORDER_ITEM];");
    }

    [Fact]
    public void AttributeAdded_emits_add_column_with_todo_when_no_ddl_available()
    {
        var r = Result(new AttributeAdded(new("{A99}+0", "email_verified", "Attribute"), Customer));
        var sql = _emitter.Emit(r).Statements[0].Sql;
        sql.Should().StartWith("ALTER TABLE [CUSTOMER] ADD [email_verified]").And.Contain("TODO");
    }

    [Fact]
    public void AttributeAdded_resolves_datatype_from_right_create_ddl()
    {
        // Prepare a v2 CREATE DDL that defines the new column's type, and
        // point CompareResult.RightDdl at it.
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
                CREATE TABLE [app].[CUSTOMER] (
                    [customer_id] INT NOT NULL,
                    [email_verified] BIT NOT NULL DEFAULT 0
                );
                """);
            var r = Result(new AttributeAdded(new("{A99}+0", "email_verified", "Attribute"), Customer)) with
            {
                RightDdl = new DdlArtifact(tmp, new FileInfo(tmp).Length, "SQL Server"),
            };
            var sql = _emitter.Emit(r).Statements[0].Sql;
            // Schema "[app]" is recovered from the CREATE TABLE header so
            // the emitter renders [schema].[table] - matches erwin's own
            // CompleteCompare output.
            sql.Should().Be("ALTER TABLE [app].[CUSTOMER] ADD [email_verified] BIT;");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void AttributeDropped_emits_drop_column()
    {
        var r = Result(new AttributeDropped(new("{A7}+0", "fax_number", "Attribute"), Customer));
        var sql = _emitter.Emit(r).Statements[0].Sql;
        sql.Should().Be("ALTER TABLE [CUSTOMER] DROP COLUMN [fax_number];");
    }

    [Fact]
    public void AttributeRenamed_emits_sp_rename_COLUMN()
    {
        var r = Result(new AttributeRenamed(
            new("{A2}+0", "mobile_no", "Attribute"), Customer, OldName: "mobile_phone"));
        var sql = _emitter.Emit(r).Statements[0].Sql;
        sql.Should().Be("EXEC sp_rename 'CUSTOMER.mobile_phone', 'mobile_no', 'COLUMN';");
    }

    [Fact]
    public void AttributeTypeChanged_emits_alter_column()
    {
        var r = Result(new AttributeTypeChanged(
            new("{A5}+0", "address", "Attribute"), Customer,
            LeftType: "varchar(100)", RightType: "varchar(250)"));
        var sql = _emitter.Emit(r).Statements[0].Sql;
        sql.Should().Be("ALTER TABLE [CUSTOMER] ALTER COLUMN [address] varchar(250);");
    }

    [Fact]
    public void ToScript_includes_header_and_GO_separator()
    {
        var r = Result(new EntityDropped(new("{E3}+0", "TEMP", "Entity")));
        var text = _emitter.Emit(r).ToScript();
        text.Should().Contain("-- ALTER DDL (MSSQL)");
        text.Should().Contain("DROP TABLE [TEMP];");
        text.Should().Contain("GO");
    }

    [Fact]
    public void Unknown_target_server_is_rejected_by_registry()
    {
        var reg = new SqlEmitterRegistry().Register(new MssqlEmitter(), "SQL Server");
        reg.Resolve("SQL Server").Should().BeOfType<MssqlEmitter>();
        reg.Resolve("MSSQL").Should().BeOfType<MssqlEmitter>();
        Action bad = () => reg.Resolve("PostgreSQL");
        bad.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Identifier_with_bracket_is_escaped()
    {
        var r = Result(new EntityDropped(new("{E3}+0", "weird]name", "Entity")));
        _emitter.Emit(r).Statements[0].Sql.Should().Be("DROP TABLE [weird]]name];");
    }
}
