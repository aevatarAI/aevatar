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
    private readonly INyxIdRecommendedSkillRefCreator? _recommendedSkillRefCreator;
    private readonly INyxIdClientCredentialsTokenSource? _clientCredentialsTokenSource;
    private readonly ILogger _logger;

    public ChannelNyxIdConnectedServiceInventoryToolSource(
        IAgentToolExecutionPort toolExecutionPort,
        NyxIdToolOptions? options = null,
        INyxIdApiClientFactory? apiClientFactory = null,
        INyxIdConnectedServiceCapabilityIssuer? capabilityIssuer = null,
        ILogger<ChannelNyxIdConnectedServiceInventoryToolSource>? logger = null,
        IExactRemoteSkillFetcher? exactSkillFetcher = null,
        INyxIdRecommendedSkillRefCreator? recommendedSkillRefCreator = null,
        INyxIdClientCredentialsTokenSource? clientCredentialsTokenSource = null)
    {
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _options = options;
        _apiClientFactory = apiClientFactory;
        _capabilityIssuer = capabilityIssuer;
        _exactSkillFetcher = exactSkillFetcher;
        _recommendedSkillRefCreator = recommendedSkillRefCreator;
        _clientCredentialsTokenSource = clientCredentialsTokenSource;
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
            if (_recommendedSkillRefCreator is not null && _options is not null && _apiClientFactory is not null)
                tools.Add(new SenderEnsureRecommendedSkillRefsTool(this));
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

        if (bindingId is not null)
            return _capabilityIssuer is not null;

        return IsRegistrationAgentKeyContext(context);
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
        if (bindingId is not null)
        {
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
                return await ExecuteWithRegistrationAgentKeyOrFailureAsync(
                        context,
                        argumentsJson,
                        "inventory_binding_revoked",
                        ct)
                    .ConfigureAwait(false);
            }
            catch (BindingScopeMismatchException ex)
            {
                _logger.LogWarning(
                    ex,
                    "NyxID connected-service inventory scope is unavailable. subject={Platform}:{Tenant}:{User}",
                    subject.Platform,
                    subject.Tenant,
                    subject.ExternalUserId);
                return await ExecuteWithRegistrationAgentKeyOrFailureAsync(
                        context,
                        argumentsJson,
                        "inventory_scope_unavailable",
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "NyxID connected-service inventory capability issue failed. subject={Platform}:{Tenant}:{User}",
                    subject.Platform,
                    subject.Tenant,
                    subject.ExternalUserId);
                return await ExecuteWithRegistrationAgentKeyOrFailureAsync(
                        context,
                        argumentsJson,
                        "inventory_capability_unavailable",
                        ct)
                    .ConfigureAwait(false);
            }
        }

        if (IsRegistrationAgentKeyContext(context))
        {
            return await ExecuteWithRegistrationAgentKeyAsync(
                    context,
                    context.Credentials.NyxIdAccessToken!,
                    argumentsJson,
                    ct)
                .ConfigureAwait(false);
        }

        return InventoryFailure("inventory_capability_unavailable");
    }

    private async Task<string> ExecuteWithRegistrationAgentKeyOrFailureAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        string failureCode,
        CancellationToken ct)
    {
        if (!IsRegistrationAgentKeyContext(context))
            return InventoryFailure(failureCode);

        return await ExecuteWithRegistrationAgentKeyAsync(
                context,
                context.Credentials.NyxIdAccessToken!,
                argumentsJson,
                ct)
            .ConfigureAwait(false);
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
                new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient()));
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
        if (bindingId is not null)
        {
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

        if (IsRegistrationAgentKeyContext(context))
        {
            return await ExecuteRecommendedSkillLoadWithRegistrationAgentKeyAsync(
                    context,
                    context.Credentials.NyxIdAccessToken!,
                    arguments,
                    ct)
                .ConfigureAwait(false);
        }

        return RecommendedSkillFailure("inventory_capability_unavailable");
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
            new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient()));
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
        var ensureResult = await EnsureInventoryRecommendedSkillRefsAsync(inventory, token, ct).ConfigureAwait(false);
        inventory = ensureResult.Inventory;
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
        if (TryLoadGeneratedRecommendedSkill(ensureResult.CreatedSkills, arguments, service, out var generatedSkillJson))
            return generatedSkillJson;

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
            if (fetchResult.FailureCode == ExactRemoteSkillFetchFailureCode.NotFound)
            {
                _logger.LogWarning(
                    "NyxID recommended skill exact Ornn ref returned NotFound; regenerating. userServiceId={UserServiceId} skillId={SkillId} literalVersion={LiteralVersion}",
                    service.UserServiceId,
                    skillRef.SkillId,
                    skillRef.LiteralVersion);
                return await RegenerateRecommendedSkillAsync(
                        inventory,
                        service,
                        skillRef,
                        arguments,
                        token,
                        ct)
                    .ConfigureAwait(false);
            }

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

    private async Task<string> ExecuteEnsureRecommendedSkillRefsAsync(string argumentsJson, CancellationToken ct)
    {
        var arguments = ParseEnsureRecommendedSkillRefsArguments(argumentsJson);
        if (arguments is null)
            return JsonSerializer.Serialize(new { error = "invalid_arguments" });

        var context = AgentToolRequestContext.Current;
        if (context is null)
            return EnsureRecommendedSkillRefsFailure("inventory_capability_unavailable");
        if (_recommendedSkillRefCreator is null)
            return EnsureRecommendedSkillRefsFailure("recommended_skill_ref_creator_unavailable");

        var bindingId = Normalize(context.SenderBinding.BindingId);
        if (bindingId is not null)
        {
            if (_capabilityIssuer is null || !TryBuildSubject(context, out var subject))
                return EnsureRecommendedSkillRefsFailure("inventory_capability_unavailable");

            try
            {
                var capability = await _capabilityIssuer
                    .IssueByBindingIdAsync(subject, bindingId, ct)
                    .ConfigureAwait(false);
                var token = Normalize(capability.AccessToken);
                if (token is null)
                    return EnsureRecommendedSkillRefsFailure("inventory_capability_unavailable");

                return await ExecuteEnsureRecommendedSkillRefsWithSenderTokenAsync(context, token, arguments, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (BindingRevokedException)
            {
                return EnsureRecommendedSkillRefsFailure("inventory_binding_revoked");
            }
            catch (BindingScopeMismatchException)
            {
                return EnsureRecommendedSkillRefsFailure("inventory_scope_unavailable");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NyxID recommended skill ref ensure capability issue failed");
                return EnsureRecommendedSkillRefsFailure("inventory_capability_unavailable");
            }
        }

        if (IsRegistrationAgentKeyContext(context))
        {
            return await ExecuteEnsureRecommendedSkillRefsWithClientCredentialsAsync(
                    context,
                    arguments,
                    ct)
                .ConfigureAwait(false);
        }

        return EnsureRecommendedSkillRefsFailure("inventory_capability_unavailable");
    }

    private async Task<string> ExecuteEnsureRecommendedSkillRefsWithClientCredentialsAsync(
        AgentToolExecutionContext context,
        EnsureRecommendedSkillRefsArguments arguments,
        CancellationToken ct)
    {
        if (_options is null || _apiClientFactory is null ||
            _recommendedSkillRefCreator is null || _clientCredentialsTokenSource is null)
        {
            return EnsureRecommendedSkillRefsFailure("inventory_source_unavailable");
        }

        var token = Normalize(await _clientCredentialsTokenSource.GetAccessTokenAsync(ct).ConfigureAwait(false));
        if (token is null)
            return EnsureRecommendedSkillRefsFailure("inventory_capability_unavailable");

        var reader = new NyxIdConnectedServiceInventoryReader(
            new NyxIdServiceInstanceClient(_apiClientFactory.CreateClient()));
        var serviceAccountContext = context with
        {
            CredentialSource = AgentToolCredentialSource.ServiceAccount,
            DurableNyxIdCredential = null,
            Credentials = new AgentToolCredentials(
                token,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation,
                NyxIdCredentialAuthority: AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
            Request = context.Request with
            {
                CallId = CreateRecommendedSkillRefsEnsureCallId(context.Request.CallId),
            },
        };
        var outcome = await _toolExecutionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                new SenderEnsureRecommendedSkillRefsProvisionerTool(
                    this,
                    reader,
                    _recommendedSkillRefCreator,
                    token,
                    InventoryReadAuthority.Caller,
                    arguments),
                "{}",
                serviceAccountContext,
                AgentToolApprovalContinuationMode.None,
                ApprovalGrant: null),
            ct).ConfigureAwait(false);
        return outcome.ResultJson;
    }

    private async Task<string> ExecuteEnsureRecommendedSkillRefsWithSenderTokenAsync(
        AgentToolExecutionContext context,
        string token,
        EnsureRecommendedSkillRefsArguments arguments,
        CancellationToken ct)
    {
        if (_options is null || _apiClientFactory is null || _recommendedSkillRefCreator is null)
            return EnsureRecommendedSkillRefsFailure("inventory_source_unavailable");

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
                CallId = CreateRecommendedSkillRefsEnsureCallId(context.Request.CallId),
            },
        };
        var outcome = await _toolExecutionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                new SenderEnsureRecommendedSkillRefsProvisionerTool(
                    this,
                    reader,
                    _recommendedSkillRefCreator,
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

    private async Task<string> EnsureRecommendedSkillRefsAsync(
        NyxIdConnectedServiceInventoryReader reader,
        INyxIdRecommendedSkillRefCreator recommendedSkillRefCreator,
        string token,
        InventoryReadAuthority inventoryReadAuthority,
        EnsureRecommendedSkillRefsArguments arguments,
        CancellationToken ct)
    {
        try
        {
            var inventory = await ReadInventoryResultAsync(reader, token, inventoryReadAuthority, ct)
                .ConfigureAwait(false);
            var service = ResolveServiceInstance(inventory.Instances, arguments.UserServiceId, arguments.ServiceSlug);
            if (service is null)
                return EnsureRecommendedSkillRefsFailure("service_instance_not_visible");
            if (service.RecommendedSkillRefs.Count > 0)
            {
                return RecommendedSkillRefsEnsured(
                    service,
                    service.RecommendedSkillRefs,
                    created: false);
            }

            var creationResult = await recommendedSkillRefCreator.CreateRecommendedSkillRefsAsync(service, token, ct)
                .ConfigureAwait(false);
            if (creationResult.Refs.Count == 0)
            {
                _logger.LogInformation(
                    "NyxID recommended skill ref ensure skipped because no generated skill was available. userServiceId={UserServiceId} serviceSlug={ServiceSlug} failureCode={FailureCode}",
                    service.UserServiceId,
                    service.DisplaySlug,
                    creationResult.PersistenceFailureCode);
                return RecommendedSkillRefsUnavailable(
                    service,
                    FirstNonEmpty(creationResult.PersistenceFailureCode, "recommended_skill_unavailable"));
            }

            return RecommendedSkillRefsEnsured(service, creationResult, created: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NyxIdServiceInventoryContractException)
        {
            _logger.LogWarning("NyxID connected-service ensure refs inventory contract is invalid");
            return EnsureRecommendedSkillRefsFailure(NyxIdServiceInventoryContractException.ErrorCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID recommended skill ref ensure failed");
            return EnsureRecommendedSkillRefsFailure("recommended_skill_ref_creation_failed");
        }
    }

    private static NyxIdServiceInstance? ResolveServiceInstance(
        IReadOnlyList<NyxIdServiceInstance> instances,
        string? userServiceId,
        string? serviceSlug)
    {
        var matches = instances.Where(instance =>
            (userServiceId is null || string.Equals(instance.UserServiceId, userServiceId, StringComparison.Ordinal)) &&
            (serviceSlug is null || string.Equals(instance.DisplaySlug, serviceSlug, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(instance.CatalogServiceSlug, serviceSlug, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
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
            return OperationFailure(callId, "operation_context_unavailable", arguments);

        var bindingId = Normalize(context.SenderBinding.BindingId);
        if (bindingId is not null)
        {
            if (_capabilityIssuer is null || !TryBuildSubject(context, out var subject))
                return OperationFailure(callId, "inventory_capability_unavailable", arguments);

            try
            {
                var capability = await _capabilityIssuer
                    .IssueByBindingIdAsync(subject, bindingId, ct)
                    .ConfigureAwait(false);
                var token = Normalize(capability.AccessToken);
                if (token is null)
                    return OperationFailure(callId, "inventory_capability_unavailable", arguments);

                var senderContext = ChannelConnectedServiceCredentialPolicy.Apply(
                    context,
                    token,
                    registrationAgentKey: null);
                _logger.LogInformation(
                    "NyxID connected-service operation using sender binding credential. bindingIdPresent={BindingIdPresent} credentialSource={CredentialSource} credentialKind={CredentialKind} authority={CredentialAuthority}",
                    true,
                    senderContext.CredentialSource,
                    senderContext.Credentials.NyxIdCredentialKind,
                    senderContext.Credentials.NyxIdCredentialAuthority);
                return await ExecuteOperationWithContextAsync(senderContext, callId, arguments, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (BindingRevokedException)
            {
                return OperationFailure(callId, "inventory_binding_revoked", arguments);
            }
            catch (BindingScopeMismatchException)
            {
                return OperationFailure(callId, "inventory_scope_unavailable", arguments);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NyxID connected-service operation capability issue failed");
                return OperationFailure(callId, "inventory_capability_unavailable", arguments);
            }
        }

        if (IsRegistrationAgentKeyContext(context))
            return await ExecuteOperationWithContextAsync(context, callId, arguments, ct).ConfigureAwait(false);

        return OperationFailure(callId, "inventory_capability_unavailable", arguments);
    }

    private async Task<AgentToolTerminalOutcome> ExecuteOperationWithContextAsync(
        AgentToolExecutionContext context,
        string callId,
        OperationArguments arguments,
        CancellationToken ct)
    {
        if (_options is null || _apiClientFactory is null)
            return OperationFailure(callId, "operation_source_unavailable", arguments);

        try
        {
            var apiClient = _apiClientFactory.CreateClient();
            var invoker = new NyxIdConnectedServiceOperationInvoker(
                _options,
                apiClient,
                new NyxIdServiceInstanceClient(apiClient));
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
                        arguments.DocumentRequest,
                        arguments.RawRequest),
                    callId,
                    "nyxid_invoke_operation",
                    ct)
                .ConfigureAwait(false);
            if (result.IsSuccess && result.Outcome is not null)
                return result.Outcome;

            _logger.LogWarning(
                "NyxID connected-service operation invocation returned failure. failureCode={FailureCode} userServiceId={UserServiceId} serviceSlug={ServiceSlug} operationId={OperationId} hasDocumentRequest={HasDocumentRequest} hasRawRequest={HasRawRequest}",
                result.FailureCode,
                arguments.UserServiceId,
                arguments.ServiceSlug,
                arguments.OperationId,
                arguments.DocumentRequest is not null,
                arguments.RawRequest is not null);
            return OperationFailure(callId, result.FailureCode, arguments, result.SuggestedSkillRefs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service operation invocation failed");
            return OperationFailure(callId, "operation_source_unavailable", arguments);
        }
    }

    private async Task<NyxIdServiceInventoryResult> ReadInventoryResultAsync(
        NyxIdConnectedServiceInventoryReader reader,
        string token,
        InventoryReadAuthority inventoryReadAuthority,
        CancellationToken ct)
    {
        try
        {
            var inventory = inventoryReadAuthority switch
            {
                InventoryReadAuthority.AgentKey => await reader.ReadAgentKeyAsync(token, ct).ConfigureAwait(false),
                _ => await reader.ReadAsync(token, organizationToken: null, ct).ConfigureAwait(false),
            };
            if (inventoryReadAuthority == InventoryReadAuthority.AgentKey &&
                NyxIdAgentKeyInventoryFallback.TrySupplementMissingRecommendedSkillRefs(
                    _options,
                    token,
                    _logger,
                    inventory))
            {
                _logger.LogWarning(
                    "NyxID Agent Key connected-service inventory is missing recommended skill refs; using configured local fallback refs");
            }

            return inventory;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NyxIdServiceInventoryContractException)
        {
            throw;
        }
        catch (Exception ex) when (inventoryReadAuthority == InventoryReadAuthority.AgentKey &&
                                   NyxIdAgentKeyInventoryFallback.TryReadInventory(
                                       _options,
                                       token,
                                       _logger,
                                       out var fallbackInventory))
        {
            _logger.LogWarning(
                ex,
                "NyxID Agent Key connected-service inventory read failed; using configured local inventory fallback");
            return fallbackInventory;
        }
    }

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
            var ensureResult = await EnsureInventoryRecommendedSkillRefsAsync(result, token, ct).ConfigureAwait(false);
            return ResultFormatter.Format(ensureResult.Inventory);
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

    private async Task<InventoryRecommendedSkillRefEnsureResult> EnsureInventoryRecommendedSkillRefsAsync(
        NyxIdServiceInventoryResult inventory,
        string documentAccessToken,
        CancellationToken ct)
    {
        if (_recommendedSkillRefCreator is null || inventory.Instances.Count == 0)
            return new InventoryRecommendedSkillRefEnsureResult(inventory, []);

        var updated = false;
        var createdSkills = new List<NyxIdCreatedRecommendedSkill>();
        foreach (var service in inventory.Instances)
        {
            if (service.RecommendedSkillRefs.Count > 0)
                continue;

            NyxIdRecommendedSkillRefCreationResult creationResult;
            try
            {
                creationResult = await _recommendedSkillRefCreator.CreateRecommendedSkillRefsAsync(service, documentAccessToken, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "NyxID recommended skill ref auto-provision failed during inventory read. userServiceId={UserServiceId} serviceSlug={ServiceSlug}",
                    service.UserServiceId,
                    service.DisplaySlug);
                continue;
            }

            if (creationResult.Refs.Count == 0)
                continue;

            service.RecommendedSkillRefs.Add(creationResult.Refs.Select(static skillRef => skillRef.Clone()));
            createdSkills.AddRange(creationResult.CreatedSkills);
            updated = true;
        }

        if (!updated)
            return new InventoryRecommendedSkillRefEnsureResult(inventory, createdSkills);

        inventory.RecommendedSkillCatalog.Clear();
        inventory.RecommendedSkillCatalog.Add(inventory.Instances.SelectMany(BuildRecommendedSkillCatalogEntries));
        return new InventoryRecommendedSkillRefEnsureResult(inventory, createdSkills);
    }

    private async Task<string> RegenerateRecommendedSkillAsync(
        NyxIdServiceInventoryResult inventory,
        NyxIdServiceInstance service,
        NyxIdRecommendedSkillRef staleSkillRef,
        RecommendedSkillArguments arguments,
        string documentAccessToken,
        CancellationToken ct)
    {
        if (_recommendedSkillRefCreator is null)
            return RecommendedSkillUnavailable("recommended_skill_unavailable");

        NyxIdRecommendedSkillRefCreationResult creationResult;
        try
        {
            creationResult = await _recommendedSkillRefCreator
                .CreateRecommendedSkillRefsAsync(service, documentAccessToken, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "NyxID recommended skill regeneration skipped after failure. userServiceId={UserServiceId} skillId={SkillId}",
                service.UserServiceId,
                staleSkillRef.SkillId);
            return RecommendedSkillUnavailable("recommended_skill_unavailable");
        }

        if (creationResult.Refs.Count == 0 || creationResult.CreatedSkills.Count == 0)
        {
            _logger.LogInformation(
                "NyxID recommended skill regeneration skipped because no generated skill was available. userServiceId={UserServiceId} serviceSlug={ServiceSlug} failureCode={FailureCode}",
                service.UserServiceId,
                service.DisplaySlug,
                creationResult.PersistenceFailureCode);
            return RecommendedSkillUnavailable(FirstNonEmpty(
                creationResult.PersistenceFailureCode,
                "recommended_skill_unavailable"));
        }

        for (var index = service.RecommendedSkillRefs.Count - 1; index >= 0; index--)
        {
            if (SameRecommendedSkillRef(service.RecommendedSkillRefs[index], arguments))
                service.RecommendedSkillRefs.RemoveAt(index);
        }

        service.RecommendedSkillRefs.Add(
            creationResult.Refs.Select(static skillRef => skillRef.Clone()));
        inventory.RecommendedSkillCatalog.Clear();
        inventory.RecommendedSkillCatalog.Add(
            inventory.Instances.SelectMany(BuildRecommendedSkillCatalogEntries));

        if (TryLoadGeneratedRecommendedSkill(
                creationResult.CreatedSkills,
                arguments,
                service,
                out var generatedSkillJson))
        {
            return generatedSkillJson;
        }

        if (creationResult.Refs.Count == 1 && creationResult.CreatedSkills.Count == 1)
        {
            var generatedSkill = creationResult.CreatedSkills[0];
            if (SameRecommendedSkillRef(generatedSkill.Ref, creationResult.Refs[0]))
            {
                return SerializeGeneratedRecommendedSkill(generatedSkill, service);
            }
        }

        return RecommendedSkillFailure("recommended_skill_ref_creation_failed");
    }

    private static bool TryLoadGeneratedRecommendedSkill(
        IReadOnlyList<NyxIdCreatedRecommendedSkill> createdSkills,
        RecommendedSkillArguments arguments,
        NyxIdServiceInstance service,
        out string resultJson)
    {
        var createdSkill = createdSkills.FirstOrDefault(skill => SameRecommendedSkillRef(skill.Ref, arguments));
        if (createdSkill is null)
        {
            resultJson = string.Empty;
            return false;
        }

        resultJson = SerializeGeneratedRecommendedSkill(createdSkill, service);
        return true;
    }

    private static string SerializeGeneratedRecommendedSkill(
        NyxIdCreatedRecommendedSkill createdSkill,
        NyxIdServiceInstance service) =>
        JsonSerializer.Serialize(new
        {
            result_type = "nyxid_recommended_skill_load",
            status = "success",
            loaded = true,
            service_instance_id = service.UserServiceId,
            service_label = service.Label,
            skill = new
            {
                source = "ornn",
                skill_id = createdSkill.Ref.SkillId,
                literal_version = createdSkill.Ref.LiteralVersion,
                name = createdSkill.Name,
                publisher_id = createdSkill.PublisherId,
                manifest_digest = createdSkill.Ref.ManifestDigest,
                recommended_name = createdSkill.Ref.RecommendationName,
                revision = createdSkill.Ref.Revision,
            },
            main_document = createdSkill.MainDocument,
            resources = Array.Empty<object>(),
        });

    private static bool SameRecommendedSkillRef(NyxIdRecommendedSkillRef skillRef, RecommendedSkillArguments arguments) =>
        skillRef.Source == NyxIdRecommendedSkillSource.Ornn &&
        string.Equals(skillRef.SkillId, arguments.SkillId, StringComparison.Ordinal) &&
        string.Equals(skillRef.LiteralVersion, arguments.LiteralVersion, StringComparison.Ordinal) &&
        string.Equals(skillRef.ManifestDigest, arguments.ManifestDigest, StringComparison.Ordinal);

    private static bool SameRecommendedSkillRef(
        NyxIdRecommendedSkillRef left,
        NyxIdRecommendedSkillRef right) =>
        left.Source == right.Source &&
        string.Equals(left.SkillId, right.SkillId, StringComparison.Ordinal) &&
        string.Equals(left.LiteralVersion, right.LiteralVersion, StringComparison.Ordinal) &&
        string.Equals(left.ManifestDigest, right.ManifestDigest, StringComparison.Ordinal);

    private static IEnumerable<NyxIdRecommendedSkillCatalogEntry> BuildRecommendedSkillCatalogEntries(
        NyxIdServiceInstance service)
    {
        foreach (var skillRef in service.RecommendedSkillRefs)
        {
            var title = FirstNonEmpty(skillRef.DisplayName, skillRef.RecommendationName, skillRef.SkillId);
            yield return new NyxIdRecommendedSkillCatalogEntry
            {
                UserServiceId = service.UserServiceId,
                ServiceSlug = service.DisplaySlug,
                ServiceLabel = FirstNonEmpty(service.Label, service.DisplaySlug, service.CatalogServiceSlug),
                SkillRef = skillRef.Clone(),
                Title = title,
                TaskSummary = $"Load this recommended skill for {FirstNonEmpty(service.Label, service.DisplaySlug, service.CatalogServiceSlug)} connected-service tasks related to {title}.",
            };
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string CreateInventoryReadCallId(string? outerCallId) =>
        $"{Normalize(outerCallId) ?? "missing"}:inventory-read";

    private static string CreateRecommendedSkillLoadCallId(string? outerCallId) =>
        $"{Normalize(outerCallId) ?? "missing"}:recommended-skill-load";

    private static string CreateRecommendedSkillRefsEnsureCallId(string? outerCallId) =>
        $"{Normalize(outerCallId) ?? "missing"}:recommended-skill-refs-ensure";

    private static string CreateOperationInvokeCallId(string? outerCallId) =>
        $"{Normalize(outerCallId) ?? "missing"}:connected-operation";

    private static EnsureRecommendedSkillRefsArguments? ParseEnsureRecommendedSkillRefsArguments(string argumentsJson)
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
                if (property.Name is not ("user_service_id" or "service_slug"))
                    return null;
            }

            var userServiceId = ReadOptionalString(root, "user_service_id");
            var serviceSlug = ReadOptionalString(root, "service_slug");
            return userServiceId is null && serviceSlug is null
                ? null
                : new EnsureRecommendedSkillRefsArguments(userServiceId, serviceSlug);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
                if (property.Name is not ("user_service_id" or "service_slug" or "operation_id" or "operation_arguments" or
                    "document_request" or "method" or "relative_path" or "query" or "headers" or "body"))
                {
                    return null;
                }
            }

            var userServiceId = ReadOptionalString(root, "user_service_id");
            var serviceSlug = ReadOptionalString(root, "service_slug");
            if (userServiceId is null && serviceSlug is null)
                return null;

            var hasTypedOperation = root.TryGetProperty("operation_id", out _);
            var hasDocumentRequest = root.TryGetProperty("document_request", out var documentRequestElement);
            var hasRawRequest = root.TryGetProperty("method", out _) ||
                                root.TryGetProperty("relative_path", out _) ||
                                root.TryGetProperty("query", out _) ||
                                root.TryGetProperty("headers", out _) ||
                                root.TryGetProperty("body", out _);
            if ((hasTypedOperation ? 1 : 0) + (hasDocumentRequest ? 1 : 0) + (hasRawRequest ? 1 : 0) != 1)
                return null;
            if ((hasDocumentRequest || hasRawRequest) && root.TryGetProperty("operation_arguments", out _))
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
                        documentRequest,
                        RawRequest: null);
            }

            if (hasRawRequest)
            {
                var rawRequest = ParseRawRequest(root);
                return rawRequest is null
                    ? null
                    : new OperationArguments(
                        userServiceId,
                        serviceSlug,
                        OperationId: null,
                        OperationArgumentsJson: "{}",
                        DocumentRequest: null,
                        rawRequest);
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
                DocumentRequest: null,
                RawRequest: null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NyxIdConnectedServiceRawRequest? ParseRawRequest(JsonElement root) =>
        TryReadAuthoredRequest(root, out var method, out var relativePath, out var requestArgumentsJson)
            ? new NyxIdConnectedServiceRawRequest(method, relativePath, requestArgumentsJson)
            : null;

    private static NyxIdConnectedServiceDocumentRequest? ParseDocumentRequest(JsonElement documentRequest)
    {
        if (documentRequest.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in documentRequest.EnumerateObject())
        {
            if (property.Name is not ("method" or "relative_path" or "skill_ref" or "query" or "headers" or "body"))
                return null;
        }

        if (!documentRequest.TryGetProperty("skill_ref", out var skillRefElement) ||
            !TryReadDocumentSkillRef(skillRefElement, out var skillRef) ||
            !TryReadAuthoredRequest(
                documentRequest,
                out var method,
                out var relativePath,
                out var requestArgumentsJson))
        {
            return null;
        }

        return new NyxIdConnectedServiceDocumentRequest(
            method,
            relativePath,
            requestArgumentsJson,
            skillRef);
    }

    private static bool TryReadAuthoredRequest(
        JsonElement root,
        out string method,
        out string relativePath,
        out string requestArgumentsJson)
    {
        method = ReadRequiredString(root, "method") ?? string.Empty;
        relativePath = ReadRequiredString(root, "relative_path") ?? string.Empty;
        requestArgumentsJson = string.Empty;
        if (method.Length == 0 || relativePath.Length == 0)
            return false;

        var runtimeArguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("query", out var query))
        {
            if (query.ValueKind != JsonValueKind.Object)
                return false;
            runtimeArguments["query"] = query;
        }
        if (root.TryGetProperty("headers", out var headers))
        {
            if (headers.ValueKind != JsonValueKind.Object)
                return false;
            runtimeArguments["headers"] = headers;
        }
        if (root.TryGetProperty("body", out var body))
        {
            if (body.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return false;
            runtimeArguments["body"] = body;
        }

        requestArgumentsJson = JsonSerializer.Serialize(runtimeArguments);
        return true;
    }

    private static bool TryReadDocumentSkillRef(JsonElement root, out NyxIdRecommendedSkillRef skillRef)
    {
        skillRef = new NyxIdRecommendedSkillRef();
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("source" or "skill_id" or "literal_version" or "manifest_digest"))
                return false;
        }

        var source = ReadRequiredString(root, "source");
        var skillId = ReadRequiredString(root, "skill_id");
        var literalVersion = ReadRequiredString(root, "literal_version");
        var manifestDigest = NormalizeRecommendedSkillManifestDigest(ReadRequiredString(root, "manifest_digest"));
        if (!string.Equals(source, "ornn", StringComparison.Ordinal) ||
            skillId is null ||
            literalVersion is null ||
            manifestDigest is null)
        {
            return false;
        }

        skillRef = new NyxIdRecommendedSkillRef
        {
            Source = NyxIdRecommendedSkillSource.Ornn,
            SkillId = skillId,
            LiteralVersion = literalVersion,
            ManifestDigest = manifestDigest,
        };
        return true;
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

    private static string? NormalizeRecommendedSkillManifestDigest(string? value)
    {
        var digest = Normalize(value);
        if (digest is null)
            return null;

        const string prefix = "sha256:";
        if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var hex = digest[prefix.Length..];
            return IsSha256Hex(hex)
                ? prefix + hex.ToLowerInvariant()
                : digest;
        }

        return IsSha256Hex(digest)
            ? prefix + digest.ToLowerInvariant()
            : digest;
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

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

    private static AgentToolTerminalOutcome OperationFailure(
        string callId,
        string errorCode,
        OperationArguments? arguments = null,
        IReadOnlyList<NyxIdRecommendedSkillRef>? suggestedSkillRefs = null)
    {
        var targetService = OperationTargetService(arguments);
        var errorMessage = OperationFailureMessage(errorCode, targetService);
        var guidanceOnly = IsOperationGuidance(errorCode);
        var resultJson = JsonSerializer.Serialize(new
        {
            result_type = "nyxid_connected_operation_invoke",
            status = guidanceOnly ? "guidance" : "failed",
            invoked = false,
            error = errorCode,
            message = errorMessage,
            user_service_id = arguments?.UserServiceId,
            service_slug = arguments?.ServiceSlug,
            next_action = OperationFailureNextAction(errorCode),
            suggested_skill_refs = ToSuggestedSkillRefs(suggestedSkillRefs),
        });
        return new AgentToolTerminalOutcome(resultJson, new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = "nyxid_invoke_operation",
            Status = guidanceOnly ? AgentToolReceiptStatus.Success : AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = guidanceOnly ? string.Empty : errorCode,
            ErrorMessage = guidanceOnly ? string.Empty : errorMessage,
            ResultJson = resultJson,
        });
    }

    private static string OperationTargetService(OperationArguments? arguments) =>
        FirstNonEmpty(arguments?.ServiceSlug, arguments?.UserServiceId);

    private static bool IsOperationGuidance(string errorCode) =>
        string.Equals(errorCode, "document_request_required", StringComparison.Ordinal);

    private static string? OperationFailureNextAction(string errorCode) => errorCode switch
    {
        "document_request_required" => "Call nyxid_invoke_operation again with document_request. Copy one suggested_skill_refs entry into document_request.skill_ref, set method and relative_path from the loaded recommended skill operation details, and do not use operation_id.",
        _ => null,
    };

    private static object[] ToSuggestedSkillRefs(IReadOnlyList<NyxIdRecommendedSkillRef>? skillRefs) =>
        skillRefs is null || skillRefs.Count == 0
            ? []
            : skillRefs.Select(static skillRef => new
            {
                source = "ornn",
                skill_id = skillRef.SkillId,
                literal_version = skillRef.LiteralVersion,
                manifest_digest = skillRef.ManifestDigest,
                recommended_name = skillRef.RecommendationName,
                revision = skillRef.Revision,
            }).ToArray<object>();

    private static string OperationFailureMessage(string errorCode, string targetService) => errorCode switch
    {
        "document_request_required" when !string.IsNullOrWhiteSpace(targetService) =>
            $"The service '{targetService}' exposes this operation through a loaded recommended skill. Retry with document_request and the exact suggested skill_ref; do not use operation_id.",
        "document_request_required" =>
            "The selected NyxID service exposes this operation through a loaded recommended skill. Retry with document_request and the exact suggested skill_ref; do not use operation_id.",
        "operation_not_visible" when !string.IsNullOrWhiteSpace(targetService) =>
            $"The requested connected-service operation is not visible for service '{targetService}'. Verify the service is admitted for this channel, the operation id is current, and the service exposes the operation.",
        "operation_not_visible" =>
            "The requested connected-service operation is not visible for the selected NyxID service. Verify the service admission, operation id, and service exposure policy.",
        "operation_ambiguous" when !string.IsNullOrWhiteSpace(targetService) =>
            $"The requested connected-service operation matched multiple NyxID service instances for service '{targetService}'. Use user_service_id to select one instance.",
        "operation_ambiguous" =>
            "The requested connected-service operation matched multiple NyxID service instances.",
        "operation_context_unavailable" =>
            "The connected-service operation credential context is unavailable.",
        "document_request_not_admitted" =>
            "The requested document-guided operation is not admitted by the selected recommended skill ref.",
        "document_request_invalid" =>
            "The requested document-guided operation request is invalid.",
        "raw_request_invalid" =>
            "The delegated connected-service operation request is invalid.",
        "inventory_capability_unavailable" =>
            "The connected-service operation inventory capability is unavailable.",
        "inventory_binding_revoked" =>
            "The connected-service operation binding was revoked.",
        "inventory_scope_unavailable" =>
            "The connected-service operation binding scope is unavailable.",
        "invalid_arguments" =>
            "The connected-service operation arguments are invalid.",
        _ => "The connected-service operation could not be invoked.",
    };

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

    private static string RecommendedSkillUnavailable(string errorCode) =>
        JsonSerializer.Serialize(new
        {
            result_type = "nyxid_recommended_skill_load",
            status = "unavailable",
            loaded = false,
            error = errorCode,
            message = "No generated recommended skill is available for this connected service. Continue without this skill.",
        });

    private static string EnsureRecommendedSkillRefsFailure(string errorCode) =>
        JsonSerializer.Serialize(new
        {
            result_type = "nyxid_recommended_skill_refs_ensure",
            status = "failed",
            ensured = false,
            error = errorCode,
        });

    private static string RecommendedSkillRefsUnavailable(
        NyxIdServiceInstance service,
        string errorCode) =>
        JsonSerializer.Serialize(new
        {
            result_type = "nyxid_recommended_skill_refs_ensure",
            status = "success",
            ensured = false,
            created = false,
            service_instance_id = service.UserServiceId,
            service_slug = service.DisplaySlug,
            catalog_service_slug = service.CatalogServiceSlug,
            recommended_skill_refs = Array.Empty<object>(),
            error = errorCode,
            message = "No generated recommended skill is available for this connected service. Continue without recommended skill refs.",
        });

    private static string RecommendedSkillRefsEnsured(
        NyxIdServiceInstance service,
        IReadOnlyList<NyxIdRecommendedSkillRef> refs,
        bool created) =>
        JsonSerializer.Serialize(new
        {
            result_type = "nyxid_recommended_skill_refs_ensure",
            status = "success",
            ensured = true,
            created,
            service_instance_id = service.UserServiceId,
            service_slug = service.DisplaySlug,
            catalog_service_slug = service.CatalogServiceSlug,
            recommended_skill_refs = refs.Select(ToRecommendedSkillRefResult).ToArray(),
        });

    private static string RecommendedSkillRefsEnsured(
        NyxIdServiceInstance service,
        NyxIdRecommendedSkillRefCreationResult creationResult,
        bool created) =>
        JsonSerializer.Serialize(new
        {
            result_type = "nyxid_recommended_skill_refs_ensure",
            status = "success",
            ensured = true,
            created,
            service_instance_id = service.UserServiceId,
            service_slug = service.DisplaySlug,
            catalog_service_slug = service.CatalogServiceSlug,
            recommended_skill_refs = creationResult.Refs.Select(ToRecommendedSkillRefResult).ToArray(),
            recommended_skill_ref_persistence_status = creationResult.PersistenceStatus.ToString(),
            recommended_skill_ref_persistence_failure_code = creationResult.PersistenceFailureCode,
        });

    private static object ToRecommendedSkillRefResult(NyxIdRecommendedSkillRef skillRef) => new
    {
        source = skillRef.Source == NyxIdRecommendedSkillSource.Ornn ? "ornn" : string.Empty,
        skill_id = skillRef.SkillId,
        literal_version = skillRef.LiteralVersion,
        manifest_digest = skillRef.ManifestDigest,
        display_name = skillRef.DisplayName,
        recommendation_name = skillRef.RecommendationName,
        revision = skillRef.Revision,
    };

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

            if (string.Equals(status, "unavailable", StringComparison.Ordinal))
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

    private static AgentToolReceipt? CreateEnsureRecommendedSkillRefsReceipt(
        string callId,
        string toolName,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadOptionalString(root, "result_type"), "nyxid_recommended_skill_refs_ensure", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(ReadOptionalString(root, "status"), "success", StringComparison.Ordinal))
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

            var errorCode = ReadOptionalString(root, "error") ?? "nyxid_recommended_skill_refs_ensure_failed";
            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                Status = AgentToolReceiptStatus.Error,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                ErrorCode = errorCode,
                ErrorMessage = "The recommended skill refs could not be ensured.",
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
        NyxIdConnectedServiceDocumentRequest? DocumentRequest,
        NyxIdConnectedServiceRawRequest? RawRequest);

    private sealed record RecommendedSkillArguments(
        string UserServiceId,
        string SkillId,
        string LiteralVersion,
        string ManifestDigest);

    private sealed record EnsureRecommendedSkillRefsArguments(
        string? UserServiceId,
        string? ServiceSlug);

    private sealed record InventoryRecommendedSkillRefEnsureResult(
        NyxIdServiceInventoryResult Inventory,
        IReadOnlyList<NyxIdCreatedRecommendedSkill> CreatedSkills);

    private sealed class SenderConnectedServiceOperationTool(ChannelNyxIdConnectedServiceInventoryToolSource source) : IAgentTool
    {
        private const string Schema =
            """
            {
              "type":"object",
              "properties":{
                "user_service_id":{"type":"string","description":"Exact user_service_id from nyxid_service_inventory when available."},
                "service_slug":{"type":"string","description":"Exact connected-service slug from inventory or channel runtime selectors."},
                "operation_id":{"type":"string","description":"Typed operation mode only for operation ids visible in nyxid_service_inventory contracts. Do not use this for operations listed only in a loaded recommended skill; use document_request instead."},
                "operation_arguments":{"type":"object","description":"Typed mode only. Only path_params, query, headers, body, and response_mode values declared by the visible operation contract.","additionalProperties":true},
                "method":{"type":"string","description":"Raw delegated mode only. HTTP method for a service-relative request when operation_id is omitted.","enum":["GET","HEAD","OPTIONS","POST","PUT","PATCH","DELETE"]},
                "relative_path":{"type":"string","description":"Raw delegated mode only. Safe relative service path. Absolute URLs, query strings, fragments, and traversal are rejected."},
                "query":{"type":"object","description":"Raw delegated mode only. Query string values for the service-relative request.","additionalProperties":{"type":"string"}},
                "headers":{"type":"object","description":"Raw delegated mode only. Non-sensitive headers. Authorization, Host, cookies, API keys, and tokens are rejected.","additionalProperties":{"type":"string"}},
                "body":{"type":"object","description":"Raw delegated mode only. JSON object body for methods that allow a body.","additionalProperties":true},
                "document_request":{
                  "type":"object",
                  "description":"Document-guided mode only. Use when the loaded recommended skill explicitly requires document_request for OpenAPI-backed service operations. This is not a fallback for an unlisted operation.",
                  "properties":{
                    "method":{"type":"string","enum":["GET","HEAD","OPTIONS","POST","PUT","PATCH","DELETE"]},
                    "relative_path":{"type":"string","description":"Safe relative service path from the loaded skill documentation. Absolute URLs, query strings, fragments, and traversal are rejected."},
                    "skill_ref":{
                      "type":"object",
                      "description":"Exact recommended_skill_refs entry that authorized this document-guided request.",
                      "properties":{
                        "source":{"type":"string","enum":["ornn"]},
                        "skill_id":{"type":"string"},
                        "literal_version":{"type":"string"},
                        "manifest_digest":{"type":"string"}
                      },
                      "required":["source","skill_id","literal_version","manifest_digest"],
                      "additionalProperties":false
                    },
                    "query":{"type":"object","additionalProperties":{"type":"string"}},
                    "headers":{"type":"object","additionalProperties":{"type":"string"}},
                    "body":{"type":"object","additionalProperties":true}
                  },
                  "required":["method","relative_path","skill_ref"],
                  "additionalProperties":false
                }
              },
              "anyOf":[{"required":["user_service_id"]},{"required":["service_slug"]}],
              "oneOf":[{"required":["operation_id"]},{"required":["document_request"]},{"required":["method","relative_path"]}],
              "additionalProperties":false
            }
            """;

        public string Name => "nyxid_invoke_operation";
        public string Description =>
            "Invoke one current NyxID connected-service request selected by exact service identity. " +
            "Use a typed operation id or document guidance when available; otherwise provide a service-relative delegated request.";
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

    private sealed class SenderEnsureRecommendedSkillRefsTool(ChannelNyxIdConnectedServiceInventoryToolSource source) : IAgentTool
    {
        private const string Schema =
            """
            {
              "type":"object",
              "properties":{
                "user_service_id":{"type":"string","description":"Exact user_service_id from nyxid_service_inventory when available."},
                "service_slug":{"type":"string","description":"Exact connected-service slug from inventory or channel runtime selectors."}
              },
              "anyOf":[{"required":["user_service_id"]},{"required":["service_slug"]}],
              "additionalProperties":false
            }
            """;

        public string Name => "nyxid_ensure_recommended_skill_refs";
        public string Description =>
            "Explicitly create and persist missing NyxID recommended Ornn skill refs for one visible connected-service instance.";
        public string ParametersSchema => Schema;
        public bool IsReadOnly => false;
        public bool IsDestructive => false;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public string SideEffectKind => "connected_service_recommended_skill_refs";

        public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
            AgentToolReplayPolicy.NonReplayable;

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            CreateEnsureRecommendedSkillRefsReceipt(callId, toolName, resultJson);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.ExecuteEnsureRecommendedSkillRefsAsync(argumentsJson, ct);
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

    private sealed class SenderEnsureRecommendedSkillRefsProvisionerTool(
        ChannelNyxIdConnectedServiceInventoryToolSource source,
        NyxIdConnectedServiceInventoryReader reader,
        INyxIdRecommendedSkillRefCreator recommendedSkillRefCreator,
        string token,
        InventoryReadAuthority inventoryReadAuthority,
        EnsureRecommendedSkillRefsArguments arguments) : IAgentTool
    {
        public string Name => "nyxid_recommended_skill_refs_provisioner";
        public string Description => "Read one visible connected service and create missing recommended skill refs.";
        public string ParametersSchema =>
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";
        public bool IsReadOnly => false;
        public bool IsDestructive => false;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public string SideEffectKind => "connected_service_recommended_skill_refs";

        public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
            AgentToolReplayPolicy.NonReplayable;

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            CreateEnsureRecommendedSkillRefsReceipt(callId, toolName, resultJson);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            source.EnsureRecommendedSkillRefsAsync(
                reader,
                recommendedSkillRefCreator,
                token,
                inventoryReadAuthority,
                arguments,
                ct);
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
