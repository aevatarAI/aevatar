using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class LLMControlContextCredentialAuthorityTests
{
    [Fact]
    public void ToToolContext_WithToolExecutionAuthority_ShouldPreserveToolCredential()
    {
        var control = new LLMControlContext(
            "llm-token",
            "llm-org",
            null,
            null,
            null,
            null,
            null);
        var baseToolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = "tool-token",
                NyxIdOrgToken = "tool-org",
                NyxIdCredentialAuthority =
                    AgentToolNyxIdCredentialAuthority.ToolExecutionContext,
            },
        };

        var toolContext = control.ToToolContext(baseToolContext);

        toolContext.Credentials.NyxIdAccessToken.Should().Be("tool-token");
        toolContext.Credentials.NyxIdOrgToken.Should().Be("tool-org");
        toolContext.Credentials.NyxIdCredentialAuthority.Should().Be(
            AgentToolNyxIdCredentialAuthority.ToolExecutionContext);
    }

    [Fact]
    public void ToToolContext_WithUnavailableToolCredential_DoesNotFallBackToLlmCredential()
    {
        var control = new LLMControlContext(
            "registration-agent-key",
            null,
            null,
            null,
            null,
            null,
            null);
        var baseToolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdCredentialKind = AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
                NyxIdCredentialAuthority = AgentToolNyxIdCredentialAuthority.ToolExecutionContext,
            },
        };

        var toolContext = control.ToToolContext(baseToolContext);

        toolContext.Credentials.NyxIdAccessToken.Should().BeNull();
        toolContext.Credentials.NyxIdOrgToken.Should().BeNull();
        toolContext.Credentials.NyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
    }
}
