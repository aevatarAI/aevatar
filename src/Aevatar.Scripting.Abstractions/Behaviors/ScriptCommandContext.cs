namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed class ScriptCommandContext<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly List<IMessage> _domainEvents = [];

    internal ScriptCommandContext(
        string actorId,
        string scriptId,
        string revision,
        string runId,
        string messageType,
        string messageId,
        string commandId,
        string correlationId,
        string causationId,
        string definitionActorId,
        TState? currentState,
        IScriptBehaviorRuntimeCapabilities runtimeCapabilities)
    {
        ActorId = actorId ?? string.Empty;
        ScriptId = scriptId ?? string.Empty;
        Revision = revision ?? string.Empty;
        RunId = runId ?? string.Empty;
        MessageType = messageType ?? string.Empty;
        MessageId = messageId ?? string.Empty;
        CommandId = commandId ?? string.Empty;
        CorrelationId = correlationId ?? string.Empty;
        CausationId = causationId ?? string.Empty;
        DefinitionActorId = definitionActorId ?? string.Empty;
        CurrentState = currentState;
        RuntimeCapabilities = runtimeCapabilities ?? throw new ArgumentNullException(nameof(runtimeCapabilities));
    }

    public string ActorId { get; }

    public string ScriptId { get; }

    public string Revision { get; }

    public string RunId { get; }

    public string MessageType { get; }

    public string MessageId { get; }

    public string CommandId { get; }

    public string CorrelationId { get; }

    public string CausationId { get; }

    public string DefinitionActorId { get; }

    public TState? CurrentState { get; }

    public IScriptBehaviorRuntimeCapabilities RuntimeCapabilities { get; }

    public void Emit(IMessage domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public void Emit<TEvent>(TEvent domainEvent)
        where TEvent : class, IMessage<TEvent>, new()
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    internal IReadOnlyList<IMessage> DrainDomainEvents() => _domainEvents.ToArray();
}
