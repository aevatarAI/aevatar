using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Application;

public class UpsertScriptDefinitionCommandAdapterTests
{
    [Fact]
    public void Map_ShouldProduceEventEnvelope_WithUpsertScriptDefinitionRequestedEvent()
    {
        var adapter = new UpsertScriptDefinitionCommandAdapter();

        var envelope = adapter.Map(
            new UpsertScriptDefinitionCommand(
                ScriptId: "script-1",
                ScriptRevision: "rev-1",
                SourceText: "return 1;",
                SourceHash: "hash-1"),
            "definition-actor-1");

        envelope.Payload.Should().NotBeNull();
        envelope.Payload!.TypeUrl.Should().Contain(nameof(UpsertScriptDefinitionRequestedEvent));
        envelope.Direction.Should().Be(EventDirection.Self);
        envelope.TargetActorId.Should().Be("definition-actor-1");

        var payload = envelope.Payload.Unpack<UpsertScriptDefinitionRequestedEvent>();
        payload.ScriptId.Should().Be("script-1");
        payload.ScriptRevision.Should().Be("rev-1");
        payload.SourceText.Should().Be("return 1;");
        payload.SourceHash.Should().Be("hash-1");
    }
}
