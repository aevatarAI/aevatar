using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf.WellKnownTypes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogGAgentTests : IAsyncLifetime
{
    private InMemoryEventStore _store = null!;
    private UserAgentCatalogGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;

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

        _agent = new UserAgentCatalogGAgent
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

        var persisted = await _store.GetEventsAsync(_agent.Id);
        persisted.Select(static item => item.EventData.TypeUrl)
            .Should().ContainInOrder(
                "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogUpsertedEvent",
                "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogApiKeyRevocationRequestedEvent",
                "type.googleapis.com/aevatar.gagents.scheduled.UserAgentCatalogTombstonedEvent");
    }

    [Fact]
    public async Task HandleRecordApiKeyRevocationAttemptAsync_FailureKeepsPendingAndCompletionClearsIt()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "agent-retry",
            ConversationId = "conv-a",
            ApiKeyId = "key-retry",
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
            });

        _agent.State.PendingApiKeyRevocations.Should().BeEmpty();
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
    public async Task HandleUpsertAsync_PartialUpsertWithDefaultOutputFormat_PreservesExistingFormat()
    {
        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "format-agent",
            ConversationId = "oc_chat_1",
            OutputFormat = SkillRunnerOutputFormat.FeishuDoc,
        });

        await _agent.HandleUpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = "format-agent",
            ScheduleCron = "0 9 * * *",
        });

        _agent.State.Entries.Should().ContainSingle();
        _agent.State.Entries[0].OutputFormat.Should().Be(SkillRunnerOutputFormat.FeishuDoc);
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
        };

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
