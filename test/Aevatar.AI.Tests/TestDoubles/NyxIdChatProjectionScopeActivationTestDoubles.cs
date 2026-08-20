using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Streaming;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public partial class NyxIdChatEndpointsCoverageTests
{
    /// <summary>
    /// Stand-in for the projection scope agent: on ensure it publishes its observation relay
    /// (the activation evidence <c>ProjectionScopeActivationService</c> waits for) and on release it removes it,
    /// mirroring what the real scope agent writes through the runtime stream provider.
    /// </summary>
    private sealed class StubProjectionScopeActor(
        string id,
        string agentKind,
        IStreamForwardingRegistry forwardingRegistry) : IActor
    {
        private static readonly string CommittedStateTypeUrl = Any.Pack(new CommittedStateEventPublished()).TypeUrl;

        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent();
        public List<EventEnvelope> HandledEnvelopes { get; } = [];
        public bool Released { get; private set; }
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            HandledEnvelopes.Add(envelope);
            if (envelope.Payload?.Is(EnsureProjectionScopeCommand.Descriptor) == true)
            {
                var command = envelope.Payload.Unpack<EnsureProjectionScopeCommand>();
                await forwardingRegistry.UpsertAsync(
                    new StreamForwardingBinding
                    {
                        SourceStreamId = command.RootActorId,
                        TargetStreamId = Id,
                        ForwardingMode = StreamForwardingMode.HandleThenForward,
                        DirectionFilter = [],
                        EventTypeFilter = new HashSet<string>(StringComparer.Ordinal) { CommittedStateTypeUrl },
                        TargetActorKind = agentKind,
                        ActivationGeneration = 1,
                    },
                    ct);
            }
            else if (envelope.Payload?.Is(ReleaseProjectionScopeCommand.Descriptor) == true)
            {
                var command = envelope.Payload.Unpack<ReleaseProjectionScopeCommand>();
                await forwardingRegistry.RemoveAsync(command.RootActorId, Id, ct);
                Released = true;
            }
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}

internal static class NyxIdChatInteractionTestServiceCollectionExtensions
{
    /// <summary>
    /// Projection scope activation/release (wired by <c>AddNyxIdChat</c>) proves relay readiness through the
    /// stream forwarding authority and registry; expose the runtime-owned in-memory registry under both
    /// contracts exactly as the local runtime does.
    /// </summary>
    public static IServiceCollection AddStreamForwarding(
        this IServiceCollection services,
        InMemoryStreamForwardingRegistry forwardingRegistry) =>
        services
            .AddSingleton<IStreamForwardingRegistry>(forwardingRegistry)
            .AddSingleton<IStreamForwardingBindingAuthority>(forwardingRegistry);
}
