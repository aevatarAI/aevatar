# NyxID Lark Skill-Streaming Inventory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route a bound Lark sender's natural-language NyxID connected-service inventory question through AgentRun, `use_skill("nyxid")`, the sender-scoped typed inventory tool, and the existing CardKit streaming lifecycle.

**Architecture:** Delete the phrase-matched direct-reply branch so natural language has one authoritative AgentRun path. Preserve the sender's exact binding and external subject as durable typed context, then resolve two independent request-local capabilities: a remote-skill-read token for `use_skill` and an inventory token for `GET /api/v1/keys`; neither capability may fall back to the bot owner or be persisted. Keep `/init` and `/whoami` as deterministic slash commands.

**Tech Stack:** .NET 9, C#, Orleans GAgents, protobuf typed tool context, xUnit, FluentAssertions, NSubstitute, NyxID OAuth broker/token exchange, Ornn remote skills, Lark CardKit streaming.

## Global Constraints

- Interactive AI execution uses `ChatStreamAsync`; do not introduce `ChatAsync` on the NyxID Chat path.
- Natural-language intent and answer composition belong to AgentRun and loaded skills, not `ChannelConversationTurnRunner` phrase matching.
- `bindingId` and `AgentToolNyxIdAuthorityContext` are distinct durable identity facts; bearer tokens are transient and must not enter actor state.
- Remote skill reads and connected-service inventory each use their own narrow, typed sender-scoped capability contract.
- A bound sender path never substitutes a bot-owner token, channel registration token, guessed subject, or sandbox CLI login.
- Inventory uses the current sender's `GET /api/v1/keys`; it never runs `nyxid service list` or `code_execute`.
- Inventory failure is not proof that the sender is unbound and must not produce an unconditional `/init` recommendation.
- Preserve the `/init` authorize contract: send the complete external subject and never send `binding_grant_id`.
- Keep `ChannelNyxIdConnectedServiceInventoryToolSource` channel-local; do not add it to `workspace.default` or the global `IAgentToolSource` collection.
- Use different fixture values for binding, external subject, and service identities.
- Edit only the isolated worktree and push without force to `origin/feature/integrate` after verification.

---

### Task 1: Replace the fixed inventory reply contract with AgentRun routing

**Files:**
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelConversationTurnRunnerTests.cs`
- Delete: `test/Aevatar.GAgents.ChannelRuntime.Tests/NyxIdConnectedServiceInventoryIntentTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs`
- Delete: `agents/Aevatar.GAgents.NyxidChat/NyxIdConnectedServiceInventoryIntent.cs`
- Delete: `agents/Aevatar.GAgents.NyxidChat/NyxIdConnectedServiceInventoryReplyRenderer.cs`
- Delete: `agents/Aevatar.GAgents.NyxidChat/INyxIdConnectedServiceInventoryQuery.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ChannelNyxIdConnectedServiceInventoryToolSource.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`
- Modify: `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs`

**Interfaces:**
- Consumes: `IExternalIdentityBindingQueryPort.ResolveAsync(ExternalSubjectRef, CancellationToken)` and `NeedsLlmReplyEvent.ToolContext`.
- Produces: one ordinary `LlmReplyRequested` result whose tool context contains `AgentToolSenderBindingContext` and `AgentToolNyxIdAuthorityContext`; `ChannelNyxIdConnectedServiceInventoryToolSource` implements only `IAgentToolSource`.

- [ ] **Step 1: Rewrite the regression test to require AgentRun and typed authority**

Use distinct fixture values and assert this observable contract:

```csharp
result.Success.Should().BeTrue();
result.LlmReplyRequest.Should().NotBeNull();
adapter.Replies.Should().BeEmpty();
var context = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest!.ToolContext);
context.SenderBinding.BindingId.Should().Be("bnd-inventory-alpha");
context.NyxIdAuthority.Should().Be(new AgentToolNyxIdAuthorityContext(
    "lark", "scope-1", "ou-sender-alpha"));
```

Delete tests that assert direct query invocation or fixed failure rendering because they encode the product bug.

- [ ] **Step 2: Run the routing test and confirm RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~ChannelConversationTurnRunnerTests.RunInboundAsync_WhenBoundSenderAsksForNyxIdInventory_QueuesAgentRunWithExactAuthority"
```

Expected: FAIL because the current phrase matcher sends a direct fixed reply and returns no `LlmReplyRequest`.

- [ ] **Step 3: Delete the parallel path and write exact typed authority into the request**

Remove `_connectedServiceInventoryQuery`, its constructor argument, the inventory intent branch, and `HandleConnectedServiceInventoryAsync`. In `BuildLlmReplyRequestAsync`, retain `SenderBinding` and add:

```csharp
NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
    senderBinding.Subject.Platform,
    senderBinding.Subject.Tenant,
    senderBinding.Subject.ExternalUserId),
```

Delete the intent, renderer, and query-port files. Remove query parsing from the channel tool source so it implements only `IAgentToolSource`. Remove the query DI alias and composition assertion while preserving the channel-only tool-source assertions.

- [ ] **Step 4: Run routing and composition tests and confirm GREEN**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~ChannelConversationTurnRunnerTests.RunInboundAsync_WhenBoundSenderAsksForNyxIdInventory_QueuesAgentRunWithExactAuthority"
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~MainnetHostCompositionTests"
```

Expected: PASS and no direct platform reply.

- [ ] **Step 5: Commit the routing migration**

```bash
git add agents/Aevatar.GAgents.NyxidChat test/Aevatar.GAgents.ChannelRuntime.Tests test/Aevatar.Capabilities.Tests
git commit -m "Route NyxID inventory through AgentRun"
```

---

### Task 2: Preserve exact NyxID authority through deferred AgentRun state

**Files:**
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/AgentRunReplyGenerationExecutorSenderTokenTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/AgentRunReplyGenerationExecutor.cs`
- Modify: the focused `ConversationGAgent` request-persistence tests in `test/Aevatar.GAgents.ChannelRuntime.Tests/`
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs`

**Interfaces:**
- Consumes: `AgentToolNyxIdAuthorityContext.IsComplete`, `AgentToolExecutionContextMapper`, and existing protobuf field `nyx_id_authority`.
- Produces: deferred sender token re-mint uses only exact typed authority; authority-only durable context is not discarded.

- [ ] **Step 1: Write exact-authority and authority-only durability tests**

Make channel identity intentionally differ from NyxID authority:

```csharp
Channel = new AgentToolChannelContext("lark", "ou-channel-alpha", "scope-1", "msg-1", null),
NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
    "lark", "tenant-authority-alpha", "ou-authority-alpha"),
```

Assert the broker receives `tenant-authority-alpha` and `ou-authority-alpha`. Add a missing-authority case where channel fields remain complete but the broker is not called. Add a durable-request case whose only tool-context fact is complete `NyxIdAuthority` and assert it survives transient credential stripping.

- [ ] **Step 2: Run the tests and confirm RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~AgentRunReplyGenerationExecutorSenderTokenTests|FullyQualifiedName~ConversationGAgent"
```

Expected: FAIL because sender subject reconstruction currently reads channel fields and `HasDurableToolContext` does not inspect `NyxIdAuthority`.

- [ ] **Step 3: Use typed authority exclusively and retain it durably**

Implement subject reconstruction as:

```csharp
var authority = toolContext.NyxIdAuthority;
if (!authority.IsComplete)
    return false;
subject = new ExternalSubjectRef
{
    Platform = authority.Platform!.Trim().ToLowerInvariant(),
    Tenant = NormalizeOptional(authority.Tenant) ?? string.Empty,
    ExternalUserId = authority.ExternalUserId!.Trim(),
};
```

Add `context.NyxIdAuthority.IsComplete ||` to `HasDurableToolContext`. Update logs to describe missing typed authority, not missing channel fields.

- [ ] **Step 4: Run the focused tests and confirm GREEN**

Repeat Step 2. Expected: PASS; no identity reconstruction guesses remain.

- [ ] **Step 5: Commit typed authority propagation**

```bash
git add agents/Aevatar.GAgents.Channel.Runtime agents/Aevatar.GAgents.NyxidChat test/Aevatar.GAgents.ChannelRuntime.Tests
git commit -m "Preserve NyxID authority for deferred tools"
```

---

### Task 3: Add a narrow sender-scoped capability for remote skill reads

**Files:**
- Create: `src/Aevatar.AI.ToolProviders.Skills/IRemoteSkillAccessTokenResolver.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Skills/UseSkillTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Skills/SkillsAgentToolSource.cs`
- Modify: `test/Aevatar.AI.ToolProviders.Ornn.Tests/LocalSkillCatalogTests.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity.Abstractions/INyxIdSkillCapabilityIssuer.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/Broker/NyxIdRemoteCapabilityBroker.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/DependencyInjection/IdentityServiceCollectionExtensions.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/NyxIdRemoteCapabilityBrokerTests.cs`
- Create: `agents/Aevatar.GAgents.NyxidChat/ChannelRemoteSkillAccessTokenResolver.cs`
- Create: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelRemoteSkillAccessTokenResolverTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`

**Interfaces:**
- Produces: `IRemoteSkillAccessTokenResolver.ResolveAsync(string skillName, CancellationToken)` for transient remote-skill reads.
- Produces: `INyxIdSkillCapabilityIssuer.IssueByBindingIdAsync(ExternalSubjectRef, string, CancellationToken)` for an exact sender binding.
- Consumes: existing `IRemoteSkillFetcher.FetchSkillAsync(string, string, CancellationToken)` without changing Ornn transport.

- [ ] **Step 1: Write failing `UseSkillTool` resolver tests**

Define the new abstraction in the test contract:

```csharp
public interface IRemoteSkillAccessTokenResolver
{
    Task<string?> ResolveAsync(string skillName, CancellationToken ct = default);
}
```

Assert a resolver result `sender-skill-token` is passed to the remote fetcher even when ambient `NyxIdAccessToken` is `owner-token`. When the resolver returns null, assert the fetcher is not invoked, owner token is not used, and the result reports `access_denied` rather than `not_found`.

- [ ] **Step 2: Run and confirm RED**

```bash
dotnet test test/Aevatar.AI.ToolProviders.Ornn.Tests/Aevatar.AI.ToolProviders.Ornn.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~LocalSkillCatalogTests.UseSkillTool_RemoteSkillUsesResolvedRequestToken|FullyQualifiedName~LocalSkillCatalogTests.UseSkillTool_WhenRequestTokenResolutionFails_DoesNotUseAmbientOwnerToken"
```

Expected: build/test FAIL because the resolver contract and constructor argument do not exist.

- [ ] **Step 3: Implement transient token resolution**

Add an optional resolver to `UseSkillTool` and `SkillsAgentToolSource`. Resolve only remote-skill fetch tokens through it. If a configured resolver returns no token, return a sanitized `access_denied` load result. Do not place the result in tool context, workflow mount input, logs, cache, or actor state. If no resolver is configured, preserve existing non-channel ambient-token behavior.

- [ ] **Step 4: Write broker and channel resolver RED tests**

Broker test: cast to `INyxIdSkillCapabilityIssuer`, exchange `bnd-skill-alpha`, and assert `scope=proxy`, no `resource` form key, and no full-runtime resource validation.

Channel resolver cases:

```text
verified SenderNyxIdAccessToken -> reuse; issuer not called
binding + complete NyxIdAuthority -> issue for exact subject + binding
binding + incomplete authority -> null; never use owner NyxIdAccessToken
no binding -> preserve ordinary ambient NyxIdAccessToken behavior
```

- [ ] **Step 5: Run broker/resolver tests and confirm RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~NyxIdRemoteCapabilityBrokerTests.IssueRemoteSkillReadByBindingIdAsync|FullyQualifiedName~ChannelRemoteSkillAccessTokenResolverTests"
```

Expected: build/test FAIL because the issuer and resolver do not exist.

- [ ] **Step 6: Implement the narrow issuer and channel resolver**

Keep `INyxIdSkillCapabilityIssuer` independent from the inventory issuer. Share only a private broker helper for binding token exchange with proxy scope and no full-runtime resource check. In the channel resolver, a bound path accepts verified sender token first, otherwise exact `NyxIdAuthority + bindingId`; it never falls through to owner credentials. Register and inject the resolver into both `SkillsAgentToolSource` and the reply generator's fallback `UseSkillTool`.

- [ ] **Step 7: Run skill/inventory capability tests and confirm GREEN**

```bash
dotnet test test/Aevatar.AI.ToolProviders.Ornn.Tests/Aevatar.AI.ToolProviders.Ornn.Tests.csproj --nologo --filter "FullyQualifiedName~LocalSkillCatalogTests"
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~NyxIdRemoteCapabilityBrokerTests|FullyQualifiedName~ChannelRemoteSkillAccessTokenResolverTests|FullyQualifiedName~ChannelNyxIdConnectedServiceInventoryToolSourceTests"
```

Expected: PASS with separate sender-scoped skill and inventory capabilities.

- [ ] **Step 8: Commit remote skill capability support**

```bash
git add src/Aevatar.AI.ToolProviders.Skills agents/Aevatar.GAgents.Channel.Identity.Abstractions agents/Aevatar.GAgents.Channel.Identity agents/Aevatar.GAgents.NyxidChat test/Aevatar.AI.ToolProviders.Ornn.Tests test/Aevatar.GAgents.ChannelRuntime.Tests
git commit -m "Issue sender capability for remote skills"
```

---

### Task 4: Prove skill-first tool rounds and streamed final output

**Files:**
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ConversationReplyGeneratorTests.cs`
- Modify only if RED exposes missing wiring: `agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs`

**Interfaces:**
- Consumes: `UseSkillTool`, `ChannelNyxIdConnectedServiceInventoryToolSource`, `ILLMProvider.ChatStreamAsync`, and `IStreamingReplySink`.
- Produces: observable order `use_skill -> nyxid_service_inventory -> streamed final answer`.

- [ ] **Step 1: Add a three-round streaming regression test**

Use a provider whose `ChatStreamAsync` emits:

```text
round 1: ToolCall(use_skill, {"skill":"nyxid"})
round 2: after the use_skill result, ToolCall(nyxid_service_inventory, {})
round 3: after the inventory result, stream "你已连接 GitHub。" and finish
```

Wire a real `UseSkillTool` to a recording remote fetcher returning a `nyxid` skill definition and a typed inventory tool returning GitHub. Assert:

```csharp
provider.ObservedToolCalls.Should().Equal("use_skill", "nyxid_service_inventory");
provider.Requests.Should().HaveCount(3);
provider.Requests.SelectMany(request => request.Tools ?? [])
    .Should().NotContain(tool => tool.Name == "code_execute");
reply.Text.Should().Be("你已连接 GitHub。");
sink.Emissions.Last().Should().Be("你已连接 GitHub。");
```

Also assert the remote fetcher received the sender skill token and neither final text nor tool results expose `UNAUTHENTICATED`, `nyxid service list`, or unconditional `/init` guidance.

- [ ] **Step 2: Run and observe RED or existing-complete behavior**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~ConversationReplyGeneratorTests.GenerateReplyAsync_ForNyxIdInventory_UsesSkillThenTypedToolAndStreamsFinalAnswer"
```

Expected before Tasks 1-3 wiring is complete: FAIL if `UseSkillTool` sees owner authority, inventory lacks typed authority, or streaming rounds do not reach the sink. If it passes after those tasks, retain it as regression evidence and make no production-only change.

- [ ] **Step 3: Implement only wiring identified by RED**

Permitted production change: pass `IRemoteSkillAccessTokenResolver` into the exact `UseSkillTool` used by the channel reply generator. Do not add forced tool calls, phrase matching, a fixed renderer, or a second reply transport.

- [ ] **Step 4: Run streaming and existing CardKit tests**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~ConversationReplyGeneratorTests.GenerateReplyAsync_ForNyxIdInventory_UsesSkillThenTypedToolAndStreamsFinalAnswer|FullyQualifiedName~ConversationReplyGeneratorTests.GenerateReplyAsync_WithStreamingSink|FullyQualifiedName~LarkCardReplyStreamRenderer|FullyQualifiedName~TurnStreamingReplySink"
```

Expected: PASS using the existing sink/CardKit lifecycle.

- [ ] **Step 5: Commit the streaming regression**

```bash
git add agents/Aevatar.GAgents.NyxidChat test/Aevatar.GAgents.ChannelRuntime.Tests
git commit -m "Test skill-streamed NyxID inventory replies"
```

---

### Task 5: Align prompts and durable product documentation

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/Skills/system-prompt.md`
- Modify: `agents/Aevatar.GAgents.NyxidChat/Skills/system-skill-overlay-default.md`
- Modify: `docs/canon/nyxid-connected-service-tools.md`
- Modify: `docs/adr/0018-per-user-nyxid-binding-via-oauth-broker.md`

**Interfaces:**
- Produces: one durable rule that natural-language inventory loads `nyxid`, then uses the sender typed inventory tool, and never infers binding absence from a read failure.

- [ ] **Step 1: Replace the stale no-skill prompt exception**

Use this semantic contract in both prompt layers:

```markdown
For a read-only request asking which services the caller already has connected, first call
`use_skill(skill="nyxid")`, then call `nyxid_service_inventory`. The skill supplies current
NyxID semantics; the typed tool supplies the current sender's live inventory. Do not call
`code_execute`, a sandbox CLI, or `nyxid service list`. If inventory access fails, report a
temporary read failure without claiming the binding is absent or recommending `/init` unless
the binding is explicitly missing or revoked.
```

Remove every statement that calls inventory a no-skill exception.

- [ ] **Step 2: Correct canon and ADR descriptions**

Document the single path:

```text
Lark inbound -> LlmReplyRequested -> AgentRun -> ChatStreamAsync
  -> use_skill("nyxid") -> nyxid_service_inventory
  -> sender GET /api/v1/keys -> CardKit stream/finalize
```

State that remote skill read and inventory use separate narrow issuers while strict runtime readiness stays independent. Delete text saying natural-language inventory bypasses LLM/skills.

- [ ] **Step 3: Search for stale semantics**

```bash
rg -n 'inventory.*(不进入|without|bypass).*(LLM|skill)|Do not (load|call).*skill|typed-tool exception|WithoutStartingLlm' \
  agents/Aevatar.GAgents.NyxidChat docs/canon/nyxid-connected-service-tools.md docs/adr/0018-per-user-nyxid-binding-via-oauth-broker.md test
```

Expected: no inventory-specific fixed-path rule remains.

- [ ] **Step 4: Run docs and prompt checks**

```bash
bash tools/docs/lint.sh
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~BuiltInPromptFloorProvider|FullyQualifiedName~SystemSkillOverlay"
```

Expected: PASS and zero docs errors.

- [ ] **Step 5: Commit semantic documentation**

```bash
git add agents/Aevatar.GAgents.NyxidChat/Skills docs/canon/nyxid-connected-service-tools.md docs/adr/0018-per-user-nyxid-binding-via-oauth-broker.md
git commit -m "Document skill-streamed NyxID inventory semantics"
```

---

### Task 6: Verify, integrate, push, and complete production acceptance

**Files:**
- Verify all changed files; add no production change unless a verification failure identifies a root cause covered by this design.

**Interfaces:**
- Produces: a verified remote `origin/feature/integrate` commit and production evidence for one real Lark turn.

- [ ] **Step 1: Run focused project test suites**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo
dotnet test test/Aevatar.AI.ToolProviders.Ornn.Tests/Aevatar.AI.ToolProviders.Ornn.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter "FullyQualifiedName~MainnetHostCompositionTests"
```

Expected: zero failed tests.

- [ ] **Step 2: Re-run `/init` and OAuth broker regressions**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~SlashCommandHandlerTests|FullyQualifiedName~IdentityOAuthCallbackEndpointTests|FullyQualifiedName~NyxIdRemoteCapabilityBrokerTests"
```

Expected: PASS; authorize URLs retain external-subject fields and omit `binding_grant_id`.

- [ ] **Step 3: Run repository gates and full build**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
dotnet build aevatar.slnx --nologo
git diff --check
```

Expected: every command exits 0, docs lint reports zero errors, and build reports zero errors.

- [ ] **Step 4: Review the final diff against the approved spec**

```bash
git status --short --branch
git diff origin/feature/integrate...HEAD --stat
git diff origin/feature/integrate...HEAD -- \
  agents/Aevatar.GAgents.NyxidChat \
  agents/Aevatar.GAgents.Channel.Identity.Abstractions \
  agents/Aevatar.GAgents.Channel.Identity \
  src/Aevatar.AI.ToolProviders.Skills \
  test/Aevatar.GAgents.ChannelRuntime.Tests \
  test/Aevatar.AI.ToolProviders.Ornn.Tests \
  docs/canon/nyxid-connected-service-tools.md \
  docs/adr/0018-per-user-nyxid-binding-via-oauth-broker.md
```

Expected: no matcher/fixed renderer/query adapter, bearer persistence, or bot-owner fallback remains.

- [ ] **Step 5: Merge latest integration without force**

```bash
git fetch origin feature/integrate
git merge --no-edit origin/feature/integrate
```

If the merge changes scoped files, repeat Steps 1-3.

- [ ] **Step 6: Push and verify remote identity**

```bash
git push origin HEAD:feature/integrate
git fetch origin feature/integrate
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/feature/integrate)"
```

Expected: push succeeds without force and the fetched remote tip equals local HEAD.

- [ ] **Step 7: Perform real Lark acceptance after deployment**

From sender `ou_937adb03f3538c5e041bb3034c4e348e`, send:

```text
我在 nyxid 上有什么服务
```

Require production evidence of AgentRun, streamed LLM chunks, `use_skill("nyxid")`, `nyxid_service_inventory`, sender `GET /api/v1/keys`, and CardKit create/stream/finalize. Reject acceptance if the reply/logs show direct inventory routing, `code_execute`, `nyxid service list`, raw `UNAUTHENTICATED`, or unconditional `/init`. Only the real Lark card plus matching logs completes the fix.
