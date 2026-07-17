using Aevatar.AI.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AIMessageDescriptorContractTests
{
    [Fact]
    public void MessageDescriptor_ShouldContainStableMessageTypes()
    {
        AiMessagesReflection.Descriptor.Should().NotBeNull();
        AiMessagesReflection.Descriptor.MessageTypes.Select(static type => type.Name).Should().Contain(
            [
                nameof(ChatRequestEvent),
                nameof(ChatResponseEvent),
                nameof(TextMessageStartEvent),
                nameof(TextMessageContentEvent),
                nameof(TextMessageReasoningEvent),
                nameof(TextMessageEndEvent),
                nameof(ToolCallEvent),
                nameof(ToolResultEvent),
                nameof(RoleChatSessionStartedEvent),
                nameof(RoleChatSessionCompletedEvent),
                nameof(InitializeRoleAgentEvent),
                nameof(AIAgentConfigOverrides),
                nameof(RoleChatSessionState),
                nameof(RoleGAgentState),
            ]);
    }

    [Fact]
    public void ExactRemoteReferences_ShouldRoundTripStableWireFields()
    {
        var skillRef = RoundTrip(
            new ExactRemoteSkillRef
            {
                Guid = "11111111-1111-1111-1111-111111111111",
                LiteralVersion = "1.2",
            },
            ExactRemoteSkillRef.Parser);
        var skillsetRef = RoundTrip(
            new ExactRemoteSkillsetRef
            {
                Guid = "22222222-2222-2222-2222-222222222222",
                LiteralVersion = "3.4",
            },
            ExactRemoteSkillsetRef.Parser);

        (skillRef.Guid, skillRef.LiteralVersion).Should()
            .Be(("11111111-1111-1111-1111-111111111111", "1.2"));
        (skillsetRef.Guid, skillsetRef.LiteralVersion).Should()
            .Be(("22222222-2222-2222-2222-222222222222", "3.4"));
        foreach (var descriptor in new[] { ExactRemoteSkillRef.Descriptor, ExactRemoteSkillsetRef.Descriptor })
        {
            descriptor.Fields.InFieldNumberOrder().Select(static field => (field.FieldNumber, field.Name))
                .Should().Equal((1, "guid"), (2, "literal_version"));
            descriptor.Oneofs.Should().BeEmpty();
        }
    }

    private static T RoundTrip<T>(T message, MessageParser<T> parser)
        where T : class, IMessage<T>, new()
    {
        var bytes = message.ToByteArray();
        var parsed = parser.ParseFrom(bytes);
        parsed.Should().Be(message);

        var merged = new T();
        merged.MergeFrom(message);
        merged.Should().Be(message);

        return parsed;
    }
}
