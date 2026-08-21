using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.ChatRouting.Core.Tests;

public sealed class ChatRoutePolicyProtoTests
{
    [Fact]
    public void ChatRouteActionDescriptor_ShouldExposeOnlyCurrentActionFields()
    {
        var actionOneof = ChatRouteAction.Descriptor.Oneofs
            .Single(oneof => oneof.Name == "action");

        var actionFieldNames = actionOneof.Fields
            .Select(field => field.Name)
            .ToArray();

        actionFieldNames.Should().BeEquivalentTo("forward_to_model", "reject");
        ChatRouteAction.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .Should()
            .NotContain(new[]
            {
                "forward_to_gagent",
                "forward_to_team",
                "forward_to_workflow",
                "bypass",
            });
    }

    [Fact]
    public void ForwardToModel_ShouldRoundTripToolSetRefAndToolChoiceHint()
    {
        var action = new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ModelName = "chrono-llm/gpt-5.5",
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
                ProfileKind = ChatRouteAgentProfileKind.WorkspaceChat,
                ProfileRef = new ChatRouteAgentProfileRef
                {
                    OwnerKind = ChatRouteAgentProfileReferenceOwnerKind.System,
                    ProfileSlug = "workspace-default",
                },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = "aevatar_invoke_gagent",
                    VoiceAttachTarget = new ChatRouteVoiceAttachTarget
                    {
                        ActorId = "voice-agent-1",
                        VoiceModuleName = "voice_presence_openai",
                        SessionOverrides = new VoiceSessionOverrides
                        {
                            Voice = "verse",
                            SampleRateHz = 16000,
                            TurnDetectionMode = VoiceTurnDetectionMode.Disabled,
                        },
                    },
                    PrefilledArguments = new Struct
                    {
                        Fields =
                        {
                            ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString("actor-1"),
                            ["wait"] = Google.Protobuf.WellKnownTypes.Value.ForString("stream"),
                        },
                    },
                },
            },
        };

        var parsed = ChatRouteAction.Parser.ParseFrom(action.ToByteArray());

        parsed.Should().Be(action);
        parsed.ActionCase.Should().Be(ChatRouteAction.ActionOneofCase.ForwardToModel);
        parsed.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.default");
        parsed.ForwardToModel.ProfileKind.Should().Be(ChatRouteAgentProfileKind.WorkspaceChat);
        parsed.ForwardToModel.ProfileRef.OwnerKind.Should().Be(ChatRouteAgentProfileReferenceOwnerKind.System);
        parsed.ForwardToModel.ProfileRef.ProfileSlug.Should().Be("workspace-default");
        parsed.ForwardToModel.ToolChoiceHint.ToolName.Should().Be("aevatar_invoke_gagent");
        parsed.ForwardToModel.ToolChoiceHint.PrefilledArguments.Fields["actor_id"].StringValue
            .Should()
            .Be("actor-1");
        parsed.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.ActorId.Should().Be("voice-agent-1");
        parsed.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.VoiceModuleName.Should().Be("voice_presence_openai");
        parsed.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.SessionOverrides.Voice.Should().Be("verse");
        parsed.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.SessionOverrides.SampleRateHz.Should().Be(16000);
        parsed.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.SessionOverrides.TurnDetectionMode.Should().Be(VoiceTurnDetectionMode.Disabled);
    }
}
