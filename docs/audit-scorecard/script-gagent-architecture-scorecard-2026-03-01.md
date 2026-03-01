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
- 未闭环项: AI 组合复用生产端口实现、脚本运行观测、revision->编译句柄绑定。

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
| R-SG-14 | In Progress | `IAICapability` 组合接口已落地，`IRoleAgentPort` 生产实现待补 |

## 5. 验证证据（已执行命令）
| 命令 | 结果 |
|---|---|
| `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo` | 通过（16/16） |
| `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo` | 通过（6/6） |
| `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~ScriptGAgentEndToEndTests" --nologo` | 通过（1/1） |
| `dotnet build aevatar.slnx --nologo` | 通过 |
| `bash tools/ci/architecture_guards.sh` | 通过（含 projection route / closed-world / run-id / script inheritance） |

## 6. 风险与后续
1. `IRoleAgentPort` 暂无生产实现，当前 AI 组合复用仍为“接口 + 委托适配器”阶段。
2. 脚本观测维度（`script_id/revision/correlation_id`）尚未统一注入日志与指标。
3. revision 回放治理尚未实现“revision -> 编译缓存句柄”强绑定。

## 7. 建议下一里程碑
1. M2.1: 落地 `IRoleAgentPort` 运行时实现与集成测试。
2. M2.2: 增加脚本执行 observability 埋点与审计事件。
3. M2.3: 实现 revision 绑定执行句柄与升级/迁移策略。
