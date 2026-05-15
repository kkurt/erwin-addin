using System.Collections.Generic;
using System.Linq;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the pure diff algorithm in
/// <see cref="UdpSyncEngine.ComputeDiff"/>. No SCAPI, no DB - the fixtures are
/// plain DTOs so each branch of the diff machine is exercised in isolation.
/// </summary>
public class UdpSyncEngineDiffTests
{
    private static UdpDefinitionSnapshot AdminUdp(
        int id,
        string name,
        string objectType = "Table",
        string udpType = "Text",
        string defaultValue = "",
        string description = "",
        bool isDeleted = false,
        params string[] listOptions)
    {
        var snap = new UdpDefinitionSnapshot
        {
            Id = id,
            Name = name,
            ObjectType = objectType,
            UdpType = udpType,
            DefaultValue = defaultValue,
            Description = description,
            IsDeleted = isDeleted,
        };
        for (int i = 0; i < listOptions.Length; i++)
        {
            snap.ListOptions.Add(new UdpListOptionSnapshot
            {
                Value = listOptions[i],
                DisplayText = listOptions[i],
                SortOrder = i,
            });
        }
        return snap;
    }

    private static ModelUdpSnapshot ModelUdp(
        string fullName,
        int dataTypeId = 2,
        string currentDefault = "",
        string currentListValues = "",
        string currentDescription = "")
    {
        int last = fullName.LastIndexOf('.');
        return new ModelUdpSnapshot
        {
            FullName = fullName,
            OwnerClass = fullName.Split('.')[0],
            UdpName = last >= 0 ? fullName.Substring(last + 1) : fullName,
            CurrentDataTypeId = dataTypeId,
            CurrentDefault = currentDefault,
            CurrentListValues = currentListValues,
            CurrentDescription = currentDescription,
        };
    }

    private static Dictionary<string, ModelUdpSnapshot> AsMap(params ModelUdpSnapshot[] entries)
        => entries.ToDictionary(e => e.FullName, e => e);

    [Fact]
    public void Empty_snapshot_yields_empty_diff()
    {
        var diff = UdpSyncEngine.ComputeDiff(
            new List<UdpDefinitionSnapshot>(),
            AsMap());

        diff.IsEmpty.Should().BeTrue();
        diff.TotalCount.Should().Be(0);
    }

    [Fact]
    public void New_admin_udp_with_no_model_match_emits_Create()
    {
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "RETENTION_DAYS", objectType: "Column", udpType: "Int", defaultValue: "365"),
        };

        var diff = UdpSyncEngine.ComputeDiff(snapshot, AsMap());

        diff.Creates.Should().HaveCount(1);
        diff.Updates.Should().BeEmpty();
        diff.Deletes.Should().BeEmpty();

        var entry = diff.Creates[0];
        entry.Action.Should().Be(UdpDiffAction.Create);
        entry.FullName.Should().Be("Attribute.Physical.RETENTION_DAYS");
        entry.UdpName.Should().Be("RETENTION_DAYS");
        entry.ObjectType.Should().Be("Column");
        entry.AdminUdp.Should().NotBeNull();
        entry.ExistingUdp.Should().BeNull();
        entry.Details.Should().Contain("Int").And.Contain("365");
    }

    [Fact]
    public void IsDeleted_with_existing_match_emits_Delete()
    {
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(7, "OLD_FLAG", isDeleted: true),
        };
        var model = AsMap(ModelUdp("Entity.Physical.OLD_FLAG", dataTypeId: 2));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Deletes.Should().HaveCount(1);
        diff.Creates.Should().BeEmpty();
        diff.Updates.Should().BeEmpty();
        diff.Deletes[0].FullName.Should().Be("Entity.Physical.OLD_FLAG");
        diff.Deletes[0].ExistingUdp.Should().NotBeNull();
    }

    [Fact]
    public void IsDeleted_without_model_match_is_silently_skipped()
    {
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(7, "ALREADY_GONE", isDeleted: true),
        };

        var diff = UdpSyncEngine.ComputeDiff(snapshot, AsMap());

        diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Identical_admin_and_model_emit_nothing()
    {
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "OWNER", objectType: "Table", udpType: "Text", defaultValue: "n/a", description: "Owner"),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.OWNER",
            dataTypeId: 2,
            currentDefault: "n/a",
            currentDescription: "Owner"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Type_change_emits_Update_with_recreates_true()
    {
        // admin: List, model: Text -> not in-place compatible (Phase 1 default)
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "OWNER", objectType: "Table", udpType: "List", listOptions: new[] { "A", "B" }),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.OWNER",
            dataTypeId: 2, // Text
            currentListValues: ""));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Updates.Should().HaveCount(1);
        var u = diff.Updates[0];
        u.Changes.Should().HaveFlag(UdpUpdateChanges.Type);
        u.Changes.Should().HaveFlag(UdpUpdateChanges.ListValues);
        u.RecreatesValues.Should().BeTrue();
        u.Details.Should().Contain("Text -> List").And.Contain("will recreate");
    }

    [Fact]
    public void List_options_changed_emits_Update_without_recreate()
    {
        // admin: List(A,B,C), model: List(A,B) -> options changed but type stays the same
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "STATUS", objectType: "Table", udpType: "List", listOptions: new[] { "A", "B", "C" }),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.STATUS",
            dataTypeId: 6, // List
            currentListValues: "A,B"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Updates.Should().HaveCount(1);
        var u = diff.Updates[0];
        u.Changes.Should().HaveFlag(UdpUpdateChanges.ListValues);
        u.Changes.Should().NotHaveFlag(UdpUpdateChanges.Type);
        u.RecreatesValues.Should().BeFalse();
        u.Details.Should().Contain("List options changed");
    }

    [Fact]
    public void List_options_reorder_emits_Update()
    {
        // ORDER matters: admin says A,B,C; model has C,B,A -> diff sees a change
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "STATUS", objectType: "Table", udpType: "List", listOptions: new[] { "A", "B", "C" }),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.STATUS",
            dataTypeId: 6,
            currentListValues: "C,B,A"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Updates.Should().HaveCount(1);
        diff.Updates[0].Changes.Should().HaveFlag(UdpUpdateChanges.ListValues);
    }

    [Fact]
    public void Default_change_only_emits_Update_with_DefaultChange()
    {
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "RETENTION_DAYS", objectType: "Column", udpType: "Int", defaultValue: "730"),
        };
        var model = AsMap(ModelUdp(
            "Attribute.Physical.RETENTION_DAYS",
            dataTypeId: 1, // Integer
            currentDefault: "365"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Updates.Should().HaveCount(1);
        var u = diff.Updates[0];
        u.Changes.Should().Be(UdpUpdateChanges.Default);
        u.RecreatesValues.Should().BeFalse();
        u.Details.Should().Contain("Default").And.Contain("365").And.Contain("730");
    }

    [Fact]
    public void Description_change_only_emits_Update_with_DescriptionChange()
    {
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "OWNER", objectType: "Table", udpType: "Text", description: "Updated owner help"),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.OWNER",
            dataTypeId: 2,
            currentDescription: "Old owner help"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Updates.Should().HaveCount(1);
        diff.Updates[0].Changes.Should().Be(UdpUpdateChanges.Description);
    }

    [Fact]
    public void Model_orphan_not_in_admin_snapshot_is_left_alone()
    {
        // Admin has nothing; model has a user-created UDP. Diff must be empty.
        var model = AsMap(ModelUdp("Entity.Physical.USER_THING", dataTypeId: 2));

        var diff = UdpSyncEngine.ComputeDiff(new List<UdpDefinitionSnapshot>(), model);

        diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Unknown_object_type_is_silently_skipped()
    {
        // Admin row with an object type the addin can't map (e.g. a future
        // erwin object class the mapping doesn't cover yet).
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(99, "FUTURE_FIELD", objectType: "Unicorn", udpType: "Text"),
        };

        var diff = UdpSyncEngine.ComputeDiff(snapshot, AsMap());

        diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Multiple_admin_udps_produce_aggregated_diff()
    {
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "NEW_ONE", objectType: "Column", udpType: "Int"),
            AdminUdp(2, "CHANGED", objectType: "Table", udpType: "Text", defaultValue: "new"),
            AdminUdp(3, "GONE", objectType: "Table", isDeleted: true),
            AdminUdp(4, "UNCHANGED", objectType: "Table", udpType: "Text", defaultValue: "same"),
        };
        var model = AsMap(
            ModelUdp("Entity.Physical.CHANGED", dataTypeId: 2, currentDefault: "old"),
            ModelUdp("Entity.Physical.GONE", dataTypeId: 2),
            ModelUdp("Entity.Physical.UNCHANGED", dataTypeId: 2, currentDefault: "same"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Creates.Should().HaveCount(1);
        diff.Updates.Should().HaveCount(1);
        diff.Deletes.Should().HaveCount(1);
        diff.TotalCount.Should().Be(3);

        diff.Creates[0].UdpName.Should().Be("NEW_ONE");
        diff.Updates[0].UdpName.Should().Be("CHANGED");
        diff.Deletes[0].UdpName.Should().Be("GONE");
    }

    [Theory]
    [InlineData("Table", "Entity")]
    [InlineData("table", "Entity")]
    [InlineData("Column", "Attribute")]
    [InlineData("View", "View")]
    [InlineData("Procedure", "Stored_Procedure")]
    [InlineData("Model", "Model")]
    [InlineData("Subject Area", "Subject_Area")]
    public void MapObjectTypeToOwnerClass_maps_known_types(string objectType, string expected)
    {
        UdpSyncEngine.MapObjectTypeToOwnerClass(objectType).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unicorn")]
    [InlineData(null)]
    public void MapObjectTypeToOwnerClass_returns_null_for_unknown(string? objectType)
    {
        UdpSyncEngine.MapObjectTypeToOwnerClass(objectType!).Should().BeNull();
    }

    [Theory]
    [InlineData("Int", 1)]
    [InlineData("Integer", 1)]
    [InlineData("Text", 2)]
    [InlineData("Date", 3)]
    [InlineData("Command", 4)]
    [InlineData("Real", 5)]
    [InlineData("List", 6)]
    [InlineData("", 2)]     // empty falls back to Text
    [InlineData("weird", 2)] // unknown falls back to Text
    public void MapUdpTypeToErwinDataTypeId_maps_admin_to_metamodel_id(string udpType, int expected)
    {
        UdpSyncEngine.MapUdpTypeToErwinDataTypeId(udpType).Should().Be(expected);
    }

    [Fact]
    public void ErwinSupportsInPlaceTypeChange_returns_true_only_for_identity()
    {
        UdpSyncEngine.ErwinSupportsInPlaceTypeChange(2, 2).Should().BeTrue();
        UdpSyncEngine.ErwinSupportsInPlaceTypeChange(1, 2).Should().BeFalse();
        UdpSyncEngine.ErwinSupportsInPlaceTypeChange(2, 6).Should().BeFalse();
        UdpSyncEngine.ErwinSupportsInPlaceTypeChange(6, 2).Should().BeFalse();
    }
}
