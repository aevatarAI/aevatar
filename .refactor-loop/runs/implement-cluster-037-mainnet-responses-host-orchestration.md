# implement-cluster-037-mainnet-responses-host-orchestration

## 修改文件列表

- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesEndpoints.cs` — 1039 lines
- `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandFacade.cs` — 933 lines
- `src/Aevatar.Mainnet.Host.Api/Messages/MessagesEndpoints.cs` — 296 lines
- `src/platform/Aevatar.GAgentService.Application/Responses/MessagesCommandFacade.cs` — 439 lines
- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesRouteResolver.cs` — 124 lines

## 测试结果

- `dotnet build aevatar.slnx --nologo` — passed after fix round 3
- `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --no-build` — passed, 568 passed / 0 failed / 0 skipped
- `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --no-build` — passed, 204 passed / 0 failed / 0 skipped
- `bash tools/ci/test_stability_guards.sh` — passed after fix round 3
- `bash tools/ci/architecture_guards.sh` — passed

## deviation 记录

- Facades are extracted into `Aevatar.GAgentService.Application/Responses`; Host handlers now only extract bearer/request context and map HTTP/SSE/JSON frames around typed command results. Boundary-owned model route lookup stays behind `IResponsesRouteResolver`; chat route decision is exposed to Application through `IResponsesChatRouteDecisionPort` and composed in Host with the current `ChatRouteResolver` implementation.
- `rg -n "Task\\.Delay|WaitUntilAsync" test/Aevatar.Hosting.Tests src/Aevatar.Mainnet.Host.Api` reports one pre-existing `Task.Delay` in `src/Aevatar.Mainnet.Host.Api/Voice/PolicyAwareVoiceEndpoints.cs`, outside this cluster scope; no tests were changed or added with polling waits.
- `architecture_guards.sh` reported `Playground asset drift guard: pnpm not found, skipping`, then completed successfully.

## SCOPE_EXTEND 记录

- None.

⟦AI:AUTO-LOOP⟧
