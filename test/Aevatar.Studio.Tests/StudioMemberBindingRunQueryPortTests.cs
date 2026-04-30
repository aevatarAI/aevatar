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
                PlatformBindingCommandId = "platform-bind-1",
                UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-30T08:00:00Z")),
            },
        ]);
        var port = new ProjectionStudioMemberBindingRunQueryPort(reader);

        var run = await port.GetAsync("scope-1", "m-1", "bind-1");

        run.Should().NotBeNull();
        run!.BindingRunId.Should().Be("bind-1");
        run.Status.Should().Be(StudioMemberBindingRunStatusNames.PlatformBindingPending);
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
