# Fix report for PR 781 round 1

## Applied
- (A) `src/Aevatar.AI.ToolProviders.ChronoStorage/Tools/ChronoFileReadTool.cs:44`, `src/Aevatar.AI.ToolProviders.Lark/Tools/LarkMessagesSendTool.cs:31`, `src/Aevatar.AI.ToolProviders.Web/Tools/WebSearchTool.cs:48`, `agents/Aevatar.GAgents.Authoring.Lark/AgentBuilderTool.cs:67`: added iter24/cluster-002 Old/New comments beside the migrated typed-context credential reads (addresses reviewer:architect evidence #1-#3).
- (A) `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs:317`: deleted dead `BuildPerCallMetadata`, removing the old call-id-in-metadata helper after `ToolContext.Request.CallId` migration (addresses reviewer:quality evidence #1).
- (A) `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolExecutionContextMapper.cs:122`: deleted no-caller public `IsOwnedControlKey`; `OwnedControlKeys` remains private to `StripOwnedControlKeys` (addresses reviewer:quality evidence #2).
- (B) `test/Aevatar.AI.Tests/AgentToolExecutionContextMapperTests.cs`: SCOPE_EXTEND reason: reviewer:tests blocked consensus on net-new public mapper/scope behavior, and focused tests in the affected Aevatar.AI test project are the same logical refactor. Added `FromRequest` typed-over-legacy precedence coverage for request id, caller context, credentials, routing context, and external metadata stripping (addresses reviewer:tests evidence #1).
- (B) `test/Aevatar.AI.Tests/AgentToolExecutionContextMapperTests.cs`: added legacy channel alias fallback coverage for `platform`, `sender_id`, `lark.open_id`, `message_id`, and `lark.message_id` (addresses reviewer:tests evidence #2).
- (B) `test/Aevatar.AI.Tests/AgentToolExecutionContextMapperTests.cs`: added invalid and blank `MaxToolRoundsOverride` coverage returning `null` (addresses reviewer:tests evidence #3).
- (B) `test/Aevatar.AI.Tests/AgentToolExecutionContextMapperTests.cs`: added nested `AgentToolContextScope` restoration coverage (addresses reviewer:tests evidence #4).
- (B) `test/Aevatar.AI.Tests/AgentToolExecutionContextMapperTests.cs`: added source-regression coverage asserting production `src/` and `agents/` do not call `AgentToolRequestContext.CurrentMetadata` or `AgentToolRequestContext.TryGet(` outside the shim definition, matching the architecture guard contract and fencing the legacy public shim to mapper/test boundary use (addresses reviewer:architect evidence #4 and reviewer:tests evidence #5).

## Rejected as false positive
- None.

## Blocked (cannot fix this round)
- None.

## Build status
- build: pass (`dotnet build aevatar.slnx --nologo 2>&1 | tail -20`)
- tests: pass (`dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-build 2>&1 | tail -10`, 594 passed)
- guards: pass (`bash tools/ci/test_stability_guards.sh`; `bash tools/ci/architecture_guards.sh`)

## Recommendation for next round
- expect unanimous

⟦AI:AUTO-LOOP⟧
