# Fix report for PR 773 round 1

## Applied
- (A) `src/Aevatar.AI.Core/RoleGAgent.cs:211`: bounded the remote approval lifecycle by persisting a 45s default deadline when NyxID does not return one, and by deriving a max status-check attempt count from the 2s interval plus 45s window (addresses reviewer:architect evidence #1 and #2).
- (A) `src/Aevatar.AI.Core/RoleGAgent.cs:305`: terminally fails and clears pending approval with `approval_timeout` once a `Pending` or `Unknown` status reaches the persisted deadline or max attempt count, while preserving stale-event checks for `request_id + session_id + remote_approval_id + attempt` (addresses reviewer:architect evidence #1).
- (A) `src/Aevatar.AI.Core/RoleGAgent.cs:953`: deleted the now-dead `ResolveApprovalTerminalReasonCode` helper after explicit timeout/denied branches replaced its only old caller (addresses reviewer:quality evidence #1).
- (A) `src/Aevatar.AI.Abstractions/ai_messages.proto:146`: removed and reserved `remote_status_check_callback_id` from `PendingToolApprovalState`; production still builds the scheduler callback id locally from request/remote/attempt and no longer persists unused state surface (addresses reviewer:quality evidence #2).
- (A) `src/Aevatar.AI.Abstractions/ai_messages.proto:171`: removed and reserved `status_check_callback_id` from `RemoteToolApprovalSubmittedEvent`, then removed all test assertions and production assignments for that unused event field (addresses reviewer:quality evidence #2).
- (A) `test/Aevatar.AI.Tests/RoleGAgentStateCoverageTests.cs:391`: added submit-exception coverage proving remote submit failure persists an `approval_timeout` terminal result and clears pending approval (addresses reviewer:tests evidence #1).
- (A) `test/Aevatar.AI.Tests/RoleGAgentStateCoverageTests.cs:465`: added status-callback missing-port coverage proving the callback branch persists timeout failure and clears pending approval (addresses reviewer:tests evidence #2).
- (A) `test/Aevatar.AI.Tests/RoleGAgentStateCoverageTests.cs:496`: added status-exception coverage proving `GetStatusAsync` exceptions become `Unknown`, preserve pending approval binding, advance exactly one attempt, and schedule exactly one next durable self-check (addresses reviewer:tests evidence #3).
- (A) `test/Aevatar.AI.Tests/RoleGAgentStateCoverageTests.cs:536`: added max-attempt `Unknown` coverage proving unending unknown status terminally times out, clears pending approval, and does not schedule another callback (addresses reviewer:architect evidence #1 and reviewer:tests evidence #3).

## Rejected as false positive
- None.

## Blocked (cannot fix this round)
- None.

## Build status
- build: pass (`dotnet build aevatar.slnx --nologo 2>&1 | tail -20`; 0 errors, existing warnings only).
- tests: pass (`dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-build 2>&1 | tail -10`; 585 passed, 0 failed, 0 skipped).
- guard: pass (`bash tools/ci/test_stability_guards.sh`).

## Recommendation for next round
- expect unanimous

⟦AI:AUTO-LOOP⟧
