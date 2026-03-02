# Aevatar.Scripting 架构评分卡（2026-03-02 R14）

## 1. 结论

1. 本轮完成第二阶段彻底重构：将 GAgent 三组端口收敛为统一运行时端口，继续削减中间层耦合面。
2. 查询回执链路完成基础组件化：定义/目录/演化查询复用同一 `QueryReply` 执行模板，移除重复模板代码。
3. 运行态查询模式语义收敛到主契约：`UseEventDrivenDefinitionQuery` 并入 `IScriptDefinitionSnapshotPort`，去除旁路转型接口。
4. 重评分：`92/100 (A-)`。

---

## 2. 核心重构项

1. GAgent 端口统一
- 删除：`IGAgentEventRoutingPort`、`IGAgentInvocationPort`、`IGAgentFactoryPort`。
- 新增：`IGAgentRuntimePort`（统一承载 publish/send/invoke/create/destroy/link/unlink）。
- 实现收敛：`RuntimeGAgentRuntimePort` 取代三份分散实现。
- DI 收敛：`AddScriptCapability` 仅注册 `IGAgentRuntimePort`。

2. 运行时能力组合降耦
- `ScriptRuntimeCapabilityComposer` 构造器依赖减少（由多端口拼装改为单一 agent runtime 端口注入）。
- `ScriptInteractionCapabilities` 与 `ScriptAgentLifecycleCapabilities` 统一依赖 `IGAgentRuntimePort`。

3. Definition Query 模式契约拉直
- 删除 `IScriptRuntimeDefinitionQueryModePort`。
- 在 `IScriptDefinitionSnapshotPort` 增加 `UseEventDrivenDefinitionQuery` 属性。
- `ScriptRuntimeGAgent` 直接使用主端口契约，不再通过 `is` 转型侧探能力。

4. Query/Reply 组件化
- 新增：`ScriptQueryReplyAwaiter`（统一封装 `requestId + replyStream + timeout + subscription` 模板）。
- 接入：
  - `RuntimeScriptDefinitionSnapshotPort`
  - `RuntimeScriptCatalogPort`
  - `RuntimeScriptEvolutionPort`
- 结果：消除三处重复等待逻辑，统一超时行为与匹配逻辑。

---

## 3. 验证结果

1. `dotnet build aevatar.slnx --nologo`：通过。
2. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo`：`12/12` 通过。
3. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo`：`61/61` 通过。
4. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptExternalEvolutionE2ETests|FullyQualifiedName~ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests|FullyQualifiedName~ScriptAutonomousEvolutionE2ETests|FullyQualifiedName~ScriptAutonomousEvolutionComprehensiveE2ETests"`：`7/7` 通过。
5. `bash tools/ci/architecture_guards.sh`：通过。
6. `bash tools/ci/test_stability_guards.sh`：通过。

---

## 4. 评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | Core 仅依赖抽象，Runtime 适配继续下沉。 |
| 协议语义纯度 | 15 | 14 | `ActorRequest` 语义延续且内部通信边界更清晰。 |
| Port 抽象质量 | 15 | 15 | GAgent 三端口并轨为单一运行时端口，抽象层次更稳定。 |
| Actor 一致性与并发控制 | 15 | 14 | Actor 内事件化推进保持稳定。 |
| 读写分离语义 | 10 | 8 | Query/Reply 路径统一，但外部 API 仍有同步终态查询空间。 |
| 可测试性 | 15 | 14 | 单元+集成+Orleans3 集群关键路径稳定。 |
| 可治理性 | 10 | 8 | 重复模板显著减少，规则可持续治理性提升。 |

总分：`92/100 (A-)`

---

## 5. 剩余改进方向

1. 外部演化 API 继续向 `202 + proposalId + ReadModel/SSE` 收敛，进一步消除同步决策等待。
2. 将 `ScriptQueryReplyAwaiter` 的超时与 stream 前缀治理参数化到统一 options，以支持多能力域复用。
