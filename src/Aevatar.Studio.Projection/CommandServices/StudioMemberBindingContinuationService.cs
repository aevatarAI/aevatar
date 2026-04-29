using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.Mapping;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Handles committed StudioMember binding requests by performing the external
/// scope binding upsert, then reporting completion or failure back to the same
/// StudioMember actor. This is deliberately not a read-model projector: it is
/// the business continuation endpoint that a durable event-delivery subscriber
/// invokes after <see cref="StudioMemberBindingRequestedEvent"/> is committed.
/// </summary>
internal sealed class StudioMemberBindingContinuationService
{
    private readonly IScopeBindingCommandPort _scopeBindingCommandPort;
    private readonly IStudioMemberCommandPort _memberCommandPort;
    private readonly ILogger<StudioMemberBindingContinuationService> _logger;

    public StudioMemberBindingContinuationService(
        IScopeBindingCommandPort scopeBindingCommandPort,
        IStudioMemberCommandPort memberCommandPort,
        ILogger<StudioMemberBindingContinuationService> logger)
    {
        _scopeBindingCommandPort = scopeBindingCommandPort
            ?? throw new ArgumentNullException(nameof(scopeBindingCommandPort));
        _memberCommandPort = memberCommandPort ?? throw new ArgumentNullException(nameof(memberCommandPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleRequestedAsync(
        StudioMemberBindingRequestedEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        ScopeBindingUpsertResult result;
        try
        {
            _logger.LogInformation(
                "Studio member binding continuation upserting scope binding. scopeId={ScopeId} memberId={MemberId} bindingId={BindingId} publishedServiceId={PublishedServiceId}",
                evt.ScopeId,
                evt.MemberId,
                evt.BindingId,
                evt.PublishedServiceId);
            result = await _scopeBindingCommandPort.UpsertAsync(BuildScopeBindingRequest(evt), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Studio member binding continuation failed scope binding upsert. scopeId={ScopeId} memberId={MemberId} bindingId={BindingId}",
                evt.ScopeId,
                evt.MemberId,
                evt.BindingId);
            await _memberCommandPort.FailBindingAsync(
                evt.ScopeId,
                evt.MemberId,
                new StudioMemberBindingFailureRequest(
                    BindingId: evt.BindingId,
                    FailureCode: "scope_binding_failed",
                    FailureSummary: ex.Message,
                    Retryable: true,
                    FailedAt: DateTimeOffset.UtcNow),
                ct);
            return;
        }

        _logger.LogInformation(
            "Studio member binding continuation completing member binding. scopeId={ScopeId} memberId={MemberId} bindingId={BindingId} revisionId={RevisionId}",
            evt.ScopeId,
            evt.MemberId,
            evt.BindingId,
            result.RevisionId);
        await _memberCommandPort.CompleteBindingAsync(
            evt.ScopeId,
            evt.MemberId,
            new StudioMemberBindingCompletionRequest(
                BindingId: evt.BindingId,
                RevisionId: result.RevisionId,
                ExpectedActorId: result.ExpectedActorId,
                ResolvedImplementationRef: BuildResolvedImplementationRef(evt.ImplementationKind, result, evt.Request),
                CompletedAt: DateTimeOffset.UtcNow),
            ct);
        _logger.LogInformation(
            "Studio member binding continuation completed member binding. scopeId={ScopeId} memberId={MemberId} bindingId={BindingId}",
            evt.ScopeId,
            evt.MemberId,
            evt.BindingId);
    }

    private static ScopeBindingUpsertRequest BuildScopeBindingRequest(
        StudioMemberBindingRequestedEvent evt)
    {
        var implementationKindWire = MemberImplementationKindMapper.ToWireName(evt.ImplementationKind);
        return evt.ImplementationKind switch
        {
            StudioMemberImplementationKind.Workflow => new ScopeBindingUpsertRequest(
                ScopeId: evt.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.Workflow,
                Workflow: new ScopeBindingWorkflowSpec(evt.Request?.Workflow?.WorkflowYamls.ToList() ?? []),
                DisplayName: evt.DisplayName,
                RevisionId: ResolveRevisionId(evt),
                ServiceId: evt.PublishedServiceId),

            StudioMemberImplementationKind.Script => new ScopeBindingUpsertRequest(
                ScopeId: evt.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.Scripting,
                Script: new ScopeBindingScriptSpec(
                    ScriptId: evt.Request?.Script?.ScriptId ?? string.Empty,
                    ScriptRevision: NullIfEmpty(evt.Request?.Script?.ScriptRevision)),
                DisplayName: evt.DisplayName,
                RevisionId: ResolveRevisionId(evt),
                ServiceId: evt.PublishedServiceId),

            StudioMemberImplementationKind.Gagent => new ScopeBindingUpsertRequest(
                ScopeId: evt.ScopeId,
                ImplementationKind: ScopeBindingImplementationKind.GAgent,
                GAgent: new ScopeBindingGAgentSpec(
                    ActorTypeName: evt.Request?.Gagent?.ActorTypeName ?? string.Empty,
                    Endpoints: (evt.Request?.Gagent?.Endpoints ?? [])
                        .Select(static endpoint => new ScopeBindingGAgentEndpoint(
                            EndpointId: endpoint.EndpointId,
                            DisplayName: endpoint.DisplayName,
                            Kind: ParseEndpointKind(endpoint.Kind),
                            RequestTypeUrl: endpoint.RequestTypeUrl,
                            ResponseTypeUrl: endpoint.ResponseTypeUrl,
                            Description: endpoint.Description))
                        .ToList()),
                DisplayName: evt.DisplayName,
                RevisionId: ResolveRevisionId(evt),
                ServiceId: evt.PublishedServiceId),

            _ => throw new InvalidOperationException(
                $"Unsupported StudioMember implementationKind '{implementationKindWire}'."),
        };
    }

    private static StudioMemberImplementationRefResponse? BuildResolvedImplementationRef(
        StudioMemberImplementationKind implementationKind,
        ScopeBindingUpsertResult result,
        StudioMemberBindingSpec? request)
    {
        return implementationKind switch
        {
            StudioMemberImplementationKind.Workflow when result.Workflow is not null =>
                new StudioMemberImplementationRefResponse(
                    MemberImplementationKindNames.Workflow,
                    WorkflowId: result.Workflow.WorkflowName,
                    WorkflowRevision: result.RevisionId),
            StudioMemberImplementationKind.Script =>
                new StudioMemberImplementationRefResponse(
                    MemberImplementationKindNames.Script,
                    ScriptId: result.Script?.ScriptId ?? request?.Script?.ScriptId,
                    ScriptRevision: result.Script?.ScriptRevision ?? request?.Script?.ScriptRevision),
            StudioMemberImplementationKind.Gagent =>
                new StudioMemberImplementationRefResponse(
                    MemberImplementationKindNames.GAgent,
                    ActorTypeName: result.GAgent?.ActorTypeName ?? request?.Gagent?.ActorTypeName),
            _ => null,
        };
    }

    private static ServiceEndpointKind ParseEndpointKind(string? kind)
    {
        return kind?.Trim().ToLowerInvariant() switch
        {
            "command" => ServiceEndpointKind.Command,
            "chat" => ServiceEndpointKind.Chat,
            _ => ServiceEndpointKind.Unspecified,
        };
    }

    private static string? NullIfEmpty(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static string ResolveRevisionId(StudioMemberBindingRequestedEvent evt) =>
        NullIfEmpty(evt.Request?.RevisionId) ?? evt.BindingId;
}
