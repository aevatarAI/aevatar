# Managed Codex Transparent Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let every eligible NyxID user invoke `codex_exec` once and have Aevatar transparently create, repair, commit, observe, and use that user's managed Codex credential in the same call.

**Architecture:** An Application-owned `ManagedCodexExecutionCoordinator` implements the managed-sandbox `ICodexExecutionPort`. It calls an Actor/Projection-backed `EnsureReadyAsync` use case before a narrow Infrastructure chrono transport, waits for committed credential state through a Projection Session instead of polling, and performs at most one forced authorization repair retry.

**Tech Stack:** .NET 10, C#, Protobuf, Aevatar GAgents/event sourcing, CQRS Projection Pipeline, Garnet mutation leases, `ISecretVault`, NyxID REST API, xUnit, FluentAssertions, NSubstitute.

## Global Constraints

- Preserve `Domain / Application / Infrastructure / Host` layering; Host only composes.
- Use `Command -> Event` and `Query -> ReadModel`; do not mutate from query paths.
- Credential readiness coordination must use Actor-owned state, a distributed mutation lease, and Projection Session observation; do not add an owner-to-task process-local registry.
- The only persistent raw agent-key copy is stored in `ISecretVault`; bearer, raw key, and delegation token must not enter protobuf, Actor state, projection documents, logs, errors, API responses, or workflow output.
- Every managed key must grant exactly the user's `chrono-sandbox` and `chrono-llm-public` UserService IDs, with no wildcard service or node access.
- `All` means all native NyxID users whose two required UserServices already exist and are usable; Aevatar does not create missing UserServices.
- A ready committed credential may execute without a current bearer; first provisioning or repair requires the current user's bearer.
- Do not use `Task.Delay`, read-model polling, query-time replay, projection priming, or an uncommitted descriptor as execution authority.
- Preserve unrelated working-tree changes, including `.superpowers/`, `agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto`, and `test/Aevatar.AI.Tests/NyxIdChatTaskContractTests.cs`.

---

### Task 1: Replace Provisioning Allowlist With Typed Eligibility

**Files:**
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexOptions.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/appsettings.json`
- Modify: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexOptionsValidatorTests.cs`
- Modify: `test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs`

**Interfaces:**
- Produces: `ManagedCodexEligibilityMode`, `ManagedCodexEligibilityOptions`, and `ManagedCodexOptions.IsEligible(string userId)`.
- Consumes: no new interfaces.

- [ ] **Step 1: Write failing option-policy tests**

Replace the old allowlist-only tests with exact mode tests:

```csharp
[Fact]
public void Validate_WhenAllowlistContainsNormalizedUsers_Succeeds()
{
    var options = ValidOptions();
    options.Eligibility = new ManagedCodexEligibilityOptions
    {
        Mode = ManagedCodexEligibilityMode.Allowlist,
        AllowedNyxIdUserIds = ["user-a", "user-b"],
    };

    _validator.Validate(null, options).Succeeded.Should().BeTrue();
    options.IsEligible("user-a").Should().BeTrue();
    options.IsEligible("user-c").Should().BeFalse();
}

[Fact]
public void Validate_WhenAllModeHasNoAllowlist_SucceedsForEveryNormalizedUser()
{
    var options = ValidOptions();
    options.Eligibility = new ManagedCodexEligibilityOptions
    {
        Mode = ManagedCodexEligibilityMode.All,
        AllowedNyxIdUserIds = [],
    };

    _validator.Validate(null, options).Succeeded.Should().BeTrue();
    options.IsEligible("user-a").Should().BeTrue();
    options.IsEligible("user-b").Should().BeTrue();
}

[Fact]
public void Validate_WhenAllowlistIsEmpty_Fails()
{
    var options = ValidOptions();
    options.Eligibility = new ManagedCodexEligibilityOptions
    {
        Mode = ManagedCodexEligibilityMode.Allowlist,
        AllowedNyxIdUserIds = [],
    };

    _validator.Validate(null, options).Failed.Should().BeTrue();
}

[Fact]
public void Validate_WhenAllModeAlsoHasUsers_Fails()
{
    var options = ValidOptions();
    options.Eligibility = new ManagedCodexEligibilityOptions
    {
        Mode = ManagedCodexEligibilityMode.All,
        AllowedNyxIdUserIds = ["user-a"],
    };

    _validator.Validate(null, options).Failed.Should().BeTrue();
}

[Fact]
public void Options_DoNotRetainTheProvisioningNamedAllowlist()
{
    typeof(ManagedCodexOptions)
        .GetProperty("ProvisioningAllowedNyxIdUserIds")
        .Should().BeNull();
}
```

Update `ValidOptions()` to use `Eligibility.Mode=Allowlist` and `AllowedNyxIdUserIds=["user-a"]`.

- [ ] **Step 2: Run the option tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter FullyQualifiedName~ManagedCodexOptionsValidatorTests --nologo
```

Expected: FAIL because `Eligibility`, `ManagedCodexEligibilityMode`, and `IsEligible` do not exist and the old property remains.

- [ ] **Step 3: Implement the typed eligibility contract**

Use this exact public shape in `ManagedCodexOptions.cs`:

```csharp
public enum ManagedCodexEligibilityMode
{
    Allowlist = 0,
    All = 1,
}

public sealed class ManagedCodexEligibilityOptions
{
    public ManagedCodexEligibilityMode Mode { get; set; } =
        ManagedCodexEligibilityMode.Allowlist;

    public string[] AllowedNyxIdUserIds { get; set; } = [];
}

public sealed class ManagedCodexOptions
{
    public const string SectionName = "Aevatar:CodexExecution:ManagedSandbox";
    public const string ChronoSandboxServiceSlug = "chrono-sandbox";
    public const string ChronoLlmServiceSlug = "chrono-llm-public";
    public const string ChronoExecutionPath = "/codex/execute";

    public bool Enabled { get; set; }
    public ManagedCodexEligibilityOptions Eligibility { get; set; } = new();
    public int CredentialLifetimeDays { get; set; } = 30;
    public int MaxResponseBytes { get; set; } = 1_048_576;
    public int MutationLeaseSeconds { get; set; } = 300;
    public int MutationCompletionSeconds { get; set; } = 240;

    public bool IsEligible(string userId)
    {
        var normalized = userId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Eligibility.Mode == ManagedCodexEligibilityMode.All ||
               Eligibility.AllowedNyxIdUserIds.Any(candidate =>
                   string.Equals(candidate?.Trim(), normalized, StringComparison.Ordinal));
    }
}
```

Validate these invariants when enabled:

```csharp
var users = options.Eligibility?.AllowedNyxIdUserIds ?? [];
if (options.Eligibility is null)
    failures.Add("Eligibility is required.");
else if (options.Eligibility.Mode == ManagedCodexEligibilityMode.Allowlist &&
         (users.Length == 0 ||
          users.Any(string.IsNullOrWhiteSpace) ||
          users.Select(static value => value.Trim()).Distinct(StringComparer.Ordinal).Count() != users.Length))
    failures.Add("Eligibility.Allowlist requires normalized distinct AllowedNyxIdUserIds.");
else if (options.Eligibility.Mode == ManagedCodexEligibilityMode.All && users.Length != 0)
    failures.Add("Eligibility.All requires an empty AllowedNyxIdUserIds list.");
else if (!Enum.IsDefined(options.Eligibility.Mode))
    failures.Add("Eligibility.Mode must be Allowlist or All.");
```

Change `appsettings.json` to:

```json
"Eligibility": {
  "Mode": "Allowlist",
  "AllowedNyxIdUserIds": []
}
```

- [ ] **Step 4: Update endpoint fixtures and run focused tests**

Replace test fixture construction with:

```csharp
private static ManagedCodexOptions ManagedOptions(bool enabled) => new()
{
    Enabled = enabled,
    Eligibility = new ManagedCodexEligibilityOptions
    {
        Mode = ManagedCodexEligibilityMode.Allowlist,
        AllowedNyxIdUserIds = ["user-a"],
    },
};
```

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter FullyQualifiedName~ManagedCodexOptionsValidatorTests --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --filter FullyQualifiedName~MainnetManagedCodexCredentialEndpointsTests --nologo
```

Expected: both commands PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexOptions.cs src/Aevatar.Mainnet.Host.Api/appsettings.json test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexOptionsValidatorTests.cs test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs
git commit -m "Add managed Codex eligibility modes"
```

### Task 2: Make Dual-Service Credential State Authoritative

**Files:**
- Modify: `agents/Aevatar.GAgents.Channel.Identity.Abstractions/protos/identity_contracts.proto`
- Modify: `agents/Aevatar.GAgents.Channel.Identity.Abstractions/ManagedCodex/IManagedCodexCredentialPorts.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/protos/managed_codex_credential.proto`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialGAgent.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialCommandPort.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialProjector.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialProjectionQueryPort.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/Projection/ChannelIdentityCommittedStateProjectionActivationPlanProvider.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialGAgentTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialProjectorTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ChannelIdentityCommittedStateProjectionActivationPlanProviderTests.cs`

**Interfaces:**
- Produces: protobuf `ManagedCodexCredentialSnapshot`.
- Produces: `IManagedCodexCredentialCommandPort.CommitPolicyReconciledAsync`.
- Consumes: existing Actor runtime, dispatch, committed-state projection, and document store contracts.

- [ ] **Step 1: Write failing Actor and projection tests**

Add tests proving that a credential without the LLM service ID is rejected and
that policy reconciliation keeps the same key and Vault reference:

```csharp
[Fact]
public async Task HandleProvisioned_WithoutChronoLlmUserServiceId_DoesNotCommit()
{
    var descriptor = Descriptor("key-a", "sec-a", 1);
    descriptor.ChronoLlmUserServiceId = string.Empty;

    await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
    {
        Credential = descriptor,
    });

    _agent.State.Credential.Should().BeNull();
}

[Fact]
public async Task HandlePolicyReconciled_PreservesKeyAndVaultReferenceAndChangesLlmService()
{
    var legacy = Descriptor("key-a", "sec-a", 1);
    legacy.ChronoLlmUserServiceId = "us-llm-old";
    await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
    {
        Credential = legacy,
    });

    var reconciled = legacy.Clone();
    reconciled.ChronoLlmUserServiceId = "us-llm";
    await _agent.HandlePolicyReconciled(
        new CommitManagedCodexCredentialPolicyReconciledCommand
        {
            ExpectedApiKeyId = "key-a",
            Credential = reconciled,
        });

    _agent.State.Credential.ApiKeyId.Should().Be("key-a");
    _agent.State.Credential.SecretReference.Ref.Should().Be("sec-a");
    _agent.State.Credential.ChronoLlmUserServiceId.Should().Be("us-llm");
}
```

Update projector tests to assert:

```csharp
document.Credential.ChronoLlmUserServiceId.Should().Be("user-service-llm");
document.LastEventId.Should().Be("event-7");
```

Add the new policy event to the activation-plan provider test's accepted event
set.

- [ ] **Step 2: Run Actor/projection tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --filter "FullyQualifiedName~ManagedCodexCredentialGAgentTests|FullyQualifiedName~ManagedCodexCredentialProjectorTests|FullyQualifiedName~ChannelIdentityCommittedStateProjectionActivationPlanProviderTests" --nologo
```

Expected: FAIL because the new protobuf fields, command, event, and command-port
method do not exist.

- [ ] **Step 3: Add the protobuf contracts**

Add field 8 and the generated snapshot to `identity_contracts.proto`:

```proto
message ManagedCodexCredentialDescriptor {
  aevatar.gagents.channel.abstractions.ExternalSubjectRef owner = 1;
  string api_key_id = 2;
  aevatar.credentials.SecretReference secret_reference = 3;
  string chrono_sandbox_user_service_id = 4;
  string chrono_sandbox_service_slug = 5;
  google.protobuf.Timestamp expires_at = 6;
  ManagedCodexCredentialStatus status = 7;
  string chrono_llm_user_service_id = 8;
}

enum ManagedCodexCredentialReadinessEvidence {
  MANAGED_CODEX_CREDENTIAL_READINESS_EVIDENCE_UNSPECIFIED = 0;
  MANAGED_CODEX_CREDENTIAL_READINESS_EVIDENCE_CURRENT_STATE_CONFIRMED = 1;
  MANAGED_CODEX_CREDENTIAL_READINESS_EVIDENCE_REMOTE_VALIDATED = 2;
}

message ManagedCodexCredentialSnapshot {
  ManagedCodexCredentialDescriptor credential = 1;
  repeated ManagedCodexCredentialCleanup pending_revocations = 2;
  int64 state_version = 3;
  string last_event_id = 4;
  ManagedCodexCredentialReadinessEvidence readiness_evidence = 5;
}
```

Remove the C# positional `ManagedCodexCredentialSnapshot` record from
`IManagedCodexCredentialPorts.cs`; its generated protobuf replacement becomes
the query result.

Add these messages to `managed_codex_credential.proto`:

```proto
message CommitManagedCodexCredentialPolicyReconciledCommand {
  string expected_api_key_id = 1;
  aevatar.gagents.channel.identity.abstractions.ManagedCodexCredentialDescriptor credential = 2;
}

message ConfirmManagedCodexCredentialReadinessCommand {
  aevatar.gagents.channel.abstractions.ExternalSubjectRef owner = 1;
  string expected_api_key_id = 2;
  aevatar.gagents.channel.identity.abstractions.ManagedCodexCredentialReadinessEvidence readiness_evidence = 3;
  aevatar.gagents.channel.identity.abstractions.ManagedCodexCredentialDescriptor expected_credential = 4;
}

message ManagedCodexCredentialPolicyReconciledEvent {
  string api_key_id = 1;
  aevatar.gagents.channel.identity.abstractions.ManagedCodexCredentialDescriptor credential = 2;
}

message ManagedCodexCredentialReadinessConfirmedEvent {
  string api_key_id = 1;
  google.protobuf.Timestamp verified_at = 2;
  aevatar.gagents.channel.identity.abstractions.ManagedCodexCredentialReadinessEvidence readiness_evidence = 3;
}
```

The readiness-confirmed event is emitted when an idempotent duplicate command
matches current authoritative state or the Application explicitly confirms
fresh remote validation. This ensures a newly attached observation can receive
typed committed evidence instead of timing out on a silent no-op.

Explicit confirmation must match the complete `expected_credential`, including
the exact typed Vault reference. `expected_api_key_id` remains a narrow
correlation field but is insufficient by itself to authorize readiness.

- [ ] **Step 4: Implement Actor transitions and validation**

Add the transition cases:

```csharp
.On<ManagedCodexCredentialPolicyReconciledEvent>(ApplyPolicyReconciled)
.On<ManagedCodexCredentialReadinessConfirmedEvent>(static (state, _) => state.Clone())
```

Require both service IDs in `TryValidateCredential`:

```csharp
string.IsNullOrWhiteSpace(candidate.ChronoSandboxUserServiceId) ||
string.IsNullOrWhiteSpace(candidate.ChronoLlmUserServiceId) ||
string.Equals(
    candidate.ChronoSandboxUserServiceId.Trim(),
    candidate.ChronoLlmUserServiceId.Trim(),
    StringComparison.Ordinal)
```

Normalize both IDs. Implement policy reconciliation with these invariants:

```csharp
[EventHandler]
public async Task HandlePolicyReconciled(
    CommitManagedCodexCredentialPolicyReconciledCommand command)
{
    ArgumentNullException.ThrowIfNull(command);
    if (!TryValidateCredential(command.Credential, out var credential))
        return;

    var current = State.Credential;
    if (current is null ||
        current.Status != ManagedCodexCredentialStatus.Active ||
        !string.Equals(current.ApiKeyId, command.ExpectedApiKeyId?.Trim(), StringComparison.Ordinal) ||
        !string.Equals(current.SecretReference?.Ref, credential.SecretReference?.Ref, StringComparison.Ordinal))
    {
        await QueueIncomingCredentialCleanupAsync(credential);
        return;
    }

    if (current.Equals(credential))
    {
        await PersistDomainEventAsync(new ManagedCodexCredentialReadinessConfirmedEvent
        {
            ApiKeyId = current.ApiKeyId,
            VerifiedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ReadinessEvidence =
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
        });
        return;
    }

    await PersistDomainEventAsync(new ManagedCodexCredentialPolicyReconciledEvent
    {
        ApiKeyId = current.ApiKeyId,
        Credential = credential,
    });
}
```

When duplicate provision/rotation commands exactly equal current state, emit
the same structural readiness-confirmed event instead of returning silently.
Duplicate Actor turns never claim `RemoteValidated`; only the explicit typed
confirmation command may carry fresh Application validation evidence.

Update every existing `Descriptor(...)` fixture in managed Codex Actor,
projection, endpoint, lifecycle, and transport tests to set a distinct
non-empty `ChronoLlmUserServiceId`.

- [ ] **Step 5: Update command/query/projection adapters**

Add:

```csharp
Task<DispatchAdmission> CommitPolicyReconciledAsync(
    string expectedApiKeyId,
    ManagedCodexCredentialDescriptor credential,
    CancellationToken ct = default);
```

Dispatch `CommitManagedCodexCredentialPolicyReconciledCommand` through the
existing runtime-neutral command port.

Return the generated snapshot from the query port:

```csharp
var snapshot = new ManagedCodexCredentialSnapshot
{
    Credential = document.Credential.Clone(),
    StateVersion = document.StateVersion,
    LastEventId = document.LastEventId,
};
snapshot.PendingRevocations.Add(
    document.PendingRevocations.Select(static item => item.Clone()));
return snapshot;
```

Replace every positional construction such as:

```csharp
new ManagedCodexCredentialSnapshot(descriptor, [], 4)
```

with the generated protobuf form:

```csharp
new ManagedCodexCredentialSnapshot
{
    Credential = descriptor.Clone(),
    StateVersion = 4,
    LastEventId = "event-4",
}
```

Add both new events to `IsManagedCodexCredentialEvent`.

- [ ] **Step 6: Run protobuf generation through build and focused tests**

Run:

```bash
dotnet build agents/Aevatar.GAgents.Channel.Identity/Aevatar.GAgents.Channel.Identity.csproj --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --filter "FullyQualifiedName~ManagedCodexCredentialGAgentTests|FullyQualifiedName~ManagedCodexCredentialProjectorTests|FullyQualifiedName~ChannelIdentityCommittedStateProjectionActivationPlanProviderTests" --nologo
```

Expected: build and tests PASS.

- [ ] **Step 7: Commit**

```bash
git add agents/Aevatar.GAgents.Channel.Identity.Abstractions/protos/identity_contracts.proto agents/Aevatar.GAgents.Channel.Identity.Abstractions/ManagedCodex/IManagedCodexCredentialPorts.cs agents/Aevatar.GAgents.Channel.Identity/protos/managed_codex_credential.proto agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialGAgent.cs agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialCommandPort.cs agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialProjector.cs agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialProjectionQueryPort.cs agents/Aevatar.GAgents.Channel.Identity/Projection/ChannelIdentityCommittedStateProjectionActivationPlanProvider.cs test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialGAgentTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialProjectorTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ChannelIdentityCommittedStateProjectionActivationPlanProviderTests.cs
git commit -m "Model managed Codex dual-service credentials"
```

### Task 3: Add Committed Readiness Observation

**Files:**
- Modify: `agents/Aevatar.GAgents.Channel.Identity.Abstractions/ManagedCodex/IManagedCodexCredentialPorts.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialReadinessProjection.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialReadinessObservationPort.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/DependencyInjection/IdentityServiceCollectionExtensions.cs`
- Create: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialReadinessObservationTests.cs`

**Interfaces:**
- Produces: `IManagedCodexCredentialReadinessObservationPort.BindAsync`.
- Produces: `IManagedCodexCredentialReadinessObservationLease.ReadAllAsync`.
- Consumes: `IProjectionScopeActivationService`, `IProjectionScopeReleaseService`, and `IProjectionSessionEventHub<ManagedCodexCredentialSnapshot>`.

- [ ] **Step 1: Write failing observation tests**

Cover subscription-before-dispatch, snapshot mapping, disposal, and two
independent subscribers:

```csharp
[Fact]
public async Task BindAsync_WhenCommittedStateArrives_PublishesAuthoritativeSnapshot()
{
    await using var lease = await _port.BindAsync(Owner("user-a"));

    await _hub.PublishAsync(
        ManagedCodexCredentialActorIdentity.From(Owner("user-a")),
        _activation.LastRequest!.SessionId,
        Snapshot("key-a", "us-sandbox-a", "us-llm-a", stateVersion: 4));

    var observed = await ReadOneAsync(lease.ReadAllAsync());

    observed.Credential.ApiKeyId.Should().Be("key-a");
    observed.Credential.ChronoLlmUserServiceId.Should().Be("us-llm-a");
    observed.StateVersion.Should().Be(4);
}

[Fact]
public async Task Projector_WhenCommittedStateIsPublished_UsesCommittedVersionAndEventId()
{
    await _projector.ProjectAsync(
        Context("session-a", ActorId("user-a")),
        CommittedEnvelope(
            Descriptor("key-a", "us-sandbox-a", "us-llm-a"),
            version: 7,
            eventId: "event-7"));

    _hub.Published.Should().ContainSingle();
    _hub.Published[0].Event.StateVersion.Should().Be(7);
    _hub.Published[0].Event.LastEventId.Should().Be("event-7");
}
```

Use this local helper; do not add polling or `Task.Delay`:

```csharp
private static async Task<T> ReadOneAsync<T>(IAsyncEnumerable<T> source)
{
    await foreach (var item in source)
        return item;
    throw new InvalidOperationException("Observation completed without an item.");
}
```

- [ ] **Step 2: Run the tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --filter FullyQualifiedName~ManagedCodexCredentialReadinessObservationTests --nologo
```

Expected: FAIL because the observation contracts and implementation do not
exist.

- [ ] **Step 3: Add the abstraction contracts**

Add to `IManagedCodexCredentialPorts.cs`:

```csharp
public interface IManagedCodexCredentialReadinessObservationPort
{
    Task<IManagedCodexCredentialReadinessObservationLease> BindAsync(
        ExternalSubjectRef owner,
        CancellationToken ct = default);
}

public interface IManagedCodexCredentialReadinessObservationLease : IAsyncDisposable
{
    IAsyncEnumerable<ManagedCodexCredentialSnapshot> ReadAllAsync(
        CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement the Projection Session**

`ManagedCodexCredentialReadinessProjection.cs` contains:

```csharp
internal sealed class ManagedCodexCredentialReadinessProjectionContext
    : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}

internal sealed class ManagedCodexCredentialReadinessRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<ManagedCodexCredentialSnapshot>,
      IProjectionContextRuntimeLease<ManagedCodexCredentialReadinessProjectionContext>
{
    public ManagedCodexCredentialReadinessRuntimeLease(
        ManagedCodexCredentialReadinessProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context;
        SessionId = context.SessionId;
    }

    public string SessionId { get; }
    public ManagedCodexCredentialReadinessProjectionContext Context { get; }
}
```

The projector must unpack the authoritative committed state:

```csharp
if (!CommittedStateEventEnvelope.TryUnpackState<ManagedCodexCredentialState>(
        envelope,
        out _,
        out var stateEvent,
        out var state) ||
    stateEvent is null ||
    state?.Credential is null)
{
    return EmptyEntries;
}

var snapshot = new ManagedCodexCredentialSnapshot
{
    Credential = state.Credential.Clone(),
    StateVersion = stateEvent.Version,
    LastEventId = stateEvent.EventId ?? string.Empty,
    ReadinessEvidence = ResolveReadinessEvidence(stateEvent.EventData),
};
snapshot.PendingRevocations.Add(
    state.PendingRevocations.Select(static item => item.Clone()));
return
[
    new ProjectionSessionEventEntry<ManagedCodexCredentialSnapshot>(
        context.RootActorId,
        context.SessionId,
        snapshot),
];
```

Implement `IProjectionSessionEventCodec<ManagedCodexCredentialSnapshot>` with
channel `managed-codex-credential-readiness`, constant event type `snapshot`,
`ToByteString()`, and `ManagedCodexCredentialSnapshot.Parser.ParseFrom`.

- [ ] **Step 5: Implement bind/read/dispose**

`ManagedCodexCredentialReadinessObservationPort` must:

1. derive the actor ID from the complete owner;
2. create a fresh session ID with `Guid.NewGuid().ToString("N")`;
3. activate `ProjectionRuntimeMode.SessionObservation`;
4. subscribe the session hub;
5. write cloned snapshots into a bounded `Channel<ManagedCodexCredentialSnapshot>`;
6. dispose the subscription and release the runtime lease with
   `CancellationToken.None`.

Create the channel with:

```csharp
var snapshots = Channel.CreateBounded<ManagedCodexCredentialSnapshot>(
    new BoundedChannelOptions(16)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
```

The subscription callback returns
`snapshots.Writer.WriteAsync(snapshot.Clone())`, preserving backpressure rather
than dropping a committed readiness snapshot.

Expose snapshots without polling:

```csharp
public async IAsyncEnumerable<ManagedCodexCredentialSnapshot> ReadAllAsync(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(ct))
        yield return snapshot.Clone();
}
```

- [ ] **Step 6: Register the projection runtime and port**

Add to `AddChannelIdentity`:

```csharp
services.AddEventSinkProjectionRuntimeCore<
    ManagedCodexCredentialReadinessProjectionContext,
    ManagedCodexCredentialReadinessRuntimeLease,
    ManagedCodexCredentialSnapshot,
    ProjectionSessionScopeGAgent<ManagedCodexCredentialReadinessProjectionContext>>(
    static scopeKey => new ManagedCodexCredentialReadinessProjectionContext
    {
        SessionId = scopeKey.SessionId,
        RootActorId = scopeKey.RootActorId,
        ProjectionKind = scopeKey.ProjectionKind,
    },
    static context => new ManagedCodexCredentialReadinessRuntimeLease(context));
services.TryAddSingleton<
    IProjectionSessionEventCodec<ManagedCodexCredentialSnapshot>,
    ManagedCodexCredentialSnapshotCodec>();
services.TryAddSingleton<
    IProjectionSessionEventHub<ManagedCodexCredentialSnapshot>,
    ProjectionSessionEventHub<ManagedCodexCredentialSnapshot>>();
services.TryAddEnumerable(ServiceDescriptor.Singleton<
    IProjectionProjector<ManagedCodexCredentialReadinessProjectionContext>,
    ManagedCodexCredentialReadinessProjector>());
services.TryAddSingleton<
    IManagedCodexCredentialReadinessObservationPort,
    ManagedCodexCredentialReadinessObservationPort>();
```

- [ ] **Step 7: Run focused tests**

Run:

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --filter "FullyQualifiedName~ManagedCodexCredentialReadinessObservationTests|FullyQualifiedName~ManagedCodexCredentialProjectorTests|FullyQualifiedName~ChannelIdentityCommittedStateProjectionActivationPlanProviderTests" --nologo
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add agents/Aevatar.GAgents.Channel.Identity.Abstractions/ManagedCodex/IManagedCodexCredentialPorts.cs agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialReadinessProjection.cs agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialReadinessObservationPort.cs agents/Aevatar.GAgents.Channel.Identity/DependencyInjection/IdentityServiceCollectionExtensions.cs test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialReadinessObservationTests.cs
git commit -m "Observe committed managed Codex readiness"
```

### Task 4: Issue And Repair Exact Dual-Service NyxID Keys

**Files:**
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/IManagedCodexNyxIdCredentialPort.cs`
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexNyxIdCatalogResolver.cs`
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs`
- Modify: `src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdManagedCodexCredentialAdapter.cs`
- Modify: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialLifecycleTests.cs`

**Interfaces:**
- Produces: `ManagedCodexNyxIdEligibility(ChronoSandboxUserServiceId, ChronoLlmUserServiceId)`.
- Produces: `IManagedCodexNyxIdCredentialPort.UpdateApiKeyPolicyAsync`.
- Consumes: NyxID list/create/update/rotate/delete endpoints and existing lifecycle compensation.

- [ ] **Step 1: Write failing dual-service and repair tests**

Change the create assertion to:

```csharp
body.RootElement.GetProperty("allowed_service_ids")
    .EnumerateArray()
    .Select(static item => item.GetString())
    .Should()
    .BeEquivalentTo(["us-sandbox", "us-llm"], options => options.WithStrictOrdering());
```

Add policy order-independence and no-extra-grant lifecycle tests:

```csharp
[Fact]
public async Task ProvisionAsync_WhenPersistedServiceIdsAreReversed_AcceptsExactSet()
{
    var handler = SuccessfulProvisionHandler(
        persistedAllowedServiceIds: ["us-llm", "us-sandbox"]);

    var result = await CreateSuccessfulLifecycle(handler).ProvisionAsync(
        "user-bearer",
        "user-a");

    result.ApiKeyId.Should().Be("key-1");
}

[Fact]
public async Task ProvisionAsync_WhenPersistedKeyHasExtraService_RejectsIt()
{
    var handler = SuccessfulProvisionHandler(
        persistedAllowedServiceIds: ["us-sandbox", "us-llm", "us-extra"]);

    var act = () => CreateSuccessfulLifecycle(handler).ProvisionAsync(
        "user-bearer",
        "user-a");

    (await act.Should()
        .ThrowAsync<ManagedCodexCredentialLifecycleException>())
        .Which.Code.Should().Be("managed_api_key_issue_invalid");
}

private static RoutingHandler SuccessfulProvisionHandler(
    IReadOnlyList<string> persistedAllowedServiceIds) =>
    new(
        MeResponse(),
        UserServicesResponse(),
        """{"keys":[]}""",
        IssuedKeyResponse(
            "key-1",
            RawKey,
            allowedServiceIds: ["us-sandbox", "us-llm"]),
        ApiKeyListResponse(
            "key-1",
            Now.AddDays(30),
            allowedServiceIds: persistedAllowedServiceIds));

private static ManagedCodexCredentialLifecycle CreateSuccessfulLifecycle(
    RoutingHandler handler)
{
    var vault = Substitute.For<ISecretVault>();
    vault.PutAsync(
            Arg.Any<StoreSecretRequest>(),
            Arg.Any<CancellationToken>())
        .Returns(call => Task.FromResult(new StoreSecretResult(
            Reference(
                call.Arg<StoreSecretRequest>(),
                call.Arg<StoreSecretRequest>().RequestedRef!,
                version: 1))));
    var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
    commands.CommitProvisionedAsync(
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>())
        .Returns(Admission());
    return CreateLifecycle(handler, vault, commands);
}
```

Add an HTTP adapter test inside the lifecycle test fixture:

```csharp
[Fact]
public async Task UpdateApiKeyPolicyAsync_SendsExactTwoServices()
{
    var handler = new RoutingHandler(
        """{"id":"key-a","updated":true}""");
    var client = new NyxIdApiClient(
        new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
        new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
    var port = new NyxIdManagedCodexCredentialAdapter(
        new TestNyxIdApiClientFactory(client));

    await port.UpdateApiKeyPolicyAsync(
        "user-bearer",
        "key-a",
        new ManagedCodexNyxIdApiKeyPolicyUpdateRequest(
            "proxy",
            "codex",
            false,
            ["us-sandbox", "us-llm"],
            false,
            []),
        CancellationToken.None);

    handler.Methods.Should().ContainInOrder(HttpMethod.Put);
    using var update = JsonDocument.Parse(handler.RequestBodies.Single());
    update.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
    update.RootElement.GetProperty("allowed_service_ids")
        .EnumerateArray()
        .Select(static value => value.GetString())
        .Should().Equal("us-sandbox", "us-llm");
}
```

Change `IssuedKeyResponse` and `ApiKeyListResponse` test helpers to accept an
optional `IReadOnlyList<string>? allowedServiceIds` and serialize
`allowedServiceIds ?? ["us-sandbox", "us-llm"]`.

- [ ] **Step 2: Run lifecycle tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter FullyQualifiedName~ManagedCodexCredentialLifecycleTests --nologo
```

Expected: FAIL because creation still grants one service and no update-policy
port exists.

- [ ] **Step 3: Expand the catalog result**

Use:

```csharp
internal sealed record ManagedCodexNyxIdEligibility(
    string ChronoSandboxUserServiceId,
    string ChronoLlmUserServiceId);
```

Require a unique usable LLM match with a non-empty ID and return both normalized
IDs. Continue requiring the sandbox match to be personal and to have the
already-deployed delegation configuration.

Use `managed_user_services_unavailable` when either required service is
missing, ambiguous, inactive, has no stable ID, or disallows its credential
source. Preserve `chrono_sandbox_delegation_misconfigured` only for an existing
sandbox UserService whose delegation policy is wrong. Update the existing
inactive/missing-service tests to assert the new readiness code.

- [ ] **Step 4: Add the update-policy port and adapter**

Add:

```csharp
public sealed record ManagedCodexNyxIdApiKeyPolicyUpdateRequest(
    string Scopes,
    string Platform,
    bool AllowAllServices,
    IReadOnlyList<string> AllowedServiceIds,
    bool AllowAllNodes,
    IReadOnlyList<string> AllowedNodeIds);

Task UpdateApiKeyPolicyAsync(
    string bearerToken,
    string apiKeyId,
    ManagedCodexNyxIdApiKeyPolicyUpdateRequest request,
    CancellationToken ct = default);
```

The Infrastructure adapter sends:

```csharp
var response = await Client.UpdateApiKeyAsync(
    bearerToken,
    apiKeyId,
    JsonSerializer.Serialize(new
    {
        scopes = request.Scopes,
        platform = request.Platform,
        allow_all_services = request.AllowAllServices,
        allowed_service_ids = request.AllowedServiceIds,
        allow_all_nodes = request.AllowAllNodes,
        allowed_node_ids = request.AllowedNodeIds,
    }, RequestJsonOptions),
    ct).ConfigureAwait(false);
using var _ = ParseObject(response, "managed_api_key_update_invalid");
```

The lifecycle must always re-list and validate the key after the update; the
update response is not accepted as authorization truth.

- [ ] **Step 5: Make issue and validation exact for two IDs**

Replace single-ID helpers with:

```csharp
private static ManagedCodexNyxIdApiKeyIssueRequest IssueRequest(
    ManagedCodexNyxIdEligibility eligibility,
    DateTimeOffset expiresAt) =>
    new(
        CredentialName,
        "Aevatar managed codex_exec invocation credential",
        "proxy",
        "codex",
        false,
        [
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
        ],
        false,
        [],
        expiresAt);
```

Validate as an exact set:

```csharp
private static bool HasExactServiceIds(
    IReadOnlyList<string> actual,
    string sandboxId,
    string llmId)
{
    var expected = new HashSet<string>(
        [sandboxId, llmId],
        StringComparer.Ordinal);
    return actual.Count == expected.Count &&
           actual.All(expected.Contains) &&
           actual.Distinct(StringComparer.Ordinal).Count() == expected.Count;
}
```

Pass both IDs into `BuildDescriptor` and set
`ChronoLlmUserServiceId`.

- [ ] **Step 6: Add policy reconciliation under the existing lease**

Extract a private method:

```csharp
private async Task<ManagedCodexCredentialDescriptor> ReconcilePolicyAsync(
    string bearerToken,
    ExternalSubjectRef owner,
    ManagedCodexCredentialDescriptor current,
    ManagedCodexNyxIdApiKey remote,
    ManagedCodexNyxIdEligibility eligibility,
    CancellationToken ct)
```

It must:

1. update the remote key only when its policy is not exact;
2. re-list and select exactly the same key ID;
3. validate exact dual-service policy, active state, and future finite expiry;
4. resolve and validate the current Vault reference;
5. build a descriptor with both service IDs;
6. dispatch `CommitPolicyReconciledAsync`;
7. return the descriptor only to the caller that will still wait for committed
   observation in Task 5.

Do not use this returned method-local descriptor for chrono execution.

- [ ] **Step 7: Run focused tests**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter FullyQualifiedName~ManagedCodexCredentialLifecycleTests --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --filter FullyQualifiedName~ManagedCodexCredentialGAgentTests --nologo
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Aevatar.AI.Application.CodexExecution/ManagedCodex/IManagedCodexNyxIdCredentialPort.cs src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexNyxIdCatalogResolver.cs src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdManagedCodexCredentialAdapter.cs test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialLifecycleTests.cs
git commit -m "Repair managed Codex dual-service key policy"
```

### Task 5: Ensure Credential Readiness In The Same Call

**Files:**
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs`
- Create: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialReadinessTests.cs`

**Interfaces:**
- Produces: `ManagedCodexCredentialReadinessMode`.
- Produces: `IManagedCodexCredentialLifecycle.EnsureReadyAsync`.
- Consumes: Task 3 observation port and Task 4 dual-service NyxID policy.

Review hardening also extends the existing typed Identity contracts and Actor:

- `ManagedCodexCredentialReadinessEvidence` distinguishes structural
  confirmation from fresh remote validation;
- the snapshot, readiness confirmation command, and readiness confirmation
  event carry that enum;
- duplicate provision/rotation/policy commands emit
  `CurrentStateConfirmed`;
- Application repair paths explicitly confirm `RemoteValidated`.
- rotation commands and events carry typed `previous_credential_cleanup`, so
  the Actor commits the replacement descriptor and exact prior-key/prior-Vault
  cleanup fact atomically;
- provision, rotation, and policy reconciliation commands/events carry typed
  `obsolete_credential_cleanups`, so no remotely observed key or Vault locator
  is deleted before the incoming credential commit;
- cleanup completion carries the exact `secret_ref`; the Actor keeps
  same-key/different-locator facts separate, assigns one NyxID owner per key,
  and preserves one Vault track per exact locator;
- generic cleanup commands are rejected when a pending track targets the
  active API key or active Vault locator, and incoming credentials targeted by
  pending cleanup are rejected across provision, rotation, policy
  reconciliation, and readiness confirmation.

- [ ] **Step 1: Write failing same-call readiness tests**

Create `ManagedCodexCredentialReadinessTests` with these dependencies:

```csharp
private readonly IManagedCodexNyxIdCredentialPort _nyxId =
    Substitute.For<IManagedCodexNyxIdCredentialPort>();
private readonly ISecretVault _vault = Substitute.For<ISecretVault>();
private readonly IManagedCodexCredentialQueryPort _query =
    Substitute.For<IManagedCodexCredentialQueryPort>();
private readonly IManagedCodexCredentialCommandPort _commands =
    Substitute.For<IManagedCodexCredentialCommandPort>();
private readonly IManagedCodexCredentialMutationLease _lease =
    Substitute.For<IManagedCodexCredentialMutationLease>();
private readonly RecordingManagedCodexReadinessObservationPort _observation = new();
private readonly FakeTimeProvider _time = new(Now);
private readonly ManagedCodexCredentialLifecycle _lifecycle;
```

Construct `_lifecycle` with `ManagedCodexOptionsValidatorTests.ValidOptions()`,
the dependencies above, and
`NullLogger<ManagedCodexCredentialLifecycle>.Instance`. By default, return a
substituted `IManagedCodexCredentialMutationLeaseHandle` from
`TryAcquireAsync`; individual tests override it with `null` for the busy path.
The recording observation port owns a `Channel<ManagedCodexCredentialSnapshot>`
per bound lease and exposes `Publish`, `PublishAfterDispatch`, `Complete`, and
`LastPublished` only as deterministic test controls.

Add these behaviors:

```csharp
[Fact]
public async Task EnsureReadyAsync_WhenProjectionIsReady_ReturnsWithoutBearerOrMutation()
{
    _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
        .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));

    var ready = await _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        "user-bearer",
        ManagedCodexCredentialReadinessMode.Normal);

    ready.ApiKeyId.Should().Be("key-a");
    await _nyxId.DidNotReceiveWithAnyArgs()
        .GetCurrentUserIdAsync(default!, default);
    await _lease.DidNotReceiveWithAnyArgs()
        .TryAcquireAsync(default!, default);
}

[Fact]
public async Task EnsureReadyAsync_WhenMissing_ProvisionsWaitsForCommitAndReturnsObservedDescriptor()
{
    _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
        .Returns((ManagedCodexCredentialSnapshot?)null);
    _observation.PublishAfterDispatch(
        Snapshot(ReadyDescriptor("key-new"), stateVersion: 1));

    var ready = await _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        bearerToken: null,
        ManagedCodexCredentialReadinessMode.Normal);

    ready.ApiKeyId.Should().Be("key-new");
    ready.Should().BeEquivalentTo(_observation.LastPublished!.Credential);
}

[Fact]
public async Task EnsureReadyAsync_WhenLeaseIsBusy_WaitsForOtherInvocationCommit()
{
    _lease.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns((IManagedCodexCredentialMutationLeaseHandle?)null);
    _observation.Publish(
        Snapshot(ReadyDescriptor("key-from-other-call"), stateVersion: 2));

    var ready = await _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        bearerToken: null,
        ManagedCodexCredentialReadinessMode.Normal);

    ready.ApiKeyId.Should().Be("key-from-other-call");
    await _nyxId.DidNotReceiveWithAnyArgs()
        .CreateApiKeyAsync(default!, default!, default);
}

[Fact]
public async Task EnsureReadyAsync_WhenObservationEndsWithoutReadySnapshot_FailsWithCommitTimeout()
{
    _observation.Complete();

    var act = () => _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        "user-bearer",
        ManagedCodexCredentialReadinessMode.Normal);

    (await act.Should()
        .ThrowAsync<ManagedCodexCredentialLifecycleException>())
        .Which.Code.Should().Be("managed_credential_commit_timeout");
}

[Fact]
public async Task EnsureReadyAsync_WhenLegacyPolicyIsSingleService_UpdatesAndObservesReadyState()
{
    var legacy = ReadyDescriptor();
    legacy.ChronoLlmUserServiceId = string.Empty;
    _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
        .Returns(Snapshot(legacy, stateVersion: 3));
    _nyxId.ListUserServicesAsync("user-bearer", Arg.Any<CancellationToken>())
        .Returns(UserServices("us-sandbox", "us-llm"));
    _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
        .Returns(Keys(Key("key-a", ["us-sandbox"])));
    _observation.PublishAfterDispatch(
        Snapshot(ReadyDescriptor(), stateVersion: 4));

    var ready = await _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        "user-bearer",
        ManagedCodexCredentialReadinessMode.Normal);

    ready.ChronoLlmUserServiceId.Should().Be("us-llm");
    await _nyxId.Received(1).UpdateApiKeyPolicyAsync(
        "user-bearer",
        "key-a",
        Arg.Is<ManagedCodexNyxIdApiKeyPolicyUpdateRequest>(request =>
            request.AllowedServiceIds.Count == 2 &&
            request.AllowedServiceIds.Contains("us-sandbox") &&
            request.AllowedServiceIds.Contains("us-llm")),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task EnsureReadyAsync_WhenCredentialExpired_CreatesFreshCredential()
{
    var expired = ReadyDescriptor();
    expired.ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-1));
    _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
        .Returns(Snapshot(expired, stateVersion: 3));
    _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
        .Returns(Keys());
    _observation.PublishAfterDispatch(
        Snapshot(ReadyDescriptor("key-fresh"), stateVersion: 4));

    var ready = await _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        "user-bearer",
        ManagedCodexCredentialReadinessMode.Normal);

    ready.ApiKeyId.Should().Be("key-fresh");
    await _nyxId.Received(1).CreateApiKeyAsync(
        "user-bearer",
        Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task EnsureReadyAsync_WhenForcedValidationFindsMissingVaultSecret_ReplacesKey()
{
    _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
        .Returns(Snapshot(ReadyDescriptor(), stateVersion: 3));
    _vault.ResolveAsync(
            Arg.Any<ResolveSecretRequest>(),
            Arg.Any<CancellationToken>())
        .Returns(new ResolveSecretResult(
            null,
            null,
            SecretResolutionFailureReason.NotFound));
    _observation.PublishAfterDispatch(
        Snapshot(ReadyDescriptor("key-replacement"), stateVersion: 4));

    var ready = await _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        "user-bearer",
        ManagedCodexCredentialReadinessMode.ForceRemoteValidation);

    ready.ApiKeyId.Should().Be("key-replacement");
    await _nyxId.Received().CreateApiKeyAsync(
        "user-bearer",
        Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task EnsureReadyAsync_WhenReservedKeysAreAmbiguous_RevokesAllAndCreatesOne()
{
    _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
        .Returns((ManagedCodexCredentialSnapshot?)null);
    _nyxId.ListApiKeysAsync("user-bearer", Arg.Any<CancellationToken>())
        .Returns(Keys(
            Key("key-orphan-a", ["us-sandbox", "us-llm"]),
            Key("key-orphan-b", ["us-sandbox", "us-llm"])));
    _observation.PublishAfterDispatch(
        Snapshot(ReadyDescriptor("key-fresh"), stateVersion: 1));

    var ready = await _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        "user-bearer",
        ManagedCodexCredentialReadinessMode.Normal);

    ready.ApiKeyId.Should().Be("key-fresh");
    await _nyxId.Received(1).RevokeApiKeyAsync(
        "user-bearer",
        "key-orphan-a",
        Arg.Any<CancellationToken>());
    await _nyxId.Received(1).RevokeApiKeyAsync(
        "user-bearer",
        "key-orphan-b",
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task EnsureReadyAsync_WhenObsoleteCleanupFails_DoesNotBlockReadyCredential()
{
    var snapshot = Snapshot(ReadyDescriptor(), stateVersion: 4);
    snapshot.PendingRevocations.Add(Cleanup("key-old"));
    _query.ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>())
        .Returns(snapshot);
    _nyxId.RevokeApiKeyAsync(
            "user-bearer",
            "key-old",
            Arg.Any<CancellationToken>())
        .Returns(false);

    var ready = await _lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        "user-bearer",
        ManagedCodexCredentialReadinessMode.Normal);

    ready.ApiKeyId.Should().Be("key-a");
}

[Fact]
public async Task EnsureReadyAsync_WhenFirstCallsAreConcurrent_MutatesOnceAndBothReturnCommittedCredential()
{
    var lifecycle = CreateLifecycle(
        mutationLease: new InMemoryManagedCodexCredentialMutationLease());
    var mutationEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseMutation = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    _nyxId.CreateApiKeyAsync(
            "user-bearer",
            Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
            Arg.Any<CancellationToken>())
        .Returns(async _ =>
        {
            mutationEntered.TrySetResult();
            await releaseMutation.Task;
            return IssuedKey("key-new");
        });

    var first = lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        "user-bearer",
        ManagedCodexCredentialReadinessMode.Normal);
    await mutationEntered.Task;

    var second = lifecycle.EnsureReadyAsync(
        Owner("user-a"),
        bearerToken: null,
        ManagedCodexCredentialReadinessMode.Normal);
    _observation.PublishAfterDispatch(
        Snapshot(ReadyDescriptor("key-new"), stateVersion: 1));
    releaseMutation.TrySetResult();

    var results = await Task.WhenAll(first, second);

    results.Should().OnlyContain(value => value.ApiKeyId == "key-new");
    await _nyxId.Received(1).CreateApiKeyAsync(
        "user-bearer",
        Arg.Any<ManagedCodexNyxIdApiKeyIssueRequest>(),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run lifecycle tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter FullyQualifiedName~ManagedCodexCredentialReadinessTests --nologo
```

Expected: FAIL because `EnsureReadyAsync`, the readiness mode, and observation
dependency do not exist.

- [ ] **Step 3: Add the readiness API**

Use:

```csharp
public enum ManagedCodexCredentialReadinessMode
{
    Normal = 0,
    ForceRemoteValidation = 1,
}

Task<ManagedCodexCredentialDescriptor> EnsureReadyAsync(
    ExternalSubjectRef owner,
    string? bearerToken,
    ManagedCodexCredentialReadinessMode mode,
    CancellationToken ct = default);
```

Inject `IManagedCodexCredentialReadinessObservationPort` into
`ManagedCodexCredentialLifecycle`.

- [ ] **Step 4: Implement fast path, bind, re-read, and wait**

Use this order:

```csharp
EnsureEnabled();
ValidateOwner(owner);
EnsureEligible(owner.ExternalUserId);

var projected = await _queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
if (mode == ManagedCodexCredentialReadinessMode.Normal &&
    IsReady(projected?.Credential, owner, _timeProvider.GetUtcNow()) &&
    (projected!.PendingRevocations.Count == 0 ||
     string.IsNullOrWhiteSpace(bearerToken)))
{
    return projected!.Credential.Clone();
}

await using var observation = await _readinessObservation.BindAsync(owner, ct)
    .ConfigureAwait(false);
projected = await _queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
if (mode == ManagedCodexCredentialReadinessMode.Normal &&
    IsReady(projected?.Credential, owner, _timeProvider.GetUtcNow()) &&
    (projected!.PendingRevocations.Count == 0 ||
     string.IsNullOrWhiteSpace(bearerToken)))
{
    return projected!.Credential.Clone();
}
```

`IsReady` validates owner, active status, future expiry, fixed slug, distinct
non-empty sandbox/LLM IDs, and a typed Vault reference. It does not resolve the
secret or call NyxID on the normal fast path. Pending obsolete cleanup is not a
readiness condition: a no-bearer call returns the ready credential immediately;
an authenticated call enters the serialized slow path to retry cleanup
best-effort and still returns the ready credential if cleanup remains pending.

Wait without polling:

```csharp
private async Task<ManagedCodexCredentialDescriptor> WaitForReadyAsync(
    IManagedCodexCredentialReadinessObservationLease observation,
    ExternalSubjectRef owner,
    ManagedCodexCredentialReadinessMode mode,
    CancellationToken ct)
{
    await foreach (var snapshot in observation.ReadAllAsync(ct).ConfigureAwait(false))
    {
        if (HasSufficientReadinessEvidence(snapshot.ReadinessEvidence, mode) &&
            IsReady(snapshot.Credential, owner, _timeProvider.GetUtcNow()))
            return snapshot.Credential.Clone();
    }

    throw Failure(
        "managed_credential_commit_timeout",
        "Managed Codex credential readiness was not committed within the allowed time.");
}
```

Map bounded observation cancellation that was not caller cancellation to the
same stable timeout code.

- [ ] **Step 5: Serialize mutation and make busy callers wait**

After binding and re-reading, attempt the distributed lease before requiring a
bearer. A concurrent invocation that does not own the mutation first waits for
typed committed evidence:

```csharp
var lease = await _mutationLease.TryAcquireAsync(
    ManagedCodexCredentialActorIdentity.From(owner),
    ct).ConfigureAwait(false);
if (lease is null)
{
    var outcome = await WaitForConcurrentReadinessAsync(
        observation,
        owner,
        projected,
        bearerToken,
        mode,
        boundedWaitToken);
    if (outcome is ConcurrentCredentialCommitted committed)
        return committed.Credential;

    var acquired = (ConcurrentMutationLeaseAcquired)outcome;
    lease = acquired.Lease;
    reacquisitionTrigger = acquired.Trigger;
}

return await EnsureReadyAsLeaseOwnerAsync(
    observation,
    owner,
    bearerToken,
    mode,
    lease,
    reacquisitionTrigger,
    outcomeDeadline,
    boundedPreMutationToken,
    ct);
```

`WaitForConcurrentReadinessAsync` returns one of two typed outcomes:

```text
ConcurrentCredentialCommitted(credential)
ConcurrentMutationLeaseAcquired(lease, triggeringSnapshot)
```

Normal mode accepts either `CurrentStateConfirmed` or `RemoteValidated`.
Force mode accepts only `RemoteValidated`. A Force waiter that observes
`CurrentStateConfirmed`:

1. attempts the distributed lease exactly once, whether or not it has a bearer;
2. when the lease is still busy, continues waiting only for sufficient
   committed evidence and never retries the lease;
3. when the lease is acquired without a bearer, disposes it and fails
   `managed_user_authorization_unavailable`;
4. when the lease is acquired with a bearer, returns the typed lease outcome.

The lease holder re-reads the projection once more before remote work, because
another committed operation may have completed immediately before acquisition.
For reacquisition, it selects the re-read only when its authoritative
`StateVersion` is at least the triggering committed snapshot's version;
otherwise the triggering snapshot is the fallback. It then requires and
verifies the bearer owner.

Create one absolute outcome deadline immediately before every distributed lease
acquisition attempt. The initial attempt and the Force caller's one
reacquisition attempt therefore have separate anchors, and delayed acquisition
cannot extend work beyond the fixed Garnet TTL. `MutationCompletionSeconds`
bounds primary work. The lease configuration must additionally reserve ten
seconds for compensation, ten seconds for durable Actor recording, and ten
seconds of lease-safety margin, so
`MutationLeaseSeconds >= MutationCompletionSeconds + 30`.

Before irreversible mutation, derive a token bounded by both caller cancellation
and the primary deadline. After mutation begins, compensation and cleanup
recording use their later reserved absolute boundaries; no phase receives a
fresh full-duration timeout. The same pre-acquisition anchor and phased
boundaries apply to the explicit Provision, Rotate, and Revoke APIs, including
ambiguous-dispatch reconciliation and compensation, while their accepted-only
receipt semantics remain unchanged.

A Normal owner that only retries obsolete cleanup releases the mutation lease
before dispatching `CurrentStateConfirmed`. Its cleanup attempt reserves the
last ten seconds of the shared deadline for that structural dispatch and
observation. Internal cleanup timeout is best effort and leaves cleanup pending;
caller cancellation observed before irreversible cleanup still propagates. The
committed structural event is the event-driven handoff that permits a waiting
Force caller's one acquisition attempt.

- [ ] **Step 6: Implement deterministic repair selection**

Under the lease:

1. resolve both UserServices;
2. list active reserved `aevatar-managed-codex` keys;
3. prefer the projected key only if remote policy and Vault reference can be
   reconciled;
4. adopt one unambiguous recoverable remote key when projection is absent;
5. update a recoverable single-service key in place;
6. replace expired, revoked, missing-secret, or irreconcilable credentials;
7. for every remotely observed obsolete reserved key, derive its deterministic
   Vault reference and carry a typed cleanup intent in the provision, rotation,
   or policy-reconciliation command; do not delete any observed key or locator
   before the exact incoming credential is committed; manual provision/rotation
   reconciliation candidates that fail validation or deterministic Vault
   resolution follow this same atomic path rather than issuance compensation;
   an active reserved entry without a stable nonblank key ID fails closed before
   any mutation because no exact cleanup identity can be constructed; every
   active-key list or relist repeats this validation, including post-issuance
   confirmation and policy reconciliation; only the exact stable nonblank ID
   returned by the local create or rotate may enter issuance compensation;
   every bearer-authorized Actor-owned cleanup retry and explicit revoke
   performs the same read-only preflight, while post-commit best-effort cleanup
   skips mutation and preserves committed readiness when that preflight fails;
8. dispatch provision or policy reconciliation directly; when rotating,
   include the exact previous API-key ID, previous Vault locator, and
   independent pending-track flags as typed `previous_credential_cleanup`; the
   Actor rejects a mismatch and commits the new descriptor plus prior cleanup
   atomically;
9. explicitly dispatch `ConfirmReadiness(RemoteValidated)` for the expected
   complete active descriptor after remote validation, including duplicate/no-op
   command cases;
10. after an exactly matching provision, rotation, or reconciliation descriptor
    is observed committed, retry only the Actor-owned cleanup tracks
    best-effort and complete each successful track by exact
    `(ApiKeyId, SecretRef)` identity; normalize same-key/different-locator
    facts so the exact previous Actor cleanup owns the single NyxID track during
    rotation, otherwise the stable sorted locator owns it, while every locator
    retains its independent Vault track; timeout or rejected completion never
    suppresses the committed ready credential in Normal or Force mode;
11. map Vault `Unauthorized`, `AuthenticationFailed`, `KeyringMismatch`, and
    `UnsupportedAlgorithm` to `managed_credential_vault_unavailable` without
    revoking, creating, or updating NyxID keys;
12. when the same API key and deterministic Vault locator resolve to newer
    reference metadata than the committed descriptor, select replacement and
    complete readiness in the same call rather than certifying the stale
    descriptor or timing out;
13. return only `WaitForReadyAsync(...)` after sufficient committed evidence
    for the exact expected descriptor.

Every cleanup-recording result is checked. If compensation or cancellation
produces an outcome that cannot be admitted to the Actor with the live
recording reserve, return `managed_credential_persistence_pending`. Manual
pending-cleanup calls use the caller-linked pre-mutation token at the actual
external boundary. Manual revoke catches compensation expiry, leaves unknown
or unattempted tracks pending, and commits them with the independent recording
reserve. Cancellation, exception, or rejected admission from that
post-destruction revoked-state recording also returns
`managed_credential_persistence_pending`.

Change pending cleanup handling to return a result instead of always throwing:

```csharp
private async Task<bool> TryRetryPendingCleanupAsync(
    string bearerToken,
    ExternalSubjectRef owner,
    IReadOnlyList<ManagedCodexCredentialCleanup> pending,
    CancellationToken ct)
```

If a committed ready credential exists, log only key IDs for failed obsolete
cleanup and continue. If no ready credential can be established, retain
`managed_credential_cleanup_pending`.

- [ ] **Step 7: Run lifecycle and lease tests**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter "FullyQualifiedName~ManagedCodexCredentialReadinessTests|FullyQualifiedName~ManagedCodexCredentialLifecycleTests" --nologo
bash tools/ci/test_stability_guards.sh
```

Expected: tests and guard PASS with no new polling allowlist entry.

- [ ] **Step 8: Commit**

```bash
git add src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialReadinessTests.cs
git commit -m "Ensure managed Codex credentials transparently"
```

### Task 6: Move Ensure-And-Execute Orchestration Into Application

**Files:**
- Modify: `src/Aevatar.AI.Application.CodexExecution/Aevatar.AI.Application.CodexExecution.csproj`
- Create: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/IManagedCodexChronoTransport.cs`
- Create: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexExecutionCoordinator.cs`
- Modify: `src/Aevatar.AI.Application.CodexExecution/DependencyInjection/ServiceCollectionExtensions.cs`
- Delete: `src/Aevatar.AI.Infrastructure.ChronoSandbox/ChronoSandboxCodexExecutionAdapter.cs`
- Move: `src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdChronoSandboxCodexClient.cs` to `src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdManagedCodexChronoTransport.cs`
- Modify: `src/Aevatar.AI.Infrastructure.ChronoSandbox/ServiceCollectionExtensions.cs`
- Create: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexExecutionCoordinatorTests.cs`
- Move: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/NyxIdChronoSandboxCodexClientTests.cs` to `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/NyxIdManagedCodexChronoTransportTests.cs`
- Delete: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ChronoSandboxCodexExecutionAdapterTests.cs`
- Modify: `test/Aevatar.Architecture.Tests/Rules/ManagedCodexDependencyBoundaryTests.cs`

**Interfaces:**
- Produces: `IManagedCodexChronoTransport.ExecuteAsync(request, credential, ct)`.
- Produces: Application-owned managed-sandbox `ICodexExecutionPort`.
- Consumes: Task 5 `EnsureReadyAsync`.

- [ ] **Step 1: Write failing coordinator tests**

Test first-call continuation:

```csharp
[Fact]
public async Task ExecuteAsync_WhenCredentialIsMissing_EnsuresThenExecutesInSameCall()
{
    _lifecycle.EnsureReadyAsync(
            Owner("user-a"),
            "caller-token",
            ManagedCodexCredentialReadinessMode.Normal,
            Arg.Any<CancellationToken>())
        .Returns(ReadyDescriptor());
    _transport.ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Is<ManagedCodexCredentialDescriptor>(value => value.ApiKeyId == "key-a"),
            Arg.Any<CancellationToken>())
        .Returns(new CodexExecutionResult("CODEX_EXEC_READY", 0, "diag-a", 100));

    var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

    events.Select(static item => item.Kind)
        .Should().Equal(CodexExecutionEventKind.Started, CodexExecutionEventKind.Completed);
    events[^1].Result!.Output.Should().Be("CODEX_EXEC_READY");
}
```

Test one authorization repair retry:

```csharp
[Fact]
public async Task ExecuteAsync_WhenAuthorizationIsDenied_RepairsAndRetriesOnce()
{
    _transport.ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>())
        .Returns(
            _ => throw TransportFailure("managed_proxy_authorization_denied"),
            _ => new CodexExecutionResult("CODEX_EXEC_READY", 0));

    var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

    events[^1].Kind.Should().Be(CodexExecutionEventKind.Completed);
    await _lifecycle.Received(1).EnsureReadyAsync(
        Owner("user-a"),
        "caller-token",
        ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
        Arg.Any<CancellationToken>());
    await _transport.Received(2).ExecuteAsync(
        Arg.Any<CodexExecutionRequest>(),
        Arg.Any<ManagedCodexCredentialDescriptor>(),
        Arg.Any<CancellationToken>());
}
```

Add these exact terminal-path tests:

```csharp
[Fact]
public async Task ExecuteAsync_WhenAuthorizationIsDeniedTwice_FailsAfterOneRepair()
{
    _transport.ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>())
        .Returns(_ => throw TransportFailure("managed_proxy_authorization_denied"));

    var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

    events[^1].Kind.Should().Be(CodexExecutionEventKind.Failed);
    events[^1].Failure!.Code.Should().Be("managed_proxy_authorization_denied");
    await _lifecycle.Received(1).EnsureReadyAsync(
        Owner("user-a"),
        "caller-token",
        ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
        Arg.Any<CancellationToken>());
}

[Theory]
[InlineData("managed_proxy_timeout", CodexExecutionFailureKind.TimedOut)]
[InlineData("managed_proxy_unavailable", CodexExecutionFailureKind.CapacityUnavailable)]
[InlineData("managed_response_invalid", CodexExecutionFailureKind.MalformedOutput)]
public async Task ExecuteAsync_WhenFailureIsNotRepairable_DoesNotForceRepair(
    string code,
    CodexExecutionFailureKind kind)
{
    _transport.ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>())
        .Returns(_ => throw TransportFailure(code, kind));

    var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

    events[^1].Failure!.Code.Should().Be(code);
    await _lifecycle.DidNotReceive().EnsureReadyAsync(
        Arg.Any<ExternalSubjectRef>(),
        Arg.Any<string?>(),
        ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task ExecuteAsync_WhenFirstUseAuthorizationIsUnavailable_MapsProvisioningFailure()
{
    _lifecycle.EnsureReadyAsync(
            Arg.Any<ExternalSubjectRef>(),
            null,
            ManagedCodexCredentialReadinessMode.Normal,
            Arg.Any<CancellationToken>())
        .Returns<Task<ManagedCodexCredentialDescriptor>>(_ =>
            throw new ManagedCodexCredentialLifecycleException(
                "managed_user_authorization_unavailable",
                "authorization required"));

    var events = await CollectAsync(_coordinator.ExecuteAsync(Request(bearer: null)));

    events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.ProvisioningFailed);
    events[^1].Failure.Code.Should().Be("managed_user_authorization_unavailable");
}

[Fact]
public async Task ExecuteAsync_WhenCallerCancels_EmitsCancelledTerminalEvent()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    var events = await CollectAsync(
        _coordinator.ExecuteAsync(Request(), cts.Token),
        CancellationToken.None);

    events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.Cancelled);
}

[Fact]
public async Task ExecuteAsync_WhenNativeAuthorityIsMissing_FailsBeforeDependencies()
{
    var events = await CollectAsync(
        _coordinator.ExecuteAsync(Request(authority: null)));

    events[^1].Failure!.Code.Should().Be("managed_identity_unavailable");
    await _lifecycle.DidNotReceiveWithAnyArgs().EnsureReadyAsync(
        default!,
        default,
        default,
        default);
    await _transport.DidNotReceiveWithAnyArgs().ExecuteAsync(
        default!,
        default!,
        default);
}
```

- [ ] **Step 2: Run coordinator tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter FullyQualifiedName~ManagedCodexExecutionCoordinatorTests --nologo
```

Expected: FAIL because the coordinator and transport port do not exist.

- [ ] **Step 3: Add Application dependency and transport contract**

Add the `Aevatar.AI.Abstractions` project reference to the Application project.

Create:

```csharp
public interface IManagedCodexChronoTransport
{
    Task<CodexExecutionResult> ExecuteAsync(
        CodexExecutionRequest request,
        ManagedCodexCredentialDescriptor credential,
        CancellationToken ct = default);
}

public sealed class ManagedCodexTransportException : Exception
{
    public ManagedCodexTransportException(CodexExecutionFailure failure)
        : base(failure.Message)
    {
        Failure = failure;
    }

    public CodexExecutionFailure Failure { get; }
}
```

- [ ] **Step 4: Implement the coordinator**

`ManagedCodexExecutionCoordinator`:

```csharp
public CodexExecutionTarget.TargetOneofCase TargetKind =>
    CodexExecutionTarget.TargetOneofCase.ManagedSandbox;
```

Execution order:

```csharp
yield return CodexExecutionEvent.Started();
var owner = ValidateRequestAndResolveOwner(request);
var credential = await _lifecycle.EnsureReadyAsync(
    owner,
    request.Caller.NyxIdAccessToken,
    ManagedCodexCredentialReadinessMode.Normal,
    ct).ConfigureAwait(false);

try
{
    var result = await _transport.ExecuteAsync(request, credential, ct)
        .ConfigureAwait(false);
    yield return CodexExecutionEvent.Completed(result);
}
catch (ManagedCodexTransportException exception)
    when (CanRepair(exception.Failure, request.Caller.NyxIdAccessToken))
{
    var repaired = await _lifecycle.EnsureReadyAsync(
        owner,
        request.Caller.NyxIdAccessToken,
        ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
        ct).ConfigureAwait(false);
    var result = await _transport.ExecuteAsync(request, repaired, ct)
        .ConfigureAwait(false);
    yield return CodexExecutionEvent.Completed(result);
}
```

Because C# iterators cannot yield inside a `try` with `catch`, calculate one
terminal `CodexExecutionEvent` in a private async method and yield it after the
method returns, matching the existing adapter pattern.

`CanRepair` returns true only for:

```csharp
failure.Code is "managed_proxy_authorization_denied" or "managed_credential_unavailable"
```

and only when the caller bearer is non-empty.

Map lifecycle failures to `CodexExecutionFailureKind.ProvisioningFailed` except
for global disabled/eligibility/identity admission failures, which remain
`TargetNotConfigured` or `AdmissionDenied`.

- [ ] **Step 5: Convert the Infrastructure client into a pure transport**

Rename the class to `NyxIdManagedCodexChronoTransport` and implement
`IManagedCodexChronoTransport`.

Remove `IManagedCodexCredentialQueryPort` and credential selection from its
constructor. Its method receives the descriptor:

```csharp
public async Task<CodexExecutionResult> ExecuteAsync(
    CodexExecutionRequest request,
    ManagedCodexCredentialDescriptor credential,
    CancellationToken ct = default)
```

Keep exact owner/reference validation, just-in-time Vault resolution, fixed
`?_nyxid_via=<sandbox-user-service-id>`, bounded response parsing, and redaction.
Throw `ManagedCodexTransportException` for typed failures.

- [ ] **Step 6: Update DI and architecture assertions**

Application DI registers:

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<
    ICodexExecutionPort,
    ManagedCodexExecutionCoordinator>());
```

Infrastructure DI registers:

```csharp
services.TryAddSingleton<
    IManagedCodexChronoTransport,
    NyxIdManagedCodexChronoTransport>();
```

Delete Infrastructure registration of `ChronoSandboxCodexExecutionAdapter`.
Extend the architecture test to assert that the sole class implementing the
managed `ICodexExecutionPort` is under
`src/Aevatar.AI.Application.CodexExecution`.

- [ ] **Step 7: Run coordinator, transport, composition, and architecture tests**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter "FullyQualifiedName~ManagedCodexExecutionCoordinatorTests|FullyQualifiedName~NyxIdManagedCodexChronoTransportTests" --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --filter FullyQualifiedName~MainnetHostCompositionTests --nologo
dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj --filter FullyQualifiedName~ManagedCodexDependencyBoundaryTests --nologo
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Aevatar.AI.Application.CodexExecution/Aevatar.AI.Application.CodexExecution.csproj src/Aevatar.AI.Application.CodexExecution/ManagedCodex/IManagedCodexChronoTransport.cs src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexExecutionCoordinator.cs src/Aevatar.AI.Application.CodexExecution/DependencyInjection/ServiceCollectionExtensions.cs src/Aevatar.AI.Infrastructure.ChronoSandbox/ServiceCollectionExtensions.cs src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdManagedCodexChronoTransport.cs test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexExecutionCoordinatorTests.cs test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/NyxIdManagedCodexChronoTransportTests.cs test/Aevatar.Architecture.Tests/Rules/ManagedCodexDependencyBoundaryTests.cs
git rm src/Aevatar.AI.Infrastructure.ChronoSandbox/ChronoSandboxCodexExecutionAdapter.cs src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdChronoSandboxCodexClient.cs test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ChronoSandboxCodexExecutionAdapterTests.cs test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/NyxIdChronoSandboxCodexClientTests.cs
git commit -m "Coordinate managed Codex execution in application"
```

### Task 7: Align Host Diagnostics, Documentation, And End-To-End Contracts

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/ManagedCodex/ManagedCodexCredentialEndpoints.cs`
- Modify: `test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs`
- Modify: `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdCodexExecToolTests.cs`
- Modify: `docs/canon/managed-codex-execution.md`
- Modify: `docs/operations/2026-07-16-managed-codex-exec-rollout.md`

**Interfaces:**
- Produces: diagnostic endpoint responses aligned with automatic readiness.
- Consumes: all prior tasks' options, coordinator, snapshot, and failure codes.

- [ ] **Step 1: Write failing Host and tool contract tests**

Add a status assertion:

```csharp
payload.GetProperty("eligible").GetBoolean().Should().BeTrue();
payload.GetProperty("status").GetString().Should().Be("not_provisioned");
```

Add an identity-boundary test using intentionally different IDs:

```csharp
[Fact]
public async Task StatusAsync_WhenScopeIdDiffersFromNyxIdSubject_UsesNyxIdSubject()
{
    var http = AuthenticatedHttpContext(
        ("scope_id", "scope-alpha"),
        ("sub", "user-alpha"));
    var query = Substitute.For<IManagedCodexCredentialQueryPort>();

    _ = await ManagedCodexCredentialEndpoints.StatusAsync(
        http,
        query,
        Options.Create(ManagedOptions(enabled: true)),
        TimeProvider.System,
        CancellationToken.None);

    await query.Received(1).ResolveAsync(
        Arg.Is<ExternalSubjectRef>(owner =>
            owner.ExternalUserId == "user-alpha"),
        Arg.Any<CancellationToken>());
}
```

Add failure-code mappings:

```csharp
[Theory]
[InlineData("managed_user_authorization_unavailable", 401)]
[InlineData("managed_feature_not_enabled", 403)]
[InlineData("managed_credential_commit_timeout", 503)]
[InlineData("managed_user_services_unavailable", 503)]
public async Task ProvisionAsync_MapsTransparentReadinessFailures(
    string code,
    int expectedStatus)
```

Extend `NyxIdCodexExecToolTests` so the managed target still exposes only
`target`, `workspace`, `prompt`, and timeout; no provisioning or credential
argument is added.

- [ ] **Step 2: Run Host/tool tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --filter "FullyQualifiedName~MainnetManagedCodexCredentialEndpointsTests|FullyQualifiedName~MainnetHostCompositionTests" --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --filter FullyQualifiedName~NyxIdCodexExecToolTests --nologo
```

Expected: endpoint tests FAIL until eligibility and new code mappings are
exposed; tool tests continue to enforce no credential arguments.

- [ ] **Step 3: Align diagnostic endpoint behavior**

Status remains read-only and performs no provisioning:

```csharp
eligible = options.Value.IsEligible(userId)
```

`TryResolveSubject` must not treat `scope_id` as a NyxID user ID. Resolve only
`uid`, `sub`, `ClaimTypes.NameIdentifier`, and `user_id`; keep `scope_id`
separate from credential ownership.

Map:

```csharp
"managed_user_authorization_unavailable" => StatusCodes.Status401Unauthorized,
"managed_feature_not_enabled" or
"nyxid_identity_mismatch" => StatusCodes.Status403Forbidden,
"managed_credential_commit_timeout" or
"managed_user_services_unavailable" => StatusCodes.Status503ServiceUnavailable,
```

Keep explicit provision/rotate/revoke endpoints for diagnostics and emergency
operations. Do not call them from tool execution.

- [ ] **Step 4: Rewrite the canonical normal path**

Update `docs/canon/managed-codex-execution.md` to state:

```text
codex_exec -> Application EnsureReadyAsync -> committed credential observation
           -> chrono transport -> terminal Codex result
```

Replace the old one-service policy with exact sandbox + LLM service grants.
State that the first interactive call uses the user's current bearer only for
key creation/repair, while later ready background calls use the Vault-backed
credential without requiring that bearer.

- [ ] **Step 5: Rewrite rollout configuration and canary steps**

The runbook configuration becomes:

```text
Aevatar__CodexExecution__ManagedSandbox__Enabled=true
Aevatar__CodexExecution__ManagedSandbox__RolloutBoundary=InternalOnly
Aevatar__CodexExecution__ManagedSandbox__Eligibility__Mode=Allowlist
Aevatar__CodexExecution__ManagedSandbox__Eligibility__AllowedNyxIdUserIds__0=example-nyxid-user-id
```

For all ready internal users:

```text
Aevatar__CodexExecution__ManagedSandbox__Eligibility__Mode=All
```

The enabled configuration remains internal-only until the delegated
authorization boundary no longer uses `proxy:*`.

Remove manual POST-and-poll provisioning from the normal canary sequence.
Canary proof starts directly with the public workflow and verifies that the
first call creates or repairs the credential and returns `CODEX_EXEC_READY`.
Keep the manual API in a diagnostics section.

- [ ] **Step 6: Run focused tests and docs lint**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --filter "FullyQualifiedName~MainnetManagedCodexCredentialEndpointsTests|FullyQualifiedName~MainnetHostCompositionTests" --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --filter FullyQualifiedName~NyxIdCodexExecToolTests --nologo
bash tools/docs/lint.sh
```

Expected: tests and lint PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Aevatar.Mainnet.Host.Api/ManagedCodex/ManagedCodexCredentialEndpoints.cs test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs test/Aevatar.AI.Tests/NyxIdCodexExecToolTests.cs docs/canon/managed-codex-execution.md docs/operations/2026-07-16-managed-codex-exec-rollout.md
git commit -m "Make managed Codex readiness transparent"
```

### Task 8: Run Repository Gates And Review Secret Boundaries

**Files:**
- Review: all files changed by Tasks 1-7
- Modify only if a gate exposes a defect in the planned implementation.

**Interfaces:**
- Consumes: the complete implementation.
- Produces: verification evidence suitable for the final commit/PR/issue update.

- [ ] **Step 1: Run all managed Codex test slices**

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --filter "FullyQualifiedName~ManagedCodex|FullyQualifiedName~ChannelIdentityCommittedStateProjectionActivationPlanProviderTests" --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --filter "FullyQualifiedName~MainnetManagedCodex|FullyQualifiedName~MainnetHostCompositionTests" --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --filter FullyQualifiedName~NyxIdCodexExecToolTests --nologo
dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj --filter FullyQualifiedName~ManagedCodexDependencyBoundaryTests --nologo
```

Expected: zero failures.

- [ ] **Step 2: Run required guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/ci/solution_split_guards.sh
bash tools/ci/test_solution_ownership_guard.sh
bash tools/ci/slow_test_guards.sh
bash tools/docs/lint.sh
```

Expected: every guard PASS. No polling allowlist entry is added.

- [ ] **Step 3: Build the solution**

```bash
dotnet build aevatar.slnx --nologo
```

Expected: zero errors. Record pre-existing warnings separately.

- [ ] **Step 4: Search for stale configuration and secret leaks**

```bash
rg -n "ProvisioningAllowedNyxIdUserIds|managed_credential_not_provisioned|provisioning_accepted" src agents test docs
rg -n "full_key|NYXID_LLM_TOKEN|Authorization: Bearer|raw-agent-key" src agents docs
```

Expected:

- no old provisioning option remains;
- `managed_credential_not_provisioned` is absent from the normal execution path;
- raw-key field names appear only at the NyxID adapter boundary or redaction
  tests;
- no bearer, agent key, or delegation token is added to protobuf, logs, or
  results.

- [ ] **Step 5: Inspect the final diff and unrelated files**

```bash
git status --short
git diff --stat origin/feature/integrate...HEAD
git diff origin/feature/integrate...HEAD -- src/Aevatar.AI.Application.CodexExecution src/Aevatar.AI.Infrastructure.ChronoSandbox agents/Aevatar.GAgents.Channel.Identity agents/Aevatar.GAgents.Channel.Identity.Abstractions docs/canon/managed-codex-execution.md docs/operations/2026-07-16-managed-codex-exec-rollout.md
```

Expected: the scoped diff contains only managed Codex implementation, tests,
and documentation. Any branch commits that predate this feature are reviewed
separately. The pre-existing `.superpowers/`, NyxID chat proto, and NyxID chat
test remain untracked and unmodified.

- [ ] **Step 6: Record final verification**

If gate-driven fixes were needed, commit them with:

```bash
git diff --name-only -- src/Aevatar.AI.Application.CodexExecution src/Aevatar.AI.Infrastructure.ChronoSandbox agents/Aevatar.GAgents.Channel.Identity agents/Aevatar.GAgents.Channel.Identity.Abstractions test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests test/Aevatar.GAgents.ChannelRuntime.Tests test/Aevatar.Capabilities.Tests test/Aevatar.AI.Tests test/Aevatar.Architecture.Tests docs/canon/managed-codex-execution.md docs/operations/2026-07-16-managed-codex-exec-rollout.md | xargs git add --
git commit -m "Fix managed Codex verification findings"
```

If no fixes were needed, do not create an empty commit. Report the exact test,
guard, and build results before pushing.
