namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandExecutionResult<TStarted, TFinalize, TError>(
    TError Error,
    TStarted? Started,
    TFinalize? FinalizeResult);
