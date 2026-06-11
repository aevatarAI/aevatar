using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberBindingRunQueryPortTests
{
    [Fact]
    public async Task GetAsync_ShouldReadRunStatusFromBindingRunDocument()
    {
        var actorId = StudioMemberConventions.BuildBindingRunActorId("bind-1");
        var reader = new StubDocumentReader([
            new StudioMemberBindingRunCurrentStateDocument
            {
                Id = actorId,
                ActorId = actorId,
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "m-1",
                Status = StudioMemberBindingRunStatusNames.PlatformBindingPending,
                StateVersion = 7,
                PlatformBindingCommandId = "platform-bind-1",
                UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-30T08:00:00Z")),
            },
        ]);
        var port = new ProjectionStudioMemberBindingRunQueryPort(reader);

        var run = await port.GetAsync("scope-1", "m-1", "bind-1");

        run.Should().NotBeNull();
        run!.BindingRunId.Should().Be("bind-1");
        run.ScopeId.Should().Be("scope-1");
        run.MemberId.Should().Be("m-1");
        run.Status.Should().Be(StudioMemberBindingRunStatusNames.PlatformBindingPending);
        run.StateVersion.Should().Be(7);
        run.PlatformBindingCommandId.Should().Be("platform-bind-1");
        run.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-04-30T08:00:00Z"));
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenRunBelongsToDifferentMember()
    {
        var actorId = StudioMemberConventions.BuildBindingRunActorId("bind-1");
        var reader = new StubDocumentReader([
            new StudioMemberBindingRunCurrentStateDocument
            {
                Id = actorId,
                ActorId = actorId,
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "other-member",
                Status = StudioMemberBindingRunStatusNames.Succeeded,
            },
        ]);
        var port = new ProjectionStudioMemberBindingRunQueryPort(reader);

        var run = await port.GetAsync("scope-1", "m-1", "bind-1");

        run.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenRunDocumentIsMissing()
    {
        var port = new ProjectionStudioMemberBindingRunQueryPort(new StubDocumentReader([]));

        var run = await port.GetAsync("scope-1", "m-1", "bind-missing");

        run.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenRunBelongsToDifferentScope()
    {
        var actorId = StudioMemberConventions.BuildBindingRunActorId("bind-1");
        var reader = new StubDocumentReader([
            new StudioMemberBindingRunCurrentStateDocument
            {
                Id = actorId,
                ActorId = actorId,
                BindingRunId = "bind-1",
                ScopeId = "other-scope",
                MemberId = "m-1",
                Status = StudioMemberBindingRunStatusNames.Succeeded,
            },
        ]);
        var port = new ProjectionStudioMemberBindingRunQueryPort(reader);

        var run = await port.GetAsync("scope-1", "m-1", "bind-1");

        run.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenDocumentBindingRunIdDoesNotMatch()
    {
        var actorId = StudioMemberConventions.BuildBindingRunActorId("bind-1");
        var reader = new StubDocumentReader([
            new StudioMemberBindingRunCurrentStateDocument
            {
                Id = actorId,
                ActorId = actorId,
                BindingRunId = "other-bind",
                ScopeId = "scope-1",
                MemberId = "m-1",
                Status = StudioMemberBindingRunStatusNames.Succeeded,
            },
        ]);
        var port = new ProjectionStudioMemberBindingRunQueryPort(reader);

        var run = await port.GetAsync("scope-1", "m-1", "bind-1");

        run.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldMapFailureAndUnknownStatusFromDocument()
    {
        var failedAt = DateTimeOffset.Parse("2026-04-30T09:30:00Z");
        var actorId = StudioMemberConventions.BuildBindingRunActorId("bind-1");
        var reader = new StubDocumentReader([
            new StudioMemberBindingRunCurrentStateDocument
            {
                Id = actorId,
                ActorId = actorId,
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "m-1",
                Status = "new-status-from-future",
                FailureCode = "BIND_FAILED",
                FailureMessage = "platform refused the revision",
                FailureAt = Timestamp.FromDateTimeOffset(failedAt),
            },
        ]);
        var port = new ProjectionStudioMemberBindingRunQueryPort(reader);

        var run = await port.GetAsync("scope-1", "m-1", "bind-1");

        run.Should().NotBeNull();
        run!.Status.Should().Be(StudioMemberBindingRunStatusNames.Unknown);
        run.Failure.Should().NotBeNull();
        run.Failure!.Code.Should().Be("BIND_FAILED");
        run.Failure.Message.Should().Be("platform refused the revision");
        run.Failure.FailedAt.Should().Be(failedAt);
        run.PlatformBindingCommandId.Should().BeNull();
    }

    [Theory]
    [InlineData(StudioMemberBindingRunStatusNames.Accepted)]
    [InlineData(StudioMemberBindingRunStatusNames.AdmissionPending)]
    [InlineData(StudioMemberBindingRunStatusNames.Admitted)]
    [InlineData(StudioMemberBindingRunStatusNames.PlatformBindingPending)]
    [InlineData(StudioMemberBindingRunStatusNames.MemberNotificationPending)]
    [InlineData(StudioMemberBindingRunStatusNames.Succeeded)]
    [InlineData(StudioMemberBindingRunStatusNames.Failed)]
    [InlineData(StudioMemberBindingRunStatusNames.Rejected)]
    public async Task GetAsync_ShouldPreserveKnownStatusWireValues(string status)
    {
        var actorId = StudioMemberConventions.BuildBindingRunActorId("bind-1");
        var reader = new StubDocumentReader([
            new StudioMemberBindingRunCurrentStateDocument
            {
                Id = actorId,
                ActorId = actorId,
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "m-1",
                Status = status,
            },
        ]);
        var port = new ProjectionStudioMemberBindingRunQueryPort(reader);

        var run = await port.GetAsync("scope-1", "m-1", "bind-1");

        run!.Status.Should().Be(status);
    }

    private sealed class StubDocumentReader
        : IProjectionDocumentReader<StudioMemberBindingRunCurrentStateDocument, string>
    {
        private readonly Dictionary<string, StudioMemberBindingRunCurrentStateDocument> _byId;

        public StubDocumentReader(IReadOnlyList<StudioMemberBindingRunCurrentStateDocument> documents)
        {
            _byId = documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
        }

        public Task<StudioMemberBindingRunCurrentStateDocument?> GetAsync(
            string key, CancellationToken ct = default)
        {
            return Task.FromResult(_byId.TryGetValue(key, out var doc) ? doc : null);
        }

        public Task<ProjectionDocumentQueryResult<StudioMemberBindingRunCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query, CancellationToken ct = default)
        {
            return Task.FromResult(new ProjectionDocumentQueryResult<StudioMemberBindingRunCurrentStateDocument>
            {
                Items = _byId.Values.ToList(),
            });
        }
    }
}
