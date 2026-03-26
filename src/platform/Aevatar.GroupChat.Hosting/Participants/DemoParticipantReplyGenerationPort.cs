using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.GroupChat.Hosting.Participants;

public sealed class DemoParticipantReplyGenerationPort : IParticipantReplyGenerationPort
{
    private readonly GroupChatCapabilityOptions _options;

    public DemoParticipantReplyGenerationPort(IOptions<GroupChatCapabilityOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<ParticipantReplyGenerationResult?> GenerateReplyAsync(
        ParticipantReplyGenerationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.EnableDemoReplyGeneration)
            return Task.FromResult<ParticipantReplyGenerationResult?>(null);

        var configuredParticipants = _options.ParticipantAgentIds
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(static x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (configuredParticipants.Count > 0 &&
            !configuredParticipants.Contains(request.ParticipantAgentId))
        {
            return Task.FromResult<ParticipantReplyGenerationResult?>(null);
        }

        var prefix = string.IsNullOrWhiteSpace(_options.DemoReplyPrefix)
            ? "demo-reply"
            : _options.DemoReplyPrefix.Trim();
        var replyText =
            $"{prefix}:{request.ParticipantAgentId}: 收到 {request.TriggerMessage.SenderId} 的消息「{request.TriggerMessage.Text}」";
        return Task.FromResult<ParticipantReplyGenerationResult?>(new ParticipantReplyGenerationResult(replyText));
    }
}
