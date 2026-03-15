using Aevatar.Scripting.Abstractions.Queries;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public interface IScriptBehaviorBridge
{
    ScriptBehaviorDescriptor Descriptor { get; }

    Task<IReadOnlyList<IMessage>> DispatchAsync(
        IMessage inbound,
        ScriptDispatchContext context,
        CancellationToken ct);

    IMessage? ApplyDomainEvent(
        IMessage? currentState,
        IMessage domainEvent,
        ScriptFactContext context);

    IMessage? ProjectReadModel(
        IMessage? currentState,
        IMessage domainEvent,
        ScriptFactContext context);

    Task<IMessage?> ExecuteQueryAsync(
        IMessage query,
        ScriptTypedReadModelSnapshot snapshot,
        CancellationToken ct);
}
