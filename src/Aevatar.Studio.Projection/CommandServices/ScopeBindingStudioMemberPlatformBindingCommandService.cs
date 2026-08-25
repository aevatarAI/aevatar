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
//   New principle: Start/Execute return accepted receipts only; outcomes are delivered as typed continuations to the run actor inbox.
internal sealed class ScopeBindingStudioMemberPlatformBindingCommandService : IStudioMemberPlatformBindingCommandPort
{
    private const string BindingRunDirectRoute = "aevatar.studio.projection.studio-member-binding-run";
    private const string PlatformBindingFailedFailureCode = "STUDIO_MEMBER_PLATFORM_BINDING_FAILED";
    private const string ReadinessFailedFailureCode = "STUDIO_MEMBER_PLATFORM_BINDING_READINESS_FAILED";
    private const string ReadinessFailedMessage = "platform binding readiness could not be verified.";
    private const string ActivationFailedFailureCode = "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_FAILED";
    private const string ActivationPreparedArtifactMissingFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_PREPARED_ARTIFACT_MISSING";
    private const string ActivationRevisionPreparationFailedFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_REVISION_PREPARATION_FAILED";
    private const string ActivationCapabilityViewNotReadyFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_CAPABILITY_VIEW_NOT_READY";
    private const string ActivationAdmissionRejectedFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_ADMISSION_REJECTED";
    private const string ActivationAdmissionEvaluationFailedFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_ADMISSION_EVALUATION_FAILED";
    private const string RuntimeActivationFailedFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_RUNTIME_ACTIVATION_FAILED";
    private const string ServingTargetDeliveryFailedFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_SERVING_TARGET_DELIVERY_FAILED";
    private const string DefaultServingRevisionDeliveryFailedFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_DEFAULT_SERVING_REVISION_DELIVERY_FAILED";
    private const string DefaultServingRevisionSupersededFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_DEFAULT_SERVING_REVISION_SUPERSEDED";
    private const string ActivationDependencyUnavailableFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_DEPENDENCY_UNAVAILABLE";
    private const string RecoverySnapshotInvalidFailureCode =
        "STUDIO_MEMBER_PLATFORM_BINDING_RECOVERY_SNAPSHOT_INVALID";

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

    public Task<StudioMemberPlatformBindingExecutionStartAccepted> StartAsync(
        string replyActorId,
        StudioMemberPlatformBindingExecutionStartRequested request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyActorId);
        ArgumentNullException.ThrowIfNull(request);

        var commandId = string.IsNullOrWhiteSpace(request.PlatformBindingCommandId)
            ? StudioMemberConventions.BuildPlatformBindingCommandId(request.BindingRunId, 1)
            : request.PlatformBindingCommandId;

        return Task.FromResult(new StudioMemberPlatformBindingExecutionStartAccepted
        {
            BindingRunId = request.BindingRunId,
            PlatformBindingCommandId = commandId,
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = request.ProtocolVersion,
            ExecutionAttempt = request.ExecutionAttempt,
        });
    }

    public Task<StudioMemberPlatformBindingExecutionAccepted> ExecuteAsync(
        string replyActorId,
        StudioMemberPlatformBindingExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyActorId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlatformBindingCommandId);

        var executionRequest = request.Clone();
        _ = RunBindingAndDispatchAsync(replyActorId, executionRequest, ct);

        return Task.FromResult(new StudioMemberPlatformBindingExecutionAccepted(
            request.BindingRunId,
            request.PlatformBindingCommandId,
            request.ProtocolVersion,
            request.ExecutionAttempt));
    }

    private async Task RunBindingAndDispatchAsync(
        string replyActorId,
        StudioMemberPlatformBindingExecutionRequest request,
        CancellationToken ct)
    {
        IMessage? outcome;
        try
        {
            outcome = await RunBindingAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding execution failed unexpectedly. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                request.PlatformBindingCommandId);
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
                request.PlatformBindingCommandId,
                replyActorId);
        }
    }

    private async Task<IMessage?> RunBindingAsync(
        StudioMemberPlatformBindingExecutionRequest request,
        CancellationToken ct)
    {
        return request.ExecutionStage switch
        {
            StudioMemberPlatformBindingExecutionStage.CommandInFlight =>
                await RunPlatformCommandAsync(request, ct).ConfigureAwait(false),
            StudioMemberPlatformBindingExecutionStage.ReadinessInFlight =>
                await RunReadinessObservationAsync(request, ct).ConfigureAwait(false),
            _ => BuildFailedContinuation(
                request,
                RecoverySnapshotInvalidFailureCode,
                "platform binding execution stage is invalid."),
        };
    }

    private async Task<IMessage> RunPlatformCommandAsync(
        StudioMemberPlatformBindingExecutionRequest request,
        CancellationToken ct)
    {
        if (request.RecoverySnapshot != null)
        {
            return BuildFailedContinuation(
                request,
                RecoverySnapshotInvalidFailureCode,
                "platform binding command execution must not carry a recovery snapshot.");
        }

        var activationAttemptId = BuildActivationAttemptId(request);
        ScopeBindingUpsertResult result;
        try
        {
            result = await _scopeBindingCommandPort
                .UpsertAsync(BuildScopeBindingRequest(request) with
                {
                    ActivationAttemptId = activationAttemptId,
                }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                request.PlatformBindingCommandId);

            return BuildFailedContinuation(
                request,
                PlatformBindingFailedFailureCode,
                ex.Message);
        }

        StudioMemberPlatformBindingRecoverySnapshot recoverySnapshot;
        try
        {
            recoverySnapshot = BuildRecoverySnapshot(
                result,
                request,
                activationAttemptId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding result mapping failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                request.PlatformBindingCommandId);

            return BuildFailedContinuation(
                request,
                PlatformBindingFailedFailureCode,
                ex.Message);
        }

        return new StudioMemberPlatformBindingCommandsCompleted
        {
            BindingRunId = request.BindingRunId,
            PlatformBindingCommandId = request.PlatformBindingCommandId,
            RecoverySnapshot = recoverySnapshot,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = request.ProtocolVersion,
            ExecutionAttempt = request.ExecutionAttempt,
        };
    }

    private async Task<IMessage> RunReadinessObservationAsync(
        StudioMemberPlatformBindingExecutionRequest request,
        CancellationToken ct)
    {
        StudioMemberPlatformBindingRecoverySnapshot recoverySnapshot;
        try
        {
            recoverySnapshot = ValidateRecoverySnapshot(
                request.RecoverySnapshot
                    ?? throw new InvalidOperationException("platform binding recovery snapshot is required."),
                request);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding recovery snapshot validation failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                request.PlatformBindingCommandId);

            return BuildFailedContinuation(
                request,
                RecoverySnapshotInvalidFailureCode,
                ex.Message);
        }

        ScopeBindingReadinessSnapshot readiness;
        try
        {
            readiness = await WaitForBindingReadyAsync(recoverySnapshot, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding readiness observation failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                request.PlatformBindingCommandId);

            return BuildFailedContinuation(
                request,
                ReadinessFailedFailureCode,
                ReadinessFailedMessage);
        }

        if (readiness.InvokeReady)
        {
            return new StudioMemberPlatformBindingExecutionSucceeded
            {
                BindingRunId = request.BindingRunId,
                PlatformBindingCommandId = request.PlatformBindingCommandId,
                CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Result = recoverySnapshot.Result.Clone(),
                ProtocolVersion = request.ProtocolVersion,
                ExecutionAttempt = request.ExecutionAttempt,
            };
        }

        if (readiness.TerminalActivationFailureCode is { } activationFailureCode
            && activationFailureCode != ServiceDeploymentActivationFailureCode.Unspecified)
        {
            var (code, message) = MapTerminalActivationFailure(activationFailureCode);
            _logger.LogError(
                "StudioMember platform binding observed a terminal service activation failure. bindingRunId={BindingRunId} platformBindingCommandId={CommandId} activationFailureCode={ActivationFailureCode}",
                request.BindingRunId,
                request.PlatformBindingCommandId,
                activationFailureCode);
            return BuildFailedContinuation(request, code, message);
        }

        _logger.LogWarning(
            "StudioMember platform binding commands completed, but readiness was not observed before timeout. Leaving binding run pending for watchdog recovery. bindingRunId={BindingRunId} platformBindingCommandId={CommandId} readinessStatus={ReadinessStatus}",
            request.BindingRunId,
            request.PlatformBindingCommandId,
            readiness.Status);

        return BuildReadinessTimedOutContinuation(
            request,
            readiness.Status);
    }

    private async Task<ScopeBindingReadinessSnapshot> WaitForBindingReadyAsync(
        StudioMemberPlatformBindingRecoverySnapshot recoverySnapshot,
        StudioMemberPlatformBindingExecutionRequest bindingRequest,
        CancellationToken ct)
    {
        var deadline = _utcNow() + _options.BindingReadinessTimeout;
        var result = recoverySnapshot.Result
            ?? throw new InvalidOperationException("platform binding recovery result is required for readiness observation.");
        var expectedRevisionId = result.RevisionId?.Trim();
        if (string.IsNullOrWhiteSpace(expectedRevisionId))
            throw new InvalidOperationException("scope binding result revision id is required for readiness observation.");

        var expectedDeploymentId = recoverySnapshot.ExpectedDeploymentId?.Trim();
        if (string.IsNullOrWhiteSpace(expectedDeploymentId))
            throw new InvalidOperationException("scope binding result deployment id is required for readiness observation.");

        var expectedEndpointIds = NormalizeEndpointIds(recoverySnapshot.ExpectedEndpointIds);

        var request = new ScopeBindingReadinessRequest(
            bindingRequest.Request.ScopeId,
            result.PublishedServiceId,
            ExpectedRevisionId: expectedRevisionId,
            ExpectedDeploymentId: expectedDeploymentId,
            ExpectedEndpointIds: expectedEndpointIds,
            ExpectedActivationAttemptId: recoverySnapshot.ActivationAttemptId);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var snapshot = await _readinessQueryPort.GetReadinessAsync(request, ct).ConfigureAwait(false);
            if (snapshot.InvokeReady
                || snapshot.TerminalActivationFailureCode is { } activationFailureCode
                    && activationFailureCode != ServiceDeploymentActivationFailureCode.Unspecified
                || _utcNow() >= deadline)
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
        StudioMemberPlatformBindingExecutionRequest request) =>
        request.Request.ImplementationCase switch
        {
            StudioMemberBindingRequest.ImplementationOneofCase.Script => RequireScriptEndpointIds(
                result.Script?.EndpointIds),
            StudioMemberBindingRequest.ImplementationOneofCase.Gagent => BuildEffectiveGAgentEndpointIds(
                request.Request.Gagent.Endpoints.Select(endpoint => endpoint.EndpointId)),
            _ => ["chat"],
        };

    private static IReadOnlyList<string> NormalizeEndpointIds(IEnumerable<string>? endpointIds) =>
        endpointIds?
            .Select(endpointId => endpointId?.Trim())
            .Where(endpointId => !string.IsNullOrWhiteSpace(endpointId))
            .Select(endpointId => endpointId!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static IReadOnlyList<string> RequireScriptEndpointIds(IEnumerable<string>? endpointIds)
    {
        var normalizedEndpointIds = NormalizeEndpointIds(endpointIds);
        if (normalizedEndpointIds.Count == 0)
        {
            throw new InvalidOperationException(
                "platform binding script result must expose at least one command endpoint.");
        }

        return normalizedEndpointIds;
    }

    private static IReadOnlyList<string> BuildEffectiveGAgentEndpointIds(IEnumerable<string>? endpointIds)
    {
        var effectiveEndpointIds = NormalizeEndpointIds(endpointIds).ToList();
        if (!effectiveEndpointIds.Any(endpointId =>
                string.Equals(endpointId, "chat", StringComparison.OrdinalIgnoreCase)))
        {
            effectiveEndpointIds.Add("chat");
        }

        return effectiveEndpointIds.Order(StringComparer.Ordinal).ToArray();
    }

    private static StudioMemberPlatformBindingRecoverySnapshot BuildRecoverySnapshot(
        ScopeBindingUpsertResult result,
        StudioMemberPlatformBindingExecutionRequest request,
        string expectedActivationAttemptId)
    {
        var activationAttemptId = RequireCanonical(
            result.ActivationAttemptId,
            "scope binding result activation attempt id");
        if (!string.Equals(activationAttemptId, expectedActivationAttemptId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "scope binding result activation attempt id does not match the platform binding command.");
        }

        var snapshot = new StudioMemberPlatformBindingRecoverySnapshot
        {
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = result.ServiceId,
                RevisionId = result.RevisionId,
                ImplementationKind = ToStudioKind(result.ImplementationKind),
                ExpectedActorId = result.ExpectedActorId,
                ImplementationRef = BuildImplementationRef(result, request),
            },
            ExpectedDeploymentId = result.ExpectedDeploymentId,
            ActivationAttemptId = activationAttemptId,
        };
        snapshot.ExpectedEndpointIds.AddRange(BuildExpectedEndpointIds(result, request));
        return snapshot;
    }

    private static StudioMemberPlatformBindingRecoverySnapshot ValidateRecoverySnapshot(
        StudioMemberPlatformBindingRecoverySnapshot snapshot,
        StudioMemberPlatformBindingExecutionRequest request)
    {
        var result = snapshot.Result
            ?? throw new InvalidOperationException("platform binding recovery result is required.");
        var expectedServiceId = request.Admitted.PublishedServiceId;
        var recoveredServiceId = RequireCanonical(
            result.PublishedServiceId,
            "platform binding recovery published service id");
        if (string.IsNullOrWhiteSpace(expectedServiceId)
            || !string.Equals(recoveredServiceId, expectedServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery published service id does not match the admitted binding.");
        }

        var expectedRevisionId = ResolveRevisionId(request);
        var recoveredRevisionId = RequireCanonical(
            result.RevisionId,
            "platform binding recovery revision id");
        if (!string.Equals(recoveredRevisionId, expectedRevisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery revision id does not match the binding command.");
        }

        var expectedImplementationKind = ToStudioKind(request.Request.ImplementationCase);
        if (result.ImplementationKind != expectedImplementationKind
            || request.Admitted.ImplementationKind != expectedImplementationKind)
        {
            throw new InvalidOperationException(
                "platform binding recovery implementation kind does not match the binding request.");
        }

        var expectedDeploymentId = RequireCanonical(
            snapshot.ExpectedDeploymentId,
            "platform binding recovery deployment id");

        if (!string.IsNullOrWhiteSpace(snapshot.ActivationAttemptId))
        {
            _ = RequireCanonical(
                snapshot.ActivationAttemptId,
                "platform binding recovery activation attempt id");
        }

        var normalizedEndpointIds = NormalizeEndpointIds(snapshot.ExpectedEndpointIds);
        if (!snapshot.ExpectedEndpointIds.SequenceEqual(normalizedEndpointIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery endpoint ids must be normalized.");
        }

        ValidateRecoveryImplementation(result, request, expectedDeploymentId);

        var expectedRecoveryEndpointIds = BuildExpectedRecoveryEndpointIds(request, result);
        if (!normalizedEndpointIds.SequenceEqual(expectedRecoveryEndpointIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery endpoint ids do not match the sealed binding result.");
        }

        var validated = snapshot.Clone();
        validated.Result.PublishedServiceId = recoveredServiceId;
        validated.Result.RevisionId = recoveredRevisionId;
        validated.Result.ExpectedActorId = RequireCanonical(
            result.ExpectedActorId,
            "platform binding recovery expected actor id");
        validated.ExpectedDeploymentId = expectedDeploymentId;
        return validated;
    }

    private static void ValidateRecoveryImplementation(
        StudioMemberPlatformBindingResult result,
        StudioMemberPlatformBindingExecutionRequest request,
        string expectedDeploymentId)
    {
        var expectedActorId = RequireCanonical(
            result.ExpectedActorId,
            "platform binding recovery expected actor id");

        var implementationRef = result.ImplementationRef
            ?? throw new InvalidOperationException("platform binding recovery implementation ref is required.");
        switch (request.Request.ImplementationCase)
        {
            case StudioMemberBindingRequest.ImplementationOneofCase.Workflow:
                ValidateWorkflowRecoveryImplementation(
                    implementationRef,
                    request.Request.Workflow,
                    result.RevisionId,
                    expectedActorId,
                    expectedDeploymentId);
                break;
            case StudioMemberBindingRequest.ImplementationOneofCase.Script:
                ValidateScriptRecoveryImplementation(
                    implementationRef,
                    request.Request.Script,
                    expectedActorId,
                    expectedDeploymentId);
                break;
            case StudioMemberBindingRequest.ImplementationOneofCase.Gagent:
                ValidateGAgentRecoveryImplementation(
                    implementationRef,
                    request.Request.Gagent,
                    expectedActorId,
                    expectedDeploymentId);
                break;
            default:
                throw new InvalidOperationException(
                    "platform binding recovery implementation request is required.");
        }
    }

    private static void ValidateWorkflowRecoveryImplementation(
        StudioMemberImplementationRef implementationRef,
        StudioMemberWorkflowBindingRequest request,
        string revisionId,
        string expectedActorId,
        string expectedDeploymentId)
    {
        if (implementationRef.Workflow == null
            || implementationRef.Script != null
            || implementationRef.Gagent != null)
        {
            throw new InvalidOperationException(
                "platform binding recovery workflow implementation ref has an invalid shape.");
        }

        var workflowId = RequireCanonical(
            implementationRef.Workflow.WorkflowId,
            "platform binding recovery workflow id");
        if (!string.Equals(workflowId, request.WorkflowId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery workflow id does not match the binding request.");
        }

        var workflowRevision = RequireCanonical(
            implementationRef.Workflow.WorkflowRevision,
            "platform binding recovery workflow revision");
        if (!string.Equals(workflowRevision, revisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery workflow revision does not match the binding result.");
        }

        var definitionActorIdPrefix = RequireCanonical(
            implementationRef.Workflow.DefinitionActorIdPrefix,
            "platform binding recovery workflow definition actor id prefix");
        if (!string.Equals(
                expectedActorId,
                $"{definitionActorIdPrefix}:{expectedDeploymentId}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery workflow actor does not match the expected deployment.");
        }
    }

    private static void ValidateScriptRecoveryImplementation(
        StudioMemberImplementationRef implementationRef,
        StudioMemberScriptBindingRequest request,
        string expectedActorId,
        string expectedDeploymentId)
    {
        if (implementationRef.Script == null
            || implementationRef.Workflow != null
            || implementationRef.Gagent != null)
        {
            throw new InvalidOperationException(
                "platform binding recovery script implementation ref has an invalid shape.");
        }

        var scriptId = RequireCanonical(
            implementationRef.Script.ScriptId,
            "platform binding recovery script id");
        if (!string.Equals(scriptId, request.ScriptId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery script id does not match the binding request.");
        }

        var scriptRevision = RequireCanonical(
            implementationRef.Script.ScriptRevision,
            "platform binding recovery script revision");
        if (request.HasScriptRevision
            && !string.Equals(scriptRevision, request.ScriptRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery script revision does not match the binding request.");
        }

        var endpointIds = NormalizeEndpointIds(implementationRef.Script.EndpointIds);
        if (endpointIds.Count == 0)
        {
            throw new InvalidOperationException(
                "platform binding recovery script endpoint ids are required.");
        }
        if (!implementationRef.Script.EndpointIds.SequenceEqual(endpointIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery script endpoint ids must be normalized.");
        }

        var requiredActorId = $"gagent-service:script-runtime:{expectedDeploymentId}";
        if (!string.Equals(expectedActorId, requiredActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery script actor does not match the expected deployment.");
        }
    }

    private static void ValidateGAgentRecoveryImplementation(
        StudioMemberImplementationRef implementationRef,
        StudioMemberGAgentBindingRequest request,
        string expectedActorId,
        string expectedDeploymentId)
    {
        if (implementationRef.Gagent == null
            || implementationRef.Workflow != null
            || implementationRef.Script != null)
        {
            throw new InvalidOperationException(
                "platform binding recovery gagent implementation ref has an invalid shape.");
        }

        var agentKind = RequireCanonical(
            implementationRef.Gagent.AgentKind,
            "platform binding recovery gagent agent kind");
        if (!string.Equals(agentKind, request.AgentKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery gagent agent kind does not match the binding request.");
        }

        var requiredActorId = $"gagent-service:static-runtime:{expectedDeploymentId}";
        if (!string.Equals(expectedActorId, requiredActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "platform binding recovery gagent actor does not match the expected deployment.");
        }
    }

    private static StudioMemberImplementationKind ToStudioKind(
        StudioMemberBindingRequest.ImplementationOneofCase implementationCase) =>
        implementationCase switch
        {
            StudioMemberBindingRequest.ImplementationOneofCase.Workflow => StudioMemberImplementationKind.Workflow,
            StudioMemberBindingRequest.ImplementationOneofCase.Script => StudioMemberImplementationKind.Script,
            StudioMemberBindingRequest.ImplementationOneofCase.Gagent => StudioMemberImplementationKind.Gagent,
            _ => StudioMemberImplementationKind.Unspecified,
        };

    private static IReadOnlyList<string> BuildExpectedRecoveryEndpointIds(
        StudioMemberPlatformBindingExecutionRequest request,
        StudioMemberPlatformBindingResult result) =>
        request.Request.ImplementationCase switch
        {
            StudioMemberBindingRequest.ImplementationOneofCase.Workflow => ["chat"],
            StudioMemberBindingRequest.ImplementationOneofCase.Gagent => BuildEffectiveGAgentEndpointIds(
                request.Request.Gagent.Endpoints.Select(endpoint => endpoint.EndpointId)),
            StudioMemberBindingRequest.ImplementationOneofCase.Script => NormalizeEndpointIds(
                result.ImplementationRef?.Script?.EndpointIds),
            _ => throw new InvalidOperationException(
                "platform binding recovery implementation request is required."),
        };

    private static string RequireCanonical(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} is required.");
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException($"{fieldName} must be canonical.");
        return value;
    }

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

    private static StudioMemberPlatformBindingReadinessObservationTimedOut BuildReadinessTimedOutContinuation(
        StudioMemberPlatformBindingExecutionRequest request,
        ScopeBindingReadinessStatus readinessStatus) =>
        new()
        {
            BindingRunId = request.BindingRunId,
            PlatformBindingCommandId = request.PlatformBindingCommandId,
            ReadinessStatus = ToStudioReadinessStatus(readinessStatus),
            TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = request.ProtocolVersion,
            ExecutionAttempt = request.ExecutionAttempt,
        };

    private static StudioMemberPlatformBindingExecutionFailed BuildFailedContinuation(
        StudioMemberPlatformBindingExecutionRequest request,
        string code,
        string message) =>
        new()
        {
            BindingRunId = request.BindingRunId,
            PlatformBindingCommandId = request.PlatformBindingCommandId,
            Failure = new StudioMemberBindingFailure
            {
                Code = code,
                Message = message,
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            ProtocolVersion = request.ProtocolVersion,
            ExecutionAttempt = request.ExecutionAttempt,
            ExecutionStage = request.ExecutionStage,
        };

    private static (string Code, string Message) MapTerminalActivationFailure(
        ServiceDeploymentActivationFailureCode failureCode) =>
        failureCode switch
        {
            ServiceDeploymentActivationFailureCode.PreparedArtifactMissing =>
                (ActivationPreparedArtifactMissingFailureCode,
                    "platform service activation failed because its prepared artifact was unavailable."),
            ServiceDeploymentActivationFailureCode.RevisionPreparationFailed =>
                (ActivationRevisionPreparationFailedFailureCode,
                    "platform service activation failed because revision preparation failed."),
            ServiceDeploymentActivationFailureCode.CapabilityViewNotReady =>
                (ActivationCapabilityViewNotReadyFailureCode,
                    "platform service activation failed because capability readiness was not established."),
            ServiceDeploymentActivationFailureCode.AdmissionRejected =>
                (ActivationAdmissionRejectedFailureCode,
                    "platform service activation was rejected by admission policy."),
            ServiceDeploymentActivationFailureCode.AdmissionEvaluationFailed =>
                (ActivationAdmissionEvaluationFailedFailureCode,
                    "platform service activation admission evaluation failed."),
            ServiceDeploymentActivationFailureCode.RuntimeActivationFailed =>
                (RuntimeActivationFailedFailureCode,
                    "platform service runtime activation failed."),
            ServiceDeploymentActivationFailureCode.ServingTargetDeliveryFailed =>
                (ServingTargetDeliveryFailedFailureCode,
                    "platform service activation could not deliver serving targets."),
            ServiceDeploymentActivationFailureCode.DefaultServingRevisionDeliveryFailed =>
                (DefaultServingRevisionDeliveryFailedFailureCode,
                    "platform service activation could not commit the default serving revision."),
            ServiceDeploymentActivationFailureCode.DefaultServingRevisionSuperseded =>
                (DefaultServingRevisionSupersededFailureCode,
                    "platform service activation was superseded by a newer serving generation."),
            ServiceDeploymentActivationFailureCode.ActivationDependencyUnavailable =>
                (ActivationDependencyUnavailableFailureCode,
                    "platform service activation dependency was unavailable."),
            _ => (ActivationFailedFailureCode, "platform service activation failed."),
        };

    private static string BuildActivationAttemptId(
        StudioMemberPlatformBindingExecutionRequest request) =>
        $"{RequireCanonical(request.PlatformBindingCommandId, "platform binding command id")}:a{request.ExecutionAttempt}";

    private static ScopeBindingUpsertRequest BuildScopeBindingRequest(
        StudioMemberPlatformBindingExecutionRequest request)
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
        StudioMemberPlatformBindingExecutionRequest request,
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

    private static string ResolveRevisionId(StudioMemberPlatformBindingExecutionRequest request)
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
        StudioMemberPlatformBindingExecutionRequest request,
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

    private static StudioMemberPlatformBindingReadinessStatus ToStudioReadinessStatus(
        ScopeBindingReadinessStatus status) =>
        status switch
        {
            ScopeBindingReadinessStatus.ServiceCatalogMissing =>
                StudioMemberPlatformBindingReadinessStatus.ServiceCatalogMissing,
            ScopeBindingReadinessStatus.ServingSetMissing =>
                StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
            ScopeBindingReadinessStatus.EligibleServingTargetMissing =>
                StudioMemberPlatformBindingReadinessStatus.EligibleServingTargetMissing,
            ScopeBindingReadinessStatus.ServiceCatalogTargetMissing =>
                StudioMemberPlatformBindingReadinessStatus.ServiceCatalogTargetMissing,
            ScopeBindingReadinessStatus.Ready => StudioMemberPlatformBindingReadinessStatus.Ready,
            ScopeBindingReadinessStatus.TrafficViewTargetMissing =>
                StudioMemberPlatformBindingReadinessStatus.TrafficViewTargetMissing,
            ScopeBindingReadinessStatus.PreparedArtifactMissing =>
                StudioMemberPlatformBindingReadinessStatus.PreparedArtifactMissing,
            ScopeBindingReadinessStatus.InvocationCatalogNotReady =>
                StudioMemberPlatformBindingReadinessStatus.InvocationCatalogNotReady,
            _ => StudioMemberPlatformBindingReadinessStatus.Unspecified,
        };

    private static StudioMemberImplementationRef BuildImplementationRef(
        ScopeBindingUpsertResult result,
        StudioMemberPlatformBindingExecutionRequest request) =>
        result.ImplementationKind switch
        {
            ScopeBindingImplementationKind.Workflow => new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = ResolveWorkflowId(result),
                    WorkflowRevision = result.RevisionId,
                    DefinitionActorIdPrefix = result.DefinitionActorIdPrefix,
                },
            },
            ScopeBindingImplementationKind.Scripting => BuildScriptImplementationRef(result),
            ScopeBindingImplementationKind.GAgent => new StudioMemberImplementationRef
            {
                Gagent = new StudioMemberGAgentRef
                {
                    ActorTypeName = result.GAgent?.DiagnosticClrTypeName ?? string.Empty,
                    AgentKind = request.Request.Gagent?.AgentKind ?? string.Empty,
                },
            },
            _ => new StudioMemberImplementationRef(),
        };

    private static StudioMemberImplementationRef BuildScriptImplementationRef(ScopeBindingUpsertResult result)
    {
        var endpointIds = RequireScriptEndpointIds(result.Script?.EndpointIds);
        var scriptRef = new StudioMemberScriptRef
        {
            ScriptId = result.Script?.ScriptId ?? string.Empty,
            ScriptRevision = result.Script?.ScriptRevision ?? result.RevisionId,
        };
        scriptRef.EndpointIds.AddRange(endpointIds);
        return new StudioMemberImplementationRef { Script = scriptRef };
    }

    private static string ResolveWorkflowId(ScopeBindingUpsertResult result)
    {
        var workflowId = result.Workflow?.WorkflowId?.Trim();
        if (!string.IsNullOrWhiteSpace(workflowId))
            return workflowId;

        throw new InvalidOperationException("scope binding workflow result workflow id is required for workflow member binding.");
    }
}
