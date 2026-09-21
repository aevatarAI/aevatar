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
    private readonly INyxIdRecommendedSkillRefCreator _recommendedSkillRefCreator;
    private readonly ILogger _logger;

    public ChannelNyxIdConnectedServiceInventoryToolSource(
        IAgentToolExecutionPort toolExecutionPort,
        NyxIdToolOptions? options = null,
        INyxIdApiClientFactory? apiClientFactory = null,
        INyxIdConnectedServiceCapabilityIssuer? capabilityIssuer = null,
        ILogger<ChannelNyxIdConnectedServiceInventoryToolSource>? logger = null,
        IExactRemoteSkillFetcher? exactSkillFetcher = null,
        INyxIdRecommendedSkillRefCreator? recommendedSkillRefCreator = null)
    {
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _options = options;
        _apiClientFactory = apiClientFactory;
        _capabilityIssuer = capabilityIssuer;
        _exactSkillFetcher = exactSkillFetcher;
        _recommendedSkillRefCreator = recommendedSkillRefCreator ?? EmptyNyxIdRecommendedSkillRefCreator.Instance;
        _logger = logger ?? NullLogger<ChannelNyxIdConnectedServiceInventoryToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var context = AgentToolRequestContext.Current;
        if (context is null)
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        var bindingId = Normalize(context.SenderBinding.BindingId);
        var tools = new List<IAgentTool>();
        if (CanReadConnectedServiceInventory(context, bindingId))
        {
            tools.Add(new SenderInventoryTool(this));
            tools.Add(new SenderRecommendedSkillTool(this));
        }

        if (CanInvokeConnectedOperation(context, bindingId))
            tools.Add(new SenderConnectedServiceOperationTool(this));

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }

    private bool CanReadConnectedServiceInventory(
        AgentToolExecutionContext context,
        string? bindingId)
    {
        if (bindingId is not null)
            return true;

        return IsRegistrationAgentKeyContext(context) &&
               _options is not null &&
               _apiClientFactory is not null;
    }

    private bool CanInvokeConnectedOperation(
        AgentToolExecutionContext context,
        string? bindingId)
    {
        if (_options is null || _apiClientFactory is null)
            return false;

        if (IsRegistrationAgentKeyContext(context))
            return true;

        return bindingId is not null && _capabilityIssuer is not null;
    }

    private static bool IsRegistrationAgentKeyContext(AgentToolExecutionContext context) =>
        context.CredentialSource == AgentToolCredentialSource.ChannelRegistration &&
        context.Credentials.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.AgentKey &&
        !string.IsNullOrWhiteSpace(context.Credentials.NyxIdAccessToken);

    private async Task<string> ExecuteInventoryAsync(string argumentsJson, CancellationToken ct)
    {
        if (!HasOnlyListArguments(argumentsJson))
            return JsonSerializer.Serialize(new { error = "invalid_arguments" });

        var context = AgentToolRequestContext.Current;
        if (context is null)
            return InventoryFailure("inventory_capability_unavailable");

        var bindingId = Normalize(context.SenderBinding.BindingId);
        if (bindingId is null)
        {
            if (!IsRegistrationAgentKeyContext(context))
                return InventoryFailure("inventory_capability_unavailable");

            return await ExecuteWithRegistrationAgentKeyAsync(context, context.Credentials.NyxIdAccessToken!, argumentsJson, ct)
                .ConfigureAwait(false);
        }

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
                new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient(), _recommendedSkillRefCreator));
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
                    new SenderInventoryReaderTool(this, reader, token, InventoryReadAuthority.Caller),
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

    private async Task<string> ExecuteWithRegistrationAgentKeyAsync(
        AgentToolExecutionContext context,
        string agentKey,
        string argumentsJson,
        CancellationToken ct)
    {
        if (_options is null || _apiClientFactory is null)
            return InventoryFailure("inventory_source_unavailable");

        try
        {
            var reader = new NyxIdConnectedServiceInventoryReader(
                new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient(), _recommendedSkillRefCreator));
            var registrationContext = context with
            {
                Request = context.Request with
                {
                    CallId = CreateInventoryReadCallId(context.Request.CallId),
                },
            };
            var outcome = await _toolExecutionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    new SenderInventoryReaderTool(this, reader, agentKey, InventoryReadAuthority.AgentKey),
                    argumentsJson,
                    registrationContext,
                    AgentToolApprovalContinuationMode.None,
                    ApprovalGrant: null),
                ct).ConfigureAwait(false);
            if (outcome.Kind is AgentToolExecutionOutcomeKind.Denied or AgentToolExecutionOutcomeKind.Failed)
            {
                _logger.LogWarning(
                    "NyxID Agent Key connected-service inventory reader failed. request={RequestId} call={CallId} failureStage={FailureStage} errorCode={ErrorCode}",
                    registrationContext.Request.RequestId,
                    registrationContext.Request.CallId,
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
            _logger.LogWarning(ex, "NyxID Agent Key connected-service inventory source creation failed");
            return InventoryFailure("inventory_source_unavailable");
        }
    }

    private async Task<string> ExecuteRecommendedSkillLoadAsync(string argumentsJson, CancellationToken ct)
    {
        var arguments = ParseRecommendedSkillArguments(argumentsJson);
        if (arguments is null)
            return JsonSerializer.Serialize(new { error = "invalid_arguments" });

        var context = AgentToolRequestContext.Current;
        if (context is null)
            return RecommendedSkillFailure("inventory_capability_unavailable");
        if (_exactSkillFetcher is null)
            return RecommendedSkillFailure("exact_skill_loader_unavailable");

        var bindingId = Normalize(context.SenderBinding.BindingId);
        if (bindingId is null)
        {
            if (!IsRegistrationAgentKeyContext(context))
                return RecommendedSkillFailure("inventory_capability_unavailable");

            return await ExecuteRecommendedSkillLoadWithRegistrationAgentKeyAsync(
                    context,
                    context.Credentials.NyxIdAccessToken!,
                    arguments,
                    ct)
                .ConfigureAwait(false);
        }

        if (_capabilityIssuer is null || !TryBuildSubject(context, out var subject))
            return RecommendedSkillFailure("inventory_capability_unavailable");

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
            new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient(), _recommendedSkillRefCreator));
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
                new SenderRecommendedSkillReaderTool(
                    this,
                    reader,
                    _exactSkillFetcher,
                    token,
                    InventoryReadAuthority.Caller,
                    arguments),
                "{}",
                senderContext,
                AgentToolApprovalContinuationMode.None,
                ApprovalGrant: null),
            ct).ConfigureAwait(false);
        return outcome.ResultJson;
    }

    private async Task<string> ExecuteRecommendedSkillLoadWithRegistrationAgentKeyAsync(
        AgentToolExecutionContext context,
        string agentKey,
        RecommendedSkillArguments arguments,
        CancellationToken ct)
    {
        if (_options is null || _apiClientFactory is null || _exactSkillFetcher is null)
            return RecommendedSkillFailure("inventory_source_unavailable");

        var reader = new NyxIdConnectedServiceInventoryReader(
            new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient(), _recommendedSkillRefCreator));
        var registrationContext = context with
        {
            Request = context.Request with
            {
                CallId = CreateRecommendedSkillLoadCallId(context.Request.CallId),
            },
        };
        var outcome = await _toolExecutionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                new SenderRecommendedSkillReaderTool(
                    this,
                    reader,
                    _exactSkillFetcher,
                    agentKey,
                    InventoryReadAuthority.AgentKey,
                    arguments),
                "{}",
                registrationContext,
                AgentToolApprovalContinuationMode.None,
                ApprovalGrant: null),
            ct).ConfigureAwait(false);
        return outcome.ResultJson;
    }

    private async Task<string> ReadRecommendedSkillAsync(
        NyxIdConnectedServiceInventoryReader reader,
        IExactRemoteSkillFetcher exactSkillFetcher,
        string token,
        InventoryReadAuthority inventoryReadAuthority,
        RecommendedSkillArguments arguments,
        CancellationToken ct)
    {
        var inventory = await ReadInventoryResultAsync(reader, token, inventoryReadAuthority, ct)
            .ConfigureAwait(false);
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

    private async Task<AgentToolTerminalOutcome> ExecuteOperationAsync(
        string callId,
        string argumentsJson,
        CancellationToken ct)
    {
        var arguments = ParseOperationArguments(argumentsJson);
        if (arguments is null)
            return OperationFailure(callId, "invalid_arguments");

        var context = AgentToolRequestContext.Current;
        if (context is null)
            return OperationFailure(callId, "operation_context_unavailable");

        if (IsRegistrationAgentKeyContext(context))
            return await ExecuteOperationWithContextAsync(context, callId, arguments, ct).ConfigureAwait(false);

        var bindingId = Normalize(context.SenderBinding.BindingId);
        if (bindingId is null || _capabilityIssuer is null || !TryBuildSubject(context, out var subject))
            return OperationFailure(callId, "inventory_capability_unavailable");

        try
        {
            var capability = await _capabilityIssuer
                .IssueByBindingIdAsync(subject, bindingId, ct)
                .ConfigureAwait(false);
            var token = Normalize(capability.AccessToken);
            if (token is null)
                return OperationFailure(callId, "inventory_capability_unavailable");

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
            };
            return await ExecuteOperationWithContextAsync(senderContext, callId, arguments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BindingRevokedException)
        {
            return OperationFailure(callId, "inventory_binding_revoked");
        }
        catch (BindingScopeMismatchException)
        {
            return OperationFailure(callId, "inventory_scope_unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service operation capability issue failed");
            return OperationFailure(callId, "inventory_capability_unavailable");
        }
    }

    private async Task<AgentToolTerminalOutcome> ExecuteOperationWithContextAsync(
        AgentToolExecutionContext context,
        string callId,
        OperationArguments arguments,
        CancellationToken ct)
    {
        if (_options is null || _apiClientFactory is null)
            return OperationFailure(callId, "operation_source_unavailable");

        try
        {
            var apiClient = _apiClientFactory.CreateClient();
            var invoker = new NyxIdConnectedServiceOperationInvoker(
                _options,
                apiClient,
                new NyxIdServiceInstanceClient(apiClient, _recommendedSkillRefCreator));
            var innerContext = context with
            {
                Request = context.Request with
                {
                    CallId = CreateOperationInvokeCallId(callId),
                },
            };
            var result = await invoker.InvokeAsync(
                    innerContext,
                    new NyxIdConnectedServiceOperationInvocation(
                        arguments.UserServiceId,
                        arguments.ServiceSlug,
                        arguments.OperationId,
                        arguments.OperationArgumentsJson,
                        arguments.DocumentRequest),
                    callId,
                    "nyxid_invoke_operation",
                    ct)
                .ConfigureAwait(false);
            return result.IsSuccess && result.Outcome is not null
                ? result.Outcome
                : OperationFailure(callId, result.FailureCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service operation invocation failed");
            return OperationFailure(callId, "operation_source_unavailable");
        }
    }

    private static Task<NyxIdServiceInventoryResult> ReadInventoryResultAsync(
        NyxIdConnectedServiceInventoryReader reader,
        string token,
        InventoryReadAuthority inventoryReadAuthority,
        CancellationToken ct) =>
        inventoryReadAuthority switch
        {
            InventoryReadAuthority.AgentKey => reader.ReadAgentKeyAsync(token, ct),
            _ => reader.ReadAsync(token, organizationToken: null, ct),
        };

    private async Task<string> ReadInventoryAsync(
        NyxIdConnectedServiceInventoryReader reader,
        string token,
        InventoryReadAuthority inventoryReadAuthority,
        CancellationToken ct)
    {
        try
        {
            var result = await ReadInventoryResultAsync(reader, token, inventoryReadAuthority, ct)
                .ConfigureAwait(false);
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

    private static string CreateOperationInvokeCallId(string? outerCallId) =>
        $"{Normalize(outerCallId) ?? "missing"}:connected-operation";

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

    private static OperationArguments? ParseOperationArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("user_service_id" or "service_slug" or "operation_id" or "operation_arguments" or "document_request"))
                    return null;
            }

            var userServiceId = ReadOptionalString(root, "user_service_id");
            var serviceSlug = ReadOptionalString(root, "service_slug");
            if (userServiceId is null && serviceSlug is null)
                return null;

            var hasTypedOperation = root.TryGetProperty("operation_id", out _);
            var hasDocumentRequest = root.TryGetProperty("document_request", out var documentRequestElement);
            if (hasTypedOperation == hasDocumentRequest)
                return null;
            if (hasDocumentRequest && root.TryGetProperty("operation_arguments", out _))
                return null;

            if (hasDocumentRequest)
            {
                var documentRequest = ParseDocumentRequest(documentRequestElement);
                return documentRequest is null
                    ? null
                    : new OperationArguments(
                        userServiceId,
                        serviceSlug,
                        OperationId: null,
                        OperationArgumentsJson: "{}",
                        documentRequest);
            }

            var operationId = ReadRequiredString(root, "operation_id");
            if (operationId is null)
                return null;

            var operationArgumentsJson = "{}";
            if (root.TryGetProperty("operation_arguments", out var operationArguments))
            {
                if (operationArguments.ValueKind != JsonValueKind.Object)
                    return null;
                operationArgumentsJson = operationArguments.GetRawText();
            }

            return new OperationArguments(
                userServiceId,
                serviceSlug,
                operationId,
                operationArgumentsJson,
                DocumentRequest: null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NyxIdConnectedServiceDocumentRequest? ParseDocumentRequest(JsonElement documentRequest)
    {
        if (documentRequest.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in documentRequest.EnumerateObject())
        {
            if (property.Name is not ("method" or "relative_path" or "query" or "headers" or "body"))
                return null;
        }

        var method = ReadRequiredString(documentRequest, "method");
        var relativePath = ReadRequiredString(documentRequest, "relative_path");
        if (method is null || relativePath is null)
            return null;

        var runtimeArguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (documentRequest.TryGetProperty("query", out var query))
        {
            if (query.ValueKind != JsonValueKind.Object)
                return null;
            runtimeArguments["query"] = query;
        }
        if (documentRequest.TryGetProperty("headers", out var headers))
        {
            if (headers.ValueKind != JsonValueKind.Object)
                return null;
            runtimeArguments["headers"] = headers;
        }
        if (documentRequest.TryGetProperty("body", out var body))
        {
            if (body.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return null;
            runtimeArguments["body"] = body;
        }

        return new NyxIdConnectedServiceDocumentRequest(
            method,
            relativePath,
            JsonSerializer.Serialize(runtimeArguments));
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

    private static AgentToolTerminalOutcome OperationFailure(string callId, string errorCode)
    {
        var resultJson = JsonSerializer.Serialize(new
        {
            result_type = "nyxid_connected_operation_invoke",
            status = "failed",
            invoked = false,
            error = errorCode,
        });
        return new AgentToolTerminalOutcome(resultJson, new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = "nyxid_invoke_operation",
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = errorCode,
            ErrorMessage = "The connected-service operation could not be invoked.",
            ResultJson = resultJson,
        });
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

    private static AgentToolReceipt? CreateRecommendedSkillReceipt(
        string callId,
        string toolName,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("result_type", out var resultType) ||
                !string.Equals(resultType.GetString(), "nyxid_recommended_skill_load", StringComparison.Ordinal) ||
                !root.TryGetProperty("status", out var statusValue) ||
                statusValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("loaded", out var loadedValue) ||
                loadedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var loaded = loadedValue.GetBoolean();
            var status = statusValue.GetString();
            if (loaded && string.Equals(status, "success", StringComparison.Ordinal))
            {
                return new AgentToolReceipt
                {
                    CallId = callId ?? string.Empty,
                    ToolName = toolName ?? string.Empty,
                    Status = AgentToolReceiptStatus.Success,
                    ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                    ResultJson = resultJson ?? string.Empty,
                };
            }

            var errorCode = ReadOptionalString(root, "error") ??
                ReadOptionalString(root, "failure_code") ??
                "nyxid_recommended_skill_load_failed";
            const string errorMessage = "The recommended skill could not be loaded.";
            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                Status = AgentToolReceiptStatus.Error,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                ResultJson = resultJson ?? string.Empty,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return Normalize(value.GetString());
    }

    private enum InventoryReadAuthority
    {
        Caller,
        AgentKey,
    }

    private sealed record OperationArguments(
        string? UserServiceId,
        string? ServiceSlug,
        string? OperationId,
        string OperationArgumentsJson,
        NyxIdConnectedServiceDocumentRequest? DocumentRequest);

    private sealed record RecommendedSkillArguments(
        string UserServiceId,
        string SkillId,
        string LiteralVersion,
        string ManifestDigest);

    private sealed class SenderConnectedServiceOperationTool(ChannelNyxIdConnectedServiceInventoryToolSource source) : IAgentTool
    {
        private const string Schema =
            """
            {
              "type":"object",
              "properties":{
                "user_service_id":{"type":"string","description":"Exact user_service_id from nyxid_service_inventory when available."},
                "service_slug":{"type":"string","description":"Exact connected-service slug from inventory or channel runtime selectors."},
                "operation_id":{"type":"string","description":"Exact endpoint or operation id named by the loaded recommended skill for typed operation mode."},
                "operation_arguments":{"type":"object","description":"Typed mode only. Only path_params, query, headers, body, and response_mode values declared by the operation contract.","additionalProperties":true},
                "document_request":{
                  "type":"object",
                  "description":"Document-guided mode only. Use only when the loaded recommended skill explicitly describes a service without typed operations. This is not a fallback for a missing operation_id.",
                  "properties":{
                    "method":{"type":"string","enum":["GET","HEAD","OPTIONS","POST","PUT","PATCH","DELETE"]},
                    "relative_path":{"type":"string","description":"Safe relative service path from the loaded skill documentation. Absolute URLs, query strings, fragments, and traversal are rejected."},
                    "query":{"type":"object","additionalProperties":{"type":"string"}},
                    "headers":{"type":"object","additionalProperties":{"type":"string"}},
                    "body":{"type":"object","additionalProperties":true}
                  },
                  "required":["method","relative_path"],
                  "additionalProperties":false
                }
              },
              "anyOf":[{"required":["user_service_id"]},{"required":["service_slug"]}],
              "oneOf":[{"required":["operation_id"]},{"required":["document_request"]}],
              "additionalProperties":false
            }
            """;

        public string Name => "nyxid_invoke_operation";
        public string Description =>
            "Invoke one current NyxID connected-service request selected by exact service identity and either a typed operation id or an explicit document-guided request.";
        public string ParametersSchema => Schema;
        public bool IsReadOnly => false;
        public bool IsDestructive => false;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public string SideEffectKind => "connected_service_operation";

        public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
            AgentToolReplayPolicy.NonReplayable;

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson)
        {
            try
            {
                using var document = JsonDocument.Parse(resultJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    string.Equals(ReadOptionalString(root, "result_type"), "nyxid_connected_operation_invoke", StringComparison.Ordinal) &&
                    string.Equals(ReadOptionalString(root, "status"), "failed", StringComparison.Ordinal))
                {
                    var errorCode = ReadOptionalString(root, "error") ?? "operation_invoke_failed";
                    return OperationFailure(callId, errorCode).Receipt;
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        public async Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
            string callId,
            string toolName,
            string argumentsJson,
            CancellationToken ct = default) =>
            await source.ExecuteOperationAsync(callId, argumentsJson, ct).ConfigureAwait(false);

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            (await ExecuteWithOutcomeAsync(string.Empty, Name, argumentsJson, ct).ConfigureAwait(false)).ResultJson;
    }

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

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            CreateRecommendedSkillReceipt(callId, toolName, resultJson);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.ExecuteRecommendedSkillLoadAsync(argumentsJson, ct);
    }

    private sealed class SenderRecommendedSkillReaderTool(
        ChannelNyxIdConnectedServiceInventoryToolSource source,
        NyxIdConnectedServiceInventoryReader reader,
        IExactRemoteSkillFetcher exactSkillFetcher,
        string token,
        InventoryReadAuthority inventoryReadAuthority,
        RecommendedSkillArguments arguments) : IAgentTool
    {
        public string Name => "nyxid_recommended_skill_reader";
        public string Description => "Read and load one current sender recommended Ornn skill ref.";
        public string ParametersSchema =>
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";
        public bool IsReadOnly => true;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            CreateRecommendedSkillReceipt(callId, toolName, resultJson);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.ReadRecommendedSkillAsync(reader, exactSkillFetcher, token, inventoryReadAuthority, arguments, ct);
    }

    private sealed class SenderInventoryReaderTool(
        ChannelNyxIdConnectedServiceInventoryToolSource source,
        NyxIdConnectedServiceInventoryReader reader,
        string token,
        InventoryReadAuthority inventoryReadAuthority) : IAgentTool
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
            source.ReadInventoryAsync(reader, token, inventoryReadAuthority, ct);
    }
}
