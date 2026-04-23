using EliteSoft.Erwin.AlterDdl.Core.Correlation;
using EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

/// <summary>
/// Covers the Phase 3.D polish work:
///   1. KeyGroup dedup: (parent, name) identity churn stays silent.
///   2. DdlColumnMap PK / UQ / Index / FK column lookups.
///   3. Emitters fill in concrete column lists + split schema-qualified names.
/// </summary>
public class PolishTests
{
    private static readonly ModelMetadata Meta = new(
        PersistenceUnitId: "{PU}+0",
        Name: "t",
        ModelType: "Physical",
        TargetServer: "SQL Server",
        TargetServerVersion: 15,
        TargetServerMinorVersion: 0);

    private static CompareResult Result(Change[] changes, DdlArtifact? rightDdl = null) =>
        new(Meta, Meta, changes, new CompareArtifact("x.xls", 0, 0)) { RightDdl = rightDdl };

    // -------------------- 1. KeyGroup dedup --------------------

    [Fact]
    public void KeyGroup_dedup_skips_identity_churn_when_parent_and_name_match()
    {
        const string leftXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity_Groups id="{G1}+0" name="g">
                <Entity id="{E1}+0" name="CUSTOMER">
                  <Key_Group id="{K_OLD}+0" name="XPKCUSTOMER"/>
                </Entity>
              </Entity_Groups>
            </erwin>
            """;
        const string rightXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity_Groups id="{G1}+0" name="g">
                <Entity id="{E1}+0" name="CUSTOMER">
                  <Key_Group id="{K_NEW}+0" name="XPKCUSTOMER"/>
                </Entity>
              </Entity_Groups>
            </erwin>
            """;
        var left = ErwinXmlObjectIdMapper.ParseXml(leftXml);
        var right = ErwinXmlObjectIdMapper.ParseXml(rightXml);

        var changes = ChangeCorrelator.Correlate(left, right, []);

        changes.OfType<KeyGroupAdded>().Should().BeEmpty();
        changes.OfType<KeyGroupDropped>().Should().BeEmpty();
    }

    [Fact]
    public void KeyGroup_without_a_name_twin_still_emits_add_and_drop()
    {
        const string leftXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity_Groups id="{G1}+0" name="g">
                <Entity id="{E1}+0" name="CUSTOMER">
                  <Key_Group id="{K1}+0" name="XIE_OLD_INDEX"/>
                </Entity>
              </Entity_Groups>
            </erwin>
            """;
        const string rightXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity_Groups id="{G1}+0" name="g">
                <Entity id="{E1}+0" name="CUSTOMER">
                  <Key_Group id="{K2}+0" name="XIE_NEW_INDEX"/>
                </Entity>
              </Entity_Groups>
            </erwin>
            """;
        var left = ErwinXmlObjectIdMapper.ParseXml(leftXml);
        var right = ErwinXmlObjectIdMapper.ParseXml(rightXml);

        var changes = ChangeCorrelator.Correlate(left, right, []);
        changes.OfType<KeyGroupDropped>().Should().ContainSingle().Which.Target.Name.Should().Be("XIE_OLD_INDEX");
        changes.OfType<KeyGroupAdded>().Should().ContainSingle().Which.Target.Name.Should().Be("XIE_NEW_INDEX");
    }

    // -------------------- 2. DdlColumnMap extensions --------------------

    [Fact]
    public void Parser_captures_inline_PRIMARY_KEY_constraint_columns()
    {
        const string ddl = """
            CREATE TABLE [CUSTOMER] (
                [customer_id] INT NOT NULL,
                [order_no] INT NOT NULL,
                CONSTRAINT XPKCUSTOMER PRIMARY KEY ([customer_id], [order_no])
            );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TryGetKeyGroupColumns("XPKCUSTOMER", out var cols).Should().BeTrue();
        cols.Should().Equal("customer_id", "order_no");
    }

    [Fact]
    public void Parser_captures_standalone_CREATE_INDEX_columns()
    {
        const string ddl = """
            CREATE INDEX XIE_CUSTOMER_EMAIL ON app.CUSTOMER (email, created_at);
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TryGetKeyGroupColumns("XIE_CUSTOMER_EMAIL", out var cols).Should().BeTrue();
        cols.Should().Equal("email", "created_at");
    }

    [Fact]
    public void Parser_captures_standalone_ADD_CONSTRAINT_FOREIGN_KEY()
    {
        const string ddl = """
            ALTER TABLE ORDERS ADD CONSTRAINT R_1 FOREIGN KEY (CUST_ID)
              REFERENCES CUSTOMER (CUST_ID);
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TryGetForeignKey("R_1", out var fk).Should().BeTrue();
        fk.ChildTable.Should().Be("ORDERS");
        fk.ChildColumns.Should().Equal("CUST_ID");
        fk.ParentTable.Should().Be("CUSTOMER");
        fk.ParentColumns.Should().Equal("CUST_ID");
    }

    [Fact]
    public void Parser_handles_inline_FK_inside_CREATE_TABLE()
    {
        const string ddl = """
            CREATE TABLE ORDERS (
                ORDER_ID INT NOT NULL,
                CUST_ID  INT NOT NULL,
                CONSTRAINT R_2 FOREIGN KEY (CUST_ID) REFERENCES CUSTOMER (CUST_ID)
            );
            """;
        var map = CreateDdlParser.Parse(ddl);
        map.TryGetForeignKey("R_2", out var fk).Should().BeTrue();
        fk.ChildTable.Should().Be("ORDERS");
        fk.ChildColumns.Should().Equal("CUST_ID");
        fk.ParentTable.Should().Be("CUSTOMER");
        fk.ParentColumns.Should().Equal("CUST_ID");
    }

    // -------------------- 3. Emitter integration --------------------

    [Fact]
    public void Mssql_PK_add_fills_in_columns_from_ddl()
    {
        using var ddlFile = WriteTempDdl("""
            CREATE TABLE [CUSTOMER] (
                [cust_id] INT NOT NULL,
                CONSTRAINT XPKCUSTOMER PRIMARY KEY ([cust_id])
            );
            """);
        var emitter = new MssqlEmitter();
        var parent = new ObjectRef("{E1}+0", "CUSTOMER", "Entity");
        var kg = new ObjectRef("{K1}+0", "XPKCUSTOMER", "Key_Group");
        var changes = new Change[] { new KeyGroupAdded(kg, parent, KeyGroupKind.PrimaryKey) };
        var script = emitter.Emit(Result(changes, new DdlArtifact(ddlFile.Path, 0, "SQL Server")));

        script.Statements[0].Sql.Should().Be(
            "ALTER TABLE [CUSTOMER] ADD CONSTRAINT [XPKCUSTOMER] PRIMARY KEY ([cust_id]);");
    }

    [Fact]
    public void Mssql_FK_add_fills_in_child_parent_columns_from_ddl()
    {
        using var ddlFile = WriteTempDdl("""
            ALTER TABLE ORDERS ADD CONSTRAINT R_1 FOREIGN KEY (CUST_ID)
              REFERENCES CUSTOMER (CUST_ID);
            """);
        var emitter = new MssqlEmitter();
        var fk = new ObjectRef("{R1}+0", "R_1", "Relationship");
        var changes = new Change[] { new ForeignKeyAdded(fk) };
        var script = emitter.Emit(Result(changes, new DdlArtifact(ddlFile.Path, 0, "SQL Server")));

        script.Statements[0].Sql.Should().Be(
            "ALTER TABLE [ORDERS] ADD CONSTRAINT [R_1] FOREIGN KEY ([CUST_ID]) REFERENCES [CUSTOMER] ([CUST_ID]);");
    }

    [Fact]
    public void Mssql_schema_qualified_entity_splits_quotes()
    {
        var entity = new ObjectRef("{E1}+0", "sales.ORDER_ITEM", "Entity");
        var script = new MssqlEmitter().Emit(Result(new Change[] { new EntityDropped(entity) }));
        script.Statements[0].Sql.Should().Be("DROP TABLE [sales].[ORDER_ITEM];");
    }

    [Fact]
    public void Oracle_schema_qualified_entity_splits_quotes()
    {
        var entity = new ObjectRef("{E1}+0", "APP.CUSTOMER", "Entity");
        var script = new OracleEmitter().Emit(Result(new Change[] { new EntityDropped(entity) }));
        script.Statements[0].Sql.Should().Be("DROP TABLE \"APP\".\"CUSTOMER\" CASCADE CONSTRAINTS;");
    }

    [Fact]
    public void Db2_schema_qualified_entity_splits_quotes()
    {
        var entity = new ObjectRef("{E1}+0", "APP.CUSTOMER", "Entity");
        var script = new Db2Emitter().Emit(Result(new Change[] { new EntityDropped(entity) }));
        script.Statements[0].Sql.Should().Be("DROP TABLE \"APP\".\"CUSTOMER\";");
    }

    [Fact]
    public void Oracle_Index_add_fills_columns_from_CREATE_INDEX()
    {
        using var ddlFile = WriteTempDdl("""
            CREATE INDEX XIE_CUSTOMER_EMAIL ON CUSTOMER (email, created_at);
            """);
        var emitter = new OracleEmitter();
        var parent = new ObjectRef("{E1}+0", "CUSTOMER", "Entity");
        var kg = new ObjectRef("{K1}+0", "XIE_CUSTOMER_EMAIL", "Key_Group");
        var script = emitter.Emit(Result(
            new Change[] { new KeyGroupAdded(kg, parent, KeyGroupKind.Index) },
            new DdlArtifact(ddlFile.Path, 0, "Oracle")));

        script.Statements[0].Sql.Should().Be(
            "CREATE INDEX \"XIE_CUSTOMER_EMAIL\" ON \"CUSTOMER\" (\"email\", \"created_at\");");
    }

    [Fact]
    public void Mssql_AttributeAdded_looks_up_type_even_when_parent_is_schema_qualified()
    {
        using var ddlFile = WriteTempDdl("""
            CREATE TABLE [app].[CUSTOMER] (
                [cust_id] INT NOT NULL,
                [full_name] VARCHAR(150) NOT NULL
            );
            """);
        var parent = new ObjectRef("{E1}+0", "app.CUSTOMER", "Entity");
        var attr = new ObjectRef("{A99}+0", "full_name", "Attribute");
        var script = new MssqlEmitter().Emit(Result(
            new Change[] { new AttributeAdded(attr, parent) },
            new DdlArtifact(ddlFile.Path, 0, "SQL Server")));

        script.Statements[0].Sql.Should().Be(
            "ALTER TABLE [app].[CUSTOMER] ADD [full_name] VARCHAR(150);");
    }

    // -------------------- helpers --------------------

    private sealed class TempDdl : IDisposable
    {
        public string Path { get; }
        public TempDdl(string content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"polish_{Guid.NewGuid():N}.sql");
            File.WriteAllText(Path, content);
        }
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { /* test cleanup */ }
        }
    }

    private static TempDdl WriteTempDdl(string content) => new(content);
}
