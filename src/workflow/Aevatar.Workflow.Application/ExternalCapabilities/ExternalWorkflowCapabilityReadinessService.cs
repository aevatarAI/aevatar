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
            .OrderBy(static descriptor => descriptor.Selector.SelectorCase)
            .ThenBy(IdentityKey, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ExternalCapabilityReadiness> InspectAsync(
        InspectExternalWorkflowCapabilityReadinessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Selector);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.None)
            return SelectionRequired(request.ExecutionMode, request.Selector, "CAPABILITY_SELECTION_REQUIRED");

        var source = _sources.SingleOrDefault(candidate =>
            candidate.SelectorKind == request.Selector.SelectorCase);
        if (source is null)
            return SelectionRequired(request.ExecutionMode, request.Selector, "CAPABILITY_SOURCE_UNAVAILABLE");

        return await source.InspectAsync(
            request.Access,
            request.Selector,
            request.ExecutionMode,
            cancellationToken);
    }

    private static ExternalCapabilityReadiness SelectionRequired(
        ExternalCapabilityExecutionMode executionMode,
        ExternalWorkflowCapabilitySelector selector,
        string code)
    {
        var result = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.SelectionRequired,
            SelectedSelector = selector.Clone(),
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
        descriptor.Selector.SelectorCase switch
        {
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector =>
                $"{descriptor.Selector.HostConnector.ConnectorCapabilityRef}\n{descriptor.Selector.HostConnector.OperationId}",
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation =>
                $"{descriptor.Selector.NyxIdOperation.UserServiceId}\n{descriptor.Selector.NyxIdOperation.OperationId}",
            _ => string.Empty,
        };
}
