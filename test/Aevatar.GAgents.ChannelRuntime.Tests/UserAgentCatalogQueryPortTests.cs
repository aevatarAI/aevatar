using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogQueryPortTests
{
    [Fact]
    public async Task QueryVisibleByCallerAsync_ReturnsOwnedAndSharedRows_WithDedupe()
    {
        var alice = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var bob = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var reader = new RecordingDocumentReader(
        [
            BuildDocument("alice-agent", alice),
            BuildSharedDocument("shared-agent", alice, allowTrigger: true),
            BuildDocument("bob-agent", bob),
        ]);
        var port = CreatePort(reader);

        var visible = await port.QueryVisibleByCallerAsync(alice, CancellationToken.None);

        visible.Select(static entry => entry.AgentId).Should().BeEquivalentTo(["alice-agent", "shared-agent"]);
        reader.Queries.Should().HaveCount(2);
        reader.Queries[0].Filters.Select(static filter => filter.FieldPath)
            .Should().Contain($"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.SenderId)}");
        reader.Queries[1].Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(UserAgentCatalogDocument.VisibleSharingAudienceKey) &&
            string.Equals(filter.Value.RawValue as string, "lark:bot-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryVisibleByCallerAsync_SameRegistrationScopeTeammate_ReturnsSharedRows()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var teammate = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var reader = new RecordingDocumentReader(
        [
            BuildDocument("owner-private-agent", owner),
            BuildSharedDocument("shared-agent", owner, allowTrigger: false),
        ]);
        var port = CreatePort(reader);

        var visible = await port.QueryVisibleByCallerAsync(teammate, CancellationToken.None);

        visible.Select(static entry => entry.AgentId).Should().Equal("shared-agent");
        reader.Queries.Should().HaveCount(2);
        reader.Queries[1].Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(UserAgentCatalogDocument.VisibleSharingAudienceKey) &&
            string.Equals(filter.Value.RawValue as string, "lark:bot-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetVisibleForCallerAsync_AllowsSameRegistrationScopeSharedRow()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var teammate = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var port = CreatePort(new RecordingDocumentReader(
        [
            BuildSharedDocument("shared-agent", owner, allowTrigger: false),
        ]));

        var entry = await port.GetVisibleForCallerAsync("shared-agent", teammate, CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.AgentId.Should().Be("shared-agent");
    }

    [Fact]
    public async Task GetTriggerableForCallerAsync_RequiresTriggerGrant()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var teammate = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var port = CreatePort(new RecordingDocumentReader(
        [
            BuildSharedDocument("view-only-agent", owner, allowTrigger: false),
            BuildSharedDocument("trigger-agent", owner, allowTrigger: true),
        ]));

        var viewOnly = await port.GetTriggerableForCallerAsync("view-only-agent", teammate, CancellationToken.None);
        var triggerable = await port.GetTriggerableForCallerAsync("trigger-agent", teammate, CancellationToken.None);

        viewOnly.Should().BeNull();
        triggerable.Should().NotBeNull();
        triggerable!.AgentId.Should().Be("trigger-agent");
    }

    [Fact]
    public async Task SharedAccess_DifferentRegistrationScope_ReturnsNull()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var otherScope = OwnerScope.ForChannel("user-B", "lark", "bot-2", "bob");
        var port = CreatePort(new RecordingDocumentReader(
        [
            BuildSharedDocument("shared-agent", owner, allowTrigger: true),
        ]));

        var visible = await port.GetVisibleForCallerAsync("shared-agent", otherScope, CancellationToken.None);
        var triggerable = await port.GetTriggerableForCallerAsync("shared-agent", otherScope, CancellationToken.None);

        visible.Should().BeNull();
        triggerable.Should().BeNull();
    }

    [Fact]
    public async Task QueryPendingApiKeyRevocationsByCallerAsync_ReturnsCallerScopedRows()
    {
        var alice = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var bob = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var revocationReader = new RecordingRevocationDocumentReader(
        [
            BuildRevocationDocument("alice-agent", "key-alice", alice),
            BuildRevocationDocument("bob-agent", "key-bob", bob),
            BuildRevocationDocument("missing-key", string.Empty, alice),
        ]);
        var port = new UserAgentCatalogQueryPort(new RecordingDocumentReader([]), revocationReader);

        var pending = await port.QueryPendingApiKeyRevocationsByCallerAsync(alice, CancellationToken.None);

        pending.Should().ContainSingle();
        pending[0].AgentId.Should().Be("alice-agent");
        pending[0].ApiKeyId.Should().Be("key-alice");
        pending[0].OwnerScope!.MatchesStrictly(alice).Should().BeTrue();
        pending[0].NyxApiKeyReference.Should().NotBeNull();
        pending[0].NyxApiKeyReference!.Ref.Should().Be("sec-alice-agent");
        pending[0].NyxApiKeyReference!.Purpose.Should().Be(CredentialSecretPurposes.ScheduledNyxApiKey);
        pending[0].NyxApiKeyReference!.OwnerScopeKey.Should().Be("owner-alice-agent");
        pending[0].NyxApiKeyReference!.Version.Should().Be(1);
        pending[0].NyxApiKeyReference!.Fingerprint.Should().Be("sha256:test");
        pending[0].NyxIdTrack.Should().NotBeNull();
        pending[0].NyxIdTrack!.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Pending);
        pending[0].NyxIdTrack!.AttemptCount.Should().Be(2);
        pending[0].NyxIdTrack!.LastAttemptAt.Should().BeEquivalentTo(Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 6, 20, 10, 1, 0, TimeSpan.Zero)));
        pending[0].NyxIdTrack!.LastHttpStatus.Should().Be(503);
        pending[0].NyxIdTrack!.LastError.Should().Be("nyx unavailable");
        pending[0].NyxIdTrack!.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Transient);
        pending[0].VaultTrack.Should().NotBeNull();
        pending[0].VaultTrack!.Status.Should().Be(ScheduledCredentialRevocationTrackStatus.Completed);
        pending[0].VaultTrack!.AttemptCount.Should().Be(1);
        pending[0].VaultTrack!.LastAttemptAt.Should().BeEquivalentTo(Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 6, 20, 10, 2, 0, TimeSpan.Zero)));
        pending[0].VaultTrack!.LastHttpStatus.Should().Be(0);
        pending[0].VaultTrack!.LastError.Should().BeEmpty();
        pending[0].VaultTrack!.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.None);
        pending[0].VaultRevocationDescriptor.Should().NotBeNull();
        pending[0].VaultRevocationDescriptor!.Ref.Should().Be("sec-alice-agent");
        pending[0].VaultRevocationDescriptor!.SubjectId.Should().Be("key-alice");
        pending[0].VaultRevocationDescriptor!.ReferenceAvailability.Should()
            .Be(ScheduledCredentialVaultReferenceAvailability.Confirmed);
        pending[0].SecretSubjectId.Should().Be("key-alice");
        pending[0].RepairReason.Should().Be("restore exact reference");
        pending[0].RequestedBySubjectId.Should().Be("admin-1");
        pending[0].RepairRequestedAtUnixMs.Should().Be(1_750_412_800_000);
        revocationReader.Queries.Should().ContainSingle();
        revocationReader.Queries[0].Filters.Select(static filter => filter.FieldPath)
            .Should().Contain($"{nameof(UserAgentApiKeyRevocationDocument.OwnerScope)}.{nameof(OwnerScope.SenderId)}");
    }

    [Fact]
    public async Task QueryPendingApiKeyRevocationsByCallerAsync_CollapsesLegacyAndCanonicalCopies()
    {
        var owner = OwnerScope.ForNyxIdNative("user-a");
        var legacy = BuildRevocationDocument("agent-a", "key-a", owner);
        var canonical = legacy.Clone();
        canonical.Id = ScheduledAgentCredentialRevocationDocumentIds.Build(
            canonical.AgentId,
            canonical.ApiKeyId,
            canonical.NyxApiKeyReference.Ref);
        canonical.LastError = "canonical-copy";
        var port = new UserAgentCatalogQueryPort(
            new RecordingDocumentReader([]),
            new RecordingRevocationDocumentReader([legacy, canonical]));

        var pending = await port.QueryPendingApiKeyRevocationsByCallerAsync(
            owner,
            CancellationToken.None);

        pending.Should().ContainSingle();
        pending[0].AgentId.Should().Be("agent-a");
        pending[0].ApiKeyId.Should().Be("key-a");
        pending[0].LastError.Should().Be("canonical-copy");
        pending[0].CatalogAuthorityStateVersion.Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPendingApiKeyRevocationsByCallerAsync_SelectsHighestAuthorityVersionRegardlessOfOrder(
        bool canonicalFirst)
    {
        var owner = OwnerScope.ForNyxIdNative("user-versioned");
        var newerLegacy = BuildRevocationDocument("agent-versioned", "key-versioned", owner);
        newerLegacy.StateVersion = 4;
        newerLegacy.LastError = "newer-legacy-copy";
        var olderCanonical = newerLegacy.Clone();
        olderCanonical.Id = ScheduledAgentCredentialRevocationDocumentIds.Build(
            olderCanonical.AgentId,
            olderCanonical.ApiKeyId,
            olderCanonical.NyxApiKeyReference.Ref);
        olderCanonical.StateVersion = 3;
        olderCanonical.LastError = "older-canonical-copy";
        var documents = canonicalFirst
            ? new List<UserAgentApiKeyRevocationDocument> { olderCanonical, newerLegacy }
            : [newerLegacy, olderCanonical];
        var port = new UserAgentCatalogQueryPort(
            new RecordingDocumentReader([]),
            new RecordingRevocationDocumentReader(documents));

        var pending = await port.QueryPendingApiKeyRevocationsByCallerAsync(
            owner,
            CancellationToken.None);

        pending.Should().ContainSingle();
        pending[0].CatalogAuthorityStateVersion.Should().Be(4);
        pending[0].LastError.Should().Be("newer-legacy-copy");
    }

    private static UserAgentCatalogDocument BuildDocument(string agentId, OwnerScope ownerScope) =>
        new()
        {
            Id = agentId,
            AgentType = ScheduledWorkflowAgentDefaults.AgentType,
            TemplateName = "summary",
            OwnerScope = ownerScope.Clone(),
            StateVersion = 1,
            ActorId = UserAgentCatalogGAgent.WellKnownId,
        };

    private static UserAgentCatalogQueryPort CreatePort(RecordingDocumentReader reader) =>
        new(reader, new ThrowingRevocationDocumentReader());

    private static UserAgentCatalogDocument BuildSharedDocument(
        string agentId,
        OwnerScope ownerScope,
        bool allowTrigger)
    {
        var document = BuildDocument(agentId, ownerScope);
        document.SharingGrant = new ScheduledAgentSharingGrant
        {
            SharedWithRegistrationScope = ownerScope.RegistrationScopeId,
            AllowTrigger = allowTrigger,
            GrantedBy = ownerScope.SenderId,
        };
        document.VisibleSharingAudienceKey = $"{ownerScope.Platform}:{ownerScope.RegistrationScopeId}";
        document.TriggerSharingAudienceKey = allowTrigger ? document.VisibleSharingAudienceKey : string.Empty;
        return document;
    }

    private static UserAgentApiKeyRevocationDocument BuildRevocationDocument(
        string agentId,
        string apiKeyId,
        OwnerScope ownerScope) =>
        new()
        {
            Id = agentId,
            AgentId = agentId,
            ApiKeyId = apiKeyId,
            OwnerScope = ownerScope.Clone(),
            NyxApiKeyReference = new SecretReference
            {
                Ref = $"sec-{agentId}",
                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                OwnerScopeKey = $"owner-{agentId}",
                Version = 1,
                Fingerprint = "sha256:test",
            },
            AttemptCount = 1,
            LastHttpStatus = 503,
            LastError = "upstream unavailable",
            FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
            NyxIdTrack = new ScheduledCredentialRevocationTrack
            {
                Status = ScheduledCredentialRevocationTrackStatus.Pending,
                AttemptCount = 2,
                LastAttemptAt = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 6, 20, 10, 1, 0, TimeSpan.Zero)),
                LastHttpStatus = 503,
                LastError = "nyx unavailable",
                FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
            },
            VaultTrack = new ScheduledCredentialRevocationTrack
            {
                Status = ScheduledCredentialRevocationTrackStatus.Completed,
                AttemptCount = 1,
                LastAttemptAt = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 6, 20, 10, 2, 0, TimeSpan.Zero)),
                FailureKind = UserAgentApiKeyRevocationFailureKind.None,
            },
            VaultRevocationDescriptor = new ScheduledCredentialVaultRevocationDescriptor
            {
                Ref = $"sec-{agentId}",
                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                OwnerScopeKey = $"owner-{agentId}",
                SubjectId = apiKeyId,
                ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.Confirmed,
            },
            SecretSubjectId = apiKeyId,
            RepairReason = "restore exact reference",
            RequestedBySubjectId = "admin-1",
            RepairRequestedAtUnixMs = 1_750_412_800_000,
            StateVersion = 3,
            LastEventId = "evt-3",
        };

    private sealed class RecordingDocumentReader : IProjectionDocumentReader<UserAgentCatalogDocument, string>
    {
        private readonly IList<UserAgentCatalogDocument> _items;

        public List<ProjectionDocumentQuery> Queries { get; } = [];

        public RecordingDocumentReader(IList<UserAgentCatalogDocument> items)
        {
            _items = items;
        }

        public Task<UserAgentCatalogDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var match = _items.FirstOrDefault(item => string.Equals(item.Id, key, StringComparison.Ordinal));
            return Task.FromResult(match?.Clone());
        }

        public Task<ProjectionDocumentQueryResult<UserAgentCatalogDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            IEnumerable<UserAgentCatalogDocument> filtered = _items.Select(static item => item.Clone());
            foreach (var filter in query.Filters)
            {
                filtered = filtered.Where(item => MatchesFilter(item, filter));
            }

            var page = filtered.Take(query.Take).ToArray();
            return Task.FromResult(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items = page,
            });
        }

        private static bool MatchesFilter(UserAgentCatalogDocument document, ProjectionDocumentFilter filter)
        {
            if (filter.Operator != ProjectionDocumentFilterOperator.Eq)
                return true;

            var actual = filter.FieldPath switch
            {
                "OwnerScope.NyxUserId" => document.OwnerScope?.NyxUserId ?? string.Empty,
                "OwnerScope.Platform" => document.OwnerScope?.Platform ?? string.Empty,
                "OwnerScope.RegistrationScopeId" => document.OwnerScope?.RegistrationScopeId ?? string.Empty,
                "OwnerScope.SenderId" => document.OwnerScope?.SenderId ?? string.Empty,
                nameof(UserAgentCatalogDocument.VisibleSharingAudienceKey) => document.VisibleSharingAudienceKey,
                nameof(UserAgentCatalogDocument.TriggerSharingAudienceKey) => document.TriggerSharingAudienceKey,
                _ => string.Empty,
            };
            return string.Equals(actual, filter.Value.RawValue as string, StringComparison.Ordinal);
        }
    }

    private sealed class ThrowingRevocationDocumentReader : IProjectionDocumentReader<UserAgentApiKeyRevocationDocument, string>
    {
        public Task<UserAgentApiKeyRevocationDocument?> GetAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("This test fixture does not exercise API key revocation documents.");

        public Task<ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("This test fixture does not exercise API key revocation documents.");
    }

    private sealed class RecordingRevocationDocumentReader : IProjectionDocumentReader<UserAgentApiKeyRevocationDocument, string>
    {
        private readonly IList<UserAgentApiKeyRevocationDocument> _items;

        public List<ProjectionDocumentQuery> Queries { get; } = [];

        public RecordingRevocationDocumentReader(IList<UserAgentApiKeyRevocationDocument> items)
        {
            _items = items;
        }

        public Task<UserAgentApiKeyRevocationDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var match = _items.FirstOrDefault(item => string.Equals(item.Id, key, StringComparison.Ordinal));
            return Task.FromResult(match?.Clone());
        }

        public Task<ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            IEnumerable<UserAgentApiKeyRevocationDocument> filtered = _items.Select(static item => item.Clone());
            foreach (var filter in query.Filters)
            {
                filtered = filtered.Where(item => MatchesFilter(item, filter));
            }

            return Task.FromResult(new ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>
            {
                Items = filtered.Take(query.Take).ToArray(),
            });
        }

        private static bool MatchesFilter(UserAgentApiKeyRevocationDocument document, ProjectionDocumentFilter filter)
        {
            if (filter.Operator != ProjectionDocumentFilterOperator.Eq)
                return true;

            var actual = filter.FieldPath switch
            {
                "OwnerScope.NyxUserId" => document.OwnerScope?.NyxUserId ?? string.Empty,
                "OwnerScope.Platform" => document.OwnerScope?.Platform ?? string.Empty,
                "OwnerScope.RegistrationScopeId" => document.OwnerScope?.RegistrationScopeId ?? string.Empty,
                "OwnerScope.SenderId" => document.OwnerScope?.SenderId ?? string.Empty,
                _ => string.Empty,
            };
            return string.Equals(actual, filter.Value.RawValue as string, StringComparison.Ordinal);
        }
    }
}
