using System.Text;
using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogGAgentTests : IAsyncLifetime
{
    private InMemoryEventStore _store = null!;
    private UserAgentCatalogGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;
    private IScheduledAgentCredentialRevocationExecutor _credentialRevocationExecutor = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        _store = new InMemoryEventStore();
        services.AddSingleton<IEventStore>(_store);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));

        _serviceProvider = services.BuildServiceProvider();

        _credentialRevocationExecutor = Substitute.For<IScheduledAgentCredentialRevocationExecutor>();
        _agent = new UserAgentCatalogGAgent(_credentialRevocationExecutor)
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<UserAgentCatalogState>>(),
        };

        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ActivateAsync_WithLegacyCredentialRevocationsSnapshot_CommitsTypedMigrationBeforeRetry()
    {
        var store = new InMemoryEventStore();
        var snapshotStore = new InMemorySnapshotStore<UserAgentCatalogState>();
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(store);
        services.AddSingleton<IEventSourcingSnapshotStore<UserAgentCatalogState>>(snapshotStore);
        services.AddSingleton(new EventSourcingRuntimeOptions
        {
            EnableSnapshots = true,
        });
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        using var serviceProvider = services.BuildServiceProvider();
        var executor = Substitute.For<IScheduledAgentCredentialRevocationExecutor>();
        var agent = new UserAgentCatalogGAgent(executor)
        {
            Services = serviceProvider,
            EventSourcingBehaviorFactory =
                serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<UserAgentCatalogState>>(),
        };
        SetId(agent, UserAgentCatalogGAgent.WellKnownId);

        await store.AppendAsync(
            agent.Id,
            [
                new StateEvent
                {
                    EventId = "evt-before-legacy-snapshot",
                    Version = 1,
                    EventData = Any.Pack(new Empty()),
                },
            ],
            expectedVersion: 0);
        var legacyAttemptedAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero));
        var legacyState = new UserAgentCatalogState
        {
            PendingApiKeyRevocations =
            {
                new UserAgentApiKeyRevocation
                {
                    AgentId = "agent-with-reference",
                    ApiKeyId = "key-with-reference",
                    NyxApiKeyReference = CompleteReference("sec-with-reference", "key-with-reference"),
                    OwnerScope = OwnerScope.ForNyxIdNative("user-a"),
                    AttemptCount = 2,
                    LastAttemptAt = legacyAttemptedAt,
                    LastHttpStatus = 503,
                    LastError = "legacy nyx failure",
                    FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
                },
                new UserAgentApiKeyRevocation
                {
                    AgentId = "agent-without-reference",
                    ApiKeyId = "key-without-reference",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-b"),
                    AttemptCount = 1,
                    LastHttpStatus = 401,
                    LastError = "legacy unauthorized",
                    FailureKind = UserAgentApiKeyRevocationFailureKind.Unauthorized,
                },
            },
        };
        var serializedLegacyState = UserAgentCatalogState.Parser.ParseFrom(legacyState.ToByteArray());
        await snapshotStore.SaveAsync(
            agent.Id,
            new EventSourcingSnapshot<UserAgentCatalogState>(serializedLegacyState, 1));

        await agent.ActivateAsync();

        agent.State.PendingApiKeyRevocations.Should().HaveCount(2);
        var executable = agent.State.PendingApiKeyRevocations.Single(revocation =>
            revocation.AgentId == "agent-with-reference");
        executable.SecretSubjectId.Should().Be("key-with-reference");
        executable.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        executable.NyxIdTrack.AttemptCount.Should().Be(2);
        executable.NyxIdTrack.LastAttemptAt.Should().BeEquivalentTo(legacyAttemptedAt);
        executable.NyxIdTrack.LastHttpStatus.Should().Be(503);
        executable.NyxIdTrack.LastError.Should().Be("legacy nyx failure");
        executable.NyxIdTrack.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        executable.VaultTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        executable.VaultRevocationDescriptor.ReferenceAvailability.Should().Be(
            ScheduledCredentialVaultReferenceAvailability.Confirmed);

        var blocked = agent.State.PendingApiKeyRevocations.Single(revocation =>
            revocation.AgentId == "agent-without-reference");
        blocked.SecretSubjectId.Should().Be("key-without-reference");
        blocked.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        blocked.NyxIdTrack.AttemptCount.Should().Be(1);
        blocked.NyxIdTrack.LastHttpStatus.Should().Be(401);
        blocked.NyxIdTrack.LastError.Should().Be("legacy unauthorized");
        blocked.NyxIdTrack.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Unauthorized);
        blocked.VaultTrack.Status.Should().Be(
            ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef);

        var events = await store.GetEventsAsync(agent.Id);
        events.Should().HaveCount(2);
        var migration = events[1].EventData.Unpack<UserAgentCatalogCredentialRevocationsMigratedEvent>();
        migration.Revocations.Should().HaveCount(2);
        await executor.DidNotReceive().ExecutePendingAsync(
            Arg.Any<string>(),
            Arg.Any<UserAgentApiKeyRevocation>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleUpsertAsync_WithRawNyxApiKeyOnly_DoesNotPersistRawKey()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-raw",
            ConversationId = "oc_chat_raw",
            NyxApiKey = "raw-command-secret",
        });

        var entry = _agent.State.Entries.Should().ContainSingle().Subject;
#pragma warning disable CS0612 // asserting deprecated field stays empty on new writes
        entry.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
        entry.NyxApiKeyReference.Should().BeNull();

        var persisted = await _store.GetEventsAsync(_agent.Id);
        var committed = persisted.Should().ContainSingle().Subject.EventData.Unpack<UserAgentCatalogUpsertedEvent>();
#pragma warning disable CS0612 // asserting deprecated field stays empty on new writes
        committed.Entry.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
        committed.Entry.NyxApiKeyReference.Should().BeNull();
    }

    [Fact]
    public async Task HandleUpsertAsync_WithReferenceAndRawNyxApiKey_PersistsReferenceOnly()
    {
        var reference = new SecretReference
        {
            Ref = "sec-scheduled-1",
            Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
            OwnerScopeKey = "scope-key-1",
        };

        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-reference",
            ConversationId = "oc_chat_reference",
            NyxApiKey = "raw-command-secret",
            NyxApiKeyReference = reference,
        });

        var entry = _agent.State.Entries.Should().ContainSingle().Subject;
#pragma warning disable CS0612 // asserting deprecated field stays empty on new writes
        entry.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
        entry.NyxApiKeyReference.Should().NotBeNull();
        entry.NyxApiKeyReference!.Ref.Should().Be("sec-scheduled-1");

        var persisted = await _store.GetEventsAsync(_agent.Id);
        var committed = persisted.Should().ContainSingle().Subject.EventData.Unpack<UserAgentCatalogUpsertedEvent>();
#pragma warning disable CS0612 // asserting deprecated field stays empty on new writes
        committed.Entry.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
        committed.Entry.NyxApiKeyReference.Should().NotBeNull();
        committed.Entry.NyxApiKeyReference!.Ref.Should().Be("sec-scheduled-1");
    }

    [Fact]
    public async Task HandleTombstoneAsync_RecordsTombstoneStateVersion()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-a",
            Platform = "lark",
            ConversationId = "conv-a",
        });

        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-a",
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].AgentId.Should().Be("agent-a");
        _agent.State.Entries[0].Tombstoned.Should().BeTrue();
        _agent.State.Entries[0].TombstoneStateVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleTombstoneAsync_WithApiKey_RecordsPendingRevocationBeforeTombstone()
    {
        var owner = OwnerScope.ForNyxIdNative("user-1");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-with-key",
            ConversationId = "conv-a",
            ApiKeyId = "key-1",
            NyxApiKeyReference = new SecretReference
            {
                Ref = "sec-1",
                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                OwnerScopeKey = "scope-key-1",
                Version = 1,
                Fingerprint = "sha256:test",
            },
            OwnerScope = owner,
        });

        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-with-key",
        });

        _agent.State.PendingApiKeyRevocations.Should().ContainSingle();
        var pending = _agent.State.PendingApiKeyRevocations[0];
        pending.AgentId.Should().Be("agent-with-key");
        pending.ApiKeyId.Should().Be("key-1");
        pending.NyxApiKeyReference.Ref.Should().Be("sec-1");
        pending.OwnerScope!.MatchesStrictly(owner).Should().BeTrue();
        pending.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        pending.VaultTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);

        var persisted = await _store.GetEventsAsync(_agent.Id);
        persisted.Select(static item => item.EventData.TypeUrl)
            .Should().ContainInOrder(
                "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogUpsertedEvent",
                "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogApiKeyRevocationRequestedEvent",
                "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogTombstonedEvent");
        await _credentialRevocationExecutor.Received(1).ExecutePendingAsync(
            string.Empty,
            Arg.Is<UserAgentApiKeyRevocation>(item =>
                item.AgentId == "agent-with-key" && item.ApiKeyId == "key-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleTombstoneAsync_WithExistingExactRevocation_ReusesFactAfterTombstoneCommit()
    {
        var owner = OwnerScope.ForNyxIdNative("user-exact");
        var reference = CompleteReference("sec-exact", "key-exact");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-exact",
            ApiKeyId = "key-exact",
            NyxApiKeyReference = reference,
            OwnerScope = owner,
        });
        await _agent.HandleRequestCredentialRevocationAsync(
            new UserAgentCatalogRequestCredentialRevocationCommand
            {
                Intent = RevocationIntent("agent-exact", "key-exact", owner, "sec-exact"),
            });
        var originalFact = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject.Clone();
        _credentialRevocationExecutor.ClearReceivedCalls();
        var tombstoneCommittedBeforeExecution = false;
        _credentialRevocationExecutor.ExecutePendingAsync(
                "bearer-exact",
                Arg.Any<UserAgentApiKeyRevocation>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var events = await _store.GetEventsAsync(_agent.Id);
                tombstoneCommittedBeforeExecution = events.Last().EventData
                    .Is(UserAgentCatalogTombstonedEvent.Descriptor);
            });

        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-exact",
            BearerToken = "bearer-exact",
        });

        tombstoneCommittedBeforeExecution.Should().BeTrue();
        _agent.State.Entries.Should().ContainSingle().Which.Tombstoned.Should().BeTrue();
        _agent.State.PendingApiKeyRevocations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(originalFact);
        var persisted = await _store.GetEventsAsync(_agent.Id);
        persisted.Count(item => item.EventData.Is(UserAgentCatalogApiKeyRevocationRequestedEvent.Descriptor))
            .Should().Be(1);
        persisted.Select(static item => item.EventData.TypeUrl).Should().ContainInOrder(
            "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogUpsertedEvent",
            "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogApiKeyRevocationRequestedEvent",
            "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogTombstonedEvent");
        await _credentialRevocationExecutor.Received(1).ExecutePendingAsync(
            "bearer-exact",
            Arg.Is<UserAgentApiKeyRevocation>(revocation =>
                revocation.AgentId == originalFact.AgentId &&
                revocation.ApiKeyId == originalFact.ApiKeyId &&
                revocation.NyxApiKeyReference.Ref == originalFact.NyxApiKeyReference.Ref &&
                revocation.RepairRequestedAtUnixMs == originalFact.RepairRequestedAtUnixMs),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleTombstoneAsync_WithAliasedRevocation_PreservesOriginalFactWithoutExecution()
    {
        var owner = OwnerScope.ForNyxIdNative("user-alias-tombstone");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-alias-tombstone",
            ApiKeyId = "key-alias-tombstone",
            NyxApiKeyReference = CompleteReference("sec-current", "key-alias-tombstone"),
            OwnerScope = owner,
        });
        await _agent.HandleRequestCredentialRevocationAsync(
            new UserAgentCatalogRequestCredentialRevocationCommand
            {
                Intent = RevocationIntent(
                    "agent-alias-tombstone",
                    "key-alias-tombstone",
                    owner,
                    "sec-prior"),
            });
        var originalFact = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject.Clone();
        _credentialRevocationExecutor.ClearReceivedCalls();

        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-alias-tombstone",
            BearerToken = "bearer-alias",
        });

        _agent.State.Entries.Should().ContainSingle().Which.Tombstoned.Should().BeTrue();
        _agent.State.PendingApiKeyRevocations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(originalFact);
        var persisted = await _store.GetEventsAsync(_agent.Id);
        persisted.Count(item => item.EventData.Is(UserAgentCatalogApiKeyRevocationRequestedEvent.Descriptor))
            .Should().Be(1);
        persisted.Select(static item => item.EventData.TypeUrl).Should().ContainInOrder(
            "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogUpsertedEvent",
            "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogApiKeyRevocationRequestedEvent",
            "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogTombstonedEvent");
        await _credentialRevocationExecutor.DidNotReceive().ExecutePendingAsync(
            Arg.Any<string>(),
            Arg.Any<UserAgentApiKeyRevocation>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRecordApiKeyRevocationAttemptAsync_ClearsOnlyAfterBothTracksComplete()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-retry",
            ConversationId = "conv-a",
            ApiKeyId = "key-retry",
            NyxApiKeyReference = new SecretReference
            {
                Ref = "sec-retry",
                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                OwnerScopeKey = "scope-retry",
                Version = 1,
                Fingerprint = "sha256:retry",
            },
            OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
        });
        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand { AgentId = "agent-retry" });

        await _agent.HandleRecordApiKeyRevocationAttemptAsync(
            new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
            {
                AgentId = "agent-retry",
                ApiKeyId = "key-retry",
                Completed = false,
                HttpStatus = 503,
                Error = "upstream unavailable",
                FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
                Track = UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId,
                SecretReferenceRef = "sec-retry",
            });

        var pending = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject;
        pending.AttemptCount.Should().Be(1);
        pending.LastHttpStatus.Should().Be(503);
        pending.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);

        await _agent.HandleRecordApiKeyRevocationAttemptAsync(
            new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
            {
                AgentId = "agent-retry",
                ApiKeyId = "key-retry",
                Completed = true,
                HttpStatus = 404,
                FailureKind = UserAgentApiKeyRevocationFailureKind.None,
                Track = UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId,
                SecretReferenceRef = "sec-retry",
            });

        _agent.State.PendingApiKeyRevocations.Should().ContainSingle()
            .Which.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Completed);

        await _agent.HandleRecordApiKeyRevocationAttemptAsync(
            new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
            {
                AgentId = "agent-retry",
                ApiKeyId = "key-retry",
                Completed = true,
                FailureKind = UserAgentApiKeyRevocationFailureKind.None,
                Track = UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault,
                SecretReferenceRef = "sec-retry",
            });

        _agent.State.PendingApiKeyRevocations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRequestCredentialRevocationAsync_ExactDuplicateDoesNotCreateAnotherFact()
    {
        var owner = OwnerScope.ForNyxIdNative("user-duplicate");
        var intent = RevocationIntent("agent-duplicate", "key-duplicate", owner, "sec-duplicate");

        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = intent,
        });
        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = intent.Clone(),
        });

        var pending = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject;
        pending.OwnerScope.MatchesStrictly(owner).Should().BeTrue();
        pending.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        pending.VaultTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        pending.NyxIdTrack.AttemptCount.Should().Be(0);
        pending.VaultTrack.AttemptCount.Should().Be(0);
        pending.RequestedAt.Should().NotBeNull();
        pending.RepairRequestedAtUnixMs.Should().Be(0);
        pending.RepairReason.Should().BeEmpty();
        pending.RequestedBySubjectId.Should().BeEmpty();
        var persisted = await _store.GetEventsAsync(_agent.Id);
        persisted.Count(item => item.EventData.Is(UserAgentCatalogApiKeyRevocationRequestedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task HandleRequestCredentialRevocationAsync_AliasConflictPreservesOriginalFact()
    {
        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent(
                "agent-alias",
                "key-alias",
                OwnerScope.ForNyxIdNative("user-alias"),
                "sec-original"),
        });

        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent(
                "agent-alias",
                "key-alias",
                OwnerScope.ForNyxIdNative("user-alias"),
                "sec-conflict"),
        });

        _agent.State.PendingApiKeyRevocations.Should().ContainSingle()
            .Which.NyxApiKeyReference.Ref.Should().Be("sec-original");
        var persisted = await _store.GetEventsAsync(_agent.Id);
        persisted.Count(item => item.EventData.Is(UserAgentCatalogApiKeyRevocationRequestedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task HandleRetryCredentialRevocationsAsync_SelectsCallerOwnedFactsInsideActor()
    {
        var owner = OwnerScope.ForChannel("user-a", "lark", "scope-1", "sender-a");
        var otherOwner = OwnerScope.ForChannel("user-b", "lark", "scope-1", "sender-b");
        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent("agent-a", "key-a", owner, "sec-a"),
        });
        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent("agent-b", "key-b", otherOwner, "sec-b"),
        });
        _credentialRevocationExecutor.ClearReceivedCalls();

        await _agent.HandleRetryCredentialRevocationsAsync(
            new UserAgentCatalogRetryCredentialRevocationsCommand
            {
                OwnerScope = owner,
                BearerToken = "bearer-a",
            });

        await _credentialRevocationExecutor.Received(1).ExecutePendingAsync(
            "bearer-a",
            Arg.Is<UserAgentApiKeyRevocation>(revocation =>
                revocation.AgentId == "agent-a" &&
                revocation.OwnerScope.MatchesStrictly(owner)),
            Arg.Any<CancellationToken>());
        await _credentialRevocationExecutor.DidNotReceive().ExecutePendingAsync(
            "bearer-a",
            Arg.Is<UserAgentApiKeyRevocation>(revocation => revocation.AgentId == "agent-b"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRecordApiKeyRevocationAttemptAsync_LatePreviousReferenceDoesNotAdvanceCurrentFact()
    {
        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent(
                "agent-rotated",
                "key-rotated",
                OwnerScope.ForNyxIdNative("user-rotated"),
                "sec-r1"),
        });
        await CompleteTrackAsync(
            "agent-rotated",
            "key-rotated",
            "sec-r1",
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId);
        await CompleteTrackAsync(
            "agent-rotated",
            "key-rotated",
            "sec-r1",
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault);
        _agent.State.PendingApiKeyRevocations.Should().BeEmpty();

        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent(
                "agent-rotated",
                "key-rotated",
                OwnerScope.ForNyxIdNative("user-rotated"),
                "sec-r2"),
        });
        await CompleteTrackAsync(
            "agent-rotated",
            "key-rotated",
            "sec-r1",
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId);

        var current = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject;
        current.NyxApiKeyReference.Ref.Should().Be("sec-r2");
        current.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        current.NyxIdTrack.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleRecordApiKeyRevocationAttemptAsync_DuplicateTerminalAttemptDoesNotIncrementTrack()
    {
        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent(
                "agent-terminal",
                "key-terminal",
                OwnerScope.ForNyxIdNative("user-terminal"),
                "sec-terminal"),
        });
        await CompleteTrackAsync(
            "agent-terminal",
            "key-terminal",
            "sec-terminal",
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId);

        await CompleteTrackAsync(
            "agent-terminal",
            "key-terminal",
            "sec-terminal",
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId);

        var pending = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject;
        pending.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Completed);
        pending.NyxIdTrack.AttemptCount.Should().Be(1);
        var persisted = await _store.GetEventsAsync(_agent.Id);
        persisted.Count(item => item.EventData.Is(UserAgentCatalogApiKeyRevocationAttemptRecordedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task HandleTombstoneAsync_WithoutSecretReference_BlocksVaultTrackWithoutCountingAttempts()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-blocked",
            ApiKeyId = "key-blocked",
            OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
        });
        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand { AgentId = "agent-blocked" });

        var pending = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject;
        pending.VaultTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef);

        await _agent.HandleRecordApiKeyRevocationAttemptAsync(new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
        {
            AgentId = "agent-blocked",
            ApiKeyId = "key-blocked",
            Completed = false,
            Track = UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault,
        });

        pending = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject;
        pending.VaultTrack.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleRequestCredentialRevocationAsync_NyxOnlyCompletionRemovesPendingFact()
    {
        await _agent.HandleRequestCredentialRevocationAsync(
            new UserAgentCatalogRequestCredentialRevocationCommand
            {
                Intent = new ScheduledAgentCredentialRevocationIntent
                {
                    AgentId = "agent-nyx-only",
                    ApiKeyId = "key-nyx-only",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-nyx-only"),
                    VaultRevocationDescriptor = new ScheduledCredentialVaultRevocationDescriptor
                    {
                        SubjectId = "key-nyx-only",
                        ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.NotApplicable,
                    },
                },
            });

        var pending = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject;
        pending.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        pending.VaultTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.NotApplicable);

        await CompleteTrackAsync(
            "agent-nyx-only",
            "key-nyx-only",
            string.Empty,
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId);

        _agent.State.PendingApiKeyRevocations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRepairCredentialRevocationAsync_BlockedReference_CommitsRepairAndMovesVaultToPending()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-repair",
            ApiKeyId = "key-repair",
            OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
        });
        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand { AgentId = "agent-repair" });
        var revocationRequestedAt = _agent.State.PendingApiKeyRevocations
            .Should().ContainSingle().Subject.RequestedAt.Clone();
        _agent.State.PendingApiKeyRevocations[0].RepairRequestedAtUnixMs.Should().Be(0);
        _credentialRevocationExecutor.ClearReceivedCalls();
        var committedBeforeExecution = false;
        _credentialRevocationExecutor.ExecutePendingAsync(
                string.Empty,
                Arg.Any<UserAgentApiKeyRevocation>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var events = await _store.GetEventsAsync(_agent.Id);
                committedBeforeExecution = events.Last().EventData
                    .Is(UserAgentCatalogCredentialRevocationRepairedEvent.Descriptor);
                var revocation = call.ArgAt<UserAgentApiKeyRevocation>(1);
                revocation.NyxApiKeyReference.Ref.Should().Be("secret-repair");
                revocation.VaultRevocationDescriptor.Ref.Should().Be("secret-repair");
                revocation.VaultRevocationDescriptor.ReferenceAvailability.Should()
                    .Be(ScheduledCredentialVaultReferenceAvailability.Confirmed);
                revocation.VaultTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
            });

        await _agent.HandleRepairCredentialRevocationAsync(new UserAgentCatalogRepairCredentialRevocationCommand
        {
            RequestId = "repair-request-1",
            AgentId = "agent-repair",
            ApiKeyId = "key-repair",
            SecretReference = CompleteReference("secret-repair", "key-repair"),
            SecretSubjectId = "key-repair",
            RepairReason = "restore exact durable reference",
            RequestedBySubjectId = "admin-1",
            RepairRequestedAtUnixMs = 1234,
        });

        var pending = _agent.State.PendingApiKeyRevocations.Should().ContainSingle().Subject;
        pending.VaultTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        pending.NyxApiKeyReference.Ref.Should().Be("secret-repair");
        pending.RepairReason.Should().Be("restore exact durable reference");
        pending.RequestedBySubjectId.Should().Be("admin-1");
        pending.RequestedAt.Should().BeEquivalentTo(revocationRequestedAt);
        pending.RepairRequestedAtUnixMs.Should().Be(1234);

        var persisted = await _store.GetEventsAsync(_agent.Id);
        var repaired = persisted.Last().EventData.Unpack<UserAgentCatalogCredentialRevocationRepairedEvent>();
        repaired.RequestId.Should().Be("repair-request-1");
        repaired.RepairRequestedAtUnixMs.Should().Be(1234);
        committedBeforeExecution.Should().BeTrue();
        await _credentialRevocationExecutor.Received(1).ExecutePendingAsync(
            string.Empty,
            Arg.Is<UserAgentApiKeyRevocation>(revocation =>
                revocation.NyxApiKeyReference.Ref == "secret-repair" &&
                revocation.VaultTrack.Status == ScheduledCredentialRevocationTrackStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleRepairCredentialRevocationAsync_NotBlocked_CommitsTypedRejection()
    {
        await _agent.HandleRepairCredentialRevocationAsync(new UserAgentCatalogRepairCredentialRevocationCommand
        {
            RequestId = "repair-request-2",
            AgentId = "missing-agent",
            ApiKeyId = "missing-key",
            SecretReference = CompleteReference("secret-missing", "missing-key"),
            SecretSubjectId = "missing-key",
            RepairReason = "restore exact durable reference",
            RequestedBySubjectId = "admin-1",
            RepairRequestedAtUnixMs = 1234,
        });

        var persisted = await _store.GetEventsAsync(_agent.Id);
        var rejected = persisted.Should().ContainSingle().Subject.EventData
            .Unpack<UserAgentCatalogCredentialRevocationRepairRejectedEvent>();
        rejected.RequestId.Should().Be("repair-request-2");
        rejected.Reason.Should().Be(UserAgentCatalogCredentialRevocationRepairRejectionReason.NotBlocked);
    }

    [Fact]
    public async Task HandleRepairCredentialRevocationAsync_InvalidRequest_CommitsTypedRejection()
    {
        await _agent.HandleRepairCredentialRevocationAsync(new UserAgentCatalogRepairCredentialRevocationCommand
        {
            RequestId = "repair-invalid",
            AgentId = "agent-invalid",
            ApiKeyId = "key-invalid",
            SecretReference = CompleteReference("secret-invalid", "key-invalid"),
            SecretSubjectId = "different-subject",
            RepairReason = "restore exact durable reference",
            RequestedBySubjectId = "admin-1",
            RepairRequestedAtUnixMs = 1234,
        });

        var persisted = await _store.GetEventsAsync(_agent.Id);
        var rejected = persisted.Should().ContainSingle().Subject.EventData
            .Unpack<UserAgentCatalogCredentialRevocationRepairRejectedEvent>();
        rejected.RequestId.Should().Be("repair-invalid");
        rejected.Reason.Should().Be(UserAgentCatalogCredentialRevocationRepairRejectionReason.InvalidRequest);
    }

    [Fact]
    public async Task HandleRepairCredentialRevocationAsync_ConflictingSecretAlias_CommitsTypedRejection()
    {
        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent(
                "agent-target",
                "key-target",
                OwnerScope.ForNyxIdNative("user-target")),
        });
        await CompleteTrackAsync(
            "agent-target",
            "key-target",
            string.Empty,
            UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId);
        await _agent.HandleRequestCredentialRevocationAsync(new UserAgentCatalogRequestCredentialRevocationCommand
        {
            Intent = RevocationIntent(
                "agent-existing",
                "key-existing",
                OwnerScope.ForNyxIdNative("user-existing"),
                "secret-shared"),
        });

        await _agent.HandleRepairCredentialRevocationAsync(new UserAgentCatalogRepairCredentialRevocationCommand
        {
            RequestId = "repair-conflict",
            AgentId = "agent-target",
            ApiKeyId = "key-target",
            SecretReference = CompleteReference("secret-shared", "key-target"),
            SecretSubjectId = "key-target",
            RepairReason = "restore exact durable reference",
            RequestedBySubjectId = "admin-1",
            RepairRequestedAtUnixMs = 1234,
        });

        var persisted = await _store.GetEventsAsync(_agent.Id);
        var rejected = persisted.Last().EventData
            .Unpack<UserAgentCatalogCredentialRevocationRepairRejectedEvent>();
        rejected.RequestId.Should().Be("repair-conflict");
        rejected.Reason.Should().Be(UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict);
    }

    [Fact]
    public async Task HandleTombstoneAsync_InvokesExecutorOnlyAfterIntentAndTombstoneAreCommitted()
    {
        var committedBeforeExecution = false;
        _credentialRevocationExecutor.ExecutePendingAsync(
                "bearer-token",
                Arg.Any<UserAgentApiKeyRevocation>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var events = await _store.GetEventsAsync(_agent.Id);
                committedBeforeExecution = events.TakeLast(2).Select(item => item.EventData.TypeUrl)
                    .SequenceEqual(
                    [
                        "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogApiKeyRevocationRequestedEvent",
                        "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogTombstonedEvent",
                    ]);
            });
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-ordered",
            ApiKeyId = "key-ordered",
            NyxApiKeyReference = CompleteReference("secret-ordered", "key-ordered"),
        });

        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-ordered",
            BearerToken = "bearer-token",
        });

        committedBeforeExecution.Should().BeTrue();
    }

    [Fact]
    public async Task HandleTombstoneAsync_DoesNotPersistBearerTokenInEventsOrState()
    {
        const string bearerToken = "sensitive-bearer-token-sentinel";
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-transient-bearer",
            ApiKeyId = "key-transient-bearer",
            NyxApiKeyReference = CompleteReference(
                "secret-transient-bearer",
                "key-transient-bearer"),
            OwnerScope = OwnerScope.ForNyxIdNative("user-transient-bearer"),
        });

        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-transient-bearer",
            BearerToken = bearerToken,
        });

        await _credentialRevocationExecutor.Received(1).ExecutePendingAsync(
            bearerToken,
            Arg.Any<UserAgentApiKeyRevocation>(),
            Arg.Any<CancellationToken>());
        Encoding.UTF8.GetString(_agent.State.ToByteArray()).Should().NotContain(bearerToken);
        var persisted = await _store.GetEventsAsync(_agent.Id);
        persisted.Should().OnlyContain(stateEvent =>
            !Encoding.UTF8.GetString(stateEvent.ToByteArray()).Contains(
                bearerToken,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleRecordApiKeyRevocationAttemptAsync_IgnoresFourthFailedAttempt()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-retry-limit",
            ConversationId = "conv-a",
            ApiKeyId = "key-retry-limit",
            OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
        });
        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand { AgentId = "agent-retry-limit" });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _agent.HandleRecordApiKeyRevocationAttemptAsync(BuildFailedRevocationAttempt());
        }

        var eventsBeforeFourthAttempt = await _store.GetEventsAsync(_agent.Id);
        eventsBeforeFourthAttempt.Count(static evt =>
                evt.EventData.Is(UserAgentCatalogApiKeyRevocationAttemptRecordedEvent.Descriptor))
            .Should().Be(3);
        _agent.State.PendingApiKeyRevocations.Should().ContainSingle()
            .Which.AttemptCount.Should().Be(3);

        await _agent.HandleRecordApiKeyRevocationAttemptAsync(BuildFailedRevocationAttempt());

        var eventsAfterFourthAttempt = await _store.GetEventsAsync(_agent.Id);
        eventsAfterFourthAttempt.Count(static evt =>
                evt.EventData.Is(UserAgentCatalogApiKeyRevocationAttemptRecordedEvent.Descriptor))
            .Should().Be(3);
        _agent.State.PendingApiKeyRevocations.Should().ContainSingle()
            .Which.AttemptCount.Should().Be(3);
    }

    [Fact]
    public async Task HandleUpsertAsync_CopiesOwnerScopeFromCommand()
    {
        // Issue #466 regression: HandleUpsertAsync must copy command.OwnerScope onto the
        // committed entry. Earlier the entry was built without it, every catalog row
        // landed with OwnerScope=null, and the caller-scoped query port returned an
        // empty list for the lark surface (which can't lazy-backfill from legacy fields
        // because legacy data lacked sender_id).
        var scope = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "alice-agent",
            ConversationId = "oc_chat_alice",
            OwnerScope = scope,
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].OwnerScope.Should().NotBeNull();
        _agent.State.Entries[0].OwnerScope!.MatchesStrictly(scope).Should().BeTrue();
#pragma warning disable CS0612 // deprecated ownership fields should not be re-emitted with owner_scope
        _agent.State.Entries[0].Platform.Should().BeEmpty();
        _agent.State.Entries[0].OwnerNyxUserId.Should().BeEmpty();
#pragma warning restore CS0612
    }

    [Fact]
    public async Task HandleUpsertAsync_RejectsCrossOwnerOverwrite()
    {
        var firstOwner = OwnerScope.ForNyxIdNative("user-1");
        var secondOwner = OwnerScope.ForNyxIdNative("user-2");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "approvals",
            ConversationId = "first-conversation",
            OwnerScope = firstOwner,
        });

        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "approvals",
            ConversationId = "second-conversation",
            OwnerScope = secondOwner,
        });

        _agent.State.Entries.Should().ContainSingle();
        var entry = _agent.State.Entries[0];
        entry.ConversationId.Should().Be("first-conversation");
        entry.OwnerScope!.MatchesStrictly(firstOwner).Should().BeTrue();
    }

    [Fact]
    public async Task HandleUpsertAsync_WithoutOwnerScope_PersistsLegacyOwnershipFields()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "legacy-agent",
            ConversationId = "oc_chat_legacy",
#pragma warning disable CS0612 // legacy command shape remains readable/writable when owner_scope is absent
            Platform = "nyxid",
            OwnerNyxUserId = "legacy-user",
#pragma warning restore CS0612
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].OwnerScope.Should().BeNull();
#pragma warning disable CS0612
        _agent.State.Entries[0].Platform.Should().Be("nyxid");
        _agent.State.Entries[0].OwnerNyxUserId.Should().Be("legacy-user");
#pragma warning restore CS0612
    }

    [Fact]
    public async Task HandleUpsertAsync_PartialUpsertWithoutOwnerScope_PreservesExisting()
    {
        // Partial membership updates can arrive without recomputing OwnerScope. The actor
        // must inherit the existing scope rather than dropping it.
        var scope = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "alice-agent",
            ConversationId = "oc_chat_alice",
            OwnerScope = scope,
        });

        // Second membership upsert without OwnerScope.
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "alice-agent",
            ScheduleCron = "0 9 * * *",
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].OwnerScope!.MatchesStrictly(scope).Should().BeTrue(
            "an upsert without OwnerScope on an existing entry inherits the existing scope");
    }

    [Fact]
    public async Task HandleUpsertAsync_PartialChannelAddressUpsert_PreservesExistingAddressFields()
    {
        var scope = OwnerScope.ForNyxIdNative("user-1");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-address",
            ConversationId = "oc_chat_original",
            TargetPlatform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                "lark",
                "api-lark-bot",
                "oc_chat_original",
                "oc_chat_original",
                "chat_id",
                "ou_user_original",
                "open_id"),
            OwnerScope = scope,
        });

        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-address",
            ConversationId = "oc_chat_new",
            NyxProviderSlug = "api-lark-bot-2",
            ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                "lark",
                "api-lark-bot-2",
                "oc_chat_new",
                "oc_chat_new",
                string.Empty,
                null,
                null),
            OwnerScope = scope,
        });

        var entry = _agent.State.Entries.Should().ContainSingle().Subject;
        entry.ChannelAddress.Platform.Should().Be("lark");
        entry.ChannelAddress.ProviderSlug.Should().Be("api-lark-bot-2");
        entry.ChannelAddress.ConversationId.Should().Be("oc_chat_new");
        entry.ChannelAddress.Primary.AddressId.Should().Be("oc_chat_new");
        entry.ChannelAddress.Primary.AddressType.Should().Be("chat_id");
        entry.ChannelAddress.Fallback.Should().NotBeNull();
        entry.ChannelAddress.Fallback!.AddressId.Should().Be("ou_user_original");
        entry.ChannelAddress.Fallback.AddressType.Should().Be("open_id");
    }

    [Fact]
    public async Task HandleUpsertAsync_PartialUpsertWithDefaultOutputFormat_PreservesExistingFormat()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "format-agent",
            ConversationId = "oc_chat_1",
            OutputFormat = ScheduledAgentOutputFormat.FeishuDoc,
        });

        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "format-agent",
            ScheduleCron = "0 9 * * *",
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].OutputFormat.Should().Be(ScheduledAgentOutputFormat.FeishuDoc);
    }

    [Fact]
    public async Task HandleShareAsync_OwnerChannelScope_AddsSingularGrant()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "alice-agent",
            ConversationId = "oc_chat_alice",
            OwnerScope = owner,
        });

        await _agent.HandleShareAsync(new UserAgentCatalogShareCommand
        {
            AgentId = "alice-agent",
            OwnerScope = owner,
            AllowTrigger = true,
        });

        var grant = _agent.State.Entries.Should().ContainSingle().Subject.SharingGrant;
        grant.Should().NotBeNull();
        grant!.SharedWithRegistrationScope.Should().Be("bot-1");
        grant.AllowTrigger.Should().BeTrue();
        grant.GrantedBy.Should().Be("alice");
        grant.GrantedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleShareAsync_NonOwner_DoesNotGrantAccess()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var otherSender = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "alice-agent",
            ConversationId = "oc_chat_alice",
            OwnerScope = owner,
        });

        await _agent.HandleShareAsync(new UserAgentCatalogShareCommand
        {
            AgentId = "alice-agent",
            OwnerScope = otherSender,
            AllowTrigger = true,
        });

        _agent.State.Entries.Should().ContainSingle().Subject.SharingGrant.Should().BeNull();
    }

    [Fact]
    public async Task HandleShareAsync_NyxIdNativeOwnedEntry_DoesNotGrantAccess()
    {
        var owner = OwnerScope.ForNyxIdNative("user-A");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "native-agent",
            ConversationId = "oc_chat_native",
            OwnerScope = owner,
        });

        await _agent.HandleShareAsync(new UserAgentCatalogShareCommand
        {
            AgentId = "native-agent",
            OwnerScope = owner,
            AllowTrigger = true,
        });

        var entry = _agent.State.Entries.Should().ContainSingle().Subject;
        entry.OwnerScope!.MatchesStrictly(owner).Should().BeTrue();
        entry.SharingGrant.Should().BeNull();
    }

    [Fact]
    public async Task HandleShareAsync_ChannelOwnedEntryWithoutRegistrationScope_DoesNotGrantAccess()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", string.Empty, "alice");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "registration-less-agent",
            ConversationId = "oc_chat_registration_less",
            OwnerScope = owner,
        });

        await _agent.HandleShareAsync(new UserAgentCatalogShareCommand
        {
            AgentId = "registration-less-agent",
            OwnerScope = owner,
            AllowTrigger = true,
        });

        var entry = _agent.State.Entries.Should().ContainSingle().Subject;
        entry.OwnerScope!.MatchesStrictly(owner).Should().BeTrue();
        entry.SharingGrant.Should().BeNull();
    }

    [Fact]
    public async Task HandleUnshareAsync_Owner_RemovesGrant()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "alice-agent",
            ConversationId = "oc_chat_alice",
            OwnerScope = owner,
        });
        await _agent.HandleShareAsync(new UserAgentCatalogShareCommand
        {
            AgentId = "alice-agent",
            OwnerScope = owner,
            AllowTrigger = true,
        });

        await _agent.HandleUnshareAsync(new UserAgentCatalogUnshareCommand
        {
            AgentId = "alice-agent",
            OwnerScope = owner,
        });

        _agent.State.Entries.Should().ContainSingle().Subject.SharingGrant.Should().BeNull();
    }

    [Fact]
    public async Task HandleUnshareAsync_NonOwner_PreservesGrant()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var otherSender = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "alice-agent",
            ConversationId = "oc_chat_alice",
            OwnerScope = owner,
        });
        await _agent.HandleShareAsync(new UserAgentCatalogShareCommand
        {
            AgentId = "alice-agent",
            OwnerScope = owner,
            AllowTrigger = true,
        });

        await _agent.HandleUnshareAsync(new UserAgentCatalogUnshareCommand
        {
            AgentId = "alice-agent",
            OwnerScope = otherSender,
        });

        var grant = _agent.State.Entries.Should().ContainSingle().Subject.SharingGrant;
        grant.Should().NotBeNull();
        grant!.SharedWithRegistrationScope.Should().Be("bot-1");
        grant.AllowTrigger.Should().BeTrue();
    }

    [Fact]
    public async Task HandleCompactTombstonesAsync_RemovesOnlyWatermarkSafeEntries()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-a",
            Platform = "lark",
            ConversationId = "conv-a",
        });
        await _agent.HandleTombstoneAsync(new UserAgentCatalogTombstoneCommand
        {
            AgentId = "agent-a",
        });

        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-b",
            Platform = "telegram",
            ConversationId = "conv-b",
        });

        await _agent.HandleCompactTombstonesAsync(new UserAgentCatalogCompactTombstonesCommand
        {
            SafeStateVersion = 1,
        });
        _agent.State.Entries.Select(x => x.AgentId).Should().Contain("agent-a");
        _agent.State.Entries.Select(x => x.AgentId).Should().Contain("agent-b");

        await _agent.HandleCompactTombstonesAsync(new UserAgentCatalogCompactTombstonesCommand
        {
            SafeStateVersion = 2,
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].AgentId.Should().Be("agent-b");
        _agent.State.Entries[0].Tombstoned.Should().BeFalse();
    }

    private static UserAgentCatalogRecordApiKeyRevocationAttemptCommand BuildFailedRevocationAttempt() =>
        new()
        {
            AgentId = "agent-retry-limit",
            ApiKeyId = "key-retry-limit",
            Completed = false,
            HttpStatus = 503,
            Error = "upstream unavailable",
            FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
            Track = UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId,
        };

    private async Task CompleteTrackAsync(
        string agentId,
        string apiKeyId,
        string secretReferenceRef,
        UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track track) =>
        await _agent.HandleRecordApiKeyRevocationAttemptAsync(
            new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
            {
                AgentId = agentId,
                ApiKeyId = apiKeyId,
                Completed = true,
                FailureKind = UserAgentApiKeyRevocationFailureKind.None,
                Track = track,
                SecretReferenceRef = secretReferenceRef,
            });

    private static ScheduledAgentCredentialRevocationIntent RevocationIntent(
        string agentId,
        string apiKeyId,
        OwnerScope ownerScope,
        string? secretReferenceRef = null)
    {
        var intent = new ScheduledAgentCredentialRevocationIntent
        {
            AgentId = agentId,
            ApiKeyId = apiKeyId,
            OwnerScope = ownerScope.Clone(),
        };
        if (!string.IsNullOrWhiteSpace(secretReferenceRef))
        {
            intent.NyxApiKeyReference = CompleteReference(secretReferenceRef, apiKeyId);
            intent.VaultRevocationDescriptor = new ScheduledCredentialVaultRevocationDescriptor
            {
                Ref = secretReferenceRef,
                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                OwnerScopeKey = $"scheduled-agent:{apiKeyId}",
                SubjectId = apiKeyId,
                ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.Confirmed,
            };
        }

        return intent;
    }

    private static SecretReference CompleteReference(string reference, string subjectId) => new()
    {
        Ref = reference,
        Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
        OwnerScopeKey = $"scheduled-agent:{subjectId}",
        Version = 1,
        Fingerprint = "sha256:test",
    };

    private static void SetId(GAgentBase agent, string actorId)
    {
        var method = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(agent, [actorId]);
    }

    private sealed class InMemorySnapshotStore<TState> : IEventSourcingSnapshotStore<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly Dictionary<string, EventSourcingSnapshot<TState>> _snapshots =
            new(StringComparer.Ordinal);

        public Task<EventSourcingSnapshot<TState>?> LoadAsync(
            string agentId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _snapshots.TryGetValue(agentId, out var snapshot);
            return Task.FromResult(snapshot is null
                ? null
                : new EventSourcingSnapshot<TState>(snapshot.State.Clone(), snapshot.Version));
        }

        public Task SaveAsync(
            string agentId,
            EventSourcingSnapshot<TState> snapshot,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _snapshots[agentId] = new EventSourcingSnapshot<TState>(
                snapshot.State.Clone(),
                snapshot.Version);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");
            }

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}
