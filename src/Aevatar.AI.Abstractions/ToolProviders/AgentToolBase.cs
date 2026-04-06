// ─────────────────────────────────────────────────────────────
// AgentToolBase<TParams> — 类型安全的 IAgentTool 基类
//
// 从 TParams 自动推导 ParametersSchema，子类只需实现
// Name / Description / ExecuteAsync(TParams, CancellationToken)。
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// 类型安全的 <see cref="IAgentTool"/> 基类。
/// <typeparamref name="TParams"/> 类型自动生成 <see cref="ParametersSchema"/>。
/// </summary>
/// <typeparam name="TParams">工具参数类型，用于自动生成 JSON Schema 和反序列化。</typeparam>
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

    /// <summary>自动从 <typeparamref name="TParams"/> 生成的 JSON Schema。</summary>
    public string ParametersSchema => CachedSchema;

    /// <inheritdoc />
    public virtual ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    /// <inheritdoc />
    public virtual bool IsReadOnly => false;

    /// <inheritdoc />
    public virtual bool IsDestructive => false;

    /// <inheritdoc />
    public virtual bool? RequiresApproval(string argumentsJson) => null;

    /// <summary>类型安全的执行方法。</summary>
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
            return Task.FromResult($$"""{"error":"Invalid parameters: {{ex.Message}}"}""");
        }

        if (parameters is null)
            return Task.FromResult("""{"error":"Parameters are required"}""");

        return ExecuteAsync(parameters, ct);
    }
}
