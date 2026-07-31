using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
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
    private readonly NyxIdToolOptions? _options;
    private readonly INyxIdApiClientFactory? _apiClientFactory;
    private readonly INyxIdConnectedServiceInventoryCapabilityIssuer? _capabilityIssuer;
    private readonly ILogger _logger;
    private readonly ILogger<NyxIdConnectedServiceInventoryToolSource>? _inventoryLogger;

    public ChannelNyxIdConnectedServiceInventoryToolSource(
        NyxIdToolOptions? options = null,
        INyxIdApiClientFactory? apiClientFactory = null,
        INyxIdConnectedServiceInventoryCapabilityIssuer? capabilityIssuer = null,
        ILogger<ChannelNyxIdConnectedServiceInventoryToolSource>? logger = null,
        ILogger<NyxIdConnectedServiceInventoryToolSource>? inventoryLogger = null)
    {
        _options = options;
        _apiClientFactory = apiClientFactory;
        _capabilityIssuer = capabilityIssuer;
        _logger = logger ?? NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance;
        _inventoryLogger = inventoryLogger;
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

    private async Task<string> ExecuteInventoryAsync(
        string argumentsJson,
        CancellationToken ct)
    {
        if (!HasOnlyListArguments(argumentsJson))
            return JsonSerializer.Serialize(new { error = "invalid_arguments" });

        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (context is null || bindingId is null)
            return InventoryFailure("inventory_capability_unavailable");

        var strictSenderToken = Normalize(context.Credentials.SenderNyxIdAccessToken);
        if (strictSenderToken is not null)
            return await ExecuteWithSenderTokenAsync(context, strictSenderToken, argumentsJson, ct)
                .ConfigureAwait(false);

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
            var inventorySource = new NyxIdConnectedServiceInventoryToolSource(
                _options,
                new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient()),
                _inventoryLogger);
            var senderContext = context with
            {
                Credentials = new AgentToolCredentials(
                    token,
                    token,
                    context.Credentials.SenderNyxIdAccessToken),
            };
            using var scope = AgentToolContextScope.Push(senderContext);
            var tools = await inventorySource.DiscoverToolsAsync(ct).ConfigureAwait(false);
            var inventoryTool = tools.SingleOrDefault(static tool =>
                string.Equals(tool.Name, "nyxid_service_inventory", StringComparison.OrdinalIgnoreCase));
            return inventoryTool is null
                ? InventoryFailure("inventory_query_unavailable")
                : await inventoryTool.ExecuteAsync(argumentsJson, ct).ConfigureAwait(false);
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

    private static bool TryBuildSubject(
        AgentToolExecutionContext context,
        out ExternalSubjectRef subject)
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
}
