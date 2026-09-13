using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelConnectedServiceCredentialPolicyTests
{
    [Fact]
    public void Bound_sender_remains_the_connected_service_authority_even_when_token_is_unavailable()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            SenderBinding = new AgentToolSenderBindingContext("binding-a", "nyx-user-a", "tenant-a"),
            Credentials = new AgentToolCredentials("registration-key", null, null),
        };

        var result = ChannelConnectedServiceCredentialPolicy.Apply(
            context,
            senderToken: null,
            registrationAgentKey: "registration-key");

        result.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        result.Credentials.NyxIdAccessToken.Should().BeNull();
        result.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        result.CredentialSource.Should().Be(AgentToolCredentialSource.BearerToken);
        result.DurableNyxIdCredential.Should().BeNull();
    }

    [Fact]
    public void Unbound_channel_uses_registration_agent_key_for_connected_services()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            Channel = new AgentToolChannelContext("telegram", "sender-a", "scope-a", "message-a", "platform-message-a"),
        };

        var result = ChannelConnectedServiceCredentialPolicy.Apply(
            context,
            senderToken: null,
            registrationAgentKey: "registration-key");

        result.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.AgentKey);
        result.Credentials.NyxIdAccessToken.Should().Be("registration-key");
        result.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
    }
}
