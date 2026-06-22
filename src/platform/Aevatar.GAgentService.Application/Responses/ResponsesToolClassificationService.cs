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

        var discoveredSubstituteTools = new List<IAgentTool>();
        foreach (var provider in providerList)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                discoveredSubstituteTools.AddRange(
                    await provider.GetSubstituteToolsAsync(context, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

        var substituteTools = discoveredSubstituteTools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        var discoveredAdditiveTools = new List<IAgentTool>();
        foreach (var provider in providerList)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                discoveredAdditiveTools.AddRange(
                    await provider.GetAdditiveToolsAsync(context, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

        var additiveTools = discoveredAdditiveTools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        var ownedToolNames = discoveredSubstituteTools
            .Concat(discoveredAdditiveTools)
            .Select(static tool => tool.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ownedToolNameSet = ownedToolNames.ToHashSet(StringComparer.Ordinal);

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
            StringComparer.Ordinal);
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
