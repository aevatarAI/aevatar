using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ExternalCallbackProjectionTests
{
    [Fact]
    public async Task CommittedActorSnapshot_ProjectsWithSourceVersion_AndResolvesBothExactKeys()
    {
        using var provider = Services();
        var projector = provider.GetRequiredService<ExternalCallbackCurrentStateProjector>();
        await projector.ProjectAsync(Context(), Envelope(7, "event-seven", CallbackResult.Succeeded));
        var query = provider.GetRequiredService<IExternalCallbackQueryPort>();
        var byCallback = await query.FindAsync("callback-one", null);
        var byLink = await query.FindAsync(null, "external-link-one");
        byCallback.Should().NotBeNull();
        byLink.Should().BeEquivalentTo(byCallback);
        byCallback!.Id.Should().Be("opaque-actor-one");
        byCallback.StateVersion.Should().Be(7);
        byCallback.LastEventId.Should().Be("event-seven");
        byCallback.Snapshot.Result.Should().Be(CallbackResult.Succeeded);
        ExternalCallbackCurrentStateDocument.Descriptor.Fields.InDeclarationOrder().Select(x => x.Name)
            .Should().NotContain(["authorization", "oauth_authorize_url", "oauth_preparation"]);
    }

    [Fact]
    public async Task Provider_RejectsOlderStateAndConflictingVersion_AndAcceptsDuplicate()
    {
        using var provider = Services();
        var projector = provider.GetRequiredService<ExternalCallbackCurrentStateProjector>();
        await projector.ProjectAsync(Context(), Envelope(7, "event-seven", CallbackResult.Succeeded));
        await projector.ProjectAsync(Context(), Envelope(7, "event-seven", CallbackResult.Succeeded));
        await projector.ProjectAsync(Context(), Envelope(6, "event-six", CallbackResult.Unspecified));
        var query = provider.GetRequiredService<IExternalCallbackQueryPort>();
        (await query.FindAsync("callback-one", null))!.StateVersion.Should().Be(7);
        var conflicting = () => projector.ProjectAsync(Context(), Envelope(7, "conflict", CallbackResult.Failed)).AsTask();
        await conflicting.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Query_RejectsMismatchingSecondKey_AndDoesNotCreateMissingState()
    {
        using var provider = Services();
        var query = provider.GetRequiredService<IExternalCallbackQueryPort>();
        (await query.FindAsync("missing", null)).Should().BeNull();
        await provider.GetRequiredService<ExternalCallbackCurrentStateProjector>().ProjectAsync(Context(), Envelope(1, "first", CallbackResult.Unspecified));
        var mismatch = () => query.FindAsync("callback-one", "wrong-external");
        await mismatch.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task IncomingCommand_IsNotProjectionEvidence()
    {
        using var provider = Services();
        await provider.GetRequiredService<ExternalCallbackCurrentStateProjector>().ProjectAsync(Context(), new()
        { Payload = Any.Pack(new RegisterExternalCallback()) });
        (await provider.GetRequiredService<IExternalCallbackQueryPort>().FindAsync("callback-one", null)).Should().BeNull();
    }

    [Fact]
    public void Activation_IsOwnedByCommittedWriteHook_AndGenericCompletionHasExactlyTwoFields()
    {
        var plans = new ExternalCallbackCommittedStateProjectionActivationPlanProvider().GetPlans(new CommittedStatePublicationContext
        {
            ActorId = "opaque-actor-one", ActorType = typeof(ExternalCallbackGAgent),
            Published = new CommittedStateEventPublished { StateEvent = new() { EventData = Any.Pack(new ExternalCallbackStateChanged()) } },
        });
        plans.Single().StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
        CallbackCompleted.Descriptor.Fields.InDeclarationOrder().Select(field => field.Name)
            .Should().Equal("callback_id", "result");
    }

    [Fact]
    public async Task Admission_ReturnsInboxReceiptWithoutWaitingForActorCommit_AndKeepsIdsDistinct()
    {
        var runtime = Substitute.For<IActorRuntime>();
        var dispatch = Substitute.For<IActorDispatchPort>();
        var query = Substitute.For<IExternalCallbackQueryPort>();
        dispatch.DispatchAsync(Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(DispatchAdmissionFactory.Create(call.ArgAt<string>(0), call.ArgAt<EventEnvelope>(1))));
        var port = new ExternalCallbackCommandPort(runtime, dispatch, query);
        var accepted = await port.AdmitAsync(new() { CallbackId = "opaque-callback", Kind = ExternalCallbackKind.ConnectLink });
        accepted.CallbackId.Should().Be("opaque-callback");
        accepted.OperationActorId.Should().NotBeNullOrWhiteSpace().And.NotBe(accepted.CallbackId);
        await runtime.Received(1).CreateAsync<ExternalCallbackGAgent>(accepted.OperationActorId, Arg.Any<CancellationToken>());
        await query.DidNotReceiveWithAnyArgs().FindAsync(default, default);
        await dispatch.Received(1).DispatchAsync(accepted.OperationActorId,
            Arg.Is<EventEnvelope>(envelope => envelope.Id != accepted.CallbackId && envelope.Payload.Is(RegisterExternalCallback.Descriptor)),
            Arg.Any<CancellationToken>());
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddChannelRuntime();
        services.AddInMemoryDocumentProjectionStore<ExternalCallbackCurrentStateDocument, string>(document => document.Id);
        services.AddTransient<ExternalCallbackCurrentStateProjector>();
        return services.BuildServiceProvider();
    }
    private static ExternalCallbackMaterializationContext Context() => new()
    { RootActorId = "opaque-actor-one", ProjectionKind = ExternalCallbackCommittedStateProjectionActivationPlanProvider.ProjectionKind };
    private static EventEnvelope Envelope(long version, string eventId, CallbackResult result)
    {
        var timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero));
        var state = new ExternalCallbackState
        {
            Snapshot = new()
            { CallbackId = "callback-one", OperationActorId = "opaque-actor-one", ExternalRequestId = "external-link-one", Kind = ExternalCallbackKind.ConnectLink, Result = result },
        };
        return new()
        {
            Id = eventId, Timestamp = timestamp.Clone(), Route = EnvelopeRouteSemantics.CreateObserverPublication("opaque-actor-one"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new() { Version = version, EventId = eventId, Timestamp = timestamp.Clone(), EventData = Any.Pack(new ExternalCallbackStateChanged { State = state }) },
                StateRoot = Any.Pack(state),
            }),
        };
    }
}
