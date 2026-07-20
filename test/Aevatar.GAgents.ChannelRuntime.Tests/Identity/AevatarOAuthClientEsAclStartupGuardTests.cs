using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class AevatarOAuthClientEsAclStartupGuardTests
{
    [Fact]
    public async Task StartAsync_WhenDisabled_ShouldNoOp_EvenWithoutProjectionWiring()
    {
        // Disabled must short-circuit before any probe or structural resolution,
        // so a bare provider (no projection providers, no probe) still passes.
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new AevatarOAuthClientEsAclOptions
        {
            EnforcementMode = AevatarOAuthClientEsAclEnforcementMode.Disabled,
        }));
        await using var provider = services.BuildServiceProvider();
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WhenWarn_AndProbeUnrestricted_ShouldLogWarningButNotThrow()
    {
        var recordingLogger = new RecordingLogger<AevatarOAuthClientEsAclStartupGuard>();
        await using var provider = BuildProvider(
            mode: AevatarOAuthClientEsAclEnforcementMode.Warn,
            aclAsserted: true,
            probe: new FakeOAuthClientEsAclProbe(
                EsAclProbeResult.Unrestricted("test: security disabled, index world-readable")));
        var guard = new AevatarOAuthClientEsAclStartupGuard(
            provider,
            provider.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>(),
            recordingLogger);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        recordingLogger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("world-readable"));
    }

    [Fact]
    public async Task StartAsync_WhenWarn_AndProbeUnavailable_ShouldLogWarningButNotThrow()
    {
        var recordingLogger = new RecordingLogger<AevatarOAuthClientEsAclStartupGuard>();
        await using var provider = BuildProvider(
            mode: AevatarOAuthClientEsAclEnforcementMode.Warn,
            aclAsserted: true,
            probe: new FakeOAuthClientEsAclProbe(
                EsAclProbeResult.Unavailable("test: InMemory provider, no cluster")));
        var guard = new AevatarOAuthClientEsAclStartupGuard(
            provider,
            provider.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>(),
            recordingLogger);

        await guard.Invoking(g => g.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
        recordingLogger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_WhenWarn_AndProbeRestricted_WithAttestation_ShouldNotWarn()
    {
        var recordingLogger = new RecordingLogger<AevatarOAuthClientEsAclStartupGuard>();
        await using var provider = BuildProvider(
            mode: AevatarOAuthClientEsAclEnforcementMode.Warn,
            aclAsserted: true,
            probe: new FakeOAuthClientEsAclProbe(
                EsAclProbeResult.Restricted("test: security enforcing, 2 role mappings")));
        var guard = new AevatarOAuthClientEsAclStartupGuard(
            provider,
            provider.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>(),
            recordingLogger);

        await guard.Invoking(g => g.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
        recordingLogger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_WhenStrict_AndProbeUnrestricted_ShouldThrow()
    {
        await using var provider = BuildProvider(
            mode: AevatarOAuthClientEsAclEnforcementMode.Strict,
            aclAsserted: true,
            probe: new FakeOAuthClientEsAclProbe(
                EsAclProbeResult.Unrestricted("test: security disabled")));
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        await guard.Invoking(g => g.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*probeStatus=Unrestricted*");
    }

    [Fact]
    public async Task StartAsync_WhenStrict_AndAttestationMissing_ShouldThrow()
    {
        // Even with a Restricted probe, a false attestation still fails closed in Strict.
        await using var provider = BuildProvider(
            mode: AevatarOAuthClientEsAclEnforcementMode.Strict,
            aclAsserted: false,
            probe: new FakeOAuthClientEsAclProbe(
                EsAclProbeResult.Restricted("test: security enforcing")));
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        await guard.Invoking(g => g.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GrantMatchesGrainEventStoreInternal*");
    }

    [Fact]
    public async Task StartAsync_WhenStrict_AndProbeRestricted_WithAttestation_ShouldNotThrow()
    {
        await using var provider = BuildProvider(
            mode: AevatarOAuthClientEsAclEnforcementMode.Strict,
            aclAsserted: true,
            probe: new FakeOAuthClientEsAclProbe(
                EsAclProbeResult.Restricted("test: security enforcing")));
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        await guard.Invoking(g => g.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(EsAclProbeStatus.Unverifiable)]
    [InlineData(EsAclProbeStatus.Unavailable)]
    public async Task StartAsync_WhenStrict_AndProbeIsNotPositive_WithAttestation_ShouldThrow(
        EsAclProbeStatus probeStatus)
    {
        await using var provider = BuildProvider(
            mode: AevatarOAuthClientEsAclEnforcementMode.Strict,
            aclAsserted: true,
            probe: new FakeOAuthClientEsAclProbe(
                new EsAclProbeResult(probeStatus, "test: ACL restriction could not be confirmed")));
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        await guard.Invoking(g => g.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*probeStatus={probeStatus}*");
    }

    private static ServiceProvider BuildProvider(
        AevatarOAuthClientEsAclEnforcementMode mode,
        bool aclAsserted,
        IOAuthClientEsAclProbe probe)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new AevatarOAuthClientEsAclOptions
        {
            EnforcementMode = mode,
            GrantMatchesGrainEventStoreInternal = aclAsserted,
        }));
        services.AddSingleton(Options.Create(new AevatarOAuthClientOptions
        {
            ClientId = "configured-client-id",
        }));
        services.AddSingleton(probe);
        services.AddSingleton<Aevatar.Foundation.Abstractions.Credentials.ISecretVault,
            Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault>();
        services.AddSingleton<IAevatarOAuthClientProvider, AevatarOAuthClientProjectionProvider>();
        services.AddSingleton<IProjectionDocumentReader<AevatarOAuthClientDocument, string>, NoOpOAuthClientDocumentReader>();
        services.AddSingleton<IProjectionDocumentWriter<AevatarOAuthClientDocument>, NoOpOAuthClientDocumentWriter>();
        services.AddSingleton<ICurrentStateProjectionMaterializer<AevatarOAuthClientMaterializationContext>, NoOpOAuthClientProjector>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeOAuthClientEsAclProbe : IOAuthClientEsAclProbe
    {
        private readonly EsAclProbeResult _result;

        public FakeOAuthClientEsAclProbe(EsAclProbeResult result) => _result = result;

        public Task<EsAclProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed record RecordedLogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<RecordedLogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new RecordedLogEntry(logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class NoOpOAuthClientDocumentReader : IProjectionDocumentReader<AevatarOAuthClientDocument, string>
    {
        public Task<AevatarOAuthClientDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<AevatarOAuthClientDocument?>(null);
        }

        public Task<ProjectionDocumentQueryResult<AevatarOAuthClientDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionDocumentQueryResult<AevatarOAuthClientDocument>.Empty);
        }
    }

    private sealed class NoOpOAuthClientDocumentWriter : IProjectionDocumentWriter<AevatarOAuthClientDocument>
    {
        public Task<ProjectionWriteResult> UpsertAsync(
            AevatarOAuthClientDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class NoOpOAuthClientProjector
        : ICurrentStateProjectionMaterializer<AevatarOAuthClientMaterializationContext>
    {
        public ValueTask ProjectAsync(
            AevatarOAuthClientMaterializationContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
