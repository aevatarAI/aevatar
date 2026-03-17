namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptCommandRegistration(
    string TypeUrl,
    Type MessageClrType,
    Func<IMessage, ScriptDispatchContext, CancellationToken, Task<IReadOnlyList<IMessage>>> HandleAsync);
