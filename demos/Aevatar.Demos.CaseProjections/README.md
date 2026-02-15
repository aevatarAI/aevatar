# Aevatar.Demos.CaseProjections

并行于 WorkflowExecution 的 CQRS Projection 示例域（Case Management）。

## 目标

- 演示通用投影内核可被外部领域复用。
- 演示 OCP：通过外部程序集新增 reducer，不修改内核与主业务项目。
- 演示 DI：RunId/Clock/CompletionDetector/ContextFactory 全部可替换。

## 结构

- `Aevatar.Demos.CaseProjections.Abstractions`
  - 领域事件（proto）、context/session、read model、服务契约
- `Aevatar.Demos.CaseProjections`
  - reducer/projector/store + DI 组合
- `Aevatar.Demos.CaseProjections.Extensions.Sla`
  - 外部扩展 reducer（升级事件）
- `Aevatar.Demos.CaseProjections.Host`
  - 控制台运行入口

## 运行

```bash
dotnet run --project demos/Aevatar.Demos.CaseProjections.Host
```
