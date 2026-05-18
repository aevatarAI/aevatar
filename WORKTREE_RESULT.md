# Worktree Result - Issue #591 Current-State Projection Helper

## Survey

| Projector | Tag | Reason |
|---|---|---|
| `src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionCurrentStateProjector.cs` | fits-helper | Single actor-scoped current-state document from `WorkflowRunState`; no read-side lookup or aggregate behavior. Migrated. |
| `src/Aevatar.Studio.Projection/Projectors/ChatConversationCurrentStateProjector.cs` | fits-helper | Simple committed state-root mirror; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/ChatHistoryIndexCurrentStateProjector.cs` | fits-helper | Simple committed state-root mirror; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/ConnectorCatalogCurrentStateProjector.cs` | fits-helper | Simple committed state-root mirror; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/GAgentRegistryCurrentStateProjector.cs` | fits-helper | Simple committed state-root mirror; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/RoleCatalogCurrentStateProjector.cs` | fits-helper | Simple committed state-root mirror; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/StreamingProxyParticipantCurrentStateProjector.cs` | fits-helper | Simple committed state-root mirror; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/UserMemoryCurrentStateProjector.cs` | fits-helper | Simple committed state-root mirror; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/UserConfigCurrentStateProjector.cs` | fits-helper | Straight state-to-document mapping; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/StudioTeamCurrentStateProjector.cs` | fits-helper | Mostly direct mapping plus roster count derivation; left unchanged because scope requires exactly one migration. |
| `src/Aevatar.Studio.Projection/Projectors/StudioMemberBindingRunCurrentStateProjector.cs` | too-custom | Applies failure/result sub-mapping helpers and binding-run semantics. Could be migrated later with care, but not representative enough for this issue. |
| `src/Aevatar.Studio.Projection/Projectors/StudioMemberCurrentStateProjector.cs` | too-custom | Denormalizes implementation refs, binding status, optional team semantics, and failure details. |
| `src/platform/Aevatar.GAgentService.Projection/Projectors/ServiceRunCurrentStateProjector.cs` | too-custom | Uses domain key `scope/service/run`, validates record identity, and is not root-actor-id document keyed. |
| `src/platform/Aevatar.GAgentService.Projection/Projectors/LlmSessionCurrentStateProjector.cs` | too-custom | Uses response id key, validates record identity, and materializes forwarded tool-call arrays. |
| `src/platform/Aevatar.GAgentService.Projection/Projectors/ResponsesAgentToolStateCurrentStateProjector.cs` | too-custom | Materializes multiple nested collections and record timestamps. |

## Helper API

Public info type:

```csharp
public sealed record CurrentStateProjectionInfo(
    string RootActorId,
    string CommandId,
    string CorrelationId,
    long StateVersion,
    string LastEventId,
    DateTimeOffset ObservedAt,
    EventEnvelope Envelope,
    Any? ObservedPayload);
```

Registration API:

```csharp
public static IServiceCollection AddCurrentStateProjection<TContext, TState, TReadModel>(
    this IServiceCollection services,
    Func<TContext, TState, CurrentStateProjectionInfo, TReadModel> map)
    where TContext : class, IProjectionMaterializationContext
    where TState : class, IMessage<TState>, new()
    where TReadModel : class, IProjectionReadModel;
```

Implementation notes:

- The adapter implements the existing `ICurrentStateProjectionMaterializer<TContext>` pipeline contract.
- It owns `CommittedStateEventEnvelope.TryUnpackState<TState>`, timestamp resolution, framework field propagation, and `IProjectionWriteDispatcher<TReadModel>.UpsertAsync`.
- Store-level version monotonicity remains in the existing writer/evaluator path; rejected results are logged by the helper.
- `EventEnvelope` and observed payload are exposed through `CurrentStateProjectionInfo` as the escape hatch.

## Migration

Migrated `WorkflowExecutionCurrentStateProjector` to:

```csharp
services.AddCurrentStateProjection<
    WorkflowExecutionMaterializationContext,
    WorkflowRunState,
    WorkflowExecutionCurrentStateDocument>(
    static (context, state, _) => new WorkflowExecutionCurrentStateDocument
    {
        // business field mapping only
    });
```

Line-count delta:

- Old concrete projector: 70 lines.
- New workflow business mapping block plus `ResolveSuccess`: 28 lines.
- Delta: -42 lines for the representative current-state projection.

The read model schema and DTO contract were not changed.

## Verification

Passed:

- `dotnet restore aevatar.slnx --nologo`
- `dotnet build aevatar.slnx --nologo`
- `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo` (137 passed, 1 skipped)
- `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowExecutionProjectionProjectorTests|FullyQualifiedName~WorkflowProjectionMaterializationTests|FullyQualifiedName~WorkflowExecutionProjectionRegistrationTests"` (32 passed)
- `bash tools/ci/test_stability_guards.sh`
- `bash tools/ci/projection_state_version_guard.sh`
- `bash tools/ci/projection_state_mirror_current_state_guard.sh`
- `bash tools/ci/projection_route_mapping_guard.sh`
- `bash tools/docs/lint.sh`

Blocked / failed:

- `bash tools/ci/architecture_guards.sh` reached `proto_lint_guard.sh` and failed because `buf` is not installed in this environment (`buf not found`). The projection-specific guards invoked by the architecture guard pass after updating them to scan the new helper path and ignore deleted files.
- `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo` has an unrelated composition failure in `WorkflowHostingExtensionsCoverageTests.AddAevatarPlatform_ShouldRegisterWorkflowScriptingAiAndMakerBundles`: `AddOrnnSkills requires NyxIdApiClient`.
- `dotnet test aevatar.slnx --nologo` fails outside this change scope:
  - `ScriptingProjectWiringTests.AddScriptCapability_ShouldResolveCurrentBehaviorAndProjectionServices` expects direct current-state materializer instances, but DI returns observed wrappers.
  - GAgentService projection infrastructure tests expect direct projector implementation registrations, but DI returns observed wrappers.
  - `StreamingProxyCoverageTests.TerminalProjector_ShouldMaterializeCommittedTerminalSnapshot` and `TerminalProjector_ShouldIgnoreNonTerminalCommittedEvents` find no `StreamingProxyChatSessionTerminalProjector` in current-state materializers.

## Skipped

- No additional current-state projectors were migrated, per the issue's "exactly one" constraint.
- No multi-actor aggregate, timeline, graph, report, audit, search, or live event projectors were changed.
- Full architecture guard completion was not possible without `buf`.
