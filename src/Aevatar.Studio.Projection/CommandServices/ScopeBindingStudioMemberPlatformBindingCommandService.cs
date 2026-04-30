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
            ? $"platform-{request.BindingRunId}-1"
            : request.PlatformBindingCommandId;

        _ = Task.Run(
            () => RunBindingAsync(replyActorId, commandId, request.Clone()),
            CancellationToken.None);

        return Task.FromResult(new StudioMemberPlatformBindingAccepted
        {
            BindingRunId = request.BindingRunId,
            PlatformBindingCommandId = commandId,
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    private async Task RunBindingAsync(
        string replyActorId,
        string commandId,
        StudioMemberPlatformBindingStartRequested request)
    {
        try
        {
            var result = await _scopeBindingCommandPort
                .UpsertAsync(BuildScopeBindingRequest(request), CancellationToken.None)
                .ConfigureAwait(false);

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
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "StudioMember platform binding failed. bindingRunId={BindingRunId} platformBindingCommandId={CommandId}",
                request.BindingRunId,
                commandId);

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
                CancellationToken.None).ConfigureAwait(false);
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
        return bindingRequest.ImplementationCase switch
        {
            StudioMemberBindingRequest.ImplementationOneofCase.Workflow => new ScopeBindingUpsertRequest(
                ScopeId: bindingRequest.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.Workflow,
                Workflow: new ScopeBindingWorkflowSpec(bindingRequest.Workflow.WorkflowYamls.ToArray()),
                DisplayName: request.Admitted.DisplayName,
                RevisionId: bindingRequest.HasRevisionId ? bindingRequest.RevisionId : null,
                ServiceId: request.Admitted.PublishedServiceId),
            StudioMemberBindingRequest.ImplementationOneofCase.Script => new ScopeBindingUpsertRequest(
                ScopeId: bindingRequest.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.Scripting,
                Script: new ScopeBindingScriptSpec(
                    bindingRequest.Script.ScriptId,
                    bindingRequest.Script.HasScriptRevision ? bindingRequest.Script.ScriptRevision : null),
                DisplayName: request.Admitted.DisplayName,
                RevisionId: bindingRequest.HasRevisionId ? bindingRequest.RevisionId : null,
                ServiceId: request.Admitted.PublishedServiceId),
            StudioMemberBindingRequest.ImplementationOneofCase.Gagent => new ScopeBindingUpsertRequest(
                ScopeId: bindingRequest.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.GAgent,
                GAgent: new ScopeBindingGAgentSpec(
                    bindingRequest.Gagent.ActorTypeName,
                    bindingRequest.Gagent.Endpoints.Select(ToScopeBindingEndpoint).ToArray()),
                DisplayName: request.Admitted.DisplayName,
                RevisionId: bindingRequest.HasRevisionId ? bindingRequest.RevisionId : null,
                ServiceId: request.Admitted.PublishedServiceId),
            _ => throw new InvalidOperationException("binding request must carry exactly one implementation payload."),
        };
    }

    private static ScopeBindingGAgentEndpoint ToScopeBindingEndpoint(
        StudioMemberGAgentEndpointBindingRequest endpoint) =>
        new(
            endpoint.EndpointId,
            endpoint.DisplayName,
            ParseEndpointKind(endpoint.Kind),
            endpoint.RequestTypeUrl,
            endpoint.ResponseTypeUrl,
            endpoint.Description);

    private static ServiceEndpointKind ParseEndpointKind(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            "chat" => ServiceEndpointKind.Chat,
            "command" or null or "" => ServiceEndpointKind.Command,
            _ => ServiceEndpointKind.Command,
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
