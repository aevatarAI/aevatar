using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Member-first Studio facade. Owns the orchestration that turns a
/// member-scoped request into the existing scope binding pipeline:
///
///   1. resolve the StudioMember authority (read model)
///   2. build a <see cref="ScopeBindingUpsertRequest"/> with
///      <c>ServiceId = publishedServiceId</c> — never the scope default
///   3. delegate to <see cref="IScopeBindingCommandPort"/>
///   4. record the resulting revision back on the member actor
///
/// Endpoints depend on this facade and never reach for the platform
/// binding port directly, which is what kept Studio's old surface in
/// scope-default fallback mode.
/// </summary>
public sealed class StudioMemberService : IStudioMemberService
{
    private readonly IStudioMemberCommandPort _memberCommandPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;
    private readonly IScopeBindingCommandPort _scopeBindingCommandPort;

    public StudioMemberService(
        IStudioMemberCommandPort memberCommandPort,
        IStudioMemberQueryPort memberQueryPort,
        IScopeBindingCommandPort scopeBindingCommandPort)
    {
        _memberCommandPort = memberCommandPort ?? throw new ArgumentNullException(nameof(memberCommandPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
        _scopeBindingCommandPort = scopeBindingCommandPort
            ?? throw new ArgumentNullException(nameof(scopeBindingCommandPort));
    }

    public Task<StudioMemberSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioMemberRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _memberCommandPort.CreateAsync(scopeId, request, ct);
    }

    public Task<StudioMemberRosterResponse> ListAsync(string scopeId, CancellationToken ct = default) =>
        _memberQueryPort.ListAsync(scopeId, ct);

    public Task<StudioMemberDetailResponse?> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default) =>
        _memberQueryPort.GetAsync(scopeId, memberId, ct);

    public async Task<StudioMemberBindingResponse> BindAsync(
        string scopeId,
        string memberId,
        UpdateStudioMemberBindingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var detail = await _memberQueryPort.GetAsync(scopeId, memberId, ct)
            ?? throw new InvalidOperationException(
                $"member '{memberId}' not found in scope '{scopeId}'.");

        var publishedServiceId = detail.Summary.PublishedServiceId;
        if (string.IsNullOrWhiteSpace(publishedServiceId))
        {
            throw new InvalidOperationException(
                $"member '{memberId}' has no publishedServiceId; this is a backend invariant violation.");
        }

        var implementationKindWire = detail.Summary.ImplementationKind;
        var bindingRequest = BuildScopeBindingRequest(
            scopeId,
            memberId,
            publishedServiceId,
            implementationKindWire,
            detail.Summary.DisplayName,
            request);

        var bindingResult = await _scopeBindingCommandPort.UpsertAsync(bindingRequest, ct);

        await _memberCommandPort.RecordBindingAsync(
            scopeId,
            memberId,
            bindingResult.ServiceId,
            bindingResult.RevisionId,
            implementationKindWire,
            ct);

        return new StudioMemberBindingResponse(
            MemberId: memberId,
            PublishedServiceId: bindingResult.ServiceId,
            RevisionId: bindingResult.RevisionId,
            ImplementationKind: implementationKindWire,
            ScopeId: scopeId,
            ExpectedActorId: bindingResult.ExpectedActorId);
    }

    public async Task<StudioMemberBindingContractResponse?> GetBindingAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var detail = await _memberQueryPort.GetAsync(scopeId, memberId, ct);
        return detail?.LastBinding;
    }

    private static ScopeBindingUpsertRequest BuildScopeBindingRequest(
        string scopeId,
        string memberId,
        string publishedServiceId,
        string implementationKindWire,
        string displayName,
        UpdateStudioMemberBindingRequest request)
    {
        return implementationKindWire switch
        {
            MemberImplementationKindNames.Workflow => new ScopeBindingUpsertRequest(
                ScopeId: scopeId,
                ImplementationKind: ScopeBindingImplementationKind.Workflow,
                Workflow: BuildWorkflowSpec(memberId, request),
                DisplayName: displayName,
                RevisionId: request.RevisionId,
                ServiceId: publishedServiceId),

            MemberImplementationKindNames.Script => new ScopeBindingUpsertRequest(
                ScopeId: scopeId,
                ImplementationKind: ScopeBindingImplementationKind.Scripting,
                Script: BuildScriptSpec(memberId, request),
                DisplayName: displayName,
                RevisionId: request.RevisionId,
                ServiceId: publishedServiceId),

            MemberImplementationKindNames.GAgent => new ScopeBindingUpsertRequest(
                ScopeId: scopeId,
                ImplementationKind: ScopeBindingImplementationKind.GAgent,
                GAgent: BuildGAgentSpec(memberId, request),
                DisplayName: displayName,
                RevisionId: request.RevisionId,
                ServiceId: publishedServiceId),

            _ => throw new InvalidOperationException(
                $"member '{memberId}' has unsupported implementationKind '{implementationKindWire}'."),
        };
    }

    private static ScopeBindingWorkflowSpec BuildWorkflowSpec(
        string memberId,
        UpdateStudioMemberBindingRequest request)
    {
        if (request.Workflow == null || request.Workflow.WorkflowYamls.Count == 0)
        {
            throw new InvalidOperationException(
                $"member '{memberId}' bind: workflow yamls are required for workflow members.");
        }

        return new ScopeBindingWorkflowSpec(request.Workflow.WorkflowYamls);
    }

    private static ScopeBindingScriptSpec BuildScriptSpec(
        string memberId,
        UpdateStudioMemberBindingRequest request)
    {
        if (request.Script == null || string.IsNullOrWhiteSpace(request.Script.ScriptId))
        {
            throw new InvalidOperationException(
                $"member '{memberId}' bind: scriptId is required for script members.");
        }

        return new ScopeBindingScriptSpec(
            ScriptId: request.Script.ScriptId,
            ScriptRevision: request.Script.ScriptRevision);
    }

    private static ScopeBindingGAgentSpec BuildGAgentSpec(
        string memberId,
        UpdateStudioMemberBindingRequest request)
    {
        if (request.GAgent == null || string.IsNullOrWhiteSpace(request.GAgent.ActorTypeName))
        {
            throw new InvalidOperationException(
                $"member '{memberId}' bind: actorTypeName is required for gagent members.");
        }

        var endpoints = (request.GAgent.Endpoints ?? [])
            .Select(static e => new ScopeBindingGAgentEndpoint(
                EndpointId: e.EndpointId,
                DisplayName: e.DisplayName,
                Kind: ParseEndpointKind(e.Kind),
                RequestTypeUrl: e.RequestTypeUrl,
                ResponseTypeUrl: e.ResponseTypeUrl,
                Description: e.Description ?? string.Empty))
            .ToList();

        return new ScopeBindingGAgentSpec(
            ActorTypeName: request.GAgent.ActorTypeName,
            Endpoints: endpoints);
    }

    private static ServiceEndpointKind ParseEndpointKind(string? kind)
    {
        var normalized = kind?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "command" => ServiceEndpointKind.Command,
            "chat" => ServiceEndpointKind.Chat,
            _ => ServiceEndpointKind.Unspecified,
        };
    }
}
