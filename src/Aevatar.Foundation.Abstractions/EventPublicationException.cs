namespace Aevatar.Foundation.Abstractions;

/// <summary>Describes the strongest fact known when an event publication fails.</summary>
public enum EventPublicationFailureOutcome
{
    Unspecified = 0,
    NotAdmitted = 1,
    OutcomeUncertain = 2,
}

/// <summary>
/// Typed publication failure used when a transport can distinguish rejection before admission
/// from a failure that may have happened after the message escaped to the target inbox.
/// </summary>
public sealed class EventPublicationException : Exception
{
    public EventPublicationException(
        EventPublicationFailureOutcome outcome,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (outcome == EventPublicationFailureOutcome.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(outcome));
        Outcome = outcome;
    }

    public EventPublicationFailureOutcome Outcome { get; }
}
