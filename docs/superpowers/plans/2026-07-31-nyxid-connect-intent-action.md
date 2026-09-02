# NyxID Connect Intent Typed Action Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route natural NyxID service-connect requests through the existing typed readiness/action path instead of stopping after catalog discovery with CLI prose.

**Architecture:** Preserve the actor-owned persisted Agent Profile as authority. Carry its existing side-effect enum through the classifier boundary, use that semantic evidence to select the intent producing the user's final outcome, and make the NyxID Chat kernel require typed readiness after catalog slug resolution. The existing receipt, action commit, projection, SSE, and exact-retry paths remain unchanged.

**Tech Stack:** .NET 10, Protobuf-generated Agent Profile contracts, streaming LLM provider boundary, xUnit, FluentAssertions.

## Global Constraints

- Modify only Aevatar; do not modify NyxID or nyxid-chat.
- Reuse `AgentProfileSideEffectClass`; do not add keyword regexes, provider/slug special cases, open bags, or a parallel routing path.
- Do not restore deleted Agent Profile rollout files or configuration.
- Do not widen discovery intent authority or synthesize actions at the HTTP boundary.
- Preserve the existing schema-v4 action receipt, actor commit, projection, SSE terminal, and exact-retry semantics.
- Test changes must pass `tools/ci/test_stability_guards.sh`.
- Production user calls must use only `nyxid proxy request aevatar`.

---

### Task 1: Preserve typed side-effect routing evidence

**Files:**
- Modify: `src/Aevatar.AI.Core/AgentProfiles/IAgentProfileTurnClassifier.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/AgentProfiles/AgentProfileTurnCatalogMaterializer.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/AgentProfiles/StreamingAgentProfileTurnClassifier.cs`
- Modify: `test/Aevatar.AI.Tests/AgentProfileTurnCatalogMaterializerTests.cs`
- Modify: `test/Aevatar.AI.Tests/StreamingAgentProfileTurnClassifierTests.cs`

**Interfaces:**
- Consumes: `AgentProfileSkillMember.SideEffectClass`.
- Produces: `AgentProfileTurnClassificationCandidate.SideEffectClass` and classifier JSON `side_effect_class`.

- [ ] **Step 1: Write failing contract tests**

Assert that an `ExternalHandoff` Profile member reaches the recorded classification
candidate unchanged and that the provider request contains the literal JSON value
`"side_effect_class":"external_handoff"`.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~StreamingAgentProfileTurnClassifierTests|FullyQualifiedName~AgentProfileTurnCatalogMaterializerTests.MaterializeAsync_ClassifierMatch'
```

Expected: compilation fails because the candidate contract has no side-effect field.

- [ ] **Step 3: Implement the minimum strong typed mapping**

Add the existing enum to the candidate record, map `member.SideEffectClass`, and serialize
its snake-case name with `JsonNamingPolicy.SnakeCaseLower`. Update existing test fixtures
to provide explicit enum values.

- [ ] **Step 4: Re-run focused tests and verify GREEN**

Run the Step 2 command. Expected: zero failures.

### Task 2: Route by final outcome and forbid catalog substitution

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/AgentProfiles/StreamingAgentProfileTurnClassifier.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/Skills/system-prompt.md`
- Modify: `test/Aevatar.AI.Tests/StreamingAgentProfileTurnClassifierTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatSystemPromptTests.cs`

**Interfaces:**
- Consumes: classifier candidates with `side_effect_class`.
- Produces: final-outcome classifier instruction and typed-handoff kernel invariant.

- [ ] **Step 1: Write and run a failing classifier instruction test**

Assert the actual provider system message says final outcome wins over a prerequisite
step and uses `external_handoff` versus `read_only` as semantic evidence. Run the
classifier test and observe the assertion fail against the current generic instruction.

- [ ] **Step 2: Implement and verify the classifier instruction**

Change only the classifier system message, then rerun its focused tests to zero failures.

- [ ] **Step 3: Write and run a failing kernel invariant test**

Assert the composed kernel distinguishes catalog definitions from connected inventory,
requires `nyxid_require_service` after a connect slug is resolved, and forbids CLI or
credential instructions as a replacement for the typed action.

- [ ] **Step 4: Implement and verify the kernel invariant**

Add the minimum invariant text under Tool Use Policy, then rerun
`NyxIdChatSystemPromptTests` and the existing canonical profiled rich-card test.

### Task 3: Preserve verified Ready outcomes

**Files:**
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdRequireServiceTool.cs`
- Modify: `test/Aevatar.AI.Tests/ToolProviderHttpClientRegistrationTests.cs`

- [x] **Step 1: Reproduce the provider receipt failure**

Run the real tool against a visible service and assert that it returns a typed Success
receipt. Verify RED against the old `Ready -> null` behavior.

- [x] **Step 2: Return the verified typed receipt**

For `Ready && !blocked`, return Success with the original typed readiness result and no
authorization action. Keep all other readiness branches unchanged.

- [x] **Step 3: Verify the canonical finalizer boundary**

Pass the real tool result through `ToolCallReceiptFinalizer` and prove it remains a
non-synthetic Success instead of becoming `tool_outcome_unknown`.

### Task 4: Verify, integrate, and deploy safely

**Files:** all files above only.

- [ ] **Step 1: Run repository guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/agent_profile_governance_guard.sh
bash tools/ci/architecture_guards.sh
```

- [ ] **Step 2: Run requested project verification**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --no-restore
dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj --no-restore
dotnet build aevatar.slnx --no-restore --nologo --verbosity minimal
```

- [ ] **Step 3: Commit and push without force**

Fetch `origin`, integrate any new `origin/feature/integrate` commits non-destructively,
rerun affected checks, commit `Route NyxID connect intents to typed actions`, and push:

```bash
git push origin HEAD:feature/integrate
```

- [ ] **Step 4: Verify the deployed image in production**

Wait until the Aevatar image includes the new commit. Then submit new natural Chinese and
English connect turns with unique client request IDs through `nyxid proxy request aevatar`.
Require one `nyxid.action.request`, the exact catalog slug, and `RUN_FINISHED blocked`;
repeat the exact request to verify terminal replay and no duplicate action/state/history.
Also verify an already-connected service produces a successful `nyxid_require_service`
outcome with no `tool_outcome_unknown` and no rich card.
