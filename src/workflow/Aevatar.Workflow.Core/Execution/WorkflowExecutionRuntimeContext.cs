using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter16/cluster-031):
//   Old pattern: helper code discovered the actor-owned Dictionary<string, object?>
//                runtime bag through a generic items context.
//   New principle: workflow execution code depends on a typed actor-owned
//                  runtime context accessor instead of string-key item lookup.
internal interface IWorkflowExecutionRuntimeContextAccessor
{
    WorkflowExecutionRuntimeContext RuntimeContext { get; }
}

// Refactor (iter16/cluster-031):
//   Old pattern: WorkflowRunGAgent kept Dictionary<string, object?> _executionItems
//                bag for request metadata, LLM overrides, authorization, secure values
//   New principle: typed non-durable actor-owned WorkflowExecutionRuntimeContext;
//                  runtime-only values stay non-durable, with no proto/state migration in this cluster.
internal sealed class WorkflowExecutionRuntimeContext
{
    private static readonly HashSet<string> RuntimeControlMetadataKeys = new(StringComparer.Ordinal)
    {
        LLMRequestMetadataKeys.NyxIdAccessToken,
        LLMRequestMetadataKeys.ModelOverride,
        LLMRequestMetadataKeys.NyxIdRoutePreference,
        ConnectorRequest.HttpAuthorizationMetadataKey,
    };

    public WorkflowLlmRuntimeOverrides LlmOverrides { get; } = new();

    public WorkflowConnectorRuntimeContext Connector { get; } = new();

    public WorkflowRequestPassthroughMetadata RequestPassthroughMetadata { get; } = new();

    public CapturedSecureInputs CapturedSecureInputs { get; } = new();

    public void Clear()
    {
        LlmOverrides.Clear();
        Connector.Clear();
        RequestPassthroughMetadata.Clear();
        CapturedSecureInputs.Clear();
    }

    public void ApplyRequestMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        LlmOverrides.Clear();
        Connector.Clear();
        RequestPassthroughMetadata.Clear();

        if (metadata == null || metadata.Count == 0)
            return;

        foreach (var pair in metadata)
        {
            var key = Normalize(pair.Key);
            var value = Normalize(pair.Value);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (string.Equals(key, LLMRequestMetadataKeys.NyxIdAccessToken, StringComparison.Ordinal))
            {
                LlmOverrides.NyxIdAccessToken = value;
                continue;
            }

            if (string.Equals(key, LLMRequestMetadataKeys.ModelOverride, StringComparison.Ordinal))
            {
                LlmOverrides.ModelOverride = value;
                continue;
            }

            if (string.Equals(key, LLMRequestMetadataKeys.NyxIdRoutePreference, StringComparison.Ordinal))
            {
                LlmOverrides.NyxIdRoutePreference = value;
                continue;
            }

            if (string.Equals(key, ConnectorRequest.HttpAuthorizationMetadataKey, StringComparison.Ordinal))
            {
                Connector.Authorization = value;
                continue;
            }

            if (!RuntimeControlMetadataKeys.Contains(key))
                RequestPassthroughMetadata.Set(key, value);
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

internal sealed class WorkflowLlmRuntimeOverrides
{
    public string? NyxIdAccessToken { get; set; }

    public string? ModelOverride { get; set; }

    public string? NyxIdRoutePreference { get; set; }

    public void Clear()
    {
        NyxIdAccessToken = null;
        ModelOverride = null;
        NyxIdRoutePreference = null;
    }
}

internal sealed class WorkflowConnectorRuntimeContext
{
    public string? Authorization { get; set; }

    public void Clear()
    {
        Authorization = null;
    }
}

internal sealed class WorkflowRequestPassthroughMetadata
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _values[key.Trim()] = value.Trim();
    }

    public void Clear() => _values.Clear();
}

internal sealed class CapturedSecureInputs
{
    private readonly Dictionary<CapturedSecureInputKey, string> _values = new();

    public IReadOnlyDictionary<CapturedSecureInputKey, string> Values => _values;

    public void Set(string? runId, string? variable, string? value)
    {
        if (!TryCreateKey(runId, variable, out var key))
            return;

        _values[key] = value ?? string.Empty;
    }

    public bool TryGet(string? runId, string? variable, out string value)
    {
        if (TryCreateKey(runId, variable, out var key) &&
            _values.TryGetValue(key, out value!))
        {
            return true;
        }

        value = string.Empty;
        return false;
    }

    public bool Remove(string? runId, string? variable)
    {
        if (!TryCreateKey(runId, variable, out var key))
            return false;

        return _values.Remove(key);
    }

    public void RemoveRun(string? runId)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (string.IsNullOrWhiteSpace(normalizedRunId))
            return;

        foreach (var key in _values.Keys.Where(x => x.RunId == normalizedRunId).ToList())
        {
            _values.Remove(key);
        }
    }

    public void Clear() => _values.Clear();

    private static bool TryCreateKey(
        string? runId,
        string? variable,
        out CapturedSecureInputKey key)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var normalizedVariable = string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRunId) ||
            string.IsNullOrWhiteSpace(normalizedVariable))
        {
            key = default;
            return false;
        }

        key = new CapturedSecureInputKey(normalizedRunId, normalizedVariable);
        return true;
    }
}

// Refactor (iter16/cluster-031):
//   Old pattern: captured secure input values used string-composed keys such as
//                "{runId}::{variable}" inside the generic execution item bag.
//   New principle: secure input capture uses a typed key in the actor-owned
//                  non-durable runtime context.
internal readonly record struct CapturedSecureInputKey(
    string RunId,
    string Variable);
