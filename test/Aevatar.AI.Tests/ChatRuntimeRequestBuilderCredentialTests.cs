using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ChatRuntimeRequestBuilderCredentialTests
{
    [Fact]
    public void Build_WhenToolContextHasAgentKeyCredential_ShouldPromoteCallerBearer()
    {
        var request = BuildRequest(AgentToolNyxIdCredentialKind.AgentKey);

        request.CallerContext.Should().NotBeNull();
        request.CallerContext!.Credentials.Should().NotBeNull();
        request.CallerContext.Credentials!.NyxIdBearer.Should().Be("nyxid_ag_alpha");
    }

    [Fact]
    public void Build_WhenToolContextCredentialKindIsUnspecified_ShouldNotPromoteCallerBearer()
    {
        var request = BuildRequest(AgentToolNyxIdCredentialKind.Unspecified);

        request.CallerContext.Should().NotBeNull();
        request.CallerContext!.Credentials.Should().BeNull();
    }

    private static LLMRequest BuildRequest(AgentToolNyxIdCredentialKind credentialKind)
    {
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                " nyxid_ag_alpha ",
                null,
                null,
                credentialKind),
            Caller = new AgentToolCallerContext(" scope-alpha ", " owner-alpha ", " response-alpha "),
        };

        return ChatRuntimeRequestBuilder.Build(
            new LLMRequest
            {
                Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            },
            null,
            null,
            toolContext,
            null,
            null);
    }
}
