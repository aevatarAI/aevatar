using Aevatar.ChatRouting.Abstractions;
using FluentAssertions;

namespace Aevatar.ChatRouting.Core.Tests;

public sealed class ChatRouteActionTargetsTests
{
    [Fact]
    public void ForwardToVoiceAttachTarget_ShouldBuildTypedForwardToModelAction()
    {
        var action = ChatRouteActionTargets.ForwardToVoiceAttachTarget(
            " voice-agent-1 ",
            " voice_presence_openai ",
            "workspace.voice");

        action.ActionCase.Should().Be(ChatRouteAction.ActionOneofCase.ForwardToModel);
        action.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.voice");
        action.ForwardToModel.ToolChoiceHint.PrefilledArguments.Should().BeNull();
        action.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.ActorId.Should().Be("voice-agent-1");
        action.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.VoiceModuleName.Should().Be("voice_presence_openai");
    }

    [Fact]
    public void ForwardToVoiceAttachTarget_WhenOptionalArgumentsAreOmitted_ShouldUseDefaultToolSetAndEmptyModule()
    {
        var action = ChatRouteActionTargets.ForwardToVoiceAttachTarget("voice-agent-1");

        action.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.default");
        action.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.ActorId.Should().Be("voice-agent-1");
        action.ForwardToModel.ToolChoiceHint.VoiceAttachTarget.VoiceModuleName.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForwardToVoiceAttachTarget_WhenActorIdIsMissing_ShouldReject(string? actorId)
    {
        var act = () => ChatRouteActionTargets.ForwardToVoiceAttachTarget(actorId!);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("actorId");
    }

    [Fact]
    public void TryGetVoiceAttachTarget_WhenTypedTargetHasActorId_ShouldReturnTarget()
    {
        var action = ChatRouteActionTargets.ForwardToVoiceAttachTarget(
            "voice-agent-1",
            "voice_presence_openai");

        var found = ChatRouteActionTargets.TryGetVoiceAttachTarget(action, out var target);

        found.Should().BeTrue();
        target.Should().BeSameAs(action.ForwardToModel.ToolChoiceHint.VoiceAttachTarget);
        target.ActorId.Should().Be("voice-agent-1");
        target.VoiceModuleName.Should().Be("voice_presence_openai");
    }

    [Theory]
    [MemberData(nameof(ActionsWithoutVoiceAttachTarget))]
    public void TryGetVoiceAttachTarget_WhenTypedTargetIsMissingOrBlank_ShouldReturnFalse(ChatRouteAction? action)
    {
        var found = ChatRouteActionTargets.TryGetVoiceAttachTarget(action, out var target);

        found.Should().BeFalse();
        target.ActorId.Should().BeEmpty();
        target.VoiceModuleName.Should().BeEmpty();
    }

    public static IEnumerable<object?[]> ActionsWithoutVoiceAttachTarget()
    {
        yield return [null];
        yield return [new ChatRouteAction()];
        yield return
        [
            new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel
                {
                    ToolChoiceHint = new ChatRouteToolChoiceHint(),
                },
            },
        ];
        yield return
        [
            new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel
                {
                    ToolChoiceHint = new ChatRouteToolChoiceHint
                    {
                        VoiceAttachTarget = new ChatRouteVoiceAttachTarget
                        {
                            ActorId = "   ",
                            VoiceModuleName = "voice_presence_openai",
                        },
                    },
                },
            },
        ];
    }
}
