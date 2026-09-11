using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelNyxIdAgentKeyReadinessPortTests
{
    [Fact]
    public async Task EnsureReadyAsync_ValidVaultCredential_ReturnsReadyWithoutRemoteInspection()
    {
        var vault = new InMemorySecretVault();
        var durable = await StoreAsync(vault, "nyxid_ag_secret_alpha");
        var port = CreatePort(
            vault,
            new RecordingLogger<ChannelNyxIdAgentKeyReadinessPort>());

        var result = await port.EnsureReadyAsync(durable);

        result.Ready.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureReadyAsync_MismatchedVaultDescriptor_FailsClosedBeforeNyxCall()
    {
        var vault = new InMemorySecretVault();
        var durable = await StoreAsync(vault, "nyxid_ag_secret_alpha");
        durable.SecretReference.Fingerprint = "mismatched-fingerprint";
        var port = CreatePort(
            vault,
            new RecordingLogger<ChannelNyxIdAgentKeyReadinessPort>());

        var result = await port.EnsureReadyAsync(durable);

        result.Ready.Should().BeFalse();
        result.FailureCode.Should().Be("channel_agent_key_unavailable");
    }

    private static async Task<DurableCallerCredentialRef> StoreAsync(
        ISecretVault vault,
        string rawAgentKey)
    {
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            "scope-alpha",
            "key-alpha",
            rawAgentKey,
            "test-store"));
        return new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = stored.Reference.Purpose,
            OwnerScopeKey = stored.Reference.OwnerScopeKey,
            SubjectId = "key-alpha",
            SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
            SecretReference = stored.Reference.Clone(),
        };
    }

    private static ChannelNyxIdAgentKeyReadinessPort CreatePort(
        ISecretVault vault,
        ILogger<ChannelNyxIdAgentKeyReadinessPort> logger) =>
        new(vault, logger);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
