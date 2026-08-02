using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Request-local, read-only view of the caller's exact NyxID connected-service
/// instances. This narrow source is safe for the default chat surface; mutation,
/// routing, generic request, and dynamic operation tools remain in the explicit
/// <c>nyxid.connected_services</c> tool set.
/// </summary>
public sealed class NyxIdConnectedServiceInventoryToolSource : IAgentToolSource
{
    private static readonly JsonFormatter ResultFormatter = new(
        JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdConnectedServiceInventoryReader _reader;
    private readonly ILogger _logger;

    public NyxIdConnectedServiceInventoryToolSource(
        NyxIdToolOptions options,
        NyxIdServiceInstanceClient client,
        ILogger<NyxIdConnectedServiceInventoryToolSource>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reader = new NyxIdConnectedServiceInventoryReader(
            client ?? throw new ArgumentNullException(nameof(client)));
        _logger = logger ?? NullLogger<NyxIdConnectedServiceInventoryToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        var userToken = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(userToken))
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        return Task.FromResult<IReadOnlyList<IAgentTool>>
        ([
            new InventoryTool(
                this,
                userToken,
                AgentToolRequestContext.NyxIdOrgToken),
        ]);
    }

    private async Task<string> ReadAsync(
        string userToken,
        string? organizationToken,
        string argumentsJson,
        CancellationToken ct)
    {
        if (!HasNoArguments(argumentsJson))
            return JsonSerializer.Serialize(new { error = "invalid_arguments" });

        try
        {
            var result = await _reader.ReadAsync(userToken, organizationToken, ct).ConfigureAwait(false);
            return ResultFormatter.Format(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service inventory read failed");
            return JsonSerializer.Serialize(new { error = "inventory_query_unavailable" });
        }
    }

    private static bool HasNoArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   !document.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class InventoryTool(
        NyxIdConnectedServiceInventoryToolSource source,
        string userToken,
        string? organizationToken) : IAgentTool
    {
        private const string Schema =
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";

        public string Name => "nyxid_service_inventory";
        public string Description => "List the caller's active NyxID connected-service instances.";
        public string ParametersSchema => Schema;
        public bool IsReadOnly => true;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            NyxIdServiceInventoryReceiptFactory.Create(callId, toolName, resultJson);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.ReadAsync(userToken, organizationToken, argumentsJson, ct);
    }
}
