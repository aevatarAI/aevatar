# Aevatar.AI.LLMProviders.MEAI

`Aevatar.AI.LLMProviders.MEAI` 基于 `Microsoft.Extensions.AI` 提供 Aevatar 的 LLM Provider 实现。

## 职责

- 将 `IChatClient` 适配为 Aevatar 的 `ILLMProvider`
- 支持单轮调用与流式调用
- 支持 Tool Calling 消息转换（函数调用与函数结果）
- 提供 DI 扩展 `AddMEAIProviders(...)`

## 核心类型

- `MEAILLMProvider`：`ILLMProvider` 实现
- `MEAILLMProviderFactory`：Provider 注册与默认 Provider 选择
- `ServiceCollectionExtensions`：DI 注册入口

## 快速接入

```csharp
services.AddMEAIProviders(factory => factory
    .RegisterOpenAI("openai", "gpt-4o-mini", openaiKey)
    .SetDefault("openai"));
```

## 依赖

- `Aevatar.AI.Core`
- `Microsoft.Extensions.AI`
- `Microsoft.Extensions.AI.OpenAI`
- `OpenAI`
