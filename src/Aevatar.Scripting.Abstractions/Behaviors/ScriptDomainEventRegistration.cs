namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptDomainEventRegistration(
    string TypeUrl,
    Type MessageClrType,
    Func<IMessage?, IMessage, ScriptFactContext, IMessage?>? Apply);
