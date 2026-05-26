# fix PR #725 r2 - architect + tests reject

## Evidence fixed
- Removed the legacy production `BindLiveObservation(lease, sink, ...)` compatibility overloads from:
  - `GAgentApprovalCommandTarget`
  - `GAgentDraftRunCommandTarget`
  - `WorkflowRunCommandTarget`
- Updated direct test bindings to pass an explicit fake `IAsyncDisposable` live-sink lease.
- Updated GAgent approval and draft-run projection fakes to return a non-null recording live-sink lease from `AttachLiveSinkAsync`.
- Replaced stale `(projection lease, sink)` detach assertions with assertions that `DetachLiveSinkAsync` receives and disposes the same live-sink lease bound into the target.
- Updated workflow projection fakes so detach semantics follow the new explicit live-sink lease instead of old projection-lease side state.
- Updated GAgent projection DI tests to assert observed materializer wrapper registrations by inner projector type.

## Verification
- `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo` - pass, 499 tests
- `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo` - pass, 163 tests
- `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~GAgentApprovalInteractionTests|FullyQualifiedName~GAgentDraftRunInteractionCoverageTests"` - pass, 28 tests
- `bash tools/ci/test_stability_guards.sh` - pass
- Static scan for `Task.Delay`, `[Skip]`, and `WaitUntilAsync` in touched test/application paths - no matches

## Notes
- The requested r2 review files were not present in this worktree under `.refactor-loop/runs/`; I read the full matching files from sibling checkout `../aevatar/.refactor-loop/runs/review-pr725-{architect,tests}-r2.md` and cross-checked the same evidence in PR #725 comments.

## Status
FIX_DONE:725:r2:pass
