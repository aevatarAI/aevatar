using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelBotServiceConnection;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationExplicitAuthorization;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationExplicitAuthorizationPreparationTests
{
    [Fact]
    public void Preparation_ConsumesVerifiedOwnerWithoutResolvingCurrentUserAgain()
    {
        var constructor = typeof(ChannelRegistrationExplicitAuthorizationPreparation)
            .GetConstructors().Should().ContainSingle().Subject;
        constructor.GetParameters().Should().NotContain(parameter =>
            parameter.ParameterType == typeof(INyxIdCurrentUserResolver));

        var prepare = typeof(ChannelRegistrationExplicitAuthorizationPreparation)
            .GetMethod(nameof(ChannelRegistrationExplicitAuthorizationPreparation.PrepareAsync))!;
        prepare.GetParameters().Should().ContainSingle(parameter =>
            parameter.ParameterType == typeof(VerifiedChannelRegistrationOwner));
    }

    internal static async Task<VerifiedChannelRegistrationExplicitAuthorization> PrepareVerifiedAsync(
        string? organizationId = null, bool requiresNodes = false)
    {
        var fixture = new Fixture { OrganizationId = organizationId, RequiredNodes = requiresNodes ? ["node-alpha", "node-beta"] : [] };
        fixture.Services.Add(fixture.Service("svc-alpha"));
        var result = await fixture.PrepareAsync(["svc-alpha"], "telegram");
        result.Succeeded.Should().BeTrue();
        return result.Preparation!;
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.OK, " \r\n ")]
    public async Task ConnectionDelete_EmptySuccessfulHttpResponseDoesNotLogFailure(HttpStatusCode status, string body)
    {
        var fixture = new Fixture { Failure = "scope-failure", CleanupStatus = status, CleanupResponseBody = body };

        var result = await fixture.PrepareAsync([]);

        result.ErrorCode.Should().Be("nyxid_scope_plan_unavailable");
        fixture.Deleted.Should().Equal("svc-bot");
        fixture.Logger.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectionDelete_UnsuccessfulHttpResponseWithEmptyBodyStillLogsFailure()
    {
        var fixture = new Fixture
        {
            Failure = "scope-failure", CleanupStatus = HttpStatusCode.ServiceUnavailable, CleanupResponseBody = string.Empty,
        };

        await fixture.PrepareAsync([]);

        fixture.Logger.Messages.Should().ContainSingle().Which.Should().Contain("channel_service_connection_cleanup_failed");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"error\":true}")]
    [InlineData("{\"error\":\"denied\"}")]
    [InlineData("{\"error\":true,\"error\":false}")]
    public async Task ConnectionDelete_InvalidNonEmptyResponseStillLogsFailure(string body)
    {
        var fixture = new Fixture { Failure = "scope-failure", CleanupResponseBody = body };

        await fixture.PrepareAsync([]);

        fixture.Logger.Messages.Should().ContainSingle().Which.Should().Contain("channel_service_connection_cleanup_failed");
    }

    [Theory]
    [InlineData("inventory-unavailable")]
    [InlineData("id")]
    [InlineData("slug")]
    [InlineData("label")]
    [InlineData("inactive")]
    [InlineData("owner")]
    public async Task ConnectionVerificationFailure_LogsRetainedResourceWithoutClaimingDeleteAuthority(string failure)
    {
        var fixture = new Fixture { Failure = failure, Drift = failure };

        var result = await fixture.PrepareAsync([]);

        result.ErrorCode.Should().Be("channel_service_connection_unavailable");
        fixture.Deleted.Should().BeEmpty();
        var message = fixture.ConnectionLogger.Messages.Should().ContainSingle().Subject;
        message.Should().Contain("channel_service_connection_verification_failed_resource_retained")
            .And.Contain("svc-bot").And.Contain("registration-alpha");
        message.Should().NotContain("owner-token").And.NotContain("cli-alpha")
            .And.NotContain("secret-alpha").And.NotContain("provider-body");
    }

    [Theory]
    [InlineData("connection-timeout", "channel_service_connection_unavailable", false)]
    [InlineData("inventory-timeout", "channel_service_connection_unavailable", false)]
    [InlineData("dependency-timeout", "nyxid_scope_plan_unavailable", true)]
    public async Task ProviderCancellationWithoutCallerCancellation_MapsStageFailureAndCleansOwnedConnection(
        string failure, string errorCode, bool shouldDelete)
    {
        var fixture = new Fixture { Failure = failure };
        using var caller = new CancellationTokenSource();

        var result = await fixture.PrepareAsync([], ct: caller.Token);

        caller.IsCancellationRequested.Should().BeFalse();
        result.ErrorCode.Should().Be(errorCode);
        fixture.Deleted.Should().Equal(shouldDelete ? ["svc-bot"] : Array.Empty<string>());
        if (failure == "inventory-timeout")
            fixture.ConnectionLogger.Messages.Should().ContainSingle().Which.Should()
                .Contain("channel_service_connection_verification_failed_resource_retained");
    }

    [Theory]
    [InlineData(typeof(VerifiedChannelBotServiceConnection))]
    [InlineData(typeof(VerifiedChannelRegistrationExplicitAuthorization))]
    [InlineData(typeof(VerifiedChannelRegistrationAuthorizationPlan))]
    [InlineData(typeof(VerifiedChannelRegistrationServiceSelection))]
    public void VerifiedCapabilities_SameAssemblyConsumersCannotConstructCloneOrMutate(Type capability)
    {
        capability.IsSealed.Should().BeTrue();
        capability.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().NotBeEmpty().And.OnlyContain(constructor => constructor.IsPrivate,
                "internal construction permits an ordinary same-assembly provisioner to forge verified capabilities");
        capability.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().OnlyContain(property => property.GetSetMethod(true) == null);
        capability.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().BeNull();
        capability.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().OnlyContain(field => field.IsInitOnly);
        capability.GetMethods(BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().OnlyContain(method => method.IsPrivate,
                "an internal factory would merely relocate the same-assembly minting bypass");
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("timeout")]
    [InlineData("provider-error")]
    public async Task ConnectionCleanupFailure_LogsStableReasonAndResourceIdsWithoutSecrets(string failure)
    {
        var fixture = new Fixture { Failure = "scope-failure", CleanupFailure = failure };

        var result = await fixture.PrepareAsync([]);

        result.ErrorCode.Should().Be("nyxid_scope_plan_unavailable");
        var message = fixture.Logger.Messages.Should().ContainSingle().Subject;
        message.Should().Contain("channel_service_connection_cleanup_failed");
        message.Should().Contain("svc-bot").And.Contain("registration-alpha");
        message.Should().NotContain("owner-token").And.NotContain("cli-alpha")
            .And.NotContain("secret-alpha").And.NotContain("provider-body");
    }

    [Theory]
    [InlineData("lark", false)]
    [InlineData("telegram", false)]
    [InlineData("lark", true)]
    [InlineData("telegram", true)]
    public async Task PlatformProvisioning_VerifiedPreparationCreatesRestrictedKeyAndDispatchesExplicitCommand(string platform, bool businessService)
    {
        var fixture = new Fixture();
        fixture.Services.Add(fixture.Service("svc-business"));

        var result = await fixture.ProvisionAsync(platform, businessService ? ["svc-business"] : []);

        result.Succeeded.Should().BeTrue();
        fixture.ScopeInputs.Should().ContainSingle();
        fixture.HttpPaths.Should().Equal(platform == "lark"
            ? ["/api/v1/keys", "/api/v1/api-keys", "/api/v1/channel-bots", "/api/v1/channel-conversations"]
            : new[] { "/api/v1/api-keys", "/api/v1/channel-bots", "/api/v1/channel-conversations" });
        fixture.Deleted.Should().BeEmpty();
        var command = fixture.Command!;
        command.AuthorizationMode.Should().Be(ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist);
        command.RegistrationServiceAllowlist.Should().NotBeNull();
        command.RegistrationServiceAllowlist.ServiceIds.Should().Equal(businessService ? ["svc-business"] : Array.Empty<string>());
        var expectedServices = new List<string>();
        if (platform == "lark")
            expectedServices.Add("svc-bot");
        if (businessService)
            expectedServices.Add("svc-business");
        command.ChannelAgentKey.Grant.AllowedServiceIds.Should().Equal(expectedServices);
        command.ChannelAgentKey.Grant.ScopePlanDigest.Should().Be("sha256:" + new string('a', 64));
        ChannelRegistrationAuthorizationContract.IsValidNewCommand(command).Should().BeTrue();
        if (platform == "lark")
            command.NyxProviderSlug.Should().Be("api-lark-bot-7");
    }

    [Theory]
    [InlineData("lark")]
    [InlineData("telegram")]
    public async Task PlatformProvisioning_UnverifiedOrganizationOwnerRejectsBeforeNyxMutation(string platform)
    {
        var fixture = new Fixture { RegistrationOwnerScopeId = "org-unverified" };

        var result = await fixture.ProvisionAsync(platform);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("service_owner_forbidden");
        fixture.HttpPaths.Should().BeEmpty();
        fixture.ScopeInputs.Should().BeEmpty();
        fixture.Command.Should().BeNull();
    }

    [Theory]
    [InlineData("lark", "key-drift")]
    [InlineData("telegram", "key-drift")]
    [InlineData("lark", "bot-failure")]
    [InlineData("telegram", "bot-failure")]
    [InlineData("lark", "route-failure")]
    [InlineData("telegram", "route-failure")]
    [InlineData("lark", "mirror-unavailable")]
    [InlineData("telegram", "mirror-unavailable")]
    public async Task PlatformProvisioning_FailureCompensatesResourcesBeforeExclusiveConnection(string platform, string failure)
    {
        var fixture = new Fixture { Failure = failure };
        var result = await fixture.ProvisionAsync(platform);
        result.Succeeded.Should().BeFalse();
        fixture.HttpPaths.Should().Contain("/api/v1/api-keys");
        var expected = new List<string>();
        if (failure == "mirror-unavailable")
            expected.Add("route-alpha");
        if (failure is "route-failure" or "mirror-unavailable")
            expected.Add("bot-alpha");
        expected.Add("key-alpha");
        if (platform == "lark")
            expected.Add("svc-bot");
        fixture.Deleted.Should().Equal(expected);
        var effects = expected.Select(id => "delete:" + id).ToList();
        if (failure != "key-drift")
            effects.Insert(effects.IndexOf("delete:key-alpha") + 1, "vault:revoke");
        fixture.CleanupEffects.Should().Equal(effects);
        fixture.Command.Should().BeNull();
    }

    [Theory]
    [InlineData("lark")]
    [InlineData("telegram")]
    public async Task PlatformProvisioning_UnknownAcceptancePreservesExplicitResources(string platform)
    {
        var fixture = new Fixture { Failure = "unknown-acceptance" };
        var result = await fixture.ProvisionAsync(platform);
        result.Error.Should().Be("local_mirror_acceptance_unknown_remote_cleanup_skipped");
        fixture.Command.Should().NotBeNull();
        fixture.Deleted.Should().BeEmpty();
        fixture.CleanupEffects.Should().BeEmpty();
    }

    [Theory]
    [InlineData("lark")]
    [InlineData("telegram")]
    public async Task PlatformProvisioning_CallerCancelledBeforeDispatch_CompensatesAllExplicitResources(string platform)
    {
        using var caller = new CancellationTokenSource();
        var fixture = new Fixture { OnMirrorTargetResolved = caller.Cancel };

        var result = await fixture.ProvisionAsync(platform, ct: caller.Token);

        caller.IsCancellationRequested.Should().BeTrue();
        fixture.Command.Should().BeNull();
        fixture.DispatchAttempts.Should().Be(0);
        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("provisioning_failed");
        fixture.CleanupEffects.Should().Equal(platform == "lark"
            ? ["delete:route-alpha", "delete:bot-alpha", "delete:key-alpha", "vault:revoke", "delete:svc-bot"]
            : new[] { "delete:route-alpha", "delete:bot-alpha", "delete:key-alpha", "vault:revoke" });
        fixture.DeleteTokens.Should().HaveCount(platform == "lark" ? 4 : 3);
        fixture.DeleteTokens.Should().OnlyContain(token => token.CanBeCanceled && token != caller.Token);
        fixture.VaultRevokeTokens.Should().ContainSingle().Which.CanBeCanceled.Should().BeTrue();
        fixture.VaultRevokeTokens.Single().Should().NotBe(caller.Token);
    }

    [Theory]
    [InlineData("lark")]
    [InlineData("telegram")]
    public async Task PlatformProvisioning_CallerCancelledAfterDispatchStarts_PreservesUnknownAcceptance(string platform)
    {
        using var caller = new CancellationTokenSource();
        var fixture = new Fixture { Failure = "unknown-acceptance", OnDispatchStarted = caller.Cancel };

        var result = await fixture.ProvisionAsync(platform, ct: caller.Token);

        caller.IsCancellationRequested.Should().BeTrue();
        fixture.Command.Should().NotBeNull();
        fixture.DispatchAttempts.Should().Be(1);
        result.Error.Should().Be("local_mirror_acceptance_unknown_remote_cleanup_skipped");
        fixture.CleanupEffects.Should().BeEmpty();
    }

    [Fact]
    public async Task Lark_ProviderNameCannotSelectAnUnrelatedCatalogForAppCredentials()
    {
        var fixture = new Fixture();

        var result = await fixture.PrepareAsync([], providerSlug: "github");

        result.ErrorCode.Should().Be("channel_service_connection_unavailable");
        fixture.CreateBody.Should().BeNull();
        fixture.ScopeInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task Lark_VerifiesOwnerThenExactConnectionBeforeSingleCanonicalScopePlan()
    {
        var fixture = new Fixture();
        fixture.Services.Add(fixture.Service("svc-z"));
        fixture.Services.Add(fixture.Service("svc-a"));
        fixture.Dependencies = ["svc-z", "svc-a", "svc-a"];

        var result = await fixture.PrepareAsync(["svc-z"]);

        result.Succeeded.Should().BeTrue();
        result.Preparation!.Plan.RegistrationServiceIds.Should().Equal("svc-z");
        result.Preparation.Plan.AllowedServiceIds.Should().Equal("svc-a", "svc-bot", "svc-z");
        result.Preparation.Connection!.UserServiceId.Should().Be("svc-bot");
        result.Preparation.Connection.ProviderSlug.Should().Be("api-lark-bot-7");
        fixture.Trace.Should().Equal("inventory", "create", "inventory", "dependencies", "inventory", "scope-plan");
        fixture.ScopeInputs.Should().ContainSingle().Which.Should().Equal("svc-a", "svc-bot", "svc-z");
        fixture.CreateBody!.RootElement.GetProperty("credential").GetString().Should().Contain("cli-alpha");
        fixture.CreateBody.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();
        fixture.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task OrganizationConnection_UsesVerifiedOrganizationAndInventoryNotCreateOwner()
    {
        var fixture = new Fixture { OrganizationId = "org-alpha" };
        fixture.Services.Add(fixture.Service("svc-business"));

        var result = await fixture.PrepareAsync(["svc-business"]);

        result.Succeeded.Should().BeTrue();
        fixture.CreateBody!.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-alpha");
        result.Preparation!.Plan.TargetOrganizationId.Should().Be("org-alpha");
        // The create response deliberately says personal. Only exact inventory is authoritative.
        result.Preparation.Connection!.Owner.Kind.Should().Be(ChannelRegistrationKeyOwnerKind.Organization);
    }

    [Fact]
    public async Task PersonalSelectionWithOrganizationRegistrationOwner_FailsBeforeConnectionOrScopePlan()
    {
        var fixture = new Fixture { RegistrationOwnerScopeId = "org-alpha" };
        fixture.Services.Add(fixture.Service("svc-business"));

        var result = await fixture.PrepareAsync(["svc-business"]);

        result.ErrorCode.Should().Be("service_owner_forbidden");
        fixture.Trace.Should().BeEmpty();
        fixture.CreateBody.Should().BeNull();
        fixture.ScopeInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task OrganizationSelectionWithDifferentRegistrationOwner_FailsBeforeConnectionOrScopePlan()
    {
        var fixture = new Fixture
        {
            OrganizationId = "org-alpha",
            RegistrationOwnerScopeId = "org-beta",
        };
        fixture.Services.Add(fixture.Service("svc-business"));

        var result = await fixture.PrepareAsync(["svc-business"]);

        result.ErrorCode.Should().Be("service_owner_forbidden");
        fixture.Trace.Should().BeEmpty();
        fixture.CreateBody.Should().BeNull();
        fixture.ScopeInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyPersonalSelection_PlansWithoutOrganizationTarget()
    {
        var fixture = new Fixture();

        var result = await fixture.PrepareAsync([], "telegram");

        result.Succeeded.Should().BeTrue();
        result.Preparation!.Plan.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Personal,
            "owner-alpha"));
        fixture.ScopeTargetOrganizationIds.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task EmptyOrganizationSelection_UsesTrustedOrganizationForConnectionAndScopePlan()
    {
        var fixture = new Fixture { OrganizationId = "org-alpha" };

        var result = await fixture.PrepareAsync([]);

        result.Succeeded.Should().BeTrue();
        result.Preparation!.Plan.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Organization,
            "org-alpha"));
        result.Preparation.Plan.TargetOrganizationId.Should().Be("org-alpha");
        fixture.CreateBody!.RootElement.GetProperty("target_org_id").GetString()
            .Should().Be("org-alpha");
        fixture.ScopeTargetOrganizationIds.Should().Equal("org-alpha");
    }

    [Theory]
    [InlineData("missing", "user_service_not_found")]
    [InlineData("inactive", "service_owner_forbidden")]
    [InlineData("foreign-organization", "service_owner_forbidden")]
    public async Task InvalidBusinessSelection_HasNoConnectionOrScopePlanSideEffects(string invalid, string error)
    {
        var fixture = new Fixture();
        if (invalid != "missing")
            fixture.Services.Add(fixture.Service("svc-business") with
            {
                IsActive = invalid != "inactive",
                CredentialSource = invalid == "foreign-organization"
                    ? new NyxIdUserServiceCredentialSource(NyxIdUserServiceCredentialSourceKind.Organization,
                        "org-foreign", OrganizationRole: NyxIdOrganizationRole.Member)
                    : new NyxIdUserServiceCredentialSource(NyxIdUserServiceCredentialSourceKind.Personal),
            });

        var result = await fixture.PrepareAsync(["svc-business"]);

        result.ErrorCode.Should().Be(error);
        fixture.Trace.Should().Equal("inventory");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("slug")]
    [InlineData("label")]
    [InlineData("inactive")]
    [InlineData("owner")]
    [InlineData("reused-id")]
    [InlineData("reused-slug")]
    [InlineData("create-id-missing")]
    [InlineData("create-slug-missing")]
    [InlineData("create-error")]
    public async Task UnverifiedConnection_FailsBeforeScopePlanAndNeverClaimsCleanupOwnership(string drift)
    {
        var fixture = new Fixture { Drift = drift };
        fixture.Services.Add(fixture.Service(drift == "reused-id" ? "svc-bot" : "svc-existing") with
        {
            Slug = drift == "reused-slug" ? "api-lark-bot-7" : "api-lark-bot",
            Label = "another bot",
        });

        var result = await fixture.PrepareAsync([]);

        result.ErrorCode.Should().Be("channel_service_connection_unavailable");
        fixture.ScopeInputs.Should().BeEmpty();
        fixture.Deleted.Should().BeEmpty();
        fixture.Trace.Should().Contain("create");
    }

    [Theory]
    [InlineData("missing", "user_service_not_found")]
    [InlineData("invalid", "nyxid_scope_plan_unavailable")]
    [InlineData("resolver-failure", "nyxid_scope_plan_unavailable")]
    [InlineData("scope-failure", "nyxid_scope_plan_unavailable")]
    public async Task FailureAfterVerifiedCreation_DeletesOnlyOwnedConnectionWithDetachedBoundedToken(
        string failure, string error)
    {
        var fixture = new Fixture { Failure = failure };
        fixture.Dependencies = failure switch
        {
            "missing" => ["svc-missing"],
            "invalid" => [" svc-invalid "],
            _ => [],
        };
        using var caller = new CancellationTokenSource();

        var result = await fixture.PrepareAsync([], ct: caller.Token);

        result.ErrorCode.Should().Be(error);
        fixture.Deleted.Should().Equal("svc-bot");
        fixture.DeleteTokens.Should().ContainSingle();
        fixture.DeleteTokens[0].CanBeCanceled.Should().BeTrue();
        fixture.DeleteTokens[0].Should().NotBe(caller.Token);
        if (failure != "scope-failure")
            fixture.ScopeInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationAfterConnectionVerification_StillCompensatesWithDetachedToken()
    {
        using var caller = new CancellationTokenSource();
        var fixture = new Fixture { OnDependencies = caller.Cancel };

        var act = () => fixture.PrepareAsync([], ct: caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.Deleted.Should().Equal("svc-bot");
        fixture.DeleteTokens[0].Should().NotBe(caller.Token);
    }

    [Fact]
    public async Task TelegramRelayOnly_PlansBusinessAndDependenciesWithoutCreatingConnection()
    {
        var fixture = new Fixture();
        fixture.Services.Add(fixture.Service("svc-llm"));
        fixture.Dependencies = ["svc-llm"];

        var result = await fixture.PrepareAsync([], "telegram");

        result.Succeeded.Should().BeTrue();
        result.Preparation!.Connection.Should().BeNull();
        result.Preparation.Plan.RegistrationServiceIds.Should().BeEmpty();
        fixture.ScopeInputs.Should().ContainSingle().Which.Should().Equal("svc-llm");
        fixture.Trace.Should().Equal("inventory", "dependencies", "inventory", "scope-plan");
    }

    [Fact]
    public async Task TelegramRequiredProxyWithoutAuthoritativeBotIdentity_FailsClosed()
    {
        var fixture = new Fixture { RequiresTelegramProxy = true };

        var result = await fixture.PrepareAsync([], "telegram");

        result.ErrorCode.Should().Be("channel_service_connection_unavailable");
        fixture.ScopeInputs.Should().BeEmpty();
        fixture.CreateBody.Should().BeNull();
    }

    [Fact]
    public async Task DefaultDependencyResolver_DoesNotGuessIdsFromSkillOrProviderNames()
    {
        var resolver = new ChannelRegistrationConfiguredDependencyResolver();

        var result = await resolver.ResolveAsync("scope-alpha", "telegram", "skill-is-not-a-service-id", CancellationToken.None);

        result.RequiredServiceIds.Should().BeEmpty();
        result.RequiresBotProxyConnection.Should().BeFalse();
    }

    private sealed class Fixture : IChannelRegistrationNyxIdAuthorizationPort, IChannelRegistrationDependencyResolver
    {
        public List<NyxIdUserService> Services { get; } = [];
        public List<string> Trace { get; } = [];
        public List<string> HttpPaths { get; } = [];
        public List<string[]> ScopeInputs { get; } = [];
        public List<string?> ScopeTargetOrganizationIds { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<string> CleanupEffects { get; } = [];
        public List<CancellationToken> DeleteTokens { get; } = [];
        public List<CancellationToken> VaultRevokeTokens { get; } = [];
        public Action? OnMirrorTargetResolved { get; init; }
        public Action? OnDispatchStarted { get; init; }
        public int DispatchAttempts { get; private set; }
        public IReadOnlyList<string> Dependencies { get; set; } = [];
        public IReadOnlyList<string> RequiredNodes { get; init; } = [];
        public string? OrganizationId { get; init; }
        public string? RegistrationOwnerScopeId { get; init; }
        public string? Drift { get; init; }
        public string? Failure { get; init; }
        public string? CleanupFailure { get; init; }
        public HttpStatusCode CleanupStatus { get; init; } = HttpStatusCode.OK;
        public string CleanupResponseBody { get; init; } = "{}";
        public RecordingLogger<ChannelRegistrationExplicitAuthorizationPreparation> Logger { get; } = new();
        public RecordingLogger<ChannelRegistrationNyxIdBotConnectionPort> ConnectionLogger { get; } = new();
        public bool RequiresTelegramProxy { get; init; }
        public Action? OnDependencies { get; init; }
        public JsonDocument? CreateBody { get; private set; }
        public ChannelBotRegisterCommand? Command { get; private set; }

        public NyxIdUserService Service(string id) => new(id, id + "-slug", null, null, true,
            OrganizationId is null
                ? new NyxIdUserServiceCredentialSource(NyxIdUserServiceCredentialSourceKind.Personal)
                : new NyxIdUserServiceCredentialSource(NyxIdUserServiceCredentialSourceKind.Organization,
                    OrganizationId, OrganizationRole: NyxIdOrganizationRole.Admin));

        public async Task<ChannelRegistrationExplicitAuthorizationResult> PrepareAsync(
            string[] serviceIds, string platform = "lark", CancellationToken ct = default, string providerSlug = "api-lark-bot")
        {
            var ownerResolution = ResolveOwner();
            if (!ownerResolution.Succeeded)
                return new ChannelRegistrationExplicitAuthorizationResult(null, ownerResolution.ErrorCode);

            var client = CreateClient();
            return await CreatePreparation(client).PrepareAsync(
                BuildRequest(serviceIds, platform, providerSlug),
                "registration-alpha",
                ownerResolution.Owner!,
                ct);
        }

        public async Task<NyxChannelBotProvisioningResult> ProvisionAsync(
            string platform, string[]? serviceIds = null, CancellationToken ct = default)
        {
            var client = CreateClient();
            var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
            actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                .Returns(_ =>
                {
                    OnMirrorTargetResolved?.Invoke();
                    return Task.FromResult<IActor?>(Failure == "mirror-unavailable" ? null : Substitute.For<IActor>());
                });
            if (Failure == "mirror-unavailable")
                actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(ChannelBotRegistrationGAgent.WellKnownId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IActor>(null!));
            ((IActorDispatchPort)actorRuntime).DispatchAsync(ChannelBotRegistrationGAgent.WellKnownId,
                    Arg.Any<EventEnvelope>(),
                    Arg.Any<CancellationToken>())
                .Returns(info =>
                {
                    DispatchAttempts++;
                    info.Arg<CancellationToken>().ThrowIfCancellationRequested();
                    Command = info.Arg<EventEnvelope>().Payload.Unpack<ChannelBotRegisterCommand>();
                    OnDispatchStarted?.Invoke();
                    return Failure == "unknown-acceptance"
                        ? Task.FromException<DispatchAdmission>(new OperationCanceledException("provider secret"))
                        : ActorDispatchPortTestSupport.AcceptAsync(info);
                });
            var facade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);
            var storedSecrets = new InMemorySecretVault();
            var vault = Substitute.For<ISecretVault>();
            vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
                .Returns(info => storedSecrets.PutAsync(info.Arg<StoreSecretRequest>(), info.Arg<CancellationToken>()));
            vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
                .Returns(info =>
                {
                    CleanupEffects.Add("vault:revoke");
                    VaultRevokeTokens.Add(info.Arg<CancellationToken>());
                    return storedSecrets.RevokeAsync(info.Arg<RevokeSecretRequest>(), info.Arg<CancellationToken>());
                });
            var keyProvisioning = new ChannelAgentKeyProvisioningService(client, vault,
                NullLogger<ChannelAgentKeyProvisioningService>.Instance, ChannelAgentKeyWriteMode.NyxIdDefault);
            var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" };
            var ownerResolver = CreateOwnerResolver();
            INyxChannelBotProvisioningService service = platform == "lark"
                ? new NyxLarkProvisioningService(client, options, facade, keyProvisioning,
                    ownerResolver, NullLogger<NyxLarkProvisioningService>.Instance, CreatePreparation(client))
                : new NyxTelegramProvisioningService(client, options, facade, keyProvisioning, ownerResolver,
                    NullLogger<NyxTelegramProvisioningService>.Instance, CreatePreparation(client));
            return await service.ProvisionAsync(BuildRequest(serviceIds ?? [], platform, "api-lark-bot"), ct);
        }

        private NyxIdApiClient CreateClient() => new(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new Handler(this)));

        private ChannelRegistrationExplicitAuthorizationPreparation CreatePreparation(NyxIdApiClient client)
        {
            using var services = new ServiceCollection()
                .AddSingleton<ILogger<ChannelRegistrationNyxIdBotConnectionPort>>(ConnectionLogger)
                .BuildServiceProvider();
            var connectionPort = ActivatorUtilities.CreateInstance<ChannelRegistrationNyxIdBotConnectionPort>(services, client, this);
            return new ChannelRegistrationExplicitAuthorizationPreparation(
                new ChannelRegistrationAuthorizationPlanner(this), this, connectionPort,
                Logger);
        }

        private IChannelRegistrationOwnerResolver CreateOwnerResolver()
        {
            var resolver = Substitute.For<IChannelRegistrationOwnerResolver>();
            var ownerId = RegistrationOwnerScopeId ?? OrganizationId ?? "owner-alpha";
            resolver.ResolveAsync("owner-token", ownerId, Arg.Any<CancellationToken>())
                .Returns(ResolveOwner());
            return resolver;
        }

        private ChannelRegistrationOwnerResolution ResolveOwner()
        {
            const string actorId = "owner-alpha";
            var ownerId = RegistrationOwnerScopeId ?? OrganizationId ?? actorId;
            if (string.Equals(ownerId, actorId, StringComparison.Ordinal))
            {
                return new ChannelRegistrationOwnerResolution(
                    new VerifiedChannelRegistrationOwner(
                        actorId,
                        new ChannelRegistrationKeyOwner(
                            ChannelRegistrationKeyOwnerKind.Personal,
                            actorId),
                        null),
                    string.Empty);
            }

            if (OrganizationId is null ||
                !string.Equals(ownerId, OrganizationId, StringComparison.Ordinal))
            {
                return new ChannelRegistrationOwnerResolution(null, "service_owner_forbidden");
            }

            return new ChannelRegistrationOwnerResolution(
                new VerifiedChannelRegistrationOwner(
                    actorId,
                    new ChannelRegistrationKeyOwner(
                        ChannelRegistrationKeyOwnerKind.Organization,
                        ownerId),
                    ownerId),
                string.Empty);
        }

        private NyxChannelBotProvisioningRequest BuildRequest(string[] serviceIds, string platform, string providerSlug)
        {
            using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { service_ids = serviceIds }));
            ChannelRegistrationServiceIdsJsonParser.TryParse(input.RootElement, out var selection).Should().BeTrue();
            return new NyxChannelBotProvisioningRequest(
                platform, "owner-token", "https://aevatar.example.com",
                RegistrationOwnerScopeId ?? OrganizationId ?? "owner-alpha", "Bot", providerSlug,
                Lark: new NyxChannelLarkCredentials("cli-alpha", "secret-alpha", "verification-alpha"),
                Credentials: new Dictionary<string, string> { ["bot_token"] = "token-alpha" },
                RequestedServiceSelection: selection);
        }

        public Task<ChannelRegistrationDependencies> ResolveAsync(string scopeId, string platform,
            string defaultSkillName, CancellationToken ct)
        {
            Trace.Add("dependencies");
            OnDependencies?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (Failure == "resolver-failure")
                throw new InvalidOperationException("provider secret body");
            if (Failure == "dependency-timeout")
                throw new OperationCanceledException("provider-body");
            return Task.FromResult(new ChannelRegistrationDependencies(Dependencies,
                platform == "lark" || RequiresTelegramProxy));
        }

        public Task<NyxIdApiAccessResult<NyxIdUserServices>> ReadUserServicesAsync(string accessToken, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Trace.Add("inventory");
            if (Trace.Contains("create") && Failure == "inventory-unavailable")
                return Task.FromResult(new NyxIdApiAccessResult<NyxIdUserServices>(null,
                    new(NyxIdApiAccessFailureKind.Transport, "provider-body")));
            if (Trace.Contains("create") && Failure == "inventory-timeout")
                throw new OperationCanceledException("provider-body");
            return Task.FromResult(new NyxIdApiAccessResult<NyxIdUserServices>(new(Services.ToArray()), null));
        }

        public Task<NyxIdApiAccessResult<NyxIdApiKeyScopePlan>> PlanApiKeyScopeAsync(string accessToken,
            IReadOnlyList<string> selectedServiceIds, string? targetOrganizationId, CancellationToken ct)
        {
            Trace.Add("scope-plan");
            ScopeInputs.Add(selectedServiceIds.ToArray());
            ScopeTargetOrganizationIds.Add(targetOrganizationId);
            if (Failure == "scope-failure")
                return Task.FromResult(new NyxIdApiAccessResult<NyxIdApiKeyScopePlan>(null,
                    new(NyxIdApiAccessFailureKind.Provider, "secret provider body")));
            var actor = new NyxIdScopePlanPrincipal("owner-alpha", NyxIdScopePlanPrincipalKind.Personal);
            var owner = OrganizationId is null ? actor : new NyxIdScopePlanPrincipal(OrganizationId, NyxIdScopePlanPrincipalKind.Organization);
            return Task.FromResult(new NyxIdApiAccessResult<NyxIdApiKeyScopePlan>(new(
                NyxIdApiAccessResponseParser.ScopePlanAuthority, NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                NyxIdApiAccessResponseParser.ScopePlanPolicyVersion, actor, owner,
                selectedServiceIds.Select(id => new NyxIdScopePlanServiceGrant(id, owner,
                    new(RequiredNodes.Count == 0 ? NyxIdScopePlanNodeGrantKind.NotRequired : NyxIdScopePlanNodeGrantKind.Required,
                        RequiredNodes))).ToArray(), selectedServiceIds, RequiredNodes,
                DateTimeOffset.Parse("2026-09-10T00:00:00Z"), "sha256:" + new string('a', 64),
                new(NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot, "scope_plan_digest", NyxIdScopePlanPostCreationDrift.FailClosed),
                new(true, true, NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes, true)), null));
        }

        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                fixture.HttpPaths.Add(request.RequestUri!.AbsolutePath);
                if (request.Method == HttpMethod.Delete)
                {
                    fixture.Deleted.Add(request.RequestUri!.Segments.Last());
                    fixture.CleanupEffects.Add("delete:" + request.RequestUri.Segments.Last());
                    fixture.DeleteTokens.Add(ct);
                    if (fixture.CleanupFailure == "exception")
                        throw new HttpRequestException("owner-token cli-alpha secret-alpha provider-body");
                    if (fixture.CleanupFailure == "timeout")
                        throw new TaskCanceledException("owner-token cli-alpha secret-alpha provider-body");
                    if (fixture.CleanupFailure == "provider-error")
                        return Response("""{"error":true,"body":"owner-token cli-alpha secret-alpha provider-body"}""");
                    return new HttpResponseMessage(fixture.CleanupStatus)
                    {
                        Content = new StringContent(fixture.CleanupResponseBody, Encoding.UTF8, "application/json"),
                    };
                }
                if (request.RequestUri.AbsolutePath == "/api/v1/api-keys")
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    body.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
                    body.RootElement.GetProperty("allow_all_nodes").GetBoolean().Should().BeFalse();
                    body.RootElement.GetProperty("scope_plan_digest").GetString().Should().Be("sha256:" + new string('a', 64));
                    return Response(JsonSerializer.Serialize(new
                    {
                        id = "key-alpha", full_key = "nyxid_ag_secret_alpha", purpose = "general",
                        scheduled_write_enabled = false, scopes = "read write proxy",
                        allow_all_services = false, allow_all_nodes = false,
                        allowed_service_ids = fixture.Failure == "key-drift" ? new[] { "svc-unplanned" } : fixture.ScopeInputs.Single(),
                        allowed_node_ids = Array.Empty<string>(),
                    }));
                }
                if (request.RequestUri.AbsolutePath == "/api/v1/channel-bots")
                    return Response(fixture.Failure == "bot-failure" ? "{\"error\":true}" : "{\"id\":\"bot-alpha\"}");
                if (request.RequestUri.AbsolutePath == "/api/v1/channel-conversations")
                    return Response(fixture.Failure == "route-failure" ? "{\"error\":true}" : "{\"id\":\"route-alpha\"}");
                request.RequestUri!.AbsolutePath.Should().Be("/api/v1/keys");
                fixture.Trace.Add("create");
                if (fixture.Failure == "connection-timeout")
                    throw new OperationCanceledException("provider-body");
                fixture.CreateBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var label = fixture.CreateBody.RootElement.GetProperty("label").GetString();
                fixture.Services.Add(fixture.Service(fixture.Drift == "id" ? "svc-wrong" : "svc-bot") with
                {
                    Slug = fixture.Drift == "slug" ? "wrong-slug" : "api-lark-bot-7",
                    Label = fixture.Drift == "label" ? "another bot" : label,
                    IsActive = fixture.Drift != "inactive",
                    CredentialSource = fixture.Drift == "owner"
                        ? new(NyxIdUserServiceCredentialSourceKind.Organization, "org-other", OrganizationRole: NyxIdOrganizationRole.Admin)
                        : fixture.Service("svc-bot").CredentialSource,
                });
                return Response(fixture.Drift switch
                {
                    "create-id-missing" => """{"slug":"api-lark-bot-7"}""",
                    "create-slug-missing" => """{"id":"svc-bot"}""",
                    "create-error" => """{"error":true,"body":"provider-secret"}""",
                    _ => """{"id":"svc-bot","slug":"api-lark-bot-7","credential_source":{"type":"personal"}}""",
                });
            }

            private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add($"{formatter(state, exception)} {exception}");
    }

}
