using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Application;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Application;

public class RunScriptCommandAdapterTests
{
    [Fact]
    public void Map_ShouldProduceEventEnvelope_WithRunScriptRequestedEvent()
    {
        var adapter = new RunScriptCommandAdapter();

        var envelope = adapter.Map(
            new RunScriptCommand("run-1", "{}", "r1"),
            "actor-1");

        envelope.Payload.Should().NotBeNull();
        envelope.Payload!.TypeUrl.Should().Contain(nameof(RunScriptRequestedEvent));
        envelope.Direction.Should().Be(EventDirection.Self);
        envelope.PublisherId.Should().Be("scripting.application");
        envelope.TargetActorId.Should().Be("actor-1");
    }
}
