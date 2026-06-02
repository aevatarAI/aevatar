using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
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
    IReadOnlyList<string> AdditiveToolNames);

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

public sealed record ResponsesCompletionResult(
    string Text,
    TokenUsage? Usage,
    IReadOnlyList<ToolCall> ForwardedToolCalls);

public interface IResponsesCompletionApplicationService
{
    Task<ResponsesCompletionResult> CollectAsync(
        ILLMProvider provider,
        LLMRequest request,
        AgentToolExecutionContext toolContext,
        ResponsesToolClassification toolClassification,
        CancellationToken ct = default);

    Task<ResponsesCompletionResult> StreamAsync(
        ILLMProvider provider,
        LLMRequest request,
        AgentToolExecutionContext toolContext,
        ResponsesToolClassification toolClassification,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct = default);
}

public sealed class ResponsesCompletionApplicationService : IResponsesCompletionApplicationService
{
    // Bounded tool-loop. The cap stops a runaway model from looping forever between
    // local tool calls; eight rounds matches the bound observed in similar
    // multi-round agent loops in this repo (e.g. SkillRunnerGAgent).
    private const int MaxToolRounds = 8;

    public async Task<ResponsesCompletionResult> CollectAsync(
        ILLMProvider provider,
        LLMRequest request,
        AgentToolExecutionContext toolContext,
        ResponsesToolClassification toolClassification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolContext);
        ArgumentNullException.ThrowIfNull(toolClassification);

        var messages = request.Messages.ToList();
        var outputText = new StringBuilder();
        TokenUsage? usage = null;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var roundRequest = CloneRequestWithMessages(request, messages);
            var (roundText, roundUsage, toolCalls) = await CollectStreamCompletionAsync(
                provider, roundRequest, toolContext, ct);
            outputText.Append(roundText);
            usage = roundUsage ?? usage;

            var forwardedToolCalls = SelectForwardedToolCalls(toolCalls, toolClassification);
            if (forwardedToolCalls.Count > 0)
                return new ResponsesCompletionResult(outputText.ToString(), usage, forwardedToolCalls);

            var localToolCalls = SelectLocalToolCalls(toolCalls, toolClassification);
            if (localToolCalls.Count == 0)
                return new ResponsesCompletionResult(outputText.ToString(), usage, []);

            messages.Add(new ChatMessage
            {
                Role = "assistant",
                ToolCalls = localToolCalls,
            });
            await ExecuteLocalToolCallsAsync(
                request,
                toolContext,
                localToolCalls,
                messages,
                round + 1,
                ct);
        }

        return new ResponsesCompletionResult(outputText.ToString(), usage, []);
    }

    public async Task<ResponsesCompletionResult> StreamAsync(
        ILLMProvider provider,
        LLMRequest request,
        AgentToolExecutionContext toolContext,
        ResponsesToolClassification toolClassification,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolContext);
        ArgumentNullException.ThrowIfNull(toolClassification);
        ArgumentNullException.ThrowIfNull(onTextDelta);

        var messages = request.Messages.ToList();
        var outputText = new StringBuilder();
        TokenUsage? usage = null;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var roundRequest = CloneRequestWithMessages(request, messages);
            var toolCalls = new ResponsesToolCallAccumulator();
            using (AgentToolContextScope.Push(toolContext))
            {
                await foreach (var chunk in provider.ChatStreamAsync(roundRequest, ct))
                {
                    var delta = ExtractChunkText(chunk);
                    if (!string.IsNullOrEmpty(delta))
                    {
                        outputText.Append(delta);
                        await onTextDelta(delta, ct);
                    }

                    if (chunk.DeltaToolCall != null)
                        toolCalls.TrackDelta(chunk.DeltaToolCall);

                    if (chunk.Usage != null)
                        usage = chunk.Usage;

                    if (chunk.IsLast)
                        break;
                }
            }

            var builtToolCalls = toolCalls.BuildToolCalls()
                .Select(ResponsesToolContext.ApplyToolChoiceHint)
                .ToArray();
            var forwardedToolCalls = SelectForwardedToolCalls(builtToolCalls, toolClassification);
            if (forwardedToolCalls.Count > 0)
                return new ResponsesCompletionResult(outputText.ToString(), usage, forwardedToolCalls);

            var localToolCalls = SelectLocalToolCalls(builtToolCalls, toolClassification);
            if (localToolCalls.Count == 0)
                return new ResponsesCompletionResult(outputText.ToString(), usage, []);

            messages.Add(new ChatMessage
            {
                Role = "assistant",
                ToolCalls = localToolCalls,
            });
            await ExecuteLocalToolCallsAsync(
                request,
                toolContext,
                localToolCalls,
                messages,
                round + 1,
                ct);
        }

        return new ResponsesCompletionResult(outputText.ToString(), usage, []);
    }

    private static LLMRequest CloneRequestWithMessages(
        LLMRequest request,
        List<ChatMessage> messages) =>
        new()
        {
            Messages = [.. messages],
            RequestId = request.RequestId,
            Metadata = request.Metadata,
            CallerContext = request.CallerContext,
            ToolContext = request.ToolContext,
            RoutingContext = request.RoutingContext,
            LlmControl = request.LlmControl,
            Tools = request.Tools,
            Model = request.Model,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ResponseFormat = request.ResponseFormat,
        };

    private static IReadOnlyList<ToolCall> SelectForwardedToolCalls(
        IReadOnlyList<ToolCall> toolCalls,
        ResponsesToolClassification toolClassification)
    {
        if (toolCalls.Count == 0 || toolClassification.ForwardedTools.Count == 0)
            return [];

        var forwardedToolNames = toolClassification.ForwardedTools
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        return toolCalls
            .Where(call => forwardedToolNames.Contains(call.Name))
            .ToArray();
    }

    private static IReadOnlyList<ToolCall> SelectLocalToolCalls(
        IReadOnlyList<ToolCall> toolCalls,
        ResponsesToolClassification toolClassification)
    {
        if (toolCalls.Count == 0 || toolClassification.EffectiveTools.Count == 0)
            return [];

        var forwardedToolNames = toolClassification.ForwardedTools
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var localToolNames = toolClassification.EffectiveTools
            .Select(static tool => tool.Name)
            .Where(name => !forwardedToolNames.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        return toolCalls
            .Where(call => localToolNames.Contains(call.Name))
            .ToArray();
    }

    private static async Task ExecuteLocalToolCallsAsync(
        LLMRequest request,
        AgentToolExecutionContext toolContext,
        IReadOnlyList<ToolCall> toolCalls,
        List<ChatMessage> messages,
        int llmRound,
        CancellationToken ct)
    {
        if (request.Tools is not { Count: > 0 })
            return;

        var toolsByName = request.Tools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        using (AgentToolContextScope.Push(toolContext))
        {
            foreach (var toolCall in toolCalls)
            {
                var argumentsJson = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson;
                string result;
                if (ResponsesToolChoiceHintPlan.IsStructuredErrorArguments(argumentsJson))
                {
                    result = argumentsJson;
                }
                else if (toolsByName.TryGetValue(toolCall.Name, out var tool))
                {
                    var callToolContext = toolContext.WithCallId(toolCall.Id);
                    using (AgentToolContextScope.Push(callToolContext))
                    {
                        result = await tool.ExecuteAsync(argumentsJson, ct);
                    }
                }
                else
                {
                    result = JsonSerializer.Serialize(new
                    {
                        error = "aevatar_substitute_tool_not_registered",
                        tool_name = toolCall.Name,
                    });
                }

                messages.Add(ChatMessage.Tool(toolCall.Id, result));
            }
        }
    }

    private static async Task<(string Text, TokenUsage? Usage, IReadOnlyList<ToolCall> ToolCalls)> CollectStreamCompletionAsync(
        ILLMProvider provider,
        LLMRequest request,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        var outputText = new StringBuilder();
        var toolCalls = new ResponsesToolCallAccumulator();
        TokenUsage? usage = null;

        using (AgentToolContextScope.Push(toolContext))
        {
            await foreach (var chunk in provider.ChatStreamAsync(request, ct))
            {
                var delta = ExtractChunkText(chunk);
                if (!string.IsNullOrEmpty(delta))
                    outputText.Append(delta);

                if (chunk.DeltaToolCall != null)
                    toolCalls.TrackDelta(chunk.DeltaToolCall);

                if (chunk.Usage != null)
                    usage = chunk.Usage;

                if (chunk.IsLast)
                    break;
            }
        }

        return (outputText.ToString(), usage, toolCalls.BuildToolCalls()
            .Select(ResponsesToolContext.ApplyToolChoiceHint)
            .ToArray());
    }

    private static string? ExtractChunkText(LLMStreamChunk chunk)
    {
        if (!string.IsNullOrWhiteSpace(chunk.DeltaContent))
            return chunk.DeltaContent;

        if (chunk.DeltaContentPart is { Kind: ContentPartKind.Text } part && !string.IsNullOrWhiteSpace(part.Text))
            return part.Text;

        return null;
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

        // Materialize providers once: substitute names are derived from the
        // provider's actual tool list, so there is no second hardcoded registry.
        var providerList = providers as IReadOnlyList<IResponsesToolProvider>
                           ?? providers.ToArray();

        var discoveredSubstituteTools = new List<IAgentTool>();
        foreach (var provider in providerList)
        {
            ct.ThrowIfCancellationRequested();
            discoveredSubstituteTools.AddRange(
                await provider.GetSubstituteToolsAsync(context, ct).ConfigureAwait(false));
        }

        var substituteTools = discoveredSubstituteTools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var substituteNames = new HashSet<string>(substituteTools.Keys, StringComparer.Ordinal);

        var discoveredAdditiveTools = new List<IAgentTool>();
        foreach (var provider in providerList)
        {
            ct.ThrowIfCancellationRequested();
            discoveredAdditiveTools.AddRange(
                await provider.GetAdditiveToolsAsync(context, ct).ConfigureAwait(false));
        }

        var additiveTools = discoveredAdditiveTools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        var forwarded = new List<ResponsesApplicationToolDeclaration>();
        var effective = new List<IAgentTool>();
        var substitutedNames = new List<string>();

        foreach (var declaration in declaredTools)
        {
            if (!substituteNames.Contains(declaration.Name))
            {
                forwarded.Add(declaration);
                effective.Add(new ResponsesForwardedTool(declaration));
                continue;
            }

            substitutedNames.Add(declaration.Name);
            var substitute = substituteTools[declaration.Name];
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
            addedAdditiveNames);
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

internal sealed class ResponsesToolCallAccumulator
{
    private readonly Dictionary<string, ToolCallAggregate> _aggregates = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private int _anonymousCounter;
    private string? _activeAnonymousKey;

    public ToolCall TrackDelta(ToolCall delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var aggregate = ResolveAggregate(delta);
        if (!string.IsNullOrWhiteSpace(delta.Name))
            aggregate.Name = delta.Name;

        if (!string.IsNullOrEmpty(delta.ArgumentsJson))
            aggregate.Arguments.Append(delta.ArgumentsJson);

        return new ToolCall
        {
            Id = aggregate.Id,
            Name = string.IsNullOrWhiteSpace(delta.Name)
                ? aggregate.Name ?? string.Empty
                : delta.Name,
            ArgumentsJson = delta.ArgumentsJson ?? string.Empty,
        };
    }

    public IReadOnlyList<ToolCall> BuildToolCalls()
    {
        var result = new List<ToolCall>(_order.Count);
        foreach (var key in _order)
        {
            var aggregate = _aggregates[key];
            result.Add(new ToolCall
            {
                Id = aggregate.Id,
                Name = aggregate.Name ?? string.Empty,
                ArgumentsJson = aggregate.Arguments.ToString(),
            });
        }

        return result;
    }

    private ToolCallAggregate ResolveAggregate(ToolCall delta)
    {
        if (!string.IsNullOrWhiteSpace(delta.Id))
            return ResolveKnownIdAggregate(delta.Id);

        return ResolveAnonymousAggregate();
    }

    private ToolCallAggregate ResolveKnownIdAggregate(string id)
    {
        var knownKey = $"id:{id}";
        if (TryPromoteActiveAnonymousAggregate(knownKey, id, out var promoted))
        {
            _activeAnonymousKey = null;
            return promoted;
        }

        _activeAnonymousKey = null;
        if (!_aggregates.TryGetValue(knownKey, out var aggregate))
        {
            aggregate = new ToolCallAggregate(id);
            _aggregates[knownKey] = aggregate;
            _order.Add(knownKey);
        }

        return aggregate;
    }

    private ToolCallAggregate ResolveAnonymousAggregate()
    {
        if (!string.IsNullOrWhiteSpace(_activeAnonymousKey))
            return _aggregates[_activeAnonymousKey];

        _anonymousCounter++;
        var anonymousKey = $"anon:{_anonymousCounter}";
        var anonymousId = $"stream-tool-call-{_anonymousCounter}";
        var aggregate = new ToolCallAggregate(anonymousId);
        _aggregates[anonymousKey] = aggregate;
        _order.Add(anonymousKey);
        _activeAnonymousKey = anonymousKey;
        return aggregate;
    }

    private bool TryPromoteActiveAnonymousAggregate(
        string knownKey,
        string knownId,
        out ToolCallAggregate aggregate)
    {
        aggregate = default!;

        if (string.IsNullOrWhiteSpace(_activeAnonymousKey))
            return false;

        var anonymousAggregate = _aggregates[_activeAnonymousKey];

        if (_aggregates.ContainsKey(knownKey))
            return false;

        anonymousAggregate.Id = knownId;
        _aggregates.Remove(_activeAnonymousKey);
        _aggregates[knownKey] = anonymousAggregate;
        ReplaceOrderKey(_activeAnonymousKey, knownKey);
        aggregate = anonymousAggregate;
        return true;
    }

    private void ReplaceOrderKey(string sourceKey, string targetKey)
    {
        _order[_order.IndexOf(sourceKey)] = targetKey;
    }

    private sealed class ToolCallAggregate
    {
        public ToolCallAggregate(string id)
        {
            Id = id;
        }

        public string Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
