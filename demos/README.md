# Demos

## Existing

- `Aevatar.Demos.Cli`：Runtime 行为场景演示。

## CQRS Projection 扩展示例

- `Aevatar.Demos.CaseProjections.Abstractions`
- `Aevatar.Demos.CaseProjections`
- `Aevatar.Demos.CaseProjections.Extensions.Sla`
- `Aevatar.Demos.CaseProjections.Host`

运行：

```bash
dotnet run --project demos/Aevatar.Demos.CaseProjections.Host
```

该示例不依赖 WorkflowExecution 业务模型，直接复用 `Aevatar.CQRS.Projection.Abstractions` + `Aevatar.CQRS.Projection.Core`，用于展示 OCP 扩展能力。
