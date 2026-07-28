using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
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
}
