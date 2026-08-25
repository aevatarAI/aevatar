using System.Collections.ObjectModel;

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Typed reason why request-local tool discovery could not produce one exact catalog.
/// </summary>
public enum AgentToolDiscoveryFailureCode
{
    SourceFailed = 0,
    InvalidToolName = 1,
    ToolNameCollision = 2,
}

/// <summary>
/// A fail-closed discovery failure. Source identities are diagnostic type names only; caller,
/// credential, connection, and request facts are never retained here.
/// </summary>
public sealed record AgentToolDiscoveryFailure(
    AgentToolDiscoveryFailureCode Code,
    string ToolName,
    string SourceType,
    string ConflictingSourceType,
    string Detail);

/// <summary>
/// One exact tool and the source that produced it during this request.
/// </summary>
public sealed record AgentToolDiscoveryEntry(IAgentTool Tool, string SourceType);

public sealed class AgentToolDiscoveryResult
{
    private AgentToolDiscoveryResult(
        IReadOnlyList<AgentToolDiscoveryEntry> entries,
        AgentToolDiscoveryFailure? failure)
    {
        Entries = entries;
        Failure = failure;
    }

    public bool IsSuccess => Failure is null;

    public IReadOnlyList<AgentToolDiscoveryEntry> Entries { get; }

    public IReadOnlyList<IAgentTool> Tools => Entries.Select(static entry => entry.Tool).ToArray();

    public AgentToolDiscoveryFailure? Failure { get; }

    public static AgentToolDiscoveryResult Success(IReadOnlyList<AgentToolDiscoveryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new AgentToolDiscoveryResult(entries, null);
    }

    public static AgentToolDiscoveryResult Failed(AgentToolDiscoveryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new AgentToolDiscoveryResult([], failure);
    }
}

public sealed class AgentToolDiscoveryException : InvalidOperationException
{
    public AgentToolDiscoveryException(AgentToolDiscoveryFailure failure, Exception? innerException = null)
        : base(failure?.Detail, innerException)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public AgentToolDiscoveryFailure Failure { get; }
}

/// <summary>
/// Materializes request-local tool sources under one typed execution context. Implementations must
/// not cache caller, connection, authority, or discovered tool facts across calls.
/// </summary>
public interface IAgentToolDiscoveryService
{
    Task<AgentToolDiscoveryResult> DiscoverAsync(
        IEnumerable<IAgentToolSource> sources,
        AgentToolExecutionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// The shared request-local discovery implementation used after tool-set topology resolution.
/// </summary>
public sealed class AgentToolDiscoveryService : IAgentToolDiscoveryService
{
    public static AgentToolDiscoveryService Instance { get; } = new();

    public async Task<AgentToolDiscoveryResult> DiscoverAsync(
        IEnumerable<IAgentToolSource> sources,
        AgentToolExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(context);

        var sourceSnapshot = sources.ToArray();
        var exactTools = new Dictionary<string, AgentToolDiscoveryEntry>(StringComparer.OrdinalIgnoreCase);
        var registeredToolCount = 0;
        using var _ = AgentToolContextScope.Push(context);
        foreach (var source in sourceSnapshot)
        {
            if (source is null)
            {
                var failure = new AgentToolDiscoveryFailure(
                    AgentToolDiscoveryFailureCode.SourceFailed,
                    string.Empty,
                    "<null>",
                    string.Empty,
                    "Tool discovery source cannot be null.");
                RecordFailure(failure, registeredToolCount, exactTools.Count);
                return AgentToolDiscoveryResult.Failed(failure);
            }

            var sourceType = source.GetType().FullName ?? source.GetType().Name;
            IReadOnlyList<IAgentTool> discovered;
            try
            {
                discovered = await source.DiscoverToolsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failure = new AgentToolDiscoveryFailure(
                    AgentToolDiscoveryFailureCode.SourceFailed,
                    string.Empty,
                    sourceType,
                    string.Empty,
                    $"Tool discovery failed for source '{sourceType}': {ex.GetType().Name}.");
                RecordFailure(failure, registeredToolCount, exactTools.Count);
                return AgentToolDiscoveryResult.Failed(failure);
            }

            if (discovered is null)
            {
                var failure = new AgentToolDiscoveryFailure(
                    AgentToolDiscoveryFailureCode.SourceFailed,
                    string.Empty,
                    sourceType,
                    string.Empty,
                    $"Tool source '{sourceType}' returned a null discovery result.");
                RecordFailure(failure, registeredToolCount, exactTools.Count);
                return AgentToolDiscoveryResult.Failed(failure);
            }

            registeredToolCount += discovered.Count;
            foreach (var tool in discovered)
            {
                if (tool is null || string.IsNullOrWhiteSpace(tool.Name))
                {
                    var failure = new AgentToolDiscoveryFailure(
                        AgentToolDiscoveryFailureCode.InvalidToolName,
                        string.Empty,
                        sourceType,
                        string.Empty,
                        $"Tool source '{sourceType}' returned a tool with an empty name.");
                    RecordFailure(failure, registeredToolCount, exactTools.Count);
                    return AgentToolDiscoveryResult.Failed(failure);
                }

                var name = tool.Name.Trim();
                if (!exactTools.TryGetValue(name, out var existing))
                {
                    exactTools.Add(name, new AgentToolDiscoveryEntry(tool, sourceType));
                    continue;
                }

                if (ReferenceEquals(existing.Tool, tool))
                    continue;

                var collision = new AgentToolDiscoveryFailure(
                    AgentToolDiscoveryFailureCode.ToolNameCollision,
                    existing.Tool.Name.Trim(),
                    existing.SourceType,
                    sourceType,
                    $"Tool name '{name}' resolved to different exact objects from " +
                    $"'{existing.SourceType}' and '{sourceType}'.");
                RecordFailure(collision, registeredToolCount, exactTools.Count);
                return AgentToolDiscoveryResult.Failed(collision);
            }
        }

        var entries = exactTools.Values
            .OrderBy(static entry => entry.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Tool.Name, StringComparer.Ordinal)
            .ToArray();
        AgentTurnToolCatalogTelemetry.RecordDiscovery(
            registeredToolCount,
            entries.Length,
            "accepted");
        return AgentToolDiscoveryResult.Success(new ReadOnlyCollection<AgentToolDiscoveryEntry>(entries));
    }

    private static void RecordFailure(
        AgentToolDiscoveryFailure failure,
        int registeredToolCount,
        int discoveredToolCount)
    {
        AgentTurnToolCatalogTelemetry.RecordDiscovery(
            registeredToolCount,
            discoveredToolCount,
            "rejected",
            failure.Code.ToString());
        AgentTurnToolCatalogTelemetry.RecordRejected(failure.Code.ToString(), "discovery");
    }
}
