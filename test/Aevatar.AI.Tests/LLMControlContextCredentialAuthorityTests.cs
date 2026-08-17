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
}
