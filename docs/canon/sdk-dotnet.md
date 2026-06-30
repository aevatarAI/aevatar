---
title: ".NET Workflow SDK Quick Start"
status: active
owner: eanzhao
---

# .NET Workflow SDK Quick Start

`Aevatar.Workflow.Sdk` is a lightweight client SDK for app-side integration with:

- `POST /api/chat` (SSE streaming)
- `POST /api/workflows/resume` (human_input/human_approval continuation)
- `POST /api/workflows/signal` (wait_signal continuation)

This SDK is transport-layer only and does not embed workflow runtime logic.

## 1. Register in DI

```csharp
using Aevatar.Workflow.Sdk.DependencyInjection;

var services = new ServiceCollection();
services.AddAevatarWorkflowSdk(options =>
{
    options.BaseUrl = "http://localhost:5100";
    options.DefaultHeaders["x-api-key"] = "<optional>";
});
```

## 2. Start a stream run

```csharp
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Session;

var client = serviceProvider.GetRequiredService<IAevatarWorkflowClient>();
var tracker = new RunSessionTracker();

await foreach (var evt in client.StartRunStreamWithTrackingAsync(new ChatRunRequest
{
    Prompt = "Design a release workflow with approval gate.",
    Workflow = "auto",
}, tracker))
{
    if (evt.Type == WorkflowEventTypes.TextMessageContent)
    {
        Console.Write(evt.Frame.TextMessageContent.Delta);
    }
}
```

## 3. Continue a suspended run

If the stream reaches `human_input` / `human_approval`, use tracked context:

```csharp
var resumeRequest = tracker.CreateResumeRequest(
    approved: true,
    userInput: "Looks good, continue.");

await client.ResumeAsync(resumeRequest);
```

For `wait_signal`:

```csharp
// Tracker automatically carries runId/signalName and the latest stepId from waiting_signal.
// Keep stepId in the request for precise resume when one run has multiple wait_signal waiters.
var signalRequest = tracker.CreateSignalRequest(payload: "ops-window-open");
await client.SignalAsync(signalRequest);
```

Long-running external work should use continuation semantics instead of stretching a normal executor step past its `600s` timeout. The supported pattern is:

1. Start the external job from the workflow.
2. Park the run at `wait_signal` (up to `86400000ms`, one durable callback/signal lease) or `human_approval` (for operator gates).
3. Resume with `SignalAsync(...)` / `ResumeAsync(...)` when the callback arrives.

## 4. Run-level error handling

- HTTP/startup errors throw `AevatarWorkflowException` (`Kind = Http`).
- Transport/parse errors throw `AevatarWorkflowException` (`Kind = Transport/StreamPayload`).
- For one-shot execution, use `RunToCompletionAsync(...)`; if stream contains `RUN_ERROR`, it throws `AevatarWorkflowException` with `Kind = RunFailed`.

## 5. Typed custom-event helpers

The SDK now provides typed parsers for common `CUSTOM` payloads:

- `WorkflowCustomEventParser.TryParseRunContext(...)`
- `WorkflowCustomEventParser.TryParseStepRequest(...)`
- `WorkflowCustomEventParser.TryParseStepCompleted(...)`
- `WorkflowCustomEventParser.TryParseHumanInputRequest(...)`
- `WorkflowCustomEventParser.TryParseWaitingSignal(...)`
- `WorkflowCustomEventParser.TryParseLlmReasoning(...)`

These parsers read the typed `WorkflowRunEventEnvelope` custom payloads exposed by the SDK.

## 6. Contract alignment notes

- Request precedence follows server behavior: `workflowYamls > workflow > auto`.
- `workflow` is file-backed name lookup only.
- `workflowYamls` is inline YAML bundle only (index 0 is entry workflow).
- Session correlation should use `aevatar.run.context` and custom step events, not ad-hoc local IDs.
- Refactor (iter104/cluster-2): Old pattern: SDK exposed WorkflowOutputFrame as JSON semantic contract (internal serialization not protobuf). New principle: SDK uses WorkflowRunEventEnvelope proto; JSON only at external wire boundary adapter.
