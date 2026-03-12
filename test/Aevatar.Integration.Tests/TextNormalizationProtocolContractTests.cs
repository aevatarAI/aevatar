using System.Threading.Channels;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.TestDoubles.Protocols;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class TextNormalizationProtocolContractTests
{
    [Theory]
    [InlineData(ProtocolSourceKind.Static)]
    [InlineData(ProtocolSourceKind.Workflow)]
    [InlineData(ProtocolSourceKind.Scripting)]
    public async Task Source_ShouldSatisfyTextNormalizationProtocolContract(ProtocolSourceKind sourceKind)
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddAevatarWorkflow();
        services.AddScriptCapability();
        services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();

        using var provider = services.BuildServiceProvider();
        var harness = await TextNormalizationProtocolHarness.CreateAsync(provider, sourceKind);
        var projectedReadModels = new Dictionary<string, TextNormalizationReadModel>(StringComparer.Ordinal);
        var completions = Channel.CreateUnbounded<TextNormalizationCompleted>();

        await using var subscription = await harness.Streams
            .GetStream(harness.ActorId)
            .SubscribeAsync<EventEnvelope>(envelope =>
            {
                if (!envelope.Route.IsObserve() ||
                    envelope.Payload?.Is(TextNormalizationCompleted.Descriptor) != true)
                {
                    return Task.CompletedTask;
                }

                var completed = envelope.Payload.Unpack<TextNormalizationCompleted>();
                projectedReadModels[harness.ActorId] = completed.Current?.Clone() ?? new TextNormalizationReadModel();
                completions.Writer.TryWrite(completed);
                return Task.CompletedTask;
            });

        var initial = await harness.QueryAsync(CancellationToken.None);
        initial.Current.Should().NotBeNull();
        initial.Current.HasValue.Should().BeFalse();

        await AssertContractRoundTripAsync(
            harness,
            completions.Reader,
            projectedReadModels,
            "cmd-1",
            "  Mixed Case  ",
            "MIXED CASE");
        await AssertContractRoundTripAsync(
            harness,
            completions.Reader,
            projectedReadModels,
            "cmd-2",
            " next-value ",
            "NEXT-VALUE");
    }

    private static async Task AssertContractRoundTripAsync(
        TextNormalizationProtocolHarness harness,
        ChannelReader<TextNormalizationCompleted> completionReader,
        IReadOnlyDictionary<string, TextNormalizationReadModel> projectedReadModels,
        string commandId,
        string input,
        string expectedNormalized)
    {
        await harness.SendAsync(
            new TextNormalizationRequested
            {
                CommandId = commandId,
                InputText = input,
            },
            CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completion = await completionReader.ReadAsync(timeout.Token);
        completion.CommandId.Should().Be(commandId);
        completion.Current.Should().NotBeNull();
        completion.Current.HasValue.Should().BeTrue();
        completion.Current.LastCommandId.Should().Be(commandId);
        completion.Current.InputText.Should().Be(input);
        completion.Current.NormalizedText.Should().Be(expectedNormalized);

        var queried = await harness.QueryAsync(CancellationToken.None);
        queried.Current.Should().NotBeNull();
        queried.Current.HasValue.Should().BeTrue();
        queried.Current.LastCommandId.Should().Be(commandId);
        queried.Current.InputText.Should().Be(input);
        queried.Current.NormalizedText.Should().Be(expectedNormalized);

        projectedReadModels.Should().ContainKey(harness.ActorId);
        projectedReadModels[harness.ActorId].HasValue.Should().BeTrue();
        projectedReadModels[harness.ActorId].LastCommandId.Should().Be(commandId);
        projectedReadModels[harness.ActorId].InputText.Should().Be(input);
        projectedReadModels[harness.ActorId].NormalizedText.Should().Be(expectedNormalized);
    }

    public enum ProtocolSourceKind
    {
        Static,
        Workflow,
        Scripting,
    }

    private sealed class TextNormalizationProtocolHarness
    {
        private readonly IActor _actor;
        private readonly IStreamRequestReplyClient _requestReplyClient;

        private TextNormalizationProtocolHarness(
            IActor actor,
            IStreamProvider streams,
            IStreamRequestReplyClient requestReplyClient)
        {
            _actor = actor;
            Streams = streams;
            _requestReplyClient = requestReplyClient;
        }

        public string ActorId => _actor.Id;

        public IStreamProvider Streams { get; }

        public static async Task<TextNormalizationProtocolHarness> CreateAsync(
            IServiceProvider provider,
            ProtocolSourceKind sourceKind)
        {
            var runtime = provider.GetRequiredService<IActorRuntime>();
            var streams = provider.GetRequiredService<IStreamProvider>();
            var requestReplyClient = provider.GetRequiredService<IStreamRequestReplyClient>();
            var actorId = $"protocol-{sourceKind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";
            var actor = sourceKind switch
            {
                ProtocolSourceKind.Static => await runtime.CreateAsync<TextNormalizationStaticGAgent>(actorId, CancellationToken.None),
                ProtocolSourceKind.Workflow => await runtime.CreateAsync<TextNormalizationWorkflowProtocolGAgent>(actorId, CancellationToken.None),
                ProtocolSourceKind.Scripting => await runtime.CreateAsync<TextNormalizationScriptingProtocolGAgent>(actorId, CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, null),
            };

            return new TextNormalizationProtocolHarness(actor, streams, requestReplyClient);
        }

        public Task SendAsync(TextNormalizationRequested request, CancellationToken ct) =>
            _actor.HandleEventAsync(CreateEnvelope(request), ct);

        public Task<TextNormalizationQueryResponded> QueryAsync(CancellationToken ct) =>
            _requestReplyClient.QueryActorAsync<TextNormalizationQueryResponded>(
                Streams,
                _actor,
                "text-normalization-query",
                TimeSpan.FromSeconds(5),
                static (requestId, replyStreamId) => CreateEnvelope(
                    new TextNormalizationQueryRequested
                    {
                        RequestId = requestId,
                        ReplyStreamId = replyStreamId,
                    }),
                static (response, requestId) => string.Equals(response.RequestId, requestId, StringComparison.Ordinal),
                static requestId => $"Text normalization query timed out. request_id={requestId}",
                ct);

        private static EventEnvelope CreateEnvelope(IMessage payload) =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(payload),
                Route = EnvelopeRouteSemantics.CreateBroadcast("contract-test", BroadcastDirection.Self),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = Guid.NewGuid().ToString("N"),
                },
            };
    }
}
