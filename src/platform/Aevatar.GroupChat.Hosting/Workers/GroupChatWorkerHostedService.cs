using Aevatar.GroupChat.Application.Workers;
using Aevatar.GroupChat.Hosting.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.GroupChat.Hosting.Workers;

public sealed class GroupChatWorkerHostedService : IHostedService
{
    private readonly GroupMentionHintWorker _mentionHintWorker;
    private readonly AgentFeedService _agentFeedService;
    private readonly GroupParticipantReplyCompletedService _replyCompletedService;
    private readonly GroupChatCapabilityOptions _options;
    private readonly List<IAsyncDisposable> _subscriptions = [];

    public GroupChatWorkerHostedService(
        GroupMentionHintWorker mentionHintWorker,
        AgentFeedService agentFeedService,
        GroupParticipantReplyCompletedService replyCompletedService,
        IOptions<GroupChatCapabilityOptions> options)
    {
        _mentionHintWorker = mentionHintWorker ?? throw new ArgumentNullException(nameof(mentionHintWorker));
        _agentFeedService = agentFeedService ?? throw new ArgumentNullException(nameof(agentFeedService));
        _replyCompletedService = replyCompletedService ?? throw new ArgumentNullException(nameof(replyCompletedService));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(await _replyCompletedService.SubscribeAsync(cancellationToken));

        var participantAgentIds = _options.ParticipantAgentIds
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var participantAgentId in participantAgentIds)
        {
            _subscriptions.Add(await _mentionHintWorker.SubscribeAsync(participantAgentId, cancellationToken));
            _subscriptions.Add(await _agentFeedService.SubscribeAsync(participantAgentId, cancellationToken));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        while (_subscriptions.Count > 0)
        {
            var subscription = _subscriptions[^1];
            _subscriptions.RemoveAt(_subscriptions.Count - 1);
            await subscription.DisposeAsync();
        }
    }
}
