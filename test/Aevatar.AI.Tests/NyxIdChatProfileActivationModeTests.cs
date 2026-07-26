using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatProfileActivationModeTests
{
    [Fact]
    public void Per_turn_chat_contract_should_not_allow_client_profile_or_activation_override()
    {
        ChatRequestEvent.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .Should()
            .NotContain(name =>
                name.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("activation_mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Direct_chat_agent_public_surface_should_not_expose_profile_switching_commands()
    {
        typeof(NyxIdChatGAgent)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .SelectMany(static method => method.GetParameters())
            .Select(static parameter => parameter.Name ?? string.Empty)
            .Should()
            .NotContain(name =>
                name.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("activationMode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Conversation_create_contract_should_only_accept_immutable_execution_binding()
    {
        NyxIdChatConversationCreateCommand.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Contain("agent_profile_binding").And.NotContain("agent_profile");
    }

    [Fact]
    public void Conversation_profile_source_should_be_async_binding_only_contract()
    {
        var method = typeof(INyxIdChatAgentProfileBindingSource)
            .GetMethod(nameof(INyxIdChatAgentProfileBindingSource.ResolveForNewConversationAsync));

        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<NyxIdChatAgentProfileBindingResult>));
        method.GetParameters().Select(static parameter => parameter.Name).Should().Equal(
            "actorId",
            "routeToolSetName",
            "ct");
        typeof(INyxIdChatAgentProfileBindingSource).Assembly.GetTypes()
            .Should()
            .NotContain(type => type.Name.Contains("AgentProfileSnapshotSource", StringComparison.Ordinal));
    }
}
