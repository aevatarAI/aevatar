using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
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
            NyxIdAccessToken = "user-token",
            NyxIdOrgToken = "org-token",
            SenderNyxIdAccessToken = "sender-token",
            NyxIdRoutePreference = "/api/v1/proxy/s/owner",
        },
        ToolContext = new AgentToolExecutionContextPayload
        {
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "user-token",
                NyxIdOrgToken = "org-token",
                SenderNyxIdAccessToken = "sender-token",
            },
            SenderBinding = new AgentToolSenderBindingContextPayload { BindingId = "bnd-1" },
            Caller = new AgentToolCallerContextPayload { OwnerSubject = "owner-subj", ScopeId = "scope-1" },
            Routing = new LLMRequestRoutingContextPayload { ModelOverride = "gpt-x" },
        },
        OwnerFallbackLlmControl = new LLMControlContextPayload
        {
            NyxIdAccessToken = "owner-token",
            NyxIdOrgToken = "owner-org-token",
            SenderNyxIdAccessToken = "leaked-sender",
        },
        OwnerFallbackToolContext = new AgentToolExecutionContextPayload
        {
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "owner-token",
                NyxIdOrgToken = "owner-org-token",
                SenderNyxIdAccessToken = "leaked-sender",
            },
        },
    };

    [Fact]
    public void StripRuntimeCredentials_ClearsEveryTokenOnAllFourSubMessages()
    {
        var stripped = AgentRunReplyStepCredentials.StripRuntimeCredentials(BuildStateWithTokens());

        stripped.LlmControl.NyxIdAccessToken.Should().BeEmpty();
        stripped.LlmControl.NyxIdOrgToken.Should().BeEmpty();
        stripped.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        stripped.ToolContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
        stripped.ToolContext.Credentials.NyxIdOrgToken.Should().BeEmpty();
        stripped.ToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
        stripped.OwnerFallbackLlmControl.NyxIdAccessToken.Should().BeEmpty();
        stripped.OwnerFallbackLlmControl.NyxIdOrgToken.Should().BeEmpty();
        stripped.OwnerFallbackLlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        stripped.OwnerFallbackToolContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
        stripped.OwnerFallbackToolContext.Credentials.NyxIdOrgToken.Should().BeEmpty();
        stripped.OwnerFallbackToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
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
    public void StripRuntimeCredentials_ScrubsOwnedCredentialMetadataFromPersistedStepState()
    {
        var original = BuildStateWithTokens();
        original.ExternalMetadata[LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-user-token";
        original.ExternalMetadata[LLMRequestMetadataKeys.NyxIdOrgToken] = "metadata-org-token";
        original.ExternalMetadata[LLMRequestMetadataKeys.SenderNyxIdAccessToken] = "metadata-sender-token";
        original.ExternalMetadata["trace-id"] = "trace-1";
        original.ToolContext.ExternalMetadata[LLMRequestMetadataKeys.NyxIdAccessToken] = "tool-user-token";
        original.ToolContext.ExternalMetadata["tool-trace"] = "tool-trace-1";
        original.OwnerFallbackToolContext.ExternalMetadata[LLMRequestMetadataKeys.NyxIdOrgToken] = "owner-org-token";
        original.OwnerFallbackToolContext.ExternalMetadata["fallback-trace"] = "fallback-trace-1";

        var stripped = AgentRunReplyStepCredentials.StripRuntimeCredentials(original);

        stripped.ExternalMetadata.Should().ContainKey("trace-id").WhoseValue.Should().Be("trace-1");
        stripped.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        stripped.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdOrgToken);
        stripped.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
        stripped.ToolContext.ExternalMetadata.Should().ContainKey("tool-trace").WhoseValue.Should().Be("tool-trace-1");
        stripped.ToolContext.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        stripped.OwnerFallbackToolContext.ExternalMetadata.Should().ContainKey("fallback-trace").WhoseValue.Should().Be("fallback-trace-1");
        stripped.OwnerFallbackToolContext.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdOrgToken);
    }

    [Fact]
    public void StripRuntimeCredentials_DoesNotMutateInput()
    {
        var original = BuildStateWithTokens();

        AgentRunReplyStepCredentials.StripRuntimeCredentials(original);

        original.LlmControl.NyxIdAccessToken.Should().Be("user-token");
        original.ToolContext.Credentials.SenderNyxIdAccessToken.Should().Be("sender-token");
        original.OwnerFallbackLlmControl.NyxIdAccessToken.Should().Be("owner-token");
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
