using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogProjectorTests
{
    private readonly RecordingWriteDispatcher _dispatcher = new();
    private readonly FixedProjectionClock _clock = new(new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero));
    private readonly UserAgentCatalogProjector _projector;
    private readonly UserAgentCatalogMaterializationContext _context;

    public UserAgentCatalogProjectorTests()
    {
        _projector = new UserAgentCatalogProjector(_dispatcher, _clock);
        _context = new UserAgentCatalogMaterializationContext
        {
            RootActorId = UserAgentCatalogGAgent.WellKnownId,
            ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
        };
    }

    [Fact]
    public async Task ProjectAsync_WithValidCommittedEvent_UpsertsDocument()
    {
        var createdAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 9, 30, 0, TimeSpan.Zero));
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-1",
                    Platform = "lark",
                    ConversationId = "oc_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    NyxApiKey = "nyx-key-1",
                    OwnerNyxUserId = "user-1",
                    AgentType = "skill_runner",
                    TemplateName = "summary",
                    ScopeId = "scope-1",
                    ApiKeyId = "key-1",
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                    CreatedAt = createdAt,
                    LarkReceiveId = "oc_dm_chat_1",
                    LarkReceiveIdType = "chat_id",
                    LarkReceiveIdFallback = "on_user_1",
                    LarkReceiveIdTypeFallback = "union_id",
                    OutputFormat = SkillRunnerOutputFormat.FeishuDoc,
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-1", 3, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle();
        var document = _dispatcher.Upserts[0];
        document.Id.Should().Be("agent-1");
        document.Platform.Should().Be("lark");
        document.ConversationId.Should().Be("oc_chat_1");
        document.NyxProviderSlug.Should().Be("api-lark-bot");
        document.OwnerNyxUserId.Should().Be("user-1");
        document.AgentType.Should().Be("skill_runner");
        document.TemplateName.Should().Be("summary");
        document.ScopeId.Should().Be("scope-1");
        document.ApiKeyId.Should().Be("key-1");
        document.ScheduleCron.Should().Be("0 9 * * *");
        document.ScheduleTimezone.Should().Be("UTC");
        document.StateVersion.Should().Be(3);
        document.LastEventId.Should().Be("evt-agent-1");
        document.ActorId.Should().Be("agent-registry-store");
        document.CreatedAt.Should().Be(createdAt.ToDateTimeOffset());
        document.UpdatedAt.Should().Be(_clock.UtcNow);
        // Typed Lark target round-trips through the projection so catalog-backed senders
        // read it via UserAgentCatalogQueryPort.ToEntry instead of falling back to
        // conversation_id prefix inference. The fallback pair (PR #412) MUST mirror
        // through the projection too — without it the runtime `230002 bot not in chat`
        // retry on outbound Lark card senders / SkillRunnerGAgent would never have a
        // fallback typed pair to retry against, even though the actor-side state captured
        // one at create time.
        document.LarkReceiveId.Should().Be("oc_dm_chat_1");
        document.LarkReceiveIdType.Should().Be("chat_id");
        document.LarkReceiveIdFallback.Should().Be("on_user_1");
        document.LarkReceiveIdTypeFallback.Should().Be("union_id");
        document.OutputFormat.Should().Be(SkillRunnerOutputFormat.FeishuDoc);
    }

    [Fact]
    public async Task ProjectAsync_WithOwnerScope_LeavesDeprecatedOwnershipFieldsEmpty()
    {
        var ownerScope = OwnerScope.ForNyxIdNative("user-1");
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-scoped",
                    ConversationId = "oc_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    AgentType = "skill_runner",
                    OwnerScope = ownerScope,
#pragma warning disable CS0612 // stale legacy values must not be copied when owner_scope exists
                    Platform = "nyxid",
                    OwnerNyxUserId = "user-1",
#pragma warning restore CS0612
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-scoped", 4, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle();
        var document = _dispatcher.Upserts[0];
        document.OwnerScope.Should().NotBeNull();
        document.OwnerScope!.MatchesStrictly(ownerScope).Should().BeTrue();
#pragma warning disable CS0612
        document.Platform.Should().BeEmpty();
        document.OwnerNyxUserId.Should().BeEmpty();
#pragma warning restore CS0612
    }

    [Fact]
    public async Task ProjectAsync_WithSharingGrant_MaterializesAudienceKeys()
    {
        var ownerScope = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "shared-agent",
                    ConversationId = "oc_chat_1",
                    AgentType = "skill_runner",
                    OwnerScope = ownerScope,
                    SharingGrant = new ScheduledAgentSharingGrant
                    {
                        SharedWithRegistrationScope = "bot-1",
                        AllowTrigger = true,
                        GrantedBy = "alice",
                        GrantedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 17, 10, 0, 0, TimeSpan.Zero)),
                    },
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-shared", 5, state), CancellationToken.None);

        var document = _dispatcher.Upserts.Should().ContainSingle().Subject;
        document.SharingGrant.Should().NotBeNull();
        document.SharingGrant!.SharedWithRegistrationScope.Should().Be("bot-1");
        document.SharingGrant.AllowTrigger.Should().BeTrue();
        document.VisibleSharingAudienceKey.Should().Be("lark:bot-1");
        document.TriggerSharingAudienceKey.Should().Be("lark:bot-1");
    }

    [Fact]
    public async Task ProjectAsync_WithViewOnlySharingGrant_DoesNotMaterializeTriggerAudience()
    {
        var ownerScope = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "view-only-agent",
                    OwnerScope = ownerScope,
                    SharingGrant = new ScheduledAgentSharingGrant
                    {
                        SharedWithRegistrationScope = "bot-1",
                        AllowTrigger = false,
                    },
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-view-only", 5, state), CancellationToken.None);

        var document = _dispatcher.Upserts.Should().ContainSingle().Subject;
        document.VisibleSharingAudienceKey.Should().Be("lark:bot-1");
        document.TriggerSharingAudienceKey.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithSkillRunnerCommittedState_DoesNotWriteCatalogDocument()
    {
        var state = new SkillRunnerState
        {
            TemplateName = "summary",
            ScopeId = "scope-1",
            ScheduleCron = "0 9 * * *",
            ScheduleTimezone = "UTC",
            Enabled = true,
            LastRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 8, 0, 0, TimeSpan.Zero)),
            NextRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero)),
            ErrorCount = 0,
        };

        await _projector.ProjectAsync(
            new UserAgentCatalogMaterializationContext
            {
                RootActorId = "runner-1",
                ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
            },
            BuildSkillRunnerCommittedEnvelope("runner-event-2", 2, state),
            CancellationToken.None);

        _dispatcher.Upserts.Should().BeEmpty("runner-owned execution state has a separate read model");
    }

    [Fact]
    public async Task SkillRunnerExecutionProjector_WithSkillRunnerFailedState_ProjectsErrorStatus()
    {
        var dispatcher = new RecordingExecutionWriteDispatcher();
        var projector = new SkillRunnerExecutionProjector(dispatcher, _clock);
        var state = new SkillRunnerState
        {
            Enabled = true,
            LastRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 8, 0, 0, TimeSpan.Zero)),
            ErrorCount = 2,
            LastError = "tool failed",
            LastSuccessfulDelivery = new DeliveryLedgerEntry
            {
                DeliveryKind = DeliveryKind.TextMessage,
                Status = DeliveryStatus.Succeeded,
                Target = new DeliveryTarget
                {
                    Channel = ChannelId.From("lark"),
                    ConversationKey = "oc_chat_1",
                },
                LarkMessageId = "om_success",
                RequestId = "request-success",
                ProducedAtVersion = 3,
            },
        };
        state.RecentDeliveries.Add(new DeliveryLedgerEntry
        {
            DeliveryKind = DeliveryKind.TextMessage,
            Status = DeliveryStatus.FailedPreSend,
            Target = new DeliveryTarget
            {
                Channel = ChannelId.From("lark"),
                ConversationKey = "oc_chat_1",
            },
            RequestId = "request-failed",
            ProducedAtVersion = 2,
        });
        state.RecentDeliveries.Add(state.LastSuccessfulDelivery.Clone());

        await projector.ProjectAsync(
            new UserAgentCatalogMaterializationContext
            {
                RootActorId = "runner-failed",
                ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
            },
            BuildSkillRunnerCommittedEnvelope("runner-event-4", 4, state),
            CancellationToken.None);

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Id.Should().Be("runner-failed");
        document.ActorId.Should().Be("runner-failed");
        document.Status.Should().Be(SkillRunnerDefaults.StatusError);
        document.LastError.Should().Be("tool failed");
        document.ErrorCount.Should().Be(2);
        document.StateVersion.Should().Be(4);
        document.LastEventId.Should().Be("runner-event-4");
        document.RecentDeliveries.Select(delivery => delivery.RequestId)
            .Should().Equal("request-failed", "request-success");
        document.LastSuccessfulDelivery.Should().NotBeNull();
        document.LastSuccessfulDelivery!.LarkMessageId.Should().Be("om_success");
    }

    [Fact]
    public void ToEntry_ShouldRoundTripTypedLarkReceiveTarget_FromDocumentToEntry()
    {
        // Outbound Lark senders consume UserAgentCatalogEntry via this conversion; dropping
        // the typed fields would silently regress workflow / social_media DM delivery back
        // to the prefix-inference path even after the projection captured them. The
        // fallback pair (PR #412) is part of the same contract — the catalog-backed
        // `230002 bot not in chat` retry depends on `LarkReceiveIdFallback` /
        // `LarkReceiveIdTypeFallback` surviving the document → entry mapping.
        var document = new UserAgentCatalogDocument
        {
            Id = "agent-1",
            Platform = "lark",
            ConversationId = "oc_dm_chat_1",
            LarkReceiveId = "oc_dm_chat_1",
            LarkReceiveIdType = "chat_id",
            LarkReceiveIdFallback = "on_user_1",
            LarkReceiveIdTypeFallback = "union_id",
            OutputFormat = SkillRunnerOutputFormat.Text,
        };

        var entry = UserAgentCatalogQueryPort.ToEntry(document);

        entry.LarkReceiveId.Should().Be("oc_dm_chat_1");
        entry.LarkReceiveIdType.Should().Be("chat_id");
        entry.LarkReceiveIdFallback.Should().Be("on_user_1");
        entry.LarkReceiveIdTypeFallback.Should().Be("union_id");
        entry.OutputFormat.Should().Be(SkillRunnerOutputFormat.Text);
    }

    [Fact]
    public async Task NyxCredentialProjector_WithReferenceCommittedEvent_UpsertsReferenceOnlyCredentialDocument()
    {
        var dispatcher = new RecordingCredentialWriteDispatcher();
        var projector = new UserAgentCatalogNyxCredentialProjector(dispatcher, _clock);
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-1",
                    ApiKeyId = "key-1",
                    NyxApiKeyReference = new SecretReference
                    {
                        Ref = "sec-1",
                        Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                        OwnerScopeKey = "scope-1",
                    },
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-cred", 4, state), CancellationToken.None);

        dispatcher.Upserts.Should().ContainSingle();
        var document = dispatcher.Upserts[0];
        document.Id.Should().Be("agent-1");
        document.NyxApiKey.Should().BeEmpty();
        document.ApiKeyId.Should().Be("key-1");
        document.NyxApiKeyReference.Ref.Should().Be("sec-1");
        document.NyxApiKeyReference.Purpose.Should().Be(CredentialSecretPurposes.ScheduledNyxApiKey);
        document.StateVersion.Should().Be(4);
        document.LastEventId.Should().Be("evt-agent-cred");
        document.ActorId.Should().Be("agent-registry-store");
        document.UpdatedAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task NyxCredentialProjector_WithRawOnlyCredential_DeletesDocument()
    {
        var dispatcher = new RecordingCredentialWriteDispatcher();
        var projector = new UserAgentCatalogNyxCredentialProjector(dispatcher, _clock);
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-legacy-raw",
                    ApiKeyId = "key-raw",
#pragma warning disable CS0612 // legacy raw state must not be projected into new writes
                    NyxApiKey = "legacy-raw-secret",
#pragma warning restore CS0612
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-raw-cred", 6, state), CancellationToken.None);

        dispatcher.Upserts.Should().BeEmpty();
        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-legacy-raw");
    }

    [Fact]
    public async Task NyxCredentialProjector_DeletesDocument_WhenCredentialMissing()
    {
        var dispatcher = new RecordingCredentialWriteDispatcher();
        var projector = new UserAgentCatalogNyxCredentialProjector(dispatcher, _clock);
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-public",
                    Platform = "lark",
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-public", 5, state), CancellationToken.None);

        dispatcher.Upserts.Should().BeEmpty();
        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-public");
    }

    [Fact]
    public async Task ApiKeyRevocationProjector_WithPendingRevocation_UpsertsDocument()
    {
        var dispatcher = new RecordingRevocationWriteDispatcher();
        var projector = new UserAgentApiKeyRevocationProjector(dispatcher, _clock);
        var owner = OwnerScope.ForNyxIdNative("user-1");
        var state = new UserAgentCatalogState
        {
            PendingApiKeyRevocations =
            {
                new UserAgentApiKeyRevocation
                {
                    AgentId = "agent-1",
                    ApiKeyId = "key-1",
                    OwnerScope = owner,
                    NyxApiKeyReference = new SecretReference
                    {
                        Ref = "sec-1",
                        Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                    },
                    RequestedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero)),
                    AttemptCount = 1,
                    LastHttpStatus = 503,
                    LastError = "upstream unavailable",
                    FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-revoke-pending", 7, state), CancellationToken.None);

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Id.Should().Be("agent-1");
        document.AgentId.Should().Be("agent-1");
        document.ApiKeyId.Should().Be("key-1");
        document.NyxApiKeyReference.Ref.Should().Be("sec-1");
        document.OwnerScope!.MatchesStrictly(owner).Should().BeTrue();
        document.AttemptCount.Should().Be(1);
        document.LastHttpStatus.Should().Be(503);
        document.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-revoke-pending");
    }

    [Fact]
    public async Task ApiKeyRevocationProjector_WithCompletedAttempt_DeletesDocument()
    {
        var dispatcher = new RecordingRevocationWriteDispatcher();
        var projector = new UserAgentApiKeyRevocationProjector(dispatcher, _clock);
        var state = new UserAgentCatalogState();
        var completed = new UserAgentCatalogApiKeyRevocationAttemptRecordedEvent
        {
            AgentId = "agent-1",
            ApiKeyId = "key-1",
            Completed = true,
            HttpStatus = 404,
            FailureKind = UserAgentApiKeyRevocationFailureKind.None,
            AttemptedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero)),
        };

        await projector.ProjectAsync(
            _context,
            BuildCommittedEnvelope("evt-revoke-complete", 8, state, Any.Pack(completed)),
            CancellationToken.None);

        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-1");
        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithTombstonedEntry_DeletesDocument()
    {
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-2",
                    Platform = "lark",
                    Tombstoned = true,
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-2", 4, state), CancellationToken.None);

        _dispatcher.Upserts.Should().BeEmpty();
        _dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-2");
    }

    [Fact]
    public async Task ProjectAsync_WithMixedLiveAndTombstonedEntries_DispatchesBothVerdicts()
    {
        // Verifies the watermark-coordination contract: live and tombstoned entries
        // in the same committed snapshot dispatch upserts + deletes in one pass so
        // the read model stays aligned with the authoritative state version.
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry { AgentId = "agent-live", Platform = "lark" },
                new UserAgentCatalogEntry { AgentId = "agent-dead", Platform = "lark", Tombstoned = true },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-mixed", 9, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle().Which.Id.Should().Be("agent-live");
        _dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-dead");
    }

    [Fact]
    public async Task ProjectAsync_SkipsBlankAgentId()
    {
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry { AgentId = "", Platform = "lark" },
                new UserAgentCatalogEntry { AgentId = "agent-3", Platform = "lark" },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-agent-3", 5, state), CancellationToken.None);

        _dispatcher.Upserts.Should().ContainSingle();
        _dispatcher.Upserts[0].Id.Should().Be("agent-3");
    }

    private static EventEnvelope BuildCommittedEnvelope(
        string eventId,
        long version,
        UserAgentCatalogState state,
        Any? eventData = null)
    {
        var occurredAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero));
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("user-agent-catalog-projector-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = occurredAt.Clone(),
                    EventData = eventData ?? Any.Pack(new Empty()),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private static EventEnvelope BuildSkillRunnerCommittedEnvelope(string eventId, long version, SkillRunnerState state)
    {
        var occurredAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 10, 0, 0, TimeSpan.Zero));
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("runner-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = occurredAt.Clone(),
                    EventData = Any.Pack(new Empty()),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher : IProjectionWriteDispatcher<UserAgentCatalogDocument>
    {
        public List<UserAgentCatalogDocument> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            UserAgentCatalogDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class RecordingCredentialWriteDispatcher : IProjectionWriteDispatcher<UserAgentCatalogNyxCredentialDocument>
    {
        public List<UserAgentCatalogNyxCredentialDocument> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            UserAgentCatalogNyxCredentialDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class RecordingRevocationWriteDispatcher : IProjectionWriteDispatcher<UserAgentApiKeyRevocationDocument>
    {
        public List<UserAgentApiKeyRevocationDocument> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            UserAgentApiKeyRevocationDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class RecordingExecutionWriteDispatcher : IProjectionWriteDispatcher<SkillRunnerExecutionDocument>
    {
        public List<SkillRunnerExecutionDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            SkillRunnerExecutionDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
