using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Application;

public class RunScriptActorRequestAdapterTests
{
    [Fact]
    public void Map_ShouldProduceEventEnvelope_WithRunScriptRequestedEvent()
    {
        var adapter = new RunScriptActorRequestAdapter();

        var envelope = adapter.Map(
            new RunScriptActorRequest(
                "run-1",
                Any.Pack(new Struct { Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-1") } }),
                "r1",
                "definition-1"),
            "actor-1");

        envelope.Payload.Should().NotBeNull();
        envelope.Payload!.TypeUrl.Should().Contain(nameof(RunScriptRequestedEvent));
        envelope.Route!.Direction.Should().Be(EventDirection.Self);
        envelope.Route.PublisherActorId.Should().Be("scripting.application");
        envelope.Route.TargetActorId.Should().Be("actor-1");

        var payload = envelope.Payload.Unpack<RunScriptRequestedEvent>();
        payload.RunId.Should().Be("run-1");
        payload.ScriptRevision.Should().Be("r1");
        payload.DefinitionActorId.Should().Be("definition-1");
        payload.InputPayload.Should().NotBeNull();
        payload.InputPayload.Is(Struct.Descriptor).Should().BeTrue();
    }
}
