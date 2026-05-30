# Implementation Report

## Scope

- Added repository-wide .NET version properties to `Directory.Build.props`.
- Added a fail-fast static guard in `tools/ci/architecture_guards.sh` to require:
  - `<Version>0.1.0-beta</Version>`
  - `<VersionPrefix>0.1.0</VersionPrefix>`
  - `<VersionSuffix>beta</VersionSuffix>`
- Phase 9 #1395 first slice: honest boundary contract for `StudioTeamEntryMemberResolver`.

## Changes

- Updated `Directory.Build.props` with repository-wide .NET version properties.
- Updated `tools/ci/architecture_guards.sh` with a fail-fast static guard for required version properties.
- Added XML contract documentation to `ITeamEntryMemberResolver` and `TeamEntryMemberResolution` clarifying that the resolver is command target resolution only.
- Added XML documentation to `StudioTeamEntryMemberResolver` clarifying it reads team/member read models only for command admission and dispatch target selection.
- Added resolver tests that assert:
  - team and member read models are read only through direct `GetAsync` calls for command target admission;
  - the resolution DTO remains limited to `ScopeId`, `TeamId`, `EntryMemberId`, and `PublishedServiceId`;
  - no composite team status/readiness or invented freshness/version field is returned.

## Boundary Notes

- No `ScopeWorkflowSummary` changes.
- No actor type, envelope kind, projection phase, aggregate actor, read model, or proto field was added.
- Existing `UpdatedAt` fields on team/member responses were not copied into `TeamEntryMemberResolution`; there was no existing team/member `StateVersion` in this resolver path to pass through.
- No `docs/canon/*` files were modified.

## Verification

```bash
dotnet build aevatar.slnx --nologo 2>&1 | tail -3
```

Result:

```text
    0 个错误

已用时间 00:00:26.03
```

```bash
dotnet test aevatar.slnx --nologo --no-build 2>&1 | tail -20
```

Result: passed. The output tail reported successful test assemblies, including:

```text
已通过! - 失败:     0，通过:   916，已跳过:     0，总计:   916，持续时间: 8 s - Aevatar.GAgents.ChannelRuntime.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   404，已跳过:     0，总计:   404，持续时间: 5 s - Aevatar.Workflow.Host.Api.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   696，已跳过:     0，总计:   696，持续时间: 35 s - Aevatar.AI.Tests.dll (net10.0)
```

```bash
bash tools/ci/architecture_guards.sh 2>&1 | tail -10
```

Result:

```text
Scripting runtime snapshot guard passed.
Running runtime callback guards...
Runtime callback guards passed.
Running channel card literal guard...
channel_card_literal_guard: ok
Running Nyx relay replay authority guard...
Running docs lint guard...

docs lint: PASSED — 50 file(s) checked, 0 errors
Architecture guards passed.
```

```bash
bash tools/ci/test_stability_guards.sh
```

Result: passed.

```bash
dotnet test aevatar.slnx --nologo --no-build 2>&1 | tail -30
```

Result: passed; the command exited with code 0 and the tailed output showed only passing test assemblies.
⟦AI:AUTO-LOOP⟧
