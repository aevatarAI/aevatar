using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class LlmRunEndToEndAcceptanceTests
{
    [Fact]
    public async Task RecorderCommands_ShouldKeepTerminalCompletionAuthoritativeAcrossDuplicatesAndLateChunks()
    {
        var actor = await CreateRegisteredActorAsync();
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Chunk("hello ", sequence: 1)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Chunk("duplicate-ignored", sequence: 1)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.LocalToolCall("call_1", sequence: 2)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Completed("hello world", sequence: 4)));
        var versionAfterCompletion = actor.State.LastAppliedEventVersion;

        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Chunk("late", sequence: 3)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Completed("hello world", sequence: 4)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Failed(sequence: 5)));

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterCompletion);
        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Completed);
        actor.State.ActiveRun!.Status.Should().Be(2);
        actor.State.ActiveRun.OutputText.Should().Be("hello world");
        actor.State.ActiveRun.LastAppliedSequence.Should().Be(4);
        actor.State.Completion!.OutputText.Should().Be("hello world");
        actor.State.Completion.FailureCode.Should().BeEmpty();
    }

    [Fact]
    public async Task RecorderCommands_ShouldKeepCancelledAuthoritativeAndIgnoreFlushAfterCancel()
    {
        var actor = await CreateRegisteredActorAsync();
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Chunk("partial", sequence: 1)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Cancelled(sequence: 2)));
        var versionAfterCancel = actor.State.LastAppliedEventVersion;

        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Chunk("late", sequence: 3)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Completed("partial late", sequence: 4)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Cancelled(sequence: 2)));

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterCancel);
        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Cancelled);
        actor.State.ActiveRun!.Status.Should().Be(4);
        actor.State.ActiveRun.OutputText.Should().Be("partial");
        actor.State.Completion!.FailureCode.Should().Be("request_cancelled");
        actor.State.Completion.OutputText.Should().Be("partial");
    }

    [Fact]
    public async Task RecorderCommands_ShouldKeepFailedAuthoritativeAndIgnoreLaterTerminalFacts()
    {
        var actor = await CreateRegisteredActorAsync();
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Chunk("partial", sequence: 1)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Failed("provider_unavailable", sequence: 2)));
        var versionAfterFailure = actor.State.LastAppliedEventVersion;

        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Chunk("late", sequence: 3)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Completed("partial late", sequence: 4)));
        await actor.HandleEventAsync(Envelope(LlmRunAcceptanceHarness.Cancelled(sequence: 5)));

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterFailure);
        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Failed);
        actor.State.ActiveRun!.Status.Should().Be(3);
        actor.State.ActiveRun.OutputText.Should().Be("partial");
        actor.State.ActiveRun.FailureCode.Should().Be("provider_unavailable");
        actor.State.ActiveRun.LastAppliedSequence.Should().Be(2);
        actor.State.Completion!.FailureCode.Should().Be("provider_unavailable");
        actor.State.Completion.OutputText.Should().Be("partial");
    }

    [Fact]
    public async Task Replay_ShouldApplyTerminalEventDespiteMissingIntermediateRecorderCommand()
    {
        var eventStore = new InMemoryEventStore();
        await eventStore.AppendAsync(
            LlmRunAcceptanceHarness.ActorId,
            [
                LlmRunAcceptanceHarness.StateEvent(1, new LlmSessionRegisteredEvent
                {
                    Record = LlmRunAcceptanceHarness.BuildRecord(),
                }),
                LlmRunAcceptanceHarness.StateEvent(2, new LlmRunStartedEvent
                {
                    ResponseId = LlmRunAcceptanceHarness.ResponseId,
                    RunId = LlmRunAcceptanceHarness.RunId,
                    Sequence = 1,
                }),
                LlmRunAcceptanceHarness.StateEvent(3, LlmRunAcceptanceHarness.Chunk("first", sequence: 2)),
                LlmRunAcceptanceHarness.StateEvent(4, LlmRunAcceptanceHarness.Completed("first final", sequence: 5)),
                LlmRunAcceptanceHarness.StateEvent(5, LlmRunAcceptanceHarness.Chunk("late", sequence: 3)),
            ],
            expectedVersion: 0);
        var actor = CreateActor(eventStore);

        await actor.ActivateAsync();

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Completed);
        actor.State.ActiveRun!.OutputText.Should().Be("first final");
        actor.State.ActiveRun.LastAppliedSequence.Should().Be(5);
        actor.State.LastAppliedEventVersion.Should().Be(4);
    }

    private static async Task<LlmSessionGAgent> CreateRegisteredActorAsync()
    {
        var actor = CreateActor(new InMemoryEventStore());
        var record = LlmRunAcceptanceHarness.BuildRecord();
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested { Record = record });
        return actor;
    }

    private static LlmSessionGAgent CreateActor(InMemoryEventStore eventStore, string? responseId = null) =>
        GAgentServiceTestKit.CreateStatefulAgent<LlmSessionGAgent, LlmSessionState>(
            eventStore,
            responseId is null
                ? LlmRunAcceptanceHarness.ActorId
                : "response-session-actor-" + responseId,
            static () => null!,
            services => services.AddSingleton<ILlmRunCore, ThrowingRunCore>());

    private static EventEnvelope Envelope(Google.Protobuf.IMessage payload) =>
        new()
        {
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(payload),
        };

    private sealed class ThrowingRunCore : ILlmRunCore
    {
        public Task RunAsync(LlmRunCoreRequest request, ILlmRunSink sink, CancellationToken ct = default) =>
            throw new InvalidOperationException("Run core should not be called by recorder acceptance tests.");
    }
}
