# Aevatar 全仓架构审计评分报告（重构后复评）

> 日期：`2026-03-22`
> 复评分支：`refactor/2026-03-22_architecture-audit-remediation`
> 范围：`aevatar.slnx` 全仓，覆盖 `src/`、`test/`、`tools/`、`docs/`
> 方法：六维评分模型 + 主链路代码复核 + CI 门禁/构建/测试验证
> 基线豁免：`InMemory` 实现、仅本地 Runtime、带外环境依赖的显式跳过测试

## 1. 结论

本次重构后的复评总分为：`96/100`，等级 `A`。

相对于整改前的 `81/100 (B+)`，这次复评的核心变化不是“又做了一轮局部修补”，而是把之前真正拉低分数的几类问题全部收口到了工程事实：

- 全仓 `build/test` 与分片门禁恢复为绿色。
- 架构测试从“存在已知 `Skip`”恢复到 `95 passed, 0 skipped`。
- workflow 行为契约、CQRS detached cleanup、scripting 抽象边界、命名债务都已同步到代码和测试。
- 原先会让全量测试 idle 挂起的 `Aevatar.Scripting.Core.Tests` 已定位到单个测试并修复。

当前结论已经不再是“主干架构正确但闭环未完成”，而是：

- 架构方向：`正确`
- 架构落地度：`高`
- 架构治理完成度：`高`
- 当前状态：`无阻断项，进入稳态维护区间`

## 2. 客观验证结果

| 验证项 | 命令 | 结果 | 结论 |
|---|---|---|---|
| 架构门禁总集 | `bash tools/ci/architecture_guards.sh` | 通过 | Projection / workflow / scripting / playground 相关 guard 全绿 |
| 分片构建门禁 | `bash tools/ci/solution_split_guards.sh` | 通过 | `Foundation/AI/CQRS/Workflow/Hosting/Distributed` 分片全部可构建 |
| 分片测试门禁 | `bash tools/ci/solution_split_test_guards.sh` | 通过 | 分片测试链路恢复绿色 |
| 轮询等待门禁 | `bash tools/ci/test_stability_guards.sh` | 通过 | 本次测试修改未引入新的 polling debt |
| 全量构建 | `dotnet build aevatar.slnx --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false` | 通过 | 全仓可编译 |
| 全量测试 | `dotnet test aevatar.slnx --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false` | 通过 | 之前挂起的 `Aevatar.Scripting.Core.Tests` 已恢复正常结束 |
| 架构测试 | `dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false` | 通过 | `95` 通过，`0` 跳过 |
| 脚本核心测试 | `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false` | 通过 | `387` 通过，`0` 跳过 |

补充说明：

- 分片测试和全量测试中仍存在少量显式 `SKIP` 的集成用例，但它们都属于环境依赖或外部基础设施前提，不再构成架构闭环扣分项。
- 当前仓库已经满足“变更必须可验证”的整改目标。

## 3. 整体评分

### 3.1 总分

| 项目 | 分数 | 等级 |
|---|---:|---|
| 整体架构评分 | `96/100` | `A` |

### 3.2 六维评分

| 维度 | 权重 | 得分 | 评分说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | `GAgentService.Application` 已不再绕过 abstractions 直连 `Scripting.Core`，上层依赖反转明显改善 |
| CQRS 与统一投影链路 | 20 | 18 | detached cleanup 语义已收口，统一投影链路与 guard 保持稳定；保留 2 分作为后续持续打磨空间 |
| Projection 编排与状态约束 | 20 | 20 | priming/state-version/route/binding/actor-model 相关门禁与测试闭环完整 |
| 读写分离与会话语义 | 15 | 15 | workflow `RoleId` fail-fast 语义恢复一致，query/readmodel 路径保持诚实 |
| 命名语义与冗余清理 | 10 | 10 | `WorkflowRunReport` 命名债已清理，遗留 `Skip` 规则已转回正式约束 |
| 可验证性（门禁/构建/测试） | 15 | 14 | build/test/guards 全绿；保留 1 分给现存 analyzer warning 与环境依赖型跳过测试 |

## 4. 分模块评分

| 模块 | 分数 | 结论 |
|---|---:|---|
| `Foundation + Runtime` | 92 | 基础运行时保持稳定，分布式/本地双路径均未出现新的边界倒灌 |
| `CQRS + Projection` | 93 | 主链路、版本语义和 reducer 路由治理都较稳健 |
| `Workflow` | 95 | 行为契约、导出命名、测试闭环与 host 路径全部收口 |
| `Scripting` | 94 | abstractions 边界与测试稳定性显著改善，原挂起项已消除 |
| `Platform / GAgentService` | 92 | 应用层脚本依赖边界收紧后，整体装配更干净 |
| `AI` | 88 | 当前抽样路径没有新的架构逆流，主要保持稳定 |
| `Host + Bootstrap + Tooling` | 91 | playground 资产同步与默认端口治理已经恢复一致 |
| `Docs + Guards` | 94 | 审计、门禁与验证结果现在是一致的，不再互相打架 |

## 5. 已关闭的问题

### 5.1 workflow 分片与全仓构建阻断已消除

修复点：

- `test/Aevatar.Workflow.Application.Tests/WorkflowRunActorResolverTests.cs`

结果：

- 陈旧测试类型引用已清理，`dotnet build aevatar.slnx`、`solution_split_guards.sh`、`solution_split_test_guards.sh` 全部恢复绿色。

### 5.2 playground 资产漂移已消除

修复点：

- `tools/Aevatar.Tools.Cli/wwwroot/playground/app.js`
- `tools/Aevatar.Tools.Cli/wwwroot/playground/app.css`

结果：

- `playground_asset_drift_guard.sh` 通过，`architecture_guards.sh` 不再被 tooling 漂移阻断。

### 5.3 CQRS detached cleanup 语义已收口

修复点：

- `src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs`
- `src/Aevatar.CQRS.Core/Interactions/DefaultCommandInteractionService.cs`
- `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunCommandTarget.cs`

结果：

- 去掉显式 `Task.Run` 背景 drain。
- cleanup/durable release 改为受 shutdown token 约束，不再硬编码 `CancellationToken.None`。

### 5.4 scripting 依赖边界已从 `Core` 上移到 abstractions

修复点：

- `src/Aevatar.Scripting.Abstractions/CorePorts/IScriptDefinitionCommandPort.cs`
- `src/Aevatar.Scripting.Abstractions/CorePorts/IScriptCatalogCommandPort.cs`
- `src/Aevatar.Scripting.Abstractions/CorePorts/IScriptCatalogQueryPort.cs`
- `src/platform/Aevatar.GAgentService.Application/Aevatar.GAgentService.Application.csproj`

结果：

- `GAgentService.Application` 不再通过 `Scripting.Core` 暴露的端口完成上层装配，依赖反转恢复一致。

### 5.5 workflow `RoleId` 契约重新统一为 fail-fast

修复点：

- `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs`

结果：

- 缺失 `RoleId` 时恢复抛错，不再出现“同一条链路里一部分 warning 跳过、一部分 fail-fast”的双重语义。

### 5.6 workflow 导出 DTO 命名债已消除

修复点：

- `src/workflow/Aevatar.Workflow.Application.Abstractions/Queries/WorkflowExecutionQueryModels.cs`
- `src/workflow/Aevatar.Workflow.Application.Abstractions/Reporting/IWorkflowRunReportExportPort.cs`
- `src/workflow/Aevatar.Workflow.Infrastructure/Reporting/WorkflowRunReportExportWriter.cs`

结果：

- 旧的 `WorkflowRunReport` 已改为 `WorkflowRunExportDocument`，导出链路语义与命名治理保持一致。

### 5.7 架构规则中的陈旧 `Skip` 已清除

修复点：

- `test/Aevatar.Architecture.Tests/Rules/NamingConventionTests.cs`
- `test/Aevatar.Architecture.Tests/Rules/ActorModelConstraintTests.cs`

结果：

- Architecture Tests 从 `93 passed, 2 skipped` 提升到 `95 passed, 0 skipped`。

### 5.8 `Aevatar.Scripting.Core.Tests` 挂起根因已消除

修复点：

- `test/Aevatar.Scripting.Core.Tests/Compilation/ScriptArtifactCoverageTests.cs`

结果：

- 把错误假设“并发同 key resolve 会触发两次 compile”的测试，改成符合 `ConcurrentDictionary + Lazy` 真实语义的 in-flight compile 共享测试。
- `dotnet test test/Aevatar.Scripting.Core.Tests/...` 和 `dotnet test aevatar.slnx ...` 均已正常结束。

## 6. 正向证据

### 6.1 当前门禁已经不再是“多数规则绿，少数规则靠跳过”

正向证据：

- `architecture_guards.sh` 通过。
- `Aevatar.Architecture.Tests` 通过且 `0 skipped`。
- `test_stability_guards.sh` 通过。

结论：

- 治理规则的权威性已经恢复，不再依赖“已知例外”维持表面通过。

### 6.2 workflow 主链路的实现与契约重新一致

正向证据：

- `WorkflowRunGAgent` 对缺失 `RoleId` 恢复 fail-fast。
- workflow 导出链路的命名与职责从“报告对象”明确收敛到“导出文档”。

结论：

- Workflow 现在不只是在代码上可运行，也在契约和命名层面可推理。

### 6.3 scripting 的稳定性问题已经从“运行挂起”降到“正常可回归”

正向证据：

- `Aevatar.Scripting.Core.Tests` 全量 `387` 项通过。
- 全仓 `dotnet test aevatar.slnx` 已顺利穿过之前会卡住的脚本测试节点。

结论：

- 当前脚本能力已经从“存在测试黑洞”恢复到可纳入稳态 CI 的状态。

## 7. 当前残余观察项

以下内容本次不再计为阻断或主要扣分项，但建议持续清理：

- `dotnet build` 仍有少量 analyzer warning，集中在 `Aevatar.GAgentService.Hosting` 与 `Aevatar.Tools.Cli`。
- 少数分布式/外部依赖测试在缺少环境前提时显式跳过，这属于可接受的环境条件差异，不等价于架构闭环缺失。
- 当前工作树仍未提交 commit；这是交付流程问题，不是架构完成度问题。

## 8. 最终判断

本次复评的核心判断是：

- 主干架构：`成立`
- 分层与边界：`基本收口`
- 统一投影与读写分离：`可验证`
- 命名与治理规则：`恢复权威`
- 工程闭环：`已完成`

综合评分更新为：`96/100 (A)`。
