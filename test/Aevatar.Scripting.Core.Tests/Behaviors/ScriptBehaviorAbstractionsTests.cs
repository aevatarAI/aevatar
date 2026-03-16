using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Abstractions.Schema;
using Aevatar.Scripting.Core.Tests.Messages;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Reflection;

namespace Aevatar.Scripting.Core.Tests.Behaviors;

public sealed class ScriptBehaviorAbstractionsTests
{
    [Fact]
    public async Task DispatchAsync_ShouldRejectUndeclaredInboundType()
    {
        var behavior = new CommandBehavior();

        var act = () => behavior.DispatchAsync(
            new SimpleTextSignal { Value = "unexpected" },
            CreateDispatchContext(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not declare inbound type*");
    }

    [Fact]
    public void ApplyDomainEvent_ShouldReturnCurrentState_WhenApplyHandlerIsMissing()
    {
        var behavior = new ReduceOnlyBehavior();
        var current = new SimpleTextState { Value = "current" };

        var result = behavior.ApplyDomainEvent(
            current,
            new SimpleTextEvent
            {
                Current = new SimpleTextReadModel { Value = "next", HasValue = true },
            },
            CreateFactContext());

        result.Should().BeSameAs(current);
    }

    [Fact]
    public void ProjectReadModel_ShouldReturnNull_WhenProjectHandlerIsMissing()
    {
        var behavior = new ApplyOnlyBehavior();
        var current = new SimpleTextState { Value = "current" };

        var result = behavior.ProjectReadModel(
            current,
            new SimpleTextEvent
            {
                Current = new SimpleTextReadModel { Value = "next", HasValue = true },
            },
            CreateFactContext());

        result.Should().BeNull();
    }

    [Fact]
    public void Descriptor_ShouldRejectDuplicateInboundRegistration()
    {
        var act = () => _ = new DuplicateInboundBehavior().Descriptor;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Inbound signal type*already registered*");
    }

    [Fact]
    public void Descriptor_ShouldRejectEventWithoutApplyOrProject()
    {
        var act = () => _ = new MissingEventHandlerBehavior().Descriptor;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*At least one of apply/project must be provided*");
    }

    [Fact]
    public void Descriptor_ShouldRejectDuplicateEventRegistration()
    {
        var act = () => _ = new DuplicateEventBehavior().Descriptor;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Domain event type*already registered*");
    }

    [Fact]
    public void ScriptCommandContext_ShouldNormalizeValues_EmitAndDrainEvents()
    {
        var context = CreateCommandContext(
            actorId: null,
            scriptId: null,
            revision: null,
            runId: null,
            messageType: null,
            messageId: null,
            commandId: null,
            correlationId: null,
            causationId: null,
            definitionActorId: null,
            currentState: null,
            runtimeCapabilities: new NoOpCapabilities());

        ReadProperty<string>(context, "ActorId").Should().BeEmpty();
        ReadProperty<string>(context, "ScriptId").Should().BeEmpty();
        ReadProperty<string>(context, "Revision").Should().BeEmpty();
        ReadProperty<string>(context, "RunId").Should().BeEmpty();
        ReadProperty<string>(context, "MessageType").Should().BeEmpty();
        ReadProperty<string>(context, "MessageId").Should().BeEmpty();
        ReadProperty<string>(context, "CommandId").Should().BeEmpty();
        ReadProperty<string>(context, "CorrelationId").Should().BeEmpty();
        ReadProperty<string>(context, "CausationId").Should().BeEmpty();
        ReadProperty<string>(context, "DefinitionActorId").Should().BeEmpty();
        ReadProperty<SimpleTextState?>(context, "CurrentState").Should().BeNull();

        InvokeEmit(context, new SimpleTextEvent
        {
            Current = new SimpleTextReadModel { Value = "hello", HasValue = true },
        });
        InvokeEmitTyped(context, new SimpleTextEvent
        {
            Current = new SimpleTextReadModel { Value = "world", HasValue = true },
        });

        var drained = DrainDomainEvents(context);
        drained.Should().HaveCount(2);
        drained.Should().OnlyContain(x => x is SimpleTextEvent);
    }

    [Fact]
    public void ScriptCommandContext_ShouldRejectNullRuntimeCapabilitiesAndNullEvents()
    {
        var ctor = () => CreateCommandContext(
            actorId: "actor-1",
            scriptId: "script-1",
            revision: "rev-1",
            runId: "run-1",
            messageType: "command",
            messageId: "msg-1",
            commandId: "cmd-1",
            correlationId: "corr-1",
            causationId: "cause-1",
            definitionActorId: "definition-1",
            currentState: new SimpleTextState(),
            runtimeCapabilities: null!);

        ctor.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentNullException>()
            .Which.ParamName.Should().Be("runtimeCapabilities");

        var context = CreateCommandContext(
            actorId: "actor-1",
            scriptId: "script-1",
            revision: "rev-1",
            runId: "run-1",
            messageType: "command",
            messageId: "msg-1",
            commandId: "cmd-1",
            correlationId: "corr-1",
            causationId: "cause-1",
            definitionActorId: "definition-1",
            currentState: new SimpleTextState(),
            runtimeCapabilities: new NoOpCapabilities());

        Action emitNull = () => InvokeEmit(context, null!);
        Action emitTypedNull = () => InvokeEmitTyped<SimpleTextEvent>(context, null!);

        emitNull.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentNullException>();
        emitTypedNull.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentNullException>();
    }

    [Fact]
    public void ScriptRuntimeSemanticsExtensions_ShouldResolveMessagesAndThrowForMissingEntries()
    {
        var typeUrl = ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextCommand));
        var spec = new ScriptRuntimeSemanticsSpec
        {
            Messages =
            {
                new ScriptMessageSemanticsSpec
                {
                    TypeUrl = typeUrl,
                    Kind = ScriptMessageKind.Command,
                    CommandIdField = "command_id",
                },
            },
        };

        spec.TryGetMessageSemantics(typeUrl, ScriptMessageKind.Command, out var commandSemantics).Should().BeTrue();
        commandSemantics.CommandIdField.Should().Be("command_id");
        spec.TryGetMessageSemantics(typeUrl, ScriptMessageKind.Unspecified, out var fallbackSemantics).Should().BeTrue();
        fallbackSemantics.Kind.Should().Be(ScriptMessageKind.Command);
        ((ScriptRuntimeSemanticsSpec?)null).TryGetMessageSemantics(typeUrl, out _).Should().BeFalse();

        Action missingMessage = () => spec.GetRequiredMessageSemantics("missing-type", ScriptMessageKind.Command);
        Action blankTypeUrl = () => ScriptRuntimeSemanticsExtensions.TryGetMessageSemantics(spec, "", out _);

        missingMessage.Should().Throw<InvalidOperationException>().WithMessage("*Runtime semantics are missing for message type*");
        blankTypeUrl.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScriptBehaviorDescriptor_ShouldCloneSemanticsAndContractMetadata()
    {
        var descriptor = new CommandBehavior().Descriptor;
        var protocol = ByteString.CopyFromUtf8("descriptor-set");

        var enriched = descriptor
            .WithProtocolDescriptorSet(protocol)
            .WithRuntimeSemantics(null!);

        enriched.ProtocolDescriptorSet!.ToByteArray().Should().Equal(protocol.ToByteArray());
        enriched.RuntimeSemantics.Should().NotBeNull();
        enriched.RuntimeSemantics!.Messages.Should().BeEmpty();

        var contract = enriched.ToContract();
        contract.ProtocolDescriptorSet!.ToByteArray().Should().Equal(protocol.ToByteArray());
        contract.StateTypeUrl.Should().Be(descriptor.StateTypeUrl);
        contract.ReadModelTypeUrl.Should().Be(descriptor.ReadModelTypeUrl);
        contract.CommandTypeUrls.Should().ContainSingle(descriptor.Commands.Keys.Single());
        contract.DomainEventTypeUrls.Should().ContainSingle(descriptor.DomainEvents.Keys.Single());
    }

    [Fact]
    public void ScriptBehaviorDescriptor_ShouldFallbackWhenProtocolAndSemanticsAreMissing()
    {
        var descriptor = new ScriptBehaviorDescriptor(
            typeof(SimpleTextState),
            typeof(SimpleTextReadModel),
            SimpleTextState.Descriptor,
            SimpleTextReadModel.Descriptor,
            ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextState)),
            ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextReadModel)),
            new Dictionary<string, ScriptCommandRegistration>(StringComparer.Ordinal),
            new Dictionary<string, ScriptSignalRegistration>(StringComparer.Ordinal),
            new Dictionary<string, ScriptDomainEventRegistration>(StringComparer.Ordinal),
            ProtocolDescriptorSet: null,
            RuntimeSemantics: null);

        var contract = descriptor.ToContract();

        contract.ProtocolDescriptorSet!.ToByteArray().Should().Equal(ByteString.Empty.ToByteArray());
        contract.RuntimeSemantics.Should().NotBeNull();
        contract.RuntimeSemantics!.Messages.Should().BeEmpty();
    }

    [Fact]
    public void ScriptReadModelDescriptorPolicy_ShouldClassifyLeafDescriptors()
    {
        ScriptReadModelDescriptorPolicy.ValidateNoUnsupportedWrapperFields(null);

        ScriptReadModelDescriptorPolicy.IsSupportedLeafMessage(Timestamp.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsSupportedLeafMessage(StringValue.Descriptor).Should().BeFalse();

        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(StringValue.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(BoolValue.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(Int32Value.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(Int64Value.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(UInt32Value.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(UInt64Value.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(DoubleValue.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(FloatValue.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(BytesValue.Descriptor).Should().BeTrue();
        ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(Timestamp.Descriptor).Should().BeFalse();

        Action nullLeaf = () => ScriptReadModelDescriptorPolicy.IsSupportedLeafMessage(null!);
        Action nullWrapper = () => ScriptReadModelDescriptorPolicy.IsUnsupportedWrapperLeaf(null!);

        nullLeaf.Should().Throw<ArgumentNullException>();
        nullWrapper.Should().Throw<ArgumentNullException>();
    }

    private static ScriptDispatchContext CreateDispatchContext() =>
        new(
            "actor-1",
            "script-1",
            "rev-1",
            "run-1",
            "command",
            "msg-1",
            "cmd-1",
            "corr-1",
            "cause-1",
            "definition-1",
            new SimpleTextState(),
            new NoOpCapabilities());

    private static ScriptFactContext CreateFactContext() =>
        new(
            "actor-1",
            "definition-1",
            "script-1",
            "rev-1",
            "run-1",
            "cmd-1",
            "corr-1",
            1,
            1,
            ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextEvent)),
            1234);

    private static object CreateCommandContext(
        string? actorId,
        string? scriptId,
        string? revision,
        string? runId,
        string? messageType,
        string? messageId,
        string? commandId,
        string? correlationId,
        string? causationId,
        string? definitionActorId,
        SimpleTextState? currentState,
        IScriptBehaviorRuntimeCapabilities runtimeCapabilities)
    {
        var type = typeof(ScriptCommandContext<>).MakeGenericType(typeof(SimpleTextState));
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args:
            [
                actorId,
                scriptId,
                revision,
                runId,
                messageType,
                messageId,
                commandId,
                correlationId,
                causationId,
                definitionActorId,
                currentState,
                runtimeCapabilities,
            ],
            culture: null)!;
    }

    private static IReadOnlyList<IMessage> DrainDomainEvents(object context)
    {
        var method = context.GetType().GetMethod("DrainDomainEvents", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (IReadOnlyList<IMessage>)method!.Invoke(context, null)!;
    }

    private static void InvokeEmit(object context, IMessage domainEvent)
    {
        var method = context.GetType().GetMethod("Emit", [typeof(IMessage)]);
        method.Should().NotBeNull();
        method!.Invoke(context, [domainEvent]);
    }

    private static void InvokeEmitTyped<TEvent>(object context, TEvent domainEvent)
        where TEvent : class, IMessage<TEvent>, new()
    {
        var method = context.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(x => x.Name == "Emit" && x.IsGenericMethodDefinition);
        method.MakeGenericMethod(typeof(TEvent)).Invoke(context, [domainEvent]);
    }

    private static T ReadProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        property.Should().NotBeNull();
        return (T)property!.GetValue(instance)!;
    }

    private sealed class CommandBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>((_, _, _) => Task.CompletedTask)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                    project: static (_, evt, _) => evt.Current);
        }
    }

    private sealed class ReduceOnlyBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder.OnEvent<SimpleTextEvent>(
                project: static (_, evt, _) => evt.Current);
        }
    }

    private sealed class ApplyOnlyBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder.OnEvent<SimpleTextEvent>(
                apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty });
        }
    }

    private sealed class DuplicateInboundBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder.OnCommand<SimpleTextCommand>((_, _, _) => Task.CompletedTask);
            builder.OnSignal<SimpleTextCommand>((_, _, _) => Task.CompletedTask);
        }
    }

    private sealed class MissingEventHandlerBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder.OnEvent<SimpleTextEvent>();
        }
    }

    private sealed class DuplicateEventBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder.OnEvent<SimpleTextEvent>(apply: static (_, _, _) => new SimpleTextState());
            builder.OnEvent<SimpleTextEvent>(project: static (_, evt, _) => evt.Current);
        }
    }

    private sealed class NoOpCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 0, RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? string.Empty);
        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) => Task.FromResult<ScriptReadModelSnapshot?>(null);
        public Task<Any?> ExecuteReadModelQueryAsync(string actorId, Any queryPayload, CancellationToken ct) => Task.FromResult<Any?>(null);
        public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(new ScriptPromotionDecision(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, new ScriptEvolutionValidationReport(false, [])));
        public Task<string> UpsertScriptDefinitionAsync(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, CancellationToken ct) =>
            Task.FromResult(definitionActorId ?? string.Empty);
        public Task<string> SpawnScriptRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, CancellationToken ct) =>
            Task.FromResult(runtimeActorId ?? string.Empty);
        public Task RunScriptInstanceAsync(string runtimeActorId, string runId, Any? inputPayload, string scriptRevision, string definitionActorId, string requestedEventType, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PromoteRevisionAsync(string catalogActorId, string scriptId, string revision, string definitionActorId, string sourceHash, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task RollbackRevisionAsync(string catalogActorId, string scriptId, string targetRevision, string reason, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
