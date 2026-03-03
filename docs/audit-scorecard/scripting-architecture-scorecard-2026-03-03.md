# Scripting 全量架构审计评分卡（重构复审，2026-03-03）

## 1. 审计范围与方法

1. 审计对象：`src/Aevatar.Scripting.Abstractions`、`src/Aevatar.Scripting.Core`、`src/Aevatar.Scripting.Application`、`src/Aevatar.Scripting.Infrastructure`、`src/Aevatar.Scripting.Hosting`、`src/Aevatar.Scripting.Projection`。
2. 评分口径：六维 100 分模型（分层与依赖反转、CQRS/统一投影链路、Projection 编排与状态约束、读写分离、命名语义、可验证性）。
3. 复审目标：验证 2026-03-02 评分卡中的 Major/Medium 扣分项是否被彻底消除，并给出重构后新分数。

## 2. 客观验证结果（本次复审命令）

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| 全量构建 | `dotnet build aevatar.slnx --nologo` | 通过（0 warning / 0 error） |
| 全量测试 | `dotnet test aevatar.slnx --nologo` | 通过（exit code 0；失败 0） |

## 3. 总分与等级（重构后）

**96 / 100（A）**

## 4. 六维评分（重构后）

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | Host 仅组装能力，核心编排留在 Core/Application；运行模式改为“配置优先 + 类型回退”。 |
| CQRS 与统一投影链路 | 20 | 19 | Scripting Projection 已接入 Hosting DI 组装，单链路闭环成立。 |
| Projection 编排与状态约束 | 20 | 20 | 未发现中间层 ID 映射事实态；守卫已覆盖 Scripting 扫描根。 |
| 读写分离与会话语义 | 15 | 14 | 演化决策改为提案事件携带回传流并由 Actor 发送终态事件；仍保留同步等待终态响应的 API 体验。 |
| 命名语义与冗余清理 | 10 | 9 | 命名与目录语义一致；废弃适配器已删除，文档锚点同步。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | 架构守卫、全量构建、全量测试全部通过。 |

## 5. 关键修复项闭环证据

### R1（已修复）演化链路“命令后查询”改为“提案携带决策回传流 + 终态推送”

1. 提案事件新增 `decision_request_id` 与 `decision_reply_stream_id`：`src/Aevatar.Scripting.Abstractions/script_host_messages.proto:166-176`。
2. 应用层请求模型与适配器贯通上述字段：
   - `src/Aevatar.Scripting.Application/Application/ProposeScriptEvolutionActorRequest.cs:3-12`
   - `src/Aevatar.Scripting.Application/Application/ProposeScriptEvolutionActorRequestAdapter.cs:19-30`
3. 生命周期端口改为订阅 `scripting.evolution.decision.reply` 等待终态响应，不再走旧查询适配器：`src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptLifecyclePort.cs:39-90`。
4. 演化管理 Actor 在 `rejected/promoted` 终态统一发送 `ScriptEvolutionDecisionRespondedEvent`：`src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs:57-159`、`257-293`。
5. `QueryScriptEvolutionDecisionRequestAdapter` 已删除（冗余层清理）。

### R2（已修复）Scripting Projection 接入宿主组装链

1. Hosting 项目引用 Projection：`src/Aevatar.Scripting.Hosting/Aevatar.Scripting.Hosting.csproj:10-15`。
2. 新增 Projection DI 组件注册入口：`src/Aevatar.Scripting.Projection/DependencyInjection/ServiceCollectionExtensions.cs:11-45`。
3. `AddScriptCapability` 组装 Projection 组件：`src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs:46`。

### R3（已修复）运行模式判定去除 `Contains`，改为“配置优先 + 精确类型回退”

1. 新增选项模型：`src/Aevatar.Scripting.Infrastructure/Ports/ScriptingRuntimeQueryModeOptions.cs:3-6`。
2. 运行模式判断改为配置优先、无配置时按 runtime 全限定类型名精确匹配：`src/Aevatar.Scripting.Infrastructure/Ports/DefaultScriptingRuntimeQueryModes.cs:5-27`。
3. Hosting 支持从 `Scripting:Runtime:UseEventDrivenDefinitionQuery` 注入：`src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs:20-69`、`src/Aevatar.Scripting.Hosting/CapabilityApi/ScriptCapabilityHostBuilderExtensions.cs:13-16`。

### R4（已修复）架构守卫覆盖 Scripting 中间层 ID 映射约束

1. `architecture_guards.sh` 扫描根新增：`src/Aevatar.Scripting.Application`、`src/Aevatar.Scripting.Infrastructure`、`src/Aevatar.Scripting.Projection`：`tools/ci/architecture_guards.sh:570-579`。

### R5（已修复）架构文档与实现锚点对齐

1. 相关文档已同步重构事实：
   - `docs/SCRIPTING_ARCHITECTURE.md`
   - `docs/architecture/scripting-autonomous-evolution-architecture-change-2026-03-02.md`
   - `docs/architecture/scripting-autonomous-evolution-implementation-2026-03-02.md`

## 6. 残余风险（非阻断）

1. API 层对“终态决策”仍呈现同步等待体验（尽管底层已事件化回传）；若后续要彻底异步化，可补充 `202 Accepted + 订阅通知` 协议。
2. 运行时类型回退仍依赖一个全限定类型常量；若未来运行时实现扩展，可引入显式能力探测接口进一步去耦。

## 7. 复审结论

本轮重构已闭环修复上一轮审计的主要扣分项，Scripting 子系统在分层、统一投影链路、Actor 化状态管理与可验证性方面达到可持续演进基线，建议按 **A（96/100）** 进入后续迭代。
