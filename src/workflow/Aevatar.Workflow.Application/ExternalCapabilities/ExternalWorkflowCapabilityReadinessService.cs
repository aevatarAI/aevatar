using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed class ExternalWorkflowCapabilityReadinessService(
    IEnumerable<IExternalWorkflowCapabilitySource> sources) :
    IExternalWorkflowCapabilityListPort,
    IExternalWorkflowCapabilityReadinessPort
{
    private readonly IReadOnlyList<IExternalWorkflowCapabilitySource> _sources =
        sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));

    public async Task<IReadOnlyList<ExternalWorkflowCapabilityDescriptor>> ListAsync(
        ListExternalWorkflowCapabilitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var batches = await Task.WhenAll(_sources.Select(source =>
            source.ListAsync(request.Access, cancellationToken)));
        return batches
            .SelectMany(static batch => batch)
            .Select(static descriptor => descriptor.Clone())
            .OrderBy(static descriptor => descriptor.Capability.CapabilityCase)
            .ThenBy(IdentityKey, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ExternalCapabilityReadiness> InspectAsync(
        InspectExternalWorkflowCapabilityReadinessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Capability);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Capability.CapabilityCase == ExternalWorkflowCapabilityRef.CapabilityOneofCase.None)
            return SelectionRequired(request.ExecutionMode, "CAPABILITY_SELECTION_REQUIRED");

        var source = _sources.SingleOrDefault(candidate =>
            candidate.CapabilityKind == request.Capability.CapabilityCase);
        if (source is null)
            return SelectionRequired(request.ExecutionMode, "CAPABILITY_SOURCE_UNAVAILABLE");

        return await source.InspectAsync(
            request.Access,
            request.Capability,
            request.ExecutionMode,
            cancellationToken);
    }

    private static ExternalCapabilityReadiness SelectionRequired(
        ExternalCapabilityExecutionMode executionMode,
        string code)
    {
        var result = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.SelectionRequired,
        };
        result.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = result.Status,
            Code = code,
            SafeMessage = "Select one exact external workflow capability.",
        });
        result.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.SelectCapability,
            Label = "Select capability",
        });
        return result;
    }

    private static string IdentityKey(ExternalWorkflowCapabilityDescriptor descriptor) =>
        descriptor.Capability.CapabilityCase switch
        {
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector =>
                $"{descriptor.Capability.HostConnector.ConnectorCapabilityRef}\n{descriptor.Capability.HostConnector.OperationId}",
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService =>
                $"{descriptor.Capability.NyxIdUserService.UserServiceId}\n{descriptor.Capability.NyxIdUserService.OperationId}",
            _ => string.Empty,
        };
}
