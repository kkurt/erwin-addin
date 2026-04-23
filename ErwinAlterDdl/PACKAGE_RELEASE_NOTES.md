# NuGet Package Release Notes

## 0.1.0-alpha (2026-04-23)

First internal alpha drop. Feature-complete for the Phase 3 change set:

- **Correlation engine (Core)**
  - XLS + .erwin XML ObjectId correlation.
  - Typed `Change` hierarchy: Entity / Attribute / Key_Group / Relationship /
    View / Trigger / Sequence add / drop / rename + AttributeTypeChanged,
    NullabilityChanged, DefaultChanged, IdentityChanged, SchemaMoved.
  - Key_Group identity churn dedup.
- **SQL emitters**
  - MSSQL 2019+, Oracle 19c/21c, Db2 z/OS v12/v13.
  - Schema-qualified name splitting (`[schema].[table]`).
  - PK / UQ / Index / FK column lists pulled from v2 CREATE DDL when present.
- **COM interop (ComInterop)**
  - In-process `ISCPersistenceUnit` session (erwin add-in embedding).
  - Out-of-process Worker session (crash isolation + singleton reset).
  - Mock session for tests / deterministic CI.

### Known limitations

- View / Trigger bodies still emit a TODO placeholder (body extraction from
  the .erwin XML is tracked for a future minor).
- ForeignKey DROP emits a TODO marker (child-table resolution from v1 XML
  planned for the next minor).
- IDENTITY toggles in MSSQL / Db2 emit guidance comments rather than
  inlined SQL (no engine supports an in-place flip).

### Consuming

```xml
<PackageReference Include="EliteSoft.Erwin.AlterDdl.Core" Version="0.1.0-alpha" />
<!-- Optional, only when targeting SCAPI directly -->
<PackageReference Include="EliteSoft.Erwin.AlterDdl.ComInterop" Version="0.1.0-alpha" />
```

`ComInterop` depends on `Core` and targets `net10.0-windows` (x64). `Core`
is platform-agnostic `net10.0`.
