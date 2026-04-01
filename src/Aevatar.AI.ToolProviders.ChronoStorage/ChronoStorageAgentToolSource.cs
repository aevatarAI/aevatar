using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ChronoStorage.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.ChronoStorage;

/// <summary>
/// ChronoStorage tool source. Provides file browsing and editing tools
/// that let agents use chrono-storage as a codebase.
/// </summary>
public sealed class ChronoStorageAgentToolSource : IAgentToolSource
{
    private readonly ChronoStorageToolOptions _options;
    private readonly ChronoStorageApiClient _client;
    private readonly ILogger _logger;

    public ChronoStorageAgentToolSource(
        ChronoStorageToolOptions options,
        ChronoStorageApiClient client,
        ILogger<ChronoStorageAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<ChronoStorageAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
        {
            _logger.LogDebug("ChronoStorage API base URL not configured, skipping chrono-storage tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        IReadOnlyList<IAgentTool> tools =
        [
            new ChronoGlobTool(_client),
            new ChronoGrepTool(_client),
            new ChronoFileReadTool(_client),
            new ChronoFileWriteTool(_client),
            new ChronoFileEditTool(_client),
        ];

        _logger.LogInformation(
            "ChronoStorage tools registered ({Count} tools, API base URL: {BaseUrl})",
            tools.Count, _options.ApiBaseUrl);

        return Task.FromResult(tools);
    }
}
