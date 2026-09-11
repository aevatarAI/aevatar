using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelAgentKeyProvisioningServiceTests
{
    private const string RawFullKey = "nyxid_ag_channel_secret_alpha";

    [Fact]
    public void DefaultProvisioning_HasNoOwnerlessPublicOverload()
    {
        var ownerlessOverloads = typeof(ChannelAgentKeyProvisioningService)
            .GetMethods()
            .Where(method => method.Name == nameof(ChannelAgentKeyProvisioningService.ProvisionAsync))
            .Select(method => method.GetParameters())
            .Where(parameters =>
                parameters.Length == 6 &&
                parameters.All(parameter =>
                    parameter.ParameterType != typeof(VerifiedChannelRegistrationOwner) &&
                    parameter.ParameterType != typeof(VerifiedChannelRegistrationExplicitAuthorization)))
            .ToArray();

        ownerlessOverloads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("org-alpha", false)]
    [InlineData(null, true)]
    [InlineData("org-alpha", true)]
    public async Task ProvisionExplicitAsync_UsesOnlyVerifiedPlanAndPersistsItsDigest(string? organizationId, bool requiresNodes)
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync(organizationId, requiresNodes);
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", CreateResponse(
            allowAllServicesJson: "false", allowAllNodesJson: "false", allowedServiceIdsJson: "[\"svc-alpha\"]",
            allowedNodeIdsJson: JsonSerializer.Serialize(authorization.Plan.AllowedNodeIds)));
        var credential = await ProvisionExplicitAsync(CreateService(handler, new InMemorySecretVault()), authorization);

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        var root = body.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(organizationId is null
            ? ["name", "scopes", "platform", "callback_url", "allow_all_services", "allow_all_nodes", "allowed_service_ids", "allowed_node_ids", "scope_plan_digest"]
            : new[] { "name", "scopes", "platform", "callback_url", "allow_all_services", "allow_all_nodes", "allowed_service_ids", "allowed_node_ids", "scope_plan_digest", "target_org_id" });
        root.GetProperty("scopes").GetString().Should().Be("read write proxy");
        root.GetProperty("platform").GetString().Should().Be("generic");
        root.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
        root.GetProperty("allow_all_nodes").GetBoolean().Should().BeFalse();
        root.GetProperty("allowed_service_ids").EnumerateArray().Select(x => x.GetString()).Should().Equal(authorization.Plan.AllowedServiceIds);
        root.GetProperty("allowed_node_ids").EnumerateArray().Select(x => x.GetString()).Should().Equal(authorization.Plan.AllowedNodeIds);
        root.GetProperty("scope_plan_digest").GetString().Should().Be(authorization.Plan.ScopePlanDigest);
        if (organizationId is not null)
            root.GetProperty("target_org_id").GetString().Should().Be(organizationId);
        credential.Grant.ScopePlanDigest.Should().Be(authorization.Plan.ScopePlanDigest);
        credential.Grant.AllowedServiceIds.Should().Equal("svc-alpha");
        credential.ToString().Should().NotContain(RawFullKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("org-alpha")]
    public async Task ProvisionExplicitAsync_ScopeDoesNotMatchVerifiedKeyOwner_RejectsBeforeCreate(
        string? organizationId)
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync(
            organizationId);
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", RestrictedResponse());
        var vault = new RecordingSecretVault();
        var service = CreateService(handler, vault);

        var act = () => service.ProvisionAsync(
            "telegram",
            "owner-token",
            "https://aevatar.example.com/api/webhooks/nyxid-relay",
            "scope-other",
            "reg-alpha",
            authorization,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be("service_owner_forbidden");
        handler.Requests.Should().BeEmpty();
        vault.PutRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("services")]
    [InlineData("nodes")]
    [InlineData("allow-services")]
    [InlineData("allow-nodes")]
    [InlineData("scopes")]
    [InlineData("purpose")]
    [InlineData("secret")]
    public async Task ProvisionExplicitAsync_ResponseDrift_CompensatesBeforeVaultWithoutLeakingSecrets(string drift)
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", CreateResponse(
            fullKey: drift == "secret" ? null : RawFullKey,
            purpose: drift == "purpose" ? "scheduled_invocation" : "general",
            scopes: drift == "scopes" ? "proxy" : "read write proxy",
            allowAllServicesJson: drift == "allow-services" ? "true" : "false",
            allowAllNodesJson: drift == "allow-nodes" ? "true" : "false",
            allowedServiceIdsJson: drift == "services" ? "[\"svc-other\"]" : "[\"svc-alpha\"]",
            allowedNodeIdsJson: drift == "nodes" ? "[\"node-other\"]" : "[]"));
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-alpha", "{}");
        var vault = new RecordingSecretVault();
        var act = () => ProvisionExplicitAsync(CreateService(handler, vault), authorization);
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be("channel_authorization_contract_invalid");
        failure.Which.ToString().Should().NotContain(RawFullKey);
        handler.Requests.Select(x => x.Method).Should().Equal(HttpMethod.Post, HttpMethod.Delete);
        vault.PutRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"error\":true,\"status\":409,\"body\":\"{\\\"error\\\":\\\"scope_plan_changed\\\",\\\"message\\\":\\\"nyxid_ag_channel_secret_alpha\\\"}\"}", "scope_plan_changed")]
    [InlineData("{\"error\":true,\"status\":503,\"body\":\"nyxid_ag_channel_secret_alpha\"}", "channel_authorization_contract_invalid")]
    [InlineData("not-json nyxid_ag_channel_secret_alpha", "channel_authorization_contract_invalid")]
    [InlineData("{\"error\":true,\"status\":\"secret\",\"body\":\"{}\"}", "channel_authorization_contract_invalid")]
    public async Task ProvisionExplicitAsync_ErrorWithoutCreatedId_DoesNotInventCleanup(string response, string code)
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", response);
        var vault = new RecordingSecretVault();
        var act = () => ProvisionExplicitAsync(CreateService(handler, vault), authorization);
        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Be(code);
        error.Which.ToString().Should().NotContain(RawFullKey);
        handler.Requests.Should().ContainSingle();
        vault.PutRequests.Should().BeEmpty();
    }

    private static string RestrictedResponse() => CreateResponse(
        allowAllServicesJson: "false", allowAllNodesJson: "false", allowedServiceIdsJson: "[\"svc-alpha\"]");

    [Theory]
    [InlineData("\"allow_all_services\":true,")]
    [InlineData("\"purpose\":\"scheduled_invocation\",")]
    [InlineData("\"scopes\":\"admin\",")]
    public async Task ProvisionExplicitAsync_DuplicateContractField_FailsClosed(string duplicate)
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", "{" + duplicate + RestrictedResponse()[1..]);
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-alpha", "{}");
        var vault = new RecordingSecretVault();
        var act = () => ProvisionExplicitAsync(CreateService(handler, vault), authorization);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("channel_authorization_contract_invalid");
        handler.Requests.Should().HaveCount(2);
        vault.PutRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionExplicitAsync_NyxIdTypedStalePlanConflict_ReturnsStableScopePlanChanged()
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync();
        var handler = new RecordingHandler { PostStatus = HttpStatusCode.Conflict };
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys",
            "{\"error\":\"api_key_scope_plan_stale\",\"error_code\":9007,\"message\":\"nyxid_ag_channel_secret_alpha\"}");
        var act = () => ProvisionExplicitAsync(CreateService(handler, new RecordingSecretVault()), authorization);
        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Be("scope_plan_changed");
        error.Which.ToString().Should().NotContain(RawFullKey);
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(" key-alpha ")]
    [InlineData("key-alpha\n")]
    public async Task ProvisionExplicitAsync_NoncanonicalCreatedId_NeverDeletesAnotherAddress(string id)
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys",
            RestrictedResponse().Replace("\"key-alpha\"", JsonSerializer.Serialize(id), StringComparison.Ordinal));
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-alpha", "{}");
        var act = () => ProvisionExplicitAsync(CreateService(handler, new RecordingSecretVault()), authorization);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("channel_authorization_contract_invalid");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ProvisionExplicitAsync_AmbiguousCreatedId_NeverChoosesCleanupTarget()
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", "{\"id\":\"key-other\"," + RestrictedResponse()[1..]);
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-alpha", "{}");
        var act = () => ProvisionExplicitAsync(CreateService(handler, new RecordingSecretVault()), authorization);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("channel_authorization_contract_invalid");
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("vault-failure")]
    [InlineData("vault-timeout")]
    [InlineData("caller-cancelled")]
    public async Task ProvisionExplicitAsync_VaultFailure_CompensationIgnoresCallerCancellation(string failure)
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync();
        using var caller = new CancellationTokenSource();
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", RestrictedResponse());
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-alpha", "{}");
        var vault = new RecordingSecretVault
        {
            OnPut = failure == "caller-cancelled" ? caller.Cancel : null,
            PutException = failure == "vault-failure"
                ? new InvalidOperationException(RawFullKey)
                : new OperationCanceledException(RawFullKey),
        };
        var act = () => ProvisionExplicitAsync(CreateService(handler, vault), authorization, caller.Token);
        if (failure == "caller-cancelled")
            await act.Should().ThrowAsync<OperationCanceledException>();
        else
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("secret_vault_unavailable");
        handler.Requests.Select(r => r.Method).Should().Equal(HttpMethod.Post, HttpMethod.Delete);
        handler.DeleteTokens.Should().ContainSingle().Which.CanBeCanceled.Should().BeTrue();
        handler.DeleteTokens.Single().Should().NotBe(caller.Token);
        vault.RevokeRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvisionExplicitAsync_CreateCancellation_DistinguishesCallerFromTimeout(bool callerCancelled)
    {
        var authorization = await ChannelRegistrationExplicitAuthorizationPreparationTests.PrepareVerifiedAsync();
        using var caller = new CancellationTokenSource();
        var handler = new RecordingHandler
        {
            OnPost = callerCancelled ? caller.Cancel : null,
            PostException = new OperationCanceledException(RawFullKey),
        };
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", RestrictedResponse());
        var vault = new RecordingSecretVault();
        var act = () => ProvisionExplicitAsync(CreateService(handler, vault), authorization, caller.Token);
        if (callerCancelled)
            (await act.Should().ThrowAsync<OperationCanceledException>()).Which.ToString().Should().NotContain(RawFullKey);
        else
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("provisioning_failed");
        handler.Requests.Should().ContainSingle();
        vault.PutRequests.Should().BeEmpty();
    }

    private static Task<ChannelAgentKeyCredential> ProvisionExplicitAsync(
        ChannelAgentKeyProvisioningService service, VerifiedChannelRegistrationExplicitAuthorization authorization,
        CancellationToken ct = default)
        => service.ProvisionAsync("telegram", "owner-token", "https://aevatar.example.com/api/webhooks/nyxid-relay",
            authorization.Plan.KeyOwner.Id, "reg-alpha", authorization, ct);

    [Fact]
    public async Task ProvisionAsync_DefaultWriteGate_RejectsBeforeExternalCalls()
    {
        var handler = new RecordingHandler();
        var vault = new RecordingSecretVault();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" };
        var service = new ChannelAgentKeyProvisioningService(
            new NyxIdApiClient(options, new HttpClient(handler)),
            vault,
            NullLogger<ChannelAgentKeyProvisioningService>.Instance);

        var act = () => service.ProvisionAsync(
            "lark",
            "owner-token",
            "https://aevatar.example.com/api/webhooks/nyxid-relay",
            "scope-alpha",
            "reg-alpha",
            PersonalOwner("scope-alpha"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Be("channel_agent_key_write_gate_closed");
        handler.Requests.Should().BeEmpty();
        vault.PutRequests.Should().BeEmpty();
        vault.RevokeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_ValidDefaultGrant_StoresSecretAndReturnsTypedCredential()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", ValidCreateResponse());
        var vault = new InMemorySecretVault();
        var service = CreateService(handler, vault);

        var credential = await service.ProvisionAsync(
            "lark",
            "owner-token",
            "https://aevatar.example.com/api/webhooks/nyxid-relay",
            "scope-alpha",
            "reg-alpha",
            PersonalOwner("scope-alpha"),
            CancellationToken.None);

        credential.ApiKeyId.Should().Be("key-alpha");
        credential.SecretReference.Purpose.Should().Be(CredentialSecretPurposes.ChannelNyxIdAgentKey);
        credential.SecretReference.OwnerScopeKey.Should().Be("scope-alpha");
        credential.SecretReference.Version.Should().BePositive();
        credential.SecretReference.Fingerprint.Should().NotBeNullOrWhiteSpace();
        credential.SecretReference.CreatedAtUnixMs.Should().BePositive();
        credential.Grant.HasAllowAllServices.Should().BeTrue();
        credential.Grant.AllowAllServices.Should().BeTrue();
        credential.Grant.HasAllowAllNodes.Should().BeTrue();
        credential.Grant.AllowAllNodes.Should().BeTrue();
        credential.Grant.AllowedServiceIds.Should().BeEmpty();
        credential.Grant.AllowedNodeIds.Should().BeEmpty();

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            credential.SecretReference.Ref,
            credential.SecretReference.Purpose,
            credential.SecretReference.OwnerScopeKey,
            credential.ApiKeyId,
            "test-read"));
        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be(RawFullKey);

        handler.Requests.Should().ContainSingle();
        handler.Requests.Single().Body.Should().Be(
            """{"name":"aevatar-lark-relay-reg-alpha","scopes":"read write proxy","platform":"generic","callback_url":"https://aevatar.example.com/api/webhooks/nyxid-relay"}""");
        using var request = JsonDocument.Parse(handler.Requests.Single().Body);
        var root = request.RootElement;
        root.GetProperty("scopes").GetString().Should().Be("read write proxy");
        root.GetProperty("platform").GetString().Should().Be("generic");
        root.GetProperty("callback_url").GetString()
            .Should().Be("https://aevatar.example.com/api/webhooks/nyxid-relay");
        root.TryGetProperty("allowed_service_ids", out _).Should().BeFalse();
        root.TryGetProperty("allowed_node_ids", out _).Should().BeFalse();
        root.TryGetProperty("allow_all_services", out _).Should().BeFalse();
        root.TryGetProperty("allow_all_nodes", out _).Should().BeFalse();
        root.TryGetProperty("scope_plan_digest", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionAsync_DefaultOrganizationOwner_SendsTargetOrganizationId()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", ValidCreateResponse());
        var service = CreateService(handler, new InMemorySecretVault());
        var owner = new VerifiedChannelRegistrationOwner(
            "user-alpha",
            new ChannelRegistrationKeyOwner(
                ChannelRegistrationKeyOwnerKind.Organization,
                "org-alpha"),
            "org-alpha");

        await service.ProvisionAsync(
            "lark",
            "owner-token",
            "https://aevatar.example.com/api/webhooks/nyxid-relay",
            "org-alpha",
            "reg-alpha",
            owner,
            CancellationToken.None);

        using var request = JsonDocument.Parse(handler.Requests.Single().Body);
        request.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-alpha");
    }

    [Theory]
    [MemberData(nameof(InvalidCreateResponses))]
    public async Task ProvisionAsync_InvalidProviderContract_DeletesCreatedKeyAndFailsClosed(
        string response)
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", response);
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-alpha", """{"ok":true}""");
        var vault = new RecordingSecretVault();
        var service = CreateService(handler, vault);

        var act = () => service.ProvisionAsync(
            "telegram",
            "owner-token",
            "https://aevatar.example.com/api/webhooks/nyxid-relay",
            "scope-alpha",
            "reg-alpha",
            PersonalOwner("scope-alpha"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Be("channel_authorization_contract_invalid");
        error.Which.Message.Should().NotContain(RawFullKey);
        handler.Requests.Select(static request => (request.Method, request.Path)).Should().Equal(
            (HttpMethod.Post, "/api/v1/api-keys"),
            (HttpMethod.Delete, "/api/v1/api-keys/key-alpha"));
        vault.PutRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_ErrorEnvelopeWithId_DoesNotDeleteUnownedKey()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(
            HttpMethod.Post,
            "/api/v1/api-keys",
            """{"error":true,"status":409,"id":"key-existing"}""");
        handler.Enqueue(
            HttpMethod.Delete,
            "/api/v1/api-keys/key-existing",
            """{"ok":true}""");
        var vault = new RecordingSecretVault();
        var service = CreateService(handler, vault);

        var act = () => service.ProvisionAsync(
            "lark",
            "owner-token",
            "https://aevatar.example.com/api/webhooks/nyxid-relay",
            "scope-alpha",
            "reg-alpha",
            PersonalOwner("scope-alpha"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Be("channel_authorization_contract_invalid");
        handler.Requests.Should().ContainSingle();
        handler.Requests.Single().Method.Should().Be(HttpMethod.Post);
        vault.PutRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionAsync_VaultFailure_DeletesCreatedKeyAndReportsVaultUnavailable()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", ValidCreateResponse());
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-alpha", """{"ok":true}""");
        var vault = new RecordingSecretVault
        {
            PutException = new InvalidOperationException("vault backend unavailable"),
        };
        var service = CreateService(handler, vault);

        var act = () => service.ProvisionAsync(
            "lark",
            "owner-token",
            "https://aevatar.example.com/api/webhooks/nyxid-relay",
            "scope-alpha",
            "reg-alpha",
            PersonalOwner("scope-alpha"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Be("secret_vault_unavailable");
        error.Which.Message.Should().NotContain("vault backend unavailable");
        handler.Requests.Select(static request => (request.Method, request.Path)).Should().Equal(
            (HttpMethod.Post, "/api/v1/api-keys"),
            (HttpMethod.Delete, "/api/v1/api-keys/key-alpha"));
        vault.PutRequests.Should().ContainSingle();
        vault.RevokeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupAsync_DeletesKeyBeforeRevokingVaultReference()
    {
        var effects = new List<string>();
        var handler = new RecordingHandler(effects);
        handler.Enqueue(HttpMethod.Delete, "/api/v1/api-keys/key-alpha", """{"ok":true}""");
        var vault = new RecordingSecretVault(effects);
        var service = CreateService(handler, vault);
        var credential = new ChannelAgentKeyCredential
        {
            ApiKeyId = "key-alpha",
            SecretReference = CompleteReference(),
            Grant = new ChannelAgentKeyGrantSnapshot
            {
                AllowAllServices = true,
                AllowAllNodes = true,
            },
        };

        await service.CleanupAsync(
            "owner-token",
            credential,
            "reg-alpha",
            CancellationToken.None);

        effects.Should().Equal("nyx:key-delete", "vault:revoke");
        vault.RevokeRequests.Should().ContainSingle();
        vault.RevokeRequests[0].SubjectId.Should().Be("key-alpha");
    }

    [Fact]
    public void AddNyxIdRelayChannel_RegistersSharedProvisioningServiceAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddNyxIdRelayChannel();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(ChannelAgentKeyProvisioningService) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task AddNyxIdRelayChannel_ConfiguredWriteMode_EnablesSharedProvisioningService()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(HttpMethod.Post, "/api/v1/api-keys", ValidCreateResponse());
        var vault = new InMemorySecretVault();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" };
        var services = new ServiceCollection();
        services.AddSingleton(new NyxIdApiClient(options, new HttpClient(handler)));
        services.AddSingleton<ISecretVault>(vault);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ChannelAgentKeyProvisioningService>>(
            NullLogger<ChannelAgentKeyProvisioningService>.Instance);
        services.AddSingleton(new NyxIdRelayOptions
        {
            ChannelAgentKeyWriteMode = ChannelAgentKeyWriteMode.NyxIdDefault,
        });
        services.AddNyxIdRelayChannel();
        using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<ChannelAgentKeyProvisioningService>();
        var credential = await service.ProvisionAsync(
            "lark",
            "owner-token",
            "https://aevatar.example.com/api/webhooks/nyxid-relay",
            "scope-alpha",
            "reg-alpha",
            PersonalOwner("scope-alpha"),
            CancellationToken.None);

        credential.ApiKeyId.Should().Be("key-alpha");
        handler.Requests.Should().ContainSingle();
    }

    public static TheoryData<string> InvalidCreateResponses() => new()
    {
        CreateResponse(scopes: "read write"),
        CreateResponse(purpose: "scheduled_invocation", scheduledWriteEnabled: true),
        CreateResponse(allowAllServicesJson: null),
        CreateResponse(allowAllNodesJson: "\"true\""),
        CreateResponse(allowedServiceIdsJson: "[\"svc-b\",\"svc-a\"]", allowAllServicesJson: "false"),
        CreateResponse(allowedNodeIdsJson: "[\"node-a\",\"node-a\"]", allowAllNodesJson: "false"),
        CreateResponse(allowedServiceIdsJson: "[\"svc-a\"]", allowAllServicesJson: "true"),
        CreateResponse(fullKey: null),
    };

    private static ChannelAgentKeyProvisioningService CreateService(
        RecordingHandler handler,
        ISecretVault vault)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" };
        return new ChannelAgentKeyProvisioningService(
            new NyxIdApiClient(options, new HttpClient(handler)),
            vault,
            NullLogger<ChannelAgentKeyProvisioningService>.Instance,
            ChannelAgentKeyWriteMode.NyxIdDefault);
    }

    private static VerifiedChannelRegistrationOwner PersonalOwner(string scopeId) =>
        new(
            scopeId,
            new ChannelRegistrationKeyOwner(
                ChannelRegistrationKeyOwnerKind.Personal,
                scopeId),
            null);

    private static string ValidCreateResponse() => CreateResponse();

    private static string CreateResponse(
        string? fullKey = RawFullKey,
        string purpose = "general",
        bool scheduledWriteEnabled = false,
        string scopes = "read write proxy",
        string? allowAllServicesJson = "true",
        string? allowAllNodesJson = "true",
        string allowedServiceIdsJson = "[]",
        string allowedNodeIdsJson = "[]")
    {
        var properties = new List<string>
        {
            "\"id\":\"key-alpha\"",
            $"\"purpose\":{JsonSerializer.Serialize(purpose)}",
            $"\"scheduled_write_enabled\":{scheduledWriteEnabled.ToString().ToLowerInvariant()}",
            $"\"scopes\":{JsonSerializer.Serialize(scopes)}",
            $"\"allowed_service_ids\":{allowedServiceIdsJson}",
            $"\"allowed_node_ids\":{allowedNodeIdsJson}",
        };
        if (fullKey is not null)
            properties.Add($"\"full_key\":{JsonSerializer.Serialize(fullKey)}");
        if (allowAllServicesJson is not null)
            properties.Add($"\"allow_all_services\":{allowAllServicesJson}");
        if (allowAllNodesJson is not null)
            properties.Add($"\"allow_all_nodes\":{allowAllNodesJson}");

        return "{" + string.Join(',', properties) + "}";
    }

    private static SecretReference CompleteReference() => new()
    {
        Ref = "sec-alpha",
        Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
        OwnerScopeKey = "scope-alpha",
        Version = 1,
        Fingerprint = "sha256:test",
        CreatedAtUnixMs = 1788912000000,
    };

    private sealed class RecordingHandler(List<string>? effects = null) : HttpMessageHandler
    {
        private readonly Queue<(HttpMethod Method, string Path, string Body)> _responses = new();

        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];
        public HttpStatusCode PostStatus { get; init; } = HttpStatusCode.OK;
        public Action? OnPost { get; init; }
        public Exception? PostException { get; init; }
        public List<CancellationToken> DeleteTokens { get; } = [];

        public void Enqueue(HttpMethod method, string path, string body) =>
            _responses.Enqueue((method, path, body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more queued responses.");

            var expected = _responses.Dequeue();
            request.Method.Should().Be(expected.Method);
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.AbsolutePath.Should().Be(expected.Path);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, expected.Path, body));
            if (request.Method == HttpMethod.Delete)
            {
                DeleteTokens.Add(cancellationToken);
                effects?.Add("nyx:key-delete");
            }
            if (request.Method == HttpMethod.Post)
            {
                OnPost?.Invoke();
                if (PostException is not null)
                    throw PostException;
            }

            return new HttpResponseMessage(request.Method == HttpMethod.Post ? PostStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(expected.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RecordingSecretVault(List<string>? effects = null) : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public Exception? PutException { get; init; }
        public Action? OnPut { get; init; }
        public List<StoreSecretRequest> PutRequests { get; } = [];
        public List<RevokeSecretRequest> RevokeRequests { get; } = [];

        public async Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default)
        {
            PutRequests.Add(request);
            OnPut?.Invoke();
            if (PutException is not null)
                throw PutException;
            return await _inner.PutAsync(request, ct);
        }

        public Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default) =>
            _inner.ResolveAsync(request, ct);

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) =>
            _inner.RotateAsync(request, ct);

        public Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default)
        {
            effects?.Add("vault:revoke");
            RevokeRequests.Add(request);
            return Task.FromResult(new RevokeSecretResult(true));
        }
    }
}
