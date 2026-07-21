using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Foundation.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>
/// An <see cref="IAgentTool"/> bound to a single marked OpenAPI operation of a NyxID
/// connected service. Execution rebuilds the operation's HTTP request from the tool
/// arguments and sends it through NyxID's credential-injecting proxy, so credential
/// injection, audit, approval, node routing, and delegation stay owned by NyxID.
/// </summary>
public sealed class ConnectedServiceProxyTool : IAgentTool
{
    private readonly NyxIdApiClient _client;
    private readonly ConnectedServiceToolOperation _operation;
    private readonly string _serviceSlug;
    private readonly bool _preferOrgToken;
    private readonly ILogger _logger;

    public ConnectedServiceProxyTool(
        NyxIdApiClient client,
        string name,
        string serviceSlug,
        ConnectedServiceToolOperation operation,
        bool preferOrgToken,
        ToolPresentationDescriptor presentation,
        ILogger? logger = null)
    {
        _client = client;
        Name = name;
        _serviceSlug = serviceSlug;
        _operation = operation;
        _preferOrgToken = preferOrgToken;
        Presentation = presentation?.Clone() ?? throw new ArgumentNullException(nameof(presentation));
        _logger = logger ?? NullLogger.Instance;

        ParametersSchema = operation.BuildParametersSchema();
        ApprovalMode = ResolveApprovalMode(operation.Marker);
        IsReadOnly = operation.Marker?.ReadOnly
                     ?? operation.Method is "GET" or "HEAD";
        IsDestructive = ResolveDestructive(operation);

        var summary = !string.IsNullOrWhiteSpace(operation.Summary)
            ? operation.Summary
            : !string.IsNullOrWhiteSpace(operation.Marker?.Description)
                ? operation.Marker!.Description
                : operation.OperationId;
        Description = $"{summary} [NyxID service: {serviceSlug}; {operation.Method} {operation.PathTemplate}]";
    }

    public string Name { get; }

    public string Description { get; }

    public string ParametersSchema { get; }

    public ToolPresentationDescriptor Presentation { get; }

    public ToolApprovalMode ApprovalMode { get; }

    public bool IsReadOnly { get; }

    public bool IsDestructive { get; }

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var resourceUri = ResolveResourceUri(argumentsJson);
        return NyxIdProxyReceiptFactory.TryCreate(
            callId,
            toolName,
            _serviceSlug,
            serviceLabel: null,
            resourceUri,
            resultJson);
    }

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var userToken = AgentToolRequestContext.NyxIdAccessToken;
        var orgToken = AgentToolRequestContext.NyxIdOrgToken;
        var token = _preferOrgToken && !string.IsNullOrWhiteSpace(orgToken) ? orgToken : userToken;
        if (string.IsNullOrWhiteSpace(token))
            return Error("No NyxID access token available. User must be authenticated.");

        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return Error("Failed to parse tool arguments", args.ParseError);

        if (!TryBuildPath(args, out var path, out var pathError))
            return pathError!;
        if (!TryBuildQuery(args, out var query, out var queryError))
            return queryError!;
        if (!TryBuildHeaders(args, out var headers, out var headerError))
            return headerError!;

        string? body = null;
        if (_operation.BodyPropertyName is { } bodyName)
        {
            body = args.RawOrStr(bodyName);
            if (body is null && _operation.RequestBodyRequired)
                return Error($"Missing required request body '{bodyName}'");
        }

        var fullPath = query.Count > 0 ? $"{path}?{string.Join("&", query)}" : path;

        _logger.LogInformation(
            "[{Tool}] proxy {Method} slug={Slug} tokenSource={Source}",
            Name, _operation.Method, _serviceSlug, _preferOrgToken ? "org" : "user");

        return await _client.ProxyRequestAsync(token, _serviceSlug, fullPath, _operation.Method, body, headers, ct);
    }

    private bool TryBuildPath(ToolArgs args, out string path, out string? error)
    {
        path = _operation.PathTemplate;
        error = null;
        foreach (var parameter in _operation.Parameters)
        {
            if (parameter.In != ParameterLocation.Path)
                continue;

            var value = AsScalar(args.Element(parameter.Name));
            if (value is null)
            {
                error = Error($"Missing required path parameter '{parameter.Name}'");
                return false;
            }

            path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }

        if (path.Contains('{', StringComparison.Ordinal))
        {
            error = Error($"Unresolved path template '{_operation.PathTemplate}' — spec is missing a path parameter definition");
            return false;
        }

        return true;
    }

    private string? ResolveResourceUri(string argumentsJson)
    {
        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError || !TryBuildPath(args, out var path, out _))
            return null;
        if (!TryBuildQuery(args, out var query, out _))
            return path;

        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    private bool TryBuildQuery(ToolArgs args, out List<string> query, out string? error)
    {
        query = [];
        error = null;
        foreach (var parameter in _operation.Parameters)
        {
            if (parameter.In != ParameterLocation.Query)
                continue;

            var value = AsScalar(args.Element(parameter.Name));
            if (value is null)
            {
                if (parameter.Required)
                {
                    error = Error($"Missing required query parameter '{parameter.Name}'");
                    return false;
                }

                continue;
            }

            query.Add($"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(value)}");
        }

        return true;
    }

    private bool TryBuildHeaders(ToolArgs args, out Dictionary<string, string>? headers, out string? error)
    {
        headers = null;
        error = null;
        foreach (var parameter in _operation.Parameters)
        {
            if (parameter.In != ParameterLocation.Header)
                continue;

            var value = AsScalar(args.Element(parameter.Name));
            if (value is null)
            {
                if (parameter.Required)
                {
                    error = Error($"Missing required header parameter '{parameter.Name}'");
                    return false;
                }

                continue;
            }

            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers[parameter.Name] = value;
        }

        return true;
    }

    private static string? AsScalar(JsonElement? element)
    {
        if (element is not { } value)
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText(),
        };
    }

    /// <summary>
    /// Fail-closed destructive default: a write (any HTTP method other than the safe,
    /// side-effect-free GET/HEAD/OPTIONS) is treated as destructive unless the spec marker
    /// EXPLICITLY opts out with <c>destructive: false</c>. Read methods are never destructive.
    /// This flips the historical fail-open default (<c>?? false</c>) so an unmarked
    /// connected-service write requires approval under <see cref="ToolApprovalMode.Auto"/>.
    /// </summary>
    private static bool ResolveDestructive(ConnectedServiceToolOperation operation)
    {
        if (IsSafeMethod(operation.Method))
            return false;

        return operation.Marker?.Destructive ?? true;
    }

    private static bool IsSafeMethod(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static ToolApprovalMode ResolveApprovalMode(AevatarToolMarker? marker) =>
        marker?.Approval?.Trim().ToLowerInvariant() switch
        {
            "always" => ToolApprovalMode.AlwaysRequire,
            "never" => ToolApprovalMode.NeverRequire,
            _ => ToolApprovalMode.Auto,
        };

    private static string Error(string message, string? detail = null) =>
        detail is null
            ? $"{{\"error\":{JsonSerializer.Serialize(message)}}}"
            : $"{{\"error\":{JsonSerializer.Serialize(message)},\"detail\":{JsonSerializer.Serialize(detail)}}}";
}
