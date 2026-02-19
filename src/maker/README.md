# Aevatar.Maker Capability

`src/maker` 提供 MAKER 能力实现，并复用统一运行时与 CQRS 约束。

## 分层

- `Aevatar.Maker.Core`：领域模块（`maker_recursive`、`maker_vote`）与模块工厂。
- `demos/Aevatar.Demos.Maker/Reporting`：Demo 报告累加器与 JSON/HTML 写出（仅 demo 使用，不在 Maker Host 主链路）。
- `Aevatar.Maker.Application.Abstractions`：应用层契约与模型。
- `Aevatar.Maker.Application`：命令执行应用服务（通过 `IMakerRunExecutionPort` 编排）。
- `Aevatar.Maker.Infrastructure`：DI 组合入口与能力 API 定义（`AddMakerCapability(IServiceCollection, IConfiguration)` / `AddMakerCapability(WebApplicationBuilder)` / `AddMakerInfrastructure` / `MapMakerCapabilityEndpoints`）。

## 关系图

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart TB
    A["Aevatar.Maker.Infrastructure"] --> B["Aevatar.Maker.Application"]
    A --> C["Aevatar.Maker.Core"]
    A --> P["Aevatar.Workflow.Projection + AGUIAdapter"]
    B --> C
    C --> E["Aevatar.Workflow.Core"]
```
