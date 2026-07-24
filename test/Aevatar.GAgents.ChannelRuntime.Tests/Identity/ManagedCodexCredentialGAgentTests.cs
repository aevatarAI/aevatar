using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class ManagedCodexCredentialGAgentTests : IAsyncLifetime
{
    private static readonly DateTimeOffset ExpiresAt =
        DateTimeOffset.Parse("2026-08-21T00:00:00Z");

    private ManagedCodexCredentialGAgent _agent = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, IdentityGAgentTestHarness.InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler,
            IdentityGAgentTestHarness.NoopCallbackScheduler>();
        _services = services.BuildServiceProvider();

        _agent = new ManagedCodexCredentialGAgent
        {
            Services = _services,
            EventSourcingBehaviorFactory =
                _services.GetRequiredService<IEventSourcingBehaviorFactory<ManagedCodexCredentialState>>(),
        };
        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _services.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void ActorIdentity_UsesTheCompleteNyxIdAuthority()
    {
        var first = Subject("tenant-a", "user-a");
        var second = Subject("tenant-b", "user-a");
        var third = Subject("tenant-a", "user-b");

        ManagedCodexCredentialActorIdentity.From(first).Should().NotBe(
            ManagedCodexCredentialActorIdentity.From(second));
        ManagedCodexCredentialActorIdentity.From(first).Should().NotBe(
            ManagedCodexCredentialActorIdentity.From(third));
    }

    [Fact]
    public async Task HandleProvisioned_PersistsOnlyTheTypedNonSecretDescriptor()
    {
        var descriptor = Descriptor("key-1", "sec-1", version: 1);

        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = descriptor,
        });

        _agent.State.Credential.Should().BeEquivalentTo(descriptor);
        _agent.State.Credential.Status.Should().Be(ManagedCodexCredentialStatus.Active);
        _agent.State.PendingRevocations.Should().BeEmpty();
        _agent.State.ToString().Should().NotContain("raw-agent-key");
    }

    [Fact]
    public async Task HandleProvisioned_WithDuplicateCommand_CommitsReadinessConfirmed()
    {
        var descriptor = Descriptor("key-1", "sec-1", version: 1);
        var command = new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = descriptor,
        };

        await _agent.HandleProvisioned(command);
        await _agent.HandleProvisioned(command);

        var readiness = await ReadLastReadinessConfirmedAsync();
        readiness.ApiKeyId.Should().Be("key-1");
    }

    [Fact]
    public async Task HandleProvisioned_WithMismatchedVaultAuthority_RejectsTheDescriptor()
    {
        var descriptor = Descriptor("key-1", "sec-1", version: 1);
        descriptor.SecretReference.Purpose = "wrong-purpose";

        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = descriptor,
        });

        _agent.State.Credential.Should().BeNull();
    }

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

    [Fact]
    public async Task HandlePolicyReconciled_WithDifferentApiKeyId_DoesNotReplaceCurrentKey()
    {
        var current = Descriptor("key-current", "sec-current", 1);
        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = current,
        });
        var incoming = current.Clone();
        incoming.ApiKeyId = "key-incoming";

        await _agent.HandlePolicyReconciled(
            new CommitManagedCodexCredentialPolicyReconciledCommand
            {
                ExpectedApiKeyId = "key-current",
                Credential = incoming,
            });

        _agent.State.Credential.ApiKeyId.Should().Be("key-current");
        _agent.State.Credential.SecretReference.Ref.Should().Be("sec-current");
        _agent.State.PendingRevocations.Should().ContainSingle();
        _agent.State.PendingRevocations[0].ApiKeyId.Should().Be("key-incoming");
        _agent.State.PendingRevocations[0].VaultPending.Should().BeFalse();
    }

    [Fact]
    public async Task HandlePolicyReconciled_WithStaleExpectedApiKeyId_DoesNotQueueActiveKey()
    {
        var current = Descriptor("key-current", "sec-current", 1);
        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = current,
        });

        await _agent.HandlePolicyReconciled(
            new CommitManagedCodexCredentialPolicyReconciledCommand
            {
                ExpectedApiKeyId = "key-stale",
                Credential = current.Clone(),
            });

        _agent.State.Credential.ApiKeyId.Should().Be("key-current");
        _agent.State.PendingRevocations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlePolicyReconciled_WithDifferentVaultReference_DoesNotQueueActiveKey()
    {
        var current = Descriptor("key-current", "sec-current", 1);
        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = current,
        });
        var drifted = current.Clone();
        drifted.SecretReference.Ref = "sec-drifted";

        await _agent.HandlePolicyReconciled(
            new CommitManagedCodexCredentialPolicyReconciledCommand
            {
                ExpectedApiKeyId = "key-current",
                Credential = drifted,
            });

        _agent.State.Credential.ApiKeyId.Should().Be("key-current");
        _agent.State.Credential.SecretReference.Ref.Should().Be("sec-current");
        _agent.State.PendingRevocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("fingerprint")]
    [InlineData("created_at")]
    [InlineData("expires_at")]
    public async Task HandlePolicyReconciled_WithSameRefButDriftedStableReference_DoesNotChangeStateOrQueueCurrentKey(
        string drift)
    {
        var current = Descriptor("key-current", "sec-current", 1);
        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = current,
        });
        var drifted = current.Clone();
        drifted.ChronoLlmUserServiceId = "user-service-llm-reconciled";
        switch (drift)
        {
            case "version":
                drifted.SecretReference.Version++;
                break;
            case "fingerprint":
                drifted.SecretReference.Fingerprint = "drifted-fingerprint";
                break;
            case "created_at":
                drifted.SecretReference.CreatedAtUnixMs++;
                break;
            case "expires_at":
                var driftedExpiry = ExpiresAt.AddDays(1);
                drifted.SecretReference.ExpiresAtUnixMs = driftedExpiry.ToUnixTimeMilliseconds();
                drifted.ExpiresAt = Timestamp.FromDateTimeOffset(driftedExpiry);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }

        await _agent.HandlePolicyReconciled(
            new CommitManagedCodexCredentialPolicyReconciledCommand
            {
                ExpectedApiKeyId = "key-current",
                Credential = drifted,
            });

        _agent.State.Credential.Should().Be(current);
        _agent.State.PendingRevocations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlePolicyReconciled_WithDuplicateCommand_CommitsReadinessConfirmed()
    {
        var current = Descriptor("key-current", "sec-current", 1);
        current.ChronoLlmUserServiceId = "user-service-llm-old";
        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = current,
        });
        var reconciled = current.Clone();
        reconciled.ChronoLlmUserServiceId = "user-service-llm";
        var command = new CommitManagedCodexCredentialPolicyReconciledCommand
        {
            ExpectedApiKeyId = "key-current",
            Credential = reconciled,
        };

        await _agent.HandlePolicyReconciled(command);
        await _agent.HandlePolicyReconciled(command);

        var readiness = await ReadLastReadinessConfirmedAsync();
        readiness.ApiKeyId.Should().Be("key-current");
    }

    [Fact]
    public async Task HandleRotated_WithStaleExpectedKey_QueuesIncomingKeyForCleanup()
    {
        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = Descriptor("key-current", "sec-1", version: 1),
        });

        await _agent.HandleRotated(new CommitManagedCodexCredentialRotatedCommand
        {
            ExpectedPreviousApiKeyId = "key-stale",
            Credential = Descriptor("key-unadopted", "sec-unadopted", version: 1),
        });

        _agent.State.Credential.ApiKeyId.Should().Be("key-current");
        _agent.State.PendingRevocations.Should().ContainSingle();
        _agent.State.PendingRevocations[0].ApiKeyId.Should().Be("key-unadopted");
        _agent.State.PendingRevocations[0].NyxIdPending.Should().BeTrue();
        _agent.State.PendingRevocations[0].VaultPending.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRotated_WithDuplicateDistinctReferenceCommand_CommitsReadinessConfirmed()
    {
        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = Descriptor("key-current", "sec-current", version: 1),
        });
        var rotated = Descriptor("key-rotated", "sec-rotated", version: 1);
        var command = new CommitManagedCodexCredentialRotatedCommand
        {
            ExpectedPreviousApiKeyId = "key-current",
            Credential = rotated,
        };

        await _agent.HandleRotated(command);
        await _agent.HandleRotated(command);

        _agent.State.Credential.Should().BeEquivalentTo(rotated);
        _agent.State.PendingRevocations.Should().BeEmpty();
        var readiness = await ReadLastReadinessConfirmedAsync();
        readiness.ApiKeyId.Should().Be("key-rotated");
    }

    [Fact]
    public async Task HandleRevoked_RetainsReferenceAndPersistsIndependentCleanupTracks()
    {
        await _agent.HandleProvisioned(new CommitManagedCodexCredentialProvisionedCommand
        {
            Credential = Descriptor("key-1", "sec-1", version: 1),
        });

        await _agent.HandleRevoked(new CommitManagedCodexCredentialRevokedCommand
        {
            Owner = Subject("tenant-a", "user-a"),
            ExpectedApiKeyId = "key-1",
            RevokedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-21T12:00:00Z")),
            Cleanup = new ManagedCodexCredentialCleanup
            {
                ApiKeyId = "key-1",
                SecretRef = "sec-1",
                NyxIdPending = true,
                VaultPending = false,
            },
        });

        _agent.State.Credential.Status.Should().Be(ManagedCodexCredentialStatus.Revoked);
        _agent.State.Credential.SecretReference.Ref.Should().Be("sec-1");
        _agent.State.PendingRevocations.Should().ContainSingle();
        _agent.State.PendingRevocations[0].ApiKeyId.Should().Be("key-1");
    }

    private async Task<ManagedCodexCredentialReadinessConfirmedEvent> ReadLastReadinessConfirmedAsync()
    {
        var store = _services.GetRequiredService<IEventStore>();
        var events = await store.GetEventsAsync(_agent.Id);
        events.Should().NotBeEmpty();
        events[^1].EventData.Is(ManagedCodexCredentialReadinessConfirmedEvent.Descriptor)
            .Should().BeTrue();
        return events[^1].EventData.Unpack<ManagedCodexCredentialReadinessConfirmedEvent>();
    }

    private static ManagedCodexCredentialDescriptor Descriptor(
        string apiKeyId,
        string secretRef,
        long version) =>
        new()
        {
            Owner = Subject("tenant-a", "user-a"),
            ApiKeyId = apiKeyId,
            SecretReference = new SecretReference
            {
                Ref = secretRef,
                Purpose = "managed.codex-invocation-agent-key",
                Fingerprint = "fingerprint",
                Version = version,
                OwnerScopeKey = "managed-codex-credential:nyxid:tenant-a:user-a",
                CreatedAtUnixMs = 1,
                ExpiresAtUnixMs = ExpiresAt.ToUnixTimeMilliseconds(),
            },
            ChronoSandboxUserServiceId = "user-service-sandbox",
            ChronoLlmUserServiceId = "user-service-llm",
            ChronoSandboxServiceSlug = "chrono-sandbox",
            ExpiresAt = Timestamp.FromDateTimeOffset(ExpiresAt),
            Status = ManagedCodexCredentialStatus.Active,
        };

    private static ExternalSubjectRef Subject(string tenant, string userId) =>
        new()
        {
            Platform = "nyxid",
            Tenant = tenant,
            ExternalUserId = userId,
        };
}
