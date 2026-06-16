using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class AevatarOAuthClientEsAclStartupGuardTests
{
    [Fact]
    public async Task StartAsync_WhenAclAssertionMissing_ShouldFailClosed()
    {
        await using var provider = BuildProvider(aclAsserted: false);
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GrantMatchesGrainEventStoreInternal=true*");
    }

    [Fact]
    public async Task StartAsync_WhenAclAssertionPresent_ShouldPass()
    {
        await using var provider = BuildProvider(aclAsserted: true);
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static ServiceProvider BuildProvider(bool aclAsserted)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new AevatarOAuthClientEsAclOptions
        {
            GrantMatchesGrainEventStoreInternal = aclAsserted,
        }));
        services.AddSingleton<IAevatarOAuthClientProvider, AevatarOAuthClientProjectionProvider>();
        services.AddSingleton<IProjectionDocumentReader<AevatarOAuthClientDocument, string>, NoOpOAuthClientDocumentReader>();
        services.AddSingleton<IProjectionDocumentWriter<AevatarOAuthClientDocument>, NoOpOAuthClientDocumentWriter>();
        services.AddSingleton<ICurrentStateProjectionMaterializer<AevatarOAuthClientMaterializationContext>, NoOpOAuthClientProjector>();
        return services.BuildServiceProvider();
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
