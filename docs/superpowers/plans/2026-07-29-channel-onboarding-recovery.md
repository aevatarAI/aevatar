# Channel Onboarding Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Make /channels the single honest channel onboarding and recovery surface, including durable Lark recovery information and optional Encrypt Key transport, and embed it from admin#/channels.

**Architecture:** Reuse the existing ChannelRelayRegistrationFacade, NyxLarkProvisioningService, registration actor/read model, and /channels static asset. Extend the current typed Lark request records by one method-local field, expose the already committed WebhookUrl through the existing list response, improve the canonical page, then delete the duplicate admin state machine.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, existing Protobuf actor/read-model contracts, xUnit, FluentAssertions, embedded HTML/CSS/JavaScript.

## Global Constraints

- Preserve Domain / Application / Infrastructure / Host layering; Host only adapts HTTP.
- Keep one authoritative channel UI and one existing registration command/query path.
- Do not add an actor, projection, endpoint, dependency, compatibility route, or process-local fact registry.
- Keep accepted, pending_webhook, and active semantically distinct.
- Never claim that Aevatar changed Lark permissions, Event Subscriptions, or app publication.
- Do not persist or log App Secret, Verification Token, or Encrypt Key in Aevatar state, events, read models, responses, or logs.
- Keep callback_url and webhook_url as separate single-purpose fields.
- Preserve owner-scope and cross-account existence-hiding rules.
- Tests must not add Task.Delay or polling helpers.
- Do not introduce port 5000 or 5050.
- Update architecture/operations documentation and pass build, test, docs, and architecture guards.

---

### Task 1: Carry Lark Encrypt Key Through the Existing Typed Path

**Files:**
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelCallbackEndpointsTests.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/NyxLarkProvisioningServiceTests.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelWorkflowResultDeliveryContractTests.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelRegistrationToolTests.cs
- Modify: agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelCallbackEndpoints.cs
- Modify: agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxLarkProvisioningService.cs
- Modify: src/Aevatar.AI.ToolProviders.ChannelAdmin/ChannelRegistrationTool.cs

**Interfaces:**
- Consumes: POST /api/channels/registrations and ChannelRelayRegistrationFacade.RegisterAsync.
- Produces: NyxChannelLarkCredentials with EncryptKey and NyxLarkProvisioningRequest with EncryptKey.
- Produces: optional encrypt_key in NyxID channel-bot JSON only; no Aevatar persisted or response contract includes it.
- Enforces: a nonblank Verification Token at the provisioning boundary before any NyxID call.

- [ ] **Step 1: Write the failing Host mapping test**

Add HandleRegisterAsync_MapsOptionalLarkEncryptKeyIntoTypedCredentials. Capture NyxChannelBotProvisioningRequest through a substituted INyxChannelBotProvisioningService, submit distinct app/verification/encryption values, and assert:

    response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    captured!.Lark!.EncryptKey.Should().Be("encrypt-alpha");
    response.Body.Should().NotContain("encrypt-alpha");

- [ ] **Step 2: Verify Host RED**

Run:

    dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~HandleRegisterAsync_MapsOptionalLarkEncryptKeyIntoTypedCredentials

Expected: compile or assertion failure because EncryptKey is not yet mapped.

- [ ] **Step 3: Write failing NyxID payload tests**

Extend the successful provisioning request with EncryptKey: "encrypt-alpha" and assert the channel-bot request contains the exact encrypt_key while the mirror envelope does not. Add one focused blank-key case asserting the channel-bot request omits encrypt_key.

Extend the invalid-request theory with a blank Verification Token case that expects missing_verification_token and no NyxID requests. Give all successful fixtures a nonblank Verification Token so the test data reflects the supported Lark contract.

- [ ] **Step 4: Verify provisioning RED**

Run:

    dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~NyxLarkProvisioningServiceTests

Expected: compile or assertion failure until typed records and payload mapping exist.

- [ ] **Step 5: Implement the minimum typed transport**

Extend the existing records with one optional typed field:

    public sealed record NyxChannelLarkCredentials(
        string AppId,
        string AppSecret,
        string VerificationToken,
        string EncryptKey = "");

Add EncryptKey as an optional final field in NyxLarkProvisioningRequest, preserving existing callers while keeping the credential typed. Map it from both the private HTTP RegistrationRequest and the existing ChannelRegistrationTool credential object, then pass it to RegisterChannelBotAsync. Reject a blank VerificationToken in NyxLarkProvisioningService before any NyxID call. Add encrypt_key to the NyxID payload only when nonblank:

    if (!string.IsNullOrWhiteSpace(encryptKey))
        payload["encrypt_key"] = encryptKey.Trim();

Do not add it to Protobuf, mirror commands, results, responses, logs, or Vault.

- [ ] **Step 6: Verify GREEN and commit**

Run both Task 1 filters, then:

    git add test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelCallbackEndpointsTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/NyxLarkProvisioningServiceTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelWorkflowResultDeliveryContractTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelRegistrationToolTests.cs agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelCallbackEndpoints.cs agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxLarkProvisioningService.cs src/Aevatar.AI.ToolProviders.ChannelAdmin/ChannelRegistrationTool.cs
    git commit -m "Carry Lark encrypt key during provisioning"

### Task 2: Expose the Committed Recovery URL

**Files:**
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelCallbackEndpointsTests.cs
- Modify: agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelCallbackEndpoints.cs

**Interfaces:**
- Consumes: ChannelBotRegistrationEntry.WebhookUrl.
- Produces: additive webhook_url in each GET /api/channels/registrations row.
- Preserves: callback_url remains empty and retains its current meaning.

- [ ] **Step 1: Write the failing list response test**

Seed a registration with:

    WebhookUrl = "https://nyx.example/api/v1/webhooks/channel/lark/bot-alpha"

Assert the response contains that exact webhook_url and still contains an empty callback_url.

- [ ] **Step 2: Verify RED**

    dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~HandleListRegistrationsAsync_ReturnsRelayModeOnly_AndScopesToCaller

Expected: FAIL because webhook_url is absent.

- [ ] **Step 3: Return the committed field**

Add exactly:

    webhook_url = e.WebhookUrl,

Do not derive it from bot id, provider slug, callback URL, or route position.

- [ ] **Step 4: Verify GREEN and commit**

    dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~ChannelCallbackEndpointsTests
    git add test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelCallbackEndpointsTests.cs agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelCallbackEndpoints.cs
    git commit -m "Expose channel recovery webhook URL"

### Task 3: Make /channels the Honest Recovery Surface

**Files:**
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelsEndpointsTests.cs
- Modify: agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html

**Interfaces:**
- Consumes: list-row webhook_url, existing status endpoint, existing DELETE endpoint, optional registration encrypt_key.
- Produces: required Verification Token, optional Encrypt Key, durable Request URL, manual recovery checklist, and delete-before-replacement behavior.

- [ ] **Step 1: Write failing static contract tests**

Add focused tests proving:

- Lark requiredOk checks app_id, app_secret, and verification_token.
- Lark credFields contains encrypt_key and buildBody forwards it.
- the manage view reads r.webhook_url and links to https://open.larksuite.com/app.
- pending copy includes Event Subscriptions, im.message.receive_v1, publication, a test message, and the sentence "只有收到验证通过的入站消息并变为 active 才算完成".
- replacement uses an async replaceRegistration function and awaits DELETE on the current registration before entering the wizard.

- [ ] **Step 2: Verify RED**

    dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~ChannelsEndpointsTests

Expected: failures for weak validation, missing Encrypt Key, missing durable URL, or missing replacement function.

- [ ] **Step 3: Strengthen credentials**

Add optional secret encrypt_key to state.cred and the Lark descriptor. Use:

    requiredOk:(c)=> !!(c.app_id.trim() && c.app_secret.trim() && c.verification_token.trim())

Build the Lark body with encrypt_key:c.encrypt_key.trim(). Show the current-session value in step 3, but never expect it from a read response.

- [ ] **Step 4: Render durable recovery guidance**

Map registration webhook_url directly. In manage view, show a copy action for the exact Lark Request URL. When pending_webhook, render one ordered checklist:

1. paste the exact Request URL into Lark Event Subscriptions;
2. ensure Verification Token and optional Encrypt Key match;
3. import the existing permission JSON;
4. subscribe to im.message.receive_v1;
5. publish and approve the app version;
6. send a test message and refresh until active.

Use https://open.larksuite.com/app and state that only verified inbound plus active proves completion.

- [ ] **Step 5: Implement honest replacement**

Rename the manage action to replacement. After explicit confirmation, await the existing DELETE endpoint, refresh registrations, then open a blank wizard for the same platform. On failure, stay in manage view and display the error; do not optimistically mutate the list.

- [ ] **Step 6: Verify GREEN and commit**

    dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~ChannelsEndpointsTests
    git add test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelsEndpointsTests.cs agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html
    git commit -m "Guide Lark channel recovery honestly"

### Task 4: Delete the Duplicate Admin Channel State Machine

**Files:**
- Modify: test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
- Modify: src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html

**Interfaces:**
- Consumes: existing suiteFrame('/channels', '通道接入') and embedTrim.
- Produces: admin#/channels as a shell around canonical /channels.

- [ ] **Step 1: Write the failing consolidation test**

Add AdminShell_Channels_ShouldEmbedCanonicalSurfaceWithoutDuplicateMutations asserting:

    html.Should().Contain("suiteFrame('/channels','通道接入')");
    html.Should().NotContain("function doRegister()");
    html.Should().NotContain("a==='wzPermImport'");
    html.Should().NotContain("a==='wzPublish'");
    html.Should().NotContain("CHANNELS_DATA.splice");

- [ ] **Step 2: Verify RED**

    dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~AdminShell_Channels_ShouldEmbedCanonicalSurfaceWithoutDuplicateMutations

- [ ] **Step 3: Replace the module**

Add in the canonical suite-frame dispatch section:

    function viewChannels(){ return {html:suiteFrame('/channels','通道接入')}; }

Delete the earlier channel live-state block and complete channel catalog/wizard/manage/bind block. Delete only channel-specific CSS proven unused; retain shared primitives.

- [ ] **Step 4: Verify GREEN and commit**

    dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~BackendConsoleStaticAssetEndpointTests
    git add test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html
    git commit -m "Use canonical channel console in admin"

### Task 5: Align Documentation and Verify

**Files:**
- Modify: docs/canon/aevatar-channel-architecture.md
- Modify: docs/operations/2026-04-22-lark-nyx-cutover-runbook.md

**Interfaces:**
- Produces: canonical UI and recovery semantics documented for operators.

- [ ] **Step 1: Update documentation**

Document that /channels is canonical and admin#/channels embeds it. Document that replacement changes bot id and webhook_url, external Lark actions remain manual, and only verified inbound plus active proves activation.

- [ ] **Step 2: Run focused verification**

    dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~ChannelsEndpointsTests|FullyQualifiedName~ChannelCallbackEndpointsTests|FullyQualifiedName~NyxLarkProvisioningServiceTests'
    dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~BackendConsoleStaticAssetEndpointTests

- [ ] **Step 3: Run required guards**

    bash tools/ci/test_stability_guards.sh
    bash tools/ci/architecture_guards.sh
    bash tools/docs/lint.sh

- [ ] **Step 4: Run complete build and tests**

    dotnet build aevatar.slnx --nologo --no-restore
    dotnet test aevatar.slnx --nologo --no-build --no-restore

Expected: zero build/test failures. Investigate any new warning.

- [ ] **Step 5: Browser smoke check**

Run Mainnet Host locally on a non-conflicting port, open /admin#/channels, and verify a single navigation shell embeds /channels. Without submitting credentials, verify Verification Token is required, Encrypt Key is optional, the Lark link is correct, and fake completion controls are absent.

- [ ] **Step 6: Commit documentation**

    git add docs/canon/aevatar-channel-architecture.md docs/operations/2026-04-22-lark-nyx-cutover-runbook.md
    git commit -m "Document channel onboarding recovery"

- [ ] **Step 7: Integrate and push**

Fetch the latest requested branch. If it advanced, rebase and rerun Steps 2-4. Push with an explicit refspec and read it back:

    git fetch origin feature/integrate
    git rebase origin/feature/integrate
    git push origin HEAD:feature/integrate
    git ls-remote origin refs/heads/feature/integrate

Require the remote hash to equal local HEAD before reporting success.
