// ─────────────────────────────────────────────────────────────
// AgentToolBase<TParams> — type-safe base class for IAgentTool
//
// Automatically derives ParametersSchema from TParams; subclasses only need to implement
// Name / Description / ExecuteAsync(TParams, CancellationToken)。
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Type-safe base class for <see cref="IAgentTool"/>.
/// <see cref="ParametersSchema"/> is automatically generated from <typeparamref name="TParams"/>.
/// </summary>
/// <typeparam name="TParams">Tool parameter type used for automatic JSON Schema generation and deserialization.</typeparam>
public abstract class AgentToolBase<TParams> : IAgentTool where TParams : class
{
    private static readonly string CachedSchema = AgentToolSchemaGenerator.GenerateSchemaString<TParams>();

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <summary>The JSON Schema automatically generated from <typeparamref name="TParams"/>.</summary>
    public string ParametersSchema => CachedSchema;

    /// <inheritdoc />
    public virtual ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    /// <inheritdoc />
    public virtual bool IsReadOnly => false;

    /// <inheritdoc />
    public virtual bool IsDestructive => false;

    /// <inheritdoc />
    public virtual bool? RequiresApproval(string argumentsJson) => null;

    /// <summary>Type-safe execution method.</summary>
    protected abstract Task<string> ExecuteAsync(TParams parameters, CancellationToken ct);

    /// <inheritdoc />
    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        TParams? parameters;
        try
        {
            parameters = string.IsNullOrWhiteSpace(argumentsJson)
                ? null
                : JsonSerializer.Deserialize<TParams>(argumentsJson, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Invalid parameters: {ex.Message}" }));
        }

        if (parameters is null)
            return Task.FromResult("""{"error":"Parameters are required"}""");

        return ExecuteAsync(parameters, ct);
    }
}
