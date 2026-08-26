using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal sealed partial class NyxIdCodeExecutionPort(
    INyxIdApiClientFactory clientFactory,
    ILogger<NyxIdCodeExecutionPort> logger,
    TimeProvider? timeProvider = null) : ICodeExecutionPort, IDurableCodeExecutionPort
{
    private const long MaxResponseBytes = 1_048_576;
    private const string ExecutionPath = "/execute";
    private const string RequiredServiceSlug = CodeExecutionContract.ServiceSlug;
    private static readonly HashSet<string> AllowedCompletedFailureCodes =
        new(StringComparer.Ordinal)
        {
            "DEPENDENCY_INSTALL_FAILED",
            "EXECUTION_FAILED",
        };
    private static readonly HashSet<string> AllowedProxyFailureCodes =
        new(StringComparer.Ordinal)
        {
            "FORBIDDEN",
            "INTERNAL_ERROR",
            "INVALID_REQUEST",
            "SANDBOX_CREATION_FAILED",
            "SANDBOX_TIMEOUT",
            "SANDBOX_UNREACHABLE",
            "UNAUTHENTICATED",
        };

    private readonly INyxIdApiClientFactory _clientFactory =
        clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly ILogger<NyxIdCodeExecutionPort> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CodeExecutionOutcome> ExecuteAsync(
        CodeExecutionRequest request,
        CancellationToken ct = default)
    {
        if (request is null ||
            !Enum.IsDefined(request.Language) ||
            request.Language == CodeExecutionLanguage.Unspecified ||
            string.IsNullOrWhiteSpace(request.Source) ||
            !CodeExecutionContract.IsValidTimeoutSeconds(request.TimeoutSeconds) ||
            request.Route is null ||
            !IsValidExecutionCredentialKind(request.Caller?.ExecutionCredentialKind) ||
            !IsValidRequestedRoute(request.Route))
        {
            return Failed(
                CodeExecutionFailureKind.AdmissionDenied,
                "code_execution_request_invalid",
                "The code execution request is invalid.");
        }

        var executionCredential = NormalizeCredential(request.Caller?.ExecutionNyxIdCredential);
        if (executionCredential is null)
        {
            return Failed(
                CodeExecutionFailureKind.AdmissionDenied,
                "code_execution_credential_unavailable",
                "A typed NyxID execution credential is required for code execution.");
        }

        var localDiagnosticId = CreateLocalDiagnosticId();
        var sourceReadableBearerToken = NormalizeCredential(
            request.Caller?.SourceReadableNyxIdAccessToken);
        CodeExecutionRouteIdentity route;
        if (sourceReadableBearerToken is null)
        {
            if (!TryResolveExactAdmittedRoute(request.Route, out _))
            {
                return Failed(
                    CodeExecutionFailureKind.AdmissionDenied,
                    "code_execution_credential_unavailable",
                    "A source-readable NyxID credential or an exact workflow admission is required for code execution.",
                    localDiagnosticId);
            }

            route = request.Route;
        }
        else
        {
            NyxIdCodeExecutionRouteResolution resolution;
            try
            {
                resolution = await NyxIdCodeExecutionRouteResolver.ResolveAsync(
                        _clientFactory,
                        sourceReadableBearerToken,
                        request.Route.UserServiceId,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Code execution route resolution failed. diagnosticId={DiagnosticId} failureKind=source_exception exceptionType={ExceptionType}",
                    localDiagnosticId,
                    exception.GetType().Name);
                return Failed(
                    CodeExecutionFailureKind.TransportUnavailable,
                    "code_execution_route_resolution_failed",
                    "The code execution route could not be resolved.",
                    localDiagnosticId);
            }

            if (!resolution.IsReady)
            {
                if (resolution.SourceFailure?.Kind == NyxIdApiAccessFailureKind.Unauthorized &&
                    TryResolveExactAdmittedRoute(request.Route, out _))
                {
                    _logger.LogWarning(
                        "Code execution source revalidation was unauthorized; using the exact workflow admission and deferring final access control to NyxID. diagnosticId={DiagnosticId} failureKind=source_unauthorized_exact_admission",
                        localDiagnosticId);
                    route = request.Route;
                }
                else
                {
                    return RouteResolutionFailed(resolution, localDiagnosticId);
                }
            }
            else
            {
                var resolvedService = resolution.Service!;
                route = new CodeExecutionRouteIdentity(
                    resolvedService.Slug,
                    resolvedService.Id,
                    CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog);
            }
        }

        if (string.IsNullOrWhiteSpace(route.UserServiceId))
        {
            return Failed(
                CodeExecutionFailureKind.AdmissionDenied,
                "code_execution_admission_invalid",
                "The code execution route does not contain an exact UserService identity.",
                localDiagnosticId);
        }
        var resolvedUserServiceId = route.UserServiceId;

        var body = JsonSerializer.Serialize(new
        {
            language = SerializeLanguage(request.Language),
            script = request.Source,
            timeout_secs = request.TimeoutSeconds,
        });

        var client = _clientFactory.CreateClient();
        NyxIdProxyTextResponse response;
        try
        {
            // Agent Keys must remain bearer caller credentials here. NyxID authenticates
            // them and forwards the same Authorization value to Chrono on admitted routes.
            response = await client.ProxyRequestBoundedAsync(
                    executionCredential,
                    route.ServiceSlug,
                    resolvedUserServiceId,
                    ExecutionPath,
                    HttpMethod.Post.Method,
                    body,
                    extraHeaders: null,
                    MaxResponseBytes,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LogLocalProxyFailure("timeout", localDiagnosticId);
            return Failed(
                CodeExecutionFailureKind.TimedOut,
                "code_execution_timed_out",
                "Code execution timed out.",
                localDiagnosticId);
        }
        catch (Exception)
        {
            LogLocalProxyFailure("transport_exception", localDiagnosticId);
            return Failed(
                CodeExecutionFailureKind.TransportUnavailable,
                "code_execution_transport_unavailable",
                "The code execution transport is unavailable.",
                localDiagnosticId);
        }

        if (!response.Succeeded)
            return ClassifyProxyFailure(response, localDiagnosticId);

        return ParseResponse(response.Content, route, localDiagnosticId);
    }

    private static bool TryResolveExactAdmittedRoute(
        CodeExecutionRouteIdentity route,
        out string userServiceId)
    {
        userServiceId = route.UserServiceId ?? string.Empty;
        return route.Source == CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission &&
               !string.IsNullOrWhiteSpace(userServiceId) &&
               string.Equals(userServiceId, userServiceId.Trim(), StringComparison.Ordinal);
    }

    private static bool IsValidRequestedRoute(CodeExecutionRouteIdentity route) =>
        CodeExecutionContract.IsValidServiceSlug(route.ServiceSlug) &&
        route.Source switch
        {
            CodeExecutionRouteIdentitySource.CodeExecutionContract =>
                string.Equals(
                    route.ServiceSlug,
                    RequiredServiceSlug,
                    StringComparison.Ordinal) &&
                IsValidOptionalUserServiceId(route.UserServiceId),
            CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog =>
                IsValidExactUserServiceId(route.UserServiceId),
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission =>
                CodeExecutionContract.IsSupportedServiceSlug(route.ServiceSlug) &&
                IsValidExactUserServiceId(route.UserServiceId),
            _ => false,
        };

    private static bool IsValidOptionalUserServiceId(string? userServiceId) =>
        userServiceId is null || IsValidExactUserServiceId(userServiceId);

    private static bool IsValidExactUserServiceId(string? userServiceId) =>
        !string.IsNullOrWhiteSpace(userServiceId) &&
        string.Equals(userServiceId, userServiceId.Trim(), StringComparison.Ordinal) &&
        !userServiceId.Any(char.IsControl);

    private static string? NormalizeCredential(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim();
        if (string.Equals(normalized, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return normalized;
    }

    private static bool IsValidExecutionCredentialKind(
        CodeExecutionNyxIdCredentialKind? kind) =>
        kind is CodeExecutionNyxIdCredentialKind.Bearer or
            CodeExecutionNyxIdCredentialKind.AgentKey;

    private static string SerializeLanguage(CodeExecutionLanguage language) => language switch
    {
        CodeExecutionLanguage.Python => "python",
        CodeExecutionLanguage.JavaScript => "javascript",
        CodeExecutionLanguage.TypeScript => "typescript",
        CodeExecutionLanguage.Bash => "bash",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    private CodeExecutionOutcome RouteResolutionFailed(
        NyxIdCodeExecutionRouteResolution resolution,
        string diagnosticId)
    {
        _logger.LogWarning(
            "Code execution route rejected. diagnosticId={DiagnosticId} failureKind={FailureKind} canonicalCandidates={CanonicalCandidates} accessibleCandidates={AccessibleCandidates} activeCandidates={ActiveCandidates} eligibleCandidates={EligibleCandidates}",
            diagnosticId,
            resolution.Kind,
            resolution.CanonicalCandidateCount,
            resolution.AccessibleCandidateCount,
            resolution.ActiveCandidateCount,
            resolution.EligibleCandidateCount);
        return resolution.Kind switch
        {
            NyxIdCodeExecutionRouteResolutionKind.Missing => Failed(
                CodeExecutionFailureKind.TargetNotConfigured,
                "code_execution_route_missing",
                "The canonical code execution route is missing.",
                diagnosticId),
            NyxIdCodeExecutionRouteResolutionKind.Inactive => Failed(
                CodeExecutionFailureKind.TargetNotConfigured,
                "code_execution_route_inactive",
                "The canonical code execution route is inactive.",
                diagnosticId),
            NyxIdCodeExecutionRouteResolutionKind.PolicyMismatch => Failed(
                CodeExecutionFailureKind.TargetNotConfigured,
                "code_execution_route_policy_mismatch",
                "The canonical code execution route policy is incompatible.",
                diagnosticId),
            NyxIdCodeExecutionRouteResolutionKind.ExecutionNotReady => Failed(
                CodeExecutionFailureKind.TargetNotConfigured,
                "code_execution_route_not_ready",
                "The canonical code execution service is not executable for this caller.",
                diagnosticId),
            NyxIdCodeExecutionRouteResolutionKind.Ambiguous => Failed(
                CodeExecutionFailureKind.TargetNotConfigured,
                "code_execution_route_ambiguous",
                "Multiple canonical code execution routes are eligible.",
                diagnosticId),
            NyxIdCodeExecutionRouteResolutionKind.AccessDenied => Failed(
                CodeExecutionFailureKind.AdmissionDenied,
                "code_execution_route_access_denied",
                "The caller cannot access the canonical code execution route.",
                diagnosticId),
            _ => Failed(
                CodeExecutionFailureKind.TransportUnavailable,
                "code_execution_route_resolution_failed",
                "The code execution route could not be resolved.",
                diagnosticId),
        };
    }

    private CodeExecutionOutcome ClassifyProxyFailure(
        NyxIdProxyTextResponse response,
        string localDiagnosticId)
    {
        if (response.Detail is "content_length_exceeds_max_bytes" or "content_exceeds_max_bytes")
        {
            LogLocalProxyFailure(
                "response_too_large",
                localDiagnosticId,
                response.HttpStatus,
                "oversized_unread");
            return Failed(
                CodeExecutionFailureKind.ResponseTooLarge,
                "code_execution_response_too_large",
                "Code execution returned an oversized response.",
                localDiagnosticId);
        }

        var inspection = ChronoProxyFailureInspector.Inspect(
            response.Content,
            AllowedProxyFailureCodes);
        var diagnosticId = inspection.DiagnosticId ?? localDiagnosticId;
        _logger.LogWarning(
            "Code execution proxy failure. status={Status} bodyBytes={BodyBytes} bodyShape={BodyShape} upstreamCodeResolved={Resolved} diagnosticId={DiagnosticId} diagnosticSource={DiagnosticSource} failureKind={FailureKind}",
            response.HttpStatus,
            inspection.BodyBytes,
            inspection.BodyShape,
            inspection.UpstreamCode is not null,
            diagnosticId,
            inspection.DiagnosticId is null ? "aevatar" : "upstream",
            "http_failure");
        if (inspection.UpstreamCode is { } upstreamCode)
        {
            var (kind, message) = upstreamCode switch
            {
                "UNAUTHENTICATED" => (
                    CodeExecutionFailureKind.AdmissionDenied,
                    "The code execution credential was rejected upstream."),
                "FORBIDDEN" => (
                    CodeExecutionFailureKind.AdmissionDenied,
                    "Code execution was denied upstream."),
                "INVALID_REQUEST" => (
                    CodeExecutionFailureKind.AdmissionDenied,
                    "The code execution request was rejected upstream."),
                "SANDBOX_TIMEOUT" => (
                    CodeExecutionFailureKind.TimedOut,
                    "Code execution timed out upstream."),
                "SANDBOX_CREATION_FAILED" or "SANDBOX_UNREACHABLE" => (
                    CodeExecutionFailureKind.TransportUnavailable,
                    "The code execution sandbox is unavailable upstream."),
                _ => (
                    CodeExecutionFailureKind.TransportUnavailable,
                    "Code execution failed upstream."),
            };
            return Failed(
                kind,
                upstreamCode,
                message,
                diagnosticId);
        }

        return response.HttpStatus switch
        {
            401 => Failed(
                CodeExecutionFailureKind.AdmissionDenied,
                "NYXID_PROXY_UNAUTHORIZED",
                "The NyxID proxy rejected the code execution credential.",
                diagnosticId),
            403 => Failed(
                CodeExecutionFailureKind.AdmissionDenied,
                "NYXID_PROXY_FORBIDDEN",
                "The NyxID proxy denied code execution.",
                diagnosticId),
            404 => Failed(
                CodeExecutionFailureKind.TargetNotConfigured,
                "NYXID_PROXY_HTTP_404",
                "The code execution target is unavailable.",
                diagnosticId),
            408 or 504 => Failed(
                CodeExecutionFailureKind.TimedOut,
                "code_execution_timed_out",
                "Code execution timed out.",
                diagnosticId),
            > 0 => Failed(
                CodeExecutionFailureKind.TransportUnavailable,
                $"NYXID_PROXY_HTTP_{response.HttpStatus}",
                "The code execution proxy request failed.",
                diagnosticId),
            _ => Failed(
                CodeExecutionFailureKind.TransportUnavailable,
                "code_execution_transport_unavailable",
                "The code execution transport is unavailable.",
                diagnosticId),
        };
    }

    private CodeExecutionOutcome ParseResponse(
        string response,
        CodeExecutionRouteIdentity route,
        string localDiagnosticId)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Object ||
                !ReadRequiredString(output, "stdout", out var stdout) ||
                !ReadRequiredString(output, "stderr", out var stderr) ||
                !output.TryGetProperty("exit_code", out var exitCodeElement) ||
                !exitCodeElement.TryGetInt32(out var exitCode))
            {
                return InvalidResponse(response, localDiagnosticId);
            }

            var elapsed = output.TryGetProperty("execution_time_ms", out var elapsedElement) &&
                          elapsedElement.TryGetInt64(out var elapsedValue)
                ? elapsedValue
                : (long?)null;
            var succeeded = success.GetBoolean();
            JsonElement error = default;
            if (succeeded)
            {
                if (exitCode != 0 || root.TryGetProperty("error", out _))
                    return InvalidResponse(response, localDiagnosticId);
            }
            else if (exitCode == 0 ||
                     !root.TryGetProperty("error", out error) ||
                     error.ValueKind != JsonValueKind.Object)
            {
                return InvalidResponse(response, localDiagnosticId);
            }

            var upstreamDiagnosticId = ChronoProxyFailureInspector.SanitizeDiagnosticId(
                ReadString(root, "diagnostic_id"));
            var diagnosticId = upstreamDiagnosticId ?? localDiagnosticId;
            if (upstreamDiagnosticId is null)
            {
                LogResponseDiagnosticFallback(response, localDiagnosticId);
            }
            var result = new CodeExecutionResult(stdout, stderr, exitCode, diagnosticId, elapsed);

            if (succeeded)
                return CodeExecutionOutcome.Succeeded(result, route);

            var upstreamCode = ReadString(error, "code");
            var code = upstreamCode is not null && AllowedCompletedFailureCodes.Contains(upstreamCode)
                ? upstreamCode
                : "code_execution_failed";
            var message = code switch
            {
                "DEPENDENCY_INSTALL_FAILED" => "Code execution dependency preparation failed.",
                "EXECUTION_FAILED" => "Code execution exited unsuccessfully.",
                _ => "Code execution failed.",
            };
            return CodeExecutionOutcome.CompletedWithFailure(
                result,
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.ExecutionFailed,
                    code,
                    message,
                    diagnosticId),
                route);
        }
        catch (JsonException)
        {
            return InvalidResponse(response, localDiagnosticId);
        }
    }

    private CodeExecutionOutcome InvalidResponse(string response, string localDiagnosticId)
    {
        LogLocalProxyFailure(
            "response_invalid",
            localDiagnosticId,
            status: 200,
            bodyShape: ChronoProxyFailureInspector.ClassifyBodyShape(response),
            bodyBytes: BodyBytes(response));
        return Failed(
            CodeExecutionFailureKind.MalformedOutput,
            "code_execution_response_invalid",
            "Code execution returned an invalid response.",
            localDiagnosticId);
    }

    private void LogResponseDiagnosticFallback(string response, string localDiagnosticId) =>
        LogLocalProxyFailure(
            "diagnostic_missing_or_invalid",
            localDiagnosticId,
            status: 200,
            bodyShape: ChronoProxyFailureInspector.ClassifyBodyShape(response),
            bodyBytes: BodyBytes(response));

    private void LogLocalProxyFailure(
        string failureKind,
        string diagnosticId,
        int status = 0,
        string bodyShape = "empty",
        int bodyBytes = 0) =>
        _logger.LogWarning(
            "Code execution proxy failure. status={Status} bodyBytes={BodyBytes} bodyShape={BodyShape} upstreamCodeResolved=false diagnosticId={DiagnosticId} diagnosticSource=aevatar failureKind={FailureKind}",
            status,
            bodyBytes,
            bodyShape,
            diagnosticId,
            failureKind);

    private static int BodyBytes(string? response) =>
        string.IsNullOrEmpty(response) ? 0 : Encoding.UTF8.GetByteCount(response);

    private static string CreateLocalDiagnosticId() => $"aevatar-{Guid.NewGuid():N}";

    private static CodeExecutionOutcome Failed(
        CodeExecutionFailureKind kind,
        string code,
        string message,
        string? diagnosticId = null) =>
        CodeExecutionOutcome.Failed(new CodeExecutionFailure(kind, code, message, diagnosticId));

    private static string? ReadString(JsonElement owner, params string[] names)
    {
        foreach (var name in names)
        {
            if (owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        return null;
    }

    private static bool ReadRequiredString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }

}
