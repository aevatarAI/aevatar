using Aevatar.AI.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class SystemSkillOverlayRefreshEventProtoTests
{
    [Fact]
    public void SystemSkillOverlayRefreshFiredEvent_ShouldRoundTripAttempt()
    {
        var evt = new SystemSkillOverlayRefreshFiredEvent
        {
            Attempt = 2,
        };

        var parsed = SystemSkillOverlayRefreshFiredEvent.Parser.ParseFrom(evt.ToByteArray());

        parsed.Attempt.Should().Be(2);
        AiMessagesReflection.Descriptor.MessageTypes
            .Should()
            .Contain(x => x.Name == nameof(SystemSkillOverlayRefreshFiredEvent));
    }
}
