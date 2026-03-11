using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Application;

public class ScriptingActorRequestEnvelopeFactoryTests
{
    [Fact]
    public void Create_ShouldProduceUpsertDefinitionEnvelope_WithTypedPayload()
    {
        var envelope = ScriptingActorRequestEnvelopeFactory.Create(
            "definition-actor-1",
            "rev-1",
            new UpsertScriptDefinitionRequestedEvent
            {
                ScriptId = "script-1",
                ScriptRevision = "rev-1",
                SourceText = "return 1;",
                SourceHash = "hash-1",
            });

        envelope.Payload.Should().NotBeNull();
        envelope.Payload!.TypeUrl.Should().Contain(nameof(UpsertScriptDefinitionRequestedEvent));
        envelope.Route!.Direction.Should().Be(EventDirection.Self);
        envelope.Route.PublisherActorId.Should().Be("scripting.application");
        envelope.Route.TargetActorId.Should().Be("definition-actor-1");
        envelope.Propagation!.CorrelationId.Should().Be("rev-1");

        var payload = envelope.Payload.Unpack<UpsertScriptDefinitionRequestedEvent>();
        payload.ScriptId.Should().Be("script-1");
        payload.ScriptRevision.Should().Be("rev-1");
        payload.SourceText.Should().Be("return 1;");
        payload.SourceHash.Should().Be("hash-1");
    }

    [Fact]
    public void Create_ShouldProduceRunEnvelope_WithTypedPayload()
    {
        var envelope = ScriptingActorRequestEnvelopeFactory.Create(
            "runtime-actor-1",
            "run-1",
            new RunScriptRequestedEvent
            {
                RunId = "run-1",
                InputPayload = Any.Pack(new Struct
                {
                    Fields = { ["caseId"] = Google.Protobuf.WellKnownTypes.Value.ForString("Case-1") },
                }),
                ScriptRevision = "rev-1",
                DefinitionActorId = "definition-1",
            });

        envelope.Payload.Should().NotBeNull();
        envelope.Payload!.TypeUrl.Should().Contain(nameof(RunScriptRequestedEvent));
        envelope.Route!.Direction.Should().Be(EventDirection.Self);
        envelope.Route.PublisherActorId.Should().Be("scripting.application");
        envelope.Route.TargetActorId.Should().Be("runtime-actor-1");
        envelope.Propagation!.CorrelationId.Should().Be("run-1");

        var payload = envelope.Payload.Unpack<RunScriptRequestedEvent>();
        payload.RunId.Should().Be("run-1");
        payload.ScriptRevision.Should().Be("rev-1");
        payload.DefinitionActorId.Should().Be("definition-1");
        payload.InputPayload.Should().NotBeNull();
        payload.InputPayload.Is(Struct.Descriptor).Should().BeTrue();
    }
}
