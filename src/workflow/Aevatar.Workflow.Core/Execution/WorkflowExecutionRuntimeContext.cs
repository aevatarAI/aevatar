using Aevatar.AI.Abstractions.LLMProviders;

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

// Refactor (iter115/cluster-3):
//   Old pattern: durable LLM, connector authorization, and secure captured values
//                were held as authority inside per-process runtime context fields.
//   New principle: runtime context only carries same-turn passthrough metadata; durable
//                  control and security facts live in typed actor state.
internal sealed class WorkflowExecutionRuntimeContext
{
    public WorkflowRequestPassthroughMetadata RequestPassthroughMetadata { get; } = new();

    public void Clear() => RequestPassthroughMetadata.Clear();

    public void ApplyRequestMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        RequestPassthroughMetadata.Clear();

        if (metadata == null || metadata.Count == 0)
            return;

        foreach (var pair in metadata)
        {
            var key = Normalize(pair.Key);
            var value = Normalize(pair.Value);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            RequestPassthroughMetadata.Set(key, value);
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

// Refactor (iter115/cluster-3):
//   Old pattern: request metadata was copied wholesale into runtime context,
//                allowing control/security fields to flow back as passthrough.
//   New principle: runtime passthrough is same-turn only and rejects known
//                  durable control/security keys.
internal sealed class WorkflowRequestPassthroughMetadata
{
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";

    private static readonly HashSet<string> BlockedKeys =
    [
        LegacyConnectorHttpAuthorizationBlockedKey,
        LLMRequestMetadataKeys.NyxIdAccessToken,
        LLMRequestMetadataKeys.ModelOverride,
        LLMRequestMetadataKeys.NyxIdRoutePreference,
    ];

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalizedKey = key.Trim();
        if (IsBlockedKey(normalizedKey))
            return;

        _values[normalizedKey] = value.Trim();
    }

    public void Clear() => _values.Clear();

    public static bool IsBlockedKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        BlockedKeys.Contains(key.Trim());
}
