using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

public class ErwinXmlObjectIdMapperTests
{
    private const string SampleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <erwin xmlns="http://www.erwin.com/dm" FileVersion="10.10.38485" Format="erwin">
          <Entity_Groups id="{GGGG1111-0000-0000-0000-000000000001}+00000000" name="all-entities">
            <Entity id="{E0000000-0000-0000-0000-000000000001}+00000000" name="CUSTOMER">
              <Attribute id="{A0000000-0000-0000-0000-000000000001}+00000000" name="customer_id"/>
              <Attribute id="{A0000000-0000-0000-0000-000000000002}+00000000" name="email"/>
            </Entity>
            <Entity id="{E0000000-0000-0000-0000-000000000002}+00000000" name="ORDERS">
              <Attribute id="{A0000000-0000-0000-0000-000000000003}+00000000" name="order_id"/>
              <Attribute id="{A0000000-0000-0000-0000-000000000004}+00000000" name="customer_id"/>
            </Entity>
          </Entity_Groups>
        </erwin>
        """;

    [Fact]
    public void ParseXml_returns_total_object_count()
    {
        var map = ErwinXmlObjectIdMapper.ParseXml(SampleXml);
        // 1 Entity_Groups + 2 Entity + 4 Attribute
        map.TotalObjectCount.Should().Be(7);
    }

    [Fact]
    public void TryGetId_by_class_and_name_finds_entity()
    {
        var map = ErwinXmlObjectIdMapper.ParseXml(SampleXml);
        map.TryGetId("Entity", "CUSTOMER", out var id).Should().BeTrue();
        id.Should().Be("{E0000000-0000-0000-0000-000000000001}+00000000");
    }

    [Fact]
    public void TryGetId_returns_false_for_unknown_name()
    {
        var map = ErwinXmlObjectIdMapper.ParseXml(SampleXml);
        map.TryGetId("Entity", "NONE", out var id).Should().BeFalse();
        id.Should().BeEmpty();
    }

    [Fact]
    public void TryGetAttributeId_uses_parent_scope()
    {
        // customer_id is a column on both CUSTOMER and ORDERS with different UIDs.
        var map = ErwinXmlObjectIdMapper.ParseXml(SampleXml);

        map.TryGetAttributeId("CUSTOMER", "customer_id", out var custId).Should().BeTrue();
        custId.Should().Be("{A0000000-0000-0000-0000-000000000001}+00000000");

        map.TryGetAttributeId("ORDERS", "customer_id", out var ordId).Should().BeTrue();
        ordId.Should().Be("{A0000000-0000-0000-0000-000000000004}+00000000");

        custId.Should().NotBe(ordId);
    }

    [Fact]
    public void ObjectsOfClass_returns_entities_only()
    {
        var map = ErwinXmlObjectIdMapper.ParseXml(SampleXml);
        var entities = map.ObjectsOfClass("Entity").ToList();
        entities.Should().HaveCount(2);
        entities.Select(e => e.Name).Should().BeEquivalentTo(["CUSTOMER", "ORDERS"]);
    }

    [Fact]
    public void TryGetById_returns_objectref_with_parent()
    {
        var map = ErwinXmlObjectIdMapper.ParseXml(SampleXml);
        var customerAttrId = "{A0000000-0000-0000-0000-000000000001}+00000000";
        map.TryGetById(customerAttrId, out var attr).Should().BeTrue();
        attr.Name.Should().Be("customer_id");
        attr.Class.Should().Be("Attribute");
        attr.ParentObjectId.Should().Be("{E0000000-0000-0000-0000-000000000001}+00000000");
    }

    [Fact]
    public void Duplicate_id_keeps_first_occurrence()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{X}+0" name="FIRST"/>
              <Entity id="{X}+0" name="DUPLICATE"/>
            </erwin>
            """;
        var map = ErwinXmlObjectIdMapper.ParseXml(xml);
        map.TryGetById("{X}+0", out var obj).Should().BeTrue();
        obj.Name.Should().Be("FIRST");
    }
}
