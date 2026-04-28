using Aevatar.CQRS.Projection.Core.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogProjectionPortTests
{
    [Fact]
    public async Task EnsureProjectionForActorAsync_ShouldUseDedicatedProjectionScopeKind_WhileKeepingLegacyCatalogIndex()
    {
        var activationService = Substitute.For<IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogMaterializationRuntimeLease?>(new UserAgentCatalogMaterializationRuntimeLease(
                new UserAgentCatalogMaterializationContext
                {
                    RootActorId = UserAgentCatalogGAgent.WellKnownId,
                    ProjectionKind = UserAgentCatalogProjectionPort.ProjectionKind,
                }))!);

        var port = new UserAgentCatalogProjectionPort(activationService);
        var metadataProvider = new UserAgentCatalogDocumentMetadataProvider();

        await port.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, CancellationToken.None);

        await activationService.Received(1).EnsureAsync(
            Arg.Is<ProjectionScopeStartRequest>(request =>
                request.RootActorId == UserAgentCatalogGAgent.WellKnownId &&
                request.ProjectionKind == UserAgentCatalogProjectionPort.ProjectionKind &&
                request.ProjectionKind != metadataProvider.Metadata.IndexName),
            Arg.Any<CancellationToken>());
        metadataProvider.Metadata.IndexName.Should().Be("agent-registry");
        UserAgentCatalogProjectionPort.ProjectionKind.Should().Be("user-agent-catalog-read-model");
    }
}
