namespace Aevatar.CQRS.Core.Abstractions.Interactions;

/// <summary>
/// Raised when an accepted command does not produce an observable frame before
/// its interaction deadline.
/// </summary>
public sealed class CommandObservationTimeoutException : TimeoutException
{
    public CommandObservationTimeoutException(string commandType, TimeSpan timeout)
        : base($"Accepted command '{commandType}' produced no observable frame within {timeout}.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        CommandType = commandType;
        Timeout = timeout;
    }

    public string CommandType { get; }

    public TimeSpan Timeout { get; }
}
