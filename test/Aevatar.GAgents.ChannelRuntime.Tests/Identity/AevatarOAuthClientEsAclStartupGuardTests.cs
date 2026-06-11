using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class AevatarOAuthClientEsAclStartupGuardTests
{
    [Fact]
    public async Task StartAsync_WhenElasticsearchEnabledWithoutAclAssertion_ShouldFailClosed()
    {
        var configuration = BuildConfiguration(esEnabled: true, aclAsserted: false);
        await using var provider = BuildProvider(configuration);
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GrantMatchesGrainEventStoreInternal=true*");
    }

    [Fact]
    public async Task StartAsync_WhenElasticsearchEnabledWithAclAssertion_ShouldPass()
    {
        var configuration = BuildConfiguration(esEnabled: true, aclAsserted: true);
        await using var provider = BuildProvider(configuration);
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WhenElasticsearchDisabled_ShouldNotRequireAclAssertion()
    {
        var configuration = BuildConfiguration(esEnabled: false, aclAsserted: false);
        await using var provider = BuildProvider(configuration);
        var guard = ActivatorUtilities.CreateInstance<AevatarOAuthClientEsAclStartupGuard>(provider);

        Func<Task> act = () => guard.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static IConfiguration BuildConfiguration(bool esEnabled, bool aclAsserted) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = esEnabled.ToString(),
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://127.0.0.1:9200",
                [$"{AevatarOAuthClientEsAclOptions.SectionName}:GrantMatchesGrainEventStoreInternal"] = aclAsserted.ToString(),
            })
            .Build();

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddOptions<AevatarOAuthClientEsAclOptions>()
            .Bind(configuration.GetSection(AevatarOAuthClientEsAclOptions.SectionName));
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
