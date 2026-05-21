# Fix report for PR 781 round 2

## Applied
- (A) `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolExecutionContextMapper.cs:74`: extracted `FromRequestWithCallId(...)` so the typed-context fallback plus call-id rule has one implementation (addresses reviewer:quality evidence #1).
- (A) `src/Aevatar.AI.Core/Chat/ChatRuntime.cs:240`, `src/Aevatar.AI.Core/Chat/ChatRuntime.cs:423`, `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs:88`, `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs:218`: replaced the four repeated `(ToolContext ?? FromRequest).WithCallId(...)` call-building sites with the shared mapper helper (addresses reviewer:quality evidence #1).
- (A) `src/Aevatar.AI.Core/Chat/ChatRuntime.cs:455`: final no-tools DSML fallback now passes `finalRequest.ToolContext` into `StreamingToolExecutor`, so typed control facts survive after owned metadata keys are stripped (addresses reviewer:tests evidence #1).
- (A) `src/Aevatar.AI.Core/Chat/ChatRuntime.cs:471`: summary request now carries the same `CallerContext`, `ToolContext`, and `RoutingContext` as the final request, preserving the typed request contract through the post-tool summary call (addresses reviewer:tests evidence #1).
- (A) `src/Aevatar.AI.Core/Tools/StreamingToolExecutor.cs:53`: executor construction now prefers explicit typed context, then current scoped typed context, and only falls back to legacy metadata decoding at the boundary (addresses reviewer:architect evidence #3 and reviewer:tests evidence #1).
- (B) `test/Aevatar.AI.Tests/ChatRuntimeStreamingBufferTests.cs:300`: SCOPE_EXTEND reason: the reject blocks consensus and this existing test file is the behavior surface for the cited final no-tools DSML `ChatRuntime` branch. Added a regression test proving final DSML tools receive typed `NyxIdAccessToken`, `ScopeId`, `CallId`, and channel message id while provider metadata remains stripped (addresses reviewer:tests evidence #1-#2).

## Rejected as false positive
- None.

## Blocked (cannot fix this round)
- None.

## Build status
- build: pass (`dotnet build aevatar.slnx --nologo 2>&1 | tail -20`, 48 existing warnings, 0 errors)
- tests: pass (`dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~ChatRuntimeStreamingBufferTests|FullyQualifiedName~AgentToolExecutionContextMapperTests" 2>&1 | tail -10`, 28 passed)
- guards: pass (`bash tools/ci/test_stability_guards.sh`; `bash tools/ci/architecture_guards.sh`)

## Recommendation for next round
- expect unanimous

⟦AI:AUTO-LOOP⟧
