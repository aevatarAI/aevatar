using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.Observability;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to make proxied requests to downstream services through NyxID.</summary>
public sealed class NyxIdProxyTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string TextResponseMode = "text";
    private const string FileArtifactResponseMode = "file_artifact";
    private const string ServiceIdRequiredErrorCode = "NYXID_PROXY_SERVICE_ID_REQUIRED";
    private const string ServiceIdRequiredErrorMessage = "'service_id' is required when 'slug' is provided";
    private const string ServiceIdRequiredResult = """{"error":"'service_id' is required when 'slug' is provided"}""";
    private const string OperationAdmissionRequiredErrorCode = "NYXID_OPERATION_ADMISSION_REQUIRED";
    private const string OperationAdmissionRequiredErrorMessage =
        "Managed workflow NyxID proxy calls require an admitted operation proof.";
    private const string OperationAdmissionRequiredResult =
        """{"error":true,"error_code":"NYXID_OPERATION_ADMISSION_REQUIRED","message":"Managed workflow NyxID proxy calls require an admitted operation proof."}""";
    private const string AccessTokenMissingErrorCode = "NYXID_ACCESS_TOKEN_MISSING";
    private const string AccessTokenMissingErrorMessage =
        "No NyxID access token available. User must be authenticated.";
    private const string ResponseTooLargeErrorCode = "NYXID_PROXY_RESPONSE_TOO_LARGE";
    private const string ResponseTooLargeErrorMessage =
        "The admitted proxy response exceeded the configured text response limit.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;
    private readonly INyxIdProxyFileArtifactIngress? _fileArtifactIngress;
    private readonly long _fileArtifactMaxBytes;
    private readonly NyxIdManagedWorkflowAdmissionMode _managedWorkflowAdmissionMode;

    public NyxIdProxyTool(
        NyxIdApiClient client,
        ILogger? logger = null,
        INyxIdProxyFileArtifactIngress? fileArtifactIngress = null,
        long fileArtifactMaxBytes = NyxIdToolOptions.DefaultProxyFileArtifactMaxBytes,
        NyxIdManagedWorkflowAdmissionMode managedWorkflowAdmissionMode =
            NyxIdManagedWorkflowAdmissionMode.Shadow)
    {
        _client = client;
        _logger = logger ?? NullLogger.Instance;
        _fileArtifactIngress = fileArtifactIngress;
        _fileArtifactMaxBytes = NormalizeMaxBytes(fileArtifactMaxBytes);
        _managedWorkflowAdmissionMode = managedWorkflowAdmissionMode;
    }

    public string Name => "nyxid_proxy";

    public IReadOnlyCollection<string> Capabilities =>
        [AgentToolCapabilities.ExcludeFromNyxIdChat];

    public string Description =>
        "Make HTTP requests to downstream services through NyxID's credential-injecting proxy. " +
        "Admitted workflow calls provide only path_params, query, headers, body, and response_mode; " +
        "the committed proof supplies service, method, path template, and schemas. Ordinary human " +
        "calls use service_id + slug + path after typed capability discovery.";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public AgentToolCallSafety GetCallSafety(string argumentsJson)
    {
        var context = AgentToolRequestContext.Current;
        var policy = context?.OperationAdmission?.ExecutionPolicy;
        if (!IsValidExecutionPolicy(policy))
            return new AgentToolCallSafety(null, false, false);

        return new AgentToolCallSafety(
            policy!.Approval == AgentToolOperationApproval.Required,
            policy!.Risk == AgentToolOperationRisk.ReadOnly,
            policy.Risk == AgentToolOperationRisk.Destructive);
    }

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        if (string.Equals(resultJson, OperationAdmissionRequiredResult, StringComparison.Ordinal))
        {
            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = OperationAdmissionRequiredErrorCode,
                ErrorMessage = OperationAdmissionRequiredErrorMessage,
                ResultJson = resultJson,
            };
        }

        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return null;

        if (string.IsNullOrWhiteSpace(args.Str("service_id")) &&
            !string.IsNullOrWhiteSpace(args.Str("slug") ?? args.Str("service")) &&
            string.Equals(resultJson, ServiceIdRequiredResult, StringComparison.Ordinal))
        {
            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = ServiceIdRequiredErrorCode,
                ErrorMessage = ServiceIdRequiredErrorMessage,
                ResultJson = resultJson,
            };
        }

        return NyxIdProxyReceiptFactory.TryCreate(
            callId,
            toolName,
            args.Str("slug") ?? args.Str("service") ?? string.Empty,
            args.Str("service_id"),
            serviceLabel: null,
            args.Str("path"),
            resultJson);
    }

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "slug": {
              "type": "string",
              "description": "Admitted service slug routing snapshot. Omit with service_id to list caller-visible service instances."
            },
            "service_id": {
              "type": "string",
              "description": "Exact NyxID UserService.id selected from typed capability discovery. Required with slug."
            },
            "path": {
              "type": "string",
              "description": "Ordinary human raw-proxy path. Never provide this field for a proof-bound workflow call."
            },
            "method": {
              "type": "string",
              "enum": ["GET", "POST", "PUT", "PATCH", "DELETE"],
              "description": "HTTP method (default: GET)"
            },
            "body": {
              "description": "Proof-bound JSON request body, or an ordinary raw-proxy JSON string."
            },
            "path_params": {
              "type": "object",
              "additionalProperties": true,
              "description": "Proof-bound path parameter values. Names and scalar types come from the committed operation proof."
            },
            "query": {
              "type": "object",
              "additionalProperties": true,
              "description": "Proof-bound query parameter values. Names and scalar types come from the committed operation proof."
            },
            "headers": {
              "type": "object",
              "additionalProperties": true,
              "description": "Non-sensitive operation headers. Authorization, cookies, API keys, and tokens are forbidden; NyxID injects credentials."
            },
            "response_mode": {
              "type": "string",
              "enum": ["text", "file_artifact"],
              "description": "Response handling mode. Omit or use text for the existing JSON/string response. Use file_artifact only for GET binary downloads in a managed workflow run."
            }
          },
          "additionalProperties": false
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        (await ExecuteWithOutcomeAsync(string.Empty, Name, argumentsJson, ct)).ResultJson;

    public async Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default) =>
        await ExecuteWithOutcomeCoreAsync(
            callId,
            toolName,
            argumentsJson,
            maxTextResponseBytes: null,
            ct);

    internal async Task<AgentToolTerminalOutcome> ExecuteAdmittedReadWithOutcomeAsync(
        string callId,
        string toolName,
        string argumentsJson,
        long maxTextResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTextResponseBytes);
        var admission = AgentToolRequestContext.Current?.OperationAdmission;
        if (admission?.ExecutionPolicy.Risk != AgentToolOperationRisk.ReadOnly)
        {
            throw new InvalidOperationException(
                "Bounded admitted read execution requires an exact read-only operation admission.");
        }

        return await ExecuteWithOutcomeCoreAsync(
            callId,
            toolName,
            argumentsJson,
            maxTextResponseBytes,
            ct);
    }

    internal async Task<AgentToolTerminalOutcome> ExecuteAdmittedEffectWithOutcomeAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var admission = AgentToolRequestContext.Current?.OperationAdmission;
        if (admission?.ExecutionPolicy.Risk != AgentToolOperationRisk.Write ||
            admission.ExecutionPolicy.Approval is not
                (AgentToolOperationApproval.None or AgentToolOperationApproval.Required))
        {
            throw new InvalidOperationException(
                "Admitted effect execution requires an exact admitted write operation admission.");
        }

        return await ExecuteWithOutcomeCoreAsync(
            callId,
            toolName,
            argumentsJson,
            maxTextResponseBytes: null,
            ct);
    }

    private async Task<AgentToolTerminalOutcome> ExecuteWithOutcomeCoreAsync(
        string callId,
        string toolName,
        string argumentsJson,
        long? maxTextResponseBytes,
        CancellationToken ct)
    {
        var context = AgentToolRequestContext.Current;
        var managed = context?.WorkflowRuntime.HasManagedParent == true;
        var proofPresent = context?.OperationAdmission is not null;
        var policy = context?.OperationAdmission?.ExecutionPolicy;
        var validPolicy = IsValidExecutionPolicy(policy);
        var wouldBlock = managed && (!proofPresent || !validPolicy);
        NyxIdProxyAdmissionTelemetry.Record(
            _managedWorkflowAdmissionMode,
            managed,
            proofPresent,
            context?.InvocationSurface ?? AgentToolInvocationSurface.Unspecified,
            validPolicy ? policy!.Risk : AgentToolOperationRisk.Unspecified,
            validPolicy && policy!.Approval == AgentToolOperationApproval.Required,
            wouldBlock);
        if (wouldBlock && _managedWorkflowAdmissionMode == NyxIdManagedWorkflowAdmissionMode.Enforce)
            return new AgentToolTerminalOutcome(OperationAdmissionRequiredResult);

        return context?.OperationAdmission is { } admission
            ? await ExecuteAdmittedOperationAsync(
                admission,
                callId,
                toolName,
                argumentsJson,
                maxTextResponseBytes,
                ct)
            : new AgentToolTerminalOutcome(await ExecuteCoreAsync(argumentsJson, ct));
    }

    private static bool IsValidExecutionPolicy(AgentToolOperationExecutionPolicy? policy)
    {
        if (policy is null ||
            policy.EnforcementOwner != AgentToolOperationEnforcementOwner.Aevatar ||
            policy.AllowedExecutionModes.Count == 0 ||
            !policy.AllowedExecutionModes.Contains(AgentToolOperationExecutionMode.Interactive) ||
            policy.AllowedExecutionModes.Any(static mode =>
                mode is not (AgentToolOperationExecutionMode.Interactive or
                    AgentToolOperationExecutionMode.Durable)) ||
            policy.AllowedExecutionModes.Distinct().Count() != policy.AllowedExecutionModes.Count)
        {
            return false;
        }

        return policy.Risk switch
        {
            AgentToolOperationRisk.ReadOnly =>
                policy.Approval == AgentToolOperationApproval.None,
            AgentToolOperationRisk.Write or AgentToolOperationRisk.Destructive =>
                policy.Approval is AgentToolOperationApproval.None or AgentToolOperationApproval.Required,
            _ => false,
        };
    }

    private async Task<string> ExecuteCoreAsync(
        string argumentsJson,
        CancellationToken ct)
    {
        // Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache):
        //   Old pattern: NyxIdSpecCatalog + SpecFetchToken + IServiceDiscoveryCache 在仓库内建第二 catalog(NyxID 真实源的影子)
        //   New principle: NyxID 是唯一真实源;删除 in-process catalog 假权威面; routing 和 spec hints 请求时读取 live NyxID surface;保留 typed tools + live nyxid_proxy
        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
        {
            _logger.LogWarning("[nyxid_proxy] Argument parse failed");
            return """{"error":"Failed to parse tool arguments"}""";
        }

        var responseMode = ResolveResponseMode(args.Str("response_mode"));
        if (responseMode == null)
            return FileArtifactError("invalid_response_mode", "response_mode must be omitted, text, or file_artifact.");

        var token = AgentToolRequestContext.NyxIdAccessToken;
        var orgToken = AgentToolRequestContext.NyxIdOrgToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return responseMode == FileArtifactResponseMode
                ? FileArtifactError("missing_nyxid_access_token", "No NyxID access token available. User must be authenticated.")
                : """{"error":"No NyxID access token available. User must be authenticated."}""";
        }

        var slug = args.Str("slug") ?? args.Str("service");
        var serviceId = args.Str("service_id");
        var path = args.Str("path");
        var method = args.Str("method", "GET");
        var body = args.RawOrStr("body");
        var headers = args.Headers();

        if (string.IsNullOrWhiteSpace(slug))
        {
            if (!string.IsNullOrWhiteSpace(serviceId))
                return """{"error":"'slug' is required when 'service_id' is provided"}""";
            if (responseMode == FileArtifactResponseMode)
                return FileArtifactError("file_artifact_requires_slug", "response_mode=file_artifact requires slug.");

            return """{"error":"'service_id' and 'slug' are required; select an exact service instance through typed capability discovery"}""";
        }

        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return responseMode == FileArtifactResponseMode
                ? FileArtifactError("file_artifact_requires_service_id", "response_mode=file_artifact requires exact service_id.")
                : ServiceIdRequiredResult;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("[nyxid_proxy] Missing path. slug={Slug}", slug);
            if (responseMode == FileArtifactResponseMode)
                return FileArtifactError("file_artifact_requires_path", "response_mode=file_artifact requires path.");

            return """{"error":"'path' is required when 'slug' is provided"}""";
        }

        var sensitiveHeader = headers?.Keys.FirstOrDefault(NyxIdProxyHeaderPolicy.IsSensitive);
        if (sensitiveHeader != null)
        {
            return responseMode == FileArtifactResponseMode
                ? FileArtifactError("sensitive_header_forbidden", $"nyxid_proxy sensitive header '{sensitiveHeader}' cannot be supplied.")
                : $"{{\"error\":\"nyxid_proxy sensitive header '{JsonEncodedText.Encode(sensitiveHeader)}' cannot be supplied\"}}";
        }

        if (responseMode == FileArtifactResponseMode)
        {
            return await ExecuteFileArtifactAsync(
                token,
                orgToken,
                slug,
                serviceId,
                path,
                method,
                args,
                body,
                headers,
                ct);
        }

        // Resolve which token owns the target service: user token first, fallback to org token
        var effectiveToken = await ResolveTokenForServiceAsync(token, orgToken, serviceId, ct);

        _logger.LogInformation("[nyxid_proxy] {Method} slug={Slug} tokenSource={Source}",
            method, slug, effectiveToken == token ? "user" : "org");
        var result = await _client.ProxyRequestAsync(
            effectiveToken,
            slug,
            serviceId,
            path,
            method,
            body,
            headers,
            ct);

        if (IsApprovalError(result, out var approvalCode, out var approvalRequestId))
        {
            _logger.LogInformation(
                "[nyxid_proxy] Approval response: code={Code} requestId={RequestId}",
                approvalCode, approvalRequestId);
        }

        return result;
    }

    /// <summary>
    /// Strict proof-bound main chain. The committed operation proof owns service, method, template
    /// and schemas, so this path never accepts caller route fields and never issues an HTTP request
    /// before the whole request has been validated against the proof.
    /// </summary>
    private async Task<AgentToolTerminalOutcome> ExecuteAdmittedOperationAsync(
        AgentToolOperationAdmission admission,
        string callId,
        string toolName,
        string argumentsJson,
        long? maxTextResponseBytes,
        CancellationToken ct)
    {
        var build = NyxIdAdmittedRequestBuilder.Build(admission, argumentsJson);
        if (!build.Succeeded)
        {
            var failure = build.Failure!;
            _logger.LogWarning(
                "[nyxid_proxy] Admitted request rejected. identity={Identity} code={Code}",
                FormatAdmissionIdentity(admission.Identity),
                failure.Code);
            return CreateAdmittedFailureOutcome(
                admission,
                callId,
                toolName,
                failure.Code,
                failure.Message);
        }

        var request = build.Request!;
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return CreateAdmittedFailureOutcome(
                admission,
                callId,
                toolName,
                AccessTokenMissingErrorCode,
                AccessTokenMissingErrorMessage);
        }

        // Admission reads the catalog through the source-readable caller authority. Revalidate
        // against that same view while retaining the delegation token for the exact proxy request.
        var authorityToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                                 AgentToolRequestContext.Current?.Credentials) ?? token;
        var revalidationFailure = await RevalidateAdmittedOperationAsync(admission, authorityToken, ct);
        if (revalidationFailure is not null)
        {
            return CreateAdmittedFailureOutcome(
                admission,
                callId,
                toolName,
                revalidationFailure.Code,
                revalidationFailure.Message);
        }

        _logger.LogInformation(
            "[nyxid_proxy] admitted {Method} slug={Slug} identity={Identity}",
            request.Method,
            request.Slug,
            FormatAdmissionIdentity(admission.Identity));

        if (request.FileArtifact)
        {
            return await ExecuteAdmittedFileArtifactAsync(
                token,
                request,
                callId,
                toolName,
                admission.Identity is AgentToolOperationIdentity.AuthoredRequest
                    ? admission.ServiceInstanceId
                    : null,
                ct);
        }

        var response = maxTextResponseBytes.HasValue
            ? await _client.ProxyRequestBoundedAsync(
                token,
                request.Slug,
                request.ServiceId,
                request.Path,
                request.Method,
                request.Body,
                request.Headers,
                maxTextResponseBytes.Value,
                ct)
            : await _client.ProxyRequestResponseAsync(
                token,
                request.Slug,
                request.ServiceId,
                request.Path,
                request.Method,
                request.Body,
                request.Headers,
                ct);
        if (!response.Succeeded &&
            response.Detail is "content_length_exceeds_max_bytes" or "content_exceeds_max_bytes")
        {
            return CreateAdmittedFailureOutcome(
                admission,
                callId,
                toolName,
                ResponseTooLargeErrorCode,
                ResponseTooLargeErrorMessage);
        }

        var authorityFailure = !response.Succeeded
            ? MapExactRouteFailure(response.Content, admission.ServiceInstanceId, request.Slug)
            : null;
        var result = authorityFailure is { } authorityError
            ? AdmissionDriftError(authorityError.ErrorCode, authorityError.ErrorMessage)
            : response.Content;
        var receipt = response.Succeeded
            ? NyxIdProxyReceiptFactory.TryCreate(
                callId,
                toolName,
                request.Slug,
                request.ServiceId,
                serviceLabel: null,
                request.Path,
                result,
                proxyRequestFailed: false)
            : authorityFailure is { } exactRouteFailure
                ? NyxIdProxyReceiptFactory.CreateError(
                    callId,
                    toolName,
                    request.ServiceId,
                    exactRouteFailure.ErrorCode,
                    exactRouteFailure.ErrorMessage,
                    result)
            : NyxIdProxyReceiptFactory.TryCreate(
                callId,
                toolName,
                request.Slug,
                request.ServiceId,
                serviceLabel: null,
                request.Path,
                response.Content,
                proxyRequestFailed: true);
        if (IsExactApprovalFailedReceipt(receipt))
        {
            receipt!.NyxIdApprovalTerminalOutcome = await ReadApprovalTerminalOutcomeAsync(
                token,
                receipt.ApprovalRequestId,
                ct);
        }
        return new AgentToolTerminalOutcome(result, receipt);
    }

    private static bool IsExactApprovalFailedReceipt(AgentToolReceipt? receipt) =>
        receipt is
        {
            Status: AgentToolReceiptStatus.Denied,
            ApprovalRequestId.Length: > 0,
        } &&
        string.Equals(
            receipt.ErrorCode,
            "NYXID_APPROVAL_FAILED",
            StringComparison.Ordinal);

    private async Task<NyxIdApprovalTerminalOutcome> ReadApprovalTerminalOutcomeAsync(
        string requesterToken,
        string approvalRequestId,
        CancellationToken ct)
    {
        try
        {
            var response = await _client.GetApprovalStatusAsync(
                requesterToken,
                approvalRequestId,
                ct);
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String)
            {
                return NyxIdApprovalTerminalOutcome.Unspecified;
            }

            return status.GetString() switch
            {
                "rejected" => NyxIdApprovalTerminalOutcome.Rejected,
                "expired" => NyxIdApprovalTerminalOutcome.Expired,
                "pending" => NyxIdApprovalTerminalOutcome.TimedOut,
                _ => NyxIdApprovalTerminalOutcome.Unspecified,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "[nyxid_proxy] Approval status readback failed. requestId={RequestId}",
                approvalRequestId);
            return NyxIdApprovalTerminalOutcome.Unspecified;
        }
    }

    private async Task<NyxIdOperationRequestFailure?> RevalidateAdmittedOperationAsync(
        AgentToolOperationAdmission admission,
        string token,
        CancellationToken ct)
    {
        if (admission.Identity is AgentToolOperationIdentity.AuthoredRequest)
        {
            // The committed explicit-request proof was validated before tool dispatch. Runtime
            // authority is deliberately enforced by NyxID's exact route, not a catalog read.
            return null;
        }

        if (admission.Identity is not AgentToolOperationIdentity.PublishedEndpoint publishedEndpoint)
        {
            return new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_IDENTITY_NOT_SUPPORTED",
                "The admitted request identity is not supported by published endpoint revalidation.");
        }

        var catalog = NyxIdMcpOperationCatalog.Parse(
            await _client.GetMcpConfigAsync(token, ct),
            "runtime",
            TimeProvider.System.GetUtcNow(),
            TimeSpan.FromMinutes(5));
        if (!string.IsNullOrWhiteSpace(admission.CatalogDigest) &&
            !string.Equals(
                catalog.Source.ContentDigest,
                admission.CatalogDigest,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[nyxid_proxy] Root catalog revision changed; continuing exact service and endpoint revalidation.");
        }
        var service = catalog.Services.SingleOrDefault(candidate =>
            string.Equals(candidate.UserServiceId, admission.ServiceInstanceId, StringComparison.Ordinal));
        if (service is null)
        {
            // A source catalog and the execution delegation can expose different visibility
            // windows for the same exact UserService. A proxy-only delegation is also expected
            // to be denied access to NyxID's management catalog. Read-only proofs may let
            // NyxID's exact route make the final live authority decision; writes and actual
            // catalog outages remain fail-closed here.
            if (!catalog.SourceUnavailable &&
                CanUseExactReadOnlyAuthority(admission))
                return null;

            return new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_AUTHORITY_DRIFT",
                "The live NyxID service authority no longer matches the admitted operation.");
        }

        if (!string.Equals(service.ServiceSlug, admission.ServiceSlug, StringComparison.Ordinal))
        {
            return new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_AUTHORITY_DRIFT",
                "The live NyxID service authority no longer matches the admitted operation.");
        }

        var endpoint = service.Endpoints.SingleOrDefault(candidate =>
            string.Equals(
                candidate.EndpointId,
                publishedEndpoint.EndpointId,
                StringComparison.Ordinal));
        if (endpoint is null ||
            !string.Equals(endpoint.ContractDigest, admission.ContractDigest, StringComparison.Ordinal))
        {
            return new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_CONTRACT_DRIFT",
                "The live NyxID endpoint no longer matches the admitted contract.");
        }

        return null;
    }

    private static bool CanUseExactReadOnlyAuthority(AgentToolOperationAdmission admission) =>
        admission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly &&
        admission.HttpMethod is "GET" or "HEAD" or "OPTIONS";

    private static (string ErrorCode, string ErrorMessage)? MapExactRouteFailure(
        string response,
        string serviceInstanceId,
        string serviceSlug)
    {
        if (!TryReadNyxIdTextProxyFailure(response, out var httpStatus, out var error, out var errorCode, out var message))
            return null;

        return MapExactRouteError(httpStatus, error, errorCode, message, serviceInstanceId, serviceSlug);
    }

    private static (string ErrorCode, string ErrorMessage)? MapExactRouteFailure(
        NyxIdProxyBinaryResponse response,
        string serviceInstanceId,
        string serviceSlug)
    {
        if (response.Succeeded ||
            !TryReadNyxIdError(response.Detail, out var error, out var errorCode, out var message))
        {
            return null;
        }

        return MapExactRouteError(response.HttpStatus, error, errorCode, message, serviceInstanceId, serviceSlug);
    }

    private static (string ErrorCode, string ErrorMessage)? MapExactRouteError(
        int httpStatus,
        string error,
        int errorCode,
        string message,
        string serviceInstanceId,
        string serviceSlug)
    {
        if (httpStatus == (int)HttpStatusCode.BadRequest &&
            error == "bad_request" &&
            errorCode == 1000 &&
            IsExactSlugMismatch(message, serviceInstanceId, serviceSlug))
        {
            return (
                "NYXID_OPERATION_AUTHORITY_DRIFT",
                "NyxID rejected the admitted UserService authority.");
        }

        if (httpStatus == (int)HttpStatusCode.NotFound &&
            error == "not_found" &&
            errorCode == 1003 &&
            message == $"Not found: UserService '{serviceInstanceId}' not found")
        {
            return (
                "NYXID_OPERATION_AUTHORITY_DRIFT",
                "NyxID rejected the admitted UserService authority.");
        }

        if (httpStatus == (int)HttpStatusCode.Forbidden &&
            error == "org_role_insufficient" &&
            errorCode == 8103 &&
            message == "Organization role insufficient: you do not have proxy access to this service")
        {
            return (
                "NYXID_OPERATION_AUTHORITY_ACCESS_DENIED",
                "NyxID denied access to the admitted UserService authority.");
        }

        return null;
    }

    private static bool IsExactSlugMismatch(string message, string serviceInstanceId, string serviceSlug)
    {
        var prefix = $"Bad request: _nyxid_via UserService '{serviceInstanceId}' has slug '";
        var suffix = $"', but the route requested '{serviceSlug}'";
        if (!message.StartsWith(prefix, StringComparison.Ordinal) ||
            !message.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var liveSlugLength = message.Length - prefix.Length - suffix.Length;
        return liveSlugLength > 0 && !message.AsSpan(prefix.Length, liveSlugLength).Contains('\'');
    }

    private static bool TryReadNyxIdTextProxyFailure(
        string? response,
        out int httpStatus,
        out string error,
        out int errorCode,
        out string message)
    {
        httpStatus = 0;
        error = string.Empty;
        errorCode = 0;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var wrapper = JsonDocument.Parse(response);
            var root = wrapper.RootElement;
            if (root.TryGetProperty("error", out var wrapperError) &&
                wrapperError.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("status", out var status) &&
                status.TryGetInt32(out httpStatus) &&
                root.TryGetProperty("body", out var body) &&
                body.ValueKind == JsonValueKind.String)
            {
                using var payload = JsonDocument.Parse(body.GetString()!);
                return TryReadNyxIdError(payload.RootElement, out error, out errorCode, out message);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadNyxIdError(
        string? response,
        out string error,
        out int errorCode,
        out string message)
    {
        error = string.Empty;
        errorCode = 0;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var payload = JsonDocument.Parse(response);
            return TryReadNyxIdError(payload.RootElement, out error, out errorCode, out message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadNyxIdError(
        JsonElement payload,
        out string error,
        out int errorCode,
        out string message)
    {
        error = string.Empty;
        errorCode = 0;
        message = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("error", out var errorElement) ||
            errorElement.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("error_code", out var errorCodeElement) ||
            !errorCodeElement.TryGetInt32(out errorCode) ||
            !payload.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        error = errorElement.GetString()!;
        message = messageElement.GetString()!;
        return true;
    }

    private static string FormatAdmissionIdentity(AgentToolOperationIdentity identity) =>
        identity switch
        {
            AgentToolOperationIdentity.PublishedEndpoint published =>
                $"published:{published.EndpointId}",
            AgentToolOperationIdentity.AuthoredRequest authored =>
                $"authored:{authored.RequestContractDigest}",
            _ => "unknown",
        };

    private static string AdmissionDriftError(string code, string message) =>
        JsonSerializer.Serialize(new { error = true, error_code = code, message });

    private static AgentToolTerminalOutcome CreateAdmittedFailureOutcome(
        AgentToolOperationAdmission admission,
        string callId,
        string toolName,
        string errorCode,
        string errorMessage)
    {
        var result = AdmissionDriftError(errorCode, errorMessage);
        return new AgentToolTerminalOutcome(
            result,
            NyxIdProxyReceiptFactory.CreateError(
                callId,
                toolName,
                admission.ServiceInstanceId,
                errorCode,
                errorMessage,
                result));
    }

    private async Task<AgentToolTerminalOutcome> ExecuteAdmittedFileArtifactAsync(
        string effectiveToken,
        NyxIdOperationRequest request,
        string callId,
        string toolName,
        string? authoredServiceInstanceId,
        CancellationToken ct)
    {
        if (_fileArtifactIngress == null)
            return new AgentToolTerminalOutcome(FileArtifactError("file_artifact_ingress_unavailable", "Host has not registered workflow file artifact ingress."));

        var context = AgentToolRequestContext.Current;
        var workflowRuntime = context?.WorkflowRuntime ?? AgentWorkflowRuntimeContext.Empty;
        var callerScopeId = Normalize(context?.Caller.ScopeId);
        var ownerRunId = Normalize(workflowRuntime.ParentRunId);
        if (!workflowRuntime.HasManagedParent || callerScopeId == null || ownerRunId == null)
            return new AgentToolTerminalOutcome(FileArtifactError("managed_workflow_context_required", "response_mode=file_artifact requires a managed workflow runtime context and caller scope."));

        var response = await _client.ProxyGetBinaryResponseAsync(
            effectiveToken,
            request.Slug,
            request.ServiceId,
            request.Path,
            request.Headers,
            _fileArtifactMaxBytes,
            ct);

        if (authoredServiceInstanceId is not null)
        {
            var authorityFailure = MapExactRouteFailure(
                response,
                authoredServiceInstanceId,
                request.Slug);
            if (authorityFailure is { } failure)
            {
                var result = AdmissionDriftError(failure.ErrorCode, failure.ErrorMessage);
                return new AgentToolTerminalOutcome(
                    result,
                    NyxIdProxyReceiptFactory.CreateError(
                        callId,
                        toolName,
                        request.ServiceId,
                        failure.ErrorCode,
                        failure.ErrorMessage,
                        result));
            }
        }

        var completion = await CompleteFileArtifactAsync(
            response,
            request.Slug,
            request.ServiceId,
            request.Path,
            callerScopeId,
            ownerRunId,
            ct);
        return new AgentToolTerminalOutcome(
            completion.ResultJson,
            completion.Succeeded
                ? NyxIdProxyReceiptFactory.CreateSuccess(
                    callId,
                    toolName,
                    request.ServiceId,
                    completion.ResultJson)
                : null);
    }

    private async Task<string> ExecuteFileArtifactAsync(
        string token,
        string? orgToken,
        string slug,
        string serviceId,
        string path,
        string method,
        ToolArgs args,
        string? body,
        Dictionary<string, string>? headers,
        CancellationToken ct)
    {
        if (!string.Equals(method.Trim(), "GET", StringComparison.OrdinalIgnoreCase))
            return FileArtifactError("file_artifact_requires_get", "response_mode=file_artifact only supports GET.");

        if (HasRequestBody(args))
            return FileArtifactError("file_artifact_disallows_body", "response_mode=file_artifact does not accept a request body.");

        if (_fileArtifactIngress == null)
            return FileArtifactError("file_artifact_ingress_unavailable", "Host has not registered workflow file artifact ingress.");

        var context = AgentToolRequestContext.Current;
        var workflowRuntime = context?.WorkflowRuntime ?? AgentWorkflowRuntimeContext.Empty;
        var callerScopeId = Normalize(context?.Caller.ScopeId);
        var ownerRunId = Normalize(workflowRuntime.ParentRunId);
        if (!workflowRuntime.HasManagedParent || callerScopeId == null || ownerRunId == null)
            return FileArtifactError("managed_workflow_context_required", "response_mode=file_artifact requires a managed workflow runtime context and caller scope.");

        var effectiveToken = await ResolveTokenForServiceAsync(token, orgToken, serviceId, ct);
        _logger.LogInformation(
            "[nyxid_proxy] GET file_artifact slug={Slug} maxBytes={MaxBytes} tokenSource={Source}",
            slug,
            _fileArtifactMaxBytes,
            effectiveToken == token ? "user" : "org");

        var response = await _client.ProxyGetBinaryResponseAsync(
            effectiveToken,
            slug,
            serviceId,
            path,
            headers,
            _fileArtifactMaxBytes,
            ct);

        return (await CompleteFileArtifactAsync(
            response,
            slug,
            serviceId,
            path,
            callerScopeId,
            ownerRunId,
            ct)).ResultJson;
    }

    private async Task<(string ResultJson, bool Succeeded)> CompleteFileArtifactAsync(
        NyxIdProxyBinaryResponse response,
        string slug,
        string serviceId,
        string path,
        string callerScopeId,
        string ownerRunId,
        CancellationToken ct)
    {
        if (!response.Succeeded)
        {
            var error = response.Detail switch
            {
                "content_length_exceeds_max_bytes" => "file_artifact_too_large",
                "content_exceeds_max_bytes" => "file_artifact_too_large",
                _ => "provider_binary_download_failed",
            };
            var detail = error == "file_artifact_too_large"
                ? response.Detail ?? "content_exceeds_max_bytes"
                : "NyxID binary proxy request failed.";
            return (FileArtifactError(
                error,
                detail,
                response.HttpStatus,
                response.ContentType), false);
        }

        if (response.Content.Length == 0)
            return (FileArtifactError("empty_file_artifact", "NyxID binary proxy response was empty.", response.HttpStatus, response.ContentType), false);

        FileArtifactIngressResult ingressResult;
        try
        {
            ingressResult = await _fileArtifactIngress!.IngestAsync(new FileArtifactIngressRequest(
                response.Content,
                FileArtifactSourceKind.ConnectedServiceResource,
                SourceMessageId: $"nyxid_proxy:{serviceId}",
                SourceResourceKey: SanitizeResourcePath(path),
                FileName: response.FileName,
                MediaType: response.ContentType,
                OwnerRunId: ownerRunId,
                OwnerScopeId: callerScopeId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "[nyxid_proxy] File artifact ingress failed. slug={Slug} exceptionType={ExceptionType}",
                slug,
                ex.GetType().Name);
            return (FileArtifactError("artifact_ingress_failed", "Downloaded resource could not be stored.", response.HttpStatus, response.ContentType), false);
        }

        return (JsonSerializer.Serialize(
            new NyxIdProxyFileArtifactSuccess(
                true,
                FileArtifactResponseMode,
                slug,
                SanitizeResourcePath(path),
                response.HttpStatus,
                response.ContentType,
                response.FileName,
                ToFileRefProjection(ingressResult.FileRef)),
            JsonOptions), true);
    }

    // ─── Dual-token exact identity routing ───

    /// <summary>
    /// Resolve which token to use for a given exact UserService id.
    /// Checks user token's service list first; falls back to org token.
    /// </summary>
    private async Task<string> ResolveTokenForServiceAsync(
        string userToken, string? orgToken, string serviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orgToken) || TokensEqual(orgToken, userToken))
            return userToken;

        if (await ServiceExistsForTokenAsync(userToken, serviceId, ct))
            return userToken;

        if (await ServiceExistsForTokenAsync(orgToken, serviceId, ct))
        {
            _logger.LogInformation(
                "[nyxid_proxy] Service instance {ServiceId} not found for user token, using org token", serviceId);
            return orgToken;
        }

        // Neither has it — use user token and let NyxID return the error
        return userToken;
    }

    /// <summary>
    /// Check whether a given token can access an exact UserService id.
    /// Reads NyxID's live keys surface for every route decision.
    /// </summary>
    private async Task<bool> ServiceExistsForTokenAsync(
        string token, string serviceId, CancellationToken ct)
    {
        // Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache):
        //   Old pattern: NyxIdSpecCatalog + SpecFetchToken + IServiceDiscoveryCache 在仓库内建第二 catalog(NyxID 真实源的影子)
        //   New principle: NyxID 是唯一真实源; routing checks read the live NyxID proxy-services surface and never keep slug facts in a process-local cache.
        try
        {
            var servicesJson = await _client.ListServicesAsync(token, ct);
            using var doc = System.Text.Json.JsonDocument.Parse(servicesJson);
            return ParseServiceIds(doc).Contains(serviceId);
        }
        catch
        {
            return false;
        }
    }

    // ─── Helpers ───

    internal static HashSet<string> ParseServiceIds(System.Text.Json.JsonDocument doc)
    {
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in EnumerateServiceItems(doc.RootElement))
        {
            var serviceId = ReadServiceId(service);
            if (!string.IsNullOrWhiteSpace(serviceId))
                serviceIds.Add(serviceId);
        }

        return serviceIds;
    }

    private static string? ReadServiceId(System.Text.Json.JsonElement service)
    {
        if (service.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;
        if (service.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
            return Normalize(id.GetString());
        if (service.TryGetProperty("service_id", out var serviceId) &&
            serviceId.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return Normalize(serviceId.GetString());
        }

        return null;
    }

    private static IEnumerable<System.Text.Json.JsonElement> EnumerateServiceItems(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "keys", "services", "custom_services", "items", "data" })
        {
            if (!root.TryGetProperty(propertyName, out var items) ||
                items.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
                yield return item;
        }
    }

    /// <summary>
    /// Detect NyxID approval error codes (7000 = approval_required, 7001 = approval_failed).
    /// </summary>
    private static bool IsApprovalError(string result, out int code, out string? requestId)
    {
        code = 0;
        requestId = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number)
                code = c.GetInt32();
            if (doc.RootElement.TryGetProperty("approval_request_id", out var rid))
                requestId = rid.GetString();
            return code is 7000 or 7001;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveResponseMode(string? raw)
    {
        var normalized = Normalize(raw);
        if (normalized == null)
            return TextResponseMode;

        if (string.Equals(normalized, TextResponseMode, StringComparison.OrdinalIgnoreCase))
            return TextResponseMode;

        if (string.Equals(normalized, FileArtifactResponseMode, StringComparison.OrdinalIgnoreCase))
            return FileArtifactResponseMode;

        return null;
    }

    private static bool HasRequestBody(ToolArgs args) =>
        args.Has("body");

    private static long NormalizeMaxBytes(long maxBytes) =>
        maxBytes <= 0
            ? NyxIdToolOptions.DefaultProxyFileArtifactMaxBytes
            : Math.Min(maxBytes, NyxIdToolOptions.HardProxyFileArtifactMaxBytes);

    private static string FileArtifactError(
        string error,
        string detail,
        int httpStatus = 0,
        string? sourceContentType = null) =>
        JsonSerializer.Serialize(
            new NyxIdProxyFileArtifactError(
                false,
                FileArtifactResponseMode,
                error,
                detail,
                httpStatus == 0 ? null : httpStatus,
                sourceContentType),
            JsonOptions);

    private static NyxIdProxyWorkflowFileRefProjection ToFileRefProjection(FileArtifactRef fileRef) =>
        new(
            fileRef.FileId,
            fileRef.ArtifactId,
            fileRef.SourceKind.ToString(),
            fileRef.SourceMessageId,
            fileRef.SourceResourceKey,
            fileRef.FileName,
            fileRef.MediaType,
            fileRef.SizeBytes,
            fileRef.Sha256,
            fileRef.CreatedAtUnixMs,
            fileRef.ExpiresAtUnixMs,
            fileRef.OwnerRunId,
            fileRef.OwnerScopeId);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SanitizeResourcePath(string path)
    {
        var normalized = path.Trim();
        var delimiter = normalized.IndexOfAny(['?', '#']);
        return delimiter >= 0 ? normalized[..delimiter] : normalized;
    }

    /// <summary>
    /// Constant-time equality for two access tokens. Comparing secrets with <c>==</c> is
    /// short-circuiting and leaks a length/prefix timing signal; <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>
    /// over the UTF-8 bytes runs in time independent of where the tokens first differ.
    /// Null is not a secret, so a null operand falls back to reference/<c>==</c> semantics
    /// (only two nulls are equal); behavior is otherwise identical to <c>==</c> for
    /// equal/unequal non-null tokens.
    /// </summary>
    internal static bool TokensEqual(string? left, string? right)
    {
        if (left is null || right is null)
            return ReferenceEquals(left, right);

        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record NyxIdProxyFileArtifactSuccess(
        bool Success,
        string ResponseMode,
        string Slug,
        string Path,
        int HttpStatus,
        string? SourceContentType,
        string? SourceFileName,
        NyxIdProxyWorkflowFileRefProjection FileRef);

    private sealed record NyxIdProxyFileArtifactError(
        bool Success,
        string ResponseMode,
        string Error,
        string Detail,
        int? HttpStatus,
        string? SourceContentType);

    private sealed record NyxIdProxyWorkflowFileRefProjection(
        string? FileId,
        string? ArtifactId,
        string SourceKind,
        string? SourceMessageId,
        string? SourceResourceKey,
        string? FileName,
        string? MediaType,
        long SizeBytes,
        string? Sha256,
        long CreatedAtUnixMs,
        long ExpiresAtUnixMs,
        string? OwnerRunId,
        string? OwnerScopeId);
}
