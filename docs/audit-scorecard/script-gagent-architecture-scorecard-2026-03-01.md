# Script GAgent 架构实施审计分卡（2026-03-01）

## 1. 审计范围
- 范围: `Aevatar.Scripting.Abstractions/Core/Projection/Hosting` 与相关测试、CI 守卫。
- 基线文档:
  - `docs/architecture/csharp-script-gagent-requirements.md`
  - `docs/architecture/csharp-script-gagent-detailed-architecture.md`
  - `docs/plans/2026-03-01-csharp-script-gagent-implementation-plan.md`

## 2. 结论摘要
- 总分: `88/100`
- 结论: 已完成首批可运行架构落地，满足“事件主链、继承边界、投影精确路由、端到端闭环”核心要求。
- 阻断项: 无。
- 未闭环项: `IGAgentFactoryPort` 与生命周期边界测试、脚本运行观测、revision->编译句柄绑定。

## 3. 维度评分
| 维度 | 分值 | 说明 |
|---|---:|---|
| 分层与依赖边界 | 18/20 | 新增项目遵循 Domain/Application/Projection/Host 分工，未引入反向依赖。 |
| Event Sourcing 主链 | 19/20 | `RunScriptCommandAdapter -> RunScriptRequestedEvent -> ScriptHostGAgent -> ScriptDomainEventCommitted` 已验证。 |
| 投影一致性 | 18/20 | `TypeUrl` 精确路由、`GroupBy + ToDictionary + TryGetValue` 已实现并通过守卫。 |
| 继承与复用边界 | 18/20 | `ScriptHostGAgent` 明确继承 `GAgentBase<ScriptHostState>`，新增继承守卫脚本。 |
| 可验证性与测试 | 15/20 | 脚本专项测试和门禁通过；全量 `dotnet test aevatar.slnx` 尚未执行。 |

## 4. 需求状态快照
| 需求 | 状态 | 证据 |
|---|---|---|
| R-SG-01 | Done | `IScriptAgentDefinition`、`RoslynScriptAgentCompiler` |
| R-SG-02 | Done | `ScriptHostGAgent` |
| R-SG-03 | Done | `ScriptHostGAgentReplayContractTests`、`ScriptGAgentEndToEndTests` |
| R-SG-04 | In Progress | `ScriptHostState` 已落地，迁移策略未完成 |
| R-SG-05 | Done | `ScriptExecutionReadModelProjector` + reducer |
| R-SG-06 | Done | `projection_route_mapping_guard` 通过 |
| R-SG-07 | In Progress | 事件主线程模型满足，timeout/retry 事件模型待补 |
| R-SG-08 | In Progress | 沙箱禁用规则已落地，IO/反射白名单待补 |
| R-SG-09 | Planned | 观测埋点未落地 |
| R-SG-10 | In Progress | revision 字段贯通，revision 绑定执行句柄未完成 |
| R-SG-11 | Done | `AddScriptCapability` Host 装配 |
| R-SG-12 | In Progress | 专项验证通过，全量测试未执行 |
| R-SG-13 | Done | `script_inheritance_guard.sh` + 测试 |
| R-SG-14 | In Progress | `IGAgentInvocationPort` + `RuntimeGAgentInvocationPort` + `RuntimeGAgentInvocationPortTests`；通用创建端口待补 |
| R-SG-15 | In Progress | 已明确 `IActorRuntime` 生命周期权威边界；缺 `IGAgentFactoryPort` 与 Scope 非生命周期测试证据 |

## 5. 验证证据（已执行命令）
| 命令 | 结果 |
|---|---|
| `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo` | 通过（16/16） |
| `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --filter "*RuntimeGAgentInvocationPortTests*" --nologo` | 通过（2/2） |
| `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo` | 通过（6/6） |
| `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~ScriptGAgentEndToEndTests" --nologo` | 通过（1/1） |
| `dotnet build aevatar.slnx --nologo` | 通过 |
| `bash tools/ci/architecture_guards.sh` | 通过（含 projection route / closed-world / run-id / script inheritance） |

## 6. 风险与后续
1. `IGAgentFactoryPort` 暂未落地，脚本“受控创建任意 GAgent”能力未闭环。
2. 生命周期边界测试尚未补齐，尚缺“IOC Scope 不托管 GAgent 生命周期”的自动化证据。
3. 脚本观测维度（`script_id/revision/correlation_id`）尚未统一注入日志与指标。
4. revision 回放治理尚未实现“revision -> 编译缓存句柄”强绑定。

## 7. 建议下一里程碑
1. M2.1: 落地 `IGAgentFactoryPort` 与 Runtime 生命周期实现（create/destroy/link/unlink/restore）。
2. M2.2: 补齐生命周期边界测试（证明 Scope 仅依赖解析，不托管 GAgent 生命周期）。
3. M2.3: 增加脚本执行 observability 埋点与审计事件。
4. M2.4: 实现 revision 绑定执行句柄与升级/迁移策略。
