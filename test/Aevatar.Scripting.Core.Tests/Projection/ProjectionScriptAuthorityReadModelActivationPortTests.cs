using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ProjectionScriptAuthorityReadModelActivationPortTests
{
    [Fact]
    public async Task ActivateAsync_ShouldEnsureProjectionForActor()
    {
        var activationService = new StubActivationService(new object());
        var projectionPort = CreateProjectionPort(activationService);

        await projectionPort.ActivateAsync("script-definition:script-1", CancellationToken.None);

        activationService.ActorIds.Should().Equal("script-definition:script-1");
    }

    [Fact]
    public async Task ActivateAsync_ShouldThrow_WhenActivationDoesNotReturnLease()
    {
        var projectionPort = CreateProjectionPort(new StubActivationService(null));
        var action = () => projectionPort.ActivateAsync("script-definition:script-2", CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*script-definition:script-2*");
    }

    [Fact]
    public async Task EnsureActorProjectionAsync_ShouldReturnNull_WhenActorIdIsBlank()
    {
        var activationService = new StubActivationService(new object());
        var projectionPort = CreateProjectionPort(activationService);

        var lease = await projectionPort.EnsureActorProjectionAsync(" ", CancellationToken.None);

        lease.Should().BeNull();
        activationService.ActorIds.Should().BeEmpty();
    }

    private static ScriptAuthorityProjectionPort CreateProjectionPort(
        StubActivationService activationService) =>
        new(
            activationService,
            new StubReleaseService());

    private sealed class StubActivationService(object? leaseMarker)
        : IProjectionMaterializationActivationService<ScriptAuthorityRuntimeLease>
    {
        public List<string> ActorIds { get; } = [];

        public Task<ScriptAuthorityRuntimeLease> EnsureAsync(
            ProjectionMaterializationStartRequest request,
            CancellationToken ct = default)
        {
            ActorIds.Add(request.RootActorId);
            if (leaseMarker is null)
                return Task.FromResult<ScriptAuthorityRuntimeLease>(null!);

            var context = new ScriptAuthorityProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            };

            return Task.FromResult(new ScriptAuthorityRuntimeLease(context));
        }
    }

    private sealed class StubReleaseService : IProjectionMaterializationReleaseService<ScriptAuthorityRuntimeLease>
    {
        public Task ReleaseIfIdleAsync(ScriptAuthorityRuntimeLease runtimeLease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
