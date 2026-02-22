# Aevatar 未提交改动重评分卡（2026-02-22）

## 1. 审计范围与方法

1. 审计对象：当前工作区未提交改动（`git status --short` 可见的 `M` 与新增目录），并结合 `aevatar.slnx` 全解决方案影响面复评。
2. 评分规范：`docs/audit-scorecard/README.md`（标准化 100 分模型）。
3. 审计口径：以“本次未提交改动引入的风险”为主，历史遗留不重复扣分。
4. 本轮重点：`Foundation Runtime Orleans 并行实现`、`Workflow actor 端口抽象化`、`Projection coordinator 类型判定`、相关测试回归覆盖。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| 路由映射门禁 | `bash tools/ci/projection_route_mapping_guard.sh` | 通过 |
| 全量构建 | `dotnet build aevatar.slnx --nologo --no-restore -m:1 -nodeReuse:false --tl:off` | 通过（`0` warning / `0` error） |
| 全量测试 | `dotnet test aevatar.slnx --nologo --no-build --no-restore -m:1 -nodeReuse:false --tl:off` | 通过（`518/518`） |

## 3. 审计结论

- 结论：`BLOCK`
- 综合分：`72 / 100`（等级：`B`）
- 结论说明：构建、测试、门禁均通过，但存在 1 个 `P1` 运行时语义风险与 2 个 `P2/P3` 类型校验健壮性问题，暂不建议直接提交。

## 4. 整体评分（Overall）

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 17 | 方向总体正确；并行 Provider 装配完成，但类型判定兜底策略偏宽。 |
| CQRS 与统一投影链路 | 20 | 15 | 未出现双轨实现；`Projection coordinator` 类型校验存在空洞放行。 |
| Projection 编排与状态约束 | 20 | 15 | 编排 Actor 化方向正确；manifest 缺失即放行削弱事实一致性约束。 |
| 读写分离与会话语义 | 15 | 8 | Workflow 端口事件化改造正确；Orleans 事件分发存在自发消息被短路风险。 |
| 命名语义与冗余清理 | 10 | 7 | 命名基本一致；部分类型识别仍依赖 `Contains` 字符串匹配。 |
| 可验证性（门禁/构建/测试） | 15 | 10 | 全量验证通过；但 Orleans 行为语义测试覆盖不足，未拦截 `P1`。 |

## 5. 分模块评分（Subsystem）

| 模块 | 分数 | 结论 |
|---|---:|---|
| Foundation + Runtime（含 Orleans） | 68 | 架构落点正确，但 Orleans 分发语义存在高风险短路点。 |
| CQRS Projection Core | 74 | Actor 化方向正确，类型校验在 manifest 缺失时过宽。 |
| Workflow Core/App/Infra | 78 | 端口抽象与事件化推进明显，类型兜底匹配建议收紧。 |
| Host/DI 装配 | 85 | Provider 装配清晰且可控，注册路径可验证。 |
| Tests + Guards | 76 | 回归覆盖面广，但 Orleans 关键行为用例不足。 |

## 6. 主要扣分项（按严重度）

### P1

1. Orleans 事件分发存在“自发布链路短路”风险。  
   证据：
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansGrainEventPublisher.cs:108`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:78`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:246`  
   现象：分发前追加 `__publishers` 包含自身 ID，而接收侧以 `ContainsPublisher` 直接丢弃，可能导致 `Self`/回流消息被错误过滤。  
   影响：核心事件链路可被静默吞掉，属于运行正确性风险。

### P2

1. Projection coordinator 类型校验在 manifest 缺失时直接放行。  
   证据：
   - `src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs:80`  
   现象：`manifest == null` 直接返回 actor。  
   影响：可能把非 coordinator actor 误用于 ownership 编排，削弱跨会话一致性约束。

### P3

1. Workflow/Projection 类型识别兜底采用 `Contains`，误判窗口偏大。  
   证据：
   - `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:82`
   - `src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs:98`  
   现象：解析失败后使用类型名包含关系兜底。  
   影响：在同名片段或重构场景下可能误识别类型，增加维护风险。

## 7. 本轮加分项（证据）

1. Workflow actor 端口从具体类型访问改为事件化配置，降低对运行时实现耦合。  
   证据：`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:61`
2. `IEventHandlerContext` 新增 `SendToAsync`，模块层点对点发送不再依赖具体 `GAgentBase`。  
   证据：`src/Aevatar.Foundation.Abstractions/EventModules/IEventHandlerContext.cs:37`、`src/Aevatar.Foundation.Core/Pipeline/EventHandlerContext.cs:55`
3. Orleans Provider 已接入 Host 统一装配入口。  
   证据：`src/Aevatar.Foundation.Runtime.Hosting/DependencyInjection/ServiceCollectionExtensions.cs:34`

## 8. 改进优先级建议

1. P1：修复 Orleans `__publishers` 追加与 `ContainsPublisher` 判定顺序，补齐 `Self/Up/Down/Both` 行为测试矩阵。
2. P2：收紧 coordinator 类型校验策略，manifest 缺失时改为 fail-fast 或补偿加载后再判定。
3. P2：将 `Contains` 兜底改为可验证的精确匹配策略（例如 FullName/AssemblyQualifiedName 白名单）。
