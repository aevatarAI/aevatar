using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.StudioMember;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Projection.CommandServices;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: platform binding start flow dispatched detached continuations and did not make the command execution boundary explicit.
//   New principle: Start/Execute return accepted receipts only; terminal success/failure is delivered as a typed continuation to the run actor inbox.
internal sealed class ScopeBindingStudioMemberPlatformBindingCommandService : IStudioMemberPlatformBindingCommandPort
{
    private readonly IScopeBindingCommandPort _scopeBindingCommandPort;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<ScopeBindingStudioMemberPlatformBindingCommandService> _logger;

    public ScopeBindingStudioMemberPlatformBindingCommandService(
        IScopeBindingCommandPort scopeBindingCommandPort,
        IActorDispatchPort dispatchPort,
        ILogger<ScopeBindingStudioMemberPlatformBindingCommandService> logger)
    {
        _scopeBindingCommandPort = scopeBindingCommandPort ?? throw new ArgumentNullException(nameof(scopeBindingCommandPort));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        var outcome = await RunBindingAsync(commandId, request, ct).ConfigureAwait(false);
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

    private async Task<IMessage> RunBindingAsync(
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

            return new StudioMemberPlatformBindingFailed
            {
                BindingRunId = request.BindingRunId,
                PlatformBindingCommandId = commandId,
                Failure = new StudioMemberBindingFailure
                {
                    Code = "STUDIO_MEMBER_PLATFORM_BINDING_FAILED",
                    Message = ex.Message,
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            };
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
                ImplementationRef = BuildImplementationRef(result),
            },
        };
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
            Route = EnvelopeRouteSemantics.CreateDirect(
                "aevatar.studio.projection.platform-binding-command-service",
                replyActorId),
        };
        return _dispatchPort.DispatchAsync(replyActorId, envelope, ct);
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
