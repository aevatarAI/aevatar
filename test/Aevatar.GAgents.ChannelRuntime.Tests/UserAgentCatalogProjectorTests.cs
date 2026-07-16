using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
                    ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                        "lark",
                        "api-lark-bot",
                        "oc_chat_1",
                        "oc_dm_chat_1",
                        "chat_id",
                        "on_user_1",
                        "union_id"),
                    OutputFormat = ScheduledAgentOutputFormat.FeishuDoc,
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
        document.ChannelAddress.Should().NotBeNull();
        document.ChannelAddress!.Platform.Should().Be("lark");
        document.ChannelAddress.ProviderSlug.Should().Be("api-lark-bot");
        document.ChannelAddress.ConversationId.Should().Be("oc_chat_1");
        document.ChannelAddress.Primary.AddressId.Should().Be("oc_dm_chat_1");
        document.ChannelAddress.Primary.AddressType.Should().Be("chat_id");
        document.ChannelAddress.Fallback.Should().NotBeNull();
        document.ChannelAddress.Fallback!.AddressId.Should().Be("on_user_1");
        document.ChannelAddress.Fallback.AddressType.Should().Be("union_id");
        document.OutputFormat.Should().Be(ScheduledAgentOutputFormat.FeishuDoc);
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
    public async Task ProjectAsync_WithLegacyLarkAddressFields_MapsToChannelAddress()
    {
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-legacy-address",
                    ConversationId = "oc_dm_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    AgentType = "skill_runner",
                    TemplateName = "summary",
                    TargetPlatform = "lark",
#pragma warning disable CS0612 // legacy fields simulate state persisted before channel_address existed
                    LarkReceiveId = "oc_dm_chat_1",
                    LarkReceiveIdType = "chat_id",
                    LarkReceiveIdFallback = "on_user_1",
                    LarkReceiveIdTypeFallback = "union_id",
#pragma warning restore CS0612
                },
            },
        };

        await _projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-legacy-address", 6, state), CancellationToken.None);

        var document = _dispatcher.Upserts.Should().ContainSingle().Subject;
        document.ChannelAddress.Should().NotBeNull();
        document.ChannelAddress!.Platform.Should().Be("lark");
        document.ChannelAddress.ProviderSlug.Should().Be("api-lark-bot");
        document.ChannelAddress.ConversationId.Should().Be("oc_dm_chat_1");
        document.ChannelAddress.Primary.AddressId.Should().Be("oc_dm_chat_1");
        document.ChannelAddress.Primary.AddressType.Should().Be("chat_id");
        document.ChannelAddress.Fallback.Should().NotBeNull();
        document.ChannelAddress.Fallback!.AddressId.Should().Be("on_user_1");
        document.ChannelAddress.Fallback.AddressType.Should().Be("union_id");
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
    public void ToEntry_ShouldRoundTripChannelAddress_FromDocumentToEntry()
    {
        var document = new UserAgentCatalogDocument
        {
            Id = "agent-1",
            Platform = "lark",
            ConversationId = "oc_dm_chat_1",
            NyxProviderSlug = "api-lark-bot",
            ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                "lark",
                "api-lark-bot",
                "oc_dm_chat_1",
                "oc_dm_chat_1",
                "chat_id",
                "on_user_1",
                "union_id"),
            OutputFormat = ScheduledAgentOutputFormat.Text,
        };

        var entry = UserAgentCatalogQueryPort.ToEntry(document);

        entry.ChannelAddress.Platform.Should().Be("lark");
        entry.ChannelAddress.ProviderSlug.Should().Be("api-lark-bot");
        entry.ChannelAddress.ConversationId.Should().Be("oc_dm_chat_1");
        entry.ChannelAddress.Primary.AddressId.Should().Be("oc_dm_chat_1");
        entry.ChannelAddress.Primary.AddressType.Should().Be("chat_id");
        entry.ChannelAddress.Fallback.Should().NotBeNull();
        entry.ChannelAddress.Fallback!.AddressId.Should().Be("on_user_1");
        entry.ChannelAddress.Fallback.AddressType.Should().Be("union_id");
        entry.OutputFormat.Should().Be(ScheduledAgentOutputFormat.Text);
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
        var nyxAttemptedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 20, 10, 1, 0, TimeSpan.Zero));
        var vaultAttemptedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 20, 10, 2, 0, TimeSpan.Zero));
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
                        OwnerScopeKey = "owner-1",
                        Version = 1,
                        Fingerprint = "sha256:test",
                    },
                    RequestedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero)),
                    AttemptCount = 1,
                    LastHttpStatus = 503,
                    LastError = "upstream unavailable",
                    FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
                    NyxIdTrack = new ScheduledCredentialRevocationTrack
                    {
                        Status = ScheduledCredentialRevocationTrackStatus.Pending,
                        AttemptCount = 2,
                        LastAttemptAt = nyxAttemptedAt,
                        LastHttpStatus = 503,
                        LastError = "nyx unavailable",
                        FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
                    },
                    VaultTrack = new ScheduledCredentialRevocationTrack
                    {
                        Status = ScheduledCredentialRevocationTrackStatus.Pending,
                        AttemptCount = 1,
                        LastAttemptAt = vaultAttemptedAt,
                        LastError = "vault unavailable",
                        FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
                    },
                    VaultRevocationDescriptor = new ScheduledCredentialVaultRevocationDescriptor
                    {
                        Ref = "sec-1",
                        Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                        OwnerScopeKey = "owner-1",
                        SubjectId = "key-1",
                        ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.Confirmed,
                    },
                    SecretSubjectId = "key-1",
                    RepairReason = "restore exact reference",
                    RequestedBySubjectId = "admin-1",
                    RepairRequestedAtUnixMs = 1_750_412_800_000,
                },
            },
        };

        await projector.ProjectAsync(_context, BuildCommittedEnvelope("evt-revoke-pending", 7, state), CancellationToken.None);

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Id.Should().Be(ScheduledAgentCredentialRevocationDocumentIds.Build("agent-1", "key-1", "sec-1"));
        document.AgentId.Should().Be("agent-1");
        document.ApiKeyId.Should().Be("key-1");
        document.NyxApiKeyReference.Ref.Should().Be("sec-1");
        document.NyxApiKeyReference.Purpose.Should().Be(CredentialSecretPurposes.ScheduledNyxApiKey);
        document.NyxApiKeyReference.OwnerScopeKey.Should().Be("owner-1");
        document.NyxApiKeyReference.Version.Should().Be(1);
        document.NyxApiKeyReference.Fingerprint.Should().Be("sha256:test");
        document.OwnerScope!.MatchesStrictly(owner).Should().BeTrue();
        document.AttemptCount.Should().Be(1);
        document.LastHttpStatus.Should().Be(503);
        document.LastError.Should().Be("upstream unavailable");
        document.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        document.NyxIdTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        document.NyxIdTrack.AttemptCount.Should().Be(2);
        document.NyxIdTrack.LastAttemptAt.Should().BeEquivalentTo(nyxAttemptedAt);
        document.NyxIdTrack.LastHttpStatus.Should().Be(503);
        document.NyxIdTrack.LastError.Should().Be("nyx unavailable");
        document.NyxIdTrack.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        document.VaultTrack.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        document.VaultTrack.AttemptCount.Should().Be(1);
        document.VaultTrack.LastAttemptAt.Should().BeEquivalentTo(vaultAttemptedAt);
        document.VaultTrack.LastHttpStatus.Should().Be(0);
        document.VaultTrack.LastError.Should().Be("vault unavailable");
        document.VaultTrack.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        document.VaultRevocationDescriptor.Ref.Should().Be("sec-1");
        document.VaultRevocationDescriptor.Purpose.Should().Be(CredentialSecretPurposes.ScheduledNyxApiKey);
        document.VaultRevocationDescriptor.OwnerScopeKey.Should().Be("owner-1");
        document.VaultRevocationDescriptor.SubjectId.Should().Be("key-1");
        document.VaultRevocationDescriptor.ReferenceAvailability.Should()
            .Be(ScheduledCredentialVaultReferenceAvailability.Confirmed);
        document.SecretSubjectId.Should().Be("key-1");
        document.RepairReason.Should().Be("restore exact reference");
        document.RequestedBySubjectId.Should().Be("admin-1");
        document.RepairRequestedAtUnixMs.Should().Be(1_750_412_800_000);
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-revoke-pending");
        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-1");
    }

    [Fact]
    public async Task ApiKeyRevocationProjector_WithBlockedReference_UsesStableBlockedDocumentId()
    {
        var dispatcher = new RecordingRevocationWriteDispatcher();
        var projector = new UserAgentApiKeyRevocationProjector(dispatcher, _clock);
        var state = new UserAgentCatalogState
        {
            PendingApiKeyRevocations =
            {
                new UserAgentApiKeyRevocation
                {
                    AgentId = "agent-blocked",
                    ApiKeyId = "key-blocked",
                    SecretSubjectId = "key-blocked",
                    NyxIdTrack = new ScheduledCredentialRevocationTrack
                    {
                        Status = ScheduledCredentialRevocationTrackStatus.Completed,
                    },
                    VaultTrack = new ScheduledCredentialRevocationTrack
                    {
                        Status = ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef,
                    },
                },
            },
        };

        await projector.ProjectAsync(
            _context,
            BuildCommittedEnvelope("evt-revoke-blocked", 8, state),
            CancellationToken.None);

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Id.Should().Be(
            ScheduledAgentCredentialRevocationDocumentIds.BuildBlocked("agent-blocked", "key-blocked"));
        document.NyxApiKeyReference.Should().BeNull();
        document.VaultTrack.Status.Should().Be(
            ScheduledCredentialRevocationTrackStatus.BlockedMissingSecretRef);
        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-blocked");
    }

    [Fact]
    public async Task ApiKeyRevocationProjector_WithRepair_DeletesBlockedKeyAndUpsertsExactKey()
    {
        var dispatcher = new RecordingRevocationWriteDispatcher();
        var projector = new UserAgentApiKeyRevocationProjector(dispatcher, _clock);
        var repairedReference = new SecretReference
        {
            Ref = "sec-repaired",
            Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
            OwnerScopeKey = "owner-repaired",
            Version = 1,
            Fingerprint = "sha256:repaired",
        };
        var state = new UserAgentCatalogState
        {
            PendingApiKeyRevocations =
            {
                new UserAgentApiKeyRevocation
                {
                    AgentId = "agent-repaired",
                    ApiKeyId = "key-repaired",
                    SecretSubjectId = "key-repaired",
                    NyxApiKeyReference = repairedReference,
                    NyxIdTrack = new ScheduledCredentialRevocationTrack
                    {
                        Status = ScheduledCredentialRevocationTrackStatus.Completed,
                    },
                    VaultTrack = new ScheduledCredentialRevocationTrack
                    {
                        Status = ScheduledCredentialRevocationTrackStatus.Pending,
                    },
                },
            },
        };
        var repaired = new UserAgentCatalogCredentialRevocationRepairedEvent
        {
            AgentId = "agent-repaired",
            ApiKeyId = "key-repaired",
            SecretReference = repairedReference,
        };

        await projector.ProjectAsync(
            _context,
            BuildCommittedEnvelope("evt-revoke-repaired", 9, state, Any.Pack(repaired)),
            CancellationToken.None);

        dispatcher.Deletes.Should().BeEquivalentTo(
        [
            "agent-repaired",
            ScheduledAgentCredentialRevocationDocumentIds.BuildBlocked("agent-repaired", "key-repaired"),
        ]);
        dispatcher.Upserts.Should().ContainSingle().Which.Id.Should().Be(
            ScheduledAgentCredentialRevocationDocumentIds.Build(
                "agent-repaired",
                "key-repaired",
                "sec-repaired"));
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
            SecretReferenceRef = "sec-1",
        };

        await projector.ProjectAsync(
            _context,
            BuildCommittedEnvelope("evt-revoke-complete", 8, state, Any.Pack(completed)),
            CancellationToken.None);

        dispatcher.Deletes.Should().BeEquivalentTo(
        [
            "agent-1",
            ScheduledAgentCredentialRevocationDocumentIds.Build("agent-1", "key-1", "sec-1"),
        ]);
        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ApiKeyRevocationProjector_WithOneTrackStillPending_KeepsCanonicalDocument()
    {
        var dispatcher = new RecordingRevocationWriteDispatcher();
        var projector = new UserAgentApiKeyRevocationProjector(dispatcher, _clock);
        var reference = new SecretReference
        {
            Ref = "sec-partial",
            Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
            OwnerScopeKey = "owner-partial",
        };
        var state = new UserAgentCatalogState
        {
            PendingApiKeyRevocations =
            {
                new UserAgentApiKeyRevocation
                {
                    AgentId = "agent-partial",
                    ApiKeyId = "key-partial",
                    NyxApiKeyReference = reference,
                    NyxIdTrack = new ScheduledCredentialRevocationTrack
                    {
                        Status = ScheduledCredentialRevocationTrackStatus.Completed,
                    },
                    VaultTrack = new ScheduledCredentialRevocationTrack
                    {
                        Status = ScheduledCredentialRevocationTrackStatus.Pending,
                    },
                },
            },
        };
        var completedNyxTrack = new UserAgentCatalogApiKeyRevocationAttemptRecordedEvent
        {
            AgentId = "agent-partial",
            ApiKeyId = "key-partial",
            Completed = true,
            Track = UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId,
            SecretReferenceRef = "sec-partial",
        };
        var canonicalId = ScheduledAgentCredentialRevocationDocumentIds.Build(
            "agent-partial",
            "key-partial",
            "sec-partial");

        await projector.ProjectAsync(
            _context,
            BuildCommittedEnvelope("evt-revoke-partial", 10, state, Any.Pack(completedNyxTrack)),
            CancellationToken.None);

        dispatcher.Upserts.Should().ContainSingle().Which.Id.Should().Be(canonicalId);
        dispatcher.Deletes.Should().ContainSingle().Which.Should().Be("agent-partial");
        dispatcher.Deletes.Should().NotContain(canonicalId);
    }

    [Fact]
    public async Task ApiKeyRevocationProjector_WithCompletedNyxOnlyFact_DeletesBlockedDocument()
    {
        var dispatcher = new RecordingRevocationWriteDispatcher();
        var projector = new UserAgentApiKeyRevocationProjector(dispatcher, _clock);
        var completed = new UserAgentCatalogApiKeyRevocationAttemptRecordedEvent
        {
            AgentId = "agent-nyx-only",
            ApiKeyId = "key-nyx-only",
            Completed = true,
            FailureKind = UserAgentApiKeyRevocationFailureKind.None,
            Track = UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId,
            SecretReferenceRef = string.Empty,
        };

        await projector.ProjectAsync(
            _context,
            BuildCommittedEnvelope(
                "evt-revoke-nyx-only-complete",
                9,
                new UserAgentCatalogState(),
                Any.Pack(completed)),
            CancellationToken.None);

        dispatcher.Deletes.Should().BeEquivalentTo(
        [
            "agent-nyx-only",
            ScheduledAgentCredentialRevocationDocumentIds.BuildBlocked(
                "agent-nyx-only",
                "key-nyx-only"),
        ]);
        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task RevocationReadModelKeyMigration_StartAsyncRekeysLegacyDocumentIdempotently()
    {
        var legacyDocument = new UserAgentApiKeyRevocationDocument
        {
            Id = "agent-legacy",
            AgentId = "agent-legacy",
            ApiKeyId = "key-legacy",
            NyxApiKeyReference = new SecretReference { Ref = "sec-legacy" },
            ActorId = UserAgentCatalogGAgent.WellKnownId,
            StateVersion = 7,
            LastEventId = "evt-legacy",
        };
        var store = new InMemoryRevocationDocumentStore(legacyDocument);
        var service = new UserAgentApiKeyRevocationReadModelKeyMigrationService(
            store,
            store,
            NullLogger<UserAgentApiKeyRevocationReadModelKeyMigrationService>.Instance);
        var canonicalId = ScheduledAgentCredentialRevocationDocumentIds.Build(
            "agent-legacy",
            "key-legacy",
            "sec-legacy");

        await service.StartAsync(CancellationToken.None);
        var rerun = await service.MigrateAsync(CancellationToken.None);

        rerun.MigratedCount.Should().Be(0);
        rerun.MaxStateVersion.Should().BeNull();
        store.Documents.Should().ContainSingle();
        store.Documents.Should().ContainKey(canonicalId);
        store.Documents.Should().NotContainKey("agent-legacy");
        store.Documents[canonicalId].StateVersion.Should().Be(7);
        store.Documents[canonicalId].LastEventId.Should().Be("evt-legacy");
    }

    [Fact]
    public void AddScheduledAgents_RegistersRevocationReadModelKeyMigrationHostedService()
    {
        var services = new ServiceCollection();

        services.AddScheduledAgents();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(UserAgentApiKeyRevocationReadModelKeyMigrationService));
    }

    [Fact]
    public async Task RevocationReadModelKeyMigration_ReadsEveryCursorPage()
    {
        var first = BuildLegacyRevocationDocument("agent-page-1", "key-page-1", "sec-page-1");
        var second = BuildLegacyRevocationDocument("agent-page-2", "key-page-2", "sec-page-2");
        var store = new MigrationDocumentStore(
            new Dictionary<string, ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>>
            {
                [string.Empty] = new()
                {
                    Items = [first],
                    NextCursor = "page-2",
                },
                ["page-2"] = new()
                {
                    Items = [second],
                },
            });
        var service = CreateMigrationService(store);

        var result = await service.MigrateAsync(CancellationToken.None);

        result.MigratedCount.Should().Be(2);
        result.MaxStateVersion.Should().Be(7);
        store.QueryCursors.Should().Equal(null, "page-2");
        store.Upserts.Select(static document => document.Id).Should().BeEquivalentTo(
        [
            ScheduledAgentCredentialRevocationDocumentIds.Build(
                "agent-page-1",
                "key-page-1",
                "sec-page-1"),
            ScheduledAgentCredentialRevocationDocumentIds.Build(
                "agent-page-2",
                "key-page-2",
                "sec-page-2"),
        ]);
        store.Deletes.Should().BeEquivalentTo(["agent-page-1", "agent-page-2"]);
    }

    [Fact]
    public async Task RevocationReadModelKeyMigration_WithoutSecretReference_UsesBlockedDocumentId()
    {
        var legacy = BuildLegacyRevocationDocument("agent-blocked-migration", "key-blocked-migration");
        var store = MigrationDocumentStore.SinglePage(legacy);
        var service = CreateMigrationService(store);

        await service.MigrateAsync(CancellationToken.None);

        store.Upserts.Should().ContainSingle().Which.Id.Should().Be(
            ScheduledAgentCredentialRevocationDocumentIds.BuildBlocked(
                "agent-blocked-migration",
                "key-blocked-migration"));
        store.Deletes.Should().ContainSingle().Which.Should().Be("agent-blocked-migration");
    }

    [Fact]
    public async Task RevocationReadModelKeyMigration_WithIncompleteNaturalIdentity_FailsBeforeWrites()
    {
        var incomplete = BuildLegacyRevocationDocument("agent-incomplete", string.Empty);
        var store = MigrationDocumentStore.SinglePage(incomplete);
        var service = CreateMigrationService(store);

        var migrate = () => service.MigrateAsync(CancellationToken.None);

        await migrate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*incomplete natural identity*");
        store.Upserts.Should().BeEmpty();
        store.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task RevocationReadModelKeyMigration_WhenCanonicalUpsertIsRejected_DoesNotDeleteLegacyDocument()
    {
        var legacy = BuildLegacyRevocationDocument("agent-upsert-rejected", "key-upsert-rejected");
        var store = MigrationDocumentStore.SinglePage(legacy);
        store.UpsertResult = ProjectionWriteResult.Conflict();
        var service = CreateMigrationService(store);

        var migrate = () => service.MigrateAsync(CancellationToken.None);

        await migrate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*migration was rejected*");
        store.Upserts.Should().ContainSingle();
        store.Deletes.Should().BeEmpty("the legacy copy is recovery authority until canonical upsert succeeds");
    }

    [Fact]
    public async Task RevocationReadModelKeyMigration_WhenLegacyDeleteIsRejected_FailsForRestartRecovery()
    {
        var legacy = BuildLegacyRevocationDocument("agent-delete-rejected", "key-delete-rejected");
        var store = MigrationDocumentStore.SinglePage(legacy);
        store.DeleteResult = ProjectionWriteResult.Gap();
        var service = CreateMigrationService(store);

        var migrate = () => service.MigrateAsync(CancellationToken.None);

        await migrate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*migration was rejected*");
        store.Upserts.Should().ContainSingle();
        store.Deletes.Should().ContainSingle().Which.Should().Be("agent-delete-rejected");
    }

    [Fact]
    public void CredentialRevocationDocumentId_UsesUtf8LengthPrefixedNaturalKey()
    {
        var first = ScheduledAgentCredentialRevocationDocumentIds.Build("a", "bc", "d");
        var second = ScheduledAgentCredentialRevocationDocumentIds.Build("ab", "c", "d");

        first.Should().StartWith("scr1_");
        string.Concat("a", "bc", "d").Should().Be(string.Concat("ab", "c", "d"));
        first.Should().NotBe(second);
        first.Should().NotContain("=");
    }

    [Fact]
    public void CredentialRevocationIdentity_PrefersConfirmedReferenceBeforeDescriptorFallback()
    {
        var revocation = new UserAgentApiKeyRevocation
        {
            NyxApiKeyReference = new SecretReference { Ref = " confirmed-ref " },
            VaultRevocationDescriptor = new ScheduledCredentialVaultRevocationDescriptor
            {
                Ref = "descriptor-ref",
            },
        };
        var document = new UserAgentApiKeyRevocationDocument
        {
            VaultRevocationDescriptor = new ScheduledCredentialVaultRevocationDescriptor
            {
                Ref = " descriptor-only-ref ",
            },
        };

        ScheduledAgentCredentialRevocationIdentity.ResolveSecretReferenceRef(revocation)
            .Should().Be("confirmed-ref");
        ScheduledAgentCredentialRevocationIdentity.ResolveSecretReferenceRef(document)
            .Should().Be("descriptor-only-ref");
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

    private static UserAgentApiKeyRevocationReadModelKeyMigrationService CreateMigrationService(
        MigrationDocumentStore store) =>
        new(
            store,
            store,
            NullLogger<UserAgentApiKeyRevocationReadModelKeyMigrationService>.Instance);

    private static UserAgentApiKeyRevocationDocument BuildLegacyRevocationDocument(
        string agentId,
        string apiKeyId,
        string? secretReference = null)
    {
        var document = new UserAgentApiKeyRevocationDocument
        {
            Id = agentId,
            AgentId = agentId,
            ApiKeyId = apiKeyId,
            ActorId = UserAgentCatalogGAgent.WellKnownId,
            StateVersion = 7,
            LastEventId = "evt-legacy",
        };
        if (!string.IsNullOrWhiteSpace(secretReference))
            document.NyxApiKeyReference = new SecretReference { Ref = secretReference };
        return document;
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

    private sealed class InMemoryRevocationDocumentStore
        : IProjectionDocumentReader<UserAgentApiKeyRevocationDocument, string>,
          IProjectionWriteDispatcher<UserAgentApiKeyRevocationDocument>
    {
        private readonly Dictionary<string, UserAgentApiKeyRevocationDocument> _documents;

        public InMemoryRevocationDocumentStore(params UserAgentApiKeyRevocationDocument[] documents)
        {
            _documents = documents.ToDictionary(
                static document => document.Id,
                static document => document.Clone(),
                StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, UserAgentApiKeyRevocationDocument> Documents => _documents;

        public Task<UserAgentApiKeyRevocationDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                _documents.TryGetValue(key, out var document) ? document.Clone() : null);
        }

        public Task<ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>
            {
                Items = _documents.Values.Take(query.Take).Select(static document => document.Clone()).ToArray(),
            });
        }

        public Task<ProjectionWriteResult> UpsertAsync(
            UserAgentApiKeyRevocationDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _documents.TryGetValue(readModel.Id, out var existing);
            var result = ProjectionWriteResultEvaluator.Evaluate(existing, readModel);
            if (result.IsApplied)
                _documents[readModel.Id] = readModel.Clone();
            return Task.FromResult(result);
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _documents.Remove(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class MigrationDocumentStore
        : IProjectionDocumentReader<UserAgentApiKeyRevocationDocument, string>,
          IProjectionWriteDispatcher<UserAgentApiKeyRevocationDocument>
    {
        private readonly IReadOnlyDictionary<string, ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>> _pages;

        public MigrationDocumentStore(
            IReadOnlyDictionary<string, ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>> pages)
        {
            _pages = pages;
        }

        public ProjectionWriteResult UpsertResult { get; set; } = ProjectionWriteResult.Applied();

        public ProjectionWriteResult DeleteResult { get; set; } = ProjectionWriteResult.Applied();

        public List<string?> QueryCursors { get; } = [];

        public List<UserAgentApiKeyRevocationDocument> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public static MigrationDocumentStore SinglePage(
            params UserAgentApiKeyRevocationDocument[] documents) =>
            new(new Dictionary<string, ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>>
            {
                [string.Empty] = new()
                {
                    Items = documents.Select(static document => document.Clone()).ToArray(),
                },
            });

        public Task<UserAgentApiKeyRevocationDocument?> GetAsync(
            string key,
            CancellationToken ct = default) =>
            Task.FromResult<UserAgentApiKeyRevocationDocument?>(null);

        public Task<ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            QueryCursors.Add(query.Cursor);
            return Task.FromResult(_pages[query.Cursor ?? string.Empty]);
        }

        public Task<ProjectionWriteResult> UpsertAsync(
            UserAgentApiKeyRevocationDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(UpsertResult);
        }

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deletes.Add(id);
            return Task.FromResult(DeleteResult);
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
