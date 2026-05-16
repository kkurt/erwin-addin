# Architecture: Admin → Model UDP Sync

Last updated: 2026-05-16

This document covers the UDP definition sync feature added in May 2026
(plan: `C:\Users\Kursat\.claude\plans\tingly-doodling-russell.md`). It is
intentionally narrow: only the parts that span the addin and the admin DB
contract. Pure addin internals are documented inline in the source files.

---

## What it does

When a model is opened, the addin compares the UDP definitions stored in
the admin DB (the `CONFIG` row bound to this model) against the
`Property_Type` objects currently in the erwin metamodel. If anything
differs, a custom dialog lists every Create / Update entry and asks the
user to Apply or Cancel.

Deletes are never proposed: a model `Property_Type` missing from the
admin snapshot is indistinguishable from a user-authored UDP and we will
not silently destroy user data. Users remove unwanted UDPs themselves
through erwin's own UDP editor.

```mermaid
flowchart TD
    A[Model opened] --> B[InitializeModelServices]
    B --> C[FetchSnapshot from MC_UDP_DEFINITION]
    C --> D[WalkModelUdps - one metamodel pass]
    D --> E[ComputeDiff snapshot vs. model]
    E -->|empty| F[Log already in sync - done]
    E -->|non-empty| G[BeginInvoke deferred dialog]
    G --> H{User choice}
    H -->|Apply| I[Busy overlay + Apply in single transaction]
    H -->|Cancel| J[Log cancelled - next open shows same diff]
    I --> K[UpdateDependencySetListValues cascade refresh]
```

---

## Code surface

| Layer | File | Responsibility |
|-------|------|----------------|
| Snapshot fetch | [Services/UdpSyncEngine.cs](../Services/UdpSyncEngine.cs) `FetchSnapshot` | Read `MC_UDP_DEFINITION` + `MC_UDP_LIST_OPTION` for the active CONFIG. Normalise admin `Boolean` UDPs to `List(True, False)` at this boundary. |
| Metamodel walk | `UdpSyncEngine.WalkModelUdps` | Single level-1 session pass that returns both the filter-resolved `ModelUdpSnapshot` map and the full Property_Type name set. The names set feeds `ModelConfigForm._cachedPropertyTypeNames` so `ValidationCoordinator` does not walk again. |
| Diff (pure) | `UdpSyncEngine.ComputeDiff` | Pairs snapshot rows against the model map by canonical `<Owner>.Physical.<Name>`. Emits only Create + Update. |
| Apply | `UdpSyncEngine.Apply` | Single named transaction, Updates then Creates. Type / default / list-values / definition all written in place. EBS-1057 unique-name conflicts are tolerated. |
| Dialog | [Forms/UdpSyncDialog.cs](../Forms/UdpSyncDialog.cs) | Borderless modal with action chips, summary counters, drag-by-header, multi-monitor positioning. |
| Wire-up | [ModelConfigForm.RunUdpSyncIfNeeded](../ModelConfigForm.cs) | Runs between dep-set load and `UdpRuntime.Initialize`. Deferred `ShowDialog` via `BeginInvoke` so it does not deadlock `Form.Load`. |
| Runtime cascade | [Services/UdpRuntimeService.cs](../Services/UdpRuntimeService.cs) `UpdateDependencySetListValues` | Recomputes `tag_Udp_Values_List` for dependency-set-driven UDPs whenever the model UDP values change. Short-circuits when `MappingCount == 0`. |

---

## Admin DB contract

The admin module owns the schema. The addin **reads** from it on every
model open and **writes** the `Apply` result into the erwin metamodel
(never back into the admin DB).

### Read by the addin

| Table | Columns read | Purpose |
|-------|--------------|---------|
| `MC_UDP_DEFINITION` | `ID`, `NAME`, `DESCRIPTION`, `OBJECT_TYPE`, `UDP_TYPE`, `DEFAULT_VALUE`, `CONFIG_ID`, `IS_REQUIRED`, `IS_LOCKED`, `MIN_VALUE`/`MAX_VALUE`/`MAX_LENGTH`, `VALIDATION_OPERATOR`/`VALIDATION_VALUE`, `ERROR_MESSAGE`, `APPLY_ON`, `SORT_ORDER` | The full UDP definition. `CONFIG_ID` is the join key. |
| `MC_UDP_LIST_OPTION` | `UDP_DEFINITION_ID`, `VALUE`, `DISPLAY_TEXT`, `SORT_ORDER` | List options for List-type UDPs. |
| `MODEL_CONFIG_MAPPING` | `MART_PATH`, `CONFIG_ID` | Resolves which CONFIG row the active model is bound to. |
| `CONFIG` | `ID`, `NAME`, `CORPORATE_ID`, `DBMS_VERSION_ID` | Context for the active config (shown on the General tab). |

### Mapping rules (admin UDP_TYPE → erwin `tag_Udp_Data_Type`)

| Admin `UDP_TYPE` | erwin `tag_Udp_Data_Type` | Notes |
|------------------|---------------------------|-------|
| `Int` / `Integer` | `1` | |
| `Text` | `2` | Default fallback for unknown values. |
| `Date` / `Datetime` | `3` | |
| `Command` | `4` | |
| `Real` / `Float` / `Decimal` | `5` | |
| `List` | `6` | `tag_Udp_Values_List` populated from `MC_UDP_LIST_OPTION`. |
| `Boolean` | `6` (List) | erwin has no native Boolean. The snapshot rewrites this to `List` with options `True,False` at the boundary, so downstream code never sees `Boolean`. |

### Object-type → owner class

| Admin `OBJECT_TYPE` | erwin `tag_Udp_Owner_Type` |
|----------------------|----------------------------|
| `Table` | `Entity` |
| `Column` | `Attribute` |
| `View` | `View` |
| `Procedure` | `Stored_Procedure` |
| `Model` | `Model` |
| `Subject Area` | `Subject_Area` |

Unknown object types are skipped at diff time.

---

## Naming Standards: SCAPI accessor mapping

Naming-standard rules in `MC_NAMING_STANDARD` reference a `PROPERTY_DEF_ID`
that points at `MC_PROPERTY_DEF.PROPERTY_CODE`. The addin reads the live
value via `scapiObject.Properties(PROPERTY_CODE).Value`; the code must
match an actual erwin SCAPI property accessor exactly or SCAPI throws
"is not valid class id or class name for object or property".

Verified empirically 2026-05-16 with [MetamodelPropertyProbeService](../Services/MetamodelPropertyProbeService.cs)
across four DBMS families:

| Concept | SCAPI accessor (PROPERTY_CODE) | Verified on |
|---------|-------------------------------|-------------|
| Table physical name | `Physical_Name` | SQL Server 2012, Oracle 19c, DB2 z/OS 12/13, PostgreSQL 16 |
| Table logical name | `Name` | All 4 |
| Table definition / comment | `Definition`, `Comment` | SQL Server (Oracle/DB2/PG only when populated) |
| **Table owner / schema** | **`Name_Qualifier`** | **All 4** (returned 'MMS' / 'dbo' depending on model) |
| Column physical name | `Physical_Name` | All 4 |
| Column data type | `Physical_Data_Type` | All 4 |
| Column nullability | `Null_Option_Type` | All 4 |
| Index name | `Physical_Name` | All 4 |
| Index type | `Key_Group_Type` (e.g. 'PK') | All 4 |
| Index uniqueness | `Is_Unique` | All 4 |
| Model name | `Name` | All 4 |
| Model target DBMS | `Target_Server` (integer code) | All 4 |

**Important non-result:** `Schema_Name` was rejected by SCAPI on every
DBMS tested. erwin's metamodel association data
(`Program Files\erwin\Data Modeler r10\EMXLPropertyAssociations.data`)
lists `Entity__has__SchemaName` but the SCAPI accessor surface does not
expose it. `Name_Qualifier` (from `AbstractEntity__has__NameQualifier`)
is the live accessor.

The previous `ReadScapiPropertyWithFallback` chain in
`TableTypeMonitorService` was removed 2026-05-16 once the empirical
mapping was confirmed - admin's `PROPERTY_CODE` is the authoritative
SCAPI accessor.

### Authoring a new naming-standard rule

1. Insert (or use admin UI to add) a row in `MC_PROPERTY_DEF` whose
   `PROPERTY_CODE` matches the SCAPI accessor for the property you
   want to constrain. Use the table above for common ones; run the
   dev-only Probe Properties button to discover new accessors on
   exotic DBMS.
2. Insert a rule in `MC_NAMING_STANDARD` pointing at the new
   `PROPERTY_DEF_ID`.
3. The addin picks it up on the next connect (or via Reload Config)
   and fires the popup when the value violates the rule.

### Adding a Platform Property (DBMS-specific)

If a property is only meaningful on one DBMS (e.g. `Oracle_Entity_Partition_Type`),
add the row with `DBMS_VERSION_ID` set to the specific version. The
addin's naming-standard load query filters on the active model's
DBMS_VERSION so other DBMS connections do not see the rule.

---

## Deliberate trade-offs

- **Renames are not detected.** The diff matches by canonical name. An
  admin rename surfaces as `Create(newname)`; the old UDP stays in the
  model as an orphan until the user removes it. Admins who need a rename
  should delete the old definition first (out of band, in their UI) and
  recreate with the new name - the addin will surface that as
  `Create(new)` and leave the orphan untouched.

- **Deletes are never automatic.** Same reason as renames: a row missing
  from the snapshot is indistinguishable from a user-authored UDP. Users
  remove UDPs themselves.

- **Name collisions are the admin team's responsibility.** If a user
  manually creates a UDP whose name matches an admin definition, the
  diff will emit an Update that overwrites the user's field shape (type,
  default, list options). Deployment conventions (admin namespace prefix,
  user training) prevent this.

- **In-place type change is the default.** `Int -> Text` /
  `Text -> List` etc. all rewrite the same `Property_Type` instead of
  delete+recreate. Existing entity-level UDP values survive (erwin
  reads them as strings regardless of `tag_Udp_Data_Type`). The previous
  silent drift sync had been doing in-place for months without value
  corruption reports, so this is the conservative choice.

- **Cancel does not stick.** No "last-seen version" is stored on the
  model side. If the user cancels, the next model open recomputes the
  same diff and shows the dialog again. The plan considered a version /
  hash mechanism and rejected it as over-engineering - the user can
  always cancel a second time, and a cancel-now / apply-later workflow
  is exactly what we want for indecisive cases.

---

## Performance notes

The first cut of the feature paid a 21-second cost on every model open
against a 1500-entry metamodel. The 2026-05-16 optimisations brought
total connect time from ~29 s to ~6 s. The remaining cost is dominated
by COM iteration of the Property_Type collection (~1.3 s for 1517
entries) which is the SCAPI floor.

Two specific things to know:

1. **Filtered walk** - `WalkModelUdps(namesOfInterest)` reads
   `tag_Udp_Data_Type` / `tag_Udp_Default_Value` / `tag_Udp_Values_List` /
   `Definition` only for the handful of `Property_Type`s whose `Name`
   matches admin's expected set. Without the filter the walk reads
   4 × 1500 = 6000 dynamic-dispatch properties = ~18 s.

2. **Single walk for two consumers** - `WalkModelUdps` returns both the
   filtered model map (for diff) and the full name set (for
   `ValidationCoordinator`'s metamodel-name cache). The old
   `EnsureAllUdpsExist` separate walk has been removed.

---

## Error paths

| Failure | Behaviour |
|---------|-----------|
| Admin DB unreachable | `RunUdpSyncIfNeeded` catches, `AddConnectWarning("UDP sync skipped: ...")` surfaces on the General tab Warnings row, model still opens. |
| `ConfigContextService.ActiveConfigId <= 0` (degraded mode) | Sync skipped silently - the form is in degraded mode anyway. |
| Metamodel session open fails inside `Apply` | Transaction rolled back, `AddConnectWarning("UDP sync apply failed: ...")`, model open continues. |
| Rapid model switch while dialog is open | `_udpSyncDialogOpen` race guard makes the second trigger a no-op. The earlier dialog runs to completion; on the next open the diff is recomputed fresh. |

---

## Where to look next

- Plan: `C:\Users\Kursat\.claude\plans\tingly-doodling-russell.md`
- Lessons from this work: [tasks/lessons.md](../tasks/lessons.md)
  (`2026-05-16: UDP sync ...` entries)
- Custom dialog visual language: [Forms/AddinMessageDialog.cs](../Forms/AddinMessageDialog.cs)
  (the UDP sync dialog shares its borderless / TopMost / multi-monitor
  patterns with this generic dialog).
