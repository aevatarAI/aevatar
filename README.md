# Aevatar

**Aevatar** is an autonomous agent platform built on the **virtual actor model**. It provides three core primitives — **GAgent**, **Workflow**, and **Script** — each of which can be published as a **Service** and served through virtual actors on the **Aevatar Mainnet**. Deep integration with **[NyxID](https://github.com/ChronoAIProject/NyxID)** gives every service identity-aware access control, credential brokering, and cross-platform connectivity.

**Live Mainnet Console:** [aevatar-console.aevatar.ai](https://aevatar-console.aevatar.ai/) | **NyxID Platform:** [nyx.chrono-ai.fun](https://nyx.chrono-ai.fun/)

> [中文版](README_zh.md)

---

## Core Primitives

### GAgent — Stateful Agent as Virtual Actor

A **GAgent** (Generic Agent) is the foundational building block of Aevatar. Every GAgent is a stateful, event-sourced virtual actor with a serial mailbox, automatic activation/deactivation, and location-transparent addressing.

**Architecture:**

```
IAgent
  └─ GAgentBase                          (stateless event pipeline)
      └─ GAgentBase<TState>              (event sourcing + state management)
          └─ GAgentBase<TState, TConfig> (mergeable configuration)
              ├─ AIGAgentBase<TState>     (LLM composition: ChatRuntime, ToolManager, Middleware)
              │   └─ RoleGAgent           (LLM-powered agent with tool calling & streaming)
              ├─ WorkflowRunGAgent        (workflow orchestration per run)
              └─ ScriptBehaviorGAgent     (dynamic script execution)
```

**Key capabilities:**

- **Event Sourcing**: State transitions via `PersistDomainEventAsync()` → committed events flow into the unified Projection Pipeline. State rebuilt from event store on activation; snapshots for fast recovery.
- **Unified Event Pipeline**: Static `[EventHandler]` methods + dynamic `IEventModule` combined, priority-sorted, and executed with a two-channel hook system (virtual method overrides + DI-injected `IGAgentExecutionHook` pipeline).
- **Topology-Aware Messaging**: Parent-child hierarchies with directional publishing (`TopologyAudience.Children | Parent | Self`), plus point-to-point `SendToAsync()` and durable timer/timeout callbacks.
- **Module Composition**: Register or replace `IEventModule` instances at runtime — this is how workflow step execution, script behaviors, and custom processing plug in without modifying the base agent.
- **State Guard**: `AsyncLocal`-based write protection ensures state mutations only occur within event handler scopes or activation — never from arbitrary threads.

**Every GAgent runs as a virtual actor**: automatically activated on first message, deactivated on idle, and transparently migrated across cluster nodes. No manual lifecycle management required.

---

### Workflow — Declarative Multi-Agent Orchestration

Workflows let you orchestrate multi-agent collaboration through **declarative YAML** without writing code. A workflow defines **roles** (agents with LLM configuration) and **steps** (execution primitives), compiled and executed by actor-based orchestrators.

**Execution model:**

```
WorkflowGAgent (Definition Actor)     ← owns YAML, compilation result, version
    │
    └─ WorkflowRunGAgent (Run Actor)  ← per-run orchestrator, event-sourced state
        ├─ WorkflowExecutionKernel    ← step dispatch engine
        ├─ RoleGAgent (per role)      ← LLM-powered sub-agents
        └─ Child WorkflowRunGAgent    ← sub-workflow invocations
```

**YAML structure:**

```yaml
name: analysis_pipeline
description: Multi-agent analysis with review
roles:
  - id: analyst
    name: Analyst
    system_prompt: "You are a domain analyst..."
    provider: claude
    model: claude-sonnet-4-20250514
    temperature: 0.7
  - id: reviewer
    name: Reviewer
    system_prompt: "You are a critical reviewer..."
    provider: deepseek
    model: deepseek-chat
steps:
  - id: analyze
    type: llm_call
    role: analyst
    next: review
  - id: review
    type: llm_call
    role: reviewer
    next: finalize
  - id: finalize
    type: transform
```

**30+ built-in step types** covering the full orchestration spectrum:

| Category | Step Types |
|----------|-----------|
| **LLM & Tools** | `llm_call`, `tool_call` |
| **Control Flow** | `conditional`, `while`, `loop`, `workflow_call`, `sub_workflow`, `assign`, `guard` |
| **Parallelism** | `parallel`, `fan_out`, `foreach`, `map_reduce`, `vote_consensus`, `race` |
| **Human-in-the-Loop** | `human_approval`, `human_input`, `secure_input` |
| **External I/O** | `connector_call`, `mcp_call`, `http_*`, `bridge_call` |
| **Async Signals** | `wait_signal`, `emit`, `delay` |
| **Evaluation** | `evaluate`, `reflect`, `workflow_yaml_validate` |
| **Sub-Workflows** | `workflow_call` with continuation-based parent-child coordination |

**Key design decisions:**
- Definition vs. Run separation: `WorkflowGAgent` holds YAML and compilation; `WorkflowRunGAgent` owns all execution facts. Clean actor boundaries.
- Continuation pattern for sub-workflows: parent sends request event, ends turn, resumes on reply — purely event-driven, no synchronous waiting.
- Module pack system: `IWorkflowModulePack` provides step modules + dependency expanders + configurators. Extensions (e.g., Maker) plug in as packs.

---

### Script — Runtime-Compiled Autonomous Agents

Scripts provide **dynamic, hot-deployable agent behaviors** written in C# and compiled at runtime via Roslyn. The script evolution system manages the full lifecycle: proposal, sandbox validation, compilation, promotion, and rollback — all event-sourced.

**Lifecycle actors:**

```
ScriptEvolutionManagerGAgent   ← index of all proposals and their status
    │
    └─ ScriptEvolutionSessionGAgent  ← orchestrates one proposal: validate → compile → promote
         │
         ├─ ScriptCatalogGAgent      ← active revision registry (per scope)
         ├─ ScriptDefinitionGAgent   ← persists compiled artifact metadata
         └─ ScriptBehaviorGAgent     ← runtime executor of script behaviors
```

**Evolution pipeline:**

```
Proposed → BuildRequested → Validated/ValidationFailed → Promoted/Rejected
                                                              ↓
                                               RollbackRequested → RolledBack
```

**Compilation process (Roslyn-based):**

1. **Sandbox validation** — `ScriptSandboxPolicy` blocks dangerous APIs (reflection, system calls, etc.)
2. **Proto compilation** — `.proto` files compiled to C# message types via `IScriptProtoCompiler`
3. **Semantic compilation** — Full Roslyn `CSharpCompilation` with curated reference assemblies
4. **Contract verification** — At least one `IScriptBehaviorBridge` implementation required
5. **Artifact creation** — `ScriptBehaviorArtifact` with factory method, descriptor, type URLs

**Runtime execution:**

Scripts implement `IScriptBehaviorBridge`, which exposes:
- `DispatchAsync(inbound, context)` → returns domain events
- `ApplyDomainEvent(state, evt)` → pure state transition
- `BuildReadModel(state)` → materialized query view

The `ScriptBehaviorGAgent` binds to a compiled definition and dispatches incoming requests through the behavior bridge. Emitted `ScriptDomainFactCommitted` events carry state snapshots, read model projections, and native materializations (document/graph store).

**Runtime capabilities available to scripts:** publish events, send messages to actors, schedule delayed signals, create child actors, query read models — all through `IScriptBehaviorRuntimeCapabilities`.

---

## Service Binding — From Primitive to API

Any GAgent, Workflow, or Script can be **published as a Service** through the service binding system. This transforms an internal actor-based capability into an addressable, versioned, governed API endpoint.

**Service lifecycle:**

```
Define → Create Revision → Prepare (adapt) → Publish → Deploy (activate) → Serve
```

**Three implementation adapters** handle the binding:

| Source | Adapter | Endpoint Kind | What Happens |
|--------|---------|---------------|--------------|
| **Workflow** | `WorkflowServiceImplementationAdapter` | `Chat` | YAML spec → chat endpoint; creates `WorkflowServiceDeploymentPlan` |
| **Script** | `ScriptingServiceImplementationAdapter` | `Command` (per script command) | Extracts command endpoints from runtime semantics; creates `ScriptingServiceDeploymentPlan` |
| **GAgent** | Static binding | Configurable | Resolves or creates actor by type; direct dispatch |

**Service identity** is hierarchical: `{tenant_id}:{app_id}:{namespace}:{service_id}`. Each service tracks:
- **Revisions** with status lifecycle: `Created → Prepared → Published → Retired`
- **Endpoint catalog** defining exposed operations
- **Bindings** to other services, connectors, or secrets
- **Governance policies** for access control

**Invocation flow:**

```
Client Request
  → ServiceInvocationApplicationService (validate, normalize)
    → ServiceInvocationResolutionService (resolve target actor)
      → IInvokeAdmissionAuthorizer (check policies)
        → DefaultServiceInvocationDispatcher (route by implementation type)
          → Target Actor (Script / Workflow / GAgent)
```

**Scope binding** simplifies common patterns: bind a workflow or script to a scope and get automatic service definition, revision preparation, publishing, and endpoint catalog management.

---

## Virtual Actor Runtime — Aevatar Mainnet

All services on the Aevatar Mainnet are served through **virtual actors** powered by [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/). The runtime maps every GAgent to an Orleans grain with transparent distribution, persistence, and messaging.

**Architecture layers:**

```
GAgentBase (business logic)
    ↓
OrleansActor / OrleansAgentProxy (lifecycle wrapper)
    ↓
RuntimeActorGrain (Orleans grain: state persistence, stream subscription, envelope dispatch)
    ↓
Orleans Silo Cluster (Garnet/Redis state, Kafka streams, gossip clustering)
```

**Key runtime properties:**

- **Location transparency**: Actors addressed by string ID; Orleans handles placement, activation, and migration across silos.
- **Serial mailbox guarantee**: Each actor processes one event at a time — no locks, no concurrent state mutation.
- **Lazy activation**: Agent type stored in grain state; actual instance created on first message via DI.
- **Hierarchical stream topology**: Parent-child relationships stored in grain state; `StreamTopologyGrain` manages forwarding rules with BFS relay, cycle prevention, and audience/type filtering.
- **Event deduplication**: `IEventDeduplicator` memo table prevents duplicate processing across retries.
- **Durable callbacks**: `RuntimeCallbackSchedulerGrain` uses Orleans reminders for timeout/timer scheduling with generation-based cancellation.

**Production infrastructure:**

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **Clustering** | Orleans gossip / fixed membership | Silo discovery and leader election |
| **State Storage** | Garnet (Redis-compatible) | Grain state persistence across silos |
| **Stream Transport** | Kafka | Cross-silo event delivery with ordering |
| **Stream Metadata** | Garnet | Pub/sub coordination for Kafka provider |

**Configuration:**

```yaml
Orleans:
  ClusteringMode: "Development"      # or "Localhost"
  ClusterId: "aevatar-mainnet-cluster"
  ServiceId: "aevatar-mainnet-host-api"
Runtime:
  Provider: "Orleans"
  OrleansStreamBackend: "KafkaProvider"  # or "InMemory"
  OrleansPersistenceBackend: "Garnet"    # or "InMemory"
```

---

## NyxID Integration

Aevatar has deep integration with **[NyxID](https://github.com/ChronoAIProject/NyxID)** ([nyx.chrono-ai.fun](https://nyx.chrono-ai.fun/)), an identity and credential brokering platform. NyxID provides the identity layer, LLM gateway, tool access control, and cross-platform connectivity for all services on the Mainnet.

### Identity & Authentication

- **Claims-based identity**: `NyxIdClaimsTransformer` maps NyxID tokens to Aevatar scopes using waterfall resolution (`scope_id → uid → sub → NameIdentifier → *_id`).
- **Scope isolation**: Every request resolves to a scope (tenant/workspace) via `IAppScopeResolver`. All actor operations, service bindings, and data access are scoped.
- **Per-request token flow**: No stored secrets — user's Bearer token flows through `AgentToolRequestContext` (AsyncLocal) to every tool and provider call.

### LLM Gateway

- **Credential brokering**: NyxID stores user's API keys for multiple providers (OpenAI, Anthropic, DeepSeek). The `NyxIdLLMProvider` routes LLM calls through NyxID's gateway, which injects the correct credentials per request.
- **Dynamic routing**: `NyxIdRoutePreference` controls endpoint selection — `auto` (default gateway), `gateway` (explicit), or relative paths for proxied services.
- **No local secrets required**: Agents call LLMs using the user's NyxID-managed credentials without ever seeing the API keys.

### Tool Ecosystem (18 Specialized Tools)

NyxID exposes a rich tool set available to all AI agents:

| Category | Tools |
|----------|-------|
| **Account & Profile** | `NyxIdAccountTool`, `NyxIdProfileTool`, `NyxIdSessionsTool` |
| **Credentials** | `NyxIdApiKeysTool`, `NyxIdExternalKeysTool`, `NyxIdMfaTool` |
| **Services** | `NyxIdServicesTool`, `NyxIdCatalogTool`, `NyxIdProvidersTool`, `NyxIdEndpointsTool` |
| **Approvals** | `NyxIdApprovalsTool` — create, approve/deny, manage grants, configure policies |
| **Infrastructure** | `NyxIdNodesTool`, `NyxIdNotificationsTool`, `NyxIdLlmStatusTool` |
| **Execution** | `NyxIdCodeExecuteTool` — Python, JavaScript, TypeScript, Bash |
| **Proxy & Bridge** | `NyxIdProxyTool`, `NyxIdChannelBotsTool` |

### Cross-Platform Connectivity

- **Channel Bots**: Register bots on Telegram, Discord, Lark, and Feishu. Map platform conversations to Aevatar agents via conversation routes — enabling AI agents to serve users across messaging platforms.
- **Credential-Injecting Proxy**: `NyxIdProxyTool` makes HTTP requests through NyxID's proxy, which handles credential injection and approval workflows (server-side approval with configurable timeout).
- **Approval Workflows**: `NyxIdToolApprovalHandler` creates remote approval requests with multi-channel notification (Telegram, FCM, APNs) and configurable timeout polling.

### Connected Services Context

When a user authenticates via NyxID, `ConnectedServicesContextMiddleware` preloads available services into the agent's system message — enabling automatic service discovery without explicit tool calls.

---

## Quick Start

### 1. Configure LLM Access

| Method | How |
|--------|-----|
| **Environment variable** | `export DEEPSEEK_API_KEY="sk-..."` or `export OPENAI_API_KEY="sk-..."` |
| **Config file** | Write provider and API key in `~/.aevatar/secrets.json` (see [Configuration](src/Aevatar.Configuration/README.md)) |
| **NyxID (recommended)** | Store API keys in NyxID — no local secrets needed |

### 2. Start the Mainnet Host

```bash
dotnet run --project src/Aevatar.Mainnet.Host.Api
```

Or for workflow-only development:

```bash
dotnet run --project src/workflow/Aevatar.Workflow.Host.Api
```

### 3. Send a Chat Request

```bash
curl -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -d '{"prompt": "Analyze the pros and cons of microservices", "workflow": "simple_qa"}'
```

Response is an SSE stream with events: `RUN_STARTED`, `STEP_STARTED`, `TEXT_MESSAGE_CONTENT`, `STEP_FINISHED`, `RUN_FINISHED`.

### API Endpoints

| Endpoint | Description |
|----------|-------------|
| `POST /api/chat` (SSE) | Execute workflow, stream results |
| `GET /api/ws/chat` (WebSocket) | Same as above, WebSocket protocol |
| `POST /api/workflows/resume` | Resume human approval/input steps |
| `POST /api/workflows/signal` | Send signal to wait_signal steps |
| `GET /api/workflows` | List available workflows |
| `POST /api/services/{id}/invoke/{endpoint}` | Invoke a published service |

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    Aevatar Mainnet                       │
│                                                         │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐              │
│  │ GAgent   │  │ Workflow  │  │ Script   │  Primitives  │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘              │
│       │              │              │                    │
│       └──────────────┼──────────────┘                    │
│                      ▼                                   │
│            ┌─────────────────┐                           │
│            │ Service Binding │  Publish as Service       │
│            └────────┬────────┘                           │
│                     ▼                                    │
│            ┌─────────────────┐                           │
│            │  Virtual Actor  │  Orleans Runtime          │
│            │    Runtime      │  (Garnet + Kafka)         │
│            └────────┬────────┘                           │
│                     ▼                                    │
│            ┌─────────────────┐                           │
│            │     NyxID       │  Identity, LLM Gateway,   │
│            │  Integration    │  Tools, Approvals         │
│            └─────────────────┘                           │
└─────────────────────────────────────────────────────────┘
```

**Layering:** `Domain / Application / Infrastructure / Host` — strict dependency inversion, no cross-layer coupling.

**CQRS Pipeline:** `Command → Event → Projection → ReadModel` — unified projection pipeline shared by all capabilities.

**Serialization:** Protobuf everywhere — state, events, commands, snapshots, cross-actor transport. JSON only at HTTP adapter boundaries.

---

## Project Structure

```
src/
├── Aevatar.Foundation.*          # Actor runtime, event sourcing, CQRS core
├── Aevatar.AI.*                  # LLM providers, tool providers, AI middleware
├── Aevatar.CQRS.*                # Projection pipeline, read model stores
├── Aevatar.Scripting.*           # Script evolution, compilation, catalog
├── platform/Aevatar.GAgentService.*  # Service binding, invocation, governance
├── workflow/Aevatar.Workflow.*   # Workflow engine, step modules, projections
├── Aevatar.Authentication.*      # NyxID authentication provider
├── Aevatar.Mainnet.Host.Api      # Production unified host
└── Aevatar.Hosting               # Shared hosting infrastructure
test/                             # Unit, integration, and API tests
workflows/                        # YAML workflow definitions
docs/                             # Architecture documentation
```

---

## Build & Test

```bash
# Restore, build, test
dotnet restore aevatar.slnx --nologo
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo

# Domain-scoped builds
dotnet build aevatar.foundation.slnf
dotnet build aevatar.workflow.slnf

# CI architecture guards
bash tools/ci/architecture_guards.sh
```

---

## Documentation

### Architecture

- [Foundation Architecture](docs/architecture/FOUNDATION.md) — Event model, pipeline, actor lifecycle
- [Project Architecture](docs/architecture/PROJECT_ARCHITECTURE.md) — Layering, capability assembly, host boundaries
- [CQRS Architecture](docs/architecture/CQRS_ARCHITECTURE.md) — Write/read side, unified projection pipeline
- [Event Sourcing](docs/architecture/EVENT_SOURCING.md) — Event store, state replay, snapshots
- [Mainnet Architecture](docs/architecture/MAINNET_ARCHITECTURE.md) — Distributed deployment, Orleans clustering, infrastructure
- [Scripting Architecture](docs/architecture/SCRIPTING_ARCHITECTURE.md) — Script evolution lifecycle, compilation, runtime
- [Stream Forwarding](docs/architecture/STREAM_FORWARD_ARCHITECTURE.md) — Hierarchical event relay topology
- [LLM Streaming](docs/architecture/WORKFLOW_LLM_STREAMING_ARCHITECTURE.md) — End-to-end LLM streaming execution flow

### Guides

- [Workflow Guide](docs/guides/WORKFLOW.md) — Workflow engine design, step execution model, module packs
- [Workflow Primitives](docs/guides/WORKFLOW_PRIMITIVES.md) — Complete step type reference with YAML examples
- [Roles & Connectors](docs/guides/ROLE.md) — Workflow YAML roles, connector configuration, MCP/CLI/API as agent capabilities
- [Connector Reference](docs/guides/CONNECTOR.md) — Connector types, configuration format, examples
- [Workflow Chat API](docs/guides/workflow-chat-ws-api-capability.md) — SSE/WebSocket protocol details
- [.NET SDK](docs/guides/SDK_WORKFLOW_CHAT_DOTNET.md) — Client SDK for workflow chat integration
- [Configuration](src/Aevatar.Configuration/README.md) — API keys, secrets, connector configuration

### Integration

- [NyxID LLM Provider](docs/integration/nyxid-llm-provider-integration.md) — NyxID gateway routing, credential brokering, provider setup
- [NyxID Chatbot Protocol](docs/integration/CHATBOT_3RD_PARTY_INTEGRATION_SPEC.md) — Third-party AI service integration specification
- [MAF Integration](docs/integration/MAF-INTEGRATION.md) — Microsoft Agent Framework integration strategy

### Design Proposals

- [External Link Framework](docs/design/2026-03-31-external-link-framework.md) — WebSocket/gRPC/MQTT external connectivity
- [Multi-Agent Evolution](docs/design/2026-04-01-workflow-actor-multi-agent-evolution.md) — TaskBoard, native messaging, agent coordination RFC

### Projection & Read Models

- [Projection Core](src/Aevatar.CQRS.Projection.Core/README.md) — Unified projection lifecycle, coordinator, read model contracts
- [Workflow Projection](src/workflow/Aevatar.Workflow.Projection/README.md) — Workflow-specific read models and real-time output events
