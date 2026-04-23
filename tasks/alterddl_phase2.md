# ErwinAlterDdl Phase 2: Core + ComInterop + Cli + Api Skeleton

**Started:** 2026-04-23
**Prereq:** Phase 1 spike complete (commit 2acf904)
**Exit criteria:** CLI compares two real `.erwin` files, dumps `Change` list as JSON; unit tests ≥80% on Core + parsers; ARCHITECTURE.md with 3 Mermaid diagrams; 0 build warnings.

## Scope

Refactor spike's one-file correlation into a proper shared module with test coverage
and two entry points (CLI + REST). **No SQL emission yet** - that is Phase 3.

## Layout (inside existing `ErwinAlterDdl/` folder)

```
ErwinAlterDdl/
├── ErwinAlterDdl.sln              (new)
├── src/
│   ├── ErwinAlterDdl.Core/
│   │   ├── Abstractions/
│   │   │   └── IScapiSession.cs
│   │   ├── Models/
│   │   │   ├── ObjectRef.cs
│   │   │   ├── Change.cs           (sealed hierarchy)
│   │   │   ├── CompareOptions.cs
│   │   │   ├── CompareArtifact.cs
│   │   │   ├── DdlArtifact.cs
│   │   │   └── ModelMetadata.cs
│   │   ├── Parsing/
│   │   │   ├── XlsDiffParser.cs
│   │   │   └── ErwinXmlObjectIdMapper.cs
│   │   ├── Correlation/
│   │   │   └── ChangeCorrelator.cs
│   │   ├── Pipeline/
│   │   │   └── CompareOrchestrator.cs
│   │   └── ErwinAlterDdl.Core.csproj
│   │
│   ├── ErwinAlterDdl.ComInterop/
│   │   ├── InProcessScapiSession.cs
│   │   ├── OutOfProcessScapiSession.cs     (stub, Phase 3)
│   │   ├── MockScapiSession.cs             (test helper, exported)
│   │   └── ErwinAlterDdl.ComInterop.csproj
│   │
│   ├── ErwinAlterDdl.Cli/
│   │   ├── Program.cs
│   │   └── ErwinAlterDdl.Cli.csproj
│   │
│   └── ErwinAlterDdl.Api/
│       ├── Program.cs
│       └── ErwinAlterDdl.Api.csproj
│
├── tests/
│   ├── ErwinAlterDdl.Core.Tests/
│   │   ├── XlsDiffParserTests.cs
│   │   ├── ErwinXmlObjectIdMapperTests.cs
│   │   ├── ChangeCorrelatorTests.cs
│   │   └── ErwinAlterDdl.Core.Tests.csproj
│   └── ErwinAlterDdl.Integration.Tests/     (stub, Phase 4)
│       └── ErwinAlterDdl.Integration.Tests.csproj
│
├── spike/                                   (existing, kept as Phase 1 reference)
├── fixture_tools/FixtureGen/                (existing, dev tool)
├── test_files/                              (existing, shared fixtures)
└── docs/
    └── ARCHITECTURE.md                      (new, with Mermaid)
```

## Tasks

### 2.0 Solution scaffolding
- [ ] Create `ErwinAlterDdl/ErwinAlterDdl.sln` referencing all 6 projects + 2 tests
- [ ] Add `Directory.Build.props` for shared settings:
    - `<TargetFramework>net10.0-windows</TargetFramework>` for ComInterop/Cli/Api
    - `<TargetFramework>net10.0</TargetFramework>` for Core (SCAPI-agnostic)
    - `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
    - `<LangVersion>latest</LangVersion>`, `<AnalysisLevel>latest</AnalysisLevel>`
- [ ] Verify `dotnet build ErwinAlterDdl.sln` passes with 0 warnings

### 2.1 Core: Domain models
- [ ] `ObjectRef` record (ObjectId, Name, Class)
- [ ] `Change` abstract record + sealed variants (Phase-2 scope only):
    - [ ] `EntityAdded`, `EntityDropped`, `EntityRenamed`, `SchemaMoved`
    - [ ] `AttributeAdded`, `AttributeDropped`, `AttributeRenamed`
    - [ ] `AttributeTypeChanged`
- [ ] `CompareOptions` (Preset: Standard/Advance/custom-xml, Level: LP/L/P/DB, custom option path)
- [ ] `CompareArtifact` (xls path, row count, metadata)
- [ ] `DdlArtifact` (sql path, target server, size)
- [ ] `ModelMetadata` (target_server, version, model_type, persistence_unit_id)
- [ ] `ChangeKindEnum` (if needed for JSON discriminator)

### 2.2 Core: Parsing
- [ ] `XlsDiffParser` (input CC xls path, output `IReadOnlyList<XlsRow>`)
    - Migrate spike's HtmlAgilityPack parse
    - Indent-based hierarchy walk helper
    - Unit tests: feed `test_files/erwin/backup_dont_consider/` derived fixtures
    - Turkish char preservation test
- [ ] `ErwinXmlObjectIdMapper` (input xml path, output `Dict<ObjectId, ObjectRef>` + parent-scoped attribute lookup)
    - Migrate spike's XDocument parse
    - Build per-class map
    - Handle duplicate ObjectIDs gracefully (first wins + log)
    - Unit tests: feed fixture XMLs, assert entity/attribute counts

### 2.3 Core: Correlation
- [ ] `ChangeCorrelator` input (left xml map, right xml map, xls rows), output `IReadOnlyList<Change>`
    - Entity level: set algebra (added, dropped, common-with-name-diff = renamed)
    - Attribute level: parent-scoped same logic
    - AttributeTypeChanged: walk XLS "Physical Data Type" Not Equal rows, pair with context
    - Unit tests: MSSQL fixture path → assert exact change list
    - Deterministic output (sorted by ObjectId or hierarchy path)

### 2.4 Core: Pipeline orchestrator
- [ ] `CompareOrchestrator` (takes IScapiSession, calls RunCompleteCompareAsync + ReadModelMetadataAsync, feeds parsers + correlator, returns `CompareResult` {ModelMetadata, List<Change>, Artifacts})
- [ ] Canonical pipeline: **no FEModel_DDL yet** (Phase 3 addition)

### 2.5 Abstractions + ComInterop
- [ ] `IScapiSession` interface (3 async methods + IAsyncDisposable)
- [ ] `MockScapiSession` (reads artifacts from disk given a directory, for tests)
- [ ] `InProcessScapiSession` (takes live `dynamic scapi` handle, wraps CompleteCompare + FEModel_DDL + PropertyBag read)
- [ ] `OutOfProcessScapiSession` **stub** (throws NotImplementedException; Phase 3 will implement isolation)

### 2.6 CLI entry point
- [ ] `ErwinAlterDdl.Cli/Program.cs` with `System.CommandLine`:
    ```
    compare --left <v1.erwin> --right <v2.erwin> --out <result.json>
            [--compare-level LP|L|P|DB]
            [--cc-option-set <xml-path>]
            [--verbose]
    ```
- [ ] DI host (`Microsoft.Extensions.Hosting`) + Serilog console/file sinks
- [ ] Uses `OutOfProcessScapiSession` at runtime (Phase 3) but in Phase 2 spec test uses Mock + local spike-produced xls/xml paths
- [ ] JSON output: `{ modelMetadata: {...}, changes: [{...}] }` via System.Text.Json with polymorphic Change
- [ ] Exit codes per NEW_NEED.md section 8

### 2.7 REST API entry point
- [ ] `ErwinAlterDdl.Api/Program.cs` ASP.NET Core minimal API
- [ ] `POST /compare` multipart form (v1.erwin, v2.erwin optional options JSON), returns async job id
- [ ] `GET /jobs/{id}` status + result
- [ ] `X-Api-Key` middleware (config-based, internal network)
- [ ] Health endpoint `/health`
- [ ] Same Core pipeline

### 2.8 Tests
- [ ] `XlsDiffParserTests` ≥5 tests (empty, 1-table, hierarchy, Turkish chars, malformed)
- [ ] `ErwinXmlObjectIdMapperTests` ≥5 tests (Entity/Attribute/KeyGroup/Relationship/duplicate-id)
- [ ] `ChangeCorrelatorTests` ≥8 tests (all Phase-2 change types + happy path MSSQL fixture end-to-end)
- [ ] `CompareOrchestratorTests` ≥2 tests with MockScapiSession
- [ ] Coverage collector: `coverlet.collector` + target ≥80% on Core + ComInterop

### 2.9 Documentation
- [ ] `ErwinAlterDdl/docs/ARCHITECTURE.md` with:
    - High-level component diagram (Mermaid)
    - Sequence diagram: CLI compare flow (Mermaid)
    - Class diagram: Change hierarchy + IScapiSession (Mermaid)
    - Prose: design decisions, Phase boundaries, NuGet packaging plan
- [ ] `ErwinAlterDdl/README.md` quick start (how to build, how to run, how to test)

### 2.10 Verification
- [ ] `dotnet build` clean, 0 warnings
- [ ] `dotnet test` green, ≥80% coverage
- [ ] CLI runnable against MSSQL fixture → produces expected JSON (ENTITY_ADD CAMPAIGN, ENTITY_DROP PRODUCT_ARCHIVE, ENTITY_RENAME CUSTOMER_BACKUP→CUSTOMER_HISTORY, ATTR_RENAME mobile_phone→mobile_no, ATTR_TYPE varchar(100)→varchar(250), varchar(50) int→bigint)
- [ ] Commit + push

## Out of scope (deferred to Phase 3)

- SQL emission / alter DDL generator (DBMS-specific dialects)
- OutOfProcessScapiSession concrete implementation (erwin.exe child process + injection)
- FEModel_DDL integration in pipeline (currently deferred)
- PK/FK/Index/View/Trigger/Sequence change types
- Addin UI "Compare Two Files" button
- NuGet publish workflow

## Commit plan

One logical piece per commit:
1. Solution scaffolding + Directory.Build.props (2.0)
2. Domain models (2.1)
3. Parsing + unit tests (2.2)
4. Correlation + unit tests (2.3)
5. Pipeline orchestrator + IScapiSession + Mock (2.4, 2.5)
6. CLI entry point (2.6)
7. REST API entry point (2.7)
8. ARCHITECTURE.md + README.md (2.9)
