using System.Reflection;
using System.Threading.Channels;
using Aevatar.GAgents.Channel.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ChannelAdapterConformanceConstraintTests
{
    [Fact]
    public void ChannelAdapterConformanceSuite_ShouldRequireTransportAndOutboundPortOnTheSameType()
    {
        var adapterType = typeof(ChannelAdapterConformanceSuite<>).GetGenericArguments()[0];
        var constraints = adapterType.GetGenericParameterConstraints();

        adapterType.GenericParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint).ShouldBeTrue();
        constraints.ShouldContain(typeof(IChannelTransport));
        constraints.ShouldContain(typeof(IChannelOutboundPort));
    }
}

public sealed class StubChannelAdapterConformanceTests : ChannelAdapterConformanceSuite<StubChannelAdapter>
{
    protected override StubChannelAdapter CreateAdapter() => new();
}

public abstract class ChannelAdapterConformanceSuite<TAdapter>
    where TAdapter : class, IChannelTransport, IChannelOutboundPort
{
    protected abstract TAdapter CreateAdapter();

    [Fact]
    public void AdapterInstance_ShouldShareTransportAndOutboundState()
    {
        var adapter = CreateAdapter();
        IChannelTransport transport = adapter;
        IChannelOutboundPort outbound = adapter;

        ReferenceEquals(transport, outbound).ShouldBeTrue();
        transport.Channel.Value.ShouldBe(outbound.Channel.Value);
        transport.Capabilities.ShouldBe(outbound.Capabilities);
    }
}

public sealed class StubChannelAdapter : IChannelTransport, IChannelOutboundPort
{
    private readonly Channel<ChatActivity> _inbound = System.Threading.Channels.Channel.CreateBounded<ChatActivity>(8);

    public ChannelId Channel { get; } = ChannelId.From("slack");

    public TransportMode TransportMode { get; } = TransportMode.Webhook;

    public ChannelCapabilities Capabilities { get; } = new()
    {
        SupportsEdit = true,
        SupportsDelete = true,
        Streaming = StreamingSupport.Native,
        Transport = TransportMode.Webhook,
    };

    public ChannelReader<ChatActivity> InboundStream => _inbound.Reader;

    public Task InitializeAsync(ChannelTransportBinding binding, CancellationToken ct) => Task.CompletedTask;

    public Task StartReceivingAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopReceivingAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<EmitResult> SendAsync(ConversationReference to, MessageContent content, CancellationToken ct) =>
        Task.FromResult(EmitResult.Sent("sent-1"));

    public Task<EmitResult> UpdateAsync(
        ConversationReference to,
        string activityId,
        MessageContent content,
        CancellationToken ct) => Task.FromResult(EmitResult.Sent(activityId));

    public Task DeleteAsync(ConversationReference to, string activityId, CancellationToken ct) => Task.CompletedTask;

    public Task<EmitResult> ContinueConversationAsync(
        ConversationReference reference,
        MessageContent content,
        AuthContext auth,
        CancellationToken ct) => Task.FromResult(EmitResult.Sent("continued-1"));
}
