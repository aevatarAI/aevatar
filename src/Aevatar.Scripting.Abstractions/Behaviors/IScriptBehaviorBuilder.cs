using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public interface IScriptBehaviorBuilder<TState, TReadModel>
    where TState : class, IMessage<TState>, new()
    where TReadModel : class, IMessage<TReadModel>, new()
{
    IScriptBehaviorBuilder<TState, TReadModel> OnCommand<TCommand>(
        Func<TCommand, ScriptCommandContext<TState>, CancellationToken, Task> handler)
        where TCommand : class, IMessage<TCommand>, new();

    IScriptBehaviorBuilder<TState, TReadModel> OnSignal<TSignal>(
        Func<TSignal, ScriptCommandContext<TState>, CancellationToken, Task> handler)
        where TSignal : class, IMessage<TSignal>, new();

    IScriptBehaviorBuilder<TState, TReadModel> OnEvent<TEvent>(
        Func<TState?, TEvent, ScriptFactContext, TState?>? apply = null,
        Func<TReadModel?, TEvent, ScriptFactContext, TReadModel?>? reduce = null)
        where TEvent : class, IMessage<TEvent>, new();

    IScriptBehaviorBuilder<TState, TReadModel> OnQuery<TQuery, TResult>(
        Func<TQuery, ScriptQueryContext<TReadModel>, CancellationToken, Task<TResult?>> handler)
        where TQuery : class, IMessage<TQuery>, new()
        where TResult : class, IMessage<TResult>, new();

    IScriptBehaviorBuilder<TState, TReadModel> DescribeReadModel(
        ScriptReadModelDefinition definition,
        IReadOnlyList<string> storeKinds);
}
