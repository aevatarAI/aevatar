# NyxIdChat Committed Progress And Tool Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development for each behavior change and superpowers:verification-before-completion before delivery.

**Goal:** Resolve issue #2893 by streaming actor-committed NyxIdChat progress through the existing Projection Pipeline and by snapshotting provider-owned typed tool-card identity.

**Architecture:** Change A introduces the shared descriptor and caller-scoped NyxID identity mapping independently of live streaming. Change B introduces an actor-owned monotonic committed progress event and maps it one-to-one through Projection, AGUI, and SSE; completion remains final authority and snapshot synthesis is replay-only.

**Tech Stack:** .NET 10, C#, Protobuf, xUnit, FluentAssertions, ASP.NET Core SSE, React/TypeScript, Vitest.

## Global Constraints

- Preserve all pre-existing uncommitted changes and the current `feature/integrate` branch.
- Keep one committed EventEnvelope Projection Pipeline; never project transient events or write HTTP/SSE from Role/ChatRuntime.
- Keep accepted ACK semantics unchanged and add no process-local session/actor fact registry.
- Use `ChatStreamAsync` only; add no `ChatAsync`, `Task.Run`, callback state mutation, polling, or `Task.Delay` tests.
- Keep tool invocation identity separate from LLM routes and NyxID connections.
- Use protobuf typed fields for stable business/control semantics; add no metadata bag keys.
- Complete and push issue #2893 to `origin/feature/integrate` without a force push.

---

## Change A: Typed Tool-Card Identity

### Task A1: Shared Descriptor Contract

**Files:**
- Add: `src/Aevatar.Foundation.Abstractions/Tools/tool_presentation.proto`
- Modify: `src/Aevatar.Foundation.Abstractions/Aevatar.Foundation.Abstractions.csproj`
- Modify: `src/Aevatar.AI.Abstractions/ToolProviders/IAgentTool.cs`
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AGUI.Contracts/Aevatar.AGUI.Contracts.csproj`
- Modify: `src/Aevatar.AGUI.Contracts/agui_events.proto`
- Test: `test/Aevatar.AI.Tests/ToolPresentationDescriptorTests.cs`

- [ ] Write failing tests for generic fallback, provider snapshot cloning, and distinct invocation/display identity.
- [ ] Add descriptor enums, source-ref oneof messages, and NyxID identity fields.
- [ ] Add provider-owned descriptor to `IAgentTool`, `ToolCallEvent`, and AGUI `ToolCallStartEvent`.
- [ ] Run focused contract tests and verify GREEN.

### Task A2: Provider-Owned Descriptors And NyxID Authorities

**Files:**
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdConnectedServiceToolSource.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/ConnectedServiceProxyTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Web/Tools/WebFetchTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Web/Tools/WebSearchTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Web/Tools/AskUserTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.MCP/MCPToolAdapter.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Skills/UseSkillTool.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdConnectedServiceToolSourceTests.cs`
- Test: `test/Aevatar.AI.Tests/ToolPresentationDescriptorTests.cs`

- [ ] Write failing `/keys` + `/catalog` tests using distinct `m-alpha`, `wf-alpha`, and service-shaped ids where applicable; assert disconnected/inactive exclusion and preservation of all NyxID identity fields.
- [ ] Add typed key/catalog DTO parsing and caller-token joins; keep proxy OpenAPI fetch for admitted operations.
- [ ] Add explicit built-in, MCP, skill, and NyxID descriptors; keep generic fallback for unknown tools.
- [ ] Run NyxID/provider focused tests and verify GREEN.

### Task A3: Historical Snapshot Through AGUI, SSE, And Web

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatCompletionAguiFrameBuilder.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatSseWriter.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatAguiSseEventWriter.cs`
- Modify: `apps/aevatar-console-web/src/shared/agui/sseFrameNormalizer.ts`
- Modify: `apps/aevatar-console-web/src/shared/agui/runtimeEventSemantics.ts`
- Modify: `apps/aevatar-console-web/src/shared/agui/runtimeConversationPresentation.tsx`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatPresentation.tsx`
- Test: NyxIdChat AGUI/SSE tests and `sseFrameNormalizer.test.ts`

- [ ] Write failing transport and frontend tests proving the descriptor is snapshotted and display name survives later provider rename.
- [ ] Clone descriptor into ToolCall/AGUI start, serialize structured SSE JSON, normalize it, and retain it in stored tool-card state.
- [ ] Render snapshotted display name with invocation name retained for diagnostics/fallback.
- [ ] Run focused backend/frontend tests and TypeScript checking; verify GREEN.

---

## Change B: Actor-Committed Live Session Progress

### Task B1: Progress Protobuf And Actor Sequence

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Core/RoleGAgent.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatGAgentCommittedProgressTests.cs`

- [ ] Write failing reducer tests for per-session monotonic progress sequence and typed payload coverage.
- [ ] Add `RoleChatSessionProgressedEvent`, typed progress payloads, replay snapshot payload, and `last_progress_sequence` state.
- [ ] Persist text start/content/reasoning/media/usage/terminal progress through `PersistDomainEventAsync` and preserve transient parent publications for non-projection consumers where required.
- [ ] Run Role focused tests and verify GREEN.

### Task B2: Tool Lifecycle Before Execution

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/LLMProviders/LLMResponse.cs`
- Modify: `src/Aevatar.AI.Core/Chat/ChatRuntime.cs`
- Modify: `src/Aevatar.AI.Core/Tools/StreamingToolExecutor.cs` only if result carrier access requires it
- Test: `test/Aevatar.AI.Tests/ChatRuntimeToolProgressTests.cs`

- [ ] Write failing controlled-tool tests proving start is yielded before a synchronously or asynchronously completing tool and every result has a live carrier.
- [ ] Yield typed tool-start before calling `AddTool`; resume execution only after the consumer advances the iterator.
- [ ] Yield typed tool-result for all outcomes while retaining provider receipts and safe-history behavior.
- [ ] Cover provider-native and text-parsed tool calls; run focused tests and verify GREEN.

### Task B3: One-To-One Projection And Replay-Only Completion Expansion

**Files:**
- Modify: `src/Aevatar.AGUI.Contracts/agui_events.proto`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatProjectionSession.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatCompletionAguiFrameBuilder.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatAguiSseEventWriter.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatSseWriter.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatProjectionSessionTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatStreamIdentityAndTerminalTests.cs`

- [ ] Write failing projector tests: one committed progress produces one sequenced frame; normal completion produces none; explicit replay expands the committed snapshot.
- [ ] Map every progress payload to AGUI and propagate sequence to SSE.
- [ ] Embed the typed terminal tail in one committed completion fact and emit exactly one run terminal without expanding the live snapshot.
- [ ] Run focused projection/SSE tests and verify GREEN.

### Task B4: Controlled End-To-End Timeliness Test

**Files:**
- Add: `test/Aevatar.AI.Tests/NyxIdChatCommittedStreamingEndToEndTests.cs`

- [ ] Compose controlled `ILLMProvider -> RoleGAgent -> committed publisher -> NyxIdChat projector -> AGUI hub -> SSE writer -> Channel`.
- [ ] Assert first `TEXT_MESSAGE_CONTENT` is readable before provider completion is released.
- [ ] Assert `TOOL_CALL_START` is readable before tool completion is released and before `TOOL_CALL_END`/terminal.
- [ ] Assert strictly increasing committed sequences, every frame is immediately signaled, no repeated text, and exactly one terminal.
- [ ] Use only `TaskCompletionSource`/`Channel`; run stability guard and verify GREEN.

---

## Documentation And Delivery

**Files:**
- Modify: `docs/canon/nyxid-chat-api.md`
- Modify: `docs/canon/nyxid-connected-service-tools.md`
- Modify: `docs/canon/llm-streaming.md`
- Modify: `docs/adr/0015-agui-sse-projection-session-pipeline.md`

- [ ] Update canon and compact Mermaid diagrams with the repository-mandated init directive and quoted labels.
- [ ] Run focused NyxIdChat, CQRS interaction, tool-provider, frontend tests, and TypeScript checking.
- [ ] Run `dotnet build aevatar.slnx --nologo` and `dotnet test aevatar.slnx --nologo`.
- [ ] Run all requested stability, projection, architecture, and docs guards.
- [ ] Review the diff against issue #2893 and the pre-existing dirty-worktree boundary.
- [ ] Commit the two independently verifiable change sets, reference #2893, fetch/rebase if needed, rerun affected verification, and push `HEAD:feature/integrate` without force.
- [ ] Close issue #2893 with the pushed commit and verification summary.
