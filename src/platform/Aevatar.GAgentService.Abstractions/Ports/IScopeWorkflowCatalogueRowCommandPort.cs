using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IScopeWorkflowCatalogueRowCommandPort
{
    Task ObserveSourcesAsync(
        string scopeId,
        string workflowId,
        ScopeWorkflowCatalogueSourceSnapshot? draftSource,
        ScopeWorkflowCatalogueSourceSnapshot? serviceSource,
        DateTimeOffset draftWatermarkUtc,
        DateTimeOffset serviceWatermarkUtc,
        string observationEventId,
        DateTimeOffset observedAt,
        CancellationToken ct = default);
}
