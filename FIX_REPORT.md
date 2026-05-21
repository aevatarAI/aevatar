# Fix report for PR 795 round 1

## Applied
- (A) `src/Aevatar.CQRS.Core/Interactions/DefaultCommandInteractionService.cs:51`: moved interactive command execution to `Prepare -> Observe -> DispatchPrepared -> Receipt refresh -> Accepted callback -> Pump -> Release`, so observation binding failure prevents dispatch and accepted is only emitted after mailbox admission (addresses reviewer:architect evidence #1 and #2).
- (A) `src/Aevatar.CQRS.Core.Abstractions/Interactions/ICommandObservationLifecycle.cs:5`: added explicit `ICommandObservationLifecycle<TCommand,TTarget,TReceipt,TError>` and `CommandObservationBindingResult<TError>` contracts, separating live projection/session observation from `ICommandTargetBinder` and dispatch-only command admission (addresses reviewer:architect evidence #4).
- (A) `src/Aevatar.CQRS.Core/Commands/DefaultCommandDispatchPipeline.cs:31`: kept `DispatchAsync` honest for dispatch-only callers by making it prepare and dispatch only; it no longer starts projection/read-model activation or returns post-dispatch observation failures as command-start failures (addresses reviewer:architect evidence #1).
- (B) `docs/canon/cqrs-projection.md:71`: SCOPE_EXTEND reason: architect reject cited missing canonical documentation for the changed CQRS command lifecycle; documented `Observe Result`, the pre-dispatch observation lifecycle rule, and the ban on live projection/session attach in `ICommandTargetBinder`.
- (B) `test/Aevatar.CQRS.Core.Tests/CqrsCoreDefaultsTests.cs:93`: SCOPE_EXTEND reason: tests reject required direct CQRS core regression coverage; added tests proving `PrepareAsync` creates target/context/envelope/receipt without binding or dispatching, and `DispatchAsync` does not call target binders.
- (B) `test/Aevatar.CQRS.Core.Tests/DefaultCommandInteractionServiceTests.cs:14`: SCOPE_EXTEND reason: tests reject required direct coverage of the new shared interaction lifecycle; added tests proving observation binds before dispatch, accepted uses the refreshed receipt, and observation bind failure returns failure without dispatch or accepted callback.

## Rejected as false positive
- None.

## Blocked (cannot fix this round)
- None.

## Build status
- build: pass (`dotnet build aevatar.slnx --nologo`; existing warnings only)
- tests: pass (`dotnet test test/Aevatar.CQRS.Core.Tests/Aevatar.CQRS.Core.Tests.csproj --nologo --no-build`: 42 passed; `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-build`: 592 passed; `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --no-build`: 182 passed; `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --no-build`: 389 passed; `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --no-build`: 523 passed; `dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --no-build`: 274 passed)
- guards: pass (`bash tools/ci/test_stability_guards.sh`; `bash tools/ci/query_projection_priming_guard.sh`)

## Recommendation for next round
- expect unanimous

⟦AI:AUTO-LOOP⟧
