using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Application.Services;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class SourceRegistryCommandApplicationServiceTests
{
    [Fact]
    public async Task RegisterSourceAsync_ShouldEnsureProjectionAndDispatchCommandEnvelope()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var projectionPort = new RecordingSourceCatalogProjectionPort();
        var service = new SourceRegistryCommandApplicationService(runtime, dispatchPort, projectionPort);

        var receipt = await service.RegisterSourceAsync(new RegisterGroupSourceCommand
        {
            SourceId = "doc-1",
            SourceKind = GroupSourceKind.Document,
            CanonicalLocator = "doc://architecture/spec-1",
        });

        projectionPort.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("group-chat:source:doc-1");
        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].actorId.Should().Be("group-chat:source:doc-1");
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be("group-chat:source:doc-1");
        dispatchPort.Calls[0].envelope.Payload.Should().NotBeNull();
        dispatchPort.Calls[0].envelope.Payload!.Is(RegisterGroupSourceCommand.Descriptor).Should().BeTrue();
        receipt.TargetActorId.Should().Be("group-chat:source:doc-1");
        receipt.CorrelationId.Should().Be("source:doc-1:register");
    }
}
