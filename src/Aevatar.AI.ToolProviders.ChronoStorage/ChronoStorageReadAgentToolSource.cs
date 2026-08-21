using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ChronoStorage.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.ChronoStorage;

/// <summary>Read-only ChronoStorage capability source.</summary>
public sealed class ChronoStorageReadAgentToolSource : IAgentToolSource
{
    private readonly ChronoStorageToolOptions _options;
    private readonly ChronoStorageApiClient _client;
    private readonly ILogger _logger;

    public ChronoStorageReadAgentToolSource(
        ChronoStorageToolOptions options,
        ChronoStorageApiClient client,
        ILogger<ChronoStorageReadAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<ChronoStorageReadAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        IReadOnlyList<IAgentTool> tools =
        [
            new ChronoGlobTool(_client),
            new ChronoGrepTool(_client),
            new ChronoFileReadTool(_client),
            new ChronoTreeTool(_client),
            new ChronoDiffTool(_client),
        ];
        _logger.LogInformation("ChronoStorage read tools registered ({Count} tools)", tools.Count);
        return Task.FromResult(tools);
    }
}
