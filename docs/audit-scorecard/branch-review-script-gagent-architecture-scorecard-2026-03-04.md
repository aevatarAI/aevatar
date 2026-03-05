# Branch 复审评分卡（script-gagent-architecture-change，2026-03-04）

## 1. 复审范围

- 分支：`feat/script-gagent-architecture-change`（含当前工作区未提交改动）。
- 核心审计对象：
  - `src/Aevatar.Scripting.Core/*`
  - `src/Aevatar.Scripting.Infrastructure/*`
  - `src/Aevatar.Scripting.Application/*`
  - `src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/*`
  - 对应测试与守卫脚本改动。

## 2. 本次验证（实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| Scripting Core 关键回归 | `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "RuntimeScriptEvolutionFlowPortTests|ScriptRuntimeGAgentEventDrivenQueryTests|ScriptDefinitionGAgentReplayContractTests|ScriptEvolutionManagerGAgentTests|ScriptCatalogGAgentTests"` | 通过（23/23） |
| Integration 关键回归 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "ScriptDefinitionRuntimeContractTests|ClaimScriptDocumentDrivenFlexibilityTests|WorkflowYamlScriptParityTests" --no-build` | 通过（10/10） |
| Workflow Host 覆盖 | `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --filter "WorkflowHostingExtensionsCoverageTests"` | 通过（9/9） |
| Bootstrap 覆盖 | `dotnet test test/Aevatar.Bootstrap.Tests/Aevatar.Bootstrap.Tests.csproj --filter "AIFeatureBootstrapCoverageTests"` | 通过（7/7） |

## 3. 主要发现（按严重级别）

### High

1. **运行失败会覆盖 Runtime 的“活动定义/版本”状态，可能导致状态语义漂移。**
   - 位置：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
   - 现状：失败路径统一落 `ScriptRunDomainEventCommitted(event_type=script.run.failed)`，而 `ApplyCommitted` 会无条件写回 `DefinitionActorId` 与 `Revision`。
   - 风险：当失败由“定义缺失/修订不匹配”触发时，Runtime 实际并未成功切换到请求版本，但状态会被改写为请求值，出现“payload 仍是旧事实、revision/definition 已是新值”的语义不一致。
   - 当前测试已固化此行为（`ScriptDefinitionRuntimeContractTests`），但从状态语义看这是高风险回归点。

### Medium

2. **`AddWorkflowCapabilityWithAIDefaults()` 默认行为发生破坏性变化（默认不再注册 Script capability）。**
   - 位置：
     - `src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/WorkflowCapabilityHostBuilderExtensions.cs`
     - `src/Aevatar.Mainnet.Host.Api/Program.cs`
     - `src/workflow/Aevatar.Workflow.Host.Api/Program.cs`
   - 现状：新增 `includeScriptCapability = false`，现有调用点未显式传 `true`，实际部署将默认关闭 Script capability。
   - 风险：对既有“仅调用 `AddWorkflowCapabilityWithAIDefaults()` 即可同时获得脚本能力”的环境属于行为变更；若没有显式迁移提示，容易形成运行时能力缺口。

3. **执行编排器仅 `await using`，未覆盖 `IDisposable` 释放路径。**
   - 位置：`src/Aevatar.Scripting.Application/Runtime/ScriptRuntimeExecutionOrchestrator.cs`
   - 现状：`ExecuteRunAsync` 对编译产物只做 `IAsyncDisposable` 释放。
   - 风险：若后续接入的 `IScriptPackageDefinition` 实现仅支持 `IDisposable`，将出现资源释放不完整；同仓内 `ScriptDefinitionGAgent` 与 `RuntimeScriptEvolutionFlowPort` 已做双路径释放，当前实现不一致。

## 4. 评分

**84 / 100（B+）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 正确性与行为一致性 | 35 | 29 | 失败路径状态改写带来高风险语义漂移。 |
| 架构与边界一致性 | 20 | 17 | Actor 化与事件化方向正确；但 Host 默认装配语义变化较大。 |
| 稳定性与可恢复性 | 20 | 16 | 新增补偿回滚与查询超时机制是加分项；释放路径一致性仍有缺口。 |
| 测试有效性 | 15 | 13 | 新增覆盖较充分，但对“失败不改活动版本”这类语义护栏尚未建立。 |
| 可运维与可迁移性 | 10 | 9 | 文档有更新；默认行为变化建议补明确迁移公告/开关策略。 |

## 5. 结论

- 这轮改动在“事件驱动查询、补偿回滚、资源释放意识、测试覆盖”上有明显进步。
- 当前阻碍高分的核心是：`ScriptRuntimeGAgent` 失败路径对 Runtime 活动状态的写回语义。
- 建议先修复 High 项，再补两项 Medium 的兼容/一致性处理；预计可提升到 A 档（90+）。
