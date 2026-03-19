using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Tools.Cli.Hosting;

public sealed class AppScopedScriptService
{
    private const string BackendClientName = "AppBridgeBackend";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IScopeScriptQueryPort? _scriptQueryPort;
    private readonly IScopeScriptCommandPort? _scriptCommandPort;
    private readonly IScriptDefinitionSnapshotPort? _definitionSnapshotPort;
    private readonly IScriptEvolutionApplicationService? _scriptEvolutionService;
    private readonly IHttpClientFactory _httpClientFactory;

    public AppScopedScriptService(
        IHttpClientFactory httpClientFactory,
        IScopeScriptQueryPort? scriptQueryPort = null,
        IScopeScriptCommandPort? scriptCommandPort = null,
        IScriptDefinitionSnapshotPort? definitionSnapshotPort = null,
        IScriptEvolutionApplicationService? scriptEvolutionService = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _scriptQueryPort = scriptQueryPort;
        _scriptCommandPort = scriptCommandPort;
        _definitionSnapshotPort = definitionSnapshotPort;
        _scriptEvolutionService = scriptEvolutionService;
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

    public async Task<ScopeScriptDetail> SaveAsync(
        string scopeId,
        AppScopeScriptSaveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var sourceText = NormalizeRequired(request.SourceText, nameof(request.SourceText));
        var scriptId = string.IsNullOrWhiteSpace(request.ScriptId)
            ? AppStudioEndpoints.NormalizeStudioDocumentId(request.ScriptId, "script")
            : NormalizeRequired(request.ScriptId, nameof(request.ScriptId));

        if (_scriptCommandPort != null)
        {
            await _scriptCommandPort.UpsertAsync(
                new ScopeScriptUpsertRequest(
                    normalizedScopeId,
                    scriptId,
                    sourceText,
                    request.RevisionId,
                    request.ExpectedBaseRevision),
                ct);
        }
        else
        {
            _ = await SendAsync<ScopeScriptUpsertResult>(
                HttpMethod.Put,
                $"/api/scopes/{Uri.EscapeDataString(normalizedScopeId)}/scripts/{Uri.EscapeDataString(scriptId)}",
                new RemoteUpsertRequest(
                    sourceText,
                    request.RevisionId,
                    request.ExpectedBaseRevision),
                ct) ?? throw new InvalidOperationException("Script save returned an empty response.");
        }

        return await GetAsync(normalizedScopeId, scriptId, ct)
               ?? throw new InvalidOperationException(
                   $"Script '{scriptId}' was saved but could not be read back for scope '{normalizedScopeId}'.");
    }

    public async Task<ScriptPromotionDecision> ProposeEvolutionAsync(
        string scopeId,
        AppScopeScriptEvolutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var scriptId = NormalizeRequired(request.ScriptId ?? string.Empty, nameof(request.ScriptId));
        if (_scriptEvolutionService != null)
        {
            return await _scriptEvolutionService.ProposeAsync(
                new ProposeScriptEvolutionRequest(
                    ScriptId: scriptId,
                    BaseRevision: request.BaseRevision ?? string.Empty,
                    CandidateRevision: request.CandidateRevision ?? string.Empty,
                    CandidateSource: request.CandidateSource ?? string.Empty,
                    CandidateSourceHash: request.CandidateSourceHash ?? string.Empty,
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
                       request.CandidateSource,
                       request.CandidateSourceHash,
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
    string SourceText,
    string? RevisionId = null,
    string? ExpectedBaseRevision = null);

public sealed record AppScopeScriptEvolutionRequest(
    string? ScriptId,
    string? BaseRevision,
    string? CandidateRevision,
    string? CandidateSource,
    string? CandidateSourceHash,
    string? Reason,
    string? ProposalId);
