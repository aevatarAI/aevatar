using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// NyxID tool source. Provides tools for managing services, credentials,
/// nodes, approvals, and making proxied requests through NyxID.
/// Also hosts channel_registrations tool (lazy-resolved via IServiceProvider
/// because its dependencies may not be in this assembly's DI scope).
/// </summary>
public sealed class NyxIdAgentToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _client;
    private readonly IServiceDiscoveryCache _cache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    public NyxIdAgentToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient client,
        IServiceProvider serviceProvider,
        IServiceDiscoveryCache? cache = null,
        ILogger<NyxIdAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _serviceProvider = serviceProvider;
        _cache = cache ?? new InMemoryServiceDiscoveryCache();
        _logger = logger ?? NullLogger<NyxIdAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogDebug("NyxID base URL not configured, skipping NyxID tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        IReadOnlyList<IAgentTool> tools =
        [
            new NyxIdAccountTool(_client),
            new NyxIdStatusTool(_client),
            new NyxIdProfileTool(_client),
            new NyxIdMfaTool(_client),
            new NyxIdSessionsTool(_client),
            new NyxIdCatalogTool(_client),
            new NyxIdServicesTool(_client),
            new NyxIdProxyTool(_client, _cache, _logger),
            new NyxIdCodeExecuteTool(_client, _logger),
            new NyxIdApiKeysTool(_client),
            new NyxIdNodesTool(_client),
            new NyxIdApprovalsTool(_client),
            new NyxIdEndpointsTool(_client),
            new NyxIdExternalKeysTool(_client),
            new NyxIdNotificationsTool(_client),
            new NyxIdLlmStatusTool(_client),
            new NyxIdProvidersTool(_client),
            new NyxIdChannelBotsDeprecatedStub(),
        ];

        // Lazy-resolve channel_registrations tool — its dependencies (IChannelBotRegistrationQueryPort,
        // IActorRuntime) live in the ChannelRuntime assembly, not here. Resolve via IServiceProvider
        // at discovery time so we don't create a hard assembly dependency.
        var channelTool = TryCreateChannelRegistrationTool();
        if (channelTool != null)
        {
            tools = [..tools, channelTool];
            _logger.LogInformation("channel_registrations tool available");
        }
        else
        {
            _logger.LogWarning("channel_registrations tool NOT available — IChannelBotRegistrationQueryPort or IActorRuntime not registered");
        }

        _logger.LogInformation(
            "NyxID tools registered ({Count} tools, base URL: {BaseUrl})",
            tools.Count, _options.BaseUrl);

        return Task.FromResult(tools);
    }

    /// <summary>
    /// Resolve channel_registrations tool via IServiceProvider.
    /// Returns null if dependencies are not registered (ChannelRuntime not configured).
    /// Uses reflection to avoid hard assembly reference from NyxId.ToolProviders → ChannelRuntime.
    /// </summary>
    private IAgentTool? TryCreateChannelRegistrationTool()
    {
        try
        {
            // Look up the tool type by name — ChannelRuntime assembly may not be loaded
            var toolType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return []; }
                })
                .FirstOrDefault(t => t.Name == "ChannelRegistrationTool" &&
                                     typeof(IAgentTool).IsAssignableFrom(t));

            if (toolType == null)
            {
                _logger.LogDebug("ChannelRegistrationTool type not found in loaded assemblies");
                return null;
            }

            return ActivatorUtilities.CreateInstance(_serviceProvider, toolType) as IAgentTool;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create ChannelRegistrationTool");
            return null;
        }
    }
}
