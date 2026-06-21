using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

// 06-20-observatory-run-state-feed (R2d): the chat ingress derives the run scope_id from the authenticated
// caller claim. When a trusted (claim-derived) scope is present it is authoritative — the body-supplied
// scopeId must NOT override it (closes a scope-spoofing hole and ensures the materialized current-state doc
// is attributed to the caller's observatory). Without a caller claim the body scopeId is used as before.
public sealed class ChatRunRequestNormalizerScopeFromClaimTests
{
    [Fact]
    public void Normalize_ShouldUseTrustedScopeId_OverBodyScopeId()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            ScopeId = "body-scope",
        };

        var result = ChatRunRequestNormalizer.Normalize(input, trustedScopeId: " caller-claim-scope ");

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("caller-claim-scope");
    }

    [Fact]
    public void Normalize_ShouldFallBackToBodyScopeId_WhenNoTrustedScope()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            ScopeId = "body-scope",
        };

        var result = ChatRunRequestNormalizer.Normalize(input, trustedScopeId: null);

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("body-scope");
    }

    [Fact]
    public void Normalize_ShouldUseTrustedScopeId_WhenBodyScopeIdMissing()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
        };

        var result = ChatRunRequestNormalizer.Normalize(input, trustedScopeId: "caller-claim-scope");

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("caller-claim-scope");
    }
}
