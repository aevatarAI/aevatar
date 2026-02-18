namespace Aevatar.CQRS.Runtime.Abstractions.Commands;

public enum CommandExecutionStatus
{
    Accepted = 0,
    Queued = 1,
    Running = 2,
    Retrying = 3,
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
    TimedOut = 7,
    DeadLettered = 8,
    DuplicateIgnored = 9,
}
