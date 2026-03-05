# PR Review 架构审计打分（Chat Run Request 归一化重构）- 2026-03-05

## 1. 审计范围与输入

1. 审计对象：
   - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunRequestNormalizer.cs`
   - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs`
   - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketRunCoordinator.cs`
   - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatCapabilityModels.cs`
   - `src/workflow/Aevatar.Workflow.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
   - `src/workflow/Aevatar.Workflow.Infrastructure/Workflows/FileBackedWorkflowNameCatalog.cs`（已删除）
   - `test/Aevatar.Workflow.Host.Api.Tests/ChatEndpointsInternalTests.cs`
   - `test/Aevatar.Workflow.Host.Api.Tests/ChatWebSocketCoordinatorAndProtocolTests.cs`
   - `test/Aevatar.Workflow.Host.Api.Tests/WorkflowCapabilityEndpointsCoverageTests.cs`
   - `src/workflow/Aevatar.Workflow.Host.Api/README.md`
   - `src/workflow/Aevatar.Workflow.Host.Api/CHAT_API_CAPABILITIES.md`
   - `docs/workflow-chat-ws-api-capability.md`
   - `src/workflow/README.md`
2. 输入来源：上一轮 review 的 2 个 P1 阻断项（F1/F2）。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制、6 维度）。

## 2. 修复结果摘要

1. **F1 已关闭**：移除 API 边界层对 `workflow` 的 file-backed 预校验，显式 workflow 不再被中间层提前拒绝。
2. **F2 已关闭**：`agentId` 复用路径不再默认注入 `auto`；仅新建 Actor（无 `agentId`）时才默认 `auto`。
3. **兼容层清理完成**：删除 `IFileBackedWorkflowNameCatalog` 及实现，去除无业务价值中间层。
4. **文档已同步**：Host README、能力入口文档、框架能力文档与顶层 workflow README 全部与新语义对齐。

## 3. 关键架构审计结论

### 3.1 边界职责

- `ChatRunRequestNormalizer` 现仅做输入归一化，不再承担“workflow 是否存在”的业务判定。
- workflow 存在性与绑定一致性回归到应用层 resolver/registry 语义，符合分层边界。

### 3.2 复用语义

- `agentId + prompt` 且未提供 `workflow/workflowYamls` 时，`WorkflowName = null`。
- actor 复用决策由 `WorkflowRunActorResolver` 基于绑定状态裁决，避免错误注入造成冲突。

### 3.3 无效层删除

- 删除 `FileBackedWorkflowNameCatalog` 和 DI 注册，避免“文件目录名”成为 API 事实源。
- 保持单一主干：请求归一化 -> 应用层 resolver -> registry。

## 4. 评分（100 分制）

**总分：95 / 100（A，建议合并）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | API 边界不再耦合 file-backed 存在性判断，职责回归应用层。 |
| CQRS 与统一投影链路 | 20 | 19 | 请求不再在边界被错误短路，命令链路保持统一入口。 |
| Projection 编排与状态约束 | 20 | 19 | 未新增中间层事实态；删除一条潜在语义分叉路径。 |
| 读写分离与会话语义 | 15 | 14 | `agentId` 复用语义恢复正确，避免 workflow 误注入。 |
| 命名语义与冗余清理 | 10 | 10 | `workflow` 语义统一为“注册表名称 lookup”，并删除冗余 catalog。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | 补齐 Host API 回归测试并通过全量测试；未新增独立 normalizer 单测项目，留 1 分改进空间。 |

## 5. 问题闭环状态

| ID | 级别 | 主题 | 状态 | 结论 |
|---|---|---|---|---|
| F1 | P1 | 内建 workflow 名称被误拒绝 | Closed | 显式 `workflow` 不再做 file-backed 前置拦截，交给 resolver/registry。 |
| F2 | P1 | 复用 Actor 路径被默认注入 `auto` | Closed | 仅新建 Actor 默认 `auto`；复用路径 workflow 保持未指定。 |

## 6. 验证记录

1. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --tl:off -m:1`
   - 结果：通过，`240 passed`。
2. `bash tools/ci/test_stability_guards.sh`
   - 结果：通过，轮询等待守卫无违规。
3. `dotnet test aevatar.slnx --nologo --tl:off -m:1`
   - 结果：通过；本次执行未复现“Orleans 停止后不退出”挂起。

## 7. 残余风险与后续建议

1. 建议后续补一个轻量的 `ChatRunRequestNormalizer` 独立契约单测文件，降低未来 API 语义回归风险。
2. 若后续新增 workflow 来源（非文件、非内建），继续保持“边界归一化 vs 应用层解析”职责分离，不在 API 层回填来源特定校验。

## 8. 审计结论

本轮“彻底重构、无兼容包袱”已完成且可验证：两项 P1 均闭环，分层语义回到单一主干，测试与文档一致性达标，当前结论为**建议合并**。
