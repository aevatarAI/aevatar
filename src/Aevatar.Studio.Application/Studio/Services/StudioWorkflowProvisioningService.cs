using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// One-call workflow provisioning facade (C1). Composes the existing
/// member-first services — it reinvents nothing: create a member via
/// <see cref="IStudioMemberService.CreateAsync"/>, bind the inline workflow YAML
/// via <see cref="IStudioMemberService.BindAsync"/>, poll the binding run via
/// <see cref="IStudioMemberService.GetBindingRunAsync"/> until it succeeds, then
/// (optionally) invoke the bound member once via
/// <see cref="IServiceInvocationPort"/> to produce a run stamped with the caller
/// scope.
///
/// The bind is asynchronous, so the single real design point here is the bounded
/// poll: the service never blocks indefinitely (capped by
/// <see cref="ProvisionWorkflowRequest.BindTimeoutSeconds"/>) and never invokes
/// before the member is bound. On timeout it returns
/// <see cref="ProvisionWorkflowBindingStatusNames.Pending"/> with no run; on a
/// terminal bind failure it throws so the endpoint can surface the error.
///
/// <paramref name="scopeId"/> is always an input parameter — the service holds
/// no HttpContext and no infrastructure dependency, only ports.
/// </summary>
public sealed class StudioWorkflowProvisioningService : IStudioWorkflowProvisioningService
{
    // Workflow members publish a single "chat" endpoint; invoking it produces a
    // workflow run (mirrors ExecutionService.StartAsync, the canonical invoke).
    private const string WorkflowInvokeEndpointId = "chat";

    private const string ObservatoryPath = "/workflow/observatory";

    private static readonly TimeSpan BindPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IStudioMemberService _memberService;
    private readonly IServiceInvocationPort _serviceInvocationPort;
    private readonly TimeProvider _timeProvider;

    public StudioWorkflowProvisioningService(
        IStudioMemberService memberService,
        IServiceInvocationPort serviceInvocationPort,
        TimeProvider? timeProvider = null)
    {
        _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
        _serviceInvocationPort = serviceInvocationPort
            ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProvisionWorkflowResponse> ProvisionAsync(
        string scopeId,
        ProvisionWorkflowRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var displayName = NormalizeRequired(request.DisplayName, nameof(request.DisplayName));
        var workflowYaml = NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));

        // 1. Create the member (kind = workflow). The published service surfaces
        //    only once the member is bound, so we do not read it here.
        var member = await _memberService.CreateAsync(
            normalizedScopeId,
            new CreateStudioMemberRequest(
                DisplayName: displayName,
                ImplementationKind: MemberImplementationKindNames.Workflow),
            ct);

        var memberId = member.MemberId;

        // 2. Bind the inline workflow YAML. WorkflowId is a stable identifier the
        //    bind contract requires; we mint one so the caller only supplies YAML.
        var bindReceipt = await _memberService.BindAsync(
            normalizedScopeId,
            memberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    WorkflowId: GenerateWorkflowId(),
                    WorkflowYamls: [workflowYaml])),
            ct);

        // 3. Poll the binding run until a terminal state, bounded by the timeout.
        var binding = await WaitForBindingAsync(
            normalizedScopeId,
            memberId,
            bindReceipt.BindingRunId,
            ResolveBindTimeout(request.BindTimeoutSeconds),
            ct);

        if (binding == null)
        {
            // Timed out before terminal: the member exists and will bind. Return
            // pending with no run — never fire-and-forget an invoke before bound.
            return BuildResponse(
                normalizedScopeId,
                memberId,
                member.TeamId,
                ProvisionWorkflowBindingStatusNames.Pending,
                publishedServiceId: null,
                revisionId: null,
                runId: null);
        }

        // 4. Bound. Optionally invoke once to produce a scope-stamped run.
        string? runId = null;
        if (request.RunImmediately)
        {
            runId = await InvokeWorkflowAsync(
                normalizedScopeId,
                binding.PublishedServiceId,
                request.Prompt ?? string.Empty,
                ct);
        }

        return BuildResponse(
            normalizedScopeId,
            memberId,
            member.TeamId,
            ProvisionWorkflowBindingStatusNames.Succeeded,
            publishedServiceId: binding.PublishedServiceId,
            revisionId: binding.RevisionId,
            runId: runId);
    }

    /// <summary>
    /// Polls the binding run until it reaches a terminal state. Returns the
    /// successful binding result, <c>null</c> on timeout, or throws on terminal
    /// failure. The wait is driven by an injected <see cref="TimeProvider"/> so
    /// tests advance time deterministically rather than sleeping wall-clock.
    /// </summary>
    private async Task<StudioMemberBindingRunResultResponse?> WaitForBindingAsync(
        string scopeId,
        string memberId,
        string bindingRunId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var run = await _memberService.GetBindingRunAsync(scopeId, memberId, bindingRunId, ct);

                if (IsSucceeded(run.Status))
                {
                    return run.Result
                        ?? throw new InvalidOperationException(
                            $"binding run '{bindingRunId}' reported succeeded but exposed no published service result.");
                }

                if (IsTerminalFailure(run.Status))
                {
                    var detail = run.Failure?.Message ?? run.Status;
                    throw new InvalidOperationException(
                        $"workflow binding for member '{memberId}' did not succeed (status '{run.Status}'): {detail}");
                }
            }
            catch (StudioMemberBindingRunNotFoundException)
            {
                // The binding-run read model is eventually consistent: immediately after
                // BindAsync the run record may not be queryable yet (the bind admission is
                // still materializing). Treat the not-found window as "not ready" and keep
                // polling until the deadline rather than surfacing it as a failure.
            }

            if (_timeProvider.GetUtcNow() >= deadline)
                return null;

            await Task.Delay(BindPollInterval, _timeProvider, ct);
        }
    }

    private async Task<string?> InvokeWorkflowAsync(
        string scopeId,
        string publishedServiceId,
        string prompt,
        CancellationToken ct)
    {
        var commandId = Guid.NewGuid().ToString("N");
        var receipt = await _serviceInvocationPort.InvokeAsync(
            new ServiceInvocationRequest
            {
                Identity = new ServiceIdentity
                {
                    TenantId = scopeId,
                    ServiceId = publishedServiceId,
                },
                EndpointId = WorkflowInvokeEndpointId,
                Payload = Any.Pack(new ChatRequestEvent
                {
                    Prompt = prompt,
                    ScopeId = scopeId,
                }),
                CommandId = commandId,
                CorrelationId = commandId,
            },
            ct);

        var runId = NormalizeOptional(receipt.RunId) ?? NormalizeOptional(receipt.CommandId);
        return runId;
    }

    private static ProvisionWorkflowResponse BuildResponse(
        string scopeId,
        string memberId,
        string? teamId,
        string bindingStatus,
        string? publishedServiceId,
        string? revisionId,
        string? runId) =>
        new(
            MemberId: memberId,
            ScopeId: scopeId,
            BindingStatus: bindingStatus,
            ObservatoryUrl: ObservatoryPath)
        {
            PublishedServiceId = publishedServiceId,
            RevisionId = revisionId,
            RunId = runId,
            // The editable Studio page is team-scoped; a freshly provisioned
            // member has no team yet, so the link is only built once one exists.
            StudioUrl = BuildStudioUrl(scopeId, teamId, memberId),
        };

    private static string? BuildStudioUrl(string scopeId, string? teamId, string memberId)
    {
        var normalizedTeamId = NormalizeOptional(teamId);
        if (normalizedTeamId == null)
            return null;

        return $"/scopes/{Uri.EscapeDataString(scopeId)}/teams/{Uri.EscapeDataString(normalizedTeamId)}/members/{Uri.EscapeDataString(memberId)}/workflow";
    }

    private static TimeSpan ResolveBindTimeout(int bindTimeoutSeconds)
    {
        var seconds = bindTimeoutSeconds > 0
            ? bindTimeoutSeconds
            : ProvisionWorkflowRequest.DefaultBindTimeoutSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsSucceeded(string status) =>
        string.Equals(status, StudioMemberBindingRunStatusNames.Succeeded, StringComparison.Ordinal);

    private static bool IsTerminalFailure(string status) =>
        string.Equals(status, StudioMemberBindingRunStatusNames.Failed, StringComparison.Ordinal) ||
        string.Equals(status, StudioMemberBindingRunStatusNames.Rejected, StringComparison.Ordinal);

    private static string GenerateWorkflowId() => $"workflow-{Guid.NewGuid():N}";

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }
}
