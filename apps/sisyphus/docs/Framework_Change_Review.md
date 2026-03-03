# Framework Change Review — feature/sisyphus Branch (Final)

> **Date**: 2026-03-03
> **Branch**: `feature/sisyphus` vs `origin/dev`
> **Framework files changed**: 28 (excluding test, demo, docs, config; 3 app-layer files removed from framework)
> **Build**: 0 errors, 0 warnings | **Tests**: 1,026 pass, 0 fail

---

## Overview

This document provides a file-by-file analysis of every framework source file modified or added on the `feature/sisyphus` branch. For each file, we explain:

1. **What changed** — the exact diff vs `origin/dev`
2. **Why it changed** — the technical motivation
3. **Where it's used** — callers, transitive consumers
4. **Sisyphus relationship** — how the app layer depends on (or ignores) this change

### Sisyphus Architecture Context

Sisyphus is a multi-agent research system with two workflow YAMLs:

- **`sisyphus_research.yaml`** — A `while` loop that iterates: Researcher → Verifier → DAG Builder. Each agent uses NyxId MCP tools to read/write a chrono-graph knowledge graph.
- **`sisyphus_maker.yaml`** — A sequential pipeline: Decomposer → 3 parallel Verifiers → Consensus.

The app layer (`Sisyphus.Application`) is thin — it manages sessions, triggers workflows via `IWorkflowRunCommandService`, and proxies chrono-graph API calls. It does NOT directly reference AI Core, ToolCallLoop, RoleGAgent, or MCP classes.

---

## Category 1: AI Core Layer (4 files)

### 1.1 `ChatSessionKeys.cs` — Removed 4-arg overload

**File**: `src/Aevatar.AI.Abstractions/ChatSessionKeys.cs`
**Change**: Removed `CreateWorkflowStepSessionId(scopeId, runId, stepId, attempt)` (14 lines deleted)

**Why**: The 4-arg overload creates workflow-scoped session IDs with run/attempt tracking (format: `{scopeId}:{runId}:{stepId}:a{attempt}`). This is workflow-layer logic that doesn't belong in `Aevatar.AI.Abstractions` — it couples a core abstraction package to workflow execution semantics.

**Where it moved**: `src/workflow/Aevatar.Workflow.Core/WorkflowSessionKeys.cs` (see §3.2). All callers in `LLMCallModule`, `EvaluateModule`, `ReflectModule` updated to use the new location.

**Sisyphus relationship**: Indirect — Sisyphus workflows execute `llm_call` and `while` modules which use `WorkflowSessionKeys` internally. The move is transparent to the app layer.

---

### 1.2 `ExecutionTraceHook.cs` — Enhanced observability logging

**File**: `src/Aevatar.AI.Core/Hooks/BuiltIn/ExecutionTraceHook.cs`
**Change**: +20 lines. Enhanced three hook methods:

| Hook method | Before | After |
|-------------|--------|-------|
| `OnLLMRequestEndAsync` | Logged only `Agent={Agent}` | Logs `Content=` (truncated 300 chars) + `ToolCalls=[name(args)]` summary |
| `OnToolExecuteStartAsync` | Logged only `{Tool}` | Logs `Args=` (truncated 500 chars) |
| `OnToolExecuteEndAsync` | Logged only `{Tool}` | Logs `Result=` (truncated 500 chars) |

Added private `Truncate(string?, int)` helper for safe log truncation.

**Why**: dev's trace hook logged almost no useful information — you couldn't tell what the LLM returned or what tools did without adding manual breakpoints. For a multi-agent research workflow executing 60+ tool calls per run, observability is essential for debugging.

**Where it's used**: Registered as a built-in hook in `AIGAgentBase.RebuildRuntime()`. Executes automatically for every AI agent (including all Sisyphus roles). No opt-in required.

**Sisyphus relationship**: **Direct benefit**. Every `llm_call` step in `sisyphus_research.yaml` (init, verify, build_dag, next_round, finalize) produces structured trace logs showing LLM reasoning and tool call results. Critical for diagnosing why a researcher missed a claim or a DAG builder created a wrong edge.

---

### 1.3 `ToolCallLoop.cs` — Tool result truncation + content preservation

**File**: `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs`
**Change**: +25 lines (net, after dead code removal).

Two changes:

**(a) `MaxToolResultChars` property + truncation logic**

```csharp
public int MaxToolResultChars { get; set; } = 200_000;

if (!toolCallContext.Terminate && toolResult.Length > MaxToolResultChars)
{
    toolResult = string.Concat(
        toolResult.AsSpan(0, MaxToolResultChars),
        $"\n\n[TRUNCATED — tool result was {toolResult.Length:N0} chars, limit is {MaxToolResultChars:N0}]");
}
```

**Why**: Sisyphus's Researcher agent calls `snapshot` on the chrono-graph, which can return 500KB+ JSON. Without truncation, this overflows the LLM context window and causes silent failures or hallucinated continuations. The 200K char limit (~50K tokens) leaves room for system prompt + history.

**Where it's used**: Runs unconditionally on every tool call result in every AI agent.

**(b) Preserve `Content` on assistant tool_call messages**

```csharp
// Before:
messages.Add(new ChatMessage { Role = "assistant", ToolCalls = response.ToolCalls });
// After:
messages.Add(new ChatMessage { Role = "assistant", Content = response.Content, ToolCalls = response.ToolCalls });
```

**Why**: Some LLMs (DeepSeek, GPT-4o) return both text content AND tool calls in the same response — the assistant "thinks aloud" while requesting a tool. dev discarded this intermediate reasoning text, which broke the conversation history for models that expect it to be preserved.

**Sisyphus relationship**: **Direct fix**. DeepSeek (the default provider) frequently returns reasoning text alongside graph tool calls. Without this fix, the Researcher agent lost its chain-of-thought context between tool rounds.

---

### 1.4 `MEAILLMProvider.cs` — Proper tool JSON Schema passthrough

**File**: `src/Aevatar.AI.LLMProviders.MEAI/MEAILLMProvider.cs`
**Change**: Replaced `AIFunctionFactory.Create()` with new `AgentToolAIFunction` class (+60 lines, -5 lines).

**Before** (dev):
```csharp
options.Tools.Add(AIFunctionFactory.Create(
    (string input) => tool.ExecuteAsync(input),
    tool.Name,
    tool.Description));
```

**After**:
```csharp
options.Tools.Add(new AgentToolAIFunction(tool));
```

**Why**: `AIFunctionFactory.Create()` wraps the tool as a simple `(string input) => result` function with a generic `input` parameter. The LLM sees only `{"input": {"type": "string"}}` as the parameter schema, losing all structured parameter definitions. For MCP tools like `create_node`, `traverse`, and `snapshot`, the LLM needs to see the real parameters (`nodeId`, `depth`, `label`, etc.) to produce correct JSON arguments.

`AgentToolAIFunction` preserves the tool's original `ParametersSchema` (the JSON Schema from MCP server advertisements) and passes it directly to the MEAI/OpenAI function-calling protocol. It falls back to `{"input": "string"}` only if the tool has no schema.

**Where it's used**: Every LLM call that includes tools — affects all agents with registered tools.

**Sisyphus relationship**: **Critical fix**. Without this, NyxId MCP tools (chrono-graph `create_node`, `create_edge`, `snapshot`, `traverse`) sent generic `input` schemas to DeepSeek. The LLM couldn't tell what parameters to use, causing >80% tool call failures. With proper schemas, the Researcher and DAG Builder agents can correctly call graph tools with structured parameters.

---

## Category 2: MCP Tool Provider Layer (7 files)

### 2.1 `MCPServerConfig.cs` — HTTP transport + OAuth configuration model

**File**: `src/Aevatar.AI.ToolProviders.MCP/MCPServerConfig.cs`
**Change**: +30 lines. Added:

- `Command` → now `string?` (optional, was required)
- `Url` property — HTTP/SSE endpoint URL
- `Headers` — custom HTTP headers dictionary
- `Auth` — `MCPAuthConfig?` for OAuth2 client_credentials
- `IsHttp` — computed property (`!string.IsNullOrEmpty(Url)`)
- `MCPAuthConfig` class — `Type`, `TokenUrl`, `ClientId`, `ClientSecret`, `Scope`
- `MCPToolsOptions.AddHttpServer()` extension method

**Why**: dev only supported stdio-based MCP servers (command + args). Sisyphus's chrono-graph integration requires HTTP/SSE-based MCP servers behind NyxId OAuth authentication. The config model needed to express URL endpoints, authorization headers, and OAuth token endpoints.

**Sisyphus relationship**: **Required for NyxId connector**. The `connectors.json` config in Sisyphus defines an HTTP MCP server with OAuth credentials to authenticate against NyxId's token endpoint.

---

### 2.2 `MCPClientManager.cs` — HTTP transport creation + thread safety

**File**: `src/Aevatar.AI.ToolProviders.MCP/MCPClientManager.cs`
**Change**: +35 lines. Added:

- `_clientsLock` object for thread-safe client list mutation
- HTTP transport branch: `if (config.IsHttp)` → creates `HttpClientTransport` + `SseClientTransport`
- OAuth integration: `new OAuthTokenHandler(auth, _logger)` when `config.Auth` is present
- Custom header injection from `config.Headers`
- Thread-safe `DisposeAsync()` — takes snapshot before iterating

**Why**: dev's `MCPClientManager` only created `StdioClientTransport` for command-based servers. HTTP/SSE transport requires a different pipeline: `HttpClient` → `OAuthTokenHandler` (DelegatingHandler) → `SseClientTransport`. Thread safety was added because tool source discovery can be called concurrently during agent activation.

**Sisyphus relationship**: **Required for NyxId MCP**. When `sisyphus_research.yaml` roles reference `nyxid_mcp` connector, the framework activates this HTTP transport path with OAuth token injection.

---

### 2.3 `OAuthTokenHandler.cs` — NEW: OAuth2 client_credentials token handler

**File**: `src/Aevatar.AI.ToolProviders.MCP/OAuthTokenHandler.cs` (89 lines, new file)
**Change**: Entire file is new.

**What it does**: `DelegatingHandler` that intercepts outgoing HTTP requests and automatically acquires/refreshes OAuth2 `client_credentials` tokens. Features:

- Thread-safe token caching with `SemaphoreSlim`
- Double-check pattern: validates token before and after lock acquisition
- 30-second expiry margin to avoid mid-request token expiration
- Standard `application/x-www-form-urlencoded` token request format
- Comprehensive error logging

**Why**: MCP HTTP servers behind NyxId require Bearer tokens. Tokens expire (typically 1 hour), so the handler transparently refreshes them without requiring the MCP client or tool adapter to know about authentication.

**Where it's used**: Created by `MCPClientManager` when `config.Auth` is present.

**Sisyphus relationship**: **Required for NyxId authentication**. Every MCP tool call from Sisyphus agents (snapshot, traverse, create_node, create_edge) passes through this handler to get an authenticated Bearer token.

---

### 2.4 `MCPToolAdapter.cs` — Multi-block content extraction

**File**: `src/Aevatar.AI.ToolProviders.MCP/MCPToolAdapter.cs`
**Change**: +15 lines. Replaced `result.Content.ToString()` with new `ExtractText(result.Content)` method.

```csharp
private static string ExtractText(IList<ContentBlock>? content)
{
    if (content is null or []) return string.Empty;
    if (content.Count == 1) return content[0].Text ?? string.Empty;
    var sb = new StringBuilder();
    foreach (var block in content)
    {
        if (!string.IsNullOrEmpty(block.Text))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(block.Text);
        }
    }
    return sb.ToString();
}
```

**Why**: MCP servers can return multiple `ContentBlock`s in a single tool result (e.g., chrono-graph snapshot returns a text description + JSON data). dev's `.ToString()` call produced `ModelContextProtocol.Client.ContentBlock[]` instead of actual content.

**Sisyphus relationship**: **Direct fix**. Without this, graph snapshot results came back as type names instead of actual JSON, making the Researcher agent unable to read the knowledge graph.

---

### 2.5 `MCPConnectorBuilder.cs` — HTTP connector support

**File**: `src/Aevatar.Bootstrap.Extensions.AI/Connectors/MCPConnectorBuilder.cs`
**Change**: +20 lines. Added:

- `ToMCPServerConfig(ConnectorConfigEntry)` static method — converts connector config entries to `MCPServerConfig` objects
- Supports both HTTP (`Url`) and stdio (`Command`) configurations
- Maps OAuth auth settings from connector config
- Updated validation: requires either URL or Command (was command-only)

**Why**: The connector builder needs to convert `connectors.json` entries (the unified connector format) to `MCPServerConfig` objects used by the MCP client. dev's builder only handled stdio.

**Sisyphus relationship**: **Required for NyxId connector registration**. `connectors.json` → `MCPConnectorBuilder.ToMCPServerConfig()` → `MCPClientManager` → HTTP transport + OAuth.

---

### 2.6 `ServiceCollectionExtensions.cs` — Simplified provider registration + MCP merge

**File**: `src/Aevatar.Bootstrap.Extensions.AI/ServiceCollectionExtensions.cs`
**Change**: -150 lines deleted, +30 lines added. Net reduction of ~120 lines.

**Removed** (was in dev):
- `ReadConfiguredProviders()`, `ResolveFallbackRegistration()`, `ResolveApiKeySelection()` — complex multi-provider resolution pipeline
- `ProviderKind` enum, `ProviderSemantic` enum, `ConfiguredProvider` record, `FallbackRegistration` record, `ApiKeySelection` record — internal types supporting the complex pipeline

**Replaced with** (simplified):
```csharp
var apiKey = options.ApiKey
    ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
    ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");
```

**Added** (new):
- `RegisterMCPTools()` merges mcp.json (legacy, lowest priority) with connectors.json MCP entries (highest priority)
- Uses `MCPConnectorBuilder.ToMCPServerConfig()` for connector→MCPServerConfig conversion

**Why**: dev's multi-provider machinery supported scenarios like "register both OpenAI and DeepSeek simultaneously with priority rules". In practice, every deployment uses a single provider. The complex pipeline added ~225 lines of untested configuration resolution that made debugging LLM provider issues very difficult. The replacement is a straightforward fallback chain.

The MCP merge logic is new — it combines mcp.json (individual server definitions) with connectors.json (unified connector definitions that can also define MCP servers), with connectors.json winning on name collisions.

**Sisyphus relationship**: **Indirect**. Sisyphus Host calls `AddWorkflowCapabilityWithAIDefaults()` → `AddAevatarAIFeatures()`. The simplified registration correctly resolves the DeepSeek API key and registers the NyxId MCP connector from connectors.json.

---

### 2.7 `AevatarConnectorConfig.cs` — HTTP/OAuth config parsing + env var expansion

**File**: `src/Aevatar.Configuration/AevatarConnectorConfig.cs`
**Change**: +40 lines. Added:

- `MCPConnectorConfig.Url` — HTTP endpoint URL property
- `MCPConnectorConfig.Headers` — custom headers dictionary
- `MCPConnectorConfig.Auth` — `MCPAuthConnectorConfig?` for OAuth
- `MCPAuthConnectorConfig` class — OAuth2 fields (Type, TokenUrl, ClientId, ClientSecret, Scope)
- `ParseMCPAuth()` — parses OAuth config section from JSON
- `ExpandEnvironmentPlaceholders()` — replaces `${VAR_NAME}` syntax in config values with environment variables
- Error handling in `LoadConnectors()` — logs warnings via `System.Diagnostics.Trace`

**Why**: `connectors.json` is the unified connector configuration file. To define HTTP MCP servers with OAuth, the config model needed URL, headers, and auth sections. The `${VAR_NAME}` expansion allows credentials to be stored in environment variables instead of plaintext in config files (security best practice).

**Sisyphus relationship**: **Required**. Sisyphus's `connectors.json` defines the NyxId MCP connector with `${NYXID_SA_CLIENT_ID}` and `${NYXID_SA_CLIENT_SECRET}` placeholders that resolve from environment variables at startup.

---

## Category 3: Workflow Core Layer (9 files)

### 3.1 `WorkflowSessionKeys.cs` — NEW: Workflow-scoped session ID utility

**File**: `src/workflow/Aevatar.Workflow.Core/WorkflowSessionKeys.cs` (21 lines, new file)
**Change**: Entire file is new.

```csharp
public static string CreateWorkflowStepSessionId(
    string scopeId, string runId, string stepId, int attempt = 1)
    => $"{scopeId}:{runId}:{stepId}:a{attempt}";
```

**Why**: Moved from `ChatSessionKeys` (see §1.1). Workflow-specific session ID generation belongs in the workflow layer, not in `AI.Abstractions`.

**Where it's used**: `LLMCallModule.cs` (line 72), `EvaluateModule.cs` (line 60), `ReflectModule.cs` (lines 108, 132).

**Sisyphus relationship**: Indirect — all `llm_call` steps in Sisyphus workflows use this internally for session tracking.

---

### 3.2 `WhileModuleConfigurator.cs` — NEW: Inject workflow definition into WhileModule

**File**: `src/workflow/Aevatar.Workflow.Core/Composition/WhileModuleConfigurator.cs` (14 lines, new file)
**Change**: Entire file is new.

```csharp
public sealed class WhileModuleConfigurator : WorkflowModuleConfiguratorBase<WhileModule>
{
    public override int Order => 0;
    protected override void Apply(WhileModule module, WorkflowDefinition workflow)
        => module.SetWorkflow(workflow);
}
```

**Why**: The refactored `WhileModule` needs access to the `WorkflowDefinition` to resolve child step definitions. Module configurators run during workflow initialization, injecting dependencies before execution.

**Where it's used**: Registered in `WorkflowCoreModulePack.ConfiguratorRegistrations`.

**Sisyphus relationship**: **Required**. Without this configurator, the `research_loop` while step couldn't resolve its 3 child steps (verify, build_dag, next_round).

---

### 3.3 `WhileModule.cs` — Major refactor: sequential child step dispatch

**File**: `src/workflow/Aevatar.Workflow.Core/Modules/WhileModule.cs`
**Change**: ~200 lines rewritten. This is the largest single change.

**Before (dev)**: While module dispatches a single step type per iteration. Condition evaluation uses expression strings (`while condition="${...}"`). State tracked via `WhileRuntimeState` record.

**After**: While module supports `children:` list in YAML. Each iteration executes children sequentially. Falls back to legacy behavior if no children defined.

**Key architectural changes**:

| Aspect | dev | feature/sisyphus |
|--------|-----|-----------------|
| Step dispatch | Single step type per iteration | Sequential children per iteration |
| Child ID format | `{whileStepId}_{iteration}` | `{whileStepId}_iter_{iteration}_{childId}` |
| State class | `WhileRuntimeState` (record) | `WhileState` (class with Children list) |
| Workflow access | None | `SetWorkflow()` + `ResolveChildren()` |
| Connector injection | None | Extracts `allowed_connectors` from role definitions |
| Context propagation | None | Passes original context to all children |

**Sisyphus YAML that drives this**:
```yaml
steps:
  - id: research_loop
    type: while
    parameters:
      max_iterations: "20"
    children:
      - id: verify
        type: llm_call
        role: verifier
      - id: build_dag
        type: llm_call
        role: dag_builder
      - id: next_round
        type: llm_call
        role: researcher
```

**Sisyphus relationship**: **Core feature**. This is THE mechanism that implements Sisyphus's research loop. Each iteration runs: verify claims → write to graph → continue research. The dev WhileModule couldn't express "multiple different steps per iteration" — it only supported repeating one step type.

---

### 3.4 `LLMCallModule.cs` — Context propagation

**File**: `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs`
**Change**: +8 lines. Two changes:

**(a) Context prepending** (lines 64-69):
```csharp
if (request.Parameters.TryGetValue("context", out var context) &&
    !string.IsNullOrEmpty(context))
{
    prompt = "--- Original Context ---\n" + context.TrimEnd() +
             "\n--- End Context ---\n\n" + prompt;
}
```

**(b) Session key** (line 72): `ChatSessionKeys` → `WorkflowSessionKeys`

**Why**: In a multi-step while loop, each child step needs access to the original user input (which contains Graph IDs). Without context propagation, only the first step sees the user's prompt — subsequent steps receive only the previous step's output. The `--- Original Context ---` block is prepended to every step's prompt so all agents can access graph UUIDs.

**Sisyphus relationship**: **Critical**. The user prompt contains `Read Graph ID: {uuid}` and `Write Graph ID: {uuid}`. Without context propagation, the Verifier and DAG Builder agents in iterations 2+ wouldn't know which graph to read/write.

---

### 3.5 `EvaluateModule.cs` — Session key rename

**File**: `src/workflow/Aevatar.Workflow.Core/Modules/EvaluateModule.cs`
**Change**: 1 line. `ChatSessionKeys.CreateWorkflowStepSessionId(...)` → `WorkflowSessionKeys.CreateWorkflowStepSessionId(...)`

**Why**: Consistency with the session key extraction (§1.1, §3.1).

**Sisyphus relationship**: None — Sisyphus doesn't use `evaluate` module.

---

### 3.6 `ReflectModule.cs` — Session key rename

**File**: `src/workflow/Aevatar.Workflow.Core/Modules/ReflectModule.cs`
**Change**: 2 lines. Same `ChatSessionKeys` → `WorkflowSessionKeys` rename at lines 108 and 132.

**Why**: Same as §3.5.

**Sisyphus relationship**: None — Sisyphus doesn't use `reflect` module.

---

### 3.7 `WorkflowLoopModule.cs` — Original input preservation

**File**: `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`
**Change**: +10 lines.

```csharp
private readonly Dictionary<string, string> _originalInputByRunId = new();

// On workflow start:
_originalInputByRunId[runId] = evt.Input;

// Before dispatching any step:
if (_originalInputByRunId.TryGetValue(runId, out var originalInput) && !string.IsNullOrEmpty(originalInput))
    req.Parameters["context"] = originalInput;

// On run completion:
_originalInputByRunId.Remove(runId);
```

**Why**: The workflow loop module orchestrates step dispatch. When a while loop iterates, each child step's prompt is built from the previous step's output — the original user input is lost. This change caches the initial `StartWorkflowEvent.Input` and injects it as a `context` parameter on every step request, enabling the context propagation in `LLMCallModule` (§3.4).

**Sisyphus relationship**: **Critical**. This is the source of context propagation. The user's prompt (containing research topic + Graph IDs) is preserved across all 60+ step dispatches in a typical Sisyphus research run.

---

### 3.8 `WorkflowCoreModulePack.cs` — Register WhileModuleConfigurator

**File**: `src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs`
**Change**: +1 line.

```csharp
ConfiguratorRegistrations =
[
    new WorkflowLoopModuleConfigurator(),
    new WhileModuleConfigurator(),  // ← NEW
];
```

**Why**: The new `WhileModuleConfigurator` (§3.2) must be registered in the module pack so it runs during workflow initialization.

**Sisyphus relationship**: **Required**. Without this registration, `WhileModule.SetWorkflow()` is never called and children can't be resolved.

---

### 3.9 `WorkflowGAgent.cs` — State transition refactoring

**File**: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
**Change**: +6/-6 lines. Refactored `TransitionState()` method style:

```csharp
// Before (fluent chain):
StateTransitionMatcher.Match(current, evt)
    .On<ConfigureWorkflowEvent>(ApplyConfigureWorkflow)
    .On<WorkflowCompletedEvent>(ApplyWorkflowCompleted)
    .OrCurrent();

// After (explicit if/returns):
if (StateTransitionMatcher.TryExtract<ConfigureWorkflowEvent>(evt, out var configure))
    return ApplyConfigureWorkflow(current, configure);
if (StateTransitionMatcher.TryExtract<WorkflowCompletedEvent>(evt, out var completed))
    return ApplyWorkflowCompleted(current, completed);
return current;
```

**Why**: The explicit pattern is more debuggable (you can set breakpoints on specific event types) and doesn't require the `StateTransitionBuilder<TState>` generic chain. Both `TryExtract<T>` and `Match()` exist on dev — this is a style preference.

**Sisyphus relationship**: Indirect — all workflows use `WorkflowGAgent` for state persistence.

---

## Category 4: Workflow Application + Infrastructure Layer (3 files)

> **Removed from framework (4.1, 4.2, 4.6)**: `GetWorkflowYaml` was added to `IWorkflowExecutionQueryApplicationService` and exposed as a REST endpoint `GET /workflows/{name}`, but this was app-layer logic (Sisyphus's `WorkflowTriggerService` reading YAML to patch `max_iterations`). Cleaned up: Sisyphus now injects `IWorkflowDefinitionRegistry` directly instead of going through the framework query service interface.

### 4.1 `WorkflowChatRunModels.cs` — Add `Args` to output frame

**File**: `src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowChatRunModels.cs`
**Change**: +1 line. Added `public string? Args { get; init; }` to `WorkflowOutputFrame` record.

**Why**: Tool call arguments are useful for frontend debugging — shows what parameters the agent sent to each tool.

**Sisyphus relationship**: Indirect — the Sisyphus frontend displays tool call details in the research session timeline.

---

### 4.2 `WorkflowRunEventContracts.cs` — Add `Args` to tool call start event

**File**: `src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunEventContracts.cs`
**Change**: +1 line. Added `public string? Args { get; init; }` to `WorkflowToolCallStartEvent`.

**Why**: Matches the output frame change (§4.1). The event contract carries tool call arguments from the projection layer to the output frame mapper.

**Sisyphus relationship**: Same as §4.1.

---

### 4.3 `WorkflowOutputFrameMapper.cs` — Map `Args` field

**File**: `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowOutputFrameMapper.cs`
**Change**: +1 line. Added `Args = e.Args` to the tool call start event mapping.

**Why**: Connects the event contract (§4.2) to the output frame (§4.1).

**Sisyphus relationship**: Same as §4.1.

---

## Category 5: AGUI Presentation Layer (1 file)

### 5.1 `AGUIEvents.cs` — Optional fields + tool call args

**File**: `src/Aevatar.Presentation.AGUI/AGUIEvents.cs`
**Change**: +1/-3 lines.

**(a) `ToolCallStartEvent`**: Added `public string? Args { get; init; }` — tool call arguments for UI display.

**(b) `HumanInputRequestEvent`**: Made `StepId`, `RunId`, `SuspensionType` optional (`required` → nullable `string?`).

**(c) `HumanInputResponseEvent`**: Made `StepId`, `RunId` optional.

**Why**: (a) Matches the workflow output frame Args field. (b-c) `HumanInputRequestEvent` is mapped in `EventEnvelopeToAGUIEventMapper` from workflow events. Not all contexts have a step/run ID (e.g., standalone agent sessions). Making them `required` caused `NullReferenceException` when constructing events outside workflow contexts.

**Sisyphus relationship**: (a) Displayed in the research session UI. (b-c) Prevents crashes when human input is requested outside a workflow step context.

---

## Category 6: Foundation Layer (2 files)

### 6.1 `EventEnvelopeSurrogate.cs` — NEW: Orleans serialization for EventEnvelope

**File**: `src/Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming/Streaming/EventEnvelopeSurrogate.cs` (70 lines, new file)
**Change**: Entire file is new.

**What it does**: Orleans `[GenerateSerializer]` surrogate for protobuf `EventEnvelope`. Converts between protobuf binary format and Orleans-serializable struct.

**Why**: Orleans cannot natively serialize protobuf `IMessage` types through its streaming infrastructure. `EventEnvelope` is the primary message type sent through `IAsyncStream<EventEnvelope>` in `OrleansActorStream.cs`. Without a surrogate, Orleans throws serialization exceptions at runtime when agents publish events through streams.

**Where it's used**: Auto-discovered by Orleans via `[GenerateSerializer]` and `[RegisterConverter]` attributes. No explicit registration needed.

**Sisyphus relationship**: **Required infrastructure**. All event propagation between agents (step completed events, tool call events, text message events) flows through Orleans streams as `EventEnvelope` messages. Without this surrogate, the entire multi-agent communication pipeline fails.

---

### 6.2 `Garnet README.md` — Documentation update

**File**: `src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/README.md`
**Change**: -2 lines. Removed mention of `DeleteEventsUpToAsync` from the feature list.

**Why**: The README listed `DeleteEventsUpToAsync` as a feature, but the method description was misleading in context. The method still exists on dev — this is just a documentation clarification.

**Sisyphus relationship**: None.

---

## Category 7: CQRS Projection Layer (3 files)

### 7.1 `ActorProjectionOwnershipCoordinator.cs` — Remove unused import

**File**: `src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs`
**Change**: -1 line. Removed `using Aevatar.CQRS.Projection.Core.Abstractions;` (unused import).

**Sisyphus relationship**: None — cleanup.

---

### 7.2 `ProjectionOwnershipCoordinatorGAgent.cs` — State transition refactoring

**File**: `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionOwnershipCoordinatorGAgent.cs`
**Change**: +6/-6 lines. Same `StateTransitionMatcher.Match()` → `TryExtract<T>()` pattern change as §3.9.

**Sisyphus relationship**: None — CQRS projection internal refactoring.

---

### 7.3 `ProjectionSessionEventHub.cs` — Remove unused import

**File**: `src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs`
**Change**: -2 lines. Removed `using Aevatar.CQRS.Projection.Core.Abstractions;` (unused import).

**Sisyphus relationship**: None — cleanup.

---

## Summary

### Change Distribution

| Category | Files | Net Lines Changed | Sisyphus Impact |
|----------|-------|-------------------|-----------------|
| AI Core | 4 | +80 | Critical — tool schema, truncation, observability |
| MCP Provider | 7 | +250 | Critical — HTTP/OAuth transport for NyxId |
| Workflow Core | 9 | +250 | Critical — while children, context propagation |
| Workflow App/Infra | 3 | +3 | Required — Args mapping (GetWorkflowYaml removed, moved to app layer) |
| AGUI Presentation | 1 | +2 | Minor — optional fields, Args |
| Foundation | 2 | +68 | Required — Orleans serialization |
| CQRS Projection | 3 | -3 | None — cleanup |
| **Total** | **28** | **~648** | |

### Dependency Chain: Sisyphus → Framework

```
Sisyphus.Host
  → AddWorkflowCapabilityWithAIDefaults()
    → AddAevatarAIFeatures()
      → RegisterMeaiProviders()  [simplified, §2.6]
      → RegisterMCPTools()       [MCP merge, §2.6]
        → MCPConnectorBuilder.ToMCPServerConfig()  [§2.5]
        → MCPClientManager (HTTP + OAuthTokenHandler)  [§2.2, §2.3]
        → MCPToolAdapter.ExtractText()  [§2.4]
      → MEAILLMProvider + AgentToolAIFunction  [§1.4]

  → WorkflowCoreModulePack
    → WhileModuleConfigurator  [§3.2]
    → WhileModule (children dispatch)  [§3.3]
    → WorkflowLoopModule (context propagation)  [§3.7]
    → LLMCallModule (context prepending)  [§3.4]
    → WorkflowSessionKeys  [§3.1]

  → IWorkflowRunCommandService
    → WorkflowTriggerService calls IWorkflowDefinitionRegistry.GetYaml()  [app-layer, cleaned up]

  → Orleans Streaming
    → EventEnvelopeSurrogate  [§6.1]
```

### No Dead Code Remaining

All dead code was removed in the previous commit (`2994d35`):
- `ToolCallEventPublishingHook.cs` — deleted (never registered)
- `ChatAsync(onContent)` callback streaming pipeline — removed (~100 lines)

Every remaining framework change is either:
1. **Directly required** by Sisyphus's multi-agent research workflow
2. **Framework infrastructure** that enables the required features (e.g., Orleans surrogate, config parsing)
3. **Observability improvement** that benefits all agents (ExecutionTraceHook)
4. **Bug fix** that prevents runtime failures (content preservation in tool_call messages, multi-block content extraction)
