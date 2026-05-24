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
    public void Model_udp_missing_from_snapshot_is_not_deleted()
    {
        // 2026-05-16: admin schema has no tombstone column, so a model UDP
        // absent from the snapshot is indistinguishable from a user-authored
        // UDP. The diff must NEVER emit a Delete for that case - silent
        // deletion would risk destroying user data on every connect. Users
        // remove UDPs themselves through erwin's UDP editor.
        var snapshot = new List<UdpDefinitionSnapshot>();  // admin has nothing
        var model = AsMap(ModelUdp("Entity.Physical.OLD_FLAG", dataTypeId: 2));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.IsEmpty.Should().BeTrue();

    }

    [Fact]
    public void Diff_never_emits_deletes_even_with_orphans()
    {
        // Admin defines OWNER. Model has OWNER (matches) plus USER_THING
        // (manually created by user). Diff should pass OWNER through with
        // no changes and ignore USER_THING entirely.
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "OWNER", objectType: "Table", udpType: "Text"),
        };
        var model = AsMap(
            ModelUdp("Entity.Physical.OWNER", dataTypeId: 2),
            ModelUdp("Entity.Physical.USER_THING", dataTypeId: 2));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

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
    public void Type_change_emits_Update_in_place()
    {
        // In-place type change is the default; the existing drift sync had
        // proven it works in production. The diff flags Type + ListValues
        // changes so the dialog shows them; Apply writes the new datatype
        // and list values on the existing Property_Type without recreate.
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
        u.Details.Should().Contain("Text -> List");
        u.Details.Should().NotContain("will recreate");
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
    public void ListValues_only_Turkish_dotted_I_difference_suppresses_false_diff()
    {
        // erwin's tag_Udp_Values_List setter transforms U+0130 'İ' to U+0049 'I'
        // on store. Without normalised compare the dialog re-fires on every model
        // open because admin's 'İ' never byte-equals erwin's stored 'I'.
        // Verified 2026-05-23 against the CLASSIFICATION UDP in the live log.
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "CLASSIFICATION", objectType: "Column", udpType: "List",
                listOptions: new[] { "Kurum İçi", "Hizmete Özel", "Gizli" }),
        };
        var model = AsMap(ModelUdp(
            "Attribute.Physical.CLASSIFICATION",
            dataTypeId: 6,
            // erwin stored value: capital dotted-I collapsed to plain I.
            currentListValues: "Kurum Içi,Hizmete Özel,Gizli"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.IsEmpty.Should().BeTrue("erwin's Turkish-I store transform is the only delta");
    }

    [Fact]
    public void ListValues_lowercase_dotted_i_difference_also_suppresses()
    {
        // Symmetric to the uppercase test - U+0131 'ı' would collapse to
        // U+0069 'i' if erwin's setter took the same path on the dotless
        // form. Mirror the rule to avoid an admin chasing the same forever-
        // loop down a different option value.
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "STATUS", objectType: "Table", udpType: "List",
                listOptions: new[] { "kapalı", "açık" }),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.STATUS",
            dataTypeId: 6,
            currentListValues: "kapali,açik"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);
        diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ListValues_real_drift_still_fires_through_normalisation()
    {
        // Sanity: normalisation must not hide a genuine drift. Admin lists
        // three options; model has an extra fourth ("TTT" - matches the
        // user-reported 2026-05-23 TABLE_TYPE scenario where a manually
        // added option needs to be cleaned up).
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "TABLE_TYPE", objectType: "Table", udpType: "List",
                listOptions: new[] { "LOG", "PARAMETER", "TRANSACTION", "HISTORY" }),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.TABLE_TYPE",
            dataTypeId: 6,
            currentListValues: "LOG,PARAMETER,TRANSACTION,HISTORY,TTT"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Updates.Should().HaveCount(1);
        diff.Updates[0].Changes.Should().HaveFlag(UdpUpdateChanges.ListValues);
    }

    [Fact]
    public void Default_value_Turkish_I_difference_suppresses_false_diff()
    {
        // Same store-side normalisation applies to tag_Udp_Default_Value.
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "DEFAULT_OWNER", objectType: "Table", udpType: "Text",
                defaultValue: "Kurum İçi"),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.DEFAULT_OWNER",
            dataTypeId: 2,
            currentDefault: "Kurum Içi"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);
        diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Description_Turkish_I_difference_suppresses_false_diff()
    {
        // Definition / Description field also goes through the same
        // setter quirk on r10.10.
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "INFO", objectType: "Table", udpType: "Text",
                description: "İçeriği belirler"),
        };
        var model = AsMap(ModelUdp(
            "Entity.Physical.INFO",
            dataTypeId: 2,
            currentDescription: "Içeriği belirler"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);
        diff.IsEmpty.Should().BeTrue();
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
        // Admin has three UDPs (NEW_ONE, CHANGED, UNCHANGED). Model has
        // CHANGED + UNCHANGED + a user-authored ORPHAN. Expectation:
        //   - NEW_ONE -> Create (admin defines, model lacks)
        //   - CHANGED -> Update (default value differs)
        //   - UNCHANGED -> no diff (already aligned)
        //   - ORPHAN -> untouched (user owns it, no delete)
        var snapshot = new List<UdpDefinitionSnapshot>
        {
            AdminUdp(1, "NEW_ONE", objectType: "Column", udpType: "Int"),
            AdminUdp(2, "CHANGED", objectType: "Table", udpType: "Text", defaultValue: "new"),
            AdminUdp(4, "UNCHANGED", objectType: "Table", udpType: "Text", defaultValue: "same"),
        };
        var model = AsMap(
            ModelUdp("Entity.Physical.CHANGED", dataTypeId: 2, currentDefault: "old"),
            ModelUdp("Entity.Physical.ORPHAN", dataTypeId: 2),
            ModelUdp("Entity.Physical.UNCHANGED", dataTypeId: 2, currentDefault: "same"));

        var diff = UdpSyncEngine.ComputeDiff(snapshot, model);

        diff.Creates.Should().HaveCount(1);
        diff.Updates.Should().HaveCount(1);

        diff.TotalCount.Should().Be(2);

        diff.Creates[0].UdpName.Should().Be("NEW_ONE");
        diff.Updates[0].UdpName.Should().Be("CHANGED");
    }

    [Fact]
    public void Boolean_admin_type_normalizes_to_List_with_True_False()
    {
        // Admin's Boolean type has no native erwin counterpart. The snapshot
        // boundary rewrites it as List(True, False) so the rest of the
        // pipeline only ever sees List. Regression guard for the 2026-05-16
        // bug where Boolean UDPs (KVKK, PCIDSS) were being projected as
        // erwin Text.
        var snap = new UdpDefinitionSnapshot
        {
            Id = 42,
            Name = "KVKK",
            ObjectType = "Column",
            UdpType = "Boolean",
        };

        UdpSyncEngine.NormalizeBooleanToList(snap);

        snap.UdpType.Should().Be("List");
        snap.ListOptions.Should().HaveCount(2);
        snap.ListOptions[0].Value.Should().Be("True");
        snap.ListOptions[1].Value.Should().Be("False");
    }

    [Fact]
    public void Boolean_admin_type_diff_emits_Update_to_List_type()
    {
        // End-to-end: a Boolean admin row vs. an Integer-typed model UDP
        // (the bug repro: KVKK was Integer in model). After normalization
        // ComputeDiff should emit Update(Type=List, ListValues=True,False).
        var snap = new UdpDefinitionSnapshot
        {
            Id = 42,
            Name = "KVKK",
            ObjectType = "Column",
            UdpType = "Boolean",
        };
        UdpSyncEngine.NormalizeBooleanToList(snap);

        var model = AsMap(ModelUdp(
            "Attribute.Physical.KVKK",
            dataTypeId: 1,        // Integer (the buggy starting state)
            currentListValues: ""));

        var diff = UdpSyncEngine.ComputeDiff(new[] { snap }, model);

        diff.Updates.Should().HaveCount(1);
        var u = diff.Updates[0];
        u.Changes.Should().HaveFlag(UdpUpdateChanges.Type);
        u.Changes.Should().HaveFlag(UdpUpdateChanges.ListValues);
        u.Details.Should().Contain("Integer -> List");
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

}
