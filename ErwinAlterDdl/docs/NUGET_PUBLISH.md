# NuGet Publish Flow

Two projects produce packages. Everything else (Cli, Api, Worker, tests)
inherits `<IsPackable>false</IsPackable>` from `Directory.Build.props`:

| Package | Project | Target | Purpose |
|---------|---------|--------|---------|
| `EliteSoft.Erwin.AlterDdl.Core` | `src/ErwinAlterDdl.Core` | `net10.0` | Correlation engine + SQL emitters. No SCAPI coupling. |
| `EliteSoft.Erwin.AlterDdl.ComInterop` | `src/ErwinAlterDdl.ComInterop` | `net10.0-windows` x64 | SCAPI session wrappers (in-process / out-of-process / mock). Depends on Core. |

Both produce a `.nupkg` (assembly) and a `.snupkg` (portable PDBs) so
consumers can step through our code during debugging.

## Build

```powershell
# from repo root
cd ErwinAlterDdl
.\scripts\pack.ps1
```

or manually:

```powershell
dotnet pack src/ErwinAlterDdl.Core/ErwinAlterDdl.Core.csproj `
    -c Release --nologo -o artifacts

dotnet pack src/ErwinAlterDdl.ComInterop/ErwinAlterDdl.ComInterop.csproj `
    -c Release --nologo -o artifacts
```

Artifacts land in `artifacts/`. Running `dotnet pack` against the full
`.slnx` also works but produces one NU5017 warning per non-packable project
(Api, Cli, Worker, tests) which is cosmetic.

## Publish to internal feed

Set the feed URL + API key once (they land in `%APPDATA%\NuGet\NuGet.Config`):

```powershell
dotnet nuget add source https://nuget.internal.elitesoft.local/v3/index.json `
    --name elitesoft-internal `
    --username <your-ad-login> `
    --password <api-key> `
    --store-password-in-clear-text
```

Push the packages (they ship together):

```powershell
dotnet nuget push artifacts/EliteSoft.Erwin.AlterDdl.Core.*.nupkg `
    --source elitesoft-internal --api-key <api-key>

dotnet nuget push artifacts/EliteSoft.Erwin.AlterDdl.ComInterop.*.nupkg `
    --source elitesoft-internal --api-key <api-key>
```

## Versioning policy (SemVer)

`Version` lives in `Directory.Build.props` and applies to both packages.
Bump it before `dotnet pack`.

- `MAJOR`: breaking API change (removed/renamed public types, breaking
  JSON schema change on `Change` records).
- `MINOR`: new Change subtype, new emitter dialect, new SCAPI session
  strategy - backward compatible.
- `PATCH`: bug fix, emitter output tweak, new TODO removed.

Pre-release suffix uses the `-alpha`, `-beta`, `-rc.N` convention. Do not
push stable `x.y.z` until the integration smoke has cleared on all three
DBMS fixtures (MSSQL / Oracle / Db2).

## Release checklist

1. Update `Version` in `Directory.Build.props`.
2. Append a section to `PACKAGE_RELEASE_NOTES.md` at the repo root.
3. Run the full test suite: `dotnet test`.
4. Run a smoke on at least one fixture: see `tests/ErwinAlterDdl.Integration.Tests`.
5. `.\scripts\pack.ps1` and verify two `.nupkg` + two `.snupkg`.
6. Tag the commit: `git tag alter-ddl/v<version>` and push the tag.
7. Push the packages to the internal feed.
8. Announce in the team channel with the version + release-notes link.
