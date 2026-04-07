# `feature/app-services` 相对 `dev` 的 Review 与修复记录

## 说明

- 基线：`dev`
- 评审对象：当前工作树
- 评审方式：优先检查高风险路径，包括 AI chat 流式主链、tool calling、workflow tool provider、chrono-storage chat history
- 本文只记录本轮确认可复现、可验证的问题与修复；不把尚未证实的猜测写成结论

## 发现的问题

### 1. 流式 chat 的最终总结轮次会丢失刚执行完的 tool result

- 位置：`src/Aevatar.AI.Core/Chat/ChatRuntime.cs`
- 触发条件：
  - tool round 已耗尽
  - 进入最后一次 `Tools = null` 的补偿轮次
  - 该轮模型输出的是文本形式的 DSML/XML tool call
  - 解析后执行了工具，再发起最终总结
- 问题原因：
  - 总结阶段复用了旧的 `finalRequest`
  - `finalRequest.Messages` 构造于工具执行之前
  - 新产生的 tool result message 没有带入总结请求
- 影响：
  - 模型在最终总结时看不到刚刚执行出的工具结果
  - 最终回答可能遗漏关键结果，或者基于旧上下文继续回答

## 已实施修复

### 修复 1. 总结阶段改为基于最新 `messages` 重建请求

- 修改：`src/Aevatar.AI.Core/Chat/ChatRuntime.cs`
- 处理方式：
  - 新建 `summaryRequest`
  - 使用最新的 `messages` 快照作为 `Messages`
  - 其余 `RequestId / Metadata / Model / Temperature / MaxTokens` 继续沿用最终轮次配置
- 效果：
  - 文本 tool call 在最终补偿轮次执行后，其 tool result 会进入真正的总结请求
  - 总结模型可以读取到刚执行完成的结果，再生成最终文本

### 修复 2. 增加回归测试覆盖该路径

- 修改：`test/Aevatar.AI.Tests/ChatRuntimeStreamingBufferTests.cs`
- 新增测试：
  - `ChatStreamAsync_WhenFinalRoundParsesTextToolCall_ShouldIncludeToolResultInSummaryRequest`
- 覆盖点：
  - 第一轮是结构化 tool call
  - 最终补偿轮次是文本 tool call
  - 断言第三次流式请求（总结请求）中确实包含最终文本 tool call 对应的 tool result

## 验证结果

- `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --filter ChatRuntimeStreamingBufferTests --nologo`
  - 通过，`13/13`
- `dotnet test test/Aevatar.AI.ToolProviders.Workflow.Tests/Aevatar.AI.ToolProviders.Workflow.Tests.csproj --nologo`
  - 通过，`9/9`
- `dotnet test test/Aevatar.AI.ToolProviders.Binding.Tests/Aevatar.AI.ToolProviders.Binding.Tests.csproj --nologo`
  - 通过，`7/7`

## 本轮未升级为问题单的残余风险

- 当前流式主链对“文本形式 tool call 的原始内容是否先被前端看到”仍依赖消费端渲染策略。
- 本地 CLI runtime 已补了 `sanitizeAssistantMessageContent` 侧的清洗，但若存在其它直接消费原始流 chunk 的前端或宿主，还应确认它们是否也做了同类处理。
