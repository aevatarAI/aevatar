using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public sealed class ChannelNyxIdConnectedServiceInventoryToolSource : IAgentToolSource
{
    private static readonly JsonFormatter ResultFormatter = new(
        JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
    private readonly NyxIdToolOptions? _options;
    private readonly INyxIdApiClientFactory? _apiClientFactory;
    private readonly INyxIdCapabilityBroker? _capabilityBroker;
    private readonly ILogger _logger;

    public ChannelNyxIdConnectedServiceInventoryToolSource(
        NyxIdToolOptions? options = null,
        INyxIdApiClientFactory? apiClientFactory = null,
        INyxIdCapabilityBroker? capabilityBroker = null,
        ILogger<ChannelNyxIdConnectedServiceInventoryToolSource>? logger = null)
    {
        _options = options;
        _apiClientFactory = apiClientFactory;
        _capabilityBroker = capabilityBroker;
        _logger = logger ?? NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var context = AgentToolRequestContext.Current;
        return context is null || Normalize(context.SenderBinding.BindingId) is null
            ? Task.FromResult<IReadOnlyList<IAgentTool>>([])
            : Task.FromResult<IReadOnlyList<IAgentTool>>([new SenderInventoryTool(this)]);
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
            return await ExecuteWithSenderTokenAsync(strictSenderToken, ct).ConfigureAwait(false);

        if (_capabilityBroker is null || !TryBuildSubject(context, out var subject))
            return InventoryFailure("inventory_capability_unavailable");

        try
        {
            var capability = await _capabilityBroker
                .IssueShortLivedByBindingIdAsync(
                    subject,
                    bindingId,
                    new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy },
                    ct)
                .ConfigureAwait(false);
            var inventoryToken = Normalize(capability.AccessToken);
            return inventoryToken is null
                ? InventoryFailure("inventory_capability_unavailable")
                : await ExecuteWithSenderTokenAsync(inventoryToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BindingRevokedException ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service inventory binding was revoked");
            return InventoryFailure("inventory_binding_revoked");
        }
        catch (BindingScopeMismatchException ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service inventory scope is unavailable");
            return InventoryFailure("inventory_scope_unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service inventory capability issue failed");
            return InventoryFailure("inventory_capability_unavailable");
        }
    }

    private async Task<string> ExecuteWithSenderTokenAsync(string token, CancellationToken ct)
    {
        if (_options is null || string.IsNullOrWhiteSpace(_options.BaseUrl) || _apiClientFactory is null)
            return InventoryFailure("inventory_source_unavailable");

        try
        {
            var reader = new NyxIdConnectedServiceInventoryReader(
                new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient()));
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

    private static bool TryBuildSubject(AgentToolExecutionContext context, out ExternalSubjectRef subject)
    {
        subject = new ExternalSubjectRef();
        var platform = Normalize(context.Channel.Platform);
        var externalUserId = Normalize(context.Channel.SenderId);
        if (platform is null || externalUserId is null)
            return false;

        subject = new ExternalSubjectRef
        {
            Platform = platform.ToLowerInvariant(),
            Tenant = Normalize(context.SenderBinding.SenderTenant) ?? string.Empty,
            ExternalUserId = externalUserId,
        };
        return true;
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

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed class SenderInventoryTool(ChannelNyxIdConnectedServiceInventoryToolSource source) : IAgentTool
    {
        private const string Schema =
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";

        public string Name => "nyxid_service_inventory";
        public string Description => "List the bound caller's active NyxID connected-service instances.";
        public string ParametersSchema => Schema;
        public bool IsReadOnly => true;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.ExecuteInventoryAsync(argumentsJson, ct);
    }
}
