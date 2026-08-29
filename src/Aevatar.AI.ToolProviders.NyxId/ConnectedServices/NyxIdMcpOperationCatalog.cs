using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal sealed record NyxIdMcpService(
    string UserServiceId,
    string ServiceName,
    string ServiceSlug,
    IReadOnlyList<NyxIdMcpEndpoint> Endpoints,
    ExternalCapabilitySourceStamp Source);

internal sealed record NyxIdMcpEndpoint(
    string EndpointId,
    string Name,
    string Method,
    string PathTemplate,
    IReadOnlyList<ConnectedServiceToolParameter> Parameters,
    JsonNode? RequestBodySchema,
    bool RequestBodyRequired,
    string? RequestBodyMediaType,
    IReadOnlyList<string> ResponseMediaTypes,
    bool? BinaryArtifact,
    string? PublishedOperationDigest = null)
{
    public bool IsReadOnly => Method is "GET" or "HEAD" or "OPTIONS";

    public bool IsDestructive => Method == "DELETE";

    public NyxIdOperationExecutionPolicy ExecutionPolicy
    {
        get
        {
            var policy = new NyxIdOperationExecutionPolicy
            {
                Risk = IsDestructive
                    ? NyxIdOperationRisk.Destructive
                    : IsReadOnly
                        ? NyxIdOperationRisk.ReadOnly
                        : NyxIdOperationRisk.Write,
                Approval = NyxIdOperationApproval.None,
                EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
            };
            policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
            if (IsReadOnly)
                policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
            return policy;
        }
    }

    public string ContractDigest => PublishedOperationDigest ?? ExternalWorkflowCapabilityContractDigest.Compute(
        "nyxid-mcp-endpoint.v1",
        EndpointId,
        Method,
        PathTemplate,
        string.Join("\n---\n", Parameters
            .OrderBy(static parameter => parameter.In)
            .ThenBy(static parameter => parameter.Name, StringComparer.Ordinal)
            .Select(static parameter => string.Join(
                "\n",
                parameter.Name,
                parameter.In,
                parameter.Required,
                parameter.Schema?.ToJsonString() ?? "string"))),
        RequestBodySchema?.ToJsonString(),
        RequestBodyRequired.ToString(),
        RequestBodyMediaType,
        string.Join("\n", ResponseMediaTypes.Order(StringComparer.Ordinal)),
        BinaryArtifact?.ToString() ?? "unknown",
        ExecutionPolicy.Risk.ToString(),
        ExecutionPolicy.Approval.ToString(),
        BinaryArtifact is true ? "file-artifact" : "text");
}

internal sealed record NyxIdMcpCatalogIssue(
    ExternalCapabilityDiscoveryDiagnosticCode Code,
    string SafeMessage,
    string? UserServiceId = null,
    string? EndpointId = null);

internal sealed record NyxIdMcpCatalogRead(
    IReadOnlyList<NyxIdMcpService> Services,
    ExternalWorkflowCapabilityDiscoveryResult Discovery,
    IReadOnlyList<NyxIdMcpCatalogIssue> Issues,
    ExternalCapabilitySourceStamp Source,
    bool AccessDenied,
    bool SourceUnavailable);

internal static class NyxIdMcpOperationCatalog
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal)
    {
        "GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE",
    };

    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "If-Match", "If-None-Match",
    };

    private static readonly HashSet<string> SupportedParameterKeywords = new(StringComparer.Ordinal)
    {
        "name", "in", "description", "required", "deprecated", "schema",
        "example", "examples",
    };

    public static NyxIdMcpCatalogRead Parse(
        string response,
        string sourceSuffix,
        DateTimeOffset observedAt,
        TimeSpan freshnessWindow)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response);
        }
        catch (JsonException)
        {
            return SourceFailure(
                sourceSuffix,
                observedAt,
                freshnessWindow,
                ExternalWorkflowCapabilityContractDigest.Compute("invalid-json", response),
                accessDenied: false);
        }

        using (document)
        {
            var root = document.RootElement;
            var userId = ExactString(root, "user_id");
            var catalogDigest = ExactString(root, "catalog_digest");
            var source = BuildSource(
                sourceSuffix,
                userId,
                observedAt,
                freshnessWindow,
                IsCatalogDigest(catalogDigest)
                    ? catalogDigest!
                    : ExternalWorkflowCapabilityContractDigest.Compute(Canonicalize(root)));
            if (NyxIdUserServiceListJson.IsErrorEnvelope(root, out var status))
                return SourceFailure(source, status is 401 or 403);
            if (root.ValueKind != JsonValueKind.Object ||
                !string.Equals(ExactString(root, "contract_version"), "1.0", StringComparison.Ordinal) ||
                !IsCatalogDigest(catalogDigest) ||
                userId is null ||
                !root.TryGetProperty("services", out var servicesElement) ||
                servicesElement.ValueKind != JsonValueKind.Array)
            {
                return SourceFailure(source, accessDenied: false);
            }

            return ParseServices(servicesElement, source);
        }
    }

    private static NyxIdMcpCatalogRead ParseServices(
        JsonElement servicesElement,
        ExternalCapabilitySourceStamp source)
    {
        var entries = servicesElement.EnumerateArray().ToArray();
        var candidateCount = entries.Sum(CountCandidates);
        var issues = new List<NyxIdMcpCatalogIssue>();
        var services = new List<NyxIdMcpService>();
        var duplicateIds = entries
            .Select(static entry => ExactString(entry, "service_id"))
            .Where(static id => id is not null)
            .GroupBy(static id => id!, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var serviceId = ExactString(entry, "service_id");
            var rejectedCount = CountCandidates(entry);
            if (serviceId is null)
            {
                AddIssues(issues, ExternalCapabilityDiscoveryDiagnosticCode.InvalidServiceIdentity,
                    "NyxID published a service without an exact service_id.", rejectedCount);
                continue;
            }
            if (duplicateIds.Contains(serviceId))
            {
                AddIssues(issues, ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousServiceIdentity,
                    "NyxID published duplicate service_id values.", rejectedCount, serviceId);
                continue;
            }
            if (!TryReadBool(entry, "is_user_service", out var isUserService) ||
                !TryReadBool(entry, "is_generic_proxy", out var isGenericProxy))
            {
                AddIssues(issues, ExternalCapabilityDiscoveryDiagnosticCode.InvalidServiceIdentity,
                    "NyxID did not publish complete service-kind facts.", rejectedCount, serviceId);
                continue;
            }
            if (!isUserService)
            {
                AddIssues(issues, ExternalCapabilityDiscoveryDiagnosticCode.NoExactUserService,
                    "Platform services are not eligible for exact NyxID workflow admission.", rejectedCount, serviceId);
                continue;
            }
            if (isGenericProxy)
            {
                AddIssues(issues, ExternalCapabilityDiscoveryDiagnosticCode.GenericProxyRejected,
                    "Generic proxy services are not eligible for workflow admission.", rejectedCount, serviceId);
                continue;
            }

            var slug = ExactString(entry, "service_slug");
            var name = ExactString(entry, "service_name");
            if (slug is null || name is null ||
                !entry.TryGetProperty("endpoints", out var endpointsElement) ||
                endpointsElement.ValueKind != JsonValueKind.Array)
            {
                AddIssues(issues, ExternalCapabilityDiscoveryDiagnosticCode.InvalidServiceIdentity,
                    "NyxID published an incomplete exact UserService contract.", rejectedCount, serviceId);
                continue;
            }

            var endpoints = ParseEndpoints(serviceId, slug, endpointsElement, issues);
            services.Add(new NyxIdMcpService(serviceId, name, slug, endpoints, source.Clone()));
        }

        if (services.Count == 0 && !issues.Any(static issue =>
                issue.Code == ExternalCapabilityDiscoveryDiagnosticCode.NoExactUserService))
        {
            issues.Add(new NyxIdMcpCatalogIssue(
                ExternalCapabilityDiscoveryDiagnosticCode.NoExactUserService,
                "NyxID did not publish an exact user-managed service eligible for workflow admission."));
        }

        var capabilities = services
            .SelectMany(service => service.Endpoints.Select(endpoint => BuildDescriptor(service, endpoint)))
            .ToArray();
        var discovery = BuildDiscovery(
            capabilities,
            candidateCount,
            Math.Max(0, candidateCount - capabilities.Length),
            issues,
            source);
        return new NyxIdMcpCatalogRead(services, discovery, issues, source, false, false);
    }

    private static IReadOnlyList<NyxIdMcpEndpoint> ParseEndpoints(
        string serviceId,
        string serviceSlug,
        JsonElement endpointsElement,
        ICollection<NyxIdMcpCatalogIssue> issues)
    {
        var entries = endpointsElement.EnumerateArray().ToArray();
        if (entries.Length == 0)
        {
            issues.Add(new NyxIdMcpCatalogIssue(
                ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity,
                "NyxID published an exact UserService without an endpoint contract.",
                serviceId));
            return [];
        }
        var duplicateIds = entries
            .Select(static entry => ExactString(entry, "endpoint_id"))
            .Where(static id => id is not null)
            .GroupBy(static id => id!, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var endpoints = new List<NyxIdMcpEndpoint>();
        foreach (var entry in entries)
        {
            var endpointId = ExactString(entry, "endpoint_id");
            if (endpointId is null)
            {
                issues.Add(new NyxIdMcpCatalogIssue(
                    ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity,
                    "NyxID published an endpoint without an exact endpoint_id.",
                    serviceId));
                continue;
            }
            if (duplicateIds.Contains(endpointId))
            {
                issues.Add(new NyxIdMcpCatalogIssue(
                    ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousEndpointIdentity,
                    "NyxID published duplicate endpoint_id values for one service.",
                    serviceId,
                    endpointId));
                continue;
            }

            var parsed = ParseEndpoint(serviceId, endpointId, entry, issues);
            if (parsed is null)
                continue;
            try
            {
                _ = NyxIdOperationAdmissionProofBuilder.Build(
                    serviceId, serviceSlug, parsed, parsed.ContractDigest);
                if (parsed.Parameters.Any(static parameter =>
                        !IsScalarParameterSchema(parameter.Schema)))
                {
                    UnsupportedParameters(issues, serviceId, endpointId);
                    continue;
                }
                endpoints.Add(parsed);
            }
            catch (NyxIdOperationSchemaUnsupportedException)
            {
                issues.Add(new NyxIdMcpCatalogIssue(
                    ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema,
                    "The endpoint schema is outside the supported workflow contract subset.",
                    serviceId,
                    endpointId));
            }
        }
        return endpoints;
    }

    private static NyxIdMcpEndpoint? ParseEndpoint(
        string serviceId,
        string endpointId,
        JsonElement entry,
        ICollection<NyxIdMcpCatalogIssue> issues)
    {
        var method = ExactString(entry, "method")?.ToUpperInvariant();
        var path = ExactString(entry, "path");
        if (method is null || !HttpMethods.Contains(method) ||
            !TryReadPathPlaceholders(path, out var pathPlaceholders))
        {
            issues.Add(new NyxIdMcpCatalogIssue(
                ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity,
                "NyxID published an endpoint with an unsupported method or path.",
                serviceId, endpointId));
            return null;
        }

        var parameters = ParseParameters(entry, serviceId, endpointId, issues);
        if (parameters is null)
            return null;
        if (!pathPlaceholders.SetEquals(parameters
                .Where(static parameter => parameter.In == ParameterLocation.Path)
                .Select(static parameter => parameter.Name)))
        {
            issues.Add(new NyxIdMcpCatalogIssue(
                ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity,
                "NyxID published a path template whose placeholders do not match its path parameters.",
                serviceId, endpointId));
            return null;
        }
        var body = ParseRequestBody(entry, serviceId, endpointId, issues);
        if (!body.Supported)
            return null;
        var response = ParseResponse(entry, serviceId, endpointId, method, issues);
        if (!response.Supported)
            return null;

        return new NyxIdMcpEndpoint(
            endpointId,
            ExactString(entry, "name") ?? endpointId,
            method,
            path!,
            parameters,
            body.Schema,
            body.Required,
            body.MediaType,
            response.MediaTypes,
            response.BinaryArtifact,
            IsCatalogDigest(ExactString(entry, "operation_digest"))
                ? ExactString(entry, "operation_digest")
                : null);
    }

    private static IReadOnlyList<ConnectedServiceToolParameter>? ParseParameters(
        JsonElement endpoint,
        string serviceId,
        string endpointId,
        ICollection<NyxIdMcpCatalogIssue> issues)
    {
        if (!endpoint.TryGetProperty("parameters", out var value) || value.ValueKind == JsonValueKind.Null)
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            return UnsupportedParameters(issues, serviceId, endpointId);

        var parameters = new List<ConnectedServiceToolParameter>();
        var identities = new HashSet<(string Name, ParameterLocation Location)>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                item.EnumerateObject().Any(static property =>
                    !SupportedParameterKeywords.Contains(property.Name)))
            {
                return UnsupportedParameters(issues, serviceId, endpointId);
            }

            var name = ExactString(item, "name");
            var locationText = ExactString(item, "in")?.ToLowerInvariant();
            if (name is null || locationText == "cookie" || !TryMapLocation(locationText, out var location))
                return UnsupportedParameters(issues, serviceId, endpointId);
            if (!IsSupportedParameterName(name, location))
                return UnsupportedParameters(issues, serviceId, endpointId);
            if (!TryReadOptionalBool(item, "required", out var required))
                return UnsupportedParameters(issues, serviceId, endpointId);
            if (location == ParameterLocation.Header &&
                (!AllowedHeaders.Contains(name) || NyxIdProxyHeaderPolicy.IsSensitive(name)))
            {
                return UnsupportedParameters(issues, serviceId, endpointId);
            }
            var identityName = location == ParameterLocation.Header ? name.ToUpperInvariant() : name;
            if (!identities.Add((identityName, location)))
                return UnsupportedParameters(issues, serviceId, endpointId);

            JsonNode? schema = null;
            if (item.TryGetProperty("schema", out var schemaElement) &&
                schemaElement.ValueKind != JsonValueKind.Null)
            {
                if (schemaElement.ValueKind != JsonValueKind.Object)
                    return UnsupportedSchema(issues, serviceId, endpointId);
                schema = JsonNode.Parse(Canonicalize(schemaElement));
            }
            parameters.Add(new ConnectedServiceToolParameter(
                name, location, required || location == ParameterLocation.Path, schema,
                OptionalString(item, "description")));
        }
        return parameters;
    }

    private static bool IsScalarParameterSchema(JsonNode? schema)
    {
        if (schema is not JsonObject value)
            return true;
        if (value["type"] is null)
            return value["properties"] is null;
        return value["type"] is JsonValue type &&
               type.TryGetValue<string>(out var text) &&
               text.Trim().ToLowerInvariant() is "string" or "integer" or "number" or "boolean";
    }

    private static bool IsSupportedParameterName(string name, ParameterLocation location) =>
        location != ParameterLocation.Query ||
        !name.StartsWith("_nyxid_", StringComparison.OrdinalIgnoreCase);

    private static (bool Supported, JsonNode? Schema, bool Required, string? MediaType) ParseRequestBody(
        JsonElement endpoint,
        string serviceId,
        string endpointId,
        ICollection<NyxIdMcpCatalogIssue> issues)
    {
        if (!TryReadBool(endpoint, "request_body_required", out var required))
            return UnsupportedRequestBody(issues, serviceId, endpointId);

        string? mediaType = null;
        if (endpoint.TryGetProperty("request_content_type", out var mediaTypeElement) &&
            mediaTypeElement.ValueKind != JsonValueKind.Null)
        {
            if (mediaTypeElement.ValueKind != JsonValueKind.String)
                return UnsupportedRequestBody(issues, serviceId, endpointId);
            mediaType = mediaTypeElement.GetString()?.Trim();
        }
        var hasSchema = endpoint.TryGetProperty("request_body_schema", out var schemaElement) &&
                        schemaElement.ValueKind != JsonValueKind.Null;
        if (!hasSchema && !required && mediaType is null)
            return (true, null, false, null);
        if (!hasSchema || schemaElement.ValueKind != JsonValueKind.Object ||
            !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            endpoint.TryGetProperty("method", out var methodElement) &&
            methodElement.ValueKind == JsonValueKind.String &&
            methodElement.GetString()?.ToUpperInvariant() is "GET" or "HEAD")
        {
            issues.Add(new NyxIdMcpCatalogIssue(
                ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody,
                "The endpoint request body is not a verifiable JSON contract.",
                serviceId, endpointId));
            return (false, null, required, mediaType);
        }
        return (true, JsonNode.Parse(Canonicalize(schemaElement)), required, "application/json");
    }

    private static (bool Supported, JsonNode? Schema, bool Required, string? MediaType)
        UnsupportedRequestBody(
            ICollection<NyxIdMcpCatalogIssue> issues,
            string serviceId,
            string endpointId)
    {
        issues.Add(new NyxIdMcpCatalogIssue(
            ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody,
            "The endpoint request body is not a verifiable JSON contract.",
            serviceId, endpointId));
        return (false, null, false, null);
    }

    private static (bool Supported, IReadOnlyList<string> MediaTypes, bool? BinaryArtifact)
        ParseResponse(
            JsonElement endpoint,
            string serviceId,
            string endpointId,
            string method,
            ICollection<NyxIdMcpCatalogIssue> issues)
    {
        if (!endpoint.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("content_types", out var contentTypes) ||
            contentTypes.ValueKind != JsonValueKind.Array ||
            !response.TryGetProperty("binary_artifact", out var binaryArtifact))
        {
            return UnsupportedResponse(issues, serviceId, endpointId);
        }

        var mediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in contentTypes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { Length: > 0 } mediaType ||
                !string.Equals(mediaType, mediaType.Trim(), StringComparison.Ordinal) ||
                !MediaTypeHeaderValue.TryParse(mediaType, out _))
            {
                return UnsupportedResponse(issues, serviceId, endpointId);
            }
            mediaTypes.Add(mediaType);
        }

        bool? binary = binaryArtifact.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null,
        };
        if (binaryArtifact.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) ||
            binary is true && !string.Equals(method, "GET", StringComparison.Ordinal))
        {
            return UnsupportedResponse(issues, serviceId, endpointId);
        }

        return (true, mediaTypes.Order(StringComparer.Ordinal).ToArray(), binary);
    }

    private static (bool Supported, IReadOnlyList<string> MediaTypes, bool? BinaryArtifact)
        UnsupportedResponse(
            ICollection<NyxIdMcpCatalogIssue> issues,
            string serviceId,
            string endpointId)
    {
        issues.Add(new NyxIdMcpCatalogIssue(
            ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedResponse,
            "The endpoint response is outside the supported workflow contract subset.",
            serviceId,
            endpointId));
        return (false, [], null);
    }

    private static ExternalWorkflowCapabilityDescriptor BuildDescriptor(
        NyxIdMcpService service,
        NyxIdMcpEndpoint endpoint) => new()
    {
        Selector = new ExternalWorkflowCapabilitySelector
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = service.UserServiceId,
                EndpointId = endpoint.EndpointId,
            },
        },
        DisplayName = $"{service.ServiceName} / {endpoint.Name}",
        ReadOnly = endpoint.IsReadOnly,
        Destructive = endpoint.IsDestructive,
        Source = service.Source.Clone(),
    };

    private static ExternalWorkflowCapabilityDiscoveryResult BuildDiscovery(
        IEnumerable<ExternalWorkflowCapabilityDescriptor> capabilities,
        int candidateCount,
        int rejectedCount,
        IEnumerable<NyxIdMcpCatalogIssue> issues,
        ExternalCapabilitySourceStamp source)
    {
        var result = new ExternalWorkflowCapabilityDiscoveryResult
        {
            CandidateCount = candidateCount,
            RejectedCount = rejectedCount,
        };
        result.Capabilities.Add(capabilities);
        foreach (var group in issues.GroupBy(
                     static issue => (issue.Code, issue.SafeMessage)))
        {
            result.Diagnostics.Add(new ExternalCapabilityDiscoveryDiagnostic
            {
                Code = group.Key.Code,
                SafeMessage = group.Key.SafeMessage,
                Count = group.Count(),
                Source = source.Clone(),
            });
        }
        return result;
    }

    private static NyxIdMcpCatalogRead SourceFailure(
        string sourceSuffix,
        DateTimeOffset observedAt,
        TimeSpan freshnessWindow,
        string digest,
        bool accessDenied) =>
        SourceFailure(BuildSource(sourceSuffix, null, observedAt, freshnessWindow, digest), accessDenied);

    private static NyxIdMcpCatalogRead SourceFailure(
        ExternalCapabilitySourceStamp source,
        bool accessDenied)
    {
        var issue = new NyxIdMcpCatalogIssue(
            ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable,
            accessDenied
                ? "The NyxID MCP catalog is not accessible to the current caller."
                : "The NyxID MCP catalog is currently unavailable.");
        return new NyxIdMcpCatalogRead(
            [],
            BuildDiscovery([], 0, 0, [issue], source),
            [issue],
            source,
            accessDenied,
            !accessDenied);
    }

    private static ExternalCapabilitySourceStamp BuildSource(
        string sourceSuffix,
        string? userId,
        DateTimeOffset observedAt,
        TimeSpan freshnessWindow,
        string digest) => new()
    {
        SourceKind = ExternalCapabilitySourceKind.NyxIdMcpConfig,
        SourceId = string.IsNullOrWhiteSpace(userId)
            ? $"nyxid-mcp-config:{sourceSuffix}"
            : $"nyxid-mcp-config:{sourceSuffix}:{userId}",
        SourceVersion = 0,
        ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
        FreshUntil = Timestamp.FromDateTimeOffset(observedAt.Add(freshnessWindow)),
        ContentDigest = digest,
    };

    private static int CountCandidates(JsonElement service) =>
        service.ValueKind == JsonValueKind.Object &&
        service.TryGetProperty("endpoints", out var endpoints) &&
        endpoints.ValueKind == JsonValueKind.Array
            ? Math.Max(1, endpoints.GetArrayLength())
            : 1;

    private static void AddIssues(
        ICollection<NyxIdMcpCatalogIssue> issues,
        ExternalCapabilityDiscoveryDiagnosticCode code,
        string message,
        int count,
        string? serviceId = null)
    {
        for (var index = 0; index < Math.Max(1, count); index++)
            issues.Add(new NyxIdMcpCatalogIssue(code, message, serviceId));
    }

    private static IReadOnlyList<ConnectedServiceToolParameter>? UnsupportedParameters(
        ICollection<NyxIdMcpCatalogIssue> issues, string serviceId, string endpointId)
    {
        issues.Add(new NyxIdMcpCatalogIssue(
            ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter,
            "The endpoint contains an unsupported parameter contract.",
            serviceId, endpointId));
        return null;
    }

    private static IReadOnlyList<ConnectedServiceToolParameter>? UnsupportedSchema(
        ICollection<NyxIdMcpCatalogIssue> issues, string serviceId, string endpointId)
    {
        issues.Add(new NyxIdMcpCatalogIssue(
            ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema,
            "The endpoint schema is outside the supported workflow contract subset.",
            serviceId, endpointId));
        return null;
    }

    private static bool TryMapLocation(string? value, out ParameterLocation location)
    {
        location = value switch
        {
            "path" => ParameterLocation.Path,
            "query" => ParameterLocation.Query,
            "header" => ParameterLocation.Header,
            _ => default,
        };
        return value is "path" or "query" or "header";
    }

    private static bool TryReadPathPlaceholders(
        string? path,
        out HashSet<string> placeholders)
    {
        placeholders = new HashSet<string>(StringComparer.Ordinal);
        if (!IsSafePath(path))
            return false;

        for (var index = 0; index < path!.Length; index++)
        {
            if (path[index] == '}')
                return false;
            if (path[index] != '{')
                continue;

            var close = path.IndexOf('}', index + 1);
            if (close < 0)
                return false;
            var name = path[(index + 1)..close];
            if (string.IsNullOrWhiteSpace(name) ||
                !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
                name.Contains('{', StringComparison.Ordinal))
            {
                return false;
            }

            placeholders.Add(name);
            index = close;
        }

        return true;
    }

    private static bool IsSafePath(string? path)
    {
        if (path is not { Length: > 0 } || path[0] != '/' ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('?', StringComparison.Ordinal) ||
            path.Contains('#', StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Any(char.IsControl))
        {
            return false;
        }

        foreach (var encoded in (ReadOnlySpan<string>)[
                     "%2f", "%5c", "%00", "%2e%2e", "%252f", "%255c", "%252e%252e"])
        {
            if (path.Contains(encoded, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string? ExactString(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return !string.IsNullOrWhiteSpace(text) &&
               string.Equals(text, text.Trim(), StringComparison.Ordinal)
            ? text
            : null;
    }

    private static bool IsCatalogDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? OptionalString(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    }

    private static bool TryReadBool(JsonElement owner, string name, out bool value)
    {
        value = false;
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        return element.ValueKind == JsonValueKind.False;
    }

    private static bool TryReadOptionalBool(JsonElement owner, string name, out bool value)
    {
        value = false;
        return !owner.TryGetProperty(name, out _) || TryReadBool(owner, name, out value);
    }

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
