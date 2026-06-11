# implement-iter164-cluster-001-role-completion

## Scope

- Cluster: `cluster-001-role-completion-publish-before-commit`
- Worktree: `/Users/auric/aevatar-wt-iter164-cluster-001-role-completion`
- Branch: `refactor/iter164-cluster-001-role-completion`

## Summary

- Updated `RoleGAgent.HandleChatRequest` so `RoleChatSessionCompletedEvent` is committed before terminal presentation frames are published.
- Removed the old publish-first / catch-commit-failure degradation path for role session completion.
- Kept terminal `TextMessageEndEvent` and missing display content publish after successful completion commit.
- Added focused regression coverage for:
  - commit happens before terminal publish;
  - completion commit failure does not publish terminal presentation frames.

## Old / New

Old pattern:

- `RoleGAgent` published `TextMessageEndEvent` before persisting `RoleChatSessionCompletedEvent`.
- Completion commit failure was logged as replay-only degradation after the response had already been externally published.

New principle:

- `RoleChatSessionCompletedEvent` is the committed business fact.
- Terminal presentation frames are emitted only after that fact commits successfully.
- If completion commit fails, terminal presentation frames are not published.

## Validation

- `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter FullyQualifiedName~RoleGAgentReplayContractTests`
  - Passed: 18 tests.
- `dotnet build aevatar.slnx --nologo`
  - Passed with existing warnings.
- `dotnet test aevatar.slnx --nologo --no-build`
  - Passed across solution test projects; existing conditional skips remained.
- `bash tools/ci/architecture_guards.sh`
  - Passed.
- `bash tools/ci/test_stability_guards.sh`
  - Passed.

⟦AI:AUTO-LOOP⟧
