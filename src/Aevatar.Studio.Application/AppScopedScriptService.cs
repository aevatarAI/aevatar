using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Studio.Application;

public sealed class AppScopedScriptService
{
    private const string BackendClientName = "AppBridgeBackend";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IScopeScriptQueryPort? _scriptQueryPort;
    private readonly IScopeScriptCommandPort? _scriptCommandPort;
    private readonly IScopeScriptSaveObservationPort? _scriptSaveObservationPort;
    private readonly IScriptDefinitionSnapshotPort? _definitionSnapshotPort;
    private readonly IScriptEvolutionApplicationService? _scriptEvolutionService;
    private readonly IScriptCatalogQueryPort? _scriptCatalogQueryPort;
    private readonly IScriptEvolutionDecisionReadPort? _scriptEvolutionDecisionReadPort;
    private readonly IScriptingActorAddressResolver? _scriptingActorAddressResolver;
    private readonly IScriptRuntimeActivityQueryPort? _runtimeActivityQueryPort;
    private readonly IHttpClientFactory _httpClientFactory;

    public AppScopedScriptService(
        IHttpClientFactory httpClientFactory,
        IScopeScriptQueryPort? scriptQueryPort = null,
        IScopeScriptCommandPort? scriptCommandPort = null,
        IScopeScriptSaveObservationPort? scriptSaveObservationPort = null,
        IScriptDefinitionSnapshotPort? definitionSnapshotPort = null,
        IScriptEvolutionApplicationService? scriptEvolutionService = null,
        IScriptCatalogQueryPort? scriptCatalogQueryPort = null,
        IScriptEvolutionDecisionReadPort? scriptEvolutionDecisionReadPort = null,
        IScriptingActorAddressResolver? scriptingActorAddressResolver = null,
        IScriptRuntimeActivityQueryPort? runtimeActivityQueryPort = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _scriptQueryPort = scriptQueryPort;
        _scriptCommandPort = scriptCommandPort;
        _scriptSaveObservationPort = scriptSaveObservationPort;
        _definitionSnapshotPort = definitionSnapshotPort;
        _scriptEvolutionService = scriptEvolutionService;
        _scriptCatalogQueryPort = scriptCatalogQueryPort;
        _scriptEvolutionDecisionReadPort = scriptEvolutionDecisionReadPort;
        _scriptingActorAddressResolver = scriptingActorAddressResolver;
        _runtimeActivityQueryPort = runtimeActivityQueryPort;
    }

    public async Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        if (_scriptQueryPort != null)
            return await _scriptQueryPort.ListAsync(normalizedScopeId, ct);

        return await SendAsync<List<ScopeScriptSummary>>(
                   HttpMethod.Get,
                   $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/scripts",
                   body: null,
                   ct) ??
               [];
    }

    public async Task<ScopeScriptDetail?> GetAsync(
        string scopeId,
        string scriptId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedScriptId = NormalizeRequired(scriptId, nameof(scriptId));
        if (_scriptQueryPort != null && _definitionSnapshotPort != null)
        {
            var script = await _scriptQueryPort.GetByScriptIdAsync(normalizedScopeId, normalizedScriptId, ct);
            if (script == null)
                return null;

            var snapshot = string.IsNullOrWhiteSpace(script.DefinitionActorId)
                ? null
                : await _definitionSnapshotPort.TryGetAsync(script.DefinitionActorId, script.ActiveRevision, ct);
            return new ScopeScriptDetail(
                true,
                normalizedScopeId,
                script,
                snapshot == null
                    ? null
                    : new ScopeScriptSource(
                        snapshot.SourceText,
                        string.IsNullOrWhiteSpace(snapshot.DefinitionActorId)
                            ? script.DefinitionActorId
                            : snapshot.DefinitionActorId,
                        snapshot.Revision,
                        snapshot.SourceHash));
        }

        return await SendAsync<ScopeScriptDetail>(
            HttpMethod.Get,
            $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/scripts/{Uri.EscapeDataString(normalizedScriptId)}",
            body: null,
            ct,
            allowNotFound: true);
    }

    public async Task<IReadOnlyList<ScriptRuntimeActivitySnapshot>> ListRuntimeActivitiesAsync(
        int take,
        CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take <= 0 ? 24 : take, 1, 200);
        if (_runtimeActivityQueryPort != null)
        {
            // Refactor (iter56/cluster-928-script-any-public-delete): old=public Any → JSON readmodel surface,
            // new=removed (keep internal semantic Any).
            // Studio activity now uses native AppScriptReadModel data.
            return await _runtimeActivityQueryPort.ListAsync(boundedTake, ct);
        }

        return await SendAsync<List<ScriptRuntimeActivitySnapshot>>(
                   HttpMethod.Get,
                   $"/api/app/scripts/runtimes?take={boundedTake}",
                   body: null,
                   ct) ??
               [];
    }

    public async Task<ScriptRuntimeActivitySnapshot?> GetRuntimeActivityAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var normalizedActorId = NormalizeRequired(actorId, nameof(actorId));
        if (_runtimeActivityQueryPort != null)
            return await _runtimeActivityQueryPort.GetAsync(normalizedActorId, ct);

        return await SendAsync<ScriptRuntimeActivitySnapshot>(
            HttpMethod.Get,
            $"/api/app/scripts/runtimes/{Uri.EscapeDataString(normalizedActorId)}/activity",
            body: null,
            ct,
            allowNotFound: true);
    }

    public async Task<AppScriptCatalogSnapshot?> GetCatalogAsync(
        string scopeId,
        string scriptId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedScriptId = NormalizeRequired(scriptId, nameof(scriptId));
        if (_scriptCatalogQueryPort != null && _scriptingActorAddressResolver != null)
        {
            var catalogActorId = _scriptingActorAddressResolver.GetCatalogActorId(normalizedScopeId);
            var snapshot = await _scriptCatalogQueryPort.GetCatalogEntryAsync(catalogActorId, normalizedScriptId, ct);
            return snapshot == null
                ? null
                : ToCatalogSnapshot(snapshot);
        }

        return await SendAsync<AppScriptCatalogSnapshot>(
            HttpMethod.Get,
            $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/scripts/{Uri.EscapeDataString(normalizedScriptId)}/catalog",
            body: null,
            ct,
            allowNotFound: true);
    }

    public async Task<ScriptPromotionDecision?> GetEvolutionDecisionAsync(
        string proposalId,
        CancellationToken ct = default)
    {
        var normalizedProposalId = NormalizeRequired(proposalId, nameof(proposalId));
        if (_scriptEvolutionDecisionReadPort != null)
            return await _scriptEvolutionDecisionReadPort.TryGetAsync(normalizedProposalId, ct);

        return await SendAsync<ScriptPromotionDecision>(
            HttpMethod.Get,
            $"/api/scripts/evolutions/{Uri.EscapeDataString(normalizedProposalId)}",
            body: null,
            ct,
            allowNotFound: true);
    }

    public async Task<AppScopeScriptSaveAcceptedResponse> SaveAsync(
        string scopeId,
        AppScopeScriptSaveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var scriptPackage = AppScriptPackagePayloads.ResolvePackage(request.Package, request.SourceText);
        var sourceText = NormalizeRequired(
            scriptPackage.GetPrimaryCSharpSource(),
            nameof(request.SourceText));
        var scriptId = StudioDocumentIdNormalizer.Normalize(request.ScriptId, "script");

        ScopeScriptUpsertResult upsertResult;
        if (_scriptCommandPort != null)
        {
            upsertResult = await _scriptCommandPort.UpsertAsync(
                new ScopeScriptUpsertRequest(
                    normalizedScopeId,
                    scriptId,
                    scriptPackage,
                    request.RevisionId,
                    request.ExpectedBaseRevision),
                ct);
        }
        else
        {
            upsertResult = await SendAsync<ScopeScriptUpsertResult>(
                HttpMethod.Put,
                $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/scripts/{Uri.EscapeDataString(scriptId)}",
                new RemoteUpsertRequest(
                    sourceText,
                    request.RevisionId,
                    request.ExpectedBaseRevision),
                ct) ?? throw new InvalidOperationException("Script save returned an empty response.");
        }

        // Refactor (iter348/cluster-002):
        //   Old pattern: AppScopedScriptService fires fire-and-forget storage upload after ACK
        //   New principle: actor projection save-observation is single source; no orphan chrono storage mirror
        return BuildAcceptedSaveResponse(sourceText, upsertResult);
    }

    public async Task<ScopeScriptSaveObservationResult> ObserveSaveAsync(
        string scopeId,
        string scriptId,
        AppScopeScriptSaveObservationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedScriptId = NormalizeRequired(scriptId, nameof(scriptId));
        var normalizedRevisionId = NormalizeRequired(request.RevisionId ?? string.Empty, nameof(request.RevisionId));
        var normalizedDefinitionActorId = NormalizeRequired(request.DefinitionActorId ?? string.Empty, nameof(request.DefinitionActorId));
        var normalizedSourceHash = NormalizeRequired(request.SourceHash ?? string.Empty, nameof(request.SourceHash));
        var remoteRequest = new ScopeScriptSaveObservationRequest(
            normalizedRevisionId,
            normalizedDefinitionActorId,
            normalizedSourceHash,
            request.ProposalId?.Trim() ?? string.Empty,
            request.ExpectedBaseRevision?.Trim() ?? string.Empty,
            request.AcceptedAt);

        if (_scriptSaveObservationPort != null)
            return await _scriptSaveObservationPort.ObserveAsync(normalizedScopeId, normalizedScriptId, remoteRequest, ct);

        return await SendAsync<ScopeScriptSaveObservationResult>(
                   HttpMethod.Post,
                   $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/scripts/{Uri.EscapeDataString(normalizedScriptId)}/save-observation",
                   remoteRequest,
                   ct) ??
               throw new InvalidOperationException("Script save observation returned an empty response.");
    }

    public async Task<ScriptPromotionDecision> ProposeEvolutionAsync(
        string scopeId,
        AppScopeScriptEvolutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var scriptId = NormalizeRequired(request.ScriptId ?? string.Empty, nameof(request.ScriptId));
        var candidateSource = NormalizeRequired(
            AppScriptPackagePayloads.ResolvePersistedSource(request.CandidatePackage, request.CandidateSource),
            nameof(request.CandidateSource));
        var candidateSourceHash = string.IsNullOrWhiteSpace(request.CandidateSourceHash)
            ? AppScriptPackagePayloads.ComputeSourceHash(request.CandidatePackage, candidateSource)
            : request.CandidateSourceHash.Trim();
        if (_scriptEvolutionService != null)
        {
            return await _scriptEvolutionService.ProposeAsync(
                new ProposeScriptEvolutionRequest(
                    ScriptId: scriptId,
                    BaseRevision: request.BaseRevision ?? string.Empty,
                    CandidateRevision: request.CandidateRevision ?? string.Empty,
                    CandidateSource: candidateSource,
                    CandidateSourceHash: candidateSourceHash,
                    Reason: request.Reason ?? string.Empty,
                    ProposalId: request.ProposalId ?? string.Empty,
                    ScopeId: normalizedScopeId),
                ct);
        }

        return await SendAsync<ScriptPromotionDecision>(
                   HttpMethod.Post,
                   $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/scripts/{Uri.EscapeDataString(scriptId)}/evolutions/proposals",
                   new RemoteProposeEvolutionRequest(
                       request.BaseRevision,
                       request.CandidateRevision,
                       candidateSource,
                       candidateSourceHash,
                       request.Reason,
                       request.ProposalId),
                   ct) ??
               throw new InvalidOperationException("Script evolution proposal returned an empty response.");
    }

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken ct,
        bool allowNotFound = false)
    {
        var client = _httpClientFactory.CreateClient(BackendClientName);
        using var request = new HttpRequestMessage(method, relativePath);
        if (body != null)
            request.Content = JsonContent.Create(body);

        using var response = await client.SendAsync(request, ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return default;

        if (!response.IsSuccessStatusCode)
            throw await BuildApiExceptionAsync(response, ct);

        if (response.Content == null)
            return default;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!IsJsonContentType(mediaType))
        {
            throw new AppApiException(
                StatusCodes.Status502BadGateway,
                AppApiErrors.BackendInvalidResponseCode,
                "Script backend returned a non-JSON response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        if (stream == Stream.Null)
            return default;

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            throw new AppApiException(
                StatusCodes.Status502BadGateway,
                AppApiErrors.BackendInvalidResponseCode,
                "Script backend returned invalid JSON.",
                innerException: ex);
        }
    }

    private static async Task<AppApiException> BuildApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var content = response.Content;
        var mediaType = response.Content?.Headers.ContentType?.MediaType;
        var redirectUrl = ResolveRedirectUrl(response);
        if (redirectUrl != null &&
            response.StatusCode is HttpStatusCode.Moved or
                HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect)
        {
            return new AppApiException(
                StatusCodes.Status401Unauthorized,
                AppApiErrors.BackendAuthRequiredCode,
                "Backend authentication required.",
                redirectUrl);
        }

        if (content == null)
        {
            return new AppApiException(
                (int)response.StatusCode,
                "SCRIPT_REQUEST_FAILED",
                $"Script request failed with status {(int)response.StatusCode}.",
                redirectUrl);
        }

        try
        {
            var payload = await content.ReadFromJsonAsync<RemoteErrorResponse>(JsonOptions, ct);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return new AppApiException(
                    (int)response.StatusCode,
                    string.IsNullOrWhiteSpace(payload.Code) ? "SCRIPT_REQUEST_FAILED" : payload.Code.Trim(),
                    payload.Message.Trim(),
                    redirectUrl);
            }
        }
        catch
        {
        }

        if (IsHtmlContentType(mediaType))
        {
            return new AppApiException(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status502BadGateway,
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? AppApiErrors.BackendAuthRequiredCode
                    : AppApiErrors.BackendInvalidResponseCode,
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Backend authentication required."
                    : "Script backend returned HTML for an API request.",
                redirectUrl);
        }

        return new AppApiException(
            (int)response.StatusCode,
            "SCRIPT_REQUEST_FAILED",
            $"Script request failed with status {(int)response.StatusCode}.",
            redirectUrl);
    }

    private static string NormalizeRequired(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static string? ResolveRedirectUrl(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location == null)
            return null;

        if (location.IsAbsoluteUri)
            return location.ToString();

        var requestUri = response.RequestMessage?.RequestUri;
        return requestUri == null
            ? location.ToString()
            : new Uri(requestUri, location).ToString();
    }

    private static bool IsJsonContentType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType) &&
        (mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private static bool IsHtmlContentType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType) &&
        (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
         mediaType.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    private static AppScopeScriptSaveAcceptedResponse BuildAcceptedSaveResponse(
        string sourceText,
        ScopeScriptUpsertResult upsertResult)
    {
        ArgumentNullException.ThrowIfNull(upsertResult);

        var submittedSource = new ScopeScriptSource(
            sourceText ?? string.Empty,
            upsertResult.DefinitionActorId,
            upsertResult.RevisionId,
            string.IsNullOrWhiteSpace(upsertResult.SourceHash)
                ? ComputeSha256(sourceText ?? string.Empty)
                : upsertResult.SourceHash);

        return new AppScopeScriptSaveAcceptedResponse(
            upsertResult.AcceptedScript,
            submittedSource,
            upsertResult.DefinitionCommand,
            upsertResult.CatalogCommand);
    }

    private static string ComputeSha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static AppScriptCatalogSnapshot ToCatalogSnapshot(
        ScriptCatalogEntrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AppScriptCatalogSnapshot(
            snapshot.ScriptId,
            snapshot.ActiveRevision,
            snapshot.ActiveDefinitionActorId,
            snapshot.ActiveSourceHash,
            snapshot.PreviousRevision,
            snapshot.RevisionHistory.ToArray(),
            snapshot.LastProposalId,
            snapshot.CatalogActorId,
            snapshot.ScopeId,
            snapshot.UpdatedAtUnixTimeMs <= 0
                ? DateTimeOffset.UnixEpoch
                : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.UpdatedAtUnixTimeMs));
    }

    private sealed record RemoteUpsertRequest(
        string SourceText,
        string? RevisionId,
        string? ExpectedBaseRevision);

    private sealed record RemoteProposeEvolutionRequest(
        string? BaseRevision,
        string? CandidateRevision,
        string? CandidateSource,
        string? CandidateSourceHash,
        string? Reason,
        string? ProposalId);

    private sealed record RemoteErrorResponse(string? Code, string? Message);
}

public sealed record AppScopeScriptSaveRequest(
    string? ScriptId,
    string? SourceText = null,
    string? RevisionId = null,
    string? ExpectedBaseRevision = null,
    AppScriptPackage? Package = null);

public sealed record AppScopeScriptSaveObservationRequest(
    string? RevisionId,
    string? DefinitionActorId,
    string? SourceHash,
    string? ProposalId,
    string? ExpectedBaseRevision,
    DateTimeOffset AcceptedAt);

public sealed record AppScopeScriptEvolutionRequest(
    string? ScriptId,
    string? BaseRevision,
    string? CandidateRevision,
    string? CandidateSource,
    string? CandidateSourceHash,
    string? Reason,
    string? ProposalId,
    AppScriptPackage? CandidatePackage = null);

public sealed record AppScopeScriptSaveAcceptedResponse(
    ScopeScriptAcceptedSummary AcceptedScript,
    ScopeScriptSource SubmittedSource,
    ScopeScriptCommandAcceptedHandle DefinitionCommand,
    ScopeScriptCommandAcceptedHandle CatalogCommand)
{
    public string ScopeId => AcceptedScript.ScopeId;
    public string ScriptId => AcceptedScript.ScriptId;
    public string RevisionId => AcceptedScript.RevisionId;
    public string CatalogActorId => AcceptedScript.CatalogActorId;
    public string DefinitionActorId => AcceptedScript.DefinitionActorId;
    public string SourceHash => AcceptedScript.SourceHash;
    public DateTimeOffset AcceptedAt => AcceptedScript.AcceptedAt;
    public string ProposalId => AcceptedScript.ProposalId;
    public string ExpectedBaseRevision => AcceptedScript.ExpectedBaseRevision;
}

public sealed record AppScriptCatalogSnapshot(
    string ScriptId,
    string ActiveRevision,
    string ActiveDefinitionActorId,
    string ActiveSourceHash,
    string PreviousRevision,
    IReadOnlyList<string> RevisionHistory,
    string LastProposalId,
    string CatalogActorId,
    string ScopeId,
    DateTimeOffset UpdatedAt);
