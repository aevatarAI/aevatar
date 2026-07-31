# Agent Profile Guided Creation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose exact Ornn declared tools and turn the existing Agent Profile Admin surface into a guided, honest create-and-edit workflow, then publish a personal aevatar-operator Profile from real aevatar-platform skills.

**Architecture:** Reuse IExactOrnnSkillResolver, the same exact-package authority used by Profile publication, to populate the Host DTO; do not duplicate Ornn integrity logic. Keep the existing actor-backed Profile create and draft commands unchanged, and let the browser orchestrate them through their 202 receipts and canonical read-model outcomes. The Admin Console remains one embedded HTML asset with local transient form state only.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, Protobuf-backed Agent Profile contracts, xUnit, FluentAssertions, embedded HTML/CSS/vanilla JavaScript, Node vm behavior tests, NyxID CLI.

## Global Constraints

- Preserve Domain / Application / Infrastructure / Host layering; Host maps typed contracts and contains no new Profile business state machine.
- mine/ Profiles use the authenticated server-provided scope; never derive scopeId from a NyxID subject or UUID shape.
- system/ mutations remain protected by the existing Aevatar Admin authorizer.
- Keep only canonical Admin route #/agent-profiles; do not restore #/agentProfiles.
- Reuse IExactOrnnSkillResolver; do not expose raw Ornn JSON or copy exact package validation into Host.
- Keep 202 semantics honest: accepted is not committed, projected, validated, or published.
- Require Idempotency-Key for mutations and If-Match for draft, publication, and binding mutations exactly as the existing API requires.
- Do not add a composite create API, second Profile state machine, new UI dependency, auto-publish, auto-bind, or runtime hot update.
- skillHash is selection-time review evidence. Do not add it to the draft DTO; the server writes SealedSkillSha256 only while publishing.
- Preserve existing user-authored tool policy entries when exact declared tools are added; never silently remove maximum policy entries.
- Use existing Console tokens, native details, the 768px breakpoint, keyboard controls, text-plus-color states, and aria-live status.
- Production API mutations must use nyxid proxy request aevatar; browser state is visual acceptance only.

---

### Task 1: Return exact declared tools from the authoritative resolver

**Files:**
- Modify: src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs
- Modify: src/Aevatar.Mainnet.Host.Api/Skills/UserSkillCatalogModels.cs
- Modify: src/Aevatar.Mainnet.Host.Api/Skills/UserSkillCatalogQueryService.cs
- Modify: test/Aevatar.AI.ToolProviders.Ornn.Tests/OrnnExactAgentProfileSkillResolverTests.cs
- Create: test/Aevatar.Capabilities.Tests/UserSkillCatalogQueryServiceTests.cs
- Modify: test/Aevatar.Capabilities.Tests/WorkflowSkillsExactDetailEndpointTests.cs

**Interfaces:**
- Consumes: IExactOrnnSkillResolver.ResolveAsync(string, ExactRemoteSkillRef, CancellationToken) and ResolvedOrnnSkillPackage.
- Produces: UserExactSkillDetail(string Guid, string Name, string LiteralVersion, string Publisher, string SkillHash, IReadOnlyList<string> DeclaredToolNames).
- Produces: resolver diagnostic ORNN_SKILL_ACCESS_DENIED for exact Ornn 403; Host maps it back to HTTP 403 without putting HTTP status in the Application contract.

- [x] **Step 1: Write failing resolver and Host contract tests**

Change the resolver failure theory so exact 403 has a distinct typed meaning:

~~~csharp
[Theory]
[InlineData(true, HttpStatusCode.Forbidden, "ORNN_SKILL_ACCESS_DENIED")]
[InlineData(false, HttpStatusCode.Forbidden, "ORNN_SKILL_ACCESS_DENIED")]
[InlineData(false, HttpStatusCode.NotFound, "ORNN_SKILL_NOT_FOUND")]
[InlineData(true, HttpStatusCode.InternalServerError, "ORNN_DEPENDENCY_UNAVAILABLE")]
[InlineData(false, HttpStatusCode.ServiceUnavailable, "ORNN_DEPENDENCY_UNAVAILABLE")]
public async Task ResolveAsync_ShouldMapExactEndpointFailuresWithoutFallback(
    bool failDetail,
    HttpStatusCode status,
    string expectedCode)
~~~

In WorkflowSkillsExactDetailEndpointTests, construct and assert the expanded response:

~~~csharp
new UserExactSkillDetail(
    "11111111-2222-3333-4444-555555555555",
    "research",
    "1.2",
    "publisher-alpha",
    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    ["lookup", "search"])

json.Value!.DeclaredToolNames.Should().Equal("lookup", "search");
~~~

Create UserSkillCatalogQueryServiceTests with a substituted resolver. The success case proves the service passes the literal reference to the resolver and maps its typed package without another Ornn exact read:

~~~csharp
[Fact]
public async Task GetExactSkillAsync_ShouldMapAuthoritativePackageAndDeclaredTools()
{
    var sha256 = ByteString.CopyFrom(Enumerable.Range(0, 32)
        .Select(static value => (byte)value).ToArray());
    var resolver = Substitute.For<IExactOrnnSkillResolver>();
    resolver.ResolveAsync(
            "token-alpha",
            Arg.Is<ExactRemoteSkillRef>(reference =>
                reference.Guid == SkillGuid && reference.LiteralVersion == "1.4"),
            Arg.Any<CancellationToken>())
        .Returns(ExactOrnnSkillResolutionResult.Success(new ResolvedOrnnSkillPackage
        {
            SkillGuid = SkillGuid,
            LiteralVersion = "1.4",
            CanonicalName = "aevatar-operations",
            PublisherId = "aevatar-platform",
            SkillSha256 = sha256,
            DeclaredToolNames = ["aevatar_read", "aevatar_write"],
        }));
    var service = NewService(resolver);

    var result = await service.GetExactSkillAsync(
        "token-alpha", SkillGuid, "1.4", CancellationToken.None);

    result.Error.Should().BeNull();
    result.Detail!.Publisher.Should().Be("aevatar-platform");
    result.Detail.SkillHash.Should().Be(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    result.Detail.DeclaredToolNames.Should().Equal("aevatar_read", "aevatar_write");
}
~~~

Add a second service test returning Failure("ORNN_SKILL_ACCESS_DENIED"); assert Detail is null and UpstreamStatus is 403. The helper creates a real OrnnSkillClient with a handler that throws if used, a substituted IRemoteSkillFetcher, and the supplied resolver:

~~~csharp
private static UserSkillCatalogQueryService NewService(IExactOrnnSkillResolver resolver)
{
    var nyxId = new NyxIdApiClient(
        new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
        new HttpClient(new RejectingHandler()));
    var ornn = new OrnnSkillClient(
        new OrnnOptions { NyxIdSlug = "ornn-api" }, nyxId);
    return new UserSkillCatalogQueryService(
        ornn,
        Substitute.For<IRemoteSkillFetcher>(),
        resolver);
}

private sealed class RejectingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "A literal exact read must use the resolver only.");
}
~~~

- [x] **Step 2: Run tests and verify RED**

~~~bash
dotnet test test/Aevatar.AI.ToolProviders.Ornn.Tests/Aevatar.AI.ToolProviders.Ornn.Tests.csproj --nologo --filter FullyQualifiedName~OrnnExactAgentProfileSkillResolverTests
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~UserSkillCatalogQueryServiceTests|FullyQualifiedName~WorkflowSkillsExactDetailEndpointTests'
~~~

Expected: resolver assertion fails because 403 is still ORNN_DEPENDENCY_UNAVAILABLE; Capabilities compilation fails because the DTO lacks DeclaredToolNames and the query service lacks the resolver constructor parameter.

- [x] **Step 3: Implement the minimum authoritative mapping**

Map only exact 403 differently in OrnnExactAgentProfileSkillResolver.MapReadFailure:

~~~csharp
read.ProxyStatus switch
{
    403 => ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_ACCESS_DENIED"),
    404 => ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_NOT_FOUND"),
    null => null,
    _ => ExactOrnnSkillResolutionResult.Failure("ORNN_DEPENDENCY_UNAVAILABLE"),
};
~~~

Expand the Host record:

~~~csharp
public sealed record UserExactSkillDetail(
    string Guid,
    string Name,
    string LiteralVersion,
    string Publisher,
    string SkillHash,
    IReadOnlyList<string> DeclaredToolNames);
~~~

Inject IExactOrnnSkillResolver into UserSkillCatalogQueryService. Keep OrnnSkillClient only for resolving current literal version when the query omits it. Replace its direct exact-detail read with:

~~~csharp
var resolution = await _exactSkillResolver.ResolveAsync(
    accessToken,
    new ExactRemoteSkillRef
    {
        Guid = guid,
        LiteralVersion = exactVersion,
    },
    ct);

if (!resolution.IsSuccess)
{
    return resolution.DiagnosticCode switch
    {
        "ORNN_SKILL_ACCESS_DENIED" =>
            new UserExactSkillReadResult(null, "exact_skill_upstream_failure", 403),
        "ORNN_SKILL_NOT_FOUND" =>
            new UserExactSkillReadResult(null, "exact_skill_not_found"),
        "ORNN_SKILL_IDENTITY_MISMATCH" or
        "ORNN_SKILL_INTEGRITY_EVIDENCE_MISSING" or
        "INVALID_SKILL_PACKAGE" =>
            new UserExactSkillReadResult(null, "exact_skill_integrity_failure"),
        _ => new UserExactSkillReadResult(null, "exact_skill_upstream_failure"),
    };
}

var package = resolution.Package!;
return new UserExactSkillReadResult(
    new UserExactSkillDetail(
        package.SkillGuid,
        package.CanonicalName,
        package.LiteralVersion,
        package.PublisherId,
        Convert.ToHexString(package.SkillSha256.Span).ToLowerInvariant(),
        package.DeclaredToolNames
            .Select(static name => name.Trim())
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray()),
    null);
~~~

Do not change WorkflowSkillsEndpoints.GetExactSkill; its existing UpstreamStatus 403 branch remains the HTTP boundary.

- [x] **Step 4: Run focused tests and verify GREEN**

Run both Step 2 commands. Expected: all selected tests pass with zero failures.

- [x] **Step 5: Commit exact contract**

~~~bash
git add src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs src/Aevatar.Mainnet.Host.Api/Skills/UserSkillCatalogModels.cs src/Aevatar.Mainnet.Host.Api/Skills/UserSkillCatalogQueryService.cs test/Aevatar.AI.ToolProviders.Ornn.Tests/OrnnExactAgentProfileSkillResolverTests.cs test/Aevatar.Capabilities.Tests/UserSkillCatalogQueryServiceTests.cs test/Aevatar.Capabilities.Tests/WorkflowSkillsExactDetailEndpointTests.cs
git commit -m "Expose exact Agent Profile skill tools"
~~~

---

### Task 2: Apply exact tools and simplify editor hierarchy

**Files:**
- Modify: src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html
- Modify: test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs

**Interfaces:**
- Consumes: exact JSON fields guid, name, literalVersion, publisher, skillHash, and declaredToolNames from Task 1.
- Produces: agentProfileUnionNames, agentProfileApplyExactSkill, agentProfileToolChipsHtml, and agentProfileExactEvidenceHtml.
- Produces: transient AGENT_PROFILE_STATE.skillProofs keyed by member index; it is visual review evidence and is never serialized into a draft.

- [x] **Step 1: Write failing policy-union and layout tests**

Add a Node vm test against a draft containing manual policy entries:

~~~javascript
context.draft = {
  displayName:'Operator',
  runtimeProfile:{
    maximumToolPolicy:{toolNames:['manual_max'],toolSetRefs:[]},
    members:[{
      intentId:'operate',skillRef:{guid:'old-guid',literalVersion:'1.0'},
      expectedSkillName:'old-name',reviewedPublisherId:'old-publisher',
      taskToolPolicy:{toolNames:['manual_task'],toolSetRefs:[]}
    }]
  }
};
context.exact = {
  guid:'11111111-1111-4111-8111-111111111111',
  name:'aevatar-operations',literalVersion:'1.4',
  publisher:'aevatar-platform',
  skillHash:'000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f',
  declaredToolNames:['aevatar_read','aevatar_write']
};
const updated = vm.runInContext(
  'agentProfileApplyExactSkill(draft, exact, 0)', context);
assert.deepEqual(
  Array.from(updated.runtimeProfile.members[0].taskToolPolicy.toolNames),
  ['manual_task','aevatar_read','aevatar_write']);
assert.deepEqual(
  Array.from(updated.runtimeProfile.maximumToolPolicy.toolNames),
  ['manual_max','aevatar_read','aevatar_write']);
assert.equal(updated.runtimeProfile.members[0].reviewedPublisherId,
  'aevatar-platform');
~~~

Extend served-asset assertions:

~~~csharp
html.Should().Contain("data-ap-exact-evidence");
html.Should().Contain("data-ap-tool-chip");
html.Should().Contain("<details class=\"ap-disclosure\"");
html.Should().Contain("AGENT_PROFILE_STATE.skillProofs");
html.Should().NotContain("agentProfileField('Exact skill GUID'");
~~~

Update the multi-member test to expect hidden exact fields plus visible evidence instead of editable GUID/version/name/publisher text fields.

- [x] **Step 2: Run Admin Agent Profile tests and verify RED**

~~~bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_AgentProfiles'
~~~

Expected: new functions/evidence/disclosures are absent and declared tools are not added to either policy.

- [x] **Step 3: Add minimum policy and evidence helpers**

~~~javascript
function agentProfileUnionNames(existing,additions){
  var seen={},out=[];
  (existing||[]).concat(additions||[]).forEach(function(value){
    var name=String(value||'').trim();
    if(name&&!seen[name]){seen[name]=true;out.push(name);}
  });
  return out;
}
~~~

Extend agentProfileApplyExactSkill without removing existing entries:

~~~javascript
var tools=Array.isArray(detail.declaredToolNames)?detail.declaredToolNames:[];
member.taskToolPolicy=member.taskToolPolicy||{toolNames:[],toolSetRefs:[]};
runtime.maximumToolPolicy=runtime.maximumToolPolicy||{toolNames:[],toolSetRefs:[]};
member.taskToolPolicy.toolNames=agentProfileUnionNames(
  member.taskToolPolicy.toolNames,tools);
runtime.maximumToolPolicy.toolNames=agentProfileUnionNames(
  runtime.maximumToolPolicy.toolNames,tools);
~~~

Replace skillProof with skillProofs. On exact selection store detail by member index. Clear proofs when loading another Profile or abandoning creation; member removal may clear all proofs instead of guessing shifted indices.

Render exact values as hidden data-ap-field inputs so the existing collector round-trips them, and render authority evidence separately:

~~~javascript
function agentProfileHidden(name,value,memberIndex){
  return '<input type="hidden" data-ap-field="'+name+
    '" data-ap-member="'+memberIndex+'" value="'+esc(value||'')+'">';
}
~~~

agentProfileExactEvidenceHtml shows name, publisher, literal version, GUID, and the first 12 hash characters when a transient proof exists. agentProfileToolChipsHtml renders allowed names with data-ap-tool-chip and an explicit 未声明工具 empty state.

- [x] **Step 4: Collapse advanced controls with native HTML**

Keep identity, purpose, instructions, activation, skill search, routing, aliases, side-effect class, evidence, and task-tool chips in the primary flow. Wrap manual maximum/recovery policies and fixed runtime parameters in two disclosures:

~~~javascript
'<details class="ap-disclosure"><summary>高级工具策略</summary>'+
  '<div class="ap-grid">'+
    agentProfileField('Maximum tools','maximumTools',
      (max.toolNames||[]).join(', '),{disabled:dis})+
    agentProfileField('Maximum tool sets','maximumToolSets',
      (max.toolSetRefs||[]).join(', '),{disabled:dis})+
    agentProfileField('Recovery tools','recoveryTools',
      (recovery.toolNames||[]).join(', '),{disabled:dis})+
    agentProfileField('Recovery tool sets','recoveryToolSets',
      (recovery.toolSetRefs||[]).join(', '),{disabled:dis})+
  '</div></details>'+
'<details class="ap-disclosure"><summary>固定运行参数</summary>'+
  '<div class="ap-grid three">'+
    agentProfileField('Max plan steps','maxPlanSteps',
      rt.maxPlanSteps||4,{type:'number',readonly:true,disabled:dis})+
    agentProfileField('Handoff TTL (s)','handoffTtlSeconds',
      rt.handoffTtlSeconds||900,{type:'number',readonly:true,disabled:dis})+
    agentProfileField('Classifier timeout (ms)','classifierTimeoutMs',
      rt.classifierTimeoutMs||600,{type:'number',readonly:true,disabled:dis})+
    agentProfileField('Exact fetch timeout (ms)','exactSkillFetchTimeoutMs',
      rt.exactSkillFetchTimeoutMs||1500,{type:'number',readonly:true,disabled:dis})+
    agentProfileField('Max selected skill bytes','maxSelectedSkillBytes',
      rt.maxSelectedSkillBytes||24576,{type:'number',readonly:true,disabled:dis})+
  '</div></details>'
~~~

Add only scoped .ap-* styles using existing CSS variables. Evidence and chips wrap; summary has visible focus; at 768px disclosure content, member actions, and editor actions fit without horizontal overflow.

- [x] **Step 5: Run focused tests and verify GREEN**

Run Step 2 command. Expected: all Agent Profile static-asset tests pass.

- [x] **Step 6: Commit editor policy UX**

~~~bash
git add src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Improve Agent Profile skill editing"
~~~

---

### Task 3: Guide creation through canonical create and draft outcomes

**Files:**
- Modify: src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html
- Modify: test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs

**Interfaces:**
- Consumes: agentProfileMutation, agentProfileTrackAccepted, agentProfileReconcilePending, loadAgentProfiles, loadAgentProfileDetail, and Task 2 form/evidence helpers.
- Produces: agentProfileStartCreate, agentProfileWorkingDraft, agentProfileStoreWorkingDraft, agentProfileSlugFromName, agentProfileSubmitCreate, agentProfileAdvanceCreate, and agentProfileCreateHtml.
- Produces transient createFlow {owner, slug, slugTouched, draft, stage}; stage is editing, catalog, or draft. This browser state is not business authority.
- Extends pending state with completedPending, a terminal outcome copied by agentProfileReconcilePending and consumed once by agentProfileAdvanceCreate. Absence of pending is not success.

- [x] **Step 1: Write failing guided-workspace tests**

Add served-asset assertions:

~~~csharp
html.Should().Contain("data-ap-start-create");
html.Should().Contain("data-ap-create-submit");
html.Should().Contain("data-ap-create-cancel");
html.Should().Contain("定义职责");
html.Should().Contain("选择能力");
html.Should().Contain("检查并创建");
html.Should().NotContain("data-ap-new-slug");
~~~

Add local-only startup and slug behavior:

~~~javascript
vm.runInContext('agentProfileStartCreate()', context);
assert.equal(context.AGENT_PROFILE_STATE.createFlow.stage, 'editing');
assert.equal(context.AGENT_PROFILE_STATE.createFlow.owner, 'mine');
assert.equal(context.mutations.length, 0);
assert.equal(
  vm.runInContext("agentProfileSlugFromName('Aevatar Operator')", context),
  'aevatar-operator');
assert.equal(vm.runInContext("agentProfileSlugFromName('运维助手')", context), '');
~~~

Add an orchestration test using slug aevatar-operator, profile ID profile-aevatar-operator, catalog operation op-catalog-alpha, draft operation op-draft-alpha, and ETag "agent-profile-v3". Prove:

1. submit sends only POST {profileSlug} and records catalog kind.
2. advance does nothing while catalog is pending or after PROFILE_PROVISIONING_STARTED.
3. matching PROFILE_ACTIVE plus exact owner/slug shell reads detail and sends one PUT /draft with retained draft and ETag.
4. mismatched operation ID and polling timeout never advance.
5. matching draft SUCCEEDED/NO_CHANGE exits creation and selects the Profile.
6. draft PUT failure after shell readback retains detail.draft, sets dirty, and never repeats POST.

- [x] **Step 2: Run Agent Profile tests and verify RED**

~~~bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_AgentProfiles'
~~~

Expected: guided controls/functions are absent and the toolbar POSTs from data-ap-new-slug.

- [x] **Step 3: Add local create state and one-shot terminal evidence**

Extend state with createFlow:null and completedPending:null. Implement ASCII slug suggestion:

~~~javascript
function agentProfileSlugFromName(value){
  return String(value||'').normalize('NFKD').toLowerCase()
    .replace(/[\u0300-\u036f]/g,'')
    .replace(/[^a-z0-9]+/g,'-')
    .replace(/^-+|-+$/g,'')
    .replace(/-+/g,'-');
}
~~~

agentProfileStartCreate stores active owner, fresh draft, and editing stage; it clears detail, diagnostics, errors, and proofs. agentProfileWorkingDraft and agentProfileStoreWorkingDraft route shared member/search/capture code to createFlow.draft or detail.draft.

When agentProfileReconcilePending sees a matching terminal outcome, set:

~~~javascript
AGENT_PROFILE_STATE.completedPending={
  kind:pending.kind,
  operationId:pending.operationId,
  outcome:outcome
};
~~~

Then clear pending as today. agentProfileAdvanceCreate consumes completedPending only when kind and operation ID match; null pending or exhausted polling is never success. Name input suggests slug until direct slug editing sets slugTouched.

- [x] **Step 4: Render the three-section create workspace**

Replace toolbar inline creation with:

~~~html
<button class="btn btn-primary" data-ap-start-create>新建 Profile</button>
~~~

agentProfileEditorHtml returns agentProfileCreateHtml during creation. Reuse normal member rendering and add owner badge, editable slug with canonical-regex error, three numbered headings, review summary for activation/members/publishers/maximum tools, cancel/create actions, and role=status aria-live=polite progress.

Only render start-create for mine/ or Admin-owned system/. Owner switch/cancel prompt only for dirty local draft and never mutate server.

- [x] **Step 5: Orchestrate existing create and draft mutations**

Submit captures draft, validates slug and local diagnostics, then POSTs:

~~~javascript
await agentProfileMutation(
  agentProfileCollectionEndpoint(),
  'POST',
  {profileSlug:flow.slug},
  null);
~~~

On 202 store operation ID, stage catalog, track receipt, and schedule refresh. On ambiguous POST failure, read collection once; continue only if exact owner/slug shell exists, else return to editing with original error. Never blindly repeat POST.

At the end of loadAgentProfiles call await agentProfileAdvanceCreate(). Advance catalog only after consuming matching successful completedPending and finding the shell. Select item, load detail for strong ETag, restore retained draft, set stage draft, and PUT itemEndpoint/draft once. Store draft operation ID, track it, and schedule refresh.

After matching draft SUCCEEDED/NO_CHANGE, load detail, clear createFlow/completedPending, retain selected slug, and show 草稿已创建，可继续校验和发布. On draft PUT failure, clear createFlow, retain detail.draft, set dirty, and surface typed error. Do not validate, publish, or bind here.

- [x] **Step 6: Finish responsive and keyboard behavior**

Add scoped step, badge, review, and wrapping action styles. At 768px stack sections and actions. Use button type=button, label for, native controls, focus-visible, role=alert, and aria-live.

- [x] **Step 7: Run focused tests and verify GREEN**

Run Step 2 command. Expected: all Agent Profile tests pass, including existing multi-member, canonical-route, system-summary, stale-response, and receipt-reconciliation cases.

- [x] **Step 8: Run modified-test guard**

~~~bash
bash tools/ci/test_stability_guards.sh
~~~

Expected: pass with no allowlist entry; tests use deterministic Node stubs and no Task.Delay.

- [x] **Step 9: Commit guided creation**

~~~bash
git add src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Guide Agent Profile creation"
~~~

---

### Task 4: Verify, deliver, and create the production operator Profile

**Files:**
- Verify: all files changed in Tasks 1-3
- Update only if semantics changed: docs/superpowers/specs/2026-07-31-agent-profile-guided-creation-design.md
- Update plan checkboxes during execution: docs/superpowers/plans/2026-07-31-agent-profile-guided-creation.md

**Interfaces:**
- Consumes: completed exact API and Admin behavior.
- Produces: fast-forward push to origin/feature/integrate, production operation/readback evidence, and published personal aevatar-operator without default binding mutation.

- [x] **Step 1: Run focused suites**

~~~bash
dotnet test test/Aevatar.AI.ToolProviders.Ornn.Tests/Aevatar.AI.ToolProviders.Ornn.Tests.csproj --nologo --filter FullyQualifiedName~OrnnExactAgentProfileSkillResolverTests
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~UserSkillCatalogQueryServiceTests|FullyQualifiedName~WorkflowSkillsExactDetailEndpointTests|FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_AgentProfiles'
~~~

Expected: zero failures.

- [x] **Step 2: Run guards and docs lint**

~~~bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/ci/solution_split_guards.sh
bash tools/docs/lint.sh
~~~

Expected: all exit 0; query guard proves exact reads do not prime Profile projection.

- [x] **Step 3: Run full build and tests**

~~~bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
~~~

Expected: build and tests exit 0 with zero failures.

- [x] **Step 4: Inspect final change set**

~~~bash
git status --short --branch
git diff --check origin/feature/integrate...HEAD
git diff --stat origin/feature/integrate...HEAD
git log --oneline origin/feature/integrate..HEAD
~~~

Require only approved docs, exact API/resolver, Admin asset, and focused tests. Exclude user work from /Users/eanzhao/Code/aevatar.

- [ ] **Step 5: Fast-forward requested branch**

~~~bash
git fetch origin feature/integrate
test "$(git merge-base HEAD origin/feature/integrate)" = "$(git rev-parse origin/feature/integrate)"
git push origin HEAD:feature/integrate
~~~

If ancestry fails, inspect remote commits; never force-push.

- [ ] **Step 6: Confirm identity and deployed contract**

~~~bash
nyxid whoami
nyxid proxy request aevatar /api/workflow/observatory/me --method GET --output json
nyxid proxy request aevatar /api/workflow/skills/11111111-1111-4111-8111-111111111111/exact?literalVersion=0.0 --method GET --output json
~~~

First two identify user and explicit scopeId. Last is read-only: 404 is acceptable, but shape must prove new deployment before mutation. Otherwise wait.

- [ ] **Step 7: Discover exact aevatar-platform skills**

~~~bash
nyxid proxy request aevatar '/api/workflow/skills?query=aevatar&page=1&pageSize=100' --method GET --output json
nyxid proxy request aevatar '/api/workflow/skills?query=operations&page=1&pageSize=100' --method GET --output json
nyxid proxy request aevatar '/api/workflow/skills?query=mainnet&page=1&pageSize=100' --method GET --output json
~~~

Read one fixed `aevatar-platform` skillset revision and its closure from Ornn. For each closure member, call `/api/workflow/skills/{guid}/exact?literalVersion={version}`. Require exact GUID, name, literalVersion, and skillHash to match the closure, and record the exact endpoint's stable publisher ID and declaredToolNames. Never use the skillset name as a publisher ID or infer membership from list names/tags.

- [ ] **Step 8: Create and save personal Profile with readback**

Re-read scopeId immediately before mutation; previously verified value is 5d0d7b72-acff-49af-bb1b-9f30bbb7c102. Build slug aevatar-operator, display name Aevatar Operator, ENFORCED activation, one intent per selected skill, task tools covering declared tools, and maximum tools as ordinal union. Instructions require confirmation before destructive, authorization-changing, or externally visible operations and typed receipt/read-model completion evidence.

POST with fresh idempotency key; poll list until matching PROFILE_ACTIVE; GET detail/ETag; PUT draft with new key and If-Match; poll matching draft outcome. On ambiguous error read back before retry.

- [ ] **Step 9: Validate and publish with terminal evidence**

POST /api/scopes/{scopeId}/agent-profiles/aevatar-operator:validate and require empty diagnostics. GET current ETag; publish with fresh Idempotency-Key and If-Match. Poll until matching PROFILE_PUBLISHED and executionAvailable true.

Finally GET /api/scopes/{scopeId}/agent-profile-bindings/nyxid.chat as read-only evidence. Do not PUT/DELETE the binding.

- [ ] **Step 10: Visual acceptance on canonical Admin route**

Open https://aevatar-console-backend-api.aevatar.ai/admin#/agent-profiles in the in-app browser. Check desktop/narrow mobile, keyboard access, exact evidence/tools, collapsed advanced controls, honest stages/errors, published status, and absence of #/agentProfiles generation/parsing. Finalize only tabs created by this run.
