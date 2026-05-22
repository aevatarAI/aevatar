# implement-cluster-037-mainnet-responses-host-orchestration

## 修改文件列表

- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesEndpoints.cs` — 1520 lines
- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesCommandFacade.cs` — 522 lines
- `src/Aevatar.Mainnet.Host.Api/Messages/MessagesEndpoints.cs` — 358 lines
- `src/Aevatar.Mainnet.Host.Api/Messages/MessagesCommandFacade.cs` — 316 lines

## 测试结果

- `dotnet build aevatar.slnx --nologo` — passed
- `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo` — passed, 204 passed / 0 failed / 0 skipped
- `bash tools/ci/test_stability_guards.sh` — passed
- `bash tools/ci/architecture_guards.sh` — passed

## deviation 记录

- Facades are extracted into separate Mainnet Responses/Messages files and handlers now only extract bearer/request context before delegating. The facade remains in the Host Api assembly and partial endpoint type to preserve existing boundary-owned SSE/JSON shaping helpers without broad helper visibility churn or behavior changes.
- `rg -n "Task\\.Delay|WaitUntilAsync" test/Aevatar.Hosting.Tests src/Aevatar.Mainnet.Host.Api` reports one pre-existing `Task.Delay` in `src/Aevatar.Mainnet.Host.Api/Voice/PolicyAwareVoiceEndpoints.cs`, outside this cluster scope; no tests were changed or added with polling waits.
- `architecture_guards.sh` reported `Playground asset drift guard: pnpm not found, skipping`, then completed successfully.

## SCOPE_EXTEND 记录

- None.

⟦AI:AUTO-LOOP⟧
