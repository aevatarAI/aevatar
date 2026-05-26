using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Infrastructure.Ports;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptCommandOutcomePublisherTests
{
    [Theory]
    [InlineData("bound")]
    [InlineData("committed")]
    public void Constructor_ShouldRejectNullChannels(string missingChannel)
    {
        var boundChannel = new StreamActorOutcomeChannel<ScriptBehaviorBoundEvent>(new InMemoryStreamProvider());
        var committedChannel = new StreamActorOutcomeChannel<ScriptDomainFactCommitted>(new InMemoryStreamProvider());

        Action act = missingChannel switch
        {
            "bound" => () => _ = new ScriptCommandOutcomePublisher(null!, committedChannel),
            "committed" => () => _ = new ScriptCommandOutcomePublisher(boundChannel, null!),
            _ => throw new InvalidOperationException("Unexpected channel name."),
        };

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be(missingChannel == "bound" ? "boundOutcomes" : "committedFactOutcomes");
    }

    [Fact]
    public async Task ObserveBoundAsync_ShouldReturnPublishedBoundOutcome()
    {
        var publisher = CreatePublisher();
        var observe = publisher.ObserveBoundAsync("command-bind", CancellationToken.None);
        var bound = new ScriptBehaviorBoundEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            CommandId = "command-bind",
        };

        await publisher.PublishBoundAsync("command-bind", bound, CancellationToken.None);

        var observed = await observe.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().BeEquivalentTo(bound);
    }

    [Fact]
    public async Task ObserveCommittedFactAsync_ShouldReturnPublishedCommittedFactOutcome()
    {
        var publisher = CreatePublisher();
        var observe = publisher.ObserveCommittedFactAsync("command-run", CancellationToken.None);
        var fact = new ScriptDomainFactCommitted
        {
            ActorId = "runtime-1",
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            RunId = "run-1",
            CommandId = "command-run",
            CorrelationId = "corr-1",
            EventSequence = 1,
            EventType = "type.googleapis.com/test.Event",
            StateVersion = 2,
            OccurredAtUnixTimeMs = 1234,
        };

        await publisher.PublishCommittedFactAsync("command-run", fact, CancellationToken.None);

        var observed = await observe.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().BeEquivalentTo(fact);
    }

    [Fact]
    public async Task PublishAsync_ShouldIgnoreBlankCommandIds()
    {
        var boundChannel = new RecordingOutcomeChannel<ScriptBehaviorBoundEvent>();
        var committedChannel = new RecordingOutcomeChannel<ScriptDomainFactCommitted>();
        var publisher = new ScriptCommandOutcomePublisher(boundChannel, committedChannel);

        await publisher.PublishBoundAsync(" ", new ScriptBehaviorBoundEvent(), CancellationToken.None);
        await publisher.PublishCommittedFactAsync(string.Empty, new ScriptDomainFactCommitted(), CancellationToken.None);

        boundChannel.Published.Should().BeEmpty();
        committedChannel.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task RuntimeScriptCommandService_ShouldUseTypedOutcomeDispatch_WhenOutcomeSucceeds()
    {
        RunScriptRuntimeCommand? capturedCommand = null;
        var outcomeService = new StaticOutcomeDispatchService<RunScriptRuntimeCommand, ScriptDomainFactCommitted>(command =>
        {
            capturedCommand = command;
            return Task.FromResult(CommandOutcomeDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptDomainFactCommitted>.Success(
                new ScriptingCommandAcceptedReceipt("runtime-1", "command-1", "corr-1"),
                new ScriptDomainFactCommitted { CommandId = "command-1" }));
        });
        var service = new RuntimeScriptCommandService(
            new ThrowingDispatchService<RunScriptRuntimeCommand>(),
            outcomeService);

        await service.RunRuntimeAsync(
            "runtime-1",
            "run-1",
            "command-1",
            "corr-1",
            Any.Pack(new Empty()),
            "rev-1",
            "definition-1",
            "type.googleapis.com/test.Command",
            "scope-1",
            CancellationToken.None);

        outcomeService.CallCount.Should().Be(1);
        capturedCommand.Should().NotBeNull();
        capturedCommand!.RuntimeActorId.Should().Be("runtime-1");
        capturedCommand.RunId.Should().Be("run-1");
        capturedCommand.CommandId.Should().Be("command-1");
        capturedCommand.CorrelationId.Should().Be("corr-1");
        capturedCommand.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task RuntimeScriptCommandService_ShouldThrowTypedFailure_FromOutcomeDispatch()
    {
        var service = new RuntimeScriptCommandService(
            new ThrowingDispatchService<RunScriptRuntimeCommand>(),
            new StaticOutcomeDispatchService<RunScriptRuntimeCommand, ScriptDomainFactCommitted>(_ => Task.FromResult(
                CommandOutcomeDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptDomainFactCommitted>.Failure(
                    ScriptingCommandStartError.InvalidArgument("runId", "run id is required")))));

        var act = () => service.RunRuntimeAsync(
            "runtime-1",
            "run-1",
            null,
            "rev-1",
            "definition-1",
            "type.googleapis.com/test.Command",
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("run id is required*");
    }

    [Fact]
    public async Task RuntimeScriptCommandService_ShouldThrowMissingReceiptFallback_FromOutcomeDispatch()
    {
        var service = new RuntimeScriptCommandService(
            new ThrowingDispatchService<RunScriptRuntimeCommand>(),
            new StaticOutcomeDispatchService<RunScriptRuntimeCommand, ScriptDomainFactCommitted>(_ => Task.FromResult(
                new CommandOutcomeDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptDomainFactCommitted>
                {
                    Succeeded = true,
                    Error = null!,
                    Receipt = null,
                    Outcome = new ScriptDomainFactCommitted(),
                })));

        var act = () => service.RunRuntimeAsync(
            "runtime-1",
            "run-1",
            null,
            "rev-1",
            "definition-1",
            "type.googleapis.com/test.Command",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script runtime dispatch did not produce a receipt.");
    }

    [Fact]
    public async Task RuntimeScriptProvisioningService_ShouldUseTypedOutcomeDispatch_WhenOutcomeSucceeds()
    {
        ProvisionScriptRuntimeCommand? capturedCommand = null;
        var outcomeService = new StaticOutcomeDispatchService<ProvisionScriptRuntimeCommand, ScriptBehaviorBoundEvent>(command =>
        {
            capturedCommand = command;
            return Task.FromResult(CommandOutcomeDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptBehaviorBoundEvent>.Success(
                new ScriptingCommandAcceptedReceipt("runtime-1", "command-1", "corr-1"),
                new ScriptBehaviorBoundEvent { CommandId = "command-1" }));
        });
        var service = new RuntimeScriptProvisioningService(
            new ThrowingDispatchService<ProvisionScriptRuntimeCommand>(),
            outcomeService);

        var actorId = await service.EnsureRuntimeAsync(
            "definition-1",
            "rev-1",
            "runtime-1",
            CreateDefinitionSnapshot(),
            "scope-1",
            CancellationToken.None);

        actorId.Should().Be("runtime-1");
        outcomeService.CallCount.Should().Be(1);
        capturedCommand.Should().NotBeNull();
        capturedCommand!.DefinitionActorId.Should().Be("definition-1");
        capturedCommand.ScriptRevision.Should().Be("rev-1");
        capturedCommand.RuntimeActorId.Should().Be("runtime-1");
        capturedCommand.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task RuntimeScriptProvisioningService_ShouldThrowTypedFailure_FromOutcomeDispatch()
    {
        var service = new RuntimeScriptProvisioningService(
            new ThrowingDispatchService<ProvisionScriptRuntimeCommand>(),
            new StaticOutcomeDispatchService<ProvisionScriptRuntimeCommand, ScriptBehaviorBoundEvent>(_ => Task.FromResult(
                CommandOutcomeDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptBehaviorBoundEvent>.Failure(
                    ScriptingCommandStartError.ActorNotFound("definition-1", "definition actor was not found")))));

        var act = () => service.EnsureRuntimeAsync(
            "definition-1",
            "rev-1",
            null,
            CreateDefinitionSnapshot(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("definition actor was not found");
    }

    [Fact]
    public async Task RuntimeScriptProvisioningService_ShouldThrowMissingReceiptFallback_FromOutcomeDispatch()
    {
        var service = new RuntimeScriptProvisioningService(
            new ThrowingDispatchService<ProvisionScriptRuntimeCommand>(),
            new StaticOutcomeDispatchService<ProvisionScriptRuntimeCommand, ScriptBehaviorBoundEvent>(_ => Task.FromResult(
                new CommandOutcomeDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptBehaviorBoundEvent>
                {
                    Succeeded = true,
                    Error = null!,
                    Receipt = null,
                    Outcome = new ScriptBehaviorBoundEvent(),
                })));

        var act = () => service.EnsureRuntimeAsync(
            "definition-1",
            "rev-1",
            null,
            CreateDefinitionSnapshot(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script runtime provisioning did not produce a receipt.");
    }

    private static ScriptCommandOutcomePublisher CreatePublisher() =>
        new(
            new StreamActorOutcomeChannel<ScriptBehaviorBoundEvent>(new InMemoryStreamProvider()),
            new StreamActorOutcomeChannel<ScriptDomainFactCommitted>(new InMemoryStreamProvider()));

    private static ScriptDefinitionSnapshot CreateDefinitionSnapshot(string revision = "rev-1") =>
        new(
            "script-1",
            revision,
            "public sealed class Behavior {}",
            "hash-1",
            "type.googleapis.com/example.State",
            "type.googleapis.com/example.ReadModel",
            "2",
            "schema-hash-1");

    private sealed class StaticOutcomeDispatchService<TCommand, TOutcome>(
        Func<TCommand, Task<CommandOutcomeDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, TOutcome>>> dispatch)
        : ICommandOutcomeDispatchService<TCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, TOutcome>
        where TOutcome : Google.Protobuf.IMessage, new()
    {
        public int CallCount { get; private set; }

        public Task<CommandOutcomeDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, TOutcome>> DispatchAndAwaitOutcomeAsync(
            TCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return dispatch(command);
        }
    }

    private sealed class ThrowingDispatchService<TCommand>
        : ICommandDispatchService<TCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
    {
        public Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>> DispatchAsync(
            TCommand command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Legacy dispatch should not run when outcome dispatch is configured.");
        }
    }

    private sealed class RecordingOutcomeChannel<TOutcome> : IActorOutcomeChannel<TOutcome>
        where TOutcome : Google.Protobuf.IMessage, new()
    {
        public List<(string CommandId, TOutcome Outcome)> Published { get; } = [];

        public Task<ActorOutcomeSubscription<TOutcome>> SubscribeAsync(
            string commandId,
            CancellationToken ct = default)
        {
            _ = commandId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ActorOutcomeSubscription<TOutcome>(
                Task.FromCanceled<TOutcome>(new CancellationToken(true)),
                () => ValueTask.CompletedTask));
        }

        public Task PublishAsync(
            string commandId,
            TOutcome outcome,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((commandId, outcome));
            return Task.CompletedTask;
        }
    }
}
