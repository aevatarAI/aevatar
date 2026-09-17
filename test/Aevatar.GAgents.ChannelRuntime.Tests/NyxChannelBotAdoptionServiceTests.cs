using System.Net;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxChannelBotAdoptionServiceTests
{
    [Theory]
    [InlineData("lark")]
    [InlineData("telegram")]
    [InlineData("matrix")]
    public async Task RegisterAsync_AdoptsAnyAuthorizedPlatformWithOnlyOwnedResources(string platform)
    {
        var fixture = new Fixture(platform);
        var result = await fixture.RegisterAsync(fixture.Request);
        result.Succeeded.Should().BeTrue(result.Error);
        result.Status.Should().Be("accepted");
        result.NyxChannelBotId.Should().Be("bot-owned");
        fixture.Command!.Platform.Should().Be(platform);
        fixture.Command.NyxProviderSlug.Should().Be("opaque-requested-slug");
        fixture.Command.NyxChannelBotId.Should().Be("bot-owned");
        fixture.Command.NyxConversationRouteId.Should().Be("route-owned");
        fixture.Command.ChannelAgentKey.ApiKeyId.Should().Be("key-owned");
        fixture.Handler.Requests.Select(x => x.Method + " " + x.Path.Split('?')[0]).Should().Equal(
            "GET /api/v1/channel-bots/bot-owned", "GET /api/v1/channel-conversations",
            "POST /api/v1/api-keys", "POST /api/v1/channel-conversations");
        using var route = JsonDocument.Parse(fixture.Handler.Requests.Last().Body);
        route.RootElement.GetProperty("channel_bot_id").GetString().Should().Be("bot-owned");
        route.RootElement.GetProperty("agent_api_key_id").GetString().Should().Be("key-owned");
        route.RootElement.GetProperty("default_agent").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("lark")]
    [InlineData("telegram")]
    [InlineData("matrix")]
    public async Task RegisterAsync_ExplicitGrantPreservesExactSelectionWithoutPlatformService(string platform)
    {
        var fixture = new Fixture(platform, explicitGrant: true);
        var result = await fixture.RegisterAsync(fixture.Request);
        result.Succeeded.Should().BeTrue(result.Error);
        fixture.Command!.RegistrationServiceAllowlist.ServiceIds.Should().Equal("svc-selected");
        fixture.Command.ChannelAgentKey.Grant.AllowedServiceIds.Should().Equal("svc-selected");
        fixture.Command.RuntimeConfig.NyxidServiceSelectors.Should().ContainSingle().Which.ServiceSlug.Should().Be("selected-service");
        using var key = JsonDocument.Parse(fixture.Handler.Requests.Single(x => x.Path == "/api/v1/api-keys").Body);
        key.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(x => x.GetString()).Should().Equal("svc-selected");
        key.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
        key.RootElement.GetProperty("scope_plan_digest").GetString().Should().Be(ChannelExplicitAuthorizationTestSupport.Digest);
        fixture.Handler.Requests.Should().NotContain(x => x.Path.StartsWith("/api/v1/keys", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" bot-owned")]
    [InlineData("bot-owned ")]
    [InlineData("bot\nowned")]
    public async Task RegisterAsync_InvalidOpaqueBotIdFailsBeforeAnyRequest(string id)
    {
        var fixture = new Fixture();
        var result = await fixture.RegisterAsync(fixture.Request with { NyxChannelBotId = id });
        result.Error.Should().Be("missing_nyx_channel_bot_id");
        fixture.Handler.Requests.Should().BeEmpty();
        fixture.Command.Should().BeNull();
    }

    [Theory]
    [InlineData("id", "bot-foreign", "invalid_channel_bot_detail")]
    [InlineData("platform", "bad platform", "invalid_channel_bot_detail")]
    [InlineData("user_id", "other-owner", "channel_bot_not_found_or_forbidden")]
    [InlineData("user_id", " owner-1", "invalid_channel_bot_detail")]
    [InlineData("status", "pending", "channel_bot_not_adoptable")]
    [InlineData("status", "failed", "channel_bot_not_adoptable")]
    [InlineData("status", "invalid", "channel_bot_not_adoptable")]
    [InlineData("status", "future-status", "channel_bot_not_adoptable")]
    public async Task RegisterAsync_InvalidDetailFailsBeforeOwnedResourceCreation(string field, string value, string error)
    {
        var fixture = new Fixture();
        fixture.Handler.Detail[field] = value;
        var result = await fixture.RegisterAsync(fixture.Request);
        result.Error.Should().Be(error);
        fixture.Handler.Requests.Should().ContainSingle().Which.Method.Should().Be("GET");
        fixture.Command.Should().BeNull();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("platform")]
    [InlineData("user_id")]
    [InlineData("status")]
    [InlineData("is_active")]
    public async Task RegisterAsync_MissingDetailFieldFailsClosed(string field)
    {
        var fixture = new Fixture();
        fixture.Handler.Detail.Remove(field);
        var result = await fixture.RegisterAsync(fixture.Request);
        result.Error.Should().Be("invalid_channel_bot_detail");
        fixture.Handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("active")]
    [InlineData("pending_webhook")]
    public async Task RegisterAsync_ActiveManualWebhookBotDoesNotRequireWebhookRegistered(string status)
    {
        var fixture = new Fixture();
        fixture.Handler.Detail["status"] = status;
        fixture.Handler.Detail["webhook_registered"] = false;
        (await fixture.RegisterAsync(fixture.Request)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsync_InactiveBotFailsClosed()
    {
        var fixture = new Fixture();
        fixture.Handler.Detail["is_active"] = false;
        (await fixture.RegisterAsync(fixture.Request)).Error.Should().Be("channel_bot_not_adoptable");
        fixture.Handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RegisterAsync_NotVisibleBotCannotBeAdopted(HttpStatusCode status)
    {
        var fixture = new Fixture();
        fixture.Handler.DetailStatus = status;
        (await fixture.RegisterAsync(fixture.Request)).Error.Should().Be("channel_bot_not_found_or_forbidden");
        fixture.Handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"id\":\"bot-owned\",\"id\":\"bot-owned\"}")]
    public async Task RegisterAsync_MalformedDetailHasObservableTypedError(string body)
    {
        var fixture = new Fixture();
        fixture.Handler.DetailBody = body;
        (await fixture.RegisterAsync(fixture.Request)).Error.Should().Be("invalid_channel_bot_detail");
        fixture.Handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RegisterAsync_NormalizesVerifiedPlatformAndDefaultsEmptyProviderSlug()
    {
        var fixture = new Fixture("matrix");
        fixture.Handler.Detail["platform"] = " MATRIX ";
        var result = await fixture.RegisterAsync(fixture.Request with { Platform = "matrix", NyxProviderSlug = "" });
        result.Succeeded.Should().BeTrue(result.Error);
        fixture.Command!.Platform.Should().Be("matrix");
        fixture.Command.NyxProviderSlug.Should().Be("api-matrix-bot");
        result.NyxProviderSlug.Should().Be("api-matrix-bot");
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData(" Matrix ")]
    [InlineData("MATRIX")]
    [InlineData("matrix room")]
    [InlineData("")]
    public async Task RegisterAsync_UsesVerifiedPlatformInsteadOfLegacyCallerPlatform(string legacyPlatform)
    {
        var fixture = new Fixture("matrix");

        var result = await fixture.RegisterAsync(fixture.Request with { Platform = legacyPlatform });

        result.Succeeded.Should().BeTrue(result.Error);
        result.Platform.Should().Be("matrix");
        fixture.Command!.Platform.Should().Be("matrix");
        fixture.Handler.Requests.First().Path.Should().Be("/api/v1/channel-bots/bot-owned");
    }

    [Theory]
    [InlineData("reg-requested")]
    [InlineData(" reg-requested ")]
    public async Task RegisterAsync_PreservesRequestedRegistrationIdAndAcceptedReceipt(string requestedRegistrationId)
    {
        var fixture = new Fixture();

        var result = await fixture.RegisterAsync(fixture.Request with
        {
            RequestedRegistrationId = requestedRegistrationId,
        });

        result.Succeeded.Should().BeTrue(result.Error);
        result.RegistrationId.Should().Be("reg-requested");
        fixture.Command!.RequestedId.Should().Be("reg-requested");
        result.Receipt.Should().NotBeNull();
        result.Receipt!.ActorId.Should().Be(ChannelBotRegistrationGAgent.WellKnownId);
        result.Receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        result.Receipt.CommandId.Should().Be(fixture.DispatchedEnvelope!.Id);
        result.Receipt.CommandId.Should().NotBe(result.RegistrationId);
        result.Receipt.CorrelationId.Should().Be(result.Receipt.CommandId);
        result.NyxProviderSlug.Should().Be("opaque-requested-slug");
    }

    [Theory]
    [InlineData("route-foreign")]
    [InlineData(" route-foreign ")]
    public async Task RegisterAsync_RejectsSpecifiedRouteBeforeOwnedResourceWrites(string conversationRouteId)
    {
        var fixture = new Fixture();

        var result = await fixture.RegisterAsync(fixture.Request, conversationRouteId);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("channel_route_not_accessible");
        var detailRequest = fixture.Handler.Requests.Should().ContainSingle().Which;
        detailRequest.Method.Should().Be("GET");
        detailRequest.Path.Should().Be("/api/v1/channel-bots/bot-owned");
        fixture.Command.Should().BeNull();
        fixture.Handler.RouteActive.Should().BeFalse();
        fixture.Handler.KeyActive.Should().BeFalse();
        fixture.Vault.Revocations.Should().Be(0);
    }

    [Fact]
    public async Task RegisterAsync_ForeignDefaultRouteIsRejectedBeforeCreatingKey()
    {
        var fixture = new Fixture();
        fixture.Handler.Routes = """{"conversations":[{"id":"route-foreign","channel_bot_id":"bot-owned","agent_api_key_id":"key-foreign","default_agent":true,"is_active":true}]}""";
        (await fixture.RegisterAsync(fixture.Request)).Error.Should().Be("channel_route_not_accessible");
        fixture.Handler.Requests.Should().OnlyContain(x => x.Method == "GET");
        fixture.Command.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_OrganizationCarriesVerifiedOwnerToKeyAndRoutes()
    {
        var fixture = new Fixture(organization: true);
        var result = await fixture.RegisterAsync(fixture.Request);
        result.Succeeded.Should().BeTrue(result.Error);
        fixture.Handler.Requests.Single(x => x.Path.StartsWith("/api/v1/channel-conversations?", StringComparison.Ordinal))
            .Path.Should().Contain("org_id=org-1");
        foreach (var write in fixture.Handler.Requests.Where(x => x.Method == "POST"))
        {
            using var body = JsonDocument.Parse(write.Body);
            body.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-1");
        }
    }

    [Fact]
    public async Task RegisterAsync_ConfirmedDispatchFailureCompensatesOnlyOwnedRouteKeyAndSecret()
    {
        var fixture = new Fixture(actorUnavailable: true);
        var result = await fixture.RegisterAsync(fixture.Request);
        result.Error.Should().Be("local_mirror_dispatch_failed");
        fixture.Handler.Requests.Where(x => x.Method == "DELETE").Select(x => x.Path).Should().Equal(
            "/api/v1/channel-conversations/route-owned", "/api/v1/api-keys/key-owned");
        fixture.Vault.Revocations.Should().Be(1);
        fixture.Handler.RouteActive.Should().BeFalse();
        fixture.Handler.KeyActive.Should().BeFalse();
        result.Cleanup!.CleanupComplete.Should().BeTrue();
        result.Cleanup.ConversationRouteRemoved.Should().BeTrue();
        result.Cleanup.AgentKeyRemoved.Should().BeTrue();
        result.Cleanup.VaultSecretRevoked.Should().BeTrue();
        result.CleanupRequest.Should().BeNull();

        fixture.ActorUnavailable = false;
        (await fixture.RegisterAsync(fixture.Request)).Succeeded.Should().BeTrue();
        fixture.Command!.NyxChannelBotId.Should().Be("bot-owned");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterAsync_ConfirmedFailureWithRouteCleanupFailureRetainsOwnershipForCleanupAndReadoption(bool transportFailure)
    {
        var fixture = new Fixture(actorUnavailable: true);
        fixture.Handler.RouteDeleteStatus = HttpStatusCode.ServiceUnavailable;
        fixture.Handler.RouteDeleteThrows = transportFailure;

        var result = await fixture.RegisterAsync(fixture.Request);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("local_mirror_dispatch_failed");
        result.RegistrationId.Should().NotBeNullOrWhiteSpace();
        result.NyxChannelBotId.Should().Be("bot-owned");
        result.NyxConversationRouteId.Should().Be("route-owned");
        result.NyxAgentApiKeyId.Should().Be("key-owned");
        result.Note.Should().Contain("cleanup").And.NotContain("acceptance is unknown");
        result.Cleanup!.CleanupComplete.Should().BeFalse();
        result.Cleanup.ConversationRouteRemoved.Should().BeFalse();
        result.Cleanup.AgentKeyRemoved.Should().BeTrue();
        result.Cleanup.VaultSecretRevoked.Should().BeTrue();
        result.Cleanup.Warnings.Should().ContainSingle().Which.Should().Be("conversation_route_delete_failed id=route-owned");
        result.CleanupRequest.Should().NotBeNull();
        result.CleanupRequest!.RegistrationId.Should().Be(result.RegistrationId);
        result.CleanupRequest.ConversationRouteId.Should().Be(result.NyxConversationRouteId);
        result.CleanupRequest.AgentKeyId.Should().Be(result.NyxAgentApiKeyId);
        result.CleanupRequest.SecretReference.Should().NotBeNull();
        fixture.Command.Should().BeNull();
        fixture.Handler.RouteActive.Should().BeTrue();
        (await fixture.RegisterAsync(fixture.Request)).Error.Should().Be("channel_route_not_accessible");

        fixture.Handler.RouteDeleteStatus = HttpStatusCode.NoContent;
        fixture.Handler.RouteDeleteThrows = false;
        var cleanup = await fixture.Deprovisioning.DeprovisionAsync("caller-token",
            result.CleanupRequest, CancellationToken.None);
        cleanup.CleanupComplete.Should().BeTrue();
        fixture.Handler.RouteActive.Should().BeFalse();
        fixture.ActorUnavailable = false;
        var readopted = await fixture.RegisterAsync(fixture.Request);
        readopted.Succeeded.Should().BeTrue(readopted.Error);
        readopted.NyxChannelBotId.Should().Be("bot-owned");
        fixture.Handler.Requests.Where(x => x.Method == "DELETE").Should().OnlyContain(x =>
            x.Path == "/api/v1/channel-conversations/route-owned" || x.Path == "/api/v1/api-keys/key-owned");
    }

    [Theory]
    [InlineData("key-http")]
    [InlineData("key-transport")]
    [InlineData("vault-rejected")]
    [InlineData("vault-transport")]
    public async Task RegisterAsync_ConfirmedFailureClassifiesKeyAndVaultCleanupAndRetainsRetryRequest(string failure)
    {
        var fixture = new Fixture(actorUnavailable: true);
        var keyFailure = failure.StartsWith("key-", StringComparison.Ordinal);
        fixture.Handler.KeyDeleteStatus = keyFailure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent;
        fixture.Handler.KeyDeleteThrows = failure == "key-transport";
        fixture.Vault.RevokeRejected = failure == "vault-rejected";
        fixture.Vault.RevokeThrows = failure == "vault-transport";

        var result = await fixture.RegisterAsync(fixture.Request);

        result.Error.Should().Be("local_mirror_dispatch_failed");
        result.RegistrationId.Should().NotBeNullOrWhiteSpace();
        result.NyxChannelBotId.Should().Be("bot-owned");
        result.NyxConversationRouteId.Should().Be("route-owned");
        result.NyxAgentApiKeyId.Should().Be("key-owned");
        result.Cleanup!.CleanupComplete.Should().BeFalse();
        result.Cleanup.ConversationRouteRemoved.Should().BeTrue();
        result.Cleanup.AgentKeyRemoved.Should().Be(!keyFailure);
        result.Cleanup.VaultSecretRevoked.Should().BeFalse();
        fixture.Handler.RouteActive.Should().BeFalse();
        fixture.Handler.KeyActive.Should().Be(keyFailure);
        fixture.Vault.Revocations.Should().Be(keyFailure ? 0 : 1);
        if (!keyFailure)
            result.Cleanup.Warnings.Should().Contain("vault_revoke_failed");
        result.CleanupRequest.Should().NotBeNull();
        result.CleanupRequest!.SecretReference.Should().NotBeNull();

        fixture.Handler.KeyDeleteStatus = HttpStatusCode.NoContent;
        fixture.Handler.KeyDeleteThrows = false;
        fixture.Vault.RevokeRejected = false;
        fixture.Vault.RevokeThrows = false;
        var cleanup = await fixture.Deprovisioning.DeprovisionAsync("caller-token", result.CleanupRequest, CancellationToken.None);
        cleanup.CleanupComplete.Should().BeTrue();
        fixture.ActorUnavailable = false;
        (await fixture.RegisterAsync(fixture.Request)).Succeeded.Should().BeTrue();
        fixture.Command!.NyxChannelBotId.Should().Be("bot-owned");
        fixture.Handler.Requests.Where(x => x.Method == "DELETE").Should().OnlyContain(x =>
            x.Path == "/api/v1/channel-conversations/route-owned" || x.Path == "/api/v1/api-keys/key-owned");
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("missing-id")]
    [InlineData("invalid-json")]
    [InlineData("invalid-id")]
    [InlineData("server-error")]
    public async Task RegisterAsync_CommittedRouteWithUnusableResponseCleansOnlyProvenOwnershipAndAllowsReadoption(string responseFailure)
    {
        var fixture = new Fixture();
        fixture.Handler.RouteCreateResponseFailure = responseFailure;

        var result = await fixture.RegisterAsync(fixture.Request);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("provisioning_failed");
        fixture.Command.Should().BeNull();
        fixture.Handler.RouteActive.Should().BeFalse("a committed route must be removed before cleanup is complete");
        result.Cleanup!.CleanupComplete.Should().BeTrue();
        result.Cleanup.ConversationRouteRemoved.Should().BeTrue();
        result.CleanupRequest.Should().BeNull();
        fixture.Handler.KeyActive.Should().BeFalse();
        fixture.Vault.Revocations.Should().Be(1);
        fixture.Handler.Requests.Where(x => x.Method == "DELETE").Select(x => x.Path).Should().Equal(
            "/api/v1/channel-conversations/route-owned", "/api/v1/api-keys/key-owned");

        fixture.Handler.RouteCreateResponseFailure = null;
        var readopted = await fixture.RegisterAsync(fixture.Request);
        readopted.Succeeded.Should().BeTrue(readopted.Error);
        readopted.NyxChannelBotId.Should().Be("bot-owned");
    }

    [Theory]
    [InlineData("http")]
    [InlineData("transport")]
    [InlineData("invalid-list")]
    [InlineData("not-yet-visible")]
    [InlineData("other-key")]
    [InlineData("other-bot")]
    [InlineData("ambiguous")]
    public async Task RegisterAsync_UnprovenRouteOwnershipRetainsCredentialsUntilExistingCleanupRetryCanProveIt(string evidenceFailure)
    {
        var fixture = new Fixture(organization: true);
        fixture.Handler.RouteCreateResponseFailure = "transport";
        fixture.Handler.OwnershipEvidenceFailure = evidenceFailure;

        var result = await fixture.RegisterAsync(fixture.Request);

        result.Error.Should().Be("provisioning_failed");
        result.Cleanup!.CleanupComplete.Should().BeFalse("unavailable ownership evidence is not proof of absence");
        result.Cleanup.Succeeded.Should().BeFalse();
        result.Cleanup.ConversationRouteRemoved.Should().BeFalse();
        result.Cleanup.AgentKeyRemoved.Should().BeFalse();
        result.Cleanup.VaultSecretRevoked.Should().BeFalse();
        result.CleanupRequest.Should().NotBeNull();
        result.CleanupRequest!.AgentKeyId.Should().Be("key-owned");
        result.CleanupRequest.SecretReference.Should().NotBeNull();
        result.CleanupRequest.UncertainRouteAcquisition.Should().Be(new NyxChannelRouteAcquisitionUncertainty("bot-owned", "org-1"));
        result.Cleanup.Warnings.Should().ContainSingle().Which.Should().Be("conversation_route_acquisition_unresolved");
        result.NyxChannelBotId.Should().Be("bot-owned");
        result.NyxConversationRouteId.Should().BeNull();
        result.Note.Should().Contain("incomplete").And.NotContain("can be adopted again");
        fixture.Command.Should().BeNull();
        fixture.Handler.Requests.Should().NotContain(x => x.Method == "DELETE");
        fixture.Handler.KeyActive.Should().BeTrue();
        fixture.Vault.Revocations.Should().Be(0);

        fixture.Handler.OwnershipEvidenceFailure = null;
        (await fixture.RegisterAsync(fixture.Request)).Error.Should().Be("channel_route_not_accessible");
        var recovered = await fixture.Deprovisioning.DeprovisionAsync("caller-token", result.CleanupRequest, CancellationToken.None);
        recovered.CleanupComplete.Should().BeTrue();
        fixture.Handler.RouteActive.Should().BeFalse();
        fixture.Handler.KeyActive.Should().BeFalse();
        fixture.Handler.Requests.Where(x => x.Method == "GET" && x.Path.StartsWith("/api/v1/channel-conversations?", StringComparison.Ordinal))
            .Should().OnlyContain(x => x.Path.Contains("bot_id=bot-owned", StringComparison.Ordinal) && x.Path.Contains("org_id=org-1", StringComparison.Ordinal));
        fixture.Handler.Requests.Where(x => x.Method == "DELETE").Select(x => x.Path).Should().Equal(
            "/api/v1/channel-conversations/route-owned", "/api/v1/api-keys/key-owned");
        fixture.Handler.RouteCreateResponseFailure = null;
        (await fixture.RegisterAsync(fixture.Request)).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("route")]
    [InlineData("key")]
    [InlineData("vault")]
    public async Task RegisterAsync_AcquisitionRecoveryRetainsResolvedRouteForSubsequentCleanupRetry(string failedResource)
    {
        var fixture = new Fixture();
        fixture.Handler.RouteCreateResponseFailure = "transport";
        fixture.Handler.OwnershipEvidenceFailure = "http";
        var adoption = await fixture.RegisterAsync(fixture.Request);
        adoption.CleanupRequest.Should().NotBeNull();

        fixture.Handler.OwnershipEvidenceFailure = null;
        fixture.Handler.RouteDeleteStatus = failedResource == "route" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent;
        fixture.Handler.KeyDeleteStatus = failedResource == "key" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent;
        fixture.Vault.RevokeRejected = failedResource == "vault";
        var interrupted = await fixture.Deprovisioning.DeprovisionAsync("caller-token", adoption.CleanupRequest!, CancellationToken.None);

        interrupted.CleanupComplete.Should().BeFalse();
        interrupted.RetryRequest.Should().NotBeNull();
        interrupted.RetryRequest!.ConversationRouteId.Should().Be("route-owned");
        interrupted.RetryRequest.UncertainRouteAcquisition.Should().BeNull();
        var readsAfterRecovery = fixture.Handler.Requests.Count(x => x.Method == "GET");

        fixture.Handler.RouteDeleteStatus = HttpStatusCode.NoContent;
        fixture.Handler.KeyDeleteStatus = HttpStatusCode.NoContent;
        fixture.Vault.RevokeRejected = false;
        var completed = await fixture.Deprovisioning.DeprovisionAsync("caller-token", interrupted.RetryRequest, CancellationToken.None);
        completed.CleanupComplete.Should().BeTrue();
        completed.RetryRequest.Should().BeNull();
        fixture.Handler.Requests.Count(x => x.Method == "GET").Should().Be(readsAfterRecovery,
            "the recovered route handle must survive route/key/Vault cleanup failures without another ownership query");
        fixture.Handler.RouteActive.Should().BeFalse();
        fixture.Handler.KeyActive.Should().BeFalse();
        fixture.Handler.RouteCreateResponseFailure = null;
        (await fixture.RegisterAsync(fixture.Request)).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task RegisterAsync_ConfirmedRouteRejectionBeforeInsertionCleansKeyWithoutRouteRecovery(HttpStatusCode status)
    {
        var fixture = new Fixture();
        fixture.Handler.RouteCreateRejection = status;

        var result = await fixture.RegisterAsync(fixture.Request);

        result.Error.Should().Be("provisioning_failed");
        result.Cleanup!.CleanupComplete.Should().BeTrue();
        result.CleanupRequest.Should().BeNull();
        fixture.Command.Should().BeNull();
        fixture.Handler.RouteActive.Should().BeFalse();
        fixture.Handler.KeyActive.Should().BeFalse();
        fixture.Vault.Revocations.Should().Be(1);
        fixture.Handler.Requests.Where(x => x.Method == "DELETE").Select(x => x.Path).Should().Equal("/api/v1/api-keys/key-owned");
        fixture.Handler.Requests.Count(x => x.Method == "GET" && x.Path.StartsWith("/api/v1/channel-conversations?", StringComparison.Ordinal)).Should().Be(1);
        fixture.Handler.RouteCreateRejection = null;
        (await fixture.RegisterAsync(fixture.Request)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsync_UnknownDispatchAcceptancePreservesOwnedResourcesForObservation()
    {
        var fixture = new Fixture(dispatchUnknown: true);
        var result = await fixture.RegisterAsync(fixture.Request);
        result.Error.Should().Be("local_mirror_acceptance_unknown_remote_cleanup_skipped");
        fixture.Handler.Requests.Should().NotContain(x => x.Method == "DELETE");
        fixture.Vault.Revocations.Should().Be(0);
        result.Status.Should().Be("error");
        result.RegistrationId.Should().NotBeNullOrWhiteSpace();
        result.NyxAgentApiKeyId.Should().Be("key-owned");
        result.NyxConversationRouteId.Should().Be("route-owned");
        result.Cleanup.Should().BeNull();
        result.CleanupRequest.Should().BeNull();
        result.Receipt.Should().BeNull();
    }

    private sealed class Fixture
    {
        private readonly ChannelRegistrationAdoptionFacade _facade;
        public AdoptionHandler Handler { get; }
        public RecordingVault Vault { get; } = new();
        public ChannelRelayRegistrationRequest Request { get; }
        public ChannelBotRegisterCommand? Command { get; private set; }
        public EventEnvelope? DispatchedEnvelope { get; private set; }
        public bool ActorUnavailable { get; set; }
        public INyxChannelBotDeprovisioningService Deprovisioning { get; }

        public Task<NyxChannelBotAdoptionResult> RegisterAsync(
            ChannelRelayRegistrationRequest request,
            string? conversationRouteId = null) =>
            _facade.AdoptAsync(
                new ChannelRegistrationAdoptionRequest(
                    request.RequestedRegistrationId,
                    request.NyxChannelBotId,
                    conversationRouteId,
                    request.NyxProviderSlug,
                    request.RuntimeConfig),
                request.ServiceSelection,
                request.AccessToken,
                request.ScopeId,
                request.WebhookBaseUrl,
                CancellationToken.None);

        public Fixture(string platform = "lark", bool organization = false, bool actorUnavailable = false, bool dispatchUnknown = false, bool explicitGrant = false)
        {
            ActorUnavailable = actorUnavailable;
            var ownerId = organization ? "org-1" : "owner-1";
            Handler = new AdoptionHandler(platform, ownerId);
            if (explicitGrant)
                Handler.KeyResponse = """{"id":"key-owned","full_key":"private-key","purpose":"general","scheduled_write_enabled":false,"scopes":"read write proxy","allow_all_services":false,"allow_all_nodes":false,"allowed_service_ids":["svc-selected"],"allowed_node_ids":[]}""";
            var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, new HttpClient(Handler));
            var runtime = Substitute.For<IActorRuntime>();
            runtime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                .Returns(_ => Task.FromResult<IActor?>(ActorUnavailable ? null : Substitute.For<IActor>()));
            runtime.CreateAsync<ChannelBotRegistrationGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IActor>(null!));
            var dispatch = Substitute.For<IActorDispatchPort>();
            dispatch.DispatchAsync(Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    DispatchedEnvelope = call.Arg<EventEnvelope>();
                    Command = DispatchedEnvelope.Payload.Unpack<ChannelBotRegisterCommand>();
                    if (dispatchUnknown) throw new InvalidOperationException("transport lost acknowledgement");
                    return ActorDispatchPortTestSupport.AcceptAsync(call);
                });
            Deprovisioning = new NyxChannelBotDeprovisioningService(client, Vault,
                NullLogger<NyxChannelBotDeprovisioningService>.Instance);
            var service = new NyxChannelBotAdoptionService(client,
                ChannelRegistrationCommandFacadeTestSupport.CreateFacade(runtime, dispatch),
                new ChannelAgentKeyProvisioningService(client, Vault, NullLogger<ChannelAgentKeyProvisioningService>.Instance,
                    ChannelAgentKeyWriteMode.NyxIdDefault), Deprovisioning, NullLogger<NyxChannelBotAdoptionService>.Instance,
                ChannelExplicitAuthorizationTestSupport.Create());
            var owners = Substitute.For<IChannelRegistrationOwnerResolver>();
            owners.ResolveAsync("caller-token", ownerId, Arg.Any<CancellationToken>())
                .Returns(new ChannelRegistrationOwnerResolution(new(organization ? "actor-1" : ownerId, new(
                    organization ? ChannelRegistrationKeyOwnerKind.Organization : ChannelRegistrationKeyOwnerKind.Personal,
                    ownerId), organization ? ownerId : null), ""));
            var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
            queryPort.QueryAllSnapshotsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationSnapshot>>([]));
            queryPort.GetSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ChannelBotRegistrationSnapshot?>(null));
            _facade = new(queryPort, owners, new VerifiedNyxChannelBotDetail.Reader(client), service,
                ChannelAgentKeyWriteMode.NyxIdDefault);
            Request = new(platform, "caller-token", "https://aevatar.example.com", ownerId, "label", "opaque-requested-slug", "bot-owned",
                RequestedServiceSelection: explicitGrant ? ChannelRegistrationServiceSelection.Explicit(["svc-selected"]) : null);
        }
    }

    private sealed class AdoptionHandler(string platform, string owner) : HttpMessageHandler
    {
        public Dictionary<string, object> Detail { get; } = new()
        {
            ["id"] = "bot-owned", ["platform"] = platform, ["user_id"] = owner,
            ["status"] = "active", ["is_active"] = true,
        };
        public string KeyResponse { get; set; } = """{"id":"key-owned","full_key":"private-key","purpose":"general","scheduled_write_enabled":false,"scopes":"read write proxy","allow_all_services":true,"allow_all_nodes":true,"allowed_service_ids":[],"allowed_node_ids":[]}""";
        public string? DetailBody { get; set; }
        public HttpStatusCode DetailStatus { get; set; } = HttpStatusCode.OK;
        public string Routes { get; set; } = "{\"conversations\":[]}";
        public string? RouteCreateResponseFailure { get; set; }
        public HttpStatusCode? RouteCreateRejection { get; set; }
        public string? OwnershipEvidenceFailure { get; set; }
        public bool RouteActive { get; private set; }
        public bool KeyActive { get; private set; }
        public HttpStatusCode RouteDeleteStatus { get; set; } = HttpStatusCode.NoContent;
        public bool RouteDeleteThrows { get; set; }
        public HttpStatusCode KeyDeleteStatus { get; set; } = HttpStatusCode.NoContent;
        public bool KeyDeleteThrows { get; set; }
        public List<(string Method, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (request.Method == HttpMethod.Post && path == "/api/v1/channel-conversations")
            {
                if (RouteCreateRejection is { } rejection)
                    return new HttpResponseMessage(rejection) { Content = new StringContent("rejected before insertion") };
                RouteActive = true;
                if (RouteCreateResponseFailure == "transport") throw new HttpRequestException("response lost after insertion");
                if (RouteCreateResponseFailure == "server-error")
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("failed after insertion") };
                var body = RouteCreateResponseFailure switch
                {
                    "missing-id" => "{}",
                    "invalid-json" => "not-json",
                    "invalid-id" => "{\"id\":42}",
                    _ => "{\"id\":\"route-owned\"}",
                };
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body) };
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/channel-conversations?", StringComparison.Ordinal) && RouteActive)
            {
                if (OwnershipEvidenceFailure == "transport") throw new HttpRequestException("ownership read unavailable");
                var body = OwnershipEvidenceFailure switch
                {
                    "invalid-list" => "{\"conversations\":[{}]}",
                    "not-yet-visible" => "{\"conversations\":[]}",
                    "other-key" => """{"conversations":[{"id":"route-foreign","channel_bot_id":"bot-owned","agent_api_key_id":"key-foreign","default_agent":true,"is_active":true}]}""",
                    "other-bot" => """{"conversations":[{"id":"route-foreign","channel_bot_id":"bot-foreign","agent_api_key_id":"key-owned","default_agent":true,"is_active":true}]}""",
                    "ambiguous" => """{"conversations":[{"id":"route-owned","channel_bot_id":"bot-owned","agent_api_key_id":"key-owned"},{"id":"route-other","channel_bot_id":"bot-owned","agent_api_key_id":"key-owned"}]}""",
                    _ => """{"conversations":[{"id":"route-owned","channel_bot_id":"bot-owned","agent_api_key_id":"key-owned","default_agent":true,"is_active":true}]}""",
                };
                return new HttpResponseMessage(OwnershipEvidenceFailure == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(body) };
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/api-keys")
                KeyActive = true;
            if (request.Method == HttpMethod.Delete)
            {
                if (path == "/api/v1/channel-conversations/route-owned")
                {
                    if (RouteDeleteThrows) throw new HttpRequestException("private-provider-detail");
                    if ((int)RouteDeleteStatus < 300) RouteActive = false;
                    return new HttpResponseMessage(RouteDeleteStatus) { Content = new StringContent("") };
                }
                if (path == "/api/v1/api-keys/key-owned")
                {
                    if (KeyDeleteThrows) throw new HttpRequestException("private-provider-detail");
                    var status = KeyActive ? KeyDeleteStatus : HttpStatusCode.NotFound;
                    if ((int)status < 300) KeyActive = false;
                    return new HttpResponseMessage(status) { Content = new StringContent("") };
                }
                throw new InvalidOperationException("Unexpected delete: " + path);
            }
            var response = request.Method == HttpMethod.Delete ? "" : path switch
            {
                "/api/v1/channel-bots/bot-owned" => DetailBody ?? JsonSerializer.Serialize(Detail),
                "/api/v1/api-keys" => KeyResponse,
                "/api/v1/channel-conversations" => "{\"id\":\"route-owned\"}",
                _ when path.StartsWith("/api/v1/channel-conversations?", StringComparison.Ordinal) => RouteActive
                    ? """{"conversations":[{"id":"route-owned","channel_bot_id":"bot-owned","agent_api_key_id":"key-owned","default_agent":true,"is_active":true}]}"""
                    : Routes,
                _ => throw new InvalidOperationException("Unexpected resource access: " + path),
            };
            return new HttpResponseMessage(path == "/api/v1/channel-bots/bot-owned" ? DetailStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            };
        }
    }

    private sealed class RecordingVault : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();
        public int Revocations { get; private set; }
        public bool RevokeRejected { get; set; }
        public bool RevokeThrows { get; set; }
        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default) => _inner.PutAsync(request, ct);
        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default) => _inner.ResolveAsync(request, ct);
        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) => _inner.RotateAsync(request, ct);
        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
        {
            Revocations++;
            if (RevokeThrows) throw new HttpRequestException("private-vault-detail");
            if (RevokeRejected) return Task.FromResult(new RevokeSecretResult(false));
            return _inner.RevokeAsync(request, ct);
        }
    }
}
