using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Core.Auditing;
using Aevatar.AI.Core.Middleware;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Bootstrap.Extensions.AI;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Bootstrap.Tests;

public sealed class AevatarAIFeaturesToolExecutionAuditObserverTests
{
    [Fact]
    public void AddAevatarAIFeatures_WhenAuditDependenciesExist_ShouldRegisterToolExecutionAuditObserver()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAuditTrailAppender, RecordingAuditTrailAppender>()
            .AddSingleton<IAuditActorIdentityHasher, StableAuditActorIdentityHasher>();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options => options.EnableMEAIProviders = false);

        using var provider = services.BuildServiceProvider();
        var middlewares = provider.GetServices<IToolCallMiddleware>().ToList();

        middlewares.Should().ContainSingle(middleware => middleware is ToolExecutionAuditMiddleware);
        provider.GetRequiredService<ToolAuditRecordFactory>().Should().NotBeNull();
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenAuditAppenderIsMissing_ShouldKeepToolMiddlewareResolutionAvailable()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options => options.EnableMEAIProviders = false);

        using var provider = services.BuildServiceProvider();
        var middlewares = provider.GetServices<IToolCallMiddleware>().ToList();

        middlewares.Should().NotContain(middleware => middleware is ToolExecutionAuditMiddleware);
        middlewares.Should().NotContain(middleware => middleware is ToolApprovalMiddleware);
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId, record.AuditActorId, DateTimeOffset.UtcNow));
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"audit:{canonicalActorKey}", "test-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            string.Equals(auditActorId, $"audit:{canonicalActorKey}", StringComparison.Ordinal) &&
            string.Equals(identityKeyId, "test-key", StringComparison.Ordinal);
    }
}
