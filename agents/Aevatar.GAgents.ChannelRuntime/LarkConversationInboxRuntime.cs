using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal interface ILarkConversationInbox
{
    Task EnqueueAsync(ChatActivity activity, CancellationToken ct);
}

internal sealed class LarkConversationInboxRuntime :
    IHostedService,
    IAsyncDisposable,
    ILarkConversationInbox
{
    internal const string InboxStreamId = "channel-runtime:lark:durable-inbox";

    private readonly IStreamProvider _streamProvider;
    private readonly IServiceProvider _services;
    private readonly ChannelPipeline _pipeline;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LarkConversationInboxRuntime> _logger;
    private DurableInboxSubscriber? _subscriber;
    private IAsyncDisposable? _subscription;

    public LarkConversationInboxRuntime(
        IStreamProvider streamProvider,
        IServiceProvider services,
        ChannelPipeline pipeline,
        ILoggerFactory loggerFactory,
        ILogger<LarkConversationInboxRuntime> logger)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_subscription is not null)
            return;

        _subscriber = new DurableInboxSubscriber(
            _pipeline,
            _services,
            CreateTurnContext,
            _loggerFactory.CreateLogger<DurableInboxSubscriber>());
        _subscriber.Start();

        _subscription = await _streamProvider
            .GetStream(InboxStreamId)
            .SubscribeAsync<ChatActivity>(_subscriber.OnNextAsync, ct);

        _logger.LogInformation("Started Lark durable inbox subscription on {StreamId}", InboxStreamId);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_subscription is null && _subscriber is null)
            return;

        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
            _subscription = null;
        }

        if (_subscriber is not null)
        {
            await _subscriber.DisposeAsync();
            _subscriber = null;
        }

        _logger.LogInformation("Stopped Lark durable inbox subscription on {StreamId}", InboxStreamId);
    }

    public Task EnqueueAsync(ChatActivity activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return _streamProvider.GetStream(InboxStreamId).ProduceAsync(activity, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    private ITurnContext CreateTurnContext(ChatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var channel = activity.ChannelId?.Clone()
                      ?? activity.Conversation?.Channel?.Clone()
                      ?? ChannelId.From("lark");
        var bot = activity.Bot?.Clone()
                  ?? activity.Conversation?.Bot?.Clone()
                  ?? BotInstanceId.From("unknown-bot");
        var registrationId = !string.IsNullOrWhiteSpace(bot.Value)
            ? bot.Value
            : "unknown-registration";

        return new ConversationPipelineTurnContext(
            activity,
            ChannelBotDescriptor.Create(registrationId, channel, bot),
            _services);
    }
}
