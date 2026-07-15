using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Pins <see cref="AgentRunReplyStepCredentials.StripRuntimeCredentials"/>: every plaintext NyxID
/// token on all four credential sub-messages is cleared before the per-step waterline is committed,
/// while identity/routing facts survive, the input is not mutated, and the sub-message objects
/// themselves are left in place (so structural owner-fallback checks keep working).
/// </summary>
public sealed class AgentRunReplyStepCredentialsTests
{
    private static AgentRunReplyStepState BuildStateWithTokens() => new()
    {
        RunId = "run-1",
        LlmControl = new LLMControlContextPayload
        {
            CredentialRef = "user-token",
            OrganizationCredentialRef = "org-token",
            SenderCredentialRef = "sender-token",
            NyxIdRoutePreference = "/api/v1/proxy/s/owner",
        },
        ToolContext = new AgentToolExecutionContextPayload
        {
            Credentials = new AgentToolCredentialsPayload
            {
                CredentialRef = "user-token",
                OrganizationCredentialRef = "org-token",
                SenderCredentialRef = "sender-token",
            },
            SenderBinding = new AgentToolSenderBindingContextPayload { BindingId = "bnd-1" },
            Caller = new AgentToolCallerContextPayload { OwnerSubject = "owner-subj", ScopeId = "scope-1" },
            Routing = new LLMRequestRoutingContextPayload { ModelOverride = "gpt-x" },
        },
        OwnerFallbackLlmControl = new LLMControlContextPayload
        {
            CredentialRef = "owner-token",
            OrganizationCredentialRef = "owner-org-token",
            SenderCredentialRef = "leaked-sender",
        },
        OwnerFallbackToolContext = new AgentToolExecutionContextPayload
        {
            Credentials = new AgentToolCredentialsPayload
            {
                CredentialRef = "owner-token",
                OrganizationCredentialRef = "owner-org-token",
                SenderCredentialRef = "leaked-sender",
            },
        },
    };

    [Fact]
    public void StripRuntimeCredentials_ClearsEveryTokenOnAllFourSubMessages()
    {
        var stripped = AgentRunReplyStepCredentials.StripRuntimeCredentials(BuildStateWithTokens());

        stripped.LlmControl.CredentialRef.Should().BeEmpty();
        stripped.LlmControl.OrganizationCredentialRef.Should().BeEmpty();
        stripped.LlmControl.SenderCredentialRef.Should().BeEmpty();
        stripped.ToolContext.Credentials.CredentialRef.Should().BeEmpty();
        stripped.ToolContext.Credentials.OrganizationCredentialRef.Should().BeEmpty();
        stripped.ToolContext.Credentials.SenderCredentialRef.Should().BeEmpty();
        stripped.OwnerFallbackLlmControl.CredentialRef.Should().BeEmpty();
        stripped.OwnerFallbackLlmControl.OrganizationCredentialRef.Should().BeEmpty();
        stripped.OwnerFallbackLlmControl.SenderCredentialRef.Should().BeEmpty();
        stripped.OwnerFallbackToolContext.Credentials.CredentialRef.Should().BeEmpty();
        stripped.OwnerFallbackToolContext.Credentials.OrganizationCredentialRef.Should().BeEmpty();
        stripped.OwnerFallbackToolContext.Credentials.SenderCredentialRef.Should().BeEmpty();
    }

    [Fact]
    public void StripRuntimeCredentials_PreservesIdentityAndRoutingFacts()
    {
        var stripped = AgentRunReplyStepCredentials.StripRuntimeCredentials(BuildStateWithTokens());

        stripped.RunId.Should().Be("run-1");
        stripped.LlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        stripped.ToolContext.SenderBinding.BindingId.Should().Be("bnd-1");
        stripped.ToolContext.Caller.OwnerSubject.Should().Be("owner-subj");
        stripped.ToolContext.Caller.ScopeId.Should().Be("scope-1");
        stripped.ToolContext.Routing.ModelOverride.Should().Be("gpt-x");
    }

    [Fact]
    public void StripRuntimeCredentials_DoesNotMutateInput()
    {
        var original = BuildStateWithTokens();

        AgentRunReplyStepCredentials.StripRuntimeCredentials(original);

        original.LlmControl.CredentialRef.Should().Be("user-token");
        original.ToolContext.Credentials.SenderCredentialRef.Should().Be("sender-token");
        original.OwnerFallbackLlmControl.CredentialRef.Should().Be("owner-token");
    }

    [Fact]
    public void StripRuntimeCredentials_KeepsSubMessagesInPlaceForStructuralChecks()
    {
        var stripped = AgentRunReplyStepCredentials.StripRuntimeCredentials(BuildStateWithTokens());

        // The owner-fallback trigger gates on sub-message presence, not token strings.
        stripped.OwnerFallbackLlmControl.Should().NotBeNull();
        stripped.OwnerFallbackToolContext.Should().NotBeNull();
    }

    [Fact]
    public void StripRuntimeCredentials_ToleratesMissingSubMessages()
    {
        var minimal = new AgentRunReplyStepState { RunId = "run-2" };

        var strip = () => AgentRunReplyStepCredentials.StripRuntimeCredentials(minimal);

        strip.Should().NotThrow();
        strip().RunId.Should().Be("run-2");
    }
}
