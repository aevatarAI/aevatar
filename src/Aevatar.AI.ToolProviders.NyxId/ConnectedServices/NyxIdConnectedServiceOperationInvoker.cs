using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    NyxIdConnectedServiceDocumentRequest? DocumentRequest = null,
    NyxIdConnectedServiceRawRequest? RawRequest = null);

public sealed record NyxIdConnectedServiceDocumentRequest(
    string Method,
    string RelativePath,
    string RequestArgumentsJson,
    NyxIdRecommendedSkillRef SkillRef);

public sealed record NyxIdConnectedServiceRawRequest(
    string Method,
    string RelativePath,
    string RequestArgumentsJson);

public sealed record NyxIdConnectedServiceOperationInvokeResult(
    bool IsSuccess,
    AgentToolTerminalOutcome? Outcome,
    string FailureCode,
    IReadOnlyList<NyxIdRecommendedSkillRef>? SuggestedSkillRefs = null)
{
    public static NyxIdConnectedServiceOperationInvokeResult Success(AgentToolTerminalOutcome outcome) =>
        new(true, outcome, string.Empty);

    public static NyxIdConnectedServiceOperationInvokeResult Failure(
        string failureCode,
        IReadOnlyList<NyxIdRecommendedSkillRef>? suggestedSkillRefs = null) =>
        new(false, null, failureCode, suggestedSkillRefs);
}

public sealed class NyxIdConnectedServiceOperationInvoker
{
    private static readonly TimeSpan CatalogFreshnessWindow = TimeSpan.FromMinutes(5);
    private const int CustomOpenApiMaxBytes = 1024 * 1024;
    private const int MaxReadSourceBytes = 16 * 1024;

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
                        matchedBindings[0],
                        invocation.DocumentRequest,
                        callId,
                        toolName,
                        ct)
                    .ConfigureAwait(false);
            }

            if (invocation.RawRequest is not null)
            {
                if (matchedBindings.Length > 1)
                    return NyxIdConnectedServiceOperationInvokeResult.Failure("operation_ambiguous");
                return await InvokeRawDelegatedRequestAsync(
                        context,
                        matchedBindings[0],
                        invocation.RawRequest,
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
            {
                var suggestedSkillRefs = matchedBindings
                    .SelectMany(static binding => binding.Instance.RecommendedSkillRefs)
                    .Where(static skillRef => skillRef.Source == NyxIdRecommendedSkillSource.Ornn)
                    .Select(static skillRef => skillRef.Clone())
                    .ToArray();
                return suggestedSkillRefs.Length > 0
                    ? NyxIdConnectedServiceOperationInvokeResult.Failure("document_request_required", suggestedSkillRefs)
                    : NyxIdConnectedServiceOperationInvokeResult.Failure("operation_not_visible");
            }
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
            return await ExecuteThroughOperationToolAsync(
                    context,
                    match.Binding,
                    admission,
                    invocation.OperationArgumentsJson,
                    match.Service.ServiceName,
                    match.Endpoint.Name,
                    callId,
                    toolName,
                    readBackPlan,
                    ct)
                .ConfigureAwait(false);
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
        NyxIdServiceInstanceBinding binding,
        NyxIdConnectedServiceDocumentRequest request,
        string callId,
        string toolName,
        CancellationToken ct)
    {
        if (!HasExactRecommendedSkillRef(binding.Instance, request.SkillRef))
            return NyxIdConnectedServiceOperationInvokeResult.Failure("document_request_not_admitted");
        return await InvokeAuthoredRequestAsync(
                context,
                binding,
                request.Method,
                request.RelativePath,
                request.RequestArgumentsJson,
                invalidFailureCode: "document_request_invalid",
                callId,
                toolName,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<NyxIdConnectedServiceOperationInvokeResult> InvokeRawDelegatedRequestAsync(
        AgentToolExecutionContext context,
        NyxIdServiceInstanceBinding binding,
        NyxIdConnectedServiceRawRequest request,
        string callId,
        string toolName,
        CancellationToken ct)
    {
        return await InvokeAuthoredRequestAsync(
                context,
                binding,
                request.Method,
                request.RelativePath,
                request.RequestArgumentsJson,
                invalidFailureCode: "raw_request_invalid",
                callId,
                toolName,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<NyxIdConnectedServiceOperationInvokeResult> InvokeAuthoredRequestAsync(
        AgentToolExecutionContext context,
        NyxIdServiceInstanceBinding binding,
        string method,
        string relativePath,
        string requestArgumentsJson,
        string invalidFailureCode,
        string callId,
        string toolName,
        CancellationToken ct)
    {
        if (!TryBuildAuthoredRequestAdmission(
                binding.Instance,
                method,
                relativePath,
                requestArgumentsJson,
                out var admission))
        {
            return NyxIdConnectedServiceOperationInvokeResult.Failure(invalidFailureCode);
        }

        return await ExecuteThroughOperationToolAsync(
                context,
                binding,
                admission,
                requestArgumentsJson,
                FirstNonEmpty(binding.Instance.Label, binding.Instance.DisplaySlug, binding.Instance.CatalogServiceSlug),
                $"{admission.HttpMethod} {admission.PathTemplate}",
                callId,
                toolName,
                readBackPlan: null,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<NyxIdConnectedServiceOperationInvokeResult> ExecuteThroughOperationToolAsync(
        AgentToolExecutionContext context,
        NyxIdServiceInstanceBinding binding,
        AgentToolOperationAdmission admission,
        string runtimeArgumentsJson,
        string serviceLabel,
        string operationLabel,
        string callId,
        string toolName,
        NyxIdConnectedServiceReadBackPlan? readBackPlan,
        CancellationToken ct)
    {
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
        using var scope = AgentToolContextScope.Push(context);
        var operationTool = new NyxIdConnectedServiceOperationTool(
            proxy,
            admission,
            serviceLabel,
            operationLabel,
            FirstNonEmpty(binding.Instance.Label, binding.Instance.DisplaySlug, binding.Instance.CatalogServiceSlug),
            readinessCapabilityId: null,
            accessTokenSource: binding.Instance.AccessTokenSource,
            readBackPlan: readBackPlan,
            maxReadSourceBytes: MaxReadSourceBytes,
            maxReadProjectionBytes: MaxReadSourceBytes);
        var outcome = await operationTool.ExecuteWithOutcomeAsync(
                callId,
                toolName,
                runtimeArgumentsJson,
                ct)
            .ConfigureAwait(false);
        return NyxIdConnectedServiceOperationInvokeResult.Success(outcome);
    }

    private static bool HasExactRecommendedSkillRef(
        NyxIdServiceInstance instance,
        NyxIdRecommendedSkillRef requestedRef) =>
        instance.RecommendedSkillRefs.Any(skillRef =>
            skillRef.Source == requestedRef.Source &&
            string.Equals(skillRef.SkillId, requestedRef.SkillId, StringComparison.Ordinal) &&
            string.Equals(skillRef.LiteralVersion, requestedRef.LiteralVersion, StringComparison.Ordinal) &&
            string.Equals(skillRef.ManifestDigest, requestedRef.ManifestDigest, StringComparison.Ordinal));

    private static bool TryBuildAuthoredRequestAdmission(
        NyxIdServiceInstance instance,
        string requestMethod,
        string relativePath,
        string requestArgumentsJson,
        out AgentToolOperationAdmission admission)
    {
        admission = null!;
        var method = NormalizeDocumentRequestMethod(requestMethod);
        if (method is null || !TryNormalizeDocumentRequestPath(relativePath, out var pathTemplate))
            return false;

        if (!TryReadDocumentRuntimeArguments(
                requestArgumentsJson,
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
                AgentToolOperationApproval.None,
                AgentToolOperationEnforcementOwner.Aevatar,
                [AgentToolOperationExecutionMode.Interactive]),
            CatalogDigest: string.Empty,
            CatalogServiceSlug: instance.CatalogServiceSlug);
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
                if (headerParameters.Any(NyxIdProxyHeaderPolicy.IsSensitive))
                    return false;
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

    private async Task<IReadOnlyList<NyxIdServiceInstanceBinding>> ReadBindingsAsync(
        AgentToolExecutionContext context,
        string executionToken,
        string inventoryToken,
        CancellationToken ct)
    {
        if (context.Credentials.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.AgentKey)
        {
            try
            {
                var bindings = await _client.DiscoverAgentKeyAsync(executionToken, ct).ConfigureAwait(false);
                if (NyxIdAgentKeyInventoryFallback.TrySupplementMissingRecommendedSkillRefs(
                        _options,
                        executionToken,
                        _logger,
                        bindings))
                {
                    _logger.LogWarning(
                        "NyxID Agent Key connected-service discovery is missing recommended skill refs; using configured local fallback refs for operation invocation");
                }

                return bindings;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (NyxIdServiceInventoryContractException)
            {
                throw;
            }
            catch (Exception ex) when (NyxIdAgentKeyInventoryFallback.TryReadBindings(
                       _options,
                       executionToken,
                       _logger,
                       out var fallbackBindings))
            {
                _logger.LogWarning(
                    ex,
                    "NyxID Agent Key connected-service discovery failed; using configured local inventory fallback for operation invocation");
                return fallbackBindings;
            }
        }

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
