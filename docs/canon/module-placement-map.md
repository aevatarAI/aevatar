---
title: "Module Placement Map"
status: active
owner: architecture
---

# Module Placement Map

本文是新增能力落点的第一入口。它不替代各 topic 的 canon 文档，也不枚举所有 `.csproj` 或 `ProjectReference`；它只回答一个问题：新功能先放在哪一类模块里，继续读哪份权威文档。

适用场景：

1. 新增一个 feature family，先判断它属于哪个 tier。
2. 给已有能力补 command/query、projection/readmodel、provider 或 host bootstrap。
3. review 时判断 PR 是否把业务编排放进了 Host/API，或把查询绕回了 write model。

不适用场景：

1. 生成完整依赖图。
2. 为每个项目名建立 lint 门禁。
3. 为单个 reducer、xUnit taxonomy、历史审计图建立新的 canon authority。

## 1. Placement 判定顺序

1. **先找权威事实拥有者**：稳定业务事实归 actor/GAgent；查询副本归 readmodel；外部系统事实只能在 adapter 边界映射。
2. **再选写入口**：外部请求进入 Application command/query surface；Host/API 只做协议解析、鉴权、组合和配置。
3. **再选读侧**：对外查询默认读 readmodel；实时输出、AGUI、SSE、WS 也走统一 Projection Pipeline 的观察分支。
4. **最后选 provider/bootstrap**：第三方 SDK、数据库、LLM、渠道、NyxID、ChronoStorage、Ornn 等只出现在 Infrastructure/provider 或 Host composition，不进入 Domain/Application 核心语义。

## 2. Tier 速查

| Tier | 放置规则 | 典型项目 |
|---|---|---|
| Stable primitives | 只有跨 feature family 复用、语义稳定且不能由上层组合表达的原语才进入 Foundation/CQRS Core。 | `Aevatar.Foundation.*`, `Aevatar.CQRS.Core*`, `Aevatar.CQRS.Projection.Core*` |
| Capability core | 某个业务能力自己的 actor、domain state、command/query contract、projection contract 放在该能力族。 | `Aevatar.Workflow.*`, `Aevatar.Scripting.*`, `Aevatar.Studio.*`, `Aevatar.GAgentService.*` |
| Extension/plugin | 只扩展既有 capability 的模块包，不拥有第二套主链路。 | `src/workflow/extensions/*`, `Aevatar.AI.ToolProviders.*`, `Aevatar.AI.LLMProviders.*` |
| Provider/adapter | 外部协议、存储、LLM、channel、NyxID/ChronoStorage/Ornn SDK 调用与技术实现。 | `*.Infrastructure`, `*.Providers.*`, `*.LLMProviders.*`, `*.ToolProviders.*` |
| Host/bootstrap | ASP.NET endpoint、配置绑定、DI 组合、health/status、auth middleware。 | `Aevatar.Mainnet.Host.Api`, `Aevatar.Workflow.Host.Api`, `*.Hosting` |

## 3. Feature-family placement map

| Feature family | Tier / owner | Actor/domain owner | Application command/query | Projection/readmodel registration | Infrastructure/provider | Host/bootstrap | Cross-actor protocol |
|---|---|---|---|---|---|---|---|
| Foundation actor runtime | Stable primitives | Actor/GAgent state and event pipeline stay in `Aevatar.Foundation.Core`; runtime topology/lifecycle in `Aevatar.Foundation.Runtime*`. | Do not add business application services here; expose only narrow runtime ports such as `IActorRuntime`, `IActorDispatchPort`, `IEventPublisher`. | Foundation may publish committed-state observation input, but business readmodels live in capability projection projects. | Local, Orleans, Kafka, Garnet and other runtime/storage adapters live under `Aevatar.Foundation.Runtime.Implementations.*` or persistence implementations. | Runtime provider selection and `AddAevatarDefaultHost()` integration stay in hosting/bootstrap code. | `EventEnvelope` is the runtime message envelope; domain facts still require explicit persisted state events. |
| CQRS command skeleton | Stable primitives | No business actor owner; owns command lifecycle abstractions only. | `Aevatar.CQRS.Core*` owns normalize/target/context/envelope/dispatch/receipt/observe templates and generic ports. | Does not materialize readmodels; observation lifecycle only attaches existing projection sessions when required. | Dispatch adapter uses `IActorDispatchPort`; no parallel command bus. | Host endpoints call capability Application contracts, not CQRS internals directly. | ACK means accepted for dispatch unless a stronger contract is explicitly modeled. |
| Projection pipeline core | Stable primitives | Projection scope/session/materialization runtime facts are actor-owned by Projection Core scope actors. | Query ports stay capability-specific; Projection Core provides lifecycle and fan-out abstractions. | `Aevatar.CQRS.Projection.Core*`, `.Runtime*`, `.Stores.Abstractions` own common pipeline contracts; reducers/projectors register by exact event type. | Store/provider bindings live in `Aevatar.CQRS.Projection.Providers.*`; provider selection is explicit. | Host registers selected projection providers and capability projection modules; it must not unconditionally register competing stores. | Projection consumes committed actor facts or same-origin durable feeds; no query-time replay or priming. |
| Workflow execution | Capability core | `WorkflowGAgent` owns definition facts; `WorkflowRunGAgent` owns one run's execution facts; workflow lease/schedule actors own their own facts. | `Aevatar.Workflow.Application*` owns chat, resume, signal, fork, webhook, and query facades over the CQRS command skeleton. | `Aevatar.Workflow.Projection` owns current-state readmodels and durable artifacts; AGUI adapter maps the same envelope input to run-event streams. | `Aevatar.Workflow.Infrastructure` owns IO and adapter implementations; AI integration stays in `Aevatar.Workflow.Integration.AI`. | `Aevatar.Workflow.Host.Api` exposes HTTP/SSE/WS and registers workflow capability; it does not execute workflow business logic. | Cross-run and cross-actor waits use command/event continuation, not synchronous actor query/reply. |
| Workflow extensions and Maker | Extension/plugin | Extension modules do not own a second workflow actor model; they contribute step/module behavior to workflow run actors. | Extensions expose module packs or typed capability hooks consumed by workflow Application/Core. | Extensions reuse workflow/CQRS projection inputs; they do not create parallel projection chains. | Extension-specific IO adapters stay inside the extension project or an infrastructure package owned by that extension. | Mainnet enables Maker via `AddAevatarPlatform(options => options.EnableMakerExtensions = true)`; Workflow Host may omit it. | Extension modules publish typed step/domain events or annotations through the workflow protocol; no `/api/maker/*` side channel. |
| Scripting | Capability core | `ScriptDefinitionGAgent`, `ScriptBehaviorGAgent`, `ScriptEvolutionSessionGAgent`, `ScriptEvolutionManagerGAgent`, and `ScriptCatalogGAgent` own scripting facts. | `Aevatar.Scripting.Application` owns definition/provisioning/runtime dispatch, evolution interaction, and readmodel query services. | `Aevatar.Scripting.Projection` materializes definition snapshots, catalog entries, execution sessions, current-state documents, native docs, and native graphs from committed facts. | `Aevatar.Scripting.Infrastructure` owns Roslyn compilation, artifact loading, and technical ports. | `Aevatar.Scripting.Hosting` wires DI, Host API, and JSON/protobuf boundary adaptation. | Script behavior may publish/send typed messages and self continuations; reads of other actors go through formal query/readmodel ports. |
| AI LLM providers | Extension/plugin | LLM provider adapters do not own business facts; conversation facts remain with workflow, chat, scripting, or capability actors. | Provider-neutral request/stream abstractions live in `Aevatar.AI.Abstractions` / `Aevatar.AI.Core`; user-facing realtime paths use streaming. | AI projection reducers live in `Aevatar.AI.Projection` only for common AI event shapes; capability readmodels stay with the owning capability. | Provider implementations live in `Aevatar.AI.LLMProviders.*`, including NyxID-backed and MEAI/Tornado adapters. | Host/bootstrap selects provider implementations and credentials; it does not branch business behavior by provider. | Tool calls, reasoning, tool results, and completion flow through the same streaming chain as text. |
| AI tool providers | Extension/plugin | Tool providers do not own workflow/run/session facts unless they define their own actor capability. | Tool contracts and discovery sit behind AI/tool provider ports; command admission belongs to the target capability. | Tool execution results only become readmodel facts after the owning actor commits domain events. | Tool adapters live in `Aevatar.AI.ToolProviders.*` for Web, MCP, Skills, Workflow, ServiceInvoke, Channel, ChronoStorage, Ornn, NyxId, Lark, Telegram, and related surfaces. | Host registers selected tool providers, credentials, scopes, and policies. | Cross-capability invocation uses typed command/event or accepted/observe contracts, not generic request-reply shortcuts. |
| GAgent service and registry | Capability core | Registry/scope membership facts are owned by `GAgentRegistryGAgent` or an explicitly modeled distributed authority. | Command/admission/query surfaces live in `Aevatar.GAgentService.Application*` and related abstractions; admission is distinct from list query. | Registry current-state readmodels are query replicas and cannot authorize command admission by themselves. | Infrastructure owns registry persistence, provider implementations, and adapters. | Hosting projects register service endpoints and capability composition. | Target actor ids are opaque; scope admission uses typed registry contracts, not actor id parsing or runtime side reads. |
| Studio and product surface | Capability core | Studio domain actors own Studio-specific facts; they do not redefine workflow, scripting, or registry authority. | `Aevatar.Studio.Application` owns product command/query facades and composes lower capability ports. | `Aevatar.Studio.Projection` owns Studio readmodels that have explicit UI/query consumers. | `Aevatar.Studio.Infrastructure` owns persistence and external adapters. | `Aevatar.Studio.Hosting` wires product endpoints and UI-facing composition. | Studio orchestration should dispatch typed commands to owning capabilities and read their readmodels. |
| Scheduled workflow dispatch | Capability core / extension | `ScheduledDispatchGAgent` owns schedule facts; workflow/team actors own execution facts. The retired `SkillRunnerGAgent` model is cleanup-only historical state, not a runtime owner. | Scheduled dispatch command/query surfaces return accepted receipts and stable ids; query reads readmodels. | Schedule projection materializes current state and freshness from committed actor state. | Trigger adapters, executor ports, and Ornn/skill invocation adapters stay in provider/infrastructure packages. | Host exposes create/update/enable/disable/list/preview/run-now surfaces and registers replay stores or executors. | Timers, retries, and external triggers re-enter the owning actor as events; callbacks never mutate state directly. |
| Channels and IM adapters | Provider/adapter plus capability core where conversation facts exist | Transcript belongs to `ChatConversationGAgent`; execution state belongs to its turn/session actor; cross-conversation user facts belong to `UserMemoryGAgent`. Platform adapters own none of them. | Application command/admission surfaces normalize inbound activities and proactive sends into typed commands; prompt context is derived only for the next LLM call. | Transcript, execution, and user-memory readmodels each materialize their own owner's committed facts; see [conversation-context-and-memory.md](conversation-context-and-memory.md). | Channel-specific transport/outbound/rendering adapters live under channel/platform provider projects; NyxID relay use stays at adapter boundary. | Host endpoints verify signatures, normalize payloads, and register channel adapters; they acknowledge only honest acceptance. | Inbound delivery is committed or actor-owned before ack when required; heavy LLM/tool work runs in run-scoped actors. |
| Authentication and NyxID integration | Provider/adapter | Auth facts owned by the auth capability or external NyxID service remain separate from workflow/AI facts. | Application layers consume typed identity/admission results, not NyxID-specific payload bags. | Auth readmodels exist only when this repository owns a stable query consumer. | NyxID providers live in `Aevatar.Authentication.Providers.NyxId`, `Aevatar.AI.LLMProviders.NyxId`, or `Aevatar.AI.ToolProviders.NyxId`; use only current public NyxID surfaces. | Host wires auth middleware, credential settings, and service-account policies. | Do not require NyxID schema or endpoint changes for aevatar features; map external responses into internal typed contracts. |
| Storage and graph/search providers | Provider/adapter | Business state is not owned by Elasticsearch, Neo4j, ChronoStorage, S3, or document stores. | Application query ports read capability readmodels through typed query contracts. | Store dispatch and schema fingerprints live in projection provider/storage abstractions; readmodel version comes from the authoritative actor. | Provider code lives in `Aevatar.CQRS.Projection.Providers.*` or capability infrastructure/tool providers. | Host selects a single coherent provider set per deployment. | Stores are materialization targets; they never decide business completion or ownership. |
| Observability and status dashboard | Capability core / Host surface | Probe actors own probe configuration and outcomes; dashboard Host owns presentation only. | Query ports expose status readmodels and freshness; command surfaces configure probes through actors. | Status/readmodel freshness documents are projection outputs from committed probe facts. | Probe executors are DI strategies called by probe actors during actor turns. | `/status` and `/api/status` live in Host; pages do not create business facts. | Probe ticks are durable self events and must be reconciled by the owning actor. |
| SDK and client-facing contracts | Boundary package | SDKs do not own server-side facts. | SDK calls target Host/Application contracts; they should preserve accepted receipt and readmodel freshness semantics. | SDKs may expose readmodel DTOs but do not define readmodel authority. | Transport clients, serialization adapters, and generated boundary types live in SDK projects. | Host remains the protocol authority; SDK defaults must follow documented ports and avoid banned ports. | Client retries use stable command/correlation ids where the server contract supports idempotency. |

## 4. Common placement decisions

### 4.1 Adding a new actor-owned business fact

1. Put the command/event/state contract with the capability that owns the fact.
2. Implement the actor/GAgent in that capability's Core/Domain tier.
3. Add Application command/admission/query ports that express business semantics.
4. Publish committed facts into the unified projection pipeline.
5. Add readmodels only after declaring the consumer, query entry, and freshness/version semantics.
6. Register Host endpoints only as protocol adapters over the Application ports.

### 4.2 Adding a new external provider

1. Keep external SDK names and payload formats inside provider/adapter projects.
2. Map external responses into internal typed contracts before entering Application/Core.
3. Register provider selection in Host/bootstrap.
4. Do not change NyxID, ChronoStorage, Ornn, or other sibling repositories for an aevatar feature unless this repository has found a confirmed bug in their published surface.

### 4.3 Adding a new readmodel

1. Name the consumption scenario first: UI, search, graph query, API DTO, realtime stream, or operational dashboard.
2. Use the owning actor's committed version or equivalent watermark.
3. Materialize from committed actor facts or `CommittedStateEventPublished(state_event + state_root)`.
4. Keep query ports read-only; no query-time projection activation, replay, priming, index repair, or actor creation.

### 4.4 Adding realtime output

1. Reuse Projection Pipeline session/observation surfaces.
2. Attach to existing observation sessions before dispatch when the command contract requires live output.
3. Return accepted-only receipts for dispatch-only commands; completion and freshness are observed asynchronously.
4. Do not create an in-process session registry keyed by actor/run/session id.

## 5. Reading map

| Need | Continue with |
|---|---|
| Runtime, EventEnvelope, actor basics | [architecture.md](architecture.md) |
| CQRS command skeleton and projection constraints | [cqrs-projection.md](cqrs-projection.md) |
| Architecture vocabulary and module depth language | [architecture-vocabulary.md](architecture-vocabulary.md) |
| Actor identity and evolution decisions | [actor-evolution.md](actor-evolution.md) |
| Workflow runtime and primitives | [workflow-runtime.md](workflow-runtime.md), [workflow-primitives.md](workflow-primitives.md) |
| Scripting current architecture | [scripting.md](scripting.md) |
| Registry ownership and admission | [gagent-registry-ownership.md](gagent-registry-ownership.md) |
| Channel architecture | [aevatar-channel-architecture.md](aevatar-channel-architecture.md) |
| LLM streaming | [llm-streaming.md](llm-streaming.md) |
| NyxID service/tool integration | [nyxid-connected-service-tools.md](nyxid-connected-service-tools.md), [nyxid-llm-integration.md](nyxid-llm-integration.md) |
| Scheduled workflow dispatch | [scheduled-skill-runners.md](scheduled-skill-runners.md) |
| Status dashboard and probes | [status-dashboard.md](status-dashboard.md) |
