using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Sends outbound platform replies using the latest public registration view.
/// </summary>
public sealed class ChannelPlatformReplyService
{
    private const int LarkTextMessageLimit = 30_000;
    private const int ChunkMarkerOverhead = 80;
    private const string ContinuesSuffixFormat = "\n\n[part {0}/{1} continues]";
    private const string ContinuedPrefixFormat = "[part {0}/{1} continued]\n\n";

    private readonly IChannelBotRegistrationRuntimeQueryPort _runtimeQueryPort;
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<ChannelPlatformReplyService> _logger;

    public ChannelPlatformReplyService(
        IChannelBotRegistrationRuntimeQueryPort runtimeQueryPort,
        NyxIdApiClient nyxClient,
        ILogger<ChannelPlatformReplyService> logger)
    {
        _runtimeQueryPort = runtimeQueryPort ?? throw new ArgumentNullException(nameof(runtimeQueryPort));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PlatformReplyDeliveryResult> DeliverAsync(
        IPlatformAdapter adapter,
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(registration);

        var currentRegistration = await ResolveCurrentRegistrationAsync(registration, ct);
        var chunks = SplitReplyText(adapter.Platform, replyText);
        for (var i = 0; i < chunks.Count; i++)
        {
            var attempt = await adapter.SendReplyAsync(
                chunks[i],
                inbound,
                currentRegistration,
                _nyxClient,
                ct);
            if (!attempt.Succeeded)
            {
                _logger.LogWarning(
                    "Channel platform reply failed: platform={Platform}, registration={RegistrationId}, detail={Detail}, kind={Kind}, chunk={ChunkIndex}, chunks={ChunkCount}",
                    adapter.Platform,
                    currentRegistration.Id,
                    attempt.Detail,
                    attempt.FailureKind,
                    i + 1,
                    chunks.Count);

                if (chunks.Count == 1)
                    return attempt;

                var detail = string.IsNullOrWhiteSpace(attempt.Detail)
                    ? $"chunk {i + 1}/{chunks.Count} failed"
                    : $"chunk {i + 1}/{chunks.Count} failed: {attempt.Detail}";
                return new PlatformReplyDeliveryResult(false, detail, attempt.FailureKind);
            }
        }

        return new PlatformReplyDeliveryResult(true, chunks.Count == 1 ? null : $"delivered {chunks.Count} chunks");
    }

    private async Task<ChannelBotRegistrationEntry> ResolveCurrentRegistrationAsync(
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registration.Id))
            return registration;

        var current = await _runtimeQueryPort.GetAsync(registration.Id, ct);
        return current ?? registration;
    }

    internal static IReadOnlyList<string> SplitReplyText(string platform, string replyText)
    {
        if (!IsLarkFamilyPlatform(platform) ||
            string.IsNullOrEmpty(replyText) ||
            replyText.Length <= LarkTextMessageLimit)
        {
            return [replyText ?? string.Empty];
        }

        var contentBudget = Math.Max(1, LarkTextMessageLimit - ChunkMarkerOverhead);
        var rawChunks = SplitRaw(replyText, contentBudget);
        if (rawChunks.Count == 1)
            return rawChunks;

        var rendered = new List<string>(rawChunks.Count);
        for (var i = 0; i < rawChunks.Count; i++)
        {
            var part = i + 1;
            var prefix = i == 0 ? string.Empty : string.Format(ContinuedPrefixFormat, part, rawChunks.Count);
            var suffix = i == rawChunks.Count - 1
                ? string.Empty
                : string.Format(ContinuesSuffixFormat, part, rawChunks.Count);
            rendered.Add(prefix + rawChunks[i] + suffix);
        }

        return rendered;
    }

    private static bool IsLarkFamilyPlatform(string? platform) =>
        string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitRaw(string text, int contentBudget)
    {
        var chunks = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            if (remaining <= contentBudget)
            {
                chunks.Add(text[offset..]);
                break;
            }

            var searchAnchor = offset + contentBudget - 1;
            var boundary = text.LastIndexOf("\n\n", searchAnchor, contentBudget, StringComparison.Ordinal);
            if (boundary <= offset)
            {
                chunks.Add(text[offset..(offset + contentBudget)]);
                offset += contentBudget;
            }
            else
            {
                chunks.Add(text[offset..boundary]);
                offset = boundary + 2;
            }
        }

        return chunks;
    }
}
