# PR16 Workflow LLM Streaming Review 与修复记录（2026-02-26）

## 1. 审计范围与方法

1. 审计对象：PR #16（`origin/dev...refactor/workflow-llm-streaming`）。
2. 聚焦范围：Workflow WebSocket 协议链路、AI streaming provider、`ChatRuntime`/`RoleGAgent` tool-call 聚合。
3. 证据来源：分支代码 diff、目标文件逐行复核、定向测试回归结果。

## 2. 发现摘要（按严重度）

| 严重度 | 问题 | 影响 |
|---|---|---|
| High | streaming provider 在 delta 缺失 `callId` 时生成随机 GUID。 | 同一次 tool call 可能被拆成多个 ID，导致参数增量拼接断裂。 |
| Medium | 聚合器未处理“先匿名片段，后补真实 ID”的归并。 | 同一次工具调用可产生重复/分裂的最终结果。 |
| Medium | 测试未覆盖 `callId` 缺失与 late-id 场景。 | CI 无法阻断上述回归。 |

## 3. 根因定位

### 3.1 Provider 层 delta ID 语义混淆

- `MEAILLMProvider` 与 `TornadoLLMProvider` 共用“完整响应转换”策略到流式 delta：当 `callId` 缺失时直接生成随机 GUID。
- 这对完整响应是可接受兜底，但对 streaming delta 会破坏“同一调用的连续片段”语义。

### 3.2 聚合器仅按当前片段 key 路由，未实现匿名到真实 ID 的提升

- 现有聚合策略在匿名阶段使用 `anon:n`；当后续片段带真实 ID 时切到 `id:<realId>`。
- 缺少“把已有匿名聚合提升到真实 ID”步骤，导致同一调用被拆成两个聚合桶。

## 4. 修复策略

1. **Provider 分离转换语义**
   - 新增“流式 delta 转换”与“完整响应转换”的分离实现。
   - 对 delta：`callId` 缺失时保留空值语义（`string.Empty`），不再生成随机 GUID。
   - 对完整响应：保留兜底 ID 策略，避免影响同步调用路径。

2. **统一聚合器并支持匿名提升**
   - 抽出共享 `StreamingToolCallAccumulator`，供 `ChatRuntime` 与 `RoleGAgent` 共同使用。
   - 引入“active anonymous -> real id promote”逻辑，确保同一调用只保留一个最终聚合结果。

3. **补回归测试**
   - 补充 provider delta 缺失 `callId` 场景。
   - 补充 runtime 对“先匿名后补 ID + 参数分段拼接”的断言。

## 5. 修复后预期行为

1. streaming 场景下，provider 不再人为制造新的 tool-call ID。
2. 聚合器可将匿名片段正确并入后续真实 ID。
3. 同一次工具调用最终只产生一条聚合结果，参数 JSON 保持顺序拼接。
4. 新增测试覆盖上述边界，后续回归可被 CI 捕获。

## 6. 验证清单

已执行以下定向回归并全部通过：

1. `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --tl:off`
   - 结果：Passed（53/53）
2. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --tl:off`
   - 结果：Passed（165/165）
3. `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --tl:off`
   - 结果：Passed（39/39）

## 7. 结论

本次整改目标是“修复行为一致性 + 补测试防回归”，不改变既有 API 契约，不引入额外基础设施依赖。修复完成后应消除本次 review 中的 High/Medium 问题。

## 8. 修复实现方式（已落地）

### 8.1 Provider 层：区分 delta 转换与完整响应转换

1. 在 `src/Aevatar.AI.LLMProviders.MEAI/MEAILLMProvider.cs` 中新增 `ConvertFunctionCallDelta`：
   - `ChatStreamAsync` 改用 delta 专用转换。
   - 当流式 `callId` 缺失时不再生成随机 GUID，改为保留空值（`string.Empty`）。
2. 在 `src/Aevatar.AI.LLMProviders.Tornado/TornadoLLMProvider.cs` 中新增 `ConvertToolCallDelta`：
   - 流式分支改为调用 delta 专用转换，保留空 ID 语义。
   - 非流式 `MapResponse` 仍使用 `ConvertToolCall`，继续保留完整响应的兜底 ID 行为。
3. 结果：provider 不再在 streaming delta 阶段“制造新 ID”，避免同一次调用在上游就被拆分。

### 8.2 聚合层：实现“匿名片段 -> 真实 ID”提升归并

1. 新增共享聚合器 `src/Aevatar.AI.Core/Chat/StreamingToolCallAccumulator.cs`，替代两处重复实现。
2. 关键策略：
   - 匿名片段先落入 active anonymous 聚合桶；
   - 后续若出现真实 ID，且该 ID 尚未建桶，则将 active anonymous 聚合桶提升为真实 ID 桶；
   - 保留参数追加顺序，确保 `ArgumentsJson` 按流式顺序拼接。
3. 接入位置：
   - `src/Aevatar.AI.Core/Chat/ChatRuntime.cs`
   - `src/Aevatar.AI.Core/RoleGAgent.cs`
4. 结果：同一次工具调用最终只输出一条聚合结果，消除分裂与重复。

### 8.3 测试层：补齐缺失 ID 与 late-id 回归场景

1. `test/Aevatar.AI.Tests/ChatRuntimeStreamingBufferTests.cs`
   - 新增“先匿名后补 ID + 参数分段拼接”用例；
   - 断言最终只保留 1 条 tool call，且 ID/名称/参数均正确。
2. `test/Aevatar.AI.Tests/AIComponentCoverageTests.cs`
   - 新增 MEAI 流式缺失 ID 场景断言；
   - 新增 Tornado `ConvertToolCallDelta` 的空 ID 语义断言。
3. 结果：本次修复点已由自动化用例覆盖，并在第 6 节回归测试中验证通过。
