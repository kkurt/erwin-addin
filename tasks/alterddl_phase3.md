# ErwinAlterDdl Phase 3: Out-of-Process Session, SQL Emission, Full Change Set

**Started:** 2026-04-23
**Prereq:** Phase 2 complete (commit d57d5b5)
**Exit criteria:** CLI compares two real `.erwin` files fully end-to-end (CC
done out-of-process, DDL parsed, alter SQL emitted for the target DBMS) on all
three canonical fixtures; all case-map change types covered; ≥80% test coverage
on Core; add-in has working "Compare Two Files..." button consuming Core.

## Blocks

### 3.A  OutOfProcessScapiSession + Worker process (foundation)

Gateway that removes the SCAPI r10.10 singleton pollution (documented in
`reference_scapi_gotchas_r10.md`). Every SCAPI operation is executed by a
short-lived child process that owns its own `erwin.exe` COM LocalServer.

**Deliverables:**
- `ErwinAlterDdl.Worker` (new project, net10.0-windows x64 Exe)
    - Single executable that accepts args describing one SCAPI operation,
      runs it, writes a JSON result to stdout, exits.
    - Subcommands: `cc`, `ddl`, `metadata` (matching the 3 IScapiSession methods).
    - No IPC, no daemon mode. Process-per-call is the isolation guarantee.
- `OutOfProcessScapiSession` (ComInterop)
    - Implements `IScapiSession` by shelling out to the Worker exe via
      `Process.Start` + stdout capture.
    - Serializes request payload to the Worker as JSON on stdin (or argv
      for small args).
    - Deserializes JSON response from stdout; failure = non-zero exit +
      stderr content in the thrown exception.
    - Cancellation: kills the Worker process on `ct` cancel.
- Worker executable auto-location (dev: via probing `bin/` next to the
  consumer DLL; published: next to the Cli/Api binary).
- Unit tests: process-startup exit codes (happy + stderr + timeout).
- Integration test (gated): actually runs against Oracle canonical fixture
  (the one that blocked Phase 1 spike) and validates CC XLS production.

**Tasks:**
- [ ] Create `src/ErwinAlterDdl.Worker/ErwinAlterDdl.Worker.csproj` (Exe) + `Program.cs`
- [ ] Worker argv/JSON contract doc (short addendum in ARCHITECTURE.md)
- [ ] Implement `cc` subcommand (left, right, options -> CompareArtifact JSON)
- [ ] Implement `metadata` subcommand (path -> ModelMetadata JSON)
- [ ] Implement `ddl` subcommand (path, options -> DdlArtifact JSON)
- [ ] `OutOfProcessScapiSession` launch helper + retry-on-transient exit code
- [ ] Replace CLI `--session-mode out-of-process` from NotImplementedException
- [ ] Oracle fixture smoke test (CC produces non-empty xls)

**Risks:**
- Worker needs to be co-located. Use `AppContext.BaseDirectory` to find it,
  fall back to env var `ERWIN_ALTER_DDL_WORKER`.
- SCAPI COM activation itself requires x64; Worker must match.
- "Processing Events" modal may flash. Out-of-process guarantees user GUI
  isolation; on the Worker process we can cross-process `ShowWindow(SW_HIDE)`
  if needed.

### 3.B  FEModel_DDL integration

Wire up the `GenerateCreateDdlAsync` path through the Worker so Core has full
CREATE DDL available for every compare.

**Deliverables:**
- Worker `ddl` subcommand fully implemented
- `CompareOrchestrator` optionally calls `GenerateCreateDdlAsync` for left
  and right (controlled by `CompareOptions.IncludeCreateDdl`)
- Ship captured DDL alongside the result artifacts

### 3.C  SQL emitter skeleton + MSSQL dialect

The piece that actually produces alter DDL from the Change list.

**Deliverables:**
- `ISqlEmitter` in Core (emits `AlterDdlScript` record = ordered list of
  statements + DBMS target + dialect info)
- `ISqlEmitterFactory` picks an emitter from `ModelMetadata.TargetServer`
- `MssqlEmitter` (first dialect, covers Phase 2 change types + extensions
  added in 3.D)
- CLI now also outputs an `.sql` file next to the JSON result
- Unit tests: per-change-type expected SQL fragments

### 3.D  Additional change types (correlator + models + emitter)

Expand `Change` hierarchy and `ChangeCorrelator` to cover full case-map
coverage:
- AttributeNullabilityChanged, AttributeDefaultChanged, AttributeIdentityAdded
- PrimaryKeyAdded / PrimaryKeyDropped / PrimaryKeyColumnsChanged
- UniqueConstraintAdded / UniqueConstraintDropped
- ForeignKeyAdded / ForeignKeyDropped / ForeignKeyCascadeChanged / ForeignKeyColumnsChanged
- IndexAdded / IndexDropped / IndexColumnsChanged / IndexUniquenessChanged
- ViewAdded / ViewDropped / ViewBodyChanged
- TriggerAdded / TriggerDropped / TriggerBodyChanged
- SequenceAdded / SequenceDropped / SequenceIncrementChanged
- CheckConstraintAdded / CheckConstraintDropped / CheckConstraintChanged
- SchemaAdded / SchemaDropped

Each change type gets:
- Record in `Models/Change.cs` with JsonDerivedType entry
- Correlator emission (from XLS signal + XML identity)
- MSSQL emitter case
- Unit test against a case-map fixture row

### 3.E  Oracle + Db2 dialects

- `OracleEmitter` (NUMBER/VARCHAR2 types, `ALTER TABLE RENAME`, CASCADE, etc.)
- `Db2Emitter` (CHAR/VARCHAR, `RENAME TABLE`, `REORG TABLE` callout)
- Reuse 3.C pattern; fixture-driven tests per dialect
- Golden master comparison against `test_files/expected_alters/*_v1_to_v2.sql`

### 3.F  Add-In UI integration

- New ribbon/menu item "Compare Two Files..." in the existing add-in
- File picker dialog (v1 + v2 .erwin)
- Launches Core pipeline using `InProcessScapiSession` wrapping the add-in's
  live SCAPI handle
- Displays Change list in a new docked pane
- Generate alter SQL button -> save as file

### 3.G  NuGet publish workflow

- Add `dotnet pack` targets for `ErwinAlterDdl.Core` (and optionally
  `ErwinAlterDdl.ComInterop`)
- Internal feed (BaGet / Azure Artifacts / GitHub Packages) - user decides
- Versioning (SemVer), release notes template

## Commit plan

1. `feat(alter-ddl): Phase 3 plan` - this markdown only
2. 3.A.1 - Worker scaffolding + metadata subcommand
3. 3.A.2 - Worker cc subcommand
4. 3.A.3 - OutOfProcessScapiSession impl + CLI wiring + smoke test
5. 3.B - FEModel_DDL through Worker + orchestrator option
6. 3.C - ISqlEmitter + MSSQL dialect + tests
7. 3.D (per change category) - nullability, defaults, PKs, FKs, indexes, views, triggers, sequences, checks
8. 3.E - Oracle emitter + tests
9. 3.E - Db2 emitter + tests
10. 3.F - Add-in UI integration
11. 3.G - NuGet packaging + docs
