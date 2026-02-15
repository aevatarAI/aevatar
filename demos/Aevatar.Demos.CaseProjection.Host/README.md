# Aevatar.Demos.CaseProjection.Host

并行于 `Workflow` 的领域 Demo（Case Management）。

## 运行

```bash
dotnet run --project demos/Aevatar.Demos.CaseProjection.Host
```

## 展示点

- `Aevatar.Demos.CaseProjection` 复用通用 CQRS 内核 (`ProjectionLifecycleService` / `ProjectionSubscriptionRegistry`)。
- `Aevatar.Demos.CaseProjection.Extensions.Sla` 通过外部程序集新增 `CaseEscalatedEventReducer`，不修改核心投影项目。
- Host 仅通过 DI 组合能力：
  - `AddCaseProjectionDemo(...)`
  - `AddCaseProjectionExtensionsFromAssembly(...)`

这是 OCP（对扩展开放）与 DIP（依赖抽象）的示例落地。
