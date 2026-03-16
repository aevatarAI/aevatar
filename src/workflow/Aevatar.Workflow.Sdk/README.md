# Aevatar.Workflow.Sdk

面向 .NET 客户端的 Aevatar Workflow SDK。  
用于以编程方式访问 Workflow Host 能力，包括：

- `/api/chat` SSE 流式运行
- `resume/signal` 人工交互恢复
- Bridge 回调 token 签发与回调上报
- Workflow/Actor 查询接口

> 默认对接地址：`http://localhost:5000`

---

## 1. 目标框架与依赖

- 目标框架：`net10.0`
- 主要依赖：`Microsoft.Extensions.Http`、`Microsoft.Extensions.Options`

---

## 2. 快速接入（推荐：DI）

```csharp
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.DependencyInjection;

var services = new ServiceCollection();

services.AddAevatarWorkflowSdk(options =>
{
    options.BaseUrl = "http://localhost:5000";
    options.DefaultHeaders["x-tenant-id"] = "demo";
});
```

注入后可直接使用 `IAevatarWorkflowClient`。

---

## 3. 核心调用示例

## 3.1 启动流式运行

```csharp
using Aevatar.Workflow.Sdk.Contracts;

var request = new ChatRunRequest
{
    Prompt = "请根据用户反馈总结3条改进建议",
    Workflow = "auto",
    Metadata = new Dictionary<string, string>
    {
        ["scenario"] = "marketing-demo",
    },
};

await foreach (var evt in client.StartRunStreamAsync(request, cancellationToken))
{
    Console.WriteLine($"{evt.Type} | message={evt.Frame.Message}");
}
```

也可以直接提交多模态输入：

```csharp
var request = new ChatRunRequest
{
    Workflow = "auto",
    InputParts =
    [
        new ChatRunContentPart
        {
            Type = "text",
            Text = "请描述这张图里的主要风险",
        },
        new ChatRunContentPart
        {
            Type = "image",
            Uri = "https://example.com/incident-board.png",
            MediaType = "image/png",
        },
    ],
};
```

### 常用事件类型

- `RUN_STARTED` / `RUN_FINISHED` / `RUN_ERROR`
- `STEP_STARTED` / `STEP_FINISHED`
- `TEXT_MESSAGE_START` / `TEXT_MESSAGE_CONTENT` / `TEXT_MESSAGE_END`
- `STATE_SNAPSHOT`
- `CUSTOM`

事件常量见 `WorkflowEventTypes`。

常见 `CUSTOM` 事件名还包括：

- `aevatar.llm.reasoning`
- `aevatar.media.chunk`

## 3.2 运行到结束（便捷模式）

```csharp
using Aevatar.Workflow.Sdk.Errors;

try
{
    var result = await client.RunToCompletionAsync(request, cancellationToken);
    Console.WriteLine($"Succeeded: {result.Succeeded}");
    Console.WriteLine($"Total events: {result.Events.Count}");
}
catch (AevatarWorkflowException ex) when (ex.Kind == AevatarWorkflowErrorKind.RunFailed)
{
    Console.WriteLine($"Workflow failed: {ex.Message}, code={ex.ErrorCode}");
}
```

---

## 4. 人机中断恢复（Resume / Signal）

SDK 提供 `RunSessionTracker`，可从流事件中自动提取 `actorId/runId/stepId/signalName`，减少手动拼接请求。

```csharp
using Aevatar.Workflow.Sdk.Session;

var tracker = new RunSessionTracker();

await foreach (var evt in client.StartRunStreamWithTrackingAsync(request, tracker, cancellationToken))
{
    if (evt.Type == WorkflowEventTypes.Custom &&
        WorkflowCustomEventParser.TryParseHumanInputRequest(evt.Frame, out var humanInput))
    {
        Console.WriteLine($"Need input: {humanInput.Prompt}");
    }
}

var snapshot = tracker.Snapshot;
if (snapshot.CanResume)
{
    var resumeRequest = tracker.CreateResumeRequest(
        approved: true,
        userInput: "approved by operator");

    await client.ResumeAsync(resumeRequest, cancellationToken);
}

if (snapshot.CanSignal)
{
    var signalRequest = tracker.CreateSignalRequest(payload: "window=open");
    await client.SignalAsync(signalRequest, cancellationToken);
}
```

### 内置 Custom 事件名（节选）

- `aevatar.run.context`
- `aevatar.human_input.request`
- `aevatar.workflow.waiting_signal`
- `aevatar.workflow.signal.buffered`
- `aevatar.llm.reasoning`

解析工具见 `WorkflowCustomEventParser`。

---

## 5. 查询接口

```csharp
var catalog = await client.GetWorkflowCatalogAsync(cancellationToken);
var capabilities = await client.GetCapabilitiesAsync(cancellationToken);
var detail = await client.GetWorkflowDetailAsync("auto", cancellationToken);

var snapshot = await client.GetActorSnapshotAsync("actor-1", cancellationToken);
var timeline = await client.GetActorTimelineAsync("actor-1", take: 200, cancellationToken);
```

说明：

- `GetCapabilitiesAsync` / `GetWorkflowDetailAsync` / `GetActorSnapshotAsync` 遇到 `404` 返回 `null`
- `GetActorTimelineAsync` 要求 `take > 0`

---

## 6. 错误模型

统一抛出 `AevatarWorkflowException`，按 `Kind` 区分错误类别：

- `InvalidRequest`：本地参数不合法
- `Http`：服务端返回非 2xx
- `Transport`：网络/连接层错误
- `StreamPayload`：SSE 或 JSON 解析失败
- `RunFailed`：运行流中出现 `RUN_ERROR`

---

## 7. 非 DI 场景（手动构造）

```csharp
using Aevatar.Workflow.Sdk.Options;
using Aevatar.Workflow.Sdk.Streaming;
using Microsoft.Extensions.Options;

var httpClient = new HttpClient
{
    BaseAddress = new Uri("http://localhost:5000"),
};

var options = Options.Create(new AevatarWorkflowClientOptions
{
    BaseUrl = "http://localhost:5000",
});

IAevatarWorkflowClient client = new AevatarWorkflowClient(
    httpClient,
    new SseChatTransport(),
    options);
```

---

## 8. 与 Host 端点的对应关系

- `StartRunStreamAsync` / `RunToCompletionAsync` -> `POST /api/chat`（SSE）
- `ResumeAsync` -> `POST /api/workflows/resume`
- `SignalAsync` -> `POST /api/workflows/signal`
- `GetWorkflowCatalogAsync` -> `GET /api/workflow-catalog`
- `GetCapabilitiesAsync` -> `GET /api/capabilities`
- `GetWorkflowDetailAsync` -> `GET /api/workflows/{workflowName}`
- `GetActorSnapshotAsync` -> `GET /api/actors/{actorId}`
- `GetActorTimelineAsync` -> `GET /api/actors/{actorId}/timeline?take={take}`
