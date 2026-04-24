using EliteSoft.Erwin.AlterDdl.Core.Correlation;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

public class ChangeCorrelatorTests
{
    private const string V1Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <erwin xmlns="http://www.erwin.com/dm">
          <Entity_Groups id="{G1}+0" name="g">
            <Entity id="{E1}+0" name="CUSTOMER">
              <Attribute id="{A1}+0" name="customer_id"/>
              <Attribute id="{A2}+0" name="mobile_phone"/>
              <Attribute id="{A3}+0" name="fax_number"/>
            </Entity>
            <Entity id="{E2}+0" name="PRODUCT_ARCHIVE">
              <Attribute id="{A4}+0" name="product_id"/>
            </Entity>
            <Entity id="{E3}+0" name="CUSTOMER_BACKUP">
              <Attribute id="{A5}+0" name="customer_id"/>
            </Entity>
          </Entity_Groups>
        </erwin>
        """;

    private const string V2Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <erwin xmlns="http://www.erwin.com/dm">
          <Entity_Groups id="{G1}+0" name="g">
            <Entity id="{E1}+0" name="CUSTOMER">
              <Attribute id="{A1}+0" name="customer_id"/>
              <Attribute id="{A2}+0" name="mobile_no"/>
              <Attribute id="{A99}+0" name="email_verified"/>
            </Entity>
            <Entity id="{E3}+0" name="CUSTOMER_HISTORY">
              <Attribute id="{A5}+0" name="customer_id"/>
            </Entity>
            <Entity id="{E4}+0" name="CAMPAIGN">
              <Attribute id="{A10}+0" name="campaign_id"/>
            </Entity>
          </Entity_Groups>
        </erwin>
        """;

    private static readonly XlsDiffRow[] XlsRowsWithTypeChange =
    [
        new(2, "Entity/Table", "CUSTOMER", "Equal", "CUSTOMER"),
        new(3, "Attribute/Column", "mobile_no", "Not Equal", "mobile_no"),
        new(4, "Physical Data Type", "varchar(100)", "Not Equal", "varchar(250)"),
    ];

    /// <summary>
    /// erwin CC XLS sometimes renders the Entity/Table row with its owner
    /// schema prefixed (e.g. "app.CUSTOMER") while the XML model map stores
    /// only the bare name ("CUSTOMER"). The correlator must resolve past
    /// that mismatch and still wire up the real ObjectIds instead of falling
    /// back to synthetic <c>(xls-only):...</c> references.
    /// </summary>
    private static readonly XlsDiffRow[] XlsRowsWithSchemaQualifiedEntity =
    [
        new(2, "Entity/Table", "app.CUSTOMER", "Equal", "app.CUSTOMER"),
        new(3, "Attribute/Column", "mobile_no", "Not Equal", "mobile_no"),
        new(4, "Physical Data Type", "varchar(100)", "Not Equal", "varchar(250)"),
    ];

    private ErwinModelMap Left => ErwinXmlObjectIdMapper.ParseXml(V1Xml);
    private ErwinModelMap Right => ErwinXmlObjectIdMapper.ParseXml(V2Xml);

    [Fact]
    public void Correlate_resolves_type_change_when_xls_entity_is_schema_qualified()
    {
        var changes = ChangeCorrelator.Correlate(Left, Right, XlsRowsWithSchemaQualifiedEntity);
        var typeChange = changes.OfType<AttributeTypeChanged>().Should().ContainSingle().Subject;
        typeChange.Target.ObjectId.Should().Be("{A2}+0");
        typeChange.ParentEntity.ObjectId.Should().Be("{E1}+0");
        typeChange.ParentEntity.Name.Should().Be("CUSTOMER");
        typeChange.LeftType.Should().Be("varchar(100)");
        typeChange.RightType.Should().Be("varchar(250)");
    }

    [Fact]
    public void Correlate_detects_entity_added()
    {
        var changes = ChangeCorrelator.Correlate(Left, Right, []);
        changes.OfType<EntityAdded>().Select(c => c.Target.Name).Should().Contain("CAMPAIGN");
    }

    [Fact]
    public void Correlate_detects_entity_dropped()
    {
        var changes = ChangeCorrelator.Correlate(Left, Right, []);
        changes.OfType<EntityDropped>().Select(c => c.Target.Name).Should().Contain("PRODUCT_ARCHIVE");
    }

    [Fact]
    public void Correlate_detects_entity_renamed_with_preserved_objectid()
    {
        var changes = ChangeCorrelator.Correlate(Left, Right, []);
        var renamed = changes.OfType<EntityRenamed>().Single();
        renamed.Target.Name.Should().Be("CUSTOMER_HISTORY");
        renamed.OldName.Should().Be("CUSTOMER_BACKUP");
        renamed.Target.ObjectId.Should().Be("{E3}+0");
    }

    [Fact]
    public void Correlate_detects_attribute_added_with_parent()
    {
        var changes = ChangeCorrelator.Correlate(Left, Right, []);
        var added = changes.OfType<AttributeAdded>().ToList();
        added.Should().Contain(c => c.Target.Name == "email_verified" && c.ParentEntity.Name == "CUSTOMER");
        added.Should().Contain(c => c.Target.Name == "campaign_id" && c.ParentEntity.Name == "CAMPAIGN");
    }

    [Fact]
    public void Correlate_detects_attribute_dropped()
    {
        var changes = ChangeCorrelator.Correlate(Left, Right, []);
        var dropped = changes.OfType<AttributeDropped>().Select(c => c.Target.Name);
        dropped.Should().Contain(["fax_number", "product_id"]);
    }

    [Fact]
    public void Correlate_detects_attribute_renamed_via_same_objectid()
    {
        var changes = ChangeCorrelator.Correlate(Left, Right, []);
        var rename = changes.OfType<AttributeRenamed>().Single();
        rename.Target.Name.Should().Be("mobile_no");
        rename.OldName.Should().Be("mobile_phone");
        rename.ParentEntity.Name.Should().Be("CUSTOMER");
        rename.Target.ObjectId.Should().Be("{A2}+0");
    }

    [Fact]
    public void Correlate_detects_type_change_from_xls_walk()
    {
        var changes = ChangeCorrelator.Correlate(Left, Right, XlsRowsWithTypeChange);
        var typeChange = changes.OfType<AttributeTypeChanged>().Single();
        typeChange.Target.Name.Should().Be("mobile_no");
        typeChange.ParentEntity.Name.Should().Be("CUSTOMER");
        typeChange.LeftType.Should().Be("varchar(100)");
        typeChange.RightType.Should().Be("varchar(250)");
    }

    [Fact]
    public void Correlate_output_is_deterministic()
    {
        var first = ChangeCorrelator.Correlate(Left, Right, XlsRowsWithTypeChange);
        var second = ChangeCorrelator.Correlate(Left, Right, XlsRowsWithTypeChange);
        first.Select(c => (c.GetType().Name, c.Target.ObjectId))
            .Should().BeEquivalentTo(second.Select(c => (c.GetType().Name, c.Target.ObjectId)),
                o => o.WithStrictOrdering());
    }

    [Fact]
    public void Identical_models_produce_no_changes()
    {
        var map = ErwinXmlObjectIdMapper.ParseXml(V1Xml);
        var changes = ChangeCorrelator.Correlate(map, map, []);
        changes.Should().BeEmpty();
    }
}
