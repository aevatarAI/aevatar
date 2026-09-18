using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
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
    private readonly INyxIdConnectedServiceCapabilityIssuer? _capabilityIssuer;
    private readonly IExactRemoteSkillFetcher? _exactSkillFetcher;
    private readonly ILogger _logger;

    public ChannelNyxIdConnectedServiceInventoryToolSource(
        IAgentToolExecutionPort toolExecutionPort,
        NyxIdToolOptions? options = null,
        INyxIdApiClientFactory? apiClientFactory = null,
        INyxIdConnectedServiceCapabilityIssuer? capabilityIssuer = null,
        ILogger<ChannelNyxIdConnectedServiceInventoryToolSource>? logger = null,
        IExactRemoteSkillFetcher? exactSkillFetcher = null)
    {
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _options = options;
        _apiClientFactory = apiClientFactory;
        _capabilityIssuer = capabilityIssuer;
        _exactSkillFetcher = exactSkillFetcher;
        _logger = logger ?? NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (context is null || bindingId is null)
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        return Task.FromResult<IReadOnlyList<IAgentTool>>([
            new SenderInventoryTool(this),
            new SenderRecommendedSkillTool(this),
        ]);
    }

    private async Task<string> ExecuteInventoryAsync(string argumentsJson, CancellationToken ct)
    {
        if (!HasOnlyListArguments(argumentsJson))
            return JsonSerializer.Serialize(new { error = "invalid_arguments" });

        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (context is null || bindingId is null)
            return InventoryFailure("inventory_capability_unavailable");

        if (_capabilityIssuer is null || !TryBuildSubject(context, out var subject))
            return InventoryFailure("inventory_capability_unavailable");

        try
        {
            // A sender token is request-local and carries no durable binding proof.
            // Revalidate the retained binding before every inventory read so a
            // tool discovered for binding A cannot read with its token after the
            // sender has moved to binding B. The issuer returns a fresh token for
            // the exact current binding, and rejects a changed binding before
            // token exchange.
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
                // This read is authorized by the bound sender, independently of
                // the registration Agent Key that admitted the outer tool call.
                CredentialSource = AgentToolCredentialSource.BearerToken,
                DurableNyxIdCredential = null,
                Credentials = new AgentToolCredentials(
                    token,
                    token,
                    SenderNyxIdAccessToken: token,
                    NyxIdCredentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
                    NyxIdCredentialAuthority: AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
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
            if (outcome.Kind is AgentToolExecutionOutcomeKind.Denied or AgentToolExecutionOutcomeKind.Failed)
            {
                _logger.LogWarning(
                    "NyxID connected-service inventory reader failed. request={RequestId} call={CallId} failureStage={FailureStage} errorCode={ErrorCode}",
                    senderContext.Request.RequestId,
                    senderContext.Request.CallId,
                    outcome.FailureStage,
                    outcome.FailureCode);
            }
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

    private async Task<string> ExecuteRecommendedSkillLoadAsync(string argumentsJson, CancellationToken ct)
    {
        var arguments = ParseRecommendedSkillArguments(argumentsJson);
        if (arguments is null)
            return JsonSerializer.Serialize(new { error = "invalid_arguments" });

        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (context is null || bindingId is null)
            return RecommendedSkillFailure("inventory_capability_unavailable");
        if (_capabilityIssuer is null || !TryBuildSubject(context, out var subject))
            return RecommendedSkillFailure("inventory_capability_unavailable");
        if (_exactSkillFetcher is null)
            return RecommendedSkillFailure("exact_skill_loader_unavailable");

        try
        {
            var capability = await _capabilityIssuer
                .IssueByBindingIdAsync(subject, bindingId, ct)
                .ConfigureAwait(false);
            var token = Normalize(capability.AccessToken);
            if (token is null)
                return RecommendedSkillFailure("inventory_capability_unavailable");

            return await ExecuteRecommendedSkillLoadWithSenderTokenAsync(context, token, arguments, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BindingRevokedException)
        {
            return RecommendedSkillFailure("inventory_binding_revoked");
        }
        catch (BindingScopeMismatchException)
        {
            return RecommendedSkillFailure("inventory_scope_unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID recommended skill capability issue failed");
            return RecommendedSkillFailure("inventory_capability_unavailable");
        }
    }

    private async Task<string> ExecuteRecommendedSkillLoadWithSenderTokenAsync(
        AgentToolExecutionContext context,
        string token,
        RecommendedSkillArguments arguments,
        CancellationToken ct)
    {
        if (_options is null || _apiClientFactory is null || _exactSkillFetcher is null)
            return RecommendedSkillFailure("inventory_source_unavailable");

        var reader = new NyxIdConnectedServiceInventoryReader(
            new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient()));
        var senderContext = context with
        {
            CredentialSource = AgentToolCredentialSource.BearerToken,
            DurableNyxIdCredential = null,
            Credentials = new AgentToolCredentials(
                token,
                token,
                SenderNyxIdAccessToken: token,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
                NyxIdCredentialAuthority: AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
            Request = context.Request with
            {
                CallId = CreateRecommendedSkillLoadCallId(context.Request.CallId),
            },
        };
        var outcome = await _toolExecutionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                new SenderRecommendedSkillReaderTool(this, reader, _exactSkillFetcher, token, arguments),
                "{}",
                senderContext,
                AgentToolApprovalContinuationMode.None,
                ApprovalGrant: null),
            ct).ConfigureAwait(false);
        return outcome.ResultJson;
    }

    private async Task<string> ReadRecommendedSkillAsync(
        NyxIdConnectedServiceInventoryReader reader,
        IExactRemoteSkillFetcher exactSkillFetcher,
        string token,
        RecommendedSkillArguments arguments,
        CancellationToken ct)
    {
        var inventory = await reader.ReadAsync(token, organizationToken: null, ct).ConfigureAwait(false);
        var service = inventory.Instances.FirstOrDefault(instance =>
            string.Equals(instance.UserServiceId, arguments.UserServiceId, StringComparison.Ordinal));
        if (service is null)
            return RecommendedSkillFailure("service_instance_not_visible");
        var skillRef = service.RecommendedSkillRefs.FirstOrDefault(candidate =>
            candidate.Source == NyxIdRecommendedSkillSource.Ornn &&
            string.Equals(candidate.SkillId, arguments.SkillId, StringComparison.Ordinal) &&
            string.Equals(candidate.LiteralVersion, arguments.LiteralVersion, StringComparison.Ordinal) &&
            string.Equals(candidate.ManifestDigest, arguments.ManifestDigest, StringComparison.Ordinal));
        if (skillRef is null)
            return RecommendedSkillFailure("recommended_skill_ref_not_visible");

        var fetchResult = await exactSkillFetcher.FetchAsync(
            token,
            new ExactRemoteSkillRef
            {
                Guid = skillRef.SkillId,
                LiteralVersion = skillRef.LiteralVersion,
            },
            ct).ConfigureAwait(false);
        if (!fetchResult.IsSuccess)
        {
            return JsonSerializer.Serialize(new
            {
                result_type = "nyxid_recommended_skill_load",
                status = "failed",
                loaded = false,
                failure_code = fetchResult.FailureCode?.ToString(),
                failure_detail = fetchResult.FailureDetail,
            });
        }

        var fetchedDigest = "sha256:" + Convert.ToHexString(fetchResult.SkillSha256!.ToByteArray()).ToLowerInvariant();
        if (!string.Equals(fetchedDigest, skillRef.ManifestDigest, StringComparison.OrdinalIgnoreCase))
            return RecommendedSkillFailure("recommended_skill_digest_mismatch");

        return JsonSerializer.Serialize(new
        {
            result_type = "nyxid_recommended_skill_load",
            status = "success",
            loaded = true,
            service_instance_id = service.UserServiceId,
            service_label = service.Label,
            skill = new
            {
                source = "ornn",
                skill_id = fetchResult.Guid,
                literal_version = fetchResult.LiteralVersion,
                name = fetchResult.Name,
                publisher_id = fetchResult.PublisherId,
                manifest_digest = fetchedDigest,
                recommended_name = skillRef.RecommendationName,
                revision = skillRef.Revision,
            },
            main_document = fetchResult.SkillMarkdown,
            resources = Array.Empty<object>(),
        });
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
        catch (NyxIdServiceInventoryContractException)
        {
            _logger.LogWarning("NyxID connected-service execution inventory contract is invalid");
            return InventoryFailure(NyxIdServiceInventoryContractException.ErrorCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service inventory read failed");
            return InventoryFailure("inventory_query_unavailable");
        }
    }

    private static string CreateInventoryReadCallId(string? outerCallId) =>
        $"{Normalize(outerCallId) ?? "missing"}:inventory-read";

    private static string CreateRecommendedSkillLoadCallId(string? outerCallId) =>
        $"{Normalize(outerCallId) ?? "missing"}:recommended-skill-load";

    private static RecommendedSkillArguments? ParseRecommendedSkillArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var userServiceId = ReadRequiredString(document.RootElement, "user_service_id");
            var source = ReadRequiredString(document.RootElement, "source");
            var skillId = ReadRequiredString(document.RootElement, "skill_id");
            var literalVersion = ReadRequiredString(document.RootElement, "literal_version");
            var manifestDigest = ReadRequiredString(document.RootElement, "manifest_digest");
            if (userServiceId is null || source is null || skillId is null ||
                literalVersion is null || manifestDigest is null ||
                !string.Equals(source, "ornn", StringComparison.Ordinal))
            {
                return null;
            }

            return new RecommendedSkillArguments(
                userServiceId,
                skillId,
                literalVersion,
                manifestDigest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return Normalize(value.GetString());
    }

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

    private static string RecommendedSkillFailure(string errorCode) =>
        JsonSerializer.Serialize(new
        {
            result_type = "nyxid_recommended_skill_load",
            status = "failed",
            loaded = false,
            error = errorCode,
        });

    private sealed record RecommendedSkillArguments(
        string UserServiceId,
        string SkillId,
        string LiteralVersion,
        string ManifestDigest);

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

    private sealed class SenderRecommendedSkillTool(ChannelNyxIdConnectedServiceInventoryToolSource source) : IAgentTool
    {
        private const string Schema =
            """
            {
              "type":"object",
              "properties":{
                "user_service_id":{"type":"string","description":"Exact user_service_id from nyxid_service_inventory."},
                "source":{"type":"string","enum":["ornn"]},
                "skill_id":{"type":"string","description":"Exact skill_id from the selected recommended_skill_refs entry."},
                "literal_version":{"type":"string","description":"Exact literal_version from the selected recommended_skill_refs entry."},
                "manifest_digest":{"type":"string","description":"Exact manifest_digest from the selected recommended_skill_refs entry."}
              },
              "required":["user_service_id","source","skill_id","literal_version","manifest_digest"],
              "additionalProperties":false
            }
            """;

        public string Name => "nyxid_load_recommended_skill";
        public string Description =>
            "Load the main document for a NyxID service-recommended exact Ornn skill ref from the current sender inventory.";
        public string ParametersSchema => Schema;
        public bool IsReadOnly => true;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.ExecuteRecommendedSkillLoadAsync(argumentsJson, ct);
    }

    private sealed class SenderRecommendedSkillReaderTool(
        ChannelNyxIdConnectedServiceInventoryToolSource source,
        NyxIdConnectedServiceInventoryReader reader,
        IExactRemoteSkillFetcher exactSkillFetcher,
        string token,
        RecommendedSkillArguments arguments) : IAgentTool
    {
        public string Name => "nyxid_recommended_skill_reader";
        public string Description => "Read and load one current sender recommended Ornn skill ref.";
        public string ParametersSchema =>
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";
        public bool IsReadOnly => true;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.ReadRecommendedSkillAsync(reader, exactSkillFetcher, token, arguments, ct);
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
