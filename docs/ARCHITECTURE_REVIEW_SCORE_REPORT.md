# 架构审查评分报告（全项目，最新版）

审查时间：2026-02-15  
审查范围：`src/`、`src/workflow/`、`demos/`、`test/`、`docs/`、CI 流水线。  
验证基线：`dotnet build aevatar.slnx --nologo`、`dotnet test aevatar.slnx --nologo` 通过（186/186 测试通过）。

## 一、总评分

**综合得分：79 / 100（评级：B+）**

| 维度 | 权重 | 得分 | 加权分 |
|---|---:|---:|---:|
| 分层清晰度（Host/Application/Core/Infrastructure） | 20% | 8.5 | 17.0 |
| 依赖反转与边界纯度 | 20% | 8.0 | 16.0 |
| CQRS/Projection 架构一致性 | 20% | 8.0 | 16.0 |
| 运行时可靠性与可运维性 | 15% | 7.0 | 10.5 |
| 测试质量与回归能力 | 15% | 8.0 | 12.0 |
| 文档一致性与治理闭环 | 10% | 7.5 | 7.5 |

结论：主干架构已经进入“可扩展、可维护”状态，但仍存在治理层和运维层短板，距离 A 级（85+）还差“文档/CI 同步”和“读模型持久化策略”两类关键项。

## 二、主要优势

1. **分层边界比早期清晰**
- `Application` 通过抽象端口编排投影，不直接依赖具体读模型实现。  
证据：`src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionProjectionPort.cs:6`、`src/workflow/Aevatar.Workflow.Application/Orchestration/WorkflowExecutionRunOrchestrator.cs:11`

2. **Projection Pipeline 统一且具备扩展点**
- reducer/projector 通过 DI 可插拔注册，支持外部程序集扩展。  
证据：`src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs:56`、`src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs:68`

3. **通用 CQRS 内核抽象合理**
- 生命周期编排 `Initialize -> Register -> Project -> Complete` 清晰，复用性高。  
证据：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionLifecycleService.cs:21`

4. **Host 端点职责较薄**
- 协议层与应用层调用分离，HTTP/WS handler 不直接写工作流编排。  
证据：`src/Aevatar.Host.Api/Endpoints/ChatEndpoints.cs:31`、`src/Aevatar.Host.Api/Endpoints/ChatWebSocketRunCoordinator.cs:14`

5. **回归保护覆盖面较好**
- 5 个测试项目全绿，覆盖 Foundation/Host/Integration/Workflow Application 主链路。

## 三、主要劣势与风险

### P1（高优先级）

1. **文档与实现存在漂移**
- `Application` README 仍提及已删除的 `WorkflowExecutionReportMapper`。  
证据：`src/workflow/Aevatar.Workflow.Application/README.md:19`
- `Projection` README 仍提及旧接口 `IWorkflowExecutionProjectionService`。  
证据：`src/workflow/Aevatar.Workflow.Projection/README.md:7`

2. **架构守卫脚本存在失效风险**
- CI guard 使用已不存在路径 `samples` 与 `src/Aevatar.Workflows.Core/...`，实际约束可能漏检。  
证据：`.github/workflows/ci.yml:38`、`.github/workflows/ci.yml:43`

3. **解决方案元数据有陈旧引用**
- `.slnx` 包含已删除文档 `docs/ARCHITECTURE_REFACTOR_PLAN.md`。  
证据：`aevatar.slnx:15`

### P2（中优先级）

1. **读模型默认仅内存存储，缺少保留/清理策略**
- 当前默认 `InMemory`，未见 TTL/归档淘汰。  
证据：`src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs:36`、`src/workflow/Aevatar.Workflow.Projection/Stores/InMemoryWorkflowExecutionReadModelStore.cs:12`

2. **Host 组合层耦合面偏大**
- `Host.Api` 直接引用 Application/Infrastructure/Projection/AGUIAdapter 多实现项目，组合根可维护性一般。  
证据：`src/Aevatar.Host.Api/Aevatar.Host.Api.csproj:10`、`src/Aevatar.Host.Api/Program.cs:32`

3. **Actor-shared 默认语义对外部使用者有认知门槛**
- 默认 `EnableRunEventIsolation=false`，同 actor 多 run 会共享事件视图；对“按 run 查询”的预期不总是直观。  
证据：`src/workflow/Aevatar.Workflow.Projection/Configuration/WorkflowExecutionProjectionOptions.cs:38`、`src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionReadModelProjector.cs:63`

## 四、优先整改建议

1. 修复文档漂移：更新 `workflow/*/README.md` 中旧接口与旧类名。  
2. 修复 CI 守卫路径：让架构检查真实命中当前目录结构。  
3. 清理 `.slnx` 陈旧文件引用。  
4. 为 read model 增加“可配置保留策略”（至少 TTL + 最大条数）。  
5. 将 Host 组合注册收敛为单一扩展入口（例如 `AddWorkflowHostApi()`），减少 `Program.cs` 组装噪声。

## 五、复评目标（到 85+）

满足以下条件即可进入 A 档：

1. 文档/CI/解决方案元数据与代码完全一致。  
2. 读模型具备持久化或至少可控保留策略（不是无限内存增长）。  
3. Host 组合入口进一步收敛，降低跨项目直接引用感知。  
4. 保持现有测试通过并补齐新增治理规则的测试或脚本校验。
