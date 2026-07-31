using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Self-write suppression (2026-07-30). A Template naming rule writes a column property
/// through SCAPI; the change detector then re-reads that very property on its next pass. Until
/// this fix the add-in's own write looked exactly like a user edit, so the rule chain restarted
/// on its own output: a Template rule targeting Physical_Name re-fired ValidateGlossary and
/// re-opened the Domain Like Glossary picker on the name it had just produced
/// ('TEST_ACKL' -> 'Table2_TEST_ACKL_TEST' -> picker again, live log 2026-07-30 10:20).
///
/// The fix re-baselines the change-detection snapshot in the SAME slot the detector compares,
/// so the chain-initiating user gesture stays the only event. These tests pin the slot mapping
/// (a mis-routed value would silently leave the loop in place) and the re-baseline itself.
/// </summary>
public class SelfWriteSnapshotTests
{
    // --- Slot classification ---

    [Theory]
    [InlineData("Physical_Name")]
    [InlineData("physical_name")]
    [InlineData("PHYSICAL_NAME")]
    public void PhysicalName_maps_to_the_first_class_name_slot(string targetCode)
    {
        ValidationCoordinatorService.ClassifyColumnTargetCode(targetCode, out string udpName)
            .Should().Be(ValidationCoordinatorService.ColumnSnapshotSlot.PhysicalName);
        udpName.Should().BeNull();
    }

    [Theory]
    [InlineData("Physical_Data_Type")]
    [InlineData("physical_data_type")]
    public void PhysicalDataType_maps_to_the_first_class_type_slot(string targetCode)
    {
        ValidationCoordinatorService.ClassifyColumnTargetCode(targetCode, out string udpName)
            .Should().Be(ValidationCoordinatorService.ColumnSnapshotSlot.PhysicalDataType);
        udpName.Should().BeNull();
    }

    [Theory]
    // The SCAPI path is what a Template rule with TARGET_UDP_ID renders to; UdpValues is keyed
    // by the BARE name (CheckAttributeUdpDependencies / BaselineLockedAttributeUdps).
    [InlineData("Attribute.Physical.TABLE_CLASS", "TABLE_CLASS")]
    [InlineData("attribute.physical.Domain", "Domain")]
    [InlineData("Attribute.Physical.A.B", "A.B")]
    public void Udp_path_is_stripped_to_the_bare_name(string targetCode, string expected)
    {
        ValidationCoordinatorService.ClassifyColumnTargetCode(targetCode, out string udpName)
            .Should().Be(ValidationCoordinatorService.ColumnSnapshotSlot.Udp);
        udpName.Should().Be(expected);
    }

    [Theory]
    [InlineData("Definition")]
    [InlineData("Comment")]
    [InlineData("Schema_Ref")]
    // A bare "Attribute.Physical." with nothing after it is not a UDP path - there is no name
    // to key by, so it stays a watched property rather than writing an empty-keyed entry.
    [InlineData("Attribute.Physical.")]
    public void Everything_else_maps_to_the_watched_property_bag(string targetCode)
    {
        ValidationCoordinatorService.ClassifyColumnTargetCode(targetCode, out string udpName)
            .Should().Be(ValidationCoordinatorService.ColumnSnapshotSlot.WatchedProperty);
        udpName.Should().BeNull();
    }

    // Null and "" are asserted inline rather than through [InlineData(null)]: a null literal on a
    // non-nullable theory parameter trips xUnit1012, and nothing here may be suppressed.
    [Fact]
    public void Missing_target_code_classifies_without_throwing()
    {
        ValidationCoordinatorService.ClassifyColumnTargetCode(null, out string fromNull)
            .Should().Be(ValidationCoordinatorService.ColumnSnapshotSlot.WatchedProperty);
        fromNull.Should().BeNull();

        ValidationCoordinatorService.ClassifyColumnTargetCode("", out string fromEmpty)
            .Should().Be(ValidationCoordinatorService.ColumnSnapshotSlot.WatchedProperty);
        fromEmpty.Should().BeNull();
    }

    // --- Re-baseline ---

    /// <summary>
    /// The reported bug, reduced: the glossary picker named the column, the Template rule
    /// rewrote that name, and the snapshot has to follow so the next pass sees no rename.
    /// </summary>
    [Fact]
    public void Template_rename_is_absorbed_so_the_next_pass_sees_no_rename()
    {
        var snapshot = new ValidationCoordinatorService.AttributeValidationSnapshot
        {
            ObjectId = "{E2FD215A}+00000000",
            PhysicalName = "TEST_ACKL",
        };

        ValidationCoordinatorService
            .ApplySelfWriteToSnapshot(snapshot, "Physical_Name", "Table2_TEST_ACKL_TEST")
            .Should().BeTrue();

        snapshot.PhysicalName.Should().Be("Table2_TEST_ACKL_TEST");
    }

    [Fact]
    public void Datatype_write_lands_in_the_datatype_slot()
    {
        var snapshot = new ValidationCoordinatorService.AttributeValidationSnapshot
        {
            PhysicalDataType = "char(18)",
        };

        ValidationCoordinatorService
            .ApplySelfWriteToSnapshot(snapshot, "Physical_Data_Type", "VARCHAR2(100)")
            .Should().BeTrue();

        snapshot.PhysicalDataType.Should().Be("VARCHAR2(100)");
    }

    [Fact]
    public void Udp_write_lands_under_the_bare_name()
    {
        var snapshot = new ValidationCoordinatorService.AttributeValidationSnapshot();

        ValidationCoordinatorService
            .ApplySelfWriteToSnapshot(snapshot, "Attribute.Physical.TPL_TEST_OUTPUT", "ABC")
            .Should().BeTrue();

        snapshot.UdpValues.Should().ContainKey("TPL_TEST_OUTPUT").WhoseValue.Should().Be("ABC");
        // The full path must NOT also be stored: EnforceLockedAttributeUdps and
        // CheckAttributeUdpDependencies both look the value up by bare name.
        snapshot.UdpValues.Should().NotContainKey("Attribute.Physical.TPL_TEST_OUTPUT");
    }

    [Fact]
    public void Watched_property_write_lands_in_the_bag_under_its_own_code()
    {
        var snapshot = new ValidationCoordinatorService.AttributeValidationSnapshot();

        ValidationCoordinatorService
            .ApplySelfWriteToSnapshot(snapshot, "Definition", "Aciklama alani")
            .Should().BeTrue();

        snapshot.WatchedProperties.Should().ContainKey("Definition")
            .WhoseValue.Should().Be("Aciklama alani");
        snapshot.PhysicalName.Should().BeNull();
        snapshot.PhysicalDataType.Should().BeNull();
    }

    /// <summary>
    /// The detector compares a missing/cleared property as "", never null, so the snapshot has
    /// to hold "" as well - otherwise a template that legitimately writes an empty value would
    /// still read back as a change on the next pass.
    /// </summary>
    [Fact]
    public void Null_value_is_recorded_as_empty_not_null()
    {
        var snapshot = new ValidationCoordinatorService.AttributeValidationSnapshot
        {
            PhysicalName = "OLD",
        };

        ValidationCoordinatorService
            .ApplySelfWriteToSnapshot(snapshot, "Physical_Name", null)
            .Should().BeTrue();

        snapshot.PhysicalName.Should().Be("");
    }

    [Fact]
    public void No_target_code_is_a_no_op()
    {
        var fromNull = new ValidationCoordinatorService.AttributeValidationSnapshot
        {
            PhysicalName = "KEEP_ME",
        };
        ValidationCoordinatorService
            .ApplySelfWriteToSnapshot(fromNull, null, "whatever")
            .Should().BeFalse();
        fromNull.PhysicalName.Should().Be("KEEP_ME");
        fromNull.WatchedProperties.Should().BeEmpty();

        var fromEmpty = new ValidationCoordinatorService.AttributeValidationSnapshot
        {
            PhysicalName = "KEEP_ME",
        };
        ValidationCoordinatorService
            .ApplySelfWriteToSnapshot(fromEmpty, "", "whatever")
            .Should().BeFalse();
        fromEmpty.PhysicalName.Should().Be("KEEP_ME");
        fromEmpty.WatchedProperties.Should().BeEmpty();
    }

    [Fact]
    public void No_snapshot_is_a_no_op()
    {
        ValidationCoordinatorService
            .ApplySelfWriteToSnapshot(null, "Physical_Name", "X")
            .Should().BeFalse();
    }

    /// <summary>
    /// Two Template rules can target the same column in one pass; the last write wins, exactly
    /// as the live property does.
    /// </summary>
    [Fact]
    public void Repeated_writes_keep_the_latest_value()
    {
        var snapshot = new ValidationCoordinatorService.AttributeValidationSnapshot();

        ValidationCoordinatorService.ApplySelfWriteToSnapshot(snapshot, "Physical_Name", "A");
        ValidationCoordinatorService.ApplySelfWriteToSnapshot(snapshot, "Physical_Name", "B_A");

        snapshot.PhysicalName.Should().Be("B_A");
    }
}
