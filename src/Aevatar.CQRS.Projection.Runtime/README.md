# Aevatar.CQRS.Projection.Runtime

通用 ReadModel Provider Runtime 组装层。

## 职责

- 收敛 Provider 注册查询（`IProjectionReadModelProviderRegistry`）。
- 执行 Provider 选择策略（`IProjectionReadModelProviderSelector`）。
- 解析 ReadModel 绑定需求（`IProjectionReadModelBindingResolver`）。
- 统一创建 Store 并输出结构化创建日志（`IProjectionReadModelStoreFactory`）。

## DI 入口

- `services.AddProjectionReadModelRuntime()`

## 设计约束

- 不承载业务 ReadModel 类型，不引用 Workflow/AI 等业务模块。
- 仅依赖抽象与 DI，具体 Provider 由上层模块按需注册。
