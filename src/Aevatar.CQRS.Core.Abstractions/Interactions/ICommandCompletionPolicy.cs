namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandCompletionPolicy<in TEvent, TCompletion>
{
    TCompletion IncompleteCompletion { get; }

    bool TryResolve(
        TEvent evt,
        out TCompletion completion);
}
