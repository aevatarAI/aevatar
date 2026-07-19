using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;

namespace Aevatar.AI.Infrastructure.OpenSandbox.Tests;

public sealed class NyxIdManagedCodexCredentialProviderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IssueAsync_RequestsOnlyLlmProxyAndRequiresExecutionLifetime()
    {
        var broker = new RecordingBroker
        {
            Result = new CapabilityHandle
            {
                AccessToken = "delegated-token",
                ExpiresAtUnix = Now.AddMinutes(5).ToUnixTimeSeconds(),
            },
        };
        var provider = new NyxIdManagedCodexCredentialProvider(broker, new FixedTimeProvider(Now));

        var result = await provider.IssueAsync(
            new CodexExecutionNyxIdAuthority("nyxid", "tenant-alpha", "user-alpha"),
            180);

        result.AccessToken.Should().Be("delegated-token");
        broker.Scope!.Value.Should().Be("llm:proxy");
        broker.Subject!.Platform.Should().Be("nyxid");
        broker.Subject.ExternalUserId.Should().Be("user-alpha");
    }

    [Fact]
    public async Task IssueAsync_WhenTokenExpiresInsideRunWindow_FailsClosed()
    {
        var broker = new RecordingBroker
        {
            Result = new CapabilityHandle
            {
                AccessToken = "short-token",
                ExpiresAtUnix = Now.AddSeconds(190).ToUnixTimeSeconds(),
            },
        };
        var provider = new NyxIdManagedCodexCredentialProvider(broker, new FixedTimeProvider(Now));

        var act = () => provider.IssueAsync(
            new CodexExecutionNyxIdAuthority("nyxid", string.Empty, "user-alpha"),
            180);

        (await act.Should().ThrowAsync<ManagedCodexCredentialException>())
            .Which.Failure.Code.Should().Be("llm_credential_lifetime_insufficient");
    }

    [Fact]
    public async Task IssueAsync_WhenBrokerRejectsCapabilityScope_ReturnsTypedSetupFailure()
    {
        var broker = new RecordingBroker
        {
            Exception = new BindingScopeMismatchException(new ExternalSubjectRef
            {
                Platform = "nyxid",
                ExternalUserId = "user-alpha",
            }),
        };
        var provider = new NyxIdManagedCodexCredentialProvider(broker, new FixedTimeProvider(Now));

        var act = () => provider.IssueAsync(
            new CodexExecutionNyxIdAuthority("nyxid", string.Empty, "user-alpha"),
            180);

        (await act.Should().ThrowAsync<ManagedCodexCredentialException>())
            .Which.Failure.Code.Should().Be("llm_proxy_scope_missing");
    }

    private sealed class RecordingBroker : INyxIdCapabilityBroker
    {
        public CapabilityHandle Result { get; init; } = new();
        public Exception? Exception { get; init; }
        public ExternalSubjectRef? Subject { get; private set; }
        public CapabilityScope? Scope { get; private set; }

        public Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default)
        {
            Subject = externalSubject.Clone();
            Scope = scope.Clone();
            return Exception == null
                ? Task.FromResult(Result)
                : Task.FromException<CapabilityHandle>(Exception);
        }

        public Task<BindingChallenge> StartExternalBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RevokeBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CapabilityScope scope,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
