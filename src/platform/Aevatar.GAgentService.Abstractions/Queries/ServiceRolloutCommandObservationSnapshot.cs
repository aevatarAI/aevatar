using System.Text.Json.Serialization;

namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceRolloutCommandObservationSnapshot(
    string CommandId,
    string CorrelationId,
    string ServiceKey,
    string RolloutId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ServiceRolloutStatus Status,
    bool WasNoOp,
    long StateVersion,
    DateTimeOffset ObservedAt);
