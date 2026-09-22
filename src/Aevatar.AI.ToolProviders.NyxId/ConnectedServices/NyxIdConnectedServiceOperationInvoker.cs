using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public sealed record NyxIdConnectedServiceOperationInvocation(
    string? UserServiceId,
    string? ServiceSlug,
    string? OperationId,
    string OperationArgumentsJson,
    NyxIdConnectedServiceDocumentRequest? DocumentRequest = null);

public sealed record NyxIdConnectedServiceDocumentRequest(
    string Method,
    string RelativePath,
    string RequestArgumentsJson,
    NyxIdRecommendedSkillRef SkillRef);

public sealed record NyxIdConnectedServiceOperationInvokeResult(
    bool IsSuccess,
    AgentToolTerminalOutcome? Outcome,
    string FailureCode)
{
    public static NyxIdConnectedServiceOperationInvokeResult Success(AgentToolTerminalOutcome outcome) =>
        new(true, outcome, string.Empty);

    public static NyxIdConnectedServiceOperationInvokeResult Failure(string failureCode) =>
        new(false, null, failureCode);
}

public sealed class NyxIdConnectedServiceOperationInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly TimeSpan CatalogFreshnessWindow = TimeSpan.FromMinutes(5);
    private const int CustomOpenApiMaxBytes = 1024 * 1024;
    private const int MaxReadSourceBytes = 16 * 1024;
    private const string ProxyResponseTooLargeErrorCode = "NYXID_PROXY_RESPONSE_TOO_LARGE";
    private const string ReadTooLargeErrorCode = "NYXID_CONNECTED_SERVICE_READ_TOO_LARGE";
    private const string ReadTooLargeErrorMessage =
        "The connected-service read result exceeded the bounded projection limit. " +
        "Retry with a narrower query or smaller page size and paginate across bounded reads.";
    private const string ReadProjectionKind = "connected_service_read_projection";
    private const string EffectReceiptKind = "connected_service_effect_receipt";

    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _apiClient;
    private readonly NyxIdServiceInstanceClient _client;
    private readonly ILogger _logger;
    private readonly INyxIdProxyFileArtifactIngress? _fileArtifactIngress;
    private readonly NyxIdDelegationTokenLease _delegationTokenLease;

    public NyxIdConnectedServiceOperationInvoker(
        NyxIdToolOptions options,
        NyxIdApiClient apiClient,
        NyxIdServiceInstanceClient client,
        ILogger<NyxIdConnectedServiceOperationInvoker>? logger = null,
        INyxIdProxyFileArtifactIngress? fileArtifactIngress = null,
        NyxIdDelegationTokenLease? delegationTokenLease = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<NyxIdConnectedServiceOperationInvoker>.Instance;
        _fileArtifactIngress = fileArtifactIngress;
        _delegationTokenLease = delegationTokenLease ?? new NyxIdDelegationTokenLease(apiClient);
    }

    public async Task<NyxIdConnectedServiceOperationInvokeResult> InvokeAsync(
        AgentToolExecutionContext context,
        NyxIdConnectedServiceOperationInvocation invocation,
        string callId,
        string toolName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        if (string.IsNullOrWhiteSpace(_options.EffectiveTransportBaseUrl))
            return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_source_unavailable");

        var executionToken = context.Credentials.NyxIdAccessToken;
        var inventoryToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context.Credentials)
                             ?? executionToken;
        if (string.IsNullOrWhiteSpace(executionToken) || string.IsNullOrWhiteSpace(inventoryToken))
            return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_context_unavailable");

        try
        {
            var bindings = await ReadBindingsAsync(context, executionToken, inventoryToken, ct)
                .ConfigureAwait(false);
            var matchedBindings = bindings
                .Where(binding => MatchesService(binding.Instance, invocation))
                .ToArray();
            if (matchedBindings.Length == 0)
                return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_not_visible");

            if (invocation.DocumentRequest is not null)
            {
                if (matchedBindings.Length > 1)
                    return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_ambiguous");
                return await InvokeDocumentGuidedRequestAsync(
                        context,
                        executionToken,
                        matchedBindings[0],
                        invocation.DocumentRequest,
                        callId,
                        toolName,
                        ct)
                    .ConfigureAwait(false);
            }

            var services = await ReadOperationContractsAsync(
                    context,
                    executionToken,
                    matchedBindings,
                    ct)
                .ConfigureAwait(false);
            var bindingsById = matchedBindings.ToDictionary(
                static binding => binding.Instance.UserServiceId,
                StringComparer.Ordinal);
            var matches = services
                .Where(service => HasExactRouteBinding(service, bindingsById))
                .SelectMany(service => service.Endpoints.Select(endpoint => new
                {
                    Service = service,
                    Endpoint = endpoint,
                    Binding = bindingsById[service.UserServiceId],
                }))
                .Where(candidate => string.Equals(
                    candidate.Endpoint.EndpointId,
                    invocation.OperationId,
                    StringComparison.Ordinal))
                .ToArray();

            if (matches.Length == 0)
                return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_not_visible");
            if (matches.Length > 1)
                return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_ambiguous");

            var match = matches[0];
            if (!match.Endpoint.IsReadOnly && !_options.EnableAssistantConnectedServiceEffects)
                return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_not_visible");

            var proof = NyxIdOperationAdmissionProofBuilder.Build(
                match.Service.UserServiceId,
                match.Service.ServiceSlug,
                match.Endpoint,
                match.Endpoint.ContractDigest).NyxIdUserService;
            var admission = NyxIdConnectedServiceOperationAdmissionMapper.Map(
                proof,
                match.Service.Source.ContentDigest,
                match.Binding.Instance.CatalogServiceSlug);
            if (!NyxIdConnectedServiceExposurePolicy.Allows(admission))
                return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_not_visible");

            var readBackPlan = NyxIdAssistantOperationReadBackRegistry.Resolve(
                _options,
                match.Service,
                match.Endpoint,
                match.Service.Source.ContentDigest,
                match.Binding.Instance);
            if (readBackPlan?.TryFreeze(invocation.OperationArgumentsJson, out var readBack) == true)
                admission = admission with { ReadBack = readBack };

            var proxy = new NyxIdProxyTool(
                _apiClient,
                _logger,
                _fileArtifactIngress,
                _options.EffectiveProxyFileArtifactMaxBytes,
                _options.ManagedWorkflowAdmissionMode,
                _delegationTokenLease);
            var sourceReadableToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context.Credentials)
                                      ?? context.Credentials.NyxIdAccessToken;
            var operationToken = match.Binding.Instance.AccessTokenSource == NyxIdServiceAccessTokenSource.Organization
                ? context.Credentials.NyxIdOrgToken
                : context.Credentials.NyxIdAccessToken;
            var credentials = context.Credentials with
            {
                NyxIdAccessToken = operationToken,
                SourceReadableNyxIdAccessToken = sourceReadableToken,
            };
            using var scope = AgentToolContextScope.Push(context with
            {
                Credentials = credentials,
                OperationAdmission = admission,
            });
            var outcome = admission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly
                ? await proxy.ExecuteAdmittedReadWithOutcomeAsync(
                    callId,
                    toolName,
                    invocation.OperationArgumentsJson,
                    MaxReadSourceBytes,
                    ct).ConfigureAwait(false)
                : await proxy.ExecuteAdmittedEffectWithOutcomeAsync(
                    callId,
                    toolName,
                    invocation.OperationArgumentsJson,
                    ct).ConfigureAwait(false);
            var receipt = outcome.Receipt ?? proxy.CreateResultReceipt(
                callId,
                toolName,
                invocation.OperationArgumentsJson,
                outcome.ResultJson);
            var terminalOutcome = admission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly
                ? BuildReadOutcome(
                    admission,
                    match.Service.ServiceName,
                    match.Endpoint.Name,
                    callId,
                    toolName,
                    outcome.ResultJson,
                    receipt)
                : BuildEffectOutcome(
                    admission,
                    readBackPlan,
                    match.Service.ServiceName,
                    match.Endpoint.Name,
                    callId,
                    toolName,
                    receipt);
            return NyxIdConnectedServiceOperationInvokeResult.Success(terminalOutcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID fixed connected-service operation invocation failed");
            return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_source_unavailable");
        }
    }

    private async Task<NyxIdConnectedServiceOperationInvokeResult> InvokeDocumentGuidedRequestAsync(
        AgentToolExecutionContext context,
        string executionToken,
        NyxIdServiceInstanceBinding binding,
        NyxIdConnectedServiceDocumentRequest request,
        string callId,
        string toolName,
        CancellationToken ct)
    {
        if (!HasExactRecommendedSkillRef(binding.Instance, request.SkillRef))
            return NyxIdConnectedServiceOperationInvokeResult.Failure("document_request_not_admitted");
        if (!TryBuildDocumentGuidedAdmission(binding.Instance, request, out var admission, out var runtimeArgumentsJson))
            return NyxIdConnectedServiceOperationInvokeResult.Failure("document_request_invalid");
        if (admission.ExecutionPolicy.Risk != AgentToolOperationRisk.ReadOnly &&
            !_options.EnableAssistantConnectedServiceEffects)
        {
            return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_not_visible");
        }

        var proxy = new NyxIdProxyTool(
            _apiClient,
            _logger,
            _fileArtifactIngress,
            _options.EffectiveProxyFileArtifactMaxBytes,
            _options.ManagedWorkflowAdmissionMode,
            _delegationTokenLease);
        var sourceReadableToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context.Credentials)
                                  ?? context.Credentials.NyxIdAccessToken;
        var operationToken = binding.Instance.AccessTokenSource == NyxIdServiceAccessTokenSource.Organization
            ? context.Credentials.NyxIdOrgToken
            : executionToken;
        var credentials = context.Credentials with
        {
            NyxIdAccessToken = operationToken,
            SourceReadableNyxIdAccessToken = sourceReadableToken,
        };
        using var scope = AgentToolContextScope.Push(context with
        {
            Credentials = credentials,
            OperationAdmission = admission,
        });
        var outcome = admission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly
            ? await proxy.ExecuteAdmittedReadWithOutcomeAsync(
                callId,
                toolName,
                runtimeArgumentsJson,
                MaxReadSourceBytes,
                ct).ConfigureAwait(false)
            : await proxy.ExecuteAdmittedEffectWithOutcomeAsync(
                callId,
                toolName,
                runtimeArgumentsJson,
                ct).ConfigureAwait(false);
        var receipt = outcome.Receipt ?? proxy.CreateResultReceipt(
            callId,
            toolName,
            runtimeArgumentsJson,
            outcome.ResultJson);
        var label = FirstNonEmpty(binding.Instance.Label, binding.Instance.DisplaySlug, binding.Instance.CatalogServiceSlug);
        var operationLabel = $"{admission.HttpMethod} {admission.PathTemplate}";
        var terminalOutcome = admission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly
            ? BuildReadOutcome(
                admission,
                label,
                operationLabel,
                callId,
                toolName,
                outcome.ResultJson,
                receipt)
            : BuildEffectOutcome(
                admission,
                readBackPlan: null,
                label,
                operationLabel,
                callId,
                toolName,
                receipt);
        return NyxIdConnectedServiceOperationInvokeResult.Success(terminalOutcome);
    }

    private static bool HasExactRecommendedSkillRef(
        NyxIdServiceInstance instance,
        NyxIdRecommendedSkillRef requestedRef) =>
        instance.RecommendedSkillRefs.Any(skillRef =>
            skillRef.Source == requestedRef.Source &&
            string.Equals(skillRef.SkillId, requestedRef.SkillId, StringComparison.Ordinal) &&
            string.Equals(skillRef.LiteralVersion, requestedRef.LiteralVersion, StringComparison.Ordinal) &&
            string.Equals(skillRef.ManifestDigest, requestedRef.ManifestDigest, StringComparison.Ordinal));

    private static bool TryBuildDocumentGuidedAdmission(
        NyxIdServiceInstance instance,
        NyxIdConnectedServiceDocumentRequest request,
        out AgentToolOperationAdmission admission,
        out string runtimeArgumentsJson)
    {
        admission = null!;
        runtimeArgumentsJson = string.Empty;
        var method = NormalizeDocumentRequestMethod(request.Method);
        if (method is null || !TryNormalizeDocumentRequestPath(request.RelativePath, out var pathTemplate))
            return false;

        if (!TryReadDocumentRuntimeArguments(
                request.RequestArgumentsJson,
                out var queryParameters,
                out var headerParameters,
                out var hasBody))
        {
            return false;
        }

        if (hasBody && method is "GET" or "HEAD" or "OPTIONS")
            return false;

        var risk = method switch
        {
            "GET" or "HEAD" or "OPTIONS" => AgentToolOperationRisk.ReadOnly,
            "DELETE" => AgentToolOperationRisk.Destructive,
            _ => AgentToolOperationRisk.Write,
        };
        var requestBody = hasBody
            ? new AgentToolOperationRequestBody(
                Required: false,
                MediaType: "application/json",
                Schema: new AgentToolOperationValueSchema(
                    AgentToolOperationValueKind.Object,
                    [],
                    new HashSet<string>(StringComparer.Ordinal),
                    null,
                    [],
                    AdditionalPropertiesAllowed: true))
            : null;
        var parameters = queryParameters
            .Select(static name => new AgentToolOperationParameter(
                name,
                AgentToolOperationParameterLocation.Query,
                Required: false,
                AgentToolOperationValueSchema.Text))
            .Concat(headerParameters.Select(static name => new AgentToolOperationParameter(
                name,
                AgentToolOperationParameterLocation.Header,
                Required: false,
                AgentToolOperationValueSchema.Text)))
            .ToArray();
        var contractDigest = ComputeDocumentRequestDigest(
            instance.UserServiceId,
            method,
            pathTemplate,
            queryParameters,
            headerParameters,
            hasBody);
        admission = new AgentToolOperationAdmission(
            instance.UserServiceId,
            instance.DisplaySlug,
            new AgentToolOperationIdentity.AuthoredRequest(contractDigest),
            AgentToolOperationAuthorizationBasis.ExplicitRequest,
            method,
            pathTemplate,
            contractDigest,
            parameters,
            requestBody,
            AgentToolOperationResponsePolicy.TextOnly,
            new AgentToolOperationExecutionPolicy(
                risk,
                risk == AgentToolOperationRisk.ReadOnly
                    ? AgentToolOperationApproval.None
                    : AgentToolOperationApproval.Required,
                AgentToolOperationEnforcementOwner.Aevatar,
                [AgentToolOperationExecutionMode.Interactive]),
            CatalogDigest: string.Empty,
            CatalogServiceSlug: instance.CatalogServiceSlug);
        runtimeArgumentsJson = request.RequestArgumentsJson;
        return true;
    }

    private static bool TryReadDocumentRuntimeArguments(
        string argumentsJson,
        out IReadOnlyList<string> queryParameters,
        out IReadOnlyList<string> headerParameters,
        out bool hasBody)
    {
        queryParameters = [];
        headerParameters = [];
        hasBody = false;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("query" or "headers" or "body"))
                    return false;
            }
            if (root.TryGetProperty("query", out var query))
            {
                if (query.ValueKind != JsonValueKind.Object)
                    return false;
                queryParameters = query.EnumerateObject()
                    .Select(static property => property.Name)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }
            if (root.TryGetProperty("headers", out var headers))
            {
                if (headers.ValueKind != JsonValueKind.Object)
                    return false;
                headerParameters = headers.EnumerateObject()
                    .Select(static property => property.Name)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            hasBody = root.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null;
            return !hasBody || body.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? NormalizeDocumentRequestMethod(string method)
    {
        var normalized = method.Trim().ToUpperInvariant();
        return normalized is "GET" or "HEAD" or "OPTIONS" or "POST" or "PUT" or "PATCH" or "DELETE"
            ? normalized
            : null;
    }

    private static bool TryNormalizeDocumentRequestPath(string relativePath, out string path)
    {
        path = string.Empty;
        try
        {
            path = "/" + NyxIdApiClient.NormalizeExactProxyPath(relativePath);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ComputeDocumentRequestDigest(
        string userServiceId,
        string method,
        string path,
        IReadOnlyList<string> queryParameters,
        IReadOnlyList<string> headerParameters,
        bool hasBody)
    {
        var material = string.Join(
            '\n',
            userServiceId,
            method,
            path,
            string.Join(',', queryParameters),
            string.Join(',', headerParameters),
            hasBody ? "body:json-object" : "body:none");
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static AgentToolTerminalOutcome BuildReadOutcome(
        AgentToolOperationAdmission admission,
        string serviceLabel,
        string operationLabel,
        string callId,
        string toolName,
        string sourceResult,
        AgentToolReceipt? sourceReceipt)
    {
        var sourceBytes = Encoding.UTF8.GetByteCount(sourceResult ?? string.Empty);
        if (sourceBytes > MaxReadSourceBytes ||
            string.Equals(
                sourceReceipt?.ErrorCode,
                ProxyResponseTooLargeErrorCode,
                StringComparison.Ordinal))
        {
            return BuildReadTooLargeOutcome(admission, serviceLabel, operationLabel, callId, toolName);
        }

        if (sourceReceipt?.Status != AgentToolReceiptStatus.Success)
        {
            var result = BuildReadProjection(
                admission,
                serviceLabel,
                operationLabel,
                "failed",
                data: null,
                SafeCode(sourceReceipt?.ErrorCode),
                SafeMessage(sourceReceipt?.ErrorMessage));
            var receipt = sourceReceipt?.Clone() ?? NyxIdProxyReceiptFactory.CreateError(
                callId,
                toolName,
                admission.ServiceInstanceId,
                "NYXID_CONNECTED_SERVICE_READ_UNVERIFIED",
                "The connected-service read result could not be verified.",
                result);
            receipt.ResultJson = result;
            return new AgentToolTerminalOutcome(result, receipt);
        }

        JsonNode? data;
        try
        {
            data = JsonNode.Parse(sourceResult ?? string.Empty);
        }
        catch (JsonException)
        {
            data = JsonValue.Create(sourceResult);
        }

        var projection = BuildReadProjection(admission, serviceLabel, operationLabel, "succeeded", data, null, null);
        if (Encoding.UTF8.GetByteCount(projection) > MaxReadSourceBytes)
            return BuildReadTooLargeOutcome(admission, serviceLabel, operationLabel, callId, toolName);

        var successReceipt = sourceReceipt.Clone();
        successReceipt.ResultJson = projection;
        return new AgentToolTerminalOutcome(projection, successReceipt);
    }

    private static AgentToolTerminalOutcome BuildReadTooLargeOutcome(
        AgentToolOperationAdmission admission,
        string serviceLabel,
        string operationLabel,
        string callId,
        string toolName)
    {
        var result = BuildBoundedReadTooLargeProjection(admission, serviceLabel, operationLabel);
        var receipt = NyxIdProxyReceiptFactory.CreateSuccess(
            callId,
            toolName,
            admission.ServiceInstanceId,
            result) ?? throw new InvalidOperationException(
            "A connected-service operation must have a valid UserService identity.");
        receipt.Effect = AgentToolReceiptEffect.ReadOnly;
        return new AgentToolTerminalOutcome(result, receipt);
    }

    private static string BuildBoundedReadTooLargeProjection(
        AgentToolOperationAdmission admission,
        string serviceLabel,
        string operationLabel)
    {
        var result = BuildReadProjection(
            admission,
            serviceLabel,
            operationLabel,
            "retry_required",
            data: null,
            ReadTooLargeErrorCode,
            ReadTooLargeErrorMessage,
            BuildReadRetryHints(admission, includeQueryParameters: true));
        if (Encoding.UTF8.GetByteCount(result) <= MaxReadSourceBytes)
            return result;

        result = BuildReadProjection(
            admission,
            serviceLabel,
            operationLabel,
            "retry_required",
            data: null,
            ReadTooLargeErrorCode,
            ReadTooLargeErrorMessage,
            BuildReadRetryHints(admission, includeQueryParameters: false));
        return Encoding.UTF8.GetByteCount(result) <= MaxReadSourceBytes
            ? result
            : BuildReadProjection(
                admission,
                serviceLabel,
                operationLabel,
                "retry_required",
                data: null,
                ReadTooLargeErrorCode,
                ReadTooLargeErrorMessage);
    }

    private static AgentToolTerminalOutcome BuildEffectOutcome(
        AgentToolOperationAdmission admission,
        NyxIdConnectedServiceReadBackPlan? readBackPlan,
        string serviceLabel,
        string operationLabel,
        string callId,
        string toolName,
        AgentToolReceipt? sourceReceipt)
    {
        var receipt = sourceReceipt?.Clone() ?? NyxIdProxyReceiptFactory.CreateError(
            callId,
            toolName,
            admission.ServiceInstanceId,
            "NYXID_CONNECTED_SERVICE_EFFECT_UNVERIFIED",
            "The connected-service effect result could not be verified.",
            string.Empty);
        if (receipt.Status == AgentToolReceiptStatus.Success)
            receipt.ProviderResourceId = readBackPlan?.ExtractProviderResourceId(receipt.ResultJson) ?? string.Empty;

        var result = new JsonObject
        {
            ["kind"] = EffectReceiptKind,
            ["status"] = receipt.Status.ToString().ToLowerInvariant(),
            ["provenance"] = BuildProvenance(admission, serviceLabel, operationLabel),
            ["approval_request_id"] = string.IsNullOrWhiteSpace(receipt.ApprovalRequestId)
                ? null
                : receipt.ApprovalRequestId,
            ["error_code"] = SafeCode(receipt.ErrorCode),
            ["error_message"] = SafeMessage(receipt.ErrorMessage),
        }.ToJsonString(JsonOptions);
        receipt.ResultJson = result;
        return new AgentToolTerminalOutcome(result, receipt);
    }

    private static string BuildReadProjection(
        AgentToolOperationAdmission admission,
        string serviceLabel,
        string operationLabel,
        string status,
        JsonNode? data,
        string? errorCode,
        string? errorMessage,
        JsonNode? retryHints = null) => new JsonObject
    {
        ["kind"] = ReadProjectionKind,
        ["status"] = status,
        ["provenance"] = BuildProvenance(admission, serviceLabel, operationLabel),
        ["content_boundary"] = "untrusted_external_data_only",
        ["instructions_allowed"] = false,
        ["data"] = data?.DeepClone(),
        ["error_code"] = errorCode,
        ["error_message"] = errorMessage,
        ["retry_hints"] = retryHints?.DeepClone(),
    }.ToJsonString(JsonOptions);

    private static JsonObject BuildReadRetryHints(
        AgentToolOperationAdmission admission,
        bool includeQueryParameters)
    {
        var result = new JsonObject
        {
            ["reason"] = "bounded_projection_limit_exceeded",
            ["operation_path_template"] = admission.PathTemplate,
            ["retry_guidance"] = "Retry the same read with a narrower query, smaller page size, or the next page token when the operation publishes those query parameters.",
        };
        if (!includeQueryParameters)
            return result;

        result["query_parameters"] = new JsonArray(admission.QueryParameters
            .OrderBy(static parameter => parameter.Name, StringComparer.Ordinal)
            .Select(parameter => new JsonObject
            {
                ["name"] = parameter.Name,
                ["required"] = parameter.Required,
                ["description"] = NyxIdConnectedServiceOperationSchema.BuildModelParameterDescription(
                    "query",
                    parameter),
            })
            .ToArray());
        return result;
    }

    private static JsonObject BuildProvenance(
        AgentToolOperationAdmission admission,
        string serviceLabel,
        string operationLabel) => new()
    {
        ["source_kind"] = "nyxid_connected_service",
        ["operation_selector_digest"] = AgentToolOperationSelector.ComputeDigest(admission),
        ["service_label"] = NormalizeLabel(serviceLabel, "Connected service"),
        ["operation_label"] = NormalizeLabel(operationLabel, "Operation"),
    };

    private static string NormalizeLabel(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var builder = new StringBuilder(Math.Min(value.Length, 80));
        var lastWasSpace = false;
        foreach (var character in value.Trim())
        {
            if (builder.Length >= 80)
                break;
            var allowed = char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':';
            if (allowed)
            {
                builder.Append(character);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static string? SafeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= 96 ? normalized : normalized[..96];
    }

    private static string? SafeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }

    private async Task<IReadOnlyList<NyxIdServiceInstanceBinding>> ReadBindingsAsync(
        AgentToolExecutionContext context,
        string executionToken,
        string inventoryToken,
        CancellationToken ct)
    {
        if (context.Credentials.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.AgentKey)
            return await _client.DiscoverAgentKeyAsync(executionToken, ct).ConfigureAwait(false);

        var discovered = await _client.DiscoverAsync(
                inventoryToken,
                context.Credentials.NyxIdOrgToken,
                ct)
            .ConfigureAwait(false);
        return discovered
            .Where(static binding => NyxIdServiceInstanceClient.IsCallerExecutable(binding.Instance))
            .ToArray();
    }

    private async Task<IReadOnlyList<NyxIdMcpService>> ReadOperationContractsAsync(
        AgentToolExecutionContext context,
        string executionToken,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings,
        CancellationToken ct)
    {
        if (context.Credentials.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.AgentKey)
            return await ReadAgentKeyOpenApiServicesAsync(bindings, ct).ConfigureAwait(false);

        var catalog = await ReadMcpCatalogAsync(executionToken, ct).ConfigureAwait(false);
        var catalogServiceIds = catalog?.Services
            .Select(static service => service.UserServiceId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var customOpenApiServices = await ReadCustomOpenApiServicesAsync(
                bindings,
                catalogServiceIds,
                ct)
            .ConfigureAwait(false);
        return (catalog?.Services ?? []).Concat(customOpenApiServices).ToArray();
    }

    private async Task<IReadOnlyList<NyxIdMcpService>> ReadAgentKeyOpenApiServicesAsync(
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings,
        CancellationToken ct)
    {
        var services = new List<NyxIdMcpService>();
        var catalogServiceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            foreach (var catalogSpecSlug in EnumerateCatalogSpecSlugs(binding.Instance))
            {
                try
                {
                    var response = await _apiClient.GetCatalogOpenApiSpecAsync(
                        binding.AccessToken,
                        catalogSpecSlug,
                        ct).ConfigureAwait(false);
                    var parsed = NyxIdMcpOperationCatalog.ParseCustomOpenApi(
                        response,
                        binding.Instance,
                        $"agent-key-catalog:{catalogSpecSlug}",
                        DateTimeOffset.UtcNow,
                        CatalogFreshnessWindow);
                    foreach (var diagnostic in parsed.Discovery.Diagnostics)
                    {
                        _logger.LogInformation(
                            "NyxID Agent Key fixed operation OpenAPI diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                            diagnostic.Code,
                            diagnostic.Count);
                    }
                    if (parsed.Services.Count > 0)
                    {
                        services.AddRange(parsed.Services);
                        catalogServiceIds.Add(binding.Instance.UserServiceId);
                        break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    _logger.LogWarning(
                        "NyxID Agent Key fixed operation OpenAPI diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                        ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable,
                        1);
                }
            }
        }

        services.AddRange(await ReadCustomOpenApiServicesAsync(bindings, catalogServiceIds, ct).ConfigureAwait(false));
        return services;
    }

    private async Task<IReadOnlyList<NyxIdMcpService>> ReadCustomOpenApiServicesAsync(
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings,
        IReadOnlySet<string> catalogServiceIds,
        CancellationToken ct)
    {
        var services = new List<NyxIdMcpService>();
        foreach (var binding in bindings)
        {
            if (catalogServiceIds.Contains(binding.Instance.UserServiceId) ||
                string.IsNullOrWhiteSpace(binding.Instance.OpenapiSpecUrl) ||
                !TryBuildCustomOpenApiProxyPath(binding.Instance, out var proxyPath))
            {
                continue;
            }

            try
            {
                var response = await _apiClient.ProxyRequestBoundedAsync(
                    binding.AccessToken,
                    binding.Instance.DisplaySlug,
                    binding.Instance.UserServiceId,
                    proxyPath,
                    HttpMethod.Get.Method,
                    body: null,
                    extraHeaders: null,
                    CustomOpenApiMaxBytes,
                    ct);
                if (!response.Succeeded)
                    continue;
                var parsed = NyxIdMcpOperationCatalog.ParseCustomOpenApi(
                    response.Content,
                    binding.Instance,
                    $"caller-custom:{binding.Instance.UserServiceId}",
                    DateTimeOffset.UtcNow,
                    CatalogFreshnessWindow);
                foreach (var diagnostic in parsed.Discovery.Diagnostics)
                {
                    _logger.LogInformation(
                        "NyxID fixed operation custom OpenAPI diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                        diagnostic.Code,
                        diagnostic.Count);
                }
                services.AddRange(parsed.Services);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                _logger.LogWarning(
                    "NyxID fixed operation custom OpenAPI diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                    ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable,
                    1);
            }
        }

        return services;
    }

    private async Task<NyxIdMcpCatalogRead?> ReadMcpCatalogAsync(
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            var response = await _apiClient.GetMcpConfigAsync(accessToken, ct).ConfigureAwait(false);
            var catalog = NyxIdMcpOperationCatalog.Parse(
                response,
                "caller",
                DateTimeOffset.UtcNow,
                CatalogFreshnessWindow);
            foreach (var diagnostic in catalog.Discovery.Diagnostics)
            {
                _logger.LogInformation(
                    "NyxID fixed operation MCP diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                    diagnostic.Code,
                    diagnostic.Count);
            }
            return catalog;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "NyxID fixed operation MCP diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable,
                1);
            return null;
        }
    }

    private static bool MatchesService(
        NyxIdServiceInstance instance,
        NyxIdConnectedServiceOperationInvocation invocation)
    {
        if (!string.IsNullOrWhiteSpace(invocation.UserServiceId) &&
            !string.Equals(instance.UserServiceId, invocation.UserServiceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(invocation.ServiceSlug))
            return true;

        return string.Equals(instance.DisplaySlug, invocation.ServiceSlug, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(instance.CatalogServiceSlug, invocation.ServiceSlug, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactRouteBinding(
        NyxIdMcpService service,
        IReadOnlyDictionary<string, NyxIdServiceInstanceBinding> bindingsById) =>
        bindingsById.TryGetValue(service.UserServiceId, out var binding) &&
        !string.IsNullOrWhiteSpace(binding.Instance.DisplaySlug) &&
        !string.IsNullOrWhiteSpace(service.ServiceSlug) &&
        string.Equals(
            binding.Instance.DisplaySlug,
            service.ServiceSlug,
            StringComparison.Ordinal);

    private static IEnumerable<string> EnumerateCatalogSpecSlugs(NyxIdServiceInstance instance)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serviceSlug in new[] { instance.CatalogServiceSlug, instance.DisplaySlug })
        {
            foreach (var candidate in EnumerateCatalogSpecSlugCandidates(serviceSlug))
            {
                if (seen.Add(candidate))
                    yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateCatalogSpecSlugCandidates(string serviceSlug)
    {
        if (string.IsNullOrWhiteSpace(serviceSlug))
            yield break;
        var normalized = serviceSlug.Trim();
        yield return normalized;
        const string apiPrefix = "api-";
        if (normalized.StartsWith(apiPrefix, StringComparison.OrdinalIgnoreCase) &&
            normalized.Length > apiPrefix.Length)
        {
            yield return normalized[apiPrefix.Length..];
        }
    }

    private static bool TryBuildCustomOpenApiProxyPath(
        NyxIdServiceInstance instance,
        out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(instance.OpenapiSpecUrl))
            return false;
        if (!Uri.TryCreate(instance.OpenapiSpecUrl.Trim(), UriKind.RelativeOrAbsolute, out var openApiUri))
            return false;

        if (openApiUri.IsAbsoluteUri)
        {
            if (!Uri.TryCreate(instance.EndpointUrl, UriKind.Absolute, out var endpointUri) ||
                !string.Equals(openApiUri.Scheme, endpointUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(openApiUri.Host, endpointUri.Host, StringComparison.OrdinalIgnoreCase) ||
                openApiUri.Port != endpointUri.Port ||
                !string.IsNullOrEmpty(openApiUri.Fragment))
            {
                return false;
            }

            path = openApiUri.PathAndQuery;
        }
        else
        {
            path = instance.OpenapiSpecUrl.Trim();
            if (!path.StartsWith("/", StringComparison.Ordinal))
                path = "/" + path;
        }

        return IsSafeCustomOpenApiProxyPath(path);
    }

    private static bool IsSafeCustomOpenApiProxyPath(string path)
    {
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        var resourcePath = queryIndex >= 0 ? path[..queryIndex] : path;
        return resourcePath is { Length: > 0 } &&
               resourcePath[0] == '/' &&
               !resourcePath.StartsWith("//", StringComparison.Ordinal) &&
               !resourcePath.Contains("..", StringComparison.Ordinal) &&
               !resourcePath.Contains('\\', StringComparison.Ordinal) &&
               !path.Contains('#', StringComparison.Ordinal) &&
               !path.Any(char.IsControl);
    }
}
