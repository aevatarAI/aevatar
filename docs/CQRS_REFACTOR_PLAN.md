# CQRS 重构计划与完成状态

> 更新时间：2026-02-15  
> 目标：按最佳实践完成 CQRS 投影架构的命名、分层、依赖方向、职责拆分与文档对齐。

## 1. 重构范围

- `src/Aevatar.CQRS.Projection.Abstractions`
- `src/Aevatar.CQRS.Projection.Core`
- `src/Aevatar.CQRS.Projection.WorkflowExecution`
- `src/Aevatar.Host.Api`（仅保留协议宿主职责）
- `demos/Aevatar.Demos.CaseProjections*`（并行扩展示例）

## 2. 已完成项（全部达成）

1. 项目组织与命名统一
- 完成 `Projection` 命名收敛，移除旧命名残留。
- CQRS 主体固定为 `Abstractions / Core / WorkflowExecution` 三层结构。

2. 冗余抽象删除
- 删除无业务增益的“别名接口+转发类”层。
- 统一改为直接依赖泛型契约：`IProjectionEventReducer<,>`、`IProjectionProjector<,>`、`IProjectionReadModelStore<,>`。

3. WorkflowExecution 边界收敛
- 合并并移除独立抽象子项目，领域契约与上下文收敛到 `Projection.WorkflowExecution`。
- 领域模型、context/session、service、reducers/projectors 在同一模块内闭环。

4. API 职责解耦
- 新增 `IWorkflowExecutionRunOrchestrator`，承接 start/wait/complete/rollback 编排。
- `ChatEndpoints` 聚焦协议适配（SSE/WS/HTTP），不直接承载投影编排细节。

5. Demo 对齐 OCP 方式
- Demo 删除重复协调/订阅封装，改为直接复用 Core 泛型实现。
- 外部扩展通过新增 reducer/projector 完成，不修改内核代码。

6. 文档与实现一致化
- 更新 `docs/FOUNDATION.md`、`src/Aevatar.CQRS.Projection.Core/README.md`、`src/Aevatar.Host.Api/README.md`、`demos/*/README.md`。
- 新增 `src/Aevatar.CQRS.Projection.WorkflowExecution/README.md`。

## 3. 最终目标架构（落地状态）

1. 分层与依赖方向
- `Projection.WorkflowExecution -> Projection.Core -> Projection.Abstractions`
- `Host.Api -> Projection.WorkflowExecution`
- API 不直接实现 CQRS 内核机制。

2. 统一投影链路
- `ProjectionLifecycleService` 管 run 生命周期。
- `ProjectionSubscriptionRegistry` 统一 actor stream 订阅与回调入口。
- `ProjectionCoordinator` 一对多分发 projector。
- ReadModel 与 AGUI 输出在同一事件链路并行分支实现。

3. 扩展方式
- 新增投影能力只需新增 reducer/projector/store 并注册 DI。
- 不需要修改 Core 内核，不破坏开闭原则。

## 4. 验收结果

1. 构建
- `dotnet build aevatar.slnx --nologo` 通过。

2. 测试
- `dotnet test aevatar.slnx --nologo` 通过。

3. 残留检查
- 代码层无旧 CQRS 项目名与旧抽象命名残留。
- 关键文档路径已全部指向 `Aevatar.CQRS.Projection.*`。

## 5. Definition of Done 对照

1. 命名与结构统一：已完成。  
2. 依赖反转与抽象收敛：已完成。  
3. API/应用编排职责清晰：已完成。  
4. 文档与代码一致：已完成。  
5. build/test 验证通过：已完成。  

当前 CQRS 重构已按本计划收口完成。
