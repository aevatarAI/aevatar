using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    IReadOnlyList<string> OwnedToolNames)
{
    public AgentTurnToolCatalog? OwnedCatalog { get; init; }
}

public interface IResponsesToolClassificationService
{
    ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        ResponsesToolProviderContext context,
        IEnumerable<IResponsesToolProvider>? additionalProviders = null,
        CancellationToken ct = default);

    ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        ResponsesToolProviderContext context,
        IEnumerable<IResponsesToolProvider>? additionalProviders,
        AgentTurnToolCatalog ownedCatalog,
        CancellationToken ct = default) =>
        ValueTask.FromResult(ResponsesToolClassifier.ClassifyAgainstFrozenCatalogWithoutSubstitutes(
            declaredTools,
            ownedCatalog));
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
        => ClassifyCoreAsync(declaredTools, context, additionalProviders, ownedCatalog: null, ct);

    public ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        ResponsesToolProviderContext context,
        IEnumerable<IResponsesToolProvider>? additionalProviders,
        AgentTurnToolCatalog ownedCatalog,
        CancellationToken ct = default) =>
        ClassifyCoreAsync(declaredTools, context, additionalProviders, ownedCatalog, ct);

    private ValueTask<ResponsesToolClassification> ClassifyCoreAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        ResponsesToolProviderContext context,
        IEnumerable<IResponsesToolProvider>? additionalProviders,
        AgentTurnToolCatalog? ownedCatalog,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(declaredTools);
        ArgumentNullException.ThrowIfNull(context);

        var effectiveProviders = additionalProviders is null
            ? toolProviders
            : toolProviders.Concat(additionalProviders);
        return ownedCatalog is null
            ? ResponsesToolClassifier.ClassifyAsync(
                declaredTools,
                effectiveProviders,
                context,
                logger,
                ct)
            : ResponsesToolClassifier.ClassifyAsync(
                declaredTools,
                effectiveProviders,
                context,
                logger,
                ownedCatalog,
                ct);
    }
}

public static class ResponsesToolClassifier
{
    internal static ResponsesToolClassification ClassifyAgainstFrozenCatalogWithoutSubstitutes(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        AgentTurnToolCatalog ownedCatalog) =>
        ClassifyAgainstFrozenCatalog(
            declaredTools,
            new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase),
            ownedCatalog,
            NullLogger.Instance);

    public static ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        IEnumerable<IResponsesToolProvider> providers,
        ResponsesToolProviderContext context,
        ILogger logger,
        CancellationToken ct = default) =>
        ClassifyCoreAsync(declaredTools, providers, context, logger, null, ct);

    public static ValueTask<ResponsesToolClassification> ClassifyAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        IEnumerable<IResponsesToolProvider> providers,
        ResponsesToolProviderContext context,
        ILogger logger,
        AgentTurnToolCatalog ownedCatalog,
        CancellationToken ct = default) =>
        ClassifyCoreAsync(declaredTools, providers, context, logger, ownedCatalog, ct);

    private static async ValueTask<ResponsesToolClassification> ClassifyCoreAsync(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        IEnumerable<IResponsesToolProvider> providers,
        ResponsesToolProviderContext context,
        ILogger logger,
        AgentTurnToolCatalog? ownedCatalog,
        CancellationToken ct)
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

        if (ownedCatalog is not null)
        {
            return ClassifyAgainstFrozenCatalog(
                declaredTools,
                substituteToolsByName.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.Tool,
                    StringComparer.OrdinalIgnoreCase),
                ownedCatalog,
                logger);
        }

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

    private static ResponsesToolClassification ClassifyAgainstFrozenCatalog(
        IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        IReadOnlyDictionary<string, IAgentTool> substituteTools,
        AgentTurnToolCatalog ownedCatalog,
        ILogger logger)
    {
        var catalogTools = ownedCatalog.ExactTools;
        ownedCatalog.AssertProofMatchesExactTools(catalogTools.Values);
        var descriptorByName = ownedCatalog.Proof.ToolDescriptors.ToDictionary(
            static descriptor => descriptor.Name,
            StringComparer.OrdinalIgnoreCase);
        var consumedCanonicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finalSelections = new List<AgentTurnToolSelection>();
        var forwarded = new List<ResponsesApplicationToolDeclaration>();
        var effective = new List<IAgentTool>();
        var substitutedNames = new List<string>();

        foreach (var declaration in declaredTools)
        {
            var canonicalName = ResolveAuthorizedCanonicalName(declaration.Name, catalogTools);
            if (canonicalName is null)
            {
                forwarded.Add(declaration);
                effective.Add(new ResponsesForwardedTool(declaration));
                continue;
            }
            if (!consumedCanonicalNames.Add(canonicalName))
            {
                throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                    AgentTurnToolCatalogFailureCode.ToolNameCollision,
                    $"Responses declarations map more than once to owned tool '{canonicalName}'.",
                    canonicalName));
            }

            if (substituteTools.TryGetValue(declaration.Name, out var substitute))
            {
                if (!string.Equals(
                        ResponsesToolSchemaHasher.Compute(substitute.ParametersSchema),
                        declaration.SchemaHash,
                        StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Responses substitute tool {ToolName} schema differs from client declaration; using Aevatar tool schema.",
                        declaration.Name);
                }

                substitutedNames.Add(substitute.Name);
                effective.Add(substitute);
                finalSelections.Add(new AgentTurnToolSelection(
                    substitute,
                    AgentTurnToolOrigin.ResponsesState));
                continue;
            }

            var exact = catalogTools[canonicalName];
            var descriptor = descriptorByName[canonicalName];
            effective.Add(exact);
            finalSelections.Add(new AgentTurnToolSelection(
                exact,
                descriptor.Origin,
                descriptor.SelectorDigest));
        }

        var additiveNames = new List<string>();
        foreach (var pair in catalogTools.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (consumedCanonicalNames.Contains(pair.Key))
                continue;

            var descriptor = descriptorByName[pair.Key];
            effective.Add(pair.Value);
            additiveNames.Add(pair.Value.Name);
            finalSelections.Add(new AgentTurnToolSelection(
                pair.Value,
                descriptor.Origin,
                descriptor.SelectorDigest));
        }

        var adaptedCatalog = new AgentTurnToolCatalog(
            finalSelections.Select(static selection => selection.Tool.Name),
            ownedCatalog.ProfilePromptLayer,
            ownedCatalog.SelectedSkillPromptLayer,
            ownedCatalog.SelectedIntentId,
            ownedCatalog.CandidateIntentId,
            ownedCatalog.Diagnostics,
            finalSelections,
            ownedCatalog.HasUnresolvedConnectedServiceSelectors,
            ownedCatalog.RequiredToolInvocation,
            ownedCatalog.Budget);
        var ownedNames = adaptedCatalog.ExactTools.Values
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var forwardedSchemaBytes = forwarded.Sum(static declaration =>
            Encoding.UTF8.GetByteCount(declaration.ParametersJson ?? string.Empty));
        AgentTurnToolCatalogTelemetry.RecordForwarded(
            forwarded.Count,
            forwardedSchemaBytes,
            "ingress");
        logger.LogInformation(
            "Responses ingress tool catalog frozen. owned={OwnedCount} ownedSchemaBytes={OwnedSchemaBytes} forwarded={ForwardedCount} forwardedSchemaBytes={ForwardedSchemaBytes} combined={CombinedCount} combinedSchemaBytes={CombinedSchemaBytes} digest={CatalogDigest}",
            adaptedCatalog.Proof.ToolCount,
            adaptedCatalog.Proof.SchemaBytes,
            forwarded.Count,
            forwardedSchemaBytes,
            adaptedCatalog.Proof.ToolCount + forwarded.Count,
            adaptedCatalog.Proof.SchemaBytes + forwardedSchemaBytes,
            adaptedCatalog.Proof.CatalogDigest);

        return new ResponsesToolClassification(
            forwarded,
            effective,
            substitutedNames,
            additiveNames,
            ownedNames)
        {
            OwnedCatalog = adaptedCatalog,
        };
    }

    private static string? ResolveAuthorizedCanonicalName(
        string declaredName,
        IReadOnlyDictionary<string, IAgentTool> catalogTools)
    {
        if (catalogTools.ContainsKey(declaredName))
            return catalogTools.Keys.Single(name =>
                string.Equals(name, declaredName, StringComparison.OrdinalIgnoreCase));

        var aliasTarget = declaredName.Trim() switch
        {
            "WebSearch" => "web_search",
            "WebFetch" => "web_fetch",
            _ => null,
        };
        return aliasTarget is not null && catalogTools.ContainsKey(aliasTarget)
            ? aliasTarget
            : null;
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
