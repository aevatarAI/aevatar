using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Application.Studio.Services;

// Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
//   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
//   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.
public sealed class ExecutionService
{
    private const int ExecutionListTake = 50;
    private const int ExecutionLookupTake = 100;

    private readonly IServiceInvocationPort _serviceInvocationPort;
    private readonly IServiceRunQueryPort _serviceRunQueryPort;
    private readonly ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> _resumeDispatchService;
    private readonly ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> _stopDispatchService;
    private readonly IUserConfigQueryPort? _userConfigStore;
    private readonly IStudioWorkspaceQueryPort? _workspaceQueryPort;
    private readonly IAppScopeResolver? _scopeResolver;

    public ExecutionService(
        IServiceInvocationPort serviceInvocationPort,
        IServiceRunQueryPort serviceRunQueryPort,
        ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeDispatchService,
        ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopDispatchService,
        IUserConfigQueryPort? userConfigStore = null,
        IStudioWorkspaceQueryPort? workspaceQueryPort = null,
        IAppScopeResolver? scopeResolver = null)
    {
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
        _serviceRunQueryPort = serviceRunQueryPort ?? throw new ArgumentNullException(nameof(serviceRunQueryPort));
        _resumeDispatchService = resumeDispatchService ?? throw new ArgumentNullException(nameof(resumeDispatchService));
        _stopDispatchService = stopDispatchService ?? throw new ArgumentNullException(nameof(stopDispatchService));
        _userConfigStore = userConfigStore;
        _workspaceQueryPort = workspaceQueryPort;
        _scopeResolver = scopeResolver;
    }

    public async Task<IReadOnlyList<ExecutionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var scope = GetScopeFilter();
        if (scope.FailClosed || string.IsNullOrWhiteSpace(scope.ScopeId))
            return [];

        var runs = await _serviceRunQueryPort.ListAsync(
            new ServiceRunQuery(scope.ScopeId, ServiceId: string.Empty, Take: ExecutionListTake),
            cancellationToken);
        return runs
            .OrderByDescending(run => run.CreatedAt)
            .Take(ExecutionListTake)
            .Select(ToSummary)
            .ToList();
    }

    public async Task<ExecutionDetail?> GetAsync(string executionId, CancellationToken cancellationToken = default)
    {
        var scope = GetScopeFilter();
        if (scope.FailClosed || string.IsNullOrWhiteSpace(scope.ScopeId))
            return null;

        var normalizedExecutionId = NormalizeRequired(executionId, nameof(executionId));
        var byCommand = await _serviceRunQueryPort.GetByCommandIdAsync(
            scope.ScopeId,
            serviceId: string.Empty,
            normalizedExecutionId,
            cancellationToken);
        if (byCommand != null)
            return await ToDetailAsync(byCommand, cancellationToken);

        // Bounded fallback for callers that pass runId instead of commandId.
        var runs = await _serviceRunQueryPort.ListAsync(
            new ServiceRunQuery(scope.ScopeId, ServiceId: string.Empty, Take: ExecutionLookupTake),
            cancellationToken);
        var run = runs.FirstOrDefault(item =>
            string.Equals(item.RunId, normalizedExecutionId, StringComparison.Ordinal) ||
            string.Equals(item.CommandId, normalizedExecutionId, StringComparison.Ordinal));
        return run is null ? null : await ToDetailAsync(run, cancellationToken);
    }

    public async Task<ExecutionDetail> StartAsync(
        StartExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ScopeId) || string.IsNullOrWhiteSpace(request.WorkflowId))
            throw new InvalidOperationException("scopeId and workflowId are required. Executions must target a registered scope service.");

        var requestedScopeId = request.ScopeId.Trim();
        var scope = GetScopeFilter();
        if (scope.FailClosed)
            throw new InvalidOperationException("Authenticated caller has no resolvable scope; refuse to start a scoped execution.");
        if (scope.ScopeId is not null && !string.Equals(scope.ScopeId, requestedScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Requested scope does not match the authenticated Studio scope.");

        var executionId = Guid.NewGuid().ToString("N");
        var runtimeBaseUrl = string.IsNullOrWhiteSpace(request.RuntimeBaseUrl)
            ? await ResolveRuntimeBaseUrlAsync(cancellationToken)
            : request.RuntimeBaseUrl.Trim().TrimEnd('/');
        var prompt = request.Prompt ?? string.Empty;
        var receipt = await _serviceInvocationPort.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = new ServiceIdentity
            {
                TenantId = requestedScopeId,
                ServiceId = request.WorkflowId.Trim(),
            },
            EndpointId = "chat",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = prompt,
                ScopeId = requestedScopeId,
            }),
            CommandId = executionId,
            CorrelationId = executionId,
        }, cancellationToken);

        var startedAtUtc = DateTimeOffset.UtcNow;
        return new ExecutionDetail(
            ExecutionId: string.IsNullOrWhiteSpace(receipt.CommandId) ? executionId : receipt.CommandId,
            WorkflowName: string.IsNullOrWhiteSpace(request.WorkflowName) ? request.WorkflowId.Trim() : request.WorkflowName.Trim(),
            Prompt: string.Empty,
            RuntimeBaseUrl: runtimeBaseUrl,
            Status: "accepted",
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: null,
            ActorId: receipt.TargetActorId,
            Error: null,
            Frames: []);
    }

    public async Task<ExecutionDetail?> ResumeAsync(
        string executionId,
        ResumeExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var detail = await GetAsync(executionId, cancellationToken);
        if (detail is null)
            return null;
        if (IsTerminalExecutionStatus(detail.Status))
            throw new InvalidOperationException(
                $"Execution is already in terminal status '{detail.Status}' and cannot be resumed.");

        var actorId = NormalizeRequired(detail.ActorId ?? string.Empty, nameof(detail.ActorId));
        var runId = NormalizeRequired(request.RunId, nameof(request.RunId));
        var stepId = NormalizeRequired(request.StepId, nameof(request.StepId));
        var result = await _resumeDispatchService.DispatchAsync(
            new WorkflowResumeCommand(
                actorId,
                runId,
                stepId,
                Guid.NewGuid().ToString("N"),
                request.Approved,
                request.UserInput,
                request.Metadata),
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Runtime resume request failed: {result.Error}");

        return detail with { Status = "running", Error = null };
    }

    public async Task<ExecutionDetail?> StopAsync(
        string executionId,
        StopExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = GetScopeFilter();
        if (scope.FailClosed || string.IsNullOrWhiteSpace(scope.ScopeId))
            return null;

        var normalizedExecutionId = NormalizeRequired(executionId, nameof(executionId));
        var run = await _serviceRunQueryPort.GetByCommandIdAsync(
            scope.ScopeId,
            serviceId: string.Empty,
            normalizedExecutionId,
            cancellationToken);
        if (run is null)
        {
            // Bounded fallback for callers that pass runId instead of commandId.
            var runs = await _serviceRunQueryPort.ListAsync(
                new ServiceRunQuery(scope.ScopeId, ServiceId: string.Empty, Take: ExecutionLookupTake),
                cancellationToken);
            run = runs.FirstOrDefault(item =>
                string.Equals(item.RunId, normalizedExecutionId, StringComparison.Ordinal) ||
                string.Equals(item.CommandId, normalizedExecutionId, StringComparison.Ordinal));
        }

        if (run is null)
            return null;

        var detail = await ToDetailAsync(run, cancellationToken);
        if (IsTerminalExecutionStatus(detail.Status))
            return detail;

        var actorId = NormalizeRequired(detail.ActorId ?? string.Empty, nameof(detail.ActorId));
        var runId = NormalizeRequired(run.RunId, nameof(run.RunId));
        var result = await _stopDispatchService.DispatchAsync(
            new WorkflowStopCommand(
                actorId,
                runId,
                Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(request.Reason) ? "user requested stop" : request.Reason.Trim()),
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Runtime stop request failed: {result.Error}");

        return detail with { Status = "stopped" };
    }

    private ScopeFilter GetScopeFilter()
    {
        var resolved = _scopeResolver?.Resolve()?.ScopeId?.Trim();
        if (!string.IsNullOrEmpty(resolved))
            return new ScopeFilter(resolved, FailClosed: false);

        var unscopedAuthed = _scopeResolver?.HasAuthenticatedRequestWithoutScope() ?? false;
        return new ScopeFilter(ScopeId: null, FailClosed: unscopedAuthed);
    }

    private async Task<ExecutionDetail> ToDetailAsync(
        ServiceRunSnapshot run,
        CancellationToken ct)
    {
        return new ExecutionDetail(
            ExecutionId: run.CommandId,
            WorkflowName: string.IsNullOrWhiteSpace(run.ServiceId) ? run.ServiceKey : run.ServiceId,
            Prompt: string.Empty,
            RuntimeBaseUrl: await ResolveRuntimeBaseUrlAsync(ct),
            Status: ToExecutionStatus(run.Status),
            StartedAtUtc: run.CreatedAt,
            CompletedAtUtc: IsTerminalServiceRunStatus(run.Status) ? run.UpdatedAt : null,
            ActorId: run.TargetActorId,
            Error: ResolveServiceRunError(run.Status),
            Frames: []);
    }

    private static ExecutionSummary ToSummary(ServiceRunSnapshot run)
    {
        return new ExecutionSummary(
            ExecutionId: run.CommandId,
            WorkflowName: string.IsNullOrWhiteSpace(run.ServiceId) ? run.ServiceKey : run.ServiceId,
            Status: ToExecutionStatus(run.Status),
            PromptPreview: string.Empty,
            StartedAtUtc: run.CreatedAt,
            CompletedAtUtc: IsTerminalServiceRunStatus(run.Status) ? run.UpdatedAt : null,
            ActorId: run.TargetActorId,
            Error: ResolveServiceRunError(run.Status));
    }

    private async Task<string> ResolveRuntimeBaseUrlAsync(CancellationToken ct)
    {
        if (_userConfigStore != null)
        {
            try
            {
                var userConfig = await _userConfigStore.GetAsync(ct);
                var resolved = UserConfigRuntime.ResolveActiveRuntimeBaseUrl(userConfig);
                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved;
            }
            catch
            {
                // Fall through to workspace settings.
            }
        }

        if (_workspaceQueryPort != null)
        {
            var workspace = await _workspaceQueryPort.GetAsync(ct);
            return workspace.Settings.RuntimeBaseUrl;
        }

        return UserConfigRuntimeDefaults.LocalRuntimeBaseUrl;
    }

    private static string ToExecutionStatus(ServiceRunStatus status) => status switch
    {
        ServiceRunStatus.Accepted => "running",
        ServiceRunStatus.Completed => "completed",
        ServiceRunStatus.Failed => "failed",
        ServiceRunStatus.Stopped => "stopped",
        ServiceRunStatus.OutcomeUncertain => "outcome_uncertain",
        _ => "unknown",
    };

    private static bool IsTerminalServiceRunStatus(ServiceRunStatus status) =>
        status is ServiceRunStatus.Completed or
            ServiceRunStatus.Failed or
            ServiceRunStatus.Stopped or
            ServiceRunStatus.OutcomeUncertain;

    private static string? ResolveServiceRunError(ServiceRunStatus status) => status switch
    {
        ServiceRunStatus.Failed => "service run failed",
        ServiceRunStatus.OutcomeUncertain => "service run outcome is uncertain",
        _ => null,
    };

    private static bool IsTerminalExecutionStatus(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "outcome_uncertain", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }

    private readonly record struct ScopeFilter(string? ScopeId, bool FailClosed);
}
