using System.Net.Http;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

// Owner fallback converts the turn into a degraded no-tools step, so eligibility must come
// from typed exception facts only (2026-07 incident, funnel B): message-substring routing of
// plain InvalidOperationException let tool-discovery and unrelated plumbing failures silently
// strip a bound sender's whole tool surface.
public sealed class LlmOwnerFallbackPolicyTests
{
    [Theory]
    [InlineData("Invalid schema for function 'aevatar_observe_run': schema must have type 'object' and not have 'oneOf' at the top level (HTTP 400).")]
    [InlineData("tool discovery failed while listing skills")]
    [InlineData("NyxID route '/api/v1/proxy/s/sender' proxy handshake failed")]
    [InlineData("sender token binding not found or revoked (401)")]
    [InlineData("Per-step agent run execution requires a step-capable reply generator.")]
    public void IsRetryable_PlainInvalidOperationException_IsNotRetryableWhateverTheMessageSays(string message)
    {
        LlmOwnerFallbackPolicy.IsRetryable(new InvalidOperationException(message)).Should().BeFalse();
    }

    [Theory]
    [InlineData(NyxIdUpstreamFailureKind.RequestRejected, 400)]
    [InlineData(NyxIdUpstreamFailureKind.AuthenticationFailed, 401)]
    [InlineData(NyxIdUpstreamFailureKind.RateLimited, 429)]
    [InlineData(NyxIdUpstreamFailureKind.ServiceUnavailable, 503)]
    [InlineData(NyxIdUpstreamFailureKind.UpstreamServerError, 500)]
    [InlineData(NyxIdUpstreamFailureKind.ProviderError, null)]
    public void IsRetryable_TypedUpstreamFailure_IsRetryable(NyxIdUpstreamFailureKind kind, int? status)
    {
        var upstream = new NyxIdUpstreamException(kind, status, "nyxid", "some-model", "classified upstream failure");

        LlmOwnerFallbackPolicy.IsRetryable(upstream).Should().BeTrue();
    }

    [Fact]
    public void IsRetryable_AuthenticationRequired_IsRetryable()
    {
        LlmOwnerFallbackPolicy.IsRetryable(new NyxIdAuthenticationRequiredException("nyxid")).Should().BeTrue();
    }

    [Fact]
    public void IsRetryable_TransportFailures_AreRetryable()
    {
        LlmOwnerFallbackPolicy.IsRetryable(new HttpRequestException("connection reset")).Should().BeTrue();
        LlmOwnerFallbackPolicy.IsRetryable(new TimeoutException()).Should().BeTrue();
        LlmOwnerFallbackPolicy.IsRetryable(new JsonException("truncated body")).Should().BeTrue();
        LlmOwnerFallbackPolicy.IsRetryable(new IOException("stream closed")).Should().BeTrue();
    }

    [Fact]
    public void IsRetryable_Cancellation_DistinguishesUserCancelFromClientTimeout()
    {
        // HttpClient timeout: TaskCanceledException without a user-cancelled token.
        LlmOwnerFallbackPolicy.IsRetryable(new TaskCanceledException()).Should().BeTrue();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        LlmOwnerFallbackPolicy.IsRetryable(new TaskCanceledException("cancelled", null, cts.Token)).Should().BeFalse();
        LlmOwnerFallbackPolicy.IsRetryable(new OperationCanceledException()).Should().BeFalse();
    }
}
