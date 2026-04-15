using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Household;

/// <summary>
/// Tool source that registers the HouseholdEntity agent-as-tool.
/// Auto-discovered by AIGAgentBase during activation via DI.
/// </summary>
public sealed class HouseholdEntityToolSource : IAgentToolSource
{
    private readonly IActorRuntime _runtime;
    private readonly HouseholdEntityToolOptions _options;
    private readonly ILogger _logger;

    public HouseholdEntityToolSource(
        IActorRuntime runtime,
        HouseholdEntityToolOptions options,
        ILogger<HouseholdEntityToolSource>? logger = null)
    {
        _runtime = runtime;
        _options = options;
        _logger = logger ?? NullLogger<HouseholdEntityToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<IAgentTool> tools =
        [
            new HouseholdEntityTool(_runtime, _options, _logger),
        ];

        _logger.LogInformation("Household entity tool registered (actor prefix: {Prefix})",
            _options.ActorIdPrefix);

        return Task.FromResult(tools);
    }
}
