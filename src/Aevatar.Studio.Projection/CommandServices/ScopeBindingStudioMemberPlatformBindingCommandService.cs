using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.StudioMember;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ScopeBindingStudioMemberPlatformBindingCommandService : IStudioMemberPlatformBindingCommandPort
{
    private const string BindingRunDirectRoute = "aevatar.studio.projection.studio-member-binding-run";
    private const string ReadinessTimeoutFailureCode = "STUDIO_MEMBER_PLATFORM_BINDING_READINESS_TIMEOUT";
    private const string ReadinessFailedFailureCode = "STUDIO_MEMBER_PLATFORM_BINDING_READINESS_FAILED";
    private const string ReadinessTimeoutFailureMessage =
        "Scope binding commands completed, but service catalog / serving-set readiness was not observed before timeout.";
    private static readonly TimeSpan BindingReadinessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BindingReadinessPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IScopeBindingCommandPort _scopeBindingCommandPort;
    private readonly IScopeBindingReadinessQueryPort _readinessQueryPort;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<ScopeBindingStudioMemberPlatformBindingCommandService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;

    public ScopeBindingStudioMemberPlatformBindingCommandService(
        IScopeBindingCommandPort scopeBindingCommandPort,
        IScopeBindingReadinessQueryPort readinessQueryPort,
        IActorDispatchPort dispatchPort,
        ILogger<ScopeBindingStudioMemberPlatformBindingCommandService> logger)
        : this(scopeBindingCommandPort, readinessQueryPort, dispatchPort, logger, Task.Delay, () => DateTimeOffset.UtcNow)
    {
    }

    internal ScopeBindingStudioMemberPlatformBindingCommandService(
        IScopeBindingCommandPort scopeBindingCommandPort,
        IScopeBindingReadinessQueryPort readinessQueryPort,
        IActorDispatchPort dispatchPort,
        ILogger<ScopeBindingStudioMemberPlatformBindingCommandService> logger,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTimeOffset> utcNow)
    {
        _scopeBindingCommandPort = scopeBindingCommandPort ?? throw new ArgumentNullException(nameof(scopeBindingCommandPort));
        _readinessQueryPort = readinessQueryPort ?? throw new ArgumentNullException(nameof(readinessQueryPort));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    public Task ExecuteAsync(
        string replyActorId,
        string platformBindingCommandId,
        StudioMemberPlatformBindingStartRequested request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformBindingCommandId);
        ArgumentNullException.ThrowIfNull(request);

        var executionRequest = request.Clone();
        executionRequest.PlatformBindingCommandId = platformBindingCommandId;
        _ = Task.Run(
            () => RunBindingFireAndForgetAsync(replyActorId, platformBindingCommandId, executionRequest),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunBindingFireAndForgetAsync(
        string replyActorId,
        string commandId,
        StudioMemberPlatformBindingStartRequested request)
    {
        try
        {
            await RunBindingAsync(replyActorId, commandId, request, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding detached execution failed unexpectedly. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                commandId);
        }
    }

    private async Task RunBindingAsync(
        string replyActorId,
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

            try
            {
                await DispatchAsync(
                    replyActorId,
                    new StudioMemberPlatformBindingFailed
                    {
                        BindingRunId = request.BindingRunId,
                        PlatformBindingCommandId = commandId,
                        Failure = new StudioMemberBindingFailure
                        {
                            Code = "STUDIO_MEMBER_PLATFORM_BINDING_FAILED",
                            Message = ex.Message,
                            FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        },
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception dispatchEx)
            {
                _logger.LogError(
                    dispatchEx,
                    "StudioMember platform binding failure continuation dispatch failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                    request.BindingRunId,
                    commandId);
            }

            return;
        }

        ScopeBindingReadinessSnapshot readiness;
        try
        {
            readiness = await WaitForBindingReadyAsync(result, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding readiness observation failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                commandId);

            try
            {
                await DispatchAsync(
                    replyActorId,
                    new StudioMemberPlatformBindingFailed
                    {
                        BindingRunId = request.BindingRunId,
                        PlatformBindingCommandId = commandId,
                        Failure = new StudioMemberBindingFailure
                        {
                            Code = ReadinessFailedFailureCode,
                            Message = ex.Message,
                            FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        },
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception dispatchEx)
            {
                _logger.LogError(
                    dispatchEx,
                    "StudioMember platform binding readiness-failure continuation dispatch failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                    request.BindingRunId,
                    commandId);
            }

            return;
        }

        if (!readiness.InvokeReady)
        {
            try
            {
                await DispatchAsync(
                    replyActorId,
                    new StudioMemberPlatformBindingFailed
                    {
                        BindingRunId = request.BindingRunId,
                        PlatformBindingCommandId = commandId,
                        Failure = new StudioMemberBindingFailure
                        {
                            Code = ReadinessTimeoutFailureCode,
                            Message = ReadinessTimeoutFailureMessage,
                            FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        },
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception dispatchEx)
            {
                _logger.LogError(
                    dispatchEx,
                    "StudioMember platform binding readiness-timeout continuation dispatch failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId} readinessStatus={ReadinessStatus}",
                    request.BindingRunId,
                    commandId,
                    readiness.Status);
            }

            return;
        }

        try
        {
            await DispatchAsync(
                replyActorId,
                new StudioMemberPlatformBindingSucceeded
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
                        ImplementationRef = BuildImplementationRef(result),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding succeeded but success continuation dispatch failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                commandId);
        }
    }

    private async Task<ScopeBindingReadinessSnapshot> WaitForBindingReadyAsync(
        ScopeBindingUpsertResult result,
        CancellationToken ct)
    {
        var deadline = _utcNow() + BindingReadinessTimeout;
        ScopeBindingReadinessSnapshot? lastSnapshot = null;
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
            ExpectedDeploymentId: expectedDeploymentId);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            lastSnapshot = await _readinessQueryPort.GetReadinessAsync(request, ct).ConfigureAwait(false);
            if (lastSnapshot.InvokeReady || _utcNow() >= deadline)
                return lastSnapshot;

            var remaining = deadline - _utcNow();
            if (remaining <= TimeSpan.Zero)
                return lastSnapshot;

            var delay = remaining < BindingReadinessPollInterval
                ? remaining
                : BindingReadinessPollInterval;
            await _delayAsync(delay, ct).ConfigureAwait(false);
        }
    }

    private Task DispatchAsync(string actorId, IMessage payload, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(BindingRunDirectRoute, actorId),
        };

        return _dispatchPort.DispatchAsync(actorId, envelope, ct);
    }

    private static ScopeBindingUpsertRequest BuildScopeBindingRequest(
        StudioMemberPlatformBindingStartRequested request)
    {
        var bindingRequest = request.Request;
        var revisionId = ResolveRevisionId(request);
        var replayRevisionId = ResolveReplayRevisionId(request, revisionId);
        return bindingRequest.ImplementationCase switch
        {
            StudioMemberBindingRequest.ImplementationOneofCase.Workflow => new ScopeBindingUpsertRequest(
                ScopeId: bindingRequest.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.Workflow,
                Workflow: new ScopeBindingWorkflowSpec(bindingRequest.Workflow.WorkflowYamls.ToArray()),
                DisplayName: request.Admitted.DisplayName,
                RevisionId: revisionId,
                ServiceId: request.Admitted.PublishedServiceId,
                AllowExistingRevisionReplay: replayRevisionId != null,
                ReplayRevisionId: replayRevisionId),
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
                    bindingRequest.Gagent.ActorTypeName,
                    bindingRequest.Gagent.Endpoints.Select(ToScopeBindingEndpoint).ToArray()),
                DisplayName: request.Admitted.DisplayName,
                RevisionId: revisionId,
                ServiceId: request.Admitted.PublishedServiceId,
                AllowExistingRevisionReplay: replayRevisionId != null,
                ReplayRevisionId: replayRevisionId),
            _ => throw new InvalidOperationException("binding request must carry exactly one implementation payload."),
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
                    WorkflowId = result.Workflow?.WorkflowName ?? result.WorkflowName,
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
                    ActorTypeName = result.GAgent?.ActorTypeName ?? string.Empty,
                },
            },
            _ => new StudioMemberImplementationRef(),
        };
}
