using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Channel-only connected-service inventory source. It binds discovery to the
/// channel sender: a verified sender-route token is reused when available;
/// otherwise a narrow request-local inventory capability is re-issued from the
/// sender's typed binding identity. Ambient bot-owner credentials are never used.
/// </summary>
public sealed class ChannelNyxIdConnectedServiceInventoryToolSource : IAgentToolSource
{
    private static readonly JsonFormatter ResultFormatter = new(
        JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly NyxIdToolOptions? _options;
    private readonly INyxIdApiClientFactory? _apiClientFactory;
    private readonly INyxIdConnectedServiceInventoryCapabilityIssuer? _capabilityIssuer;
    private readonly ILogger _logger;

    public ChannelNyxIdConnectedServiceInventoryToolSource(
        IAgentToolExecutionPort toolExecutionPort,
        NyxIdToolOptions? options = null,
        INyxIdApiClientFactory? apiClientFactory = null,
        INyxIdConnectedServiceInventoryCapabilityIssuer? capabilityIssuer = null,
        ILogger<ChannelNyxIdConnectedServiceInventoryToolSource>? logger = null)
    {
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _options = options;
        _apiClientFactory = apiClientFactory;
        _capabilityIssuer = capabilityIssuer;
        _logger = logger ?? NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (context is null || bindingId is null)
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        return Task.FromResult<IReadOnlyList<IAgentTool>>([new SenderInventoryTool(this)]);
    }

    private async Task<string> ExecuteInventoryAsync(string argumentsJson, CancellationToken ct)
    {
        if (!HasOnlyListArguments(argumentsJson))
            return JsonSerializer.Serialize(new { error = "invalid_arguments" });

        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (context is null || bindingId is null)
            return InventoryFailure("inventory_capability_unavailable");

        var strictSenderToken = Normalize(context.Credentials.SenderNyxIdAccessToken);
        if (strictSenderToken is not null)
        {
            return await ExecuteWithSenderTokenAsync(context, strictSenderToken, argumentsJson, ct)
                .ConfigureAwait(false);
        }

        if (_capabilityIssuer is null || !TryBuildSubject(context, out var subject))
            return InventoryFailure("inventory_capability_unavailable");

        try
        {
            var capability = await _capabilityIssuer
                .IssueByBindingIdAsync(subject, bindingId, ct)
                .ConfigureAwait(false);
            var inventoryToken = Normalize(capability.AccessToken);
            if (inventoryToken is null)
                return InventoryFailure("inventory_capability_unavailable");

            return await ExecuteWithSenderTokenAsync(context, inventoryToken, argumentsJson, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BindingRevokedException ex)
        {
            _logger.LogWarning(
                ex,
                "NyxID connected-service inventory binding was revoked. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return InventoryFailure("inventory_binding_revoked");
        }
        catch (BindingScopeMismatchException ex)
        {
            _logger.LogWarning(
                ex,
                "NyxID connected-service inventory scope is unavailable. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return InventoryFailure("inventory_scope_unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "NyxID connected-service inventory capability issue failed. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return InventoryFailure("inventory_capability_unavailable");
        }
    }

    private async Task<string> ExecuteWithSenderTokenAsync(
        AgentToolExecutionContext context,
        string token,
        string argumentsJson,
        CancellationToken ct)
    {
        if (_options is null || _apiClientFactory is null)
            return InventoryFailure("inventory_source_unavailable");

        try
        {
            var reader = new NyxIdConnectedServiceInventoryReader(
                new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient()));
            var senderContext = context with
            {
                Credentials = new AgentToolCredentials(
                    token,
                    token,
                    context.Credentials.SenderNyxIdAccessToken),
                Request = context.Request with
                {
                    CallId = CreateInventoryReadCallId(context.Request.CallId),
                },
            };
            var outcome = await _toolExecutionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    new SenderInventoryReaderTool(this, reader, token),
                    argumentsJson,
                    senderContext,
                    AgentToolApprovalContinuationMode.None,
                    ApprovalGrant: null),
                ct).ConfigureAwait(false);
            return outcome.ResultJson;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service inventory source creation failed");
            return InventoryFailure("inventory_source_unavailable");
        }
    }

    private async Task<string> ReadInventoryAsync(
        NyxIdConnectedServiceInventoryReader reader,
        string token,
        CancellationToken ct)
    {
        try
        {
            var result = await reader.ReadAsync(token, organizationToken: null, ct).ConfigureAwait(false);
            return ResultFormatter.Format(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service inventory read failed");
            return InventoryFailure("inventory_query_unavailable");
        }
    }

    private static string CreateInventoryReadCallId(string? outerCallId) =>
        $"{Normalize(outerCallId) ?? "missing"}:inventory-read";

    private static bool TryBuildSubject(AgentToolExecutionContext context, out ExternalSubjectRef subject)
    {
        subject = new ExternalSubjectRef();
        var authorityPlatform = Normalize(context.NyxIdAuthority.Platform);
        var authorityUserId = Normalize(context.NyxIdAuthority.ExternalUserId);
        if (authorityPlatform is not null && authorityUserId is not null)
        {
            subject = new ExternalSubjectRef
            {
                Platform = authorityPlatform,
                Tenant = Normalize(context.NyxIdAuthority.Tenant) ?? string.Empty,
                ExternalUserId = authorityUserId,
            };
            return true;
        }

        return false;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool HasOnlyListArguments(string argumentsJson)
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

    private static string InventoryFailure(string errorCode) =>
        JsonSerializer.Serialize(new
        {
            error = errorCode,
            message = "The connected-service inventory for the bound NyxID account is temporarily unavailable. Retry shortly.",
        });

    private sealed class SenderInventoryTool(ChannelNyxIdConnectedServiceInventoryToolSource source) : IAgentTool
    {
        private const string Schema =
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";

        public string Name => "nyxid_service_inventory";
        public string Description =>
            "List the bound caller's exact NyxID connected-service instances.";
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
            source.ExecuteInventoryAsync(argumentsJson, ct);
    }

    private sealed class SenderInventoryReaderTool(
        ChannelNyxIdConnectedServiceInventoryToolSource source,
        NyxIdConnectedServiceInventoryReader reader,
        string token) : IAgentTool
    {
        public string Name => "nyxid_service_inventory_reader";
        public string Description => "Read the bound caller's NyxID connected-service inventory.";
        public string ParametersSchema =>
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";
        public bool IsReadOnly => true;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            NyxIdServiceInventoryReceiptFactory.Create(callId, toolName, resultJson);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.ReadInventoryAsync(reader, token, ct);
    }
}
