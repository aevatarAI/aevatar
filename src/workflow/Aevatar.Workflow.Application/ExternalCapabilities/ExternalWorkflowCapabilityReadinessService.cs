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

    public async Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
        ListExternalWorkflowCapabilitiesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var batches = await Task.WhenAll(_sources.Select(source =>
            source.ListAsync(request.Access, cancellationToken)));
        var capabilities = batches
            .SelectMany(static batch => batch.Capabilities)
            .Select(static descriptor => new
            {
                Descriptor = descriptor.Clone(),
                Identity = IdentityKey(descriptor),
            })
            .OrderBy(static item => item.Descriptor.Selector.SelectorCase)
            .ThenBy(static item => item.Identity, StringComparer.Ordinal)
            .Select(static item => item.Descriptor)
            .ToArray();
        var result = new ExternalWorkflowCapabilityDiscoveryResult
        {
            CandidateCount = batches.Sum(static batch => batch.CandidateCount),
            RejectedCount = batches.Sum(static batch => batch.RejectedCount),
        };
        result.Capabilities.Add(capabilities);
        result.Diagnostics.Add(batches.SelectMany(static batch => batch.Diagnostics));
        return result;
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
                $"{descriptor.Selector.NyxIdOperation.UserServiceId}\n{descriptor.Selector.NyxIdOperation.EndpointId}",
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest =>
                $"{descriptor.Selector.NyxIdRequest.UserServiceId}\n" +
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(
                    descriptor.Selector.NyxIdRequest),
            _ => throw new InvalidOperationException(
                "External workflow capability selector identity is unavailable."),
        };
}
