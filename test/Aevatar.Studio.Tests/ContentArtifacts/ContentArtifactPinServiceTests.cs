using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests.ContentArtifacts;

public sealed class ContentArtifactPinServiceTests
{
    [Fact]
    public async Task SetAsync_ShouldValidateTargetThenDispatchCanonicalPinMutation()
    {
        var artifactQuery = new RecordingArtifactQueryPort(Artifact());
        var commandPort = new RecordingPinCommandPort();
        var service = new ContentArtifactPinService(
            artifactQuery,
            new RecordingPinQueryPort(current: null),
            commandPort);

        var receipt = await service.SetAsync(
            " scope-1 ",
            " daily-ops-report ",
            new SetContentArtifactPinRequest(" artifact-1 ", 0, " mutation-1 "),
            Principal("owner-1"));

        receipt.PinKey.Should().Be("daily-ops-report");
        commandPort.SetRequest.Should().Be(new SetContentArtifactPinRequest("artifact-1", 0, "mutation-1"));
        commandPort.ScopeId.Should().Be("scope-1");
        commandPort.PinKey.Should().Be("daily-ops-report");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tombstoned")]
    [InlineData("other-owner")]
    public async Task SetAsync_ShouldRejectUnavailableOrNonOwnedTarget(string scenario)
    {
        var artifact = scenario switch
        {
            "missing" => null,
            "tombstoned" => Artifact() with { LifecycleStatus = ContentArtifactLifecycleStatusNames.Tombstoned },
            "other-owner" => Artifact() with { Owner = Principal("owner-2") },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };
        var commandPort = new RecordingPinCommandPort();
        var service = new ContentArtifactPinService(
            new RecordingArtifactQueryPort(artifact),
            new RecordingPinQueryPort(current: null),
            commandPort);

        var act = () => service.SetAsync(
            "scope-1",
            "daily-ops-report",
            new SetContentArtifactPinRequest("artifact-1", 0, "mutation-1"),
            Principal("owner-1"));

        await act.Should().ThrowAsync<ContentArtifactNotFoundException>();
        commandPort.SetRequest.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ShouldDispatchStaleVersionForActorOwnedPersistedRejection()
    {
        var commandPort = new RecordingPinCommandPort();
        var service = new ContentArtifactPinService(
            new RecordingArtifactQueryPort(Artifact()),
            new RecordingPinQueryPort(Pin(version: 3)),
            commandPort);

        await service.SetAsync(
            "scope-1",
            "daily-ops-report",
            new SetContentArtifactPinRequest("artifact-1", 2, "mutation-2"),
            Principal("owner-1"));

        commandPort.SetRequest.Should().Be(
            new SetContentArtifactPinRequest("artifact-1", 2, "mutation-2"));
    }

    [Fact]
    public async Task ClearAsync_ShouldUsePinnedByAuthorityEvenWhenTargetArtifactIsUnavailable()
    {
        var artifactQuery = new RecordingArtifactQueryPort(current: null);
        var commandPort = new RecordingPinCommandPort();
        var service = new ContentArtifactPinService(
            artifactQuery,
            new RecordingPinQueryPort(Pin(version: 4)),
            commandPort);

        await service.ClearAsync(
            "scope-1",
            "daily-ops-report",
            new ClearContentArtifactPinRequest(4, "mutation-clear"),
            Principal("owner-1"));

        artifactQuery.GetCallCount.Should().Be(0);
        commandPort.ClearRequest.Should().Be(new ClearContentArtifactPinRequest(4, "mutation-clear"));
    }

    [Fact]
    public async Task ClearAsync_ShouldDispatchStaleVersionForActorOwnedPersistedRejection()
    {
        var commandPort = new RecordingPinCommandPort();
        var service = new ContentArtifactPinService(
            new RecordingArtifactQueryPort(current: null),
            new RecordingPinQueryPort(Pin(version: 4)),
            commandPort);

        await service.ClearAsync(
            "scope-1",
            "daily-ops-report",
            new ClearContentArtifactPinRequest(3, "mutation-clear"),
            Principal("owner-1"));

        commandPort.ClearRequest.Should().Be(new ClearContentArtifactPinRequest(3, "mutation-clear"));
    }

    [Fact]
    public async Task ClearAsync_ShouldHideOtherOwnersPin()
    {
        var otherOwnerService = new ContentArtifactPinService(
            new RecordingArtifactQueryPort(current: null),
            new RecordingPinQueryPort(Pin(version: 1)),
            new RecordingPinCommandPort());
        var denied = () => otherOwnerService.ClearAsync(
            "scope-1",
            "daily-ops-report",
            new ClearContentArtifactPinRequest(1, "mutation-clear"),
            Principal("owner-2"));
        await denied.Should().ThrowAsync<ContentArtifactPinNotFoundException>();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnExistingEmptyPointerDocument()
    {
        var emptyPin = Pin(version: 2) with { PinnedArtifactId = null, PinnedBy = null };
        var service = new ContentArtifactPinService(
            new RecordingArtifactQueryPort(current: null),
            new RecordingPinQueryPort(emptyPin),
            new RecordingPinCommandPort());

        var current = await service.GetAsync("scope-1", "daily-ops-report");

        current.Should().Be(emptyPin);
        current.PinVersion.Should().Be(2);
        current.LastMutationStatus.Should().Be("succeeded");
    }

    [Fact]
    public async Task ClearAsync_ShouldDispatchEmptyPointerReplayForLastMutationRequester()
    {
        var commandPort = new RecordingPinCommandPort();
        var emptyPin = Pin(version: 2) with
        {
            PinnedArtifactId = null,
            PinnedBy = null,
            LastMutationId = "mutation-clear",
        };
        var service = new ContentArtifactPinService(
            new RecordingArtifactQueryPort(current: null),
            new RecordingPinQueryPort(emptyPin),
            commandPort);

        await service.ClearAsync(
            "scope-1",
            "daily-ops-report",
            new ClearContentArtifactPinRequest(1, "mutation-clear"),
            Principal("owner-1"));

        commandPort.ClearRequest.Should().Be(new ClearContentArtifactPinRequest(1, "mutation-clear"));
    }

    [Theory]
    [InlineData("different-mutation", "owner-1")]
    [InlineData("mutation-2", "owner-2")]
    public async Task ClearAsync_ShouldRejectNonReplayAgainstEmptyPointer(string mutationId, string requesterId)
    {
        var commandPort = new RecordingPinCommandPort();
        var emptyPin = Pin(version: 2) with { PinnedArtifactId = null, PinnedBy = null };
        var service = new ContentArtifactPinService(
            new RecordingArtifactQueryPort(current: null),
            new RecordingPinQueryPort(emptyPin),
            commandPort);

        var act = () => service.ClearAsync(
            "scope-1",
            "daily-ops-report",
            new ClearContentArtifactPinRequest(2, mutationId),
            Principal(requesterId));

        await act.Should().ThrowAsync<ContentArtifactPinNotFoundException>();
        commandPort.ClearRequest.Should().BeNull();
    }

    private static ContentArtifactCurrentStateResponse Artifact() =>
        new(
            "artifact-1",
            "scope-1",
            null,
            "markdown",
            "Daily report",
            "internal",
            ContentArtifactLifecycleStatusNames.Active,
            null,
            1,
            1,
            Principal("owner-1"),
            [],
            [],
            null,
            null,
            [],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static ContentArtifactPinCurrentStateResponse Pin(long version) =>
        new(
            "scope-1",
            "daily-ops-report",
            "artifact-1",
            Principal("owner-1"),
            version,
            version,
            DateTimeOffset.UnixEpoch,
            $"mutation-{version}",
            "succeeded",
            LastMutationRequestedBy: Principal("owner-1"));

    private static ContentArtifactPrincipalContract Principal(string id) => new(id, "user");

    private sealed class RecordingArtifactQueryPort(ContentArtifactCurrentStateResponse? current)
        : IContentArtifactQueryPort
    {
        public int GetCallCount { get; private set; }

        public Task<ContentArtifactCurrentStateResponse?> GetAsync(
            string scopeId,
            string artifactId,
            CancellationToken ct = default)
        {
            GetCallCount++;
            return Task.FromResult(current);
        }

        public Task<ContentArtifactListResponse> ListAsync(string scopeId, string requesterPrincipalId, ContentArtifactQueryRequest query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactCurrentStateResponse?> GetByDedupKeyAsync(string scopeId, string dedupKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingPinQueryPort(ContentArtifactPinCurrentStateResponse? current)
        : IContentArtifactPinQueryPort
    {
        public Task<ContentArtifactPinCurrentStateResponse?> GetAsync(
            string scopeId,
            string pinKey,
            CancellationToken ct = default) => Task.FromResult(current);
    }

    private sealed class RecordingPinCommandPort : IContentArtifactPinCommandPort
    {
        public string? ScopeId { get; private set; }
        public string? PinKey { get; private set; }
        public SetContentArtifactPinRequest? SetRequest { get; private set; }
        public ClearContentArtifactPinRequest? ClearRequest { get; private set; }

        public Task<ContentArtifactPinAcceptedReceipt> SetAsync(string scopeId, string pinKey, SetContentArtifactPinRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            ScopeId = scopeId;
            PinKey = pinKey;
            SetRequest = request;
            return Receipt(scopeId, pinKey);
        }

        public Task<ContentArtifactPinAcceptedReceipt> ClearAsync(string scopeId, string pinKey, ClearContentArtifactPinRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            ScopeId = scopeId;
            PinKey = pinKey;
            ClearRequest = request;
            return Receipt(scopeId, pinKey);
        }

        private static Task<ContentArtifactPinAcceptedReceipt> Receipt(string scopeId, string pinKey) =>
            Task.FromResult(new ContentArtifactPinAcceptedReceipt(
                scopeId,
                pinKey,
                "command-1",
                "correlation-1",
                ContentArtifactCommandStageNames.DispatchAccepted));
    }
}
