namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptQueryRegistration(
    string TypeUrl,
    Type QueryClrType,
    Type ResultClrType,
    Func<IMessage, ScriptTypedReadModelSnapshot, CancellationToken, Task<IMessage?>> ExecuteAsync);
