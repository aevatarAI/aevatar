using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ChronoStorage.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.ChronoStorage;

/// <summary>Mutating ChronoStorage capability source.</summary>
public sealed class ChronoStorageWriteAgentToolSource : IAgentToolSource
{
    private readonly ChronoStorageToolOptions _options;
    private readonly ChronoStorageApiClient _client;
    private readonly ILogger _logger;

    public ChronoStorageWriteAgentToolSource(
        ChronoStorageToolOptions options,
        ChronoStorageApiClient client,
        ILogger<ChronoStorageWriteAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<ChronoStorageWriteAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        IReadOnlyList<IAgentTool> tools =
        [
            new ChronoFileWriteTool(_client),
            new ChronoFileEditTool(_client),
        ];
        _logger.LogInformation("ChronoStorage write tools registered ({Count} tools)", tools.Count);
        return Task.FromResult(tools);
    }
}
