namespace Aevatar.CQRS.Runtime.Abstractions.Commands;

public sealed record QueuedCommandMessage(
    CommandEnvelope Envelope,
    string CommandType,
    string PayloadJson,
    int Attempt = 0);
