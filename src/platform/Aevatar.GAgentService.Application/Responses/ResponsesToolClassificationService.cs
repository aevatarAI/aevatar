using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Responses;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

public sealed record ResponsesApplicationToolDeclaration(
    string Name,
    string Description,
    string ParametersJson,
    string SchemaHash);

public sealed record ResponsesToolClassification(
    IReadOnlyList<ResponsesApplicationToolDeclaration> ForwardedTools,
    IReadOnlyList<IAgentTool> EffectiveTools,
    IReadOnlyList<string> SubstitutedToolNames,
    IReadOnlyList<string> AdditiveToolNames,
    IReadOnlyList<string> OwnedToolNames);

public interface IResponsesToolClassificationService
{
    ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        ResponsesToolProviderContext context,
        IEnumerable<IResponsesToolProvider>? additionalProviders = null,
        CancellationToken ct = default);
}

public sealed class ResponsesToolClassificationService(
    IEnumerable<IResponsesToolProvider> toolProviders,
    ILogger<ResponsesToolClassificationService> logger) : IResponsesToolClassificationService
{
    public ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        ResponsesToolProviderContext context,
        IEnumerable<IResponsesToolProvider>? additionalProviders = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaredTools);
        ArgumentNullException.ThrowIfNull(context);

        var effectiveProviders = additionalProviders is null
            ? toolProviders
            : toolProviders.Concat(additionalProviders);
        return ResponsesToolClassifier.ClassifyAsync(
            declaredTools,
            effectiveProviders,
            context,
            logger,
            ct);
    }
}

public static class ResponsesToolClassifier
{
    public static async ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        IEnumerable<IResponsesToolProvider> providers,
        ResponsesToolProviderContext context,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(declaredTools);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        var providerList = providers as IReadOnlyList<IResponsesToolProvider>
                           ?? providers.ToArray();

        var substituteToolsByName = new Dictionary<string, (IAgentTool Tool, string ProviderType)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providerList)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                AddExactToolsOrThrow(
                    substituteToolsByName,
                    await provider.GetSubstituteToolsAsync(context, ct).ConfigureAwait(false),
                    provider.GetType().FullName ?? provider.GetType().Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AgentToolDiscoveryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Responses substitute tool discovery failed for provider {ProviderType}; continuing without that provider.",
                    provider.GetType().Name);
            }
        }

        var substituteTools = substituteToolsByName.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Tool,
            StringComparer.OrdinalIgnoreCase);

        var additiveToolsByName = new Dictionary<string, (IAgentTool Tool, string ProviderType)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providerList)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                AddExactToolsOrThrow(
                    additiveToolsByName,
                    await provider.GetAdditiveToolsAsync(context, ct).ConfigureAwait(false),
                    provider.GetType().FullName ?? provider.GetType().Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AgentToolDiscoveryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Responses additive tool discovery failed for provider {ProviderType}; continuing without that provider.",
                    provider.GetType().Name);
            }
        }

        var additiveTools = additiveToolsByName.Values.Select(static value => value.Tool).ToArray();
        var ownedToolNames = substituteTools.Values
            .Concat(additiveTools)
            .Select(static tool => tool.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownedToolNameSet = ownedToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var forwarded = new List<ResponsesApplicationToolDeclaration>();
        var effective = new List<IAgentTool>();
        var substitutedNames = new List<string>();

        foreach (var declaration in declaredTools)
        {
            if (!ownedToolNameSet.Contains(declaration.Name))
            {
                forwarded.Add(declaration);
                effective.Add(new ResponsesForwardedTool(declaration));
                continue;
            }

            if (substituteTools.TryGetValue(declaration.Name, out var substitute))
            {
                substitutedNames.Add(declaration.Name);
                if (!string.Equals(
                        ResponsesToolSchemaHasher.Compute(substitute.ParametersSchema),
                        declaration.SchemaHash,
                        StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Responses substitute tool {ToolName} schema differs from client declaration; using Aevatar tool schema.",
                        declaration.Name);
                }

                effective.Add(substitute);
            }
        }

        var effectiveNames = new HashSet<string>(
            effective.Select(static tool => tool.Name),
            StringComparer.OrdinalIgnoreCase);
        var addedAdditiveNames = new List<string>();
        foreach (var additive in additiveTools)
        {
            if (!effectiveNames.Add(additive.Name))
            {
                logger.LogWarning(
                    "Responses additive tool {ToolName} skipped because an effective tool with the same name already exists.",
                    additive.Name);
                continue;
            }

            effective.Add(additive);
            addedAdditiveNames.Add(additive.Name);
        }

        return new ResponsesToolClassification(
            forwarded,
            effective,
            substitutedNames,
            addedAdditiveNames,
            ownedToolNames);
    }

    private static void AddExactToolsOrThrow(
        Dictionary<string, (IAgentTool Tool, string ProviderType)> exactTools,
        IReadOnlyList<IAgentTool> tools,
        string providerType)
    {
        foreach (var tool in tools)
        {
            if (tool is null || string.IsNullOrWhiteSpace(tool.Name))
            {
                throw new AgentToolDiscoveryException(new AgentToolDiscoveryFailure(
                    AgentToolDiscoveryFailureCode.InvalidToolName,
                    string.Empty,
                    providerType,
                    string.Empty,
                    $"Responses tool provider '{providerType}' returned a tool with an empty name."));
            }

            var name = tool.Name.Trim();
            if (!exactTools.TryGetValue(name, out var existing))
            {
                exactTools.Add(name, (tool, providerType));
                continue;
            }

            if (ReferenceEquals(existing.Tool, tool))
                continue;

            throw new AgentToolDiscoveryException(new AgentToolDiscoveryFailure(
                AgentToolDiscoveryFailureCode.ToolNameCollision,
                existing.Tool.Name.Trim(),
                existing.ProviderType,
                providerType,
                $"Responses tool name '{name}' resolved to different exact objects from " +
                $"'{existing.ProviderType}' and '{providerType}'."));
        }
    }
}

internal sealed class ResponsesForwardedTool : IAgentTool
{
    public ResponsesForwardedTool(ResponsesApplicationToolDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        Name = declaration.Name;
        Description = declaration.Description;
        ParametersSchema = declaration.ParametersJson;
        SchemaHash = declaration.SchemaHash;
    }

    public string Name { get; }

    public string Description { get; }

    public string ParametersSchema { get; }

    public string SchemaHash { get; }

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Forwarded Responses tool '{Name}' must be executed by the client, not by Aevatar.");
}

internal static class ResponsesToolSchemaHasher
{
    public static string Compute(string parametersJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(parametersJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
