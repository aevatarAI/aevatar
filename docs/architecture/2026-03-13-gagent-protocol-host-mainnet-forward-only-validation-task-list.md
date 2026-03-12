# GAgent 协议优先收尾任务单：Host / Mainnet 前滚升级与来源无关验收（2026-03-13）

## 1. 文档元信息

- 状态：Completed
- 版本：Final
- 日期：2026-03-13
- 关联文档：
  - `docs/architecture/2026-03-13-gagent-protocol-series-closeout.md`
  - `docs/architecture/2026-03-12-gagent-implementation-source-unification-blueprint.md`
  - `docs/FOUNDATION.md`
  - `docs/SCRIPTING_ARCHITECTURE.md`
  - `AGENTS.md`
- 文档定位：
  - 本文是 `gagent-protocol` 主重构完成后的最后一项收尾任务记录。
  - 本文不再要求继续重构 Foundation / Workflow / Scripting 主链，只记录 Host / Mainnet 路径上的显式验收与归档证据。

## 2. 背景

截至当前代码状态：

1. phase 1-5 主重构已经完成
2. Host / Application 不按 `actorId` 字面模式做来源分支的约束已经写入守卫
3. Foundation / Workflow / Scripting 主干边界已经稳定

本次收尾补齐的不是新的架构改造，而是以下显式验收材料：

1. forward-only upgrade 在 Host / Mainnet 路径上的可复核验证
2. source-agnostic 行为在 Host / Mainnet 层的对外验收
3. 相关文档从 `Proposed` 真正归档到最终状态

## 3. 本任务单的目标

本任务单实际完成了三件事：

1. 为 Host / Mainnet 补齐 forward-only upgrade 验证
2. 为 Host / Mainnet 补齐 source-agnostic acceptance 验证
3. 将 `implementation source unification` 系列文档正式归档

## 4. 完成定义

本任务完成后，已经满足：

1. 至少有一组 Host / Mainnet 级别验证，证明“旧 run 留旧实现，新 run 走新实现”
2. 至少有一组 Host / Mainnet 级别验证，证明调用方只依赖协议与 `actorId`，不依赖 workflow/script/static 来源判断
3. `docs/architecture/2026-03-12-gagent-implementation-source-unification-blueprint.md` 不再保持 `Proposed`
4. 最终文档能明确写出：主重构已完成，剩余事项仅为验证与归档

## 5. 非目标

本任务不做以下内容：

1. 不继续新增 Foundation 公共抽象
2. 不重做 `EnvelopeRoute`
3. 不重做 `Scripting Evolution`
4. 不新增统一 `implementation_kind/source_binding` 模型
5. 不要求热替换存量 run

## 6. 任务清单

## T1. 梳理 Host / Mainnet 当前 source-agnostic 路径

### 目标

确认当前 Host / Mainnet 代码已经满足“不按来源分支”的既有设计，并把证据固化到文档里。

### 任务

1. 盘点 `Aevatar.Mainnet.Host.Api`、`Aevatar.Workflow.Host.Api`、相关 Application 层的目标解析与通信路径
2. 记录哪些地方只依赖协议、typed resolver、`actorId`
3. 记录哪些现有 guard 已经覆盖 “不得解析 actorId / 不得按来源分支”

### 验收

1. 有一节明确的 source-agnostic 现状说明
2. 有对应代码位置与 guard 位置

## T2. 增加 forward-only upgrade 验证

### 目标

把“旧 run 留旧实现，新 run 走新实现”从架构原则提升为显式验收。

### 任务

1. 选一个最小可验证协议样本
2. 设计两代实现的前滚场景
3. 验证旧 run 不会被原地替换
4. 验证新 run 会走新实现

### 验收

1. 至少一组自动化测试或等价可复核验证命令
2. 文档写清楚验证前提、步骤和期望结果

## T3. 增加 Host / Mainnet 层 cross-source acceptance 验证

### 目标

证明 Host / Mainnet 侧真正只依赖协议与实例地址，而不是内部来源。

### 任务

1. 选一个已有 cross-source protocol 样本
2. 在 Host / Mainnet 路径上验证至少两种不同来源实现
3. 验证创建、通信、观察三段行为对调用方保持同一语义

### 验收

1. 至少一组面向 Host/API 的 acceptance 证据
2. 文档能明确说明“来源差异未暴露到调用方”

## T4. 归档 source-unification 文档

### 目标

把仍然停留在 `Proposed` 的总蓝图文档收口为历史归档或最终归档。

### 任务

1. 更新 `2026-03-12-gagent-implementation-source-unification-blueprint.md`
2. 明确哪些工作已由 phase 1-5 完成
3. 明确剩余工作已经缩成 Host/Mainnet 验收项
4. 清理与当前最终边界冲突的描述

### 验收

1. 该文档不再保持 `Proposed`
2. 文档与当前代码事实一致

## 7. 建议执行顺序

1. `T1`
2. `T2`
3. `T3`
4. `T4`

## 8. 验证建议

建议至少执行：

1. `dotnet build aevatar.slnx --nologo`
2. Host / Mainnet 相关 targeted tests
3. `bash tools/ci/architecture_guards.sh`
4. `bash tools/ci/test_stability_guards.sh`

## 9. 实施结果

### 9.1 Source-agnostic 路径验收

当前 Host / Mainnet 路径已经具备明确的来源无关证据：

1. `WorkflowRunActorResolver` 对已有实例只按不透明 `actorId` 查询 binding，不解析前缀、不按来源分支。
   - 验证：`WorkflowRunActorResolverTests.ResolveOrCreateAsync_ShouldKeepExistingBindingForOpaqueActorId_WhileNewRunUsesLatestRegistryDefinition`
2. `WorkflowCapabilityEndpoints` 在接收、恢复路径上都按不透明 `actorId` 透传，不做 workflow/script/static 来源判断。
   - 验证：`ChatEndpointsInternalTests.HandleCommand_ShouldPreserveOpaqueActorIdInAcceptedLocationAndPayload`
   - 验证：`ChatEndpointsInternalTests.HandleResume_ShouldTreatActorIdAsOpaqueAndForwardItUnchanged`
3. `Aevatar.Mainnet.Host.Api` 当前只负责分布式宿主与 Orleans 运行时组合，不承载任何按来源分支的 actor 解析逻辑。
   - 代码位置：`src/Aevatar.Mainnet.Host.Api/Hosting/MainnetDistributedHostBuilderExtensions.cs`
4. 静态门禁已经覆盖 Host/Application 不得按 `actorId` 字面模式做来源分支。
   - 代码位置：`tools/ci/architecture_guards.sh`

### 9.2 Forward-only upgrade 验收

前滚升级语义已被自动化测试显式固定：

1. 对已有 run，如果请求携带已存在的 opaque `actorId`，解析器继续读取 actor-owned binding，保留旧 definition 与旧 YAML。
2. 对新的 run，请求只给 workflow name 时，解析器使用 registry 最新 definition 与最新 YAML。

自动化验证：

1. `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowRunActorResolverTests"`

### 9.3 Host acceptance 验收

Host API 层已经有显式 acceptance 证据，证明调用方只依赖协议与 `actorId`：

1. `202 Accepted` Location 头保留原始 opaque `actorId`
2. accepted payload 保留原始 opaque `actorId`
3. resume command 透传原始 opaque `actorId`

自动化验证：

1. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter "FullyQualifiedName~ChatEndpointsInternalTests"`

### 9.4 文档归档

以下文档已经与最终状态对齐：

1. `docs/architecture/2026-03-13-gagent-protocol-series-closeout.md`
2. `docs/architecture/2026-03-12-gagent-protocol-first-implementation-plan.md`
3. `docs/architecture/2026-03-12-gagent-implementation-source-unification-blueprint.md`

## 10. 结论

本文记录的最后一项收尾工作已经完成。

完成它之后，`gagent-protocol` 系列不再有未归档的执行型尾项；后续只剩常规增量需求，不再属于这一轮架构重构收口范围。
