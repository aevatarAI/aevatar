using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.Bridge;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class BridgeCallbackTokenServiceTests
{
    [Fact]
    public void IssueAndValidate_ShouldRoundtripClaims()
    {
        var service = CreateService(
            new WorkflowBridgeOptions
            {
                TokenSigningKey = "bridge-sign-key",
                MaxTokenTtlMs = 60_000,
            });
        var now = DateTimeOffset.UtcNow;

        var issued = service.Issue(
            new BridgeCallbackTokenIssueRequest
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "wait-1",
                SignalName = "openclaw_reply",
                TimeoutMs = 10_000,
                ChannelId = "telegram:group",
                SessionId = "session-1",
                Metadata = new Dictionary<string, string>
                {
                    ["tenant"] = "demo",
                },
            },
            now);

        var ok = service.TryValidate(issued.Token, now.AddSeconds(1), out var claims, out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        claims.ActorId.Should().Be("actor-1");
        claims.RunId.Should().Be("run-1");
        claims.StepId.Should().Be("wait-1");
        claims.SignalName.Should().Be("openclaw_reply");
        claims.ChannelId.Should().Be("telegram:group");
        claims.SessionId.Should().Be("session-1");
        claims.Metadata.Should().ContainKey("tenant");
    }

    [Fact]
    public void TryValidate_WhenTokenExpired_ShouldReturnFalseAndExposeClaims()
    {
        var service = CreateService(
            new WorkflowBridgeOptions
            {
                TokenSigningKey = "bridge-sign-key",
                MaxTokenTtlMs = 60_000,
            });
        var now = DateTimeOffset.UtcNow;
        var issued = service.Issue(
            new BridgeCallbackTokenIssueRequest
            {
                ActorId = "actor-2",
                RunId = "run-2",
                StepId = "wait-2",
                SignalName = "approval",
                TimeoutMs = 1_000,
            },
            now);

        var ok = service.TryValidate(issued.Token, now.AddSeconds(5), out var claims, out var error);

        ok.Should().BeFalse();
        error.Should().Be("token expired");
        claims.TokenId.Should().NotBeNullOrWhiteSpace();
        claims.ActorId.Should().Be("actor-2");
    }

    private static HmacBridgeCallbackTokenService CreateService(WorkflowBridgeOptions options)
    {
        var monitor = new FixedOptionsMonitor<WorkflowBridgeOptions>(options);
        return new HmacBridgeCallbackTokenService(monitor);
    }

    private sealed class FixedOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    {
        public FixedOptionsMonitor(TOptions currentValue)
        {
            CurrentValue = currentValue;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
