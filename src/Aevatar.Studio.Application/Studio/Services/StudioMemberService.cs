using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Member-first Studio facade. Bind is intentionally an accepted command
/// surface: this service delegates admission to the StudioMember authority
/// and never performs synchronous scope-binding orchestration inside the
/// request stack. Read and revision operations still resolve the member's
/// published service through the read model before touching platform ports.
/// </summary>
public sealed class StudioMemberService : IStudioMemberService
{
    // Mirrors the values pinned in ScopeWorkflowCapabilityOptions
    // (FixedServiceAppId / FixedServiceNamespace). Adding a runtime options
    // dependency here would force every Studio.Application consumer to wire
    // the platform Application layer just to read two const strings; copying
    // the constants keeps the layer boundary clean and matches what
    // AppScopedWorkflowService already does for the same reason.
    private const string ServiceAppId = "default";
    private const string ServiceNamespace = "default";
    private const string DefaultSmokePrompt = "Hello from Studio Bind.";

    private readonly IStudioMemberCommandPort _memberCommandPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IServiceCommandPort _serviceCommandPort;

    public StudioMemberService(
        IStudioMemberCommandPort memberCommandPort,
        IStudioMemberQueryPort memberQueryPort,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceCommandPort serviceCommandPort)
    {
        _memberCommandPort = memberCommandPort ?? throw new ArgumentNullException(nameof(memberCommandPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort
            ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceCommandPort = serviceCommandPort
            ?? throw new ArgumentNullException(nameof(serviceCommandPort));
    }

    public Task<StudioMemberSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioMemberRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validation lives at this Application boundary (CLAUDE.md
        // `严格分层 / 上层依赖抽象`). The Projection-layer command port is
        // an interchangeable transport; if it ever swaps, the bounds must
        // not silently disappear with it. Callers receive a single typed
        // error path here regardless of which command port is wired in.
        StudioMemberCreateRequestValidator.Validate(request);

        return _memberCommandPort.CreateAsync(scopeId, request, ct);
    }

    public Task<StudioMemberRosterResponse> ListAsync(
        string scopeId,
        StudioMemberRosterPageRequest? page = null,
        CancellationToken ct = default) =>
        _memberQueryPort.ListAsync(scopeId, page, ct);

    public async Task<StudioMemberDetailResponse> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        // Mirrors GetBindingAsync semantics: a missing member is
        // unambiguous "404 STUDIO_MEMBER_NOT_FOUND", not a 200-with-null
        // body that the frontend would have to pattern-match. Endpoints
        // catch the typed exception and return the same body shape from
        // every member-centric endpoint.
        var detail = await _memberQueryPort.GetAsync(scopeId, memberId, ct)
            ?? throw new StudioMemberNotFoundException(scopeId, memberId);
        return detail;
    }

    public Task<StudioMemberBindingAcceptedResponse> BindAsync(
        string scopeId,
        string memberId,
        UpdateStudioMemberBindingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _memberCommandPort.RequestBindingAsync(scopeId, memberId, request, ct);
    }

    public async Task<StudioMemberBindingViewResponse> GetBindingAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var detail = await _memberQueryPort.GetAsync(scopeId, memberId, ct)
            ?? throw new StudioMemberNotFoundException(scopeId, memberId);
        return new StudioMemberBindingViewResponse(detail.LastBinding, detail.LatestBindingRun);
    }

    public async Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
        string scopeId,
        string memberId,
        string endpointId,
        CancellationToken ct = default)
    {
        var normalizedEndpointId = NormalizeRequired(endpointId, nameof(endpointId));
        var context = await ResolveBoundServiceContextAsync(scopeId, memberId, ct);
        return BuildMemberEndpointContractResponse(
            context.ScopeId,
            context.MemberId,
            context.PublishedServiceId,
            normalizedEndpointId,
            context.Service,
            context.Revisions);
    }

    public async Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
        string scopeId,
        string memberId,
        string revisionId,
        CancellationToken ct = default)
    {
        var normalizedRevisionId = NormalizeRequired(revisionId, nameof(revisionId));
        var context = await ResolveBoundServiceContextAsync(scopeId, memberId, ct);
        var revision = ResolveRevisionOrThrow(context.Revisions, normalizedRevisionId);

        if (string.Equals(
                revision.Status,
                ServiceRevisionStatus.Retired.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Revision '{normalizedRevisionId}' is retired and cannot be activated.");
        }

        // NOTE: Activate is intentionally non-atomic — it dispatches
        // SetDefaultServingRevision then ActivateServiceRevision. If the
        // second command fails after the first succeeds, the revision is
        // marked default-serving but never moves to "active". This matches
        // the legacy scope-default activate path; no compensating action
        // is taken here. Both commands are also idempotent on the
        // platform side, so a retried Activate from the caller will
        // converge.
        await _serviceCommandPort.SetDefaultServingRevisionAsync(
            new SetDefaultServingRevisionCommand
            {
                Identity = context.Identity.Clone(),
                RevisionId = normalizedRevisionId,
            },
            ct);
        await _serviceCommandPort.ActivateServiceRevisionAsync(
            new ActivateServiceRevisionCommand
            {
                Identity = context.Identity.Clone(),
                RevisionId = normalizedRevisionId,
            },
            ct);

        return new StudioMemberBindingActivationResponse(
            ScopeId: context.ScopeId,
            MemberId: context.MemberId,
            PublishedServiceId: context.PublishedServiceId,
            DisplayName: context.Service.DisplayName,
            RevisionId: normalizedRevisionId);
    }

    public async Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
        string scopeId,
        string memberId,
        string revisionId,
        CancellationToken ct = default)
    {
        var normalizedRevisionId = NormalizeRequired(revisionId, nameof(revisionId));
        var context = await ResolveBoundServiceContextAsync(scopeId, memberId, ct);

        // Verify the revision exists in the catalog before dispatching.
        // The platform's RetireRevision is idempotent, but a typo in the
        // revisionId would silently succeed and the projection would
        // surface the contradiction later — the deterministic 400 here is
        // the friendlier failure mode.
        _ = ResolveRevisionOrThrow(context.Revisions, normalizedRevisionId);

        await _serviceCommandPort.RetireRevisionAsync(
            new RetireServiceRevisionCommand
            {
                Identity = context.Identity.Clone(),
                RevisionId = normalizedRevisionId,
            },
            ct);

        return new StudioMemberBindingRevisionActionResponse(
            ScopeId: context.ScopeId,
            MemberId: context.MemberId,
            PublishedServiceId: context.PublishedServiceId,
            RevisionId: normalizedRevisionId,
            Status: MemberRevisionLifecycleStatusNames.Retired);
    }

    /// <summary>
    /// Resolves the published service the member is currently bound to in
    /// one round-trip: member authority → service catalog → revisions.
    /// Bundling the three queries here means the contract / activate /
    /// retire paths all see the same revision snapshot they validated
    /// against — no TOCTOU window where a revision was retired between
    /// the verify and the dispatch — and the test surface only stubs one
    /// method instead of three. Throws <see cref="StudioMemberNotFoundException"/>
    /// for missing members and <see cref="InvalidOperationException"/> for
    /// "exists but never bound" so the endpoint layer maps each to the
    /// right HTTP status.
    /// </summary>
    private async Task<BoundServiceContext> ResolveBoundServiceContextAsync(
        string scopeId,
        string memberId,
        CancellationToken ct)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));

        var detail = await _memberQueryPort.GetAsync(normalizedScopeId, memberId, ct)
            ?? throw new StudioMemberNotFoundException(normalizedScopeId, memberId);

        var publishedServiceId = detail.Summary.PublishedServiceId;
        if (string.IsNullOrWhiteSpace(publishedServiceId))
        {
            throw new InvalidOperationException(
                $"member '{memberId}' has no publishedServiceId; this is a backend invariant violation.");
        }

        var identity = new ServiceIdentity
        {
            TenantId = normalizedScopeId,
            AppId = ServiceAppId,
            Namespace = ServiceNamespace,
            ServiceId = publishedServiceId,
        };

        // The published service surfaces only after the member is bound — a
        // pre-bind read returns null from the platform query port. We surface
        // that as a 400 (not 404) so the frontend can distinguish "missing
        // member" from "exists but unbound" without parsing error text.
        var service = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct)
            ?? throw BuildMemberNotBoundException(memberId);
        var revisions = await _serviceLifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        return new BoundServiceContext(
            normalizedScopeId,
            detail.Summary.MemberId,
            publishedServiceId,
            identity,
            service,
            revisions);
    }

    private static ServiceRevisionSnapshot ResolveRevisionOrThrow(
        ServiceRevisionCatalogSnapshot? revisions,
        string revisionId)
    {
        var revision = revisions?.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        if (revision == null)
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' was not found on the member's published service.");
        }

        return revision;
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }

    private static InvalidOperationException BuildMemberNotBoundException(string memberId) =>
        new($"member '{memberId}' has no published service yet; bind the member before reading or mutating its revisions.");

    private readonly record struct BoundServiceContext(
        string ScopeId,
        string MemberId,
        string PublishedServiceId,
        ServiceIdentity Identity,
        ServiceCatalogSnapshot Service,
        ServiceRevisionCatalogSnapshot? Revisions);

    private static StudioMemberEndpointContractResponse? BuildMemberEndpointContractResponse(
        string scopeId,
        string memberId,
        string publishedServiceId,
        string normalizedEndpointId,
        ServiceCatalogSnapshot service,
        ServiceRevisionCatalogSnapshot? revisions)
    {
        var currentRevision = ServiceEndpointContractMath.ResolveCurrentContractRevision(
            service, revisions, normalizedEndpointId);
        var endpoint = currentRevision?.Endpoints.FirstOrDefault(x =>
                string.Equals(x.EndpointId, normalizedEndpointId, StringComparison.Ordinal))
            ?? service.Endpoints.FirstOrDefault(x =>
                string.Equals(x.EndpointId, normalizedEndpointId, StringComparison.Ordinal));
        if (endpoint == null)
            return null;

        var implementationKind = ServiceEndpointContractMath.NullIfEmpty(currentRevision?.ImplementationKind);
        var supportsSse = ServiceEndpointContractMath.IsChatEndpoint(endpoint.Kind);
        var streamFrameFormat = ServiceEndpointContractMath.ResolveStreamFrameFormat(
            supportsSse, implementationKind);
        var supportsAguiFrames = string.Equals(
            streamFrameFormat,
            ServiceEndpointContractMath.StreamFrameFormatAgui,
            StringComparison.Ordinal);
        var invokePath = supportsSse
            ? BuildMemberStreamInvokePath(scopeId, memberId, normalizedEndpointId)
            : BuildMemberInvokePath(scopeId, memberId, normalizedEndpointId);
        var responseContentType = supportsSse ? "text/event-stream" : "application/json";
        var defaultSmokeInputMode = supportsSse ? "prompt" : "typed-payload";
        var defaultSmokePrompt = supportsSse ? DefaultSmokePrompt : null;
        var sampleRequestJson = supportsSse
            ? null
            : ServiceEndpointContractMath.BuildTypedInvokeRequestExampleBody(
                endpoint.RequestTypeUrl, prettyPrinted: true);
        var smokeTestSupported = supportsSse || sampleRequestJson != null;

        return new StudioMemberEndpointContractResponse(
            ScopeId: scopeId,
            MemberId: memberId,
            PublishedServiceId: publishedServiceId,
            EndpointId: normalizedEndpointId,
            InvokePath: invokePath,
            Method: "POST",
            RequestContentType: "application/json",
            ResponseContentType: responseContentType,
            RequestTypeUrl: endpoint.RequestTypeUrl,
            ResponseTypeUrl: endpoint.ResponseTypeUrl,
            SupportsSse: supportsSse,
            SupportsWebSocket: false,
            SupportsAguiFrames: supportsAguiFrames,
            StreamFrameFormat: streamFrameFormat,
            SmokeTestSupported: smokeTestSupported,
            DefaultSmokeInputMode: defaultSmokeInputMode,
            DefaultSmokePrompt: defaultSmokePrompt,
            SampleRequestJson: sampleRequestJson,
            DeploymentStatus: service.DeploymentStatus,
            RevisionId: currentRevision?.RevisionId
                ?? ServiceEndpointContractMath.NullIfEmpty(service.DefaultServingRevisionId)
                ?? ServiceEndpointContractMath.NullIfEmpty(service.ActiveServingRevisionId)
                ?? string.Empty,
            CurlExample: smokeTestSupported
                ? BuildCurlExample(invokePath, supportsSse, endpoint.RequestTypeUrl)
                : null,
            FetchExample: smokeTestSupported
                ? BuildFetchExample(invokePath, supportsSse, endpoint.RequestTypeUrl)
                : null);
    }

    private static string BuildMemberInvokePath(string scopeId, string memberId, string endpointId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/members/{Uri.EscapeDataString(memberId)}/invoke/{Uri.EscapeDataString(endpointId)}";

    private static string BuildMemberStreamInvokePath(string scopeId, string memberId, string endpointId) =>
        $"{BuildMemberInvokePath(scopeId, memberId, endpointId)}:stream";

    private static string BuildCurlExample(string invokePath, bool supportsSse, string? requestTypeUrl)
    {
        if (supportsSse)
        {
            var requestBody = System.Text.Json.JsonSerializer.Serialize(new { prompt = DefaultSmokePrompt });
            return $"""
curl -N -X POST \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -H "Authorization: Bearer <token>" \
  "{invokePath}" \
  -d '{requestBody}'
""";
        }

        var typedBody = ServiceEndpointContractMath.BuildTypedInvokeRequestExampleBody(
            requestTypeUrl, prettyPrinted: false) ?? "{}";
        return $"""
curl -X POST \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <token>" \
  "{invokePath}" \
  -d '{typedBody}'
""";
    }

    private static string BuildFetchExample(string invokePath, bool supportsSse, string? requestTypeUrl)
    {
        if (supportsSse)
        {
            return $$"""
const response = await fetch("{{invokePath}}", {
  method: "POST",
  headers: {
    "Content-Type": "application/json",
    "Accept": "text/event-stream",
    "Authorization": "Bearer <token>",
  },
  body: JSON.stringify({
    prompt: "{{DefaultSmokePrompt}}",
  }),
});

// Consume response.body as an SSE stream.
""";
        }

        var normalizedRequestTypeUrl = ServiceEndpointContractMath.NullIfEmpty(requestTypeUrl) ?? "<type-url>";
        var payloadBase64 = ServiceEndpointContractMath.BuildBase64PayloadPlaceholder(normalizedRequestTypeUrl);
        return $$"""
const response = await fetch("{{invokePath}}", {
  method: "POST",
  headers: {
    "Content-Type": "application/json",
    "Authorization": "Bearer <token>",
  },
  body: JSON.stringify({
    payloadTypeUrl: "{{normalizedRequestTypeUrl}}",
    payloadBase64: "{{payloadBase64}}",
  }),
});
""";
    }

}
