using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.StudioMember;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Projection.CommandServices;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: platform binding start flow dispatched detached continuations and did not make the command execution boundary explicit.
//   New principle: Start/Execute return accepted receipts only; terminal success/failure is delivered as a typed continuation to the run actor inbox.
internal sealed class ScopeBindingStudioMemberPlatformBindingCommandService : IStudioMemberPlatformBindingCommandPort
{
    private const string BindingRunDirectRoute = "aevatar.studio.projection.studio-member-binding-run";
    private const string PlatformBindingFailedFailureCode = "STUDIO_MEMBER_PLATFORM_BINDING_FAILED";
    private const string ReadinessFailedFailureCode = "STUDIO_MEMBER_PLATFORM_BINDING_READINESS_FAILED";

    private readonly IScopeBindingCommandPort _scopeBindingCommandPort;
    private readonly IScopeBindingReadinessQueryPort _readinessQueryPort;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<ScopeBindingStudioMemberPlatformBindingCommandService> _logger;
    private readonly StudioMemberPlatformBindingOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;

    public ScopeBindingStudioMemberPlatformBindingCommandService(
        IScopeBindingCommandPort scopeBindingCommandPort,
        IScopeBindingReadinessQueryPort readinessQueryPort,
        IActorDispatchPort dispatchPort,
        ILogger<ScopeBindingStudioMemberPlatformBindingCommandService> logger,
        IOptions<StudioMemberPlatformBindingOptions> options)
        : this(scopeBindingCommandPort, readinessQueryPort, dispatchPort, logger, options, Task.Delay, () => DateTimeOffset.UtcNow)
    {
    }

    internal ScopeBindingStudioMemberPlatformBindingCommandService(
        IScopeBindingCommandPort scopeBindingCommandPort,
        IScopeBindingReadinessQueryPort readinessQueryPort,
        IActorDispatchPort dispatchPort,
        ILogger<ScopeBindingStudioMemberPlatformBindingCommandService> logger,
        IOptions<StudioMemberPlatformBindingOptions> options,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTimeOffset> utcNow)
    {
        _scopeBindingCommandPort = scopeBindingCommandPort ?? throw new ArgumentNullException(nameof(scopeBindingCommandPort));
        _readinessQueryPort = readinessQueryPort ?? throw new ArgumentNullException(nameof(readinessQueryPort));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        _options = NormalizeOptions(options.Value);
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public Task<StudioMemberPlatformBindingAccepted> StartAsync(
        string replyActorId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyActorId);
        ArgumentNullException.ThrowIfNull(request);

        var commandId = string.IsNullOrWhiteSpace(request.PlatformBindingCommandId)
            ? StudioMemberConventions.BuildPlatformBindingCommandId(request.BindingRunId, 1)
            : request.PlatformBindingCommandId;

        return Task.FromResult(new StudioMemberPlatformBindingAccepted
        {
            BindingRunId = request.BindingRunId,
            PlatformBindingCommandId = commandId,
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    public Task<StudioMemberPlatformBindingExecutionAccepted> ExecuteAsync(
        string replyActorId,
        string platformBindingCommandId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformBindingCommandId);
        ArgumentNullException.ThrowIfNull(request);

        var executionRequest = request.Clone();
        executionRequest.PlatformBindingCommandId = platformBindingCommandId;
        _ = RunBindingAndDispatchAsync(replyActorId, platformBindingCommandId, executionRequest, ct);

        return Task.FromResult(new StudioMemberPlatformBindingExecutionAccepted(
            request.BindingRunId,
            platformBindingCommandId));
    }

    private async Task RunBindingAndDispatchAsync(
        string replyActorId,
        string commandId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct)
    {
        IMessage? outcome;
        try
        {
            outcome = await RunBindingAsync(commandId, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding execution failed unexpectedly. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                commandId);
            return;
        }

        if (outcome == null)
            return;

        try
        {
            await DispatchContinuationAsync(replyActorId, outcome, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding continuation dispatch failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId} replyActorId={ReplyActorId}",
                request.BindingRunId,
                commandId,
                replyActorId);
        }
    }

    private async Task<IMessage?> RunBindingAsync(
        string commandId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct)
    {
        ScopeBindingUpsertResult result;
        try
        {
            result = await _scopeBindingCommandPort
                .UpsertAsync(BuildScopeBindingRequest(request), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                commandId);

            return BuildFailedContinuation(
                request.BindingRunId,
                commandId,
                PlatformBindingFailedFailureCode,
                ex.Message);
        }

        ScopeBindingReadinessSnapshot readiness;
        try
        {
            readiness = await WaitForBindingReadyAsync(result, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding readiness observation failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                commandId);

            return BuildFailedContinuation(
                request.BindingRunId,
                commandId,
                ReadinessFailedFailureCode,
                ex.Message);
        }

        if (!readiness.InvokeReady)
        {
            _logger.LogWarning(
                "StudioMember platform binding commands completed, but readiness was not observed before timeout. Leaving binding run pending for watchdog recovery. bindingRunId={BindingRunId} platformBindingCommandId={CommandId} readinessStatus={ReadinessStatus}",
                request.BindingRunId,
                commandId,
                readiness.Status);

            return null;
        }

        StudioMemberImplementationRef implementationRef;
        try
        {
            implementationRef = BuildImplementationRef(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding result mapping failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                commandId);

            return BuildFailedContinuation(
                request.BindingRunId,
                commandId,
                PlatformBindingFailedFailureCode,
                ex.Message);
        }

        return new StudioMemberPlatformBindingSucceeded
        {
            BindingRunId = request.BindingRunId,
            PlatformBindingCommandId = commandId,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = result.ServiceId,
                RevisionId = result.RevisionId,
                ImplementationKind = ToStudioKind(result.ImplementationKind),
                ExpectedActorId = result.ExpectedActorId,
                ImplementationRef = implementationRef,
            },
        };
    }

    private async Task<ScopeBindingReadinessSnapshot> WaitForBindingReadyAsync(
        ScopeBindingUpsertResult result,
        StudioMemberPlatformBindingStartRequested bindingRequest,
        CancellationToken ct)
    {
        var deadline = _utcNow() + _options.BindingReadinessTimeout;
        var expectedRevisionId = result.RevisionId?.Trim();
        if (string.IsNullOrWhiteSpace(expectedRevisionId))
            throw new InvalidOperationException("scope binding result revision id is required for readiness observation.");

        var expectedDeploymentId = result.ExpectedDeploymentId?.Trim();
        if (string.IsNullOrWhiteSpace(expectedDeploymentId))
            throw new InvalidOperationException("scope binding result deployment id is required for readiness observation.");

        var request = new ScopeBindingReadinessRequest(
            result.ScopeId,
            result.ServiceId,
            ExpectedRevisionId: expectedRevisionId,
            ExpectedDeploymentId: expectedDeploymentId,
            ExpectedEndpointIds: BuildExpectedEndpointIds(result, bindingRequest));

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var snapshot = await _readinessQueryPort.GetReadinessAsync(request, ct).ConfigureAwait(false);
            if (snapshot.InvokeReady || _utcNow() >= deadline)
                return snapshot;

            var remaining = deadline - _utcNow();
            if (remaining <= TimeSpan.Zero)
                return snapshot;

            var delay = remaining < _options.BindingReadinessPollInterval
                ? remaining
                : _options.BindingReadinessPollInterval;
            await _delayAsync(delay, ct).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<string> BuildExpectedEndpointIds(
        ScopeBindingUpsertResult result,
        StudioMemberPlatformBindingStartRequested request) =>
        request.Request.ImplementationCase switch
        {
            StudioMemberBindingRequest.ImplementationOneofCase.Script => NormalizeEndpointIds(result.Script?.EndpointIds),
            StudioMemberBindingRequest.ImplementationOneofCase.Gagent => NormalizeEndpointIds(
                request.Request.Gagent.Endpoints.Select(endpoint => endpoint.EndpointId)),
            _ => ["chat"],
        };

    private static IReadOnlyList<string> NormalizeEndpointIds(IEnumerable<string>? endpointIds) =>
        endpointIds?
            .Select(endpointId => endpointId?.Trim())
            .Where(endpointId => !string.IsNullOrWhiteSpace(endpointId))
            .Select(endpointId => endpointId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static StudioMemberPlatformBindingOptions NormalizeOptions(StudioMemberPlatformBindingOptions options)
    {
        if (options.BindingReadinessTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Studio member platform binding readiness timeout must be positive.");

        if (options.BindingReadinessPollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Studio member platform binding readiness poll interval must be positive.");

        return options;
    }

    private Task DispatchContinuationAsync(
        string replyActorId,
        IMessage continuation,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(continuation),
            Route = EnvelopeRouteSemantics.CreateDirect(BindingRunDirectRoute, replyActorId),
        };
        return _dispatchPort.DispatchAsync(replyActorId, envelope, ct);
    }

    private static StudioMemberPlatformBindingFailed BuildFailedContinuation(
        string bindingRunId,
        string commandId,
        string code,
        string message) =>
        new()
        {
            BindingRunId = bindingRunId,
            PlatformBindingCommandId = commandId,
            Failure = new StudioMemberBindingFailure
            {
                Code = code,
                Message = message,
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

    private static ScopeBindingUpsertRequest BuildScopeBindingRequest(
        StudioMemberPlatformBindingStartRequested request)
    {
        var bindingRequest = request.Request;
        var revisionId = ResolveRevisionId(request);
        var replayRevisionId = ResolveReplayRevisionId(request, revisionId);
        return bindingRequest.ImplementationCase switch
        {
            StudioMemberBindingRequest.ImplementationOneofCase.Workflow =>
                BuildWorkflowScopeBindingRequest(request, revisionId),
            StudioMemberBindingRequest.ImplementationOneofCase.Script => new ScopeBindingUpsertRequest(
                ScopeId: bindingRequest.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.Scripting,
                Script: new ScopeBindingScriptSpec(
                    bindingRequest.Script.ScriptId,
                    bindingRequest.Script.HasScriptRevision ? bindingRequest.Script.ScriptRevision : null),
                DisplayName: request.Admitted.DisplayName,
                RevisionId: revisionId,
                ServiceId: request.Admitted.PublishedServiceId,
                AllowExistingRevisionReplay: true,
                ReplayRevisionId: replayRevisionId),
            StudioMemberBindingRequest.ImplementationOneofCase.Gagent => new ScopeBindingUpsertRequest(
                ScopeId: bindingRequest.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.GAgent,
                GAgent: new ScopeBindingGAgentSpec(
                    bindingRequest.Gagent.AgentKind,
                    bindingRequest.Gagent.Endpoints.Select(ToScopeBindingEndpoint).ToArray()),
                DisplayName: request.Admitted.DisplayName,
                RevisionId: revisionId,
                ServiceId: request.Admitted.PublishedServiceId,
                AllowExistingRevisionReplay: true,
                ReplayRevisionId: revisionId),
            _ => throw new InvalidOperationException("binding request must carry exactly one implementation payload."),
        };
    }

    private static ScopeBindingUpsertRequest BuildWorkflowScopeBindingRequest(
        StudioMemberPlatformBindingStartRequested request,
        string revisionId)
    {
        var bindingRequest = request.Request;
        var admissionPlan = bindingRequest.Workflow.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                "workflow capability admission plan is required for Studio member binding.");
        return new ScopeBindingUpsertRequest(
            ScopeId: bindingRequest.ScopeId,
            ImplementationKind: ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec(
                bindingRequest.Workflow.WorkflowId,
                bindingRequest.Workflow.WorkflowYamls.ToArray()),
            DisplayName: request.Admitted.DisplayName,
            RevisionId: revisionId,
            ServiceId: request.Admitted.PublishedServiceId,
            AllowExistingRevisionReplay: true,
            ReplayRevisionId: revisionId)
        {
            CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                string.Empty,
                executionMode: admissionPlan.ExecutionMode,
                existingPlan: admissionPlan),
        };
    }

    private static string ResolveRevisionId(StudioMemberPlatformBindingStartRequested request)
    {
        var explicitRevisionId = request.Request.HasRevisionId
            ? request.Request.RevisionId?.Trim()
            : null;
        if (!string.IsNullOrWhiteSpace(explicitRevisionId))
            return explicitRevisionId;

        var source = !string.IsNullOrWhiteSpace(request.PlatformBindingCommandId)
            ? request.PlatformBindingCommandId
            : request.BindingRunId;
        return $"rev-{BuildStableRevisionComponent(source)}";
    }

    private static string? ResolveReplayRevisionId(
        StudioMemberPlatformBindingStartRequested request,
        string revisionId)
    {
        var explicitRevisionId = request.Request.HasRevisionId
            ? request.Request.RevisionId?.Trim()
            : null;
        if (!string.IsNullOrWhiteSpace(explicitRevisionId))
            return null;

        var expectedRevisionId = $"rev-{BuildStableRevisionComponent(request.PlatformBindingCommandId)}";
        return string.Equals(revisionId, expectedRevisionId, StringComparison.Ordinal)
            ? revisionId
            : null;
    }

    private static string BuildStableRevisionComponent(string value)
    {
        var component = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                component.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (component.Length > 0 && component[^1] != '-')
                component.Append('-');
        }

        while (component.Length > 0 && component[^1] == '-')
            component.Length--;

        return component.Length == 0 ? "binding" : component.ToString();
    }

    private static ScopeBindingGAgentEndpoint ToScopeBindingEndpoint(
        StudioMemberGAgentEndpointBindingRequest endpoint) =>
        new(
            endpoint.EndpointId,
            endpoint.DisplayName,
            ToEndpointKind(endpoint.Kind),
            endpoint.RequestTypeUrl,
            endpoint.ResponseTypeUrl,
            endpoint.Description);

    private static ServiceEndpointKind ToEndpointKind(StudioMemberGAgentEndpointKind kind) =>
        kind switch
        {
            StudioMemberGAgentEndpointKind.Command => ServiceEndpointKind.Command,
            StudioMemberGAgentEndpointKind.Chat => ServiceEndpointKind.Chat,
            _ => throw new InvalidOperationException($"Unsupported gagent endpoint kind '{kind}'."),
        };

    private static StudioMemberImplementationKind ToStudioKind(ScopeBindingImplementationKind kind) =>
        kind switch
        {
            ScopeBindingImplementationKind.Workflow => StudioMemberImplementationKind.Workflow,
            ScopeBindingImplementationKind.Scripting => StudioMemberImplementationKind.Script,
            ScopeBindingImplementationKind.GAgent => StudioMemberImplementationKind.Gagent,
            _ => StudioMemberImplementationKind.Unspecified,
        };

    private static StudioMemberImplementationRef BuildImplementationRef(ScopeBindingUpsertResult result) =>
        result.ImplementationKind switch
        {
            ScopeBindingImplementationKind.Workflow => new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = ResolveWorkflowId(result),
                    WorkflowRevision = result.RevisionId,
                },
            },
            ScopeBindingImplementationKind.Scripting => new StudioMemberImplementationRef
            {
                Script = new StudioMemberScriptRef
                {
                    ScriptId = result.Script?.ScriptId ?? string.Empty,
                    ScriptRevision = result.Script?.ScriptRevision ?? result.RevisionId,
                },
            },
            ScopeBindingImplementationKind.GAgent => new StudioMemberImplementationRef
            {
                Gagent = new StudioMemberGAgentRef
                {
                    ActorTypeName = result.GAgent?.DiagnosticClrTypeName ?? string.Empty,
                },
            },
            _ => new StudioMemberImplementationRef(),
        };

    private static string ResolveWorkflowId(ScopeBindingUpsertResult result)
    {
        var workflowId = result.Workflow?.WorkflowId?.Trim();
        if (!string.IsNullOrWhiteSpace(workflowId))
            return workflowId;

        throw new InvalidOperationException("scope binding workflow result workflow id is required for workflow member binding.");
    }
}
