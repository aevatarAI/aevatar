# Worktree Result

## Summary

Implemented issue #641 for `ResponsesAgentToolStateGAgent` actor ids.

The default behavior is unchanged: `FeatureFlags:AevatarResponsesAgentToolReadableIds=false` keeps the legacy `responses-agent-tools-{sha256}` id byte-for-byte. When the flag is enabled, actor ids use:

```text
responses-agent-tools/scope:{percent_encoded_scope_id}/owner:{percent_encoded_owner_subject}
```

Readable ids are capped at 512 characters with deterministic truncation and a 16-hex SHA-256 tail. Query reads try readable id first, then legacy hash id. Command writes with the flag enabled reuse an existing legacy actor during the 30-day dual-read window to avoid splitting state.

## Files Changed

- `src/platform/Aevatar.GAgentService.Abstractions/ResponseAgentToolStateIds.cs`
- `src/platform/Aevatar.GAgentService.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/platform/Aevatar.GAgentService.Infrastructure/Aevatar.GAgentService.Infrastructure.csproj`
- `src/platform/Aevatar.GAgentService.Infrastructure/Adapters/ResponsesAgentToolStateCommandAdapter.cs`
- `src/platform/Aevatar.GAgentService.Projection/Aevatar.GAgentService.Projection.csproj`
- `src/platform/Aevatar.GAgentService.Projection/Queries/ResponsesAgentToolStateQueryReader.cs`
- `test/Aevatar.GAgentService.Tests/Abstractions/ResponseAgentToolStateIdsTests.cs`
- `test/Aevatar.GAgentService.Tests/Infrastructure/ResponsesAgentToolStateCommandAdapterTests.cs`
- `test/Aevatar.GAgentService.Tests/Projection/ResponsesAgentToolStateCurrentStateProjectorTests.cs`
- `docs/adr/0024-responses-agent-tool-actor-id-scheme.md`

## Tests Added

- Round-trip coverage for slashes, colons, whitespace, UTF-8 emoji, and control chars.
- RFC 3986 percent-encoding expectation.
- 512-character cap and deterministic truncation coverage.
- Feature-flag off legacy hash coverage.
- Feature-flag on readable id coverage.
- Readable-id query fallback to legacy hash id.
- Readable-id command path reuse of existing legacy actor.

## ADR

- Assigned ADR number: `0024`
- Path: `docs/adr/0024-responses-agent-tool-actor-id-scheme.md`

## Verification

Passed:

- `dotnet restore aevatar.slnx --nologo`
- `dotnet build aevatar.slnx --nologo`
- `dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj --nologo`
- `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --filter "ResponseAgentToolStateIdsTests|ResponsesAgentToolStateCommandAdapterTests|ResponsesAgentToolStateCurrentStateProjectorTests" --nologo`
- `bash tools/ci/test_stability_guards.sh`
- `git diff --check`

Not clean:

- `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo`
  - Failed 3 existing projection DI registration assertion tests:
    - `ServiceServingProjectionInfrastructureTests.AddGAgentServiceProjection_ShouldRegisterServingProjectionServices`
    - `ServiceConfigurationProjectionInfrastructureTests.AddGAgentServiceGovernanceProjection_ShouldRegisterGovernanceProjectionServices`
    - `ServiceProjectionInfrastructureTests.AddGAgentServiceProjection_ShouldRegisterProjectionServices`
  - The failures expect raw projector implementation registrations, while the current service collection contains observed materializer wrappers. These tests are outside the actor id change.
- `bash tools/ci/architecture_guards.sh`
  - Passed before the proto lint step:
    - query projection priming guard
    - scripting write-path CQRS guard
    - projection state version guard
    - projection state mirror current-state guard
    - agent kind naming guard
  - Failed because `buf` is not installed: `buf is required to lint proto contracts.`

## Surprises

- `TASK.md` was already untracked in the worktree and was left untouched.
- Full restore/build emitted existing package vulnerability, pruning, obsolete API, nullable, and analyzer warnings.
