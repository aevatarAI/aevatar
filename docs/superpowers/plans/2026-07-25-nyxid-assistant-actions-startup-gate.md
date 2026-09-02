# NyxID Assistant Actions Startup Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent an unavailable, disabled NyxID Assistant browser-action registry from aborting Aevatar Host startup while preserving strict fail-fast behavior when the capability is explicitly enabled.

**Architecture:** Bind a typed process-level option during `AddNyxIdChat` composition. Disabled composition injects an immutable deny-all registry and omits the HTTP startup service; enabled composition retains the existing one-shot remote snapshot and strict validation.

**Tech Stack:** .NET 10, Microsoft.Extensions.Configuration, Microsoft.Extensions.DependencyInjection, xUnit, FluentAssertions.

## Global Constraints

- Managed Codex credential, eligibility, chrono-sandbox, and delegation behavior must not change.
- Disabled Assistant actions must fail closed and must not use an embedded fallback manifest.
- Enabled Assistant actions must continue to fail Host startup for an unavailable or incompatible registry.
- Configuration semantics must use a typed option rather than a generic bag.

---

### Task 1: Prove default-disabled and explicitly-enabled composition

**Files:**
- Modify: `test/Aevatar.AI.Tests/NyxIdChatServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: `IServiceCollection AddNyxIdChat(IConfiguration? configuration = null)`.
- Produces: regression coverage for the default-disabled and explicitly-enabled composition contracts.

- [ ] **Step 1: Write the failing default-disabled test**

```csharp
[Fact]
public void AddNyxIdChat_Default_ShouldDisableAssistantActionsWithoutStartupFetch()
{
    var services = new ServiceCollection();

    services.AddNyxIdChat(new ConfigurationBuilder().Build());
    using var provider = services.BuildServiceProvider();

    provider.GetServices<IHostedService>()
        .Should().NotContain(service =>
            service is NyxIdAssistantActionRegistryStartupService);
    var registry = provider.GetRequiredService<NyxIdAssistantActionRegistry>();
    registry.TryGetDefinition("service.connect", out _).Should().BeFalse();
    Action resolve = () => registry.ResolveCatalogServiceConnect("api-github");
    resolve.Should().Throw<NyxIdAssistantActionRegistryException>()
        .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore \
  --filter FullyQualifiedName~AddNyxIdChat_Default_ShouldDisableAssistantActionsWithoutStartupFetch
```

Expected: FAIL because the startup hosted service is currently registered
unconditionally.

- [ ] **Step 3: Add the explicitly-enabled composition test**

```csharp
[Fact]
public void AddNyxIdChat_WhenAssistantActionsEnabled_ShouldRegisterStrictStartupFetcher()
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:AssistantActions:Enabled"] = "true",
        })
        .Build();
    var services = new ServiceCollection();

    services.AddNyxIdChat(configuration);

    services.Should().ContainSingle(descriptor =>
        descriptor.ServiceType == typeof(IHostedService) &&
        descriptor.ImplementationType ==
        typeof(NyxIdAssistantActionRegistryStartupService));
}
```

### Task 2: Implement typed startup gating

**Files:**
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdAssistantActionsOptions.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdAssistantActionRegistry.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`

**Interfaces:**
- Produces: `NyxIdAssistantActionsOptions.ConfigSection`,
  `NyxIdAssistantActionsOptions.Enabled`, and
  `NyxIdAssistantActionRegistry.CreateDisabled()`.

- [ ] **Step 1: Add the typed configuration object**

```csharp
namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdAssistantActionsOptions
{
    public const string ConfigSection = "Aevatar:NyxId:AssistantActions";

    public bool Enabled { get; set; }
}
```

- [ ] **Step 2: Add the immutable disabled registry factory**

Inside `NyxIdAssistantActionRegistry`:

```csharp
internal static NyxIdAssistantActionRegistry CreateDisabled() =>
    new(
        SupportedSchemaVersion,
        SupportedRegistryRevision,
        new Dictionary<string, RegistryEntry>(StringComparer.Ordinal));
```

- [ ] **Step 3: Gate composition in `AddNyxIdChat`**

Bind the typed option once. Register `CreateDisabled()` when false. Preserve the
existing snapshot/source/hosted-service registrations only when true.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore \
  --filter "FullyQualifiedName~NyxIdAssistantActionRegistryTests|FullyQualifiedName~NyxIdChatServiceCollectionExtensionsTests"
```

Expected: all selected tests pass.

### Task 3: Make production defaults and documentation explicit

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/appsettings.json`
- Modify: `docs/canon/nyxid-chat-api.md`

**Interfaces:**
- Produces: an explicit production default and operator contract.

- [ ] **Step 1: Add the default configuration**

Add:

```json
"AssistantActions": {
  "Enabled": false
}
```

under `Aevatar:NyxId`.

- [ ] **Step 2: Document the rollout contract**

State that disabled mode performs no registry request and rejects browser
actions, while enabled mode requires the NyxID endpoint and retains strict
startup validation.

### Task 4: Verify and integrate

**Files:**
- Verify all files from Tasks 1-3.

**Interfaces:**
- Produces: a deployable `feature/integrate` commit.

- [ ] **Step 1: Run affected suites**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --no-restore
```

- [ ] **Step 2: Run repository guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
```

- [ ] **Step 3: Build the solution**

```bash
dotnet build aevatar.slnx --nologo --no-restore
```

- [ ] **Step 4: Commit and push without force**

```bash
git add agents/Aevatar.GAgents.NyxidChat \
  test/Aevatar.AI.Tests/NyxIdChatServiceCollectionExtensionsTests.cs \
  src/Aevatar.Mainnet.Host.Api/appsettings.json \
  docs/canon/nyxid-chat-api.md \
  docs/superpowers/specs/2026-07-25-nyxid-assistant-actions-startup-gate-design.md \
  docs/superpowers/plans/2026-07-25-nyxid-assistant-actions-startup-gate.md
git commit -m "Gate NyxID assistant action startup"
git fetch origin feature/integrate
git rebase origin/feature/integrate
git push origin HEAD:feature/integrate
```

- [ ] **Step 5: Verify production**

After the new image is deployed, confirm the Host remains ready without an
Assistant registry request, then use the local NyxID CLI to run the canonical
workflow and require exact output `CODEX_EXEC_READY`.

