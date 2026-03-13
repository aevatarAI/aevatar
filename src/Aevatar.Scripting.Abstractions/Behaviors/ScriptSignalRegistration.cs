namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptSignalRegistration(
    string TypeUrl,
    Type MessageClrType,
    Func<IMessage, ScriptDispatchContext, CancellationToken, Task<IReadOnlyList<IMessage>>> HandleAsync);
