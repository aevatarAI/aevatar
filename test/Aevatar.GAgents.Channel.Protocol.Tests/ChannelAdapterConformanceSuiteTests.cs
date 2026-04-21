using System.Reflection;
using System.Threading.Channels;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Testing;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ChannelTestingLibraryConstraintTests
{
    [Fact]
    public void ConformanceTests_AdapterArgument_IsConstrainedToTransportAndOutboundPort()
    {
        var parameter = typeof(ChannelAdapterConformanceTests<>).GetGenericArguments()[0];
        var constraints = parameter.GetGenericParameterConstraints();

        parameter.GenericParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint).ShouldBeTrue();
        constraints.ShouldContain(typeof(IChannelTransport));
        constraints.ShouldContain(typeof(IChannelOutboundPort));
    }

    [Fact]
    public void FaultTests_AdapterArgument_IsConstrainedToTransportAndOutboundPort()
    {
        var parameter = typeof(ChannelAdapterFaultTests<>).GetGenericArguments()[0];
        var constraints = parameter.GetGenericParameterConstraints();

        parameter.GenericParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint).ShouldBeTrue();
        constraints.ShouldContain(typeof(IChannelTransport));
        constraints.ShouldContain(typeof(IChannelOutboundPort));
    }

    [Fact]
    public void ComposerTests_ComposerArgument_IsConstrainedToMessageComposer()
    {
        var parameter = typeof(MessageComposerUnitTests<>).GetGenericArguments()[0];
        parameter.GetGenericParameterConstraints().ShouldContain(typeof(IMessageComposer));
    }
}

public sealed class StubChannelAdapterConformanceTests : ChannelAdapterConformanceTests<StubChannelAdapter>
{
    private readonly StubAdapterHarness _harness = new();

    protected override StubChannelAdapter CreateAdapter() => _harness.Reset();

    protected override WebhookFixture? WebhookFixture => _harness.Webhook;

    protected override GatewayFixture? GatewayFixture => null;

    protected override ChannelTransportBinding CreateBinding() => _harness.DefaultBinding;

    protected override ChannelTransportBinding? CreateSecondaryBinding() => _harness.SecondaryBinding;
}

public sealed class StubChannelAdapterFaultTests : ChannelAdapterFaultTests<StubChannelAdapter>
{
    private readonly StubAdapterHarness _harness = new();

    protected override StubChannelAdapter CreateAdapter() => _harness.Reset();

    protected override WebhookFixture? WebhookFixture => _harness.Webhook;

    protected override GatewayFixture? GatewayFixture => null;

    protected override ChannelTransportBinding CreateBinding() => _harness.DefaultBinding;
}

public sealed class StubMessageComposerUnitTests : MessageComposerUnitTests<StubMessageComposer>
{
    protected override StubMessageComposer CreateComposer() => new();

    protected override ChannelCapabilities CreateCapabilities() => StubMessageComposer.DefaultCapabilities;

    protected override void AssertSimpleTextPayload(object payload, MessageContent intent, ComposeContext context)
    {
        payload.ShouldBeOfType<StubNativePayload>().Text.ShouldBe(intent.Text);
    }

    protected override void AssertActionsPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        var native = payload.ShouldBeOfType<StubNativePayload>();
        if (capability == ComposeCapability.Exact)
            native.ActionCount.ShouldBe(intent.Actions.Count);
    }

    protected override void AssertCardPayload(object payload, MessageContent intent, ComposeContext context, ComposeCapability capability)
    {
        payload.ShouldBeOfType<StubNativePayload>().CardCount.ShouldBe(intent.Cards.Count);
    }

    protected override void AssertOverflowTruncation(object payload, int maxLength)
    {
        payload.ShouldBeOfType<StubNativePayload>().Text.Length.ShouldBeLessThanOrEqualTo(maxLength);
    }
}

public sealed class StubChannelAdapter : IChannelTransport, IChannelOutboundPort
{
    private readonly StubAdapterHarness _harness;
    private readonly Channel<ChatActivity> _inbound = System.Threading.Channels.Channel.CreateBounded<ChatActivity>(16);
    private long _nextActivityId;
    private bool _initialized;
    private bool _receiving;

    public StubChannelAdapter(StubAdapterHarness harness)
    {
        _harness = harness;
    }

    public ChannelId Channel { get; } = ChannelId.From("stub");

    public TransportMode TransportMode { get; } = TransportMode.Webhook;

    public ChannelCapabilities Capabilities { get; } = new()
    {
        SupportsEdit = true,
        SupportsDelete = true,
        SupportsThread = true,
        Streaming = StreamingSupport.EditLoopRateLimited,
        SupportsFiles = true,
        MaxMessageLength = 4000,
        SupportsActionButtons = true,
        SupportsConfirmDialog = true,
        SupportsModal = false,
        SupportsMention = true,
        SupportsTyping = false,
        SupportsReactions = false,
        RecommendedStreamDebounceMs = 250,
        Transport = TransportMode.Webhook,
        SupportsEphemeral = false,
    };

    public ChannelReader<ChatActivity> InboundStream => _inbound.Reader;

    public Task InitializeAsync(ChannelTransportBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_initialized) throw new InvalidOperationException("Already initialized.");
        _harness.ActiveBinding = binding;
        _initialized = true;
        return Task.CompletedTask;
    }

    public Task StartReceivingAsync(CancellationToken ct)
    {
        if (!_initialized) throw new InvalidOperationException("Initialize first.");
        _receiving = true;
        return Task.CompletedTask;
    }

    public Task StopReceivingAsync(CancellationToken ct)
    {
        _receiving = false;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task<EmitResult> SendAsync(ConversationReference to, MessageContent content, CancellationToken ct)
    {
        EnsureReady();
        if (content.Disposition == MessageDisposition.Ephemeral && !Capabilities.SupportsEphemeral)
            return Task.FromResult(EmitResult.Sent(NextActivityId(), ComposeCapability.Degraded));

        var capability = content.Actions.Count > 0 && !Capabilities.SupportsActionButtons
            ? ComposeCapability.Degraded
            : ComposeCapability.Exact;
        return Task.FromResult(EmitResult.Sent(NextActivityId(), capability));
    }

    public Task<EmitResult> UpdateAsync(ConversationReference to, string activityId, MessageContent content, CancellationToken ct)
    {
        EnsureReady();
        return Task.FromResult(EmitResult.Sent(activityId));
    }

    public Task DeleteAsync(ConversationReference to, string activityId, CancellationToken ct)
    {
        EnsureReady();
        return Task.CompletedTask;
    }

    public Task<EmitResult> ContinueConversationAsync(ConversationReference reference, MessageContent content, AuthContext auth, CancellationToken ct)
    {
        EnsureReady();
        return Task.FromResult(EmitResult.Sent(NextActivityId()));
    }

    internal async Task<ChatActivity> InjectInboundAsync(InboundActivitySeed seed, ChannelTransportBinding binding)
    {
        var activityId = seed.PlatformMessageId ?? NextActivityId();
        var activity = new ChatActivity
        {
            Id = activityId,
            Type = seed.ActivityType,
            ChannelId = Channel,
            Bot = binding.Bot.Bot,
            Conversation = ConversationReference.Create(
                Channel,
                binding.Bot.Bot,
                seed.Scope,
                partition: null,
                seed.ConversationKey),
            From = new ParticipantRef
            {
                CanonicalId = seed.SenderCanonicalId,
                DisplayName = seed.SenderDisplayName,
            },
            Content = new MessageContent
            {
                Text = seed.Text,
                Disposition = MessageDisposition.Normal,
            },
        };

        if (seed.Mentions is not null)
        {
            foreach (var mention in seed.Mentions)
                activity.Mentions.Add(mention);
        }

        if (seed.ReplyToActivityId is not null)
            activity.ReplyToActivityId = seed.ReplyToActivityId;

        await _inbound.Writer.WriteAsync(activity);
        return activity;
    }

    private void EnsureReady()
    {
        if (!_initialized || !_receiving)
            throw new InvalidOperationException("Adapter not ready.");
    }

    private string NextActivityId() => $"stub-{Interlocked.Increment(ref _nextActivityId)}";
}

public sealed class StubAdapterHarness
{
    private StubChannelAdapter _adapter;
    private StubWebhookFixture _webhook;

    public StubAdapterHarness()
    {
        _adapter = new StubChannelAdapter(this);
        _webhook = new StubWebhookFixture(this, _adapter);
    }

    public ChannelTransportBinding DefaultBinding { get; } = ChannelTransportBinding.Create(
        ChannelBotDescriptor.Create("reg-primary", ChannelId.From("stub"), BotInstanceId.From("primary-bot")),
        "vault://stub/primary");

    public ChannelTransportBinding SecondaryBinding { get; } = ChannelTransportBinding.Create(
        ChannelBotDescriptor.Create("reg-secondary", ChannelId.From("stub"), BotInstanceId.From("secondary-bot")),
        "vault://stub/secondary");

    public ChannelTransportBinding? ActiveBinding { get; internal set; }

    public StubChannelAdapter Reset()
    {
        _adapter = new StubChannelAdapter(this);
        _webhook = new StubWebhookFixture(this, _adapter);
        return _adapter;
    }

    public WebhookFixture Webhook => _webhook;
}

internal sealed class StubWebhookFixture : WebhookFixture
{
    private readonly StubAdapterHarness _harness;
    private readonly StubChannelAdapter _adapter;
    private InboundActivitySeed? _last;
    private ChatActivity? _lastActivity;

    public StubWebhookFixture(StubAdapterHarness harness, StubChannelAdapter adapter)
    {
        _harness = harness;
        _adapter = adapter;
    }

    public override async Task<ChatActivity> DispatchInboundAsync(InboundActivitySeed seed, CancellationToken ct = default)
    {
        var binding = _harness.ActiveBinding ?? _harness.DefaultBinding;
        _last = seed;
        _lastActivity = await _adapter.InjectInboundAsync(seed, binding);
        return _lastActivity;
    }

    public override async Task<ChatActivity?> ReplayLastInboundAsync(CancellationToken ct = default)
    {
        if (_last is null)
            return null;
        var binding = _harness.ActiveBinding ?? _harness.DefaultBinding;
        _lastActivity = await _adapter.InjectInboundAsync(_last, binding);
        return _lastActivity;
    }

    public override string? LastPersistedBlobRef => _lastActivity?.RawPayloadBlobRef;

    public override byte[]? LastRawPayloadBytes => null;
}

public sealed class StubMessageComposer : IMessageComposer<StubNativePayload>
{
    public static ChannelCapabilities DefaultCapabilities { get; } = new()
    {
        SupportsEdit = true,
        SupportsDelete = true,
        SupportsActionButtons = true,
        SupportsMention = true,
        MaxMessageLength = 200,
        Streaming = StreamingSupport.None,
        Transport = TransportMode.Webhook,
    };

    public ChannelId Channel { get; } = ChannelId.From("stub");

    public StubNativePayload Compose(MessageContent intent, ComposeContext context)
    {
        var max = context.Capabilities.MaxMessageLength > 0
            ? context.Capabilities.MaxMessageLength
            : int.MaxValue;
        var text = intent.Text.Length <= max ? intent.Text : intent.Text[..max];
        return new StubNativePayload(
            Text: text,
            ActionCount: intent.Actions.Count,
            CardCount: intent.Cards.Count);
    }

    object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context)
    {
        if (intent.Disposition == MessageDisposition.Ephemeral && !context.Capabilities.SupportsEphemeral)
            return ComposeCapability.Degraded;
        if (intent.Actions.Count > 0 && !context.Capabilities.SupportsActionButtons)
            return ComposeCapability.Degraded;
        return ComposeCapability.Exact;
    }
}

public sealed record StubNativePayload(string Text, int ActionCount, int CardCount);
