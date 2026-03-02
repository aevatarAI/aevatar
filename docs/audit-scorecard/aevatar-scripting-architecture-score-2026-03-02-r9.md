# Aevatar.Scripting 架构评分卡（2026-03-02 R9）

## 1. 审计范围

- 代码范围：`src/Aevatar.Scripting.*`
- 测试范围：`test/Aevatar.Scripting.Core.Tests`、`test/Aevatar.Hosting.Tests`、`test/Aevatar.Integration.Tests` 的 Scripting 相关用例
- 备注：`Aevatar.Foundation.*` 本轮未改动

## 2. 客观验证

1. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo`：`58/58` 通过。
2. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptCapabilityHostExtensionsTests"`：`3/3` 通过。
3. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptAutonomousEvolutionE2ETests|FullyQualifiedName~ScriptAutonomousEvolutionComprehensiveE2ETests|FullyQualifiedName~ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests|FullyQualifiedName~ScriptExternalEvolutionE2ETests"`：`7/7` 通过。
4. `bash tools/ci/test_stability_guards.sh`：通过。
5. `bash tools/ci/architecture_guards.sh`：通过。

## 3. 总分

- 本轮总分：`97/100`（`A+`）
- 对比 R8：`95 -> 97`

提升原因：

1. Core 已移除 Orleans 类型名前缀探测耦合。
2. Evolution 提案结果从轮询查询改为推送式终态响应。
3. 端口查询超时改为抽象注入，不再散落硬编码常量。

## 4. 六维评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | Core 与 Hosting 边界更清晰；运行模式判定已下沉。 |
| CQRS 与统一投影链路 | 20 | 19 | 提案决策从轮询收敛升级为事件推送收敛。 |
| Projection 编排与状态约束 | 20 | 20 | 事实源保持 Actor 状态，无中间层事实字典。 |
| 读写分离与会话语义 | 15 | 14 | 请求/响应语义清晰；仍可补充提案高并发背压策略。 |
| 命名语义与冗余清理 | 10 | 10 | 命名一致，演化协议字段语义清晰。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | 核心单测、集成、Orleans 3 节点、架构门禁均通过。 |

## 5. 关键证据

1. Core 运行模式判定抽象化：
   - `src/Aevatar.Scripting.Core/Ports/IScriptRuntimeDefinitionQueryModePort.cs:3`
   - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:43`
2. 运行模式判定下沉到 Hosting：
   - `src/Aevatar.Scripting.Hosting/Ports/RuntimeScriptDefinitionSnapshotPort.cs:11`
   - `src/Aevatar.Scripting.Hosting/Ports/RuntimeScriptDefinitionSnapshotPort.cs:27`
3. 提案协议新增决策回传字段：
   - `src/Aevatar.Scripting.Abstractions/script_host_messages.proto:169`
   - `src/Aevatar.Scripting.Abstractions/script_host_messages.proto:180`
4. Application 命令适配已携带回传字段：
   - `src/Aevatar.Scripting.Application/Application/ProposeScriptEvolutionCommand.cs:3`
   - `src/Aevatar.Scripting.Application/Application/ProposeScriptEvolutionCommandAdapter.cs:31`
5. Evolution Port 推送式等待终态响应：
   - `src/Aevatar.Scripting.Hosting/Ports/RuntimeScriptEvolutionPort.cs:41`
   - `src/Aevatar.Scripting.Hosting/Ports/RuntimeScriptEvolutionPort.cs:74`
6. Evolution Manager 终态直接回传决策：
   - `src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs:69`
   - `src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs:129`
7. 超时抽象注入：
   - `src/Aevatar.Scripting.Hosting/Ports/IScriptingPortTimeouts.cs:3`
   - `src/Aevatar.Scripting.Hosting/Ports/DefaultScriptingPortTimeouts.cs:3`
   - `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs:30`

## 6. 仍可优化项

1. Hosting 的 Orleans/Local 运行模式判定仍采用类型名前缀，可进一步替换为显式运行模式提供器。
2. `IScriptingPortTimeouts` 当前默认实现固定 45 秒，建议提供环境化实现并纳入配置管理。
