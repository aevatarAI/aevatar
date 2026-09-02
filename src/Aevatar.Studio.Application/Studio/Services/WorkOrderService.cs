using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkOrderService : IWorkOrderService
{
    private readonly WorkOrderAssignmentValidator _assignmentValidator;
    private readonly IWorkOrderCommandPort _commandPort;
    private readonly IWorkOrderQueryPort _queryPort;
    private readonly TimeProvider _timeProvider;

    public WorkOrderService(
        WorkOrderAssignmentValidator assignmentValidator,
        IWorkOrderCommandPort commandPort,
        IWorkOrderQueryPort queryPort,
        TimeProvider? timeProvider = null)
    {
        _assignmentValidator = assignmentValidator ?? throw new ArgumentNullException(nameof(assignmentValidator));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkOrderAcceptedReceipt> CreateAsync(
        string scopeId,
        CreateWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedRequester = NormalizePrincipal(requester);
        var normalizedRequest = NormalizeCreateRequest(request);
        if (normalizedRequest.TimeoutAtUtc <= _timeProvider.GetUtcNow())
            throw new InvalidOperationException("timeoutAtUtc must be in the future when provided.");

        var assignment = await _assignmentValidator.ValidateAsync(
            normalizedScopeId,
            normalizedRequest.TeamId,
            normalizedRequest.MemberId,
            normalizedRequest.PublishedServiceId,
            normalizedRequest.EndpointId,
            ct);
        return await _commandPort.CreateAsync(
            normalizedScopeId,
            normalizedRequest,
            normalizedRequester,
            assignment,
            ct);
    }

    public Task<WorkOrderListResponse> ListAsync(
        string scopeId,
        WorkOrderQueryRequest query,
        CancellationToken ct = default) =>
        _queryPort.ListAsync(
            NormalizeRequired(scopeId, nameof(scopeId)),
            query ?? new WorkOrderQueryRequest(),
            ct);

    public async Task<WorkOrderCurrentStateResponse> GetAsync(
        string scopeId,
        string workOrderId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkOrderId = NormalizeRequired(workOrderId, nameof(workOrderId));
        return await _queryPort.GetAsync(normalizedScopeId, normalizedWorkOrderId, ct)
            ?? throw new WorkOrderNotFoundException(normalizedScopeId, normalizedWorkOrderId);
    }

    public async Task<WorkOrderAcceptedReceipt> ReassignAsync(
        string scopeId,
        string workOrderId,
        ReassignWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await GetAuthorizedCurrentAsync(
            scopeId,
            workOrderId,
            request.ExpectedLifecycleVersion,
            requester,
            ct);
        var normalizedRequest = request with
        {
            MemberId = NormalizeRequired(request.MemberId, nameof(request.MemberId)),
            PublishedServiceId = NormalizeRequired(request.PublishedServiceId, nameof(request.PublishedServiceId)),
        };
        var assignment = await _assignmentValidator.ValidateAsync(
            current.ScopeId,
            current.TeamId,
            normalizedRequest.MemberId,
            normalizedRequest.PublishedServiceId,
            current.EndpointId,
            ct);
        return await _commandPort.ReassignAsync(
            current.ScopeId,
            current.WorkOrderId,
            normalizedRequest,
            NormalizePrincipal(requester),
            assignment,
            ct);
    }

    public async Task<WorkOrderAcceptedReceipt> DispatchAsync(
        string scopeId,
        string workOrderId,
        DispatchWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await GetAuthorizedCurrentAsync(
            scopeId,
            workOrderId,
            request.ExpectedLifecycleVersion,
            requester,
            ct);
        await _assignmentValidator.ValidateAsync(
            current.ScopeId,
            current.TeamId,
            current.MemberId,
            current.PublishedServiceId,
            current.EndpointId,
            ct);
        return await _commandPort.DispatchAsync(
            current.ScopeId,
            current.WorkOrderId,
            request,
            NormalizePrincipal(requester),
            ct);
    }

    public async Task<WorkOrderAcceptedReceipt> CancelAsync(
        string scopeId,
        string workOrderId,
        CancelWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await GetAuthorizedCurrentAsync(
            scopeId,
            workOrderId,
            request.ExpectedLifecycleVersion,
            requester,
            ct);
        return await _commandPort.CancelAsync(
            current.ScopeId,
            current.WorkOrderId,
            request with { Reason = NormalizeOptional(request.Reason) },
            NormalizePrincipal(requester),
            ct);
    }

    private async Task<WorkOrderCurrentStateResponse> GetAuthorizedCurrentAsync(
        string scopeId,
        string workOrderId,
        long expectedLifecycleVersion,
        WorkOrderPrincipalContract requester,
        CancellationToken ct)
    {
        var current = await GetCurrentAtVersionAsync(scopeId, workOrderId, expectedLifecycleVersion, ct);
        var normalizedRequester = NormalizePrincipal(requester);
        if (!string.Equals(current.Requester.PrincipalId, normalizedRequester.PrincipalId, StringComparison.Ordinal) ||
            !string.Equals(current.Requester.PrincipalKind, normalizedRequester.PrincipalKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("WorkOrder command principal is not the requester.");
        }
        return current;
    }

    private async Task<WorkOrderCurrentStateResponse> GetCurrentAtVersionAsync(
        string scopeId,
        string workOrderId,
        long expectedLifecycleVersion,
        CancellationToken ct)
    {
        var current = await GetAsync(scopeId, workOrderId, ct);
        if (current.LifecycleVersion != expectedLifecycleVersion)
        {
            throw new InvalidOperationException(
                $"WorkOrder lifecycle version is {current.LifecycleVersion}, not {expectedLifecycleVersion}.");
        }
        return current;
    }

    private static CreateWorkOrderRequest NormalizeCreateRequest(CreateWorkOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Input?.Chat == null)
            throw new InvalidOperationException("input.chat is required.");

        return request with
        {
            TeamId = NormalizeRequired(request.TeamId, nameof(request.TeamId)),
            MemberId = NormalizeRequired(request.MemberId, nameof(request.MemberId)),
            PublishedServiceId = NormalizeRequired(request.PublishedServiceId, nameof(request.PublishedServiceId)),
            EndpointId = NormalizeRequired(request.EndpointId, nameof(request.EndpointId)),
            Intent = NormalizeRequired(request.Intent, nameof(request.Intent)),
            DedupKey = NormalizeRequired(request.DedupKey, nameof(request.DedupKey)),
            Input = new WorkOrderServiceInputContract(
                new WorkOrderChatInputContract(NormalizeRequired(request.Input.Chat.Prompt, "input.chat.prompt")),
                NormalizeArtifacts(request.Input.InputArtifacts),
                NormalizeArtifacts(request.Input.DeclaredResultArtifacts)),
        };
    }

    private static IReadOnlyList<WorkOrderArtifactReferenceContract> NormalizeArtifacts(
        IReadOnlyList<WorkOrderArtifactReferenceContract>? artifacts)
    {
        var normalized = (artifacts ?? [])
            .Select(artifact => new WorkOrderArtifactReferenceContract(
                NormalizeRequired(artifact.ArtifactId, "artifactId"),
                NormalizeRequired(artifact.ArtifactKind, "artifactKind"),
                NormalizeOptional(artifact.Uri),
                NormalizeOptional(artifact.RevisionId)))
            .OrderBy(static artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(normalized.Select(static artifact => artifact.ArtifactId), "artifact id");
        return normalized;
    }

    private static WorkOrderPrincipalContract NormalizePrincipal(WorkOrderPrincipalContract principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new WorkOrderPrincipalContract(
            NormalizeRequired(principal.PrincipalId, nameof(principal.PrincipalId)),
            NormalizeRequired(principal.PrincipalKind, nameof(principal.PrincipalKind)));
    }

    private static void EnsureUnique(IEnumerable<string> values, string fieldName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (values.Any(value => !seen.Add(value)))
            throw new InvalidOperationException($"Duplicate {fieldName} is not allowed.");
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new InvalidOperationException($"{fieldName} is required.")
            : normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
