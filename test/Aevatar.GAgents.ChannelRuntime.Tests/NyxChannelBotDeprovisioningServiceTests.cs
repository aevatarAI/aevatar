using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Service-level tests for <see cref="NyxChannelBotDeprovisioningService"/>. These drive a real
/// <see cref="NyxIdApiClient"/> over a canned <see cref="HttpMessageHandler"/> so the successful
/// <c>204 No Content</c> and "404 = already gone = success" classifications are exercised against
/// the actual client contract (non-2xx is returned as <c>{"error":true,"status":...}</c>, not thrown).
/// </summary>
public sealed class NyxChannelBotDeprovisioningServiceTests
{
    private const string BaseUrl = "https://nyx.example.com";

    private const string RoutePath = "/api/v1/channel-conversations/route-1";
    private const string ChannelBotPath = "/api/v1/channel-bots/bot-1";
    private const string ApiKeyPath = "/api/v1/api-keys/key-1";

    [Fact]
    public void FromRegistration_ValidNewModelUsesTypedAgentKey()
    {
        var registration = ValidNewModelRegistration();

        var request = NyxChannelBotDeprovisioningRequest.FromRegistration(registration);

        request.AgentKeyId.Should().Be("key-1");
        request.SecretReference.Should().BeEquivalentTo(registration.ChannelAgentKey.SecretReference);
        request.SecretReference.Should().NotBeSameAs(registration.ChannelAgentKey.SecretReference);
        request.AgentKeyDeletionRequired.Should().BeTrue();
    }

    [Fact]
    public void FromRegistration_HistoricalLegacyUsesLegacyAliases()
    {
        var legacyReference = CredentialReference();
        var request = NyxChannelBotDeprovisioningRequest.FromRegistration(
            new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxAgentApiKeyId = "key-1",
                WorkflowResultDeliveryCredential = legacyReference,
            });

        request.AgentKeyId.Should().Be("key-1");
        request.SecretReference.Should().BeEquivalentTo(legacyReference);
        request.SecretReference.Should().NotBeSameAs(legacyReference);
        request.AgentKeyDeletionRequired.Should().BeTrue();
    }

    [Fact]
    public void FromRegistration_AllowlistOnlyInvalidContractDoesNotUseLegacyAliases()
    {
        var request = NyxChannelBotDeprovisioningRequest.FromRegistration(
            new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxAgentApiKeyId = "key-legacy",
                WorkflowResultDeliveryCredential = CredentialReference(),
                RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist(),
            });

        request.AgentKeyId.Should().BeNull();
        request.SecretReference.Should().BeNull();
        request.AgentKeyDeletionRequired.Should().BeTrue();
    }

    [Fact]
    public void FromRegistration_AliasMismatchInvalidContractDoesNotUseTypedOrLegacyAliases()
    {
        var registration = ValidNewModelRegistration();
        registration.NyxAgentApiKeyId = "key-legacy";

        var request = NyxChannelBotDeprovisioningRequest.FromRegistration(registration);

        request.AgentKeyId.Should().BeNull();
        request.SecretReference.Should().BeNull();
        request.AgentKeyDeletionRequired.Should().BeTrue();
    }

    [Fact]
    public async Task DeprovisionAsync_NewModelWithoutTypedAgentKeyFailsHardWithoutLegacyFallback()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, "/api/v1/api-keys/key-legacy", HttpStatusCode.OK, "{}");
        var request = NyxChannelBotDeprovisioningRequest.FromRegistration(
            new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxConversationRouteId = "route-1",
                NyxChannelBotId = "bot-1",
                NyxAgentApiKeyId = "key-legacy",
                AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
            });

        var result = await CreateService(handler).DeprovisionAsync(
            "token",
            request,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ChannelBotRemoved.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeFalse();
        handler.DeletedPaths.Should().Equal(RoutePath, ChannelBotPath);
    }

    [Fact]
    public async Task DeprovisionAsync_DeletesRemoteResourcesThenRevokesVault_AndSucceeds()
    {
        var effects = new List<string>();
        var handler = new StatusRoutingHandler(effects);
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");
        var vault = new RecordingSecretVault(effects);

        var result = await CreateService(handler, vault).DeprovisionAsync(
            "token", BuildRequest(CredentialReference()), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
        handler.DeletedPaths.Should().Equal(RoutePath, ChannelBotPath, ApiKeyPath);
        vault.RevokeRequests.Should().ContainSingle();
        vault.RevokeRequests[0].Ref.Should().Be("vault://channel/key-1");
        vault.RevokeRequests[0].SubjectId.Should().Be("key-1");
        effects.Should().Equal(
            "nyx:route-delete",
            "nyx:bot-delete",
            "nyx:key-delete",
            "vault:revoke");
    }

    [Fact]
    public async Task DeprovisionAsync_204NoContentOnEveryResource_IsTreatedAsSuccess()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.NoContent, string.Empty);
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.NoContent, string.Empty);
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.NoContent, string.Empty);

        var result = await CreateService(handler).DeprovisionAsync(
            "token", BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
        handler.DeletedPaths.Should().Equal(RoutePath, ChannelBotPath, ApiKeyPath);
    }

    [Fact]
    public async Task DeprovisionAsync_404OnEveryResource_IsTreatedAsSuccess()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.NotFound, """{"detail":"gone"}""");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.NotFound, """{"detail":"gone"}""");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.NotFound, """{"detail":"gone"}""");

        var result = await CreateService(handler).DeprovisionAsync(
            "token", BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task DeprovisionAsync_HardChannelBotFailure_StillAttemptsIndependentAgentKeyAndVault()
    {
        var effects = new List<string>();
        var handler = new StatusRoutingHandler(effects);
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.InternalServerError, """{"detail":"boom"}""");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");
        var vault = new RecordingSecretVault(effects);

        var result = await CreateService(handler, vault).DeprovisionAsync(
            "token", BuildRequest(CredentialReference()), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ChannelBotRemoved.Should().BeFalse();
        result.AgentKeyRemoved.Should().BeTrue();
        handler.DeletedPaths.Should().Equal(RoutePath, ChannelBotPath, ApiKeyPath);
        vault.RevokeRequests.Should().ContainSingle();
        effects.Should().Equal("nyx:route-delete", "nyx:bot-delete", "nyx:key-delete", "vault:revoke");
    }

    [Fact]
    public async Task DeprovisionAsync_HardAgentKeyFailure_FailsAndSkipsVault()
    {
        var effects = new List<string>();
        var handler = new StatusRoutingHandler(effects);
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.InternalServerError, """{"detail":"key-boom"}""");
        var vault = new RecordingSecretVault(effects);

        var result = await CreateService(handler, vault).DeprovisionAsync(
            "token", BuildRequest(CredentialReference()), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ChannelBotRemoved.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeFalse();
        result.Warnings.Should().BeEmpty();
        vault.RevokeRequests.Should().BeEmpty();
        effects.Should().Equal("nyx:route-delete", "nyx:bot-delete", "nyx:key-delete");
    }

    [Fact]
    public async Task DeprovisionAsync_ResidualRouteAndVaultFailures_StillSucceed_WithWarnings()
    {
        var effects = new List<string>();
        var handler = new StatusRoutingHandler(effects);
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.InternalServerError, """{"detail":"route-provider-secret"}""");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");
        var vault = new RecordingSecretVault(effects)
        {
            RevokeException = new InvalidOperationException("vault provider secret"),
        };

        var result = await CreateService(handler, vault).DeprovisionAsync(
            "token", BuildRequest(CredentialReference()), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeTrue();
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain(w => w.Contains("conversation_route_delete_failed") && w.Contains("route-1"));
        result.Warnings.Should().Contain("vault_revoke_failed");
        result.Warnings.Should().NotContain(w => w.Contains("vault://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeprovisionAsync_RouteTransportFailure_RemainsBestEffort()
    {
        var handler = new StatusRoutingHandler();
        handler.ThrowOn(
            HttpMethod.Delete,
            RoutePath,
            new HttpRequestException("route provider secret"));
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");

        var result = await CreateService(handler).DeprovisionAsync(
            "token", BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeTrue();
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("conversation_route_delete_failed id=route-1");
        result.Warnings.Should().NotContain(w => w.Contains("provider secret", StringComparison.Ordinal));
        handler.DeletedPaths.Should().Equal(RoutePath, ChannelBotPath, ApiKeyPath);
    }

    [Fact]
    public async Task DeprovisionAsync_SkipsBlankIds()
    {
        var handler = new StatusRoutingHandler();
        // Only the channel-bot has an id; the route and Agent Key are blank and must not be called.
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");

        var result = await CreateService(handler).DeprovisionAsync(
            "token",
            BuildRequest() with
            {
                ConversationRouteId = "  ",
                AgentKeyId = null,
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
        handler.DeletedPaths.Should().Equal(ChannelBotPath);
    }

    [Fact]
    public async Task DeprovisionAsync_NoChannelBotId_SucceedsWithoutCallingChannelBotDelete()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");

        var result = await CreateService(handler).DeprovisionAsync(
            "token",
            BuildRequest() with { ChannelBotId = null },
            CancellationToken.None);

        // No channel-bot to delete means that gate is vacuously satisfied; the route is best
        // effort, while the identified Agent Key remains a hard deletion gate.
        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.AgentKeyRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
        handler.DeletedPaths.Should().Equal(RoutePath, ApiKeyPath);
    }

    [Fact]
    public async Task DeprovisionAsync_MissingVaultReference_DoesNotBlockDeletion()
    {
        var handler = SuccessfulHandler();
        var vault = new RecordingSecretVault();

        var result = await CreateService(handler, vault).DeprovisionAsync(
            "token", BuildRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
        vault.RevokeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeprovisionAsync_InvalidVaultReference_DoesNotBlockDeletion()
    {
        var handler = SuccessfulHandler();
        var vault = new RecordingSecretVault();
        var invalidReference = CredentialReference();
        invalidReference.Ref = " ";

        var result = await CreateService(handler, vault).DeprovisionAsync(
            "token", BuildRequest(invalidReference), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Be("vault_secret_reference_invalid");
        vault.RevokeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeprovisionAsync_VaultRevokeReturnsFalse_ReportsWarningWithoutBlockingDeletion()
    {
        var handler = SuccessfulHandler();
        var vault = new RecordingSecretVault
        {
            RevokeResult = new RevokeSecretResult(false),
        };

        var result = await CreateService(handler, vault).DeprovisionAsync(
            "token", BuildRequest(CredentialReference()), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Be("vault_revoke_failed");
        vault.RevokeRequests.Should().ContainSingle();
    }

    private static StatusRoutingHandler SuccessfulHandler()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");
        return handler;
    }

    private static NyxChannelBotDeprovisioningRequest BuildRequest(
        SecretReference? secretReference = null) =>
        new(
            RegistrationId: "reg-1",
            Platform: "lark",
            ConversationRouteId: "route-1",
            ChannelBotId: "bot-1",
            AgentKeyId: "key-1",
            SecretReference: secretReference);

    private static SecretReference CredentialReference() => new()
    {
        Ref = "vault://channel/key-1",
        Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
        OwnerScopeKey = "scope-1",
        Version = 1,
        Fingerprint = "sha256:key-1",
        CreatedAtUnixMs = 1788825600000,
    };

    private static ChannelBotRegistrationEntry ValidNewModelRegistration()
    {
        var reference = CredentialReference();
        return new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            ScopeId = "scope-1",
            NyxAgentApiKeyId = "key-1",
            WorkflowResultDeliveryCredential = reference.Clone(),
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
            ChannelAgentKey = new ChannelAgentKeyCredential
            {
                ApiKeyId = "key-1",
                SecretReference = reference,
                Grant = new ChannelAgentKeyGrantSnapshot
                {
                    AllowAllServices = true,
                    AllowAllNodes = true,
                },
            },
        };
    }

    private static NyxChannelBotDeprovisioningService CreateService(
        StatusRoutingHandler handler,
        ISecretVault? secretVault = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = BaseUrl },
            new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) });
        return new NyxChannelBotDeprovisioningService(
            nyxClient,
            secretVault ?? new RecordingSecretVault(),
            NullLogger<NyxChannelBotDeprovisioningService>.Instance);
    }

    private sealed class StatusRoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _exceptions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string>? _effects;

        public StatusRoutingHandler(List<string>? effects = null)
        {
            _effects = effects;
        }

        public List<string> DeletedPaths { get; } = [];

        public void Set(HttpMethod method, string path, HttpStatusCode status, string body)
        {
            _responses[$"{method.Method}:{path}"] = (status, body);
        }

        public void ThrowOn(HttpMethod method, string path, Exception exception)
        {
            _exceptions[$"{method.Method}:{path}"] = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (request.Method == HttpMethod.Delete)
            {
                DeletedPaths.Add(path);
                _effects?.Add(path switch
                {
                    RoutePath => "nyx:route-delete",
                    ChannelBotPath => "nyx:bot-delete",
                    ApiKeyPath => "nyx:key-delete",
                    _ => "nyx:delete",
                });
            }

            if (_exceptions.TryGetValue($"{request.Method.Method}:{path}", out var exception))
                return Task.FromException<HttpResponseMessage>(exception);

            if (!_responses.TryGetValue($"{request.Method.Method}:{path}", out var canned))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"detail":"unrouted"}""", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(canned.Status)
            {
                Content = new StringContent(canned.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingSecretVault(List<string>? effects = null) : ISecretVault
    {
        public RevokeSecretResult RevokeResult { get; set; } = new(true);

        public Exception? RevokeException { get; set; }

        public List<RevokeSecretRequest> RevokeRequests { get; } = [];

        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
        {
            RevokeRequests.Add(request);
            effects?.Add("vault:revoke");
            return RevokeException is null
                ? Task.FromResult(RevokeResult)
                : Task.FromException<RevokeSecretResult>(RevokeException);
        }
    }
}
