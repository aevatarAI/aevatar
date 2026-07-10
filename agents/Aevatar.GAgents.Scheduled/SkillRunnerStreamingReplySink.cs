using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Sends actor-approved SkillRunner output snapshots through the channel-neutral outbound
/// delivery port.
/// </summary>
/// <remarks>
/// Refactor (issue #2683): the sink no longer owns Lark request construction or dispatcher
/// dependencies. Platform-specific send/update behavior belongs behind the selected channel
/// adapter.
/// </remarks>
internal sealed class SkillRunnerStreamingReplySink : IDisposable
{
    /// <summary>
    /// Conservative text body cap shared by scheduled output chunking and streaming snapshots.
    /// </summary>
    public const int MaxLarkTextLength = 30_000;

    private const string TruncationMarker = "\n\n...[truncated]";

    private readonly ISkillRunnerOutboundDeliveryPort _outboundDeliveryPort;
    private readonly SkillRunnerOutboundDeliveryRequest _requestTemplate;
    private readonly ILogger? _logger;
    private string? _platformMessageId;
    private bool _disposed;

    public SkillRunnerStreamingReplySink(
        ISkillRunnerOutboundDeliveryPort outboundDeliveryPort,
        SkillRunnerOutboundDeliveryRequest requestTemplate,
        ILogger? logger)
    {
        _outboundDeliveryPort = outboundDeliveryPort ?? throw new ArgumentNullException(nameof(outboundDeliveryPort));
        _requestTemplate = requestTemplate ?? throw new ArgumentNullException(nameof(requestTemplate));
        _logger = logger;
    }

    public int ChunksEmitted { get; private set; }

    public string? PlatformMessageId => _platformMessageId;

    public void Dispose() => _disposed = true;

    public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
        DispatchAsync(accumulatedText, isFinal: false, ct);

    public Task FinalizeAsync(string finalText, CancellationToken ct) =>
        DispatchAsync(finalText, isFinal: true, ct);

    public async Task DispatchAsync(string text, bool isFinal, CancellationToken ct)
    {
        if (_disposed)
            return;

        var capped = TruncateForLark(text);
        if (string.IsNullOrWhiteSpace(capped))
            return;

        try
        {
            var receipt = await _outboundDeliveryPort.SendAsync(
                _requestTemplate with { Text = capped },
                ct).ConfigureAwait(false);
            _platformMessageId = string.IsNullOrWhiteSpace(receipt.PlatformMessageId)
                ? receipt.SentActivityId
                : receipt.PlatformMessageId;
            ChunksEmitted++;
        }
        catch (Exception ex) when (!isFinal && IsTransientFailure(ex, ct))
        {
            _logger?.LogWarning(
                ex,
                "SkillRunner streaming sink: channel-native send threw mid-stream; will retry on next actor-approved snapshot.");
        }
    }

    private static bool IsTransientFailure(Exception ex, CancellationToken callerCt) =>
        !(ex is OperationCanceledException && callerCt.IsCancellationRequested);

    internal static string TruncateForLark(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (text.Length <= MaxLarkTextLength)
            return text;
        var head = MaxLarkTextLength - TruncationMarker.Length;
        if (head < 0)
            head = 0;
        return text[..head] + TruncationMarker;
    }
}
