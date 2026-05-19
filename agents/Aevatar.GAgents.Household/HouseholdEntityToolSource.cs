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
    private readonly IActorDispatchPort _dispatchPort;
    private readonly HouseholdEntityToolOptions _options;
    private readonly ILogger _logger;

    public HouseholdEntityToolSource(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        HouseholdEntityToolOptions options,
        ILogger<HouseholdEntityToolSource>? logger = null)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
        _options = options;
        _logger = logger ?? NullLogger<HouseholdEntityToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        // Refactor (iter2/cluster-007):
        //   Old pattern: the tool source passed only runtime, forcing direct actor invocation.
        //   New principle: the tool receives lifecycle and dispatch ports as separate contracts.
        IReadOnlyList<IAgentTool> tools =
        [
            new HouseholdEntityTool(_runtime, _dispatchPort, _options, _logger),
        ];

        _logger.LogInformation("Household entity tool registered (actor prefix: {Prefix})",
            _options.ActorIdPrefix);

        return Task.FromResult(tools);
    }
}
