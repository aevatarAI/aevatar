namespace Aevatar.Scripting.Abstractions.Behaviors;

public abstract class ScriptBehavior<TState, TReadModel> : IScriptBehaviorBridge
    where TState : class, IMessage<TState>, new()
    where TReadModel : class, IMessage<TReadModel>, new()
{
    private readonly Lazy<ScriptBehaviorDescriptor> _descriptor;

    protected ScriptBehavior()
    {
        _descriptor = new Lazy<ScriptBehaviorDescriptor>(CreateDescriptor, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ScriptBehaviorDescriptor Descriptor => _descriptor.Value;

    protected abstract void Configure(IScriptBehaviorBuilder<TState, TReadModel> builder);

    public async Task<IReadOnlyList<IMessage>> DispatchAsync(
        IMessage inbound,
        ScriptDispatchContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(context);

        var typeUrl = ScriptMessageTypes.GetTypeUrl(inbound);
        if (Descriptor.Commands.TryGetValue(typeUrl, out var command))
            return await command.HandleAsync(inbound, context, ct);
        if (Descriptor.Signals.TryGetValue(typeUrl, out var signal))
            return await signal.HandleAsync(inbound, context, ct);

        throw new InvalidOperationException(
            $"Script behavior `{GetType().FullName}` does not declare inbound type `{typeUrl}`.");
    }

    public IMessage? ApplyDomainEvent(
        IMessage? currentState,
        IMessage domainEvent,
        ScriptFactContext context)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(context);

        var registration = ResolveDomainEventRegistration(domainEvent);
        return registration.Apply == null
            ? currentState
            : registration.Apply(currentState, domainEvent, context);
    }

    public IMessage? BuildReadModel(
        IMessage? currentState,
        ScriptFactContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Descriptor.ReadModelProjector == null
            ? null
            : Descriptor.ReadModelProjector(currentState, context);
    }

    private ScriptBehaviorDescriptor CreateDescriptor()
    {
        var builder = new ScriptBehaviorBuilder<TState, TReadModel>();
        Configure(builder);
        return ScriptBehaviorRuntimeSemanticsCompiler.Attach(builder.Build());
    }

    private ScriptDomainEventRegistration ResolveDomainEventRegistration(IMessage domainEvent)
    {
        var typeUrl = ScriptMessageTypes.GetTypeUrl(domainEvent);
        if (Descriptor.DomainEvents.TryGetValue(typeUrl, out var registration))
            return registration;

        throw new InvalidOperationException(
            $"Script behavior `{GetType().FullName}` does not declare domain event type `{typeUrl}`.");
    }

    private sealed class ScriptBehaviorBuilder<TBuilderState, TBuilderReadModel>
        : IScriptBehaviorBuilder<TBuilderState, TBuilderReadModel>
        where TBuilderState : class, IMessage<TBuilderState>, new()
        where TBuilderReadModel : class, IMessage<TBuilderReadModel>, new()
    {
        private readonly Dictionary<string, ScriptCommandRegistration> _commands = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptSignalRegistration> _signals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptDomainEventRegistration> _domainEvents = new(StringComparer.Ordinal);
        private Func<TBuilderState?, ScriptFactContext, TBuilderReadModel?>? _readModelProjector;

        public IScriptBehaviorBuilder<TBuilderState, TBuilderReadModel> OnCommand<TCommand>(
            Func<TCommand, ScriptCommandContext<TBuilderState>, CancellationToken, Task> handler)
            where TCommand : class, IMessage<TCommand>, new()
        {
            ArgumentNullException.ThrowIfNull(handler);
            var typeUrl = ScriptMessageTypes.GetTypeUrl<TCommand>();
            EnsureNoInboundConflict(typeUrl, "command");
            _commands[typeUrl] = new ScriptCommandRegistration(
                typeUrl,
                typeof(TCommand),
                async (message, context, ct) =>
                {
                    var command = CastRequired<TCommand>(message);
                    var commandContext = new ScriptCommandContext<TBuilderState>(
                        context.ActorId,
                        context.ScriptId,
                        context.Revision,
                        context.RunId,
                        context.MessageType,
                        context.MessageId,
                        context.CommandId,
                        context.CorrelationId,
                        context.CausationId,
                        context.DefinitionActorId,
                        CastOptional<TBuilderState>(context.CurrentState),
                        context.RuntimeCapabilities);
                    await handler(command, commandContext, ct);
                    return commandContext.DrainDomainEvents();
                });
            return this;
        }

        public IScriptBehaviorBuilder<TBuilderState, TBuilderReadModel> OnSignal<TSignal>(
            Func<TSignal, ScriptCommandContext<TBuilderState>, CancellationToken, Task> handler)
            where TSignal : class, IMessage<TSignal>, new()
        {
            ArgumentNullException.ThrowIfNull(handler);
            var typeUrl = ScriptMessageTypes.GetTypeUrl<TSignal>();
            EnsureNoInboundConflict(typeUrl, "signal");
            _signals[typeUrl] = new ScriptSignalRegistration(
                typeUrl,
                typeof(TSignal),
                async (message, context, ct) =>
                {
                    var signal = CastRequired<TSignal>(message);
                    var signalContext = new ScriptCommandContext<TBuilderState>(
                        context.ActorId,
                        context.ScriptId,
                        context.Revision,
                        context.RunId,
                        context.MessageType,
                        context.MessageId,
                        context.CommandId,
                        context.CorrelationId,
                        context.CausationId,
                        context.DefinitionActorId,
                        CastOptional<TBuilderState>(context.CurrentState),
                        context.RuntimeCapabilities);
                    await handler(signal, signalContext, ct);
                    return signalContext.DrainDomainEvents();
                });
            return this;
        }

        public IScriptBehaviorBuilder<TBuilderState, TBuilderReadModel> OnEvent<TEvent>(
            Func<TBuilderState?, TEvent, ScriptFactContext, TBuilderState?>? apply = null)
            where TEvent : class, IMessage<TEvent>, new()
        {
            var typeUrl = ScriptMessageTypes.GetTypeUrl<TEvent>();
            if (_domainEvents.ContainsKey(typeUrl))
                throw new InvalidOperationException($"Domain event type `{typeUrl}` is already registered.");

            _domainEvents[typeUrl] = new ScriptDomainEventRegistration(
                typeUrl,
                typeof(TEvent),
                apply == null
                    ? null
                    : (currentState, domainEvent, factContext) => apply(
                        CastOptional<TBuilderState>(currentState),
                        CastRequired<TEvent>(domainEvent),
                        factContext));
            return this;
        }

        public IScriptBehaviorBuilder<TBuilderState, TBuilderReadModel> ProjectState(
            Func<TBuilderState?, ScriptFactContext, TBuilderReadModel?> project)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (_readModelProjector != null)
                throw new InvalidOperationException("Read model projector is already registered.");

            _readModelProjector = project;
            return this;
        }

        public ScriptBehaviorDescriptor Build()
        {
            var stateDescriptor = ScriptMessageTypes.GetDescriptor(typeof(TBuilderState));
            var readModelDescriptor = ScriptMessageTypes.GetDescriptor(typeof(TBuilderReadModel));
            return new ScriptBehaviorDescriptor(
                typeof(TBuilderState),
                typeof(TBuilderReadModel),
                stateDescriptor,
                readModelDescriptor,
                ScriptMessageTypes.GetTypeUrl<TBuilderState>(),
                ScriptMessageTypes.GetTypeUrl<TBuilderReadModel>(),
                new Dictionary<string, ScriptCommandRegistration>(_commands, StringComparer.Ordinal),
                new Dictionary<string, ScriptSignalRegistration>(_signals, StringComparer.Ordinal),
                new Dictionary<string, ScriptDomainEventRegistration>(_domainEvents, StringComparer.Ordinal),
                _readModelProjector == null
                    ? null
                    : (currentState, factContext) => _readModelProjector(
                        CastOptional<TBuilderState>(currentState),
                        factContext),
                ByteString.Empty,
                new ScriptRuntimeSemanticsSpec());
        }

        private void EnsureNoInboundConflict(string typeUrl, string category)
        {
            if (_commands.ContainsKey(typeUrl) || _signals.ContainsKey(typeUrl))
                throw new InvalidOperationException($"Inbound {category} type `{typeUrl}` is already registered.");
        }

        private static TMessage CastRequired<TMessage>(IMessage message)
            where TMessage : class, IMessage<TMessage>, new()
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message is TMessage typed)
                return typed;

            throw new InvalidOperationException(
                $"Expected protobuf message `{typeof(TMessage).FullName}`, but got `{message.GetType().FullName}`.");
        }

        private static TMessage? CastOptional<TMessage>(IMessage? message)
            where TMessage : class, IMessage<TMessage>, new()
        {
            if (message == null)
                return null;

            if (message is TMessage typed)
                return typed;

            throw new InvalidOperationException(
                $"Expected protobuf message `{typeof(TMessage).FullName}`, but got `{message.GetType().FullName}`.");
        }
    }
}
