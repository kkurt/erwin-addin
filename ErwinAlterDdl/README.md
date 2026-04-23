# ErwinAlterDdl

Compare two `.erwin` models and emit alter DDL. Shared module consumed by the
existing erwin add-in, a standalone CLI, and an internal REST daemon.

- **Target:** erwin DM r10.10 (see `docs/research_findings.md` for rationale).
- **Framework:** .NET 10 (`net10.0-windows` for SCAPI-bound projects,
  `net10.0` for Core).
- **Status:** Phase 2 (Core library + CLI + REST skeleton).

## Quick build

```bash
cd ErwinAlterDdl
dotnet build ErwinAlterDdl.slnx -c Release
```

## Quick test

```bash
dotnet test ErwinAlterDdl.slnx
```

## Quick CLI run (Phase 2 mock mode)

Phase 2 CLI runs against a pre-computed artifacts directory (it does not yet
drive SCAPI out-of-process; that is Phase 3). Prepare a directory with:
- `v1.erwin` + `v1.xml`
- `v2.erwin` + `v2.xml`
- `diff.xls` (a `CompleteCompare` output you already captured)

Then:

```bash
dotnet run --project src/ErwinAlterDdl.Cli -- \
    --left /path/v1.erwin \
    --right /path/v2.erwin \
    --out /path/result.json \
    --session-mode mock \
    --artifacts-dir /path
```

Output: `result.json` with `LeftMetadata`, `RightMetadata`, and a polymorphic
`Changes` array (discriminator = `kind`).

## Quick REST run

```bash
dotnet run --project src/ErwinAlterDdl.Api
```

Then:

```bash
curl -X POST http://localhost:5000/compare \
    -H "X-Api-Key: dev-change-me" \
    -F "leftErwin=@/path/v1.erwin" \
    -F "leftXml=@/path/v1.xml" \
    -F "rightErwin=@/path/v2.erwin" \
    -F "rightXml=@/path/v2.xml" \
    -F "diffXls=@/path/diff.xls"
```

Health endpoint (no auth required): `GET /health`.

Configure the API key via `appsettings.json` key `Api:ApiKey` or env var
`API__APIKEY`.

## Layout

- `src/ErwinAlterDdl.Core/` - SCAPI-agnostic domain + parsing + correlation.
  This is the NuGet-publishable package consumed by external projects.
- `src/ErwinAlterDdl.ComInterop/` - `IScapiSession` implementations that talk
  to erwin over COM. Windows x64 only.
- `src/ErwinAlterDdl.Cli/` - `erwin-ddl-diff` executable.
- `src/ErwinAlterDdl.Api/` - REST daemon.
- `tests/ErwinAlterDdl.Core.Tests/` - xUnit + FluentAssertions.
- `spike/` - Phase 1 PoC (kept for reference).
- `fixture_tools/FixtureGen/` - dev tool that transplants v1 UIDs onto a
  v2 XML (used to build the canonical test fixtures).
- `test_files/` - 3 DBMS fixture families (MSSQL 2022, Db2 z/OS v12-v13,
  Oracle 19c-21c) with case-map markdown, expected alter SQL references,
  and per-version `.erwin` + `.xml` exports. `backup_dont_consider/` holds
  the raw SQL-reverse-engineered originals.
- `docs/ARCHITECTURE.md` - component, sequence, and class diagrams.

See `tasks/alterddl_phase2.md` for the Phase 2 task list and `docs/research_findings.md`
(repo root) for design rationale.
