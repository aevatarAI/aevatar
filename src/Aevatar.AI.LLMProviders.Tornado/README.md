# Aevatar.AI.LLMProviders.Tornado

`Aevatar.AI.LLMProviders.Tornado` 基于 `LlmTornado` 适配多供应商 LLM 到 Aevatar 的统一接口。

## 职责

- 提供 `ILLMProvider` 实现：`TornadoLLMProvider`
- 支持多供应商模型调用（如 Anthropic、Google 等）
- 提供流式输出适配
- 提供 DI 扩展 `AddTornadoProviders(...)`

## 核心类型

- `TornadoLLMProvider`：调用 TornadoApi 完成 chat/stream 请求
- `TornadoLLMProviderFactory`：集中注册模型与默认 Provider
- `ServiceCollectionExtensions`：DI 注册入口

## 快速接入

```csharp
services.AddTornadoProviders(factory => factory
    .Register("anthropic", LlmTornado.Code.LLmProviders.Anthropic, apiKey, "claude-sonnet-4-20250514")
    .SetDefault("anthropic"));
```

## 依赖

- `Aevatar.AI.Core`
- `LlmTornado`
- `Microsoft.Extensions.*.Abstractions`
