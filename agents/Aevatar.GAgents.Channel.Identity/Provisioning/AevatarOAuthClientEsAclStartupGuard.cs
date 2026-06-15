using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity;

internal sealed class AevatarOAuthClientEsAclStartupGuard : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly AevatarOAuthClientEsAclOptions _options;
    private readonly ILogger<AevatarOAuthClientEsAclStartupGuard> _logger;

    public AevatarOAuthClientEsAclStartupGuard(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IOptions<AevatarOAuthClientEsAclOptions> options,
        ILogger<AevatarOAuthClientEsAclStartupGuard>? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AevatarOAuthClientEsAclStartupGuard>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ElasticsearchProjectionConfiguration.IsEnabled(_configuration, storeName: "ChannelIdentity"))
            return Task.CompletedTask;

        if (!_options.GrantMatchesGrainEventStoreInternal)
        {
            throw new InvalidOperationException(
                "AevatarOAuthClient Elasticsearch projection contains state-token HMAC keys. " +
                $"When ChannelIdentity uses Elasticsearch, set {AevatarOAuthClientEsAclOptions.SectionName}:GrantMatchesGrainEventStoreInternal=true " +
                "only after the aevatar-oauth-clients index read grant is limited to grain/event-store internal services.");
        }

        using var scope = _serviceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        var oauthClientProvider = scopedProvider.GetRequiredService<IAevatarOAuthClientProvider>();
        if (oauthClientProvider.GetType() != typeof(AevatarOAuthClientProjectionProvider))
        {
            throw new InvalidOperationException(
                "AevatarOAuthClient ES ACL guard requires IAevatarOAuthClientProvider to be the internal projection provider.");
        }

        _ = scopedProvider.GetRequiredService<IProjectionDocumentReader<AevatarOAuthClientDocument, string>>();
        _ = scopedProvider.GetRequiredService<IProjectionDocumentWriter<AevatarOAuthClientDocument>>();
        _ = scopedProvider.GetRequiredService<ICurrentStateProjectionMaterializer<AevatarOAuthClientMaterializationContext>>();

        _logger.LogInformation(
            "AevatarOAuthClient ES ACL startup guard passed. index={IndexName}",
            AevatarOAuthClientDocumentMetadataProvider.IndexName);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
