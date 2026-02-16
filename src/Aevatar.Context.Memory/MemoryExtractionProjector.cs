using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Memory;

/// <summary>
/// 记忆提取投影器。
/// 接入统一 Projection Pipeline，在 Run 完成时提取记忆。
///
/// 生命周期：
/// - InitializeAsync: 初始化事件累积列表
/// - ProjectAsync: 累积 Run 内有文本内容的事件
/// - CompleteAsync: 调用 IMemoryExtractor → MemoryDeduplicator → MemoryWriter
///
/// Order = 200，在 ReadModelProjector (0) 和 AGUIEventProjector (100) 之后执行。
/// </summary>
/// <remarks>
/// 泛型参数使用 object 作为 TContext 和 TTopology 占位，
/// 实际集成时由 Workflow.Projection 注册为具体类型。
/// 使用者通过 adapter 或在注册时桥接到 WorkflowExecutionProjectionContext。
/// </remarks>
public sealed class MemoryExtractionProjector<TContext, TTopology>
    : IProjectionProjector<TContext, TTopology>
    where TContext : class
{
    private const string AccumulatedMessagesKey = "aevatar.context.memory.messages";
    private const string UserIdKey = "aevatar.context.memory.userId";
    private const string AgentIdKey = "aevatar.context.memory.agentId";

    private readonly IMemoryExtractor _extractor;
    private readonly MemoryDeduplicator _deduplicator;
    private readonly MemoryWriter _writer;
    private readonly ILogger _logger;

    private List<string> _messages = [];

    public int Order => 200;

    public MemoryExtractionProjector(
        IMemoryExtractor extractor,
        MemoryDeduplicator deduplicator,
        MemoryWriter writer,
        ILogger<MemoryExtractionProjector<TContext, TTopology>>? logger = null)
    {
        _extractor = extractor;
        _deduplicator = deduplicator;
        _writer = writer;
        _logger = logger ?? NullLogger<MemoryExtractionProjector<TContext, TTopology>>.Instance;
    }

    public ValueTask InitializeAsync(TContext context, CancellationToken ct = default)
    {
        _messages = [];
        _logger.LogDebug("MemoryExtractionProjector initialized");
        return ValueTask.CompletedTask;
    }

    public ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        // Accumulate text content from events for later extraction
        if (envelope.Payload != null)
        {
            var textContent = ExtractTextFromEnvelope(envelope);
            if (textContent != null)
                _messages.Add(textContent);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(TContext context, TTopology topology, CancellationToken ct = default)
    {
        if (_messages.Count == 0)
        {
            _logger.LogDebug("No messages to extract memories from");
            return;
        }

        try
        {
            _logger.LogInformation("Extracting memories from {Count} accumulated messages", _messages.Count);

            var candidates = await _extractor.ExtractAsync(_messages, ct);
            if (candidates.Count == 0)
            {
                _logger.LogDebug("No candidate memories extracted");
                return;
            }

            var userId = "default";
            var agentId = "default";

            var deduplicated = await _deduplicator.DeduplicateAsync(
                candidates, userId, agentId, ct);

            await _writer.WriteAsync(deduplicated, ct);

            _logger.LogInformation(
                "Memory extraction complete: {Candidates} candidates → {Written} written",
                candidates.Count, deduplicated.Count(r => r.Decision != DeduplicationDecision.Skip));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory extraction failed");
        }
    }

    private static string? ExtractTextFromEnvelope(EventEnvelope envelope)
    {
        // Extract text from known event types by checking the type URL
        var typeUrl = envelope.Payload?.TypeUrl;
        if (string.IsNullOrEmpty(typeUrl))
            return null;

        // For text-bearing events, extract content from the payload
        // The specific extraction depends on event types registered in the system
        // Here we provide a generic fallback: if the payload can be converted to string
        try
        {
            if (envelope.Payload?.Value != null)
            {
                var bytes = envelope.Payload.Value.ToByteArray();
                if (bytes.Length > 0)
                {
                    var text = System.Text.Encoding.UTF8.GetString(bytes);
                    if (text.Length > 0 && text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t'))
                        return $"[{typeUrl}] {text}";
                }
            }
        }
        catch
        {
            // Silently ignore non-text payloads
        }

        return null;
    }
}
