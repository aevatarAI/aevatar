using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdAssistantActionRegistryTests
{
    private const string LegacyRevision = "nyxid-assistant-actions.v4";
    private const string TransitionRevision = "nyxid-assistant-actions.v5";
    private const string LeastScopeRevision = "nyxid-assistant-actions.v6";
    private const string KeyRotationRevision = "nyxid-assistant-actions.v7";
    private const string SupportedRevision = "nyxid-assistant-actions.v8";

    [Fact]
    public void Load_ShouldPinSchemaVersionAndRevision()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize());

        registry.SchemaVersion.Should().Be(4);
        registry.RegistryRevision.Should().Be(SupportedRevision);

        var legacy = NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: LegacyRevision));
        legacy.RegistryRevision.Should().Be(LegacyRevision);
        legacy.TryGetDefinition("service.connect", out _).Should().BeTrue();

        Action act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(schemaVersion: 3));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_SCHEMA_UNSUPPORTED");

        act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: "nyxid-assistant-actions.future"));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_REVISION_UNSUPPORTED");

        act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: SupportedRevision));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public void Load_ShouldIgnoreUnknownActionWhenExecutableActionsArePresent()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithUnknownAction());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("workflow.launch", out _).Should().BeFalse();
    }

    [Fact]
    public void Load_ShouldPinWaveOneSchemasAndKeepUnimplementedActionsClosed()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        registry.TryGetDefinition("key.create", out _).Should().BeFalse();
        registry.TryGetDefinition("key.rotate", out _).Should().BeFalse();

        var legacy = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(revision: LegacyRevision));
        legacy.TryGetDefinition("service.connect", out _).Should().BeTrue();
        legacy.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        legacy.TryGetDefinition("key.create", out _).Should().BeFalse();
        legacy.TryGetDefinition("key.rotate", out _).Should().BeFalse();

        Action staleReauthorizeSchema = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(
                serviceReauthorizeSchema: StaleServiceReauthorizeSchema));
        staleReauthorizeSchema.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action relaxedKeyCreateSchema = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(
                keyCreateSchema: RelaxedKeyCreateSchema));
        relaxedKeyCreateSchema.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action callerRememberPolicy = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(keyCreateRememberEligible: true));
        callerRememberPolicy.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action rememberedReauthorization = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(serviceReauthorizeRememberEligible: true));
        rememberedReauthorization.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public void Load_ShouldExposeLeastScopeKeyCreateOnlyInV6()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithLeastScopeKeyCreate());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("key.create", out _).Should().BeTrue();
        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        registry.TryGetDefinition("key.rotate", out _).Should().BeFalse();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                LeastScopeRevision,
                NyxIdAssistantActionKind.KeyCreate)
            .Should().BeTrue();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                TransitionRevision,
                NyxIdAssistantActionKind.KeyCreate)
            .Should().BeFalse();

        Action staleSchema = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithLeastScopeKeyCreate(KeyCreateSchema));
        staleSchema.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public void Load_ShouldExposeKeyRotationOnlyInV7()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("key.create", out _).Should().BeTrue();
        registry.TryGetDefinition("key.rotate", out _).Should().BeTrue();
        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                KeyRotationRevision,
                NyxIdAssistantActionKind.KeyRotate)
            .Should().BeTrue();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                LeastScopeRevision,
                NyxIdAssistantActionKind.KeyRotate)
            .Should().BeFalse();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                KeyRotationRevision,
                NyxIdAssistantActionKind.ServiceReauthorize)
            .Should().BeFalse();
    }

    [Fact]
    public void Load_ShouldExposeServiceReauthorizeOnlyInV8()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("key.create", out _).Should().BeTrue();
        registry.TryGetDefinition("key.rotate", out _).Should().BeTrue();
        registry.TryGetDefinition("service.reauthorize", out var definition).Should().BeTrue();
        definition!.Action.Should().Be(NyxIdAssistantActionKind.ServiceReauthorize);
        definition.RememberEligible.Should().BeFalse();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                SupportedRevision,
                NyxIdAssistantActionKind.ServiceReauthorize)
            .Should().BeTrue();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                SupportedRevision,
                NyxIdAssistantActionKind.KeyRotate)
            .Should().BeTrue();
        foreach (var revision in new[]
                 {
                     LegacyRevision,
                     TransitionRevision,
                     LeastScopeRevision,
                     KeyRotationRevision,
                 })
        {
            NyxIdAssistantActionRegistry.IsActionExecutable(
                    revision,
                    NyxIdAssistantActionKind.ServiceReauthorize)
                .Should().BeFalse(revision);
        }

        var validated = registry.ValidateRequest(
            "service.reauthorize",
            """{"userServiceId":"us-github-alpha","requestedScopes":["repo","read:org"]}""");
        validated.Definition.Action.Should().Be(NyxIdAssistantActionKind.ServiceReauthorize);
        validated.Params.ServiceReauthorize.UserServiceId.Should().Be("us-github-alpha");
        validated.Params.ServiceReauthorize.RequestedScopes.Should().Equal("repo", "read:org");
    }

    [Fact]
    public void Load_ShouldRejectServiceReauthorizeDescriptorDriftInV8()
    {
        Action staleSchema = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize(
                serviceReauthorizeSchema: StaleServiceReauthorizeSchema));
        staleSchema.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action rememberDrift = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize(serviceReauthorizeRememberEligible: true));
        rememberDrift.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action riskDrift = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize(serviceReauthorizeRisk: "low"));
        riskDrift.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action missingDescriptor = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(revision: SupportedRevision));
        missingDescriptor.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action looseKeyCreate = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize(keyCreateSchema: KeyCreateSchema));
        looseKeyCreate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public void ResolveServiceReauthorize_ShouldRequireExecutableRevisionAndExactParams()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize());
        var requirement = new NyxIdServiceReauthorizeActionRequirement
        {
            UserServiceId = "us-github-alpha",
            RequestedScopes = { "repo", "read:org" },
        };

        var validated = registry.ResolveServiceReauthorize(requirement);
        validated.Definition.Action.Should().Be(NyxIdAssistantActionKind.ServiceReauthorize);
        validated.Definition.RegistryRevision.Should().Be(SupportedRevision);
        validated.Params.ServiceReauthorize.UserServiceId.Should().Be("us-github-alpha");
        validated.Params.ServiceReauthorize.RequestedScopes.Should().Equal("repo", "read:org");

        foreach (var invalid in new[]
                 {
                     new NyxIdServiceReauthorizeActionRequirement
                     {
                         UserServiceId = "us-github-alpha",
                     },
                     new NyxIdServiceReauthorizeActionRequirement
                     {
                         UserServiceId = "us-github-alpha",
                         RequestedScopes = { "repo", "repo" },
                     },
                     new NyxIdServiceReauthorizeActionRequirement
                     {
                         UserServiceId = "us-github-alpha",
                         RequestedScopes = { " repo" },
                     },
                     new NyxIdServiceReauthorizeActionRequirement
                     {
                         UserServiceId = "us github",
                         RequestedScopes = { "repo" },
                     },
                     new NyxIdServiceReauthorizeActionRequirement
                     {
                         UserServiceId = "us/github",
                         RequestedScopes = { "repo" },
                     },
                     new NyxIdServiceReauthorizeActionRequirement
                     {
                         UserServiceId = "",
                         RequestedScopes = { "repo" },
                     },
                 })
        {
            Action resolve = () => registry.ResolveServiceReauthorize(invalid);
            resolve.Should().Throw<NyxIdAssistantActionRegistryException>()
                .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");
        }

        var keyRotationRegistry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation());
        Action failClosed = () => keyRotationRegistry.ResolveServiceReauthorize(requirement);
        failClosed.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");

        var waveOneRegistry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions());
        Action pinnedButNotExecutable = () => waveOneRegistry.ResolveServiceReauthorize(requirement);
        pinnedButNotExecutable.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void Load_ShouldRejectUnsupportedTierForKnownAction()
    {
        Action v2 = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(tier: "v2"));
        v2.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_TIER_UNSUPPORTED");
    }

    [Fact]
    public void ValidateRequest_ShouldKeepCatalogAndCustomServiceConnectDistinct()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        var catalog = registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github","requestedScopes":["repo"]}}""");
        var custom = registry.ValidateRequest(
            "service.connect",
            """{"customService":{"name":"Internal API","endpointUrl":"https://api.internal.example.com","authMethod":"bearer","authKeyName":"X-Api-Key"}}""");

        catalog.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect);
        catalog.Params.CatalogServiceConnect.ServiceSlug.Should().Be("api-github");
        catalog.Params.CatalogServiceConnect.RequestedScopes.Should().Equal("repo");
        custom.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect);
        custom.Params.CustomServiceConnect.EndpointUrl.Should().Be(
            "https://api.internal.example.com/");
        catalog.Definition.AdvisoryRisk.Should().Be(NyxIdAssistantActionRisk.Grant);
        catalog.Definition.RememberEligible.Should().BeTrue();
    }

    [Fact]
    public void ValidateRequest_ShouldRejectUndeclaredFieldsAndVariantAmbiguity()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        Action undeclared = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github","displayLabel":"GitHub"}}""");
        undeclared.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        Action ambiguous = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"},"customService":{"name":"Internal API","endpointUrl":"https://api.internal.example.com","authMethod":"none"}}""");
        ambiguous.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");
    }

    [Fact]
    public void ValidateRequest_ShouldRejectCallerOwnedRiskOrRememberPolicy()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        Action risk = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"}}""",
            callerRisk: "low");
        risk.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_POLICY_CALLER_OWNED");

        Action remember = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"}}""",
            callerRememberEligible: false);
        remember.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_POLICY_CALLER_OWNED");
    }

    [Fact]
    public void RegistrySnapshot_ShouldNotExposeHotReloadOrDeviceUserCodeAction()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        registry.TryGetDefinition("device.approve.user_code", out _).Should().BeFalse();
        registry.TryGetDefinition("device.approve", out _).Should().BeFalse();
        typeof(NyxIdAssistantActionRegistry).GetMethods()
            .Select(static method => method.Name)
            .Should().NotContain(name =>
                name.Contains("Reload", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Refresh", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("secret")]
    [InlineData("clientSecret")]
    [InlineData("password")]
    [InlineData("user_code")]
    [InlineData("deviceCode")]
    [InlineData("raw_upstream_body")]
    public void SecretPolicy_ShouldRejectForbiddenFieldNames(string fieldName)
    {
        Action act = () => NyxIdActionSecretPolicy.ValidateParamsJson(
            "{\"catalogService\":{\"serviceSlug\":\"api-github\",\"" +
            fieldName +
            "\":\"secret-alpha\"}}");

        act.Should().Throw<NyxIdActionSecretPolicyException>()
            .Which.Code.Should().Be("NYXID_ACTION_SECRET_FIELD_FORBIDDEN");
    }

    [Theory]
    [InlineData("https://user:password@example.com/path")]
    [InlineData("https://example.com/path?token=secret-alpha")]
    [InlineData("https://example.com/path#secret-alpha")]
    [InlineData("ftp://example.com/path")]
    [InlineData("/relative/path")]
    public void SecretPolicy_ShouldRejectUnsafeUrls(string value)
    {
        Action act = () => NyxIdActionSecretPolicy.NormalizeSafeUrl(value);

        act.Should().Throw<NyxIdActionSecretPolicyException>()
            .Which.Code.Should().Be("NYXID_ACTION_URL_UNSAFE");
    }

    [Fact]
    public void ValidateRequest_ShouldRejectManifestOnlyActionWithoutProducerAndPostconditionPolicy()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithManifestOnlyAction());

        Action act = () => registry.ValidateRequest(
            "developer_app.create",
            """{"name":"My App","redirectUris":["https://app.example.com/cb"]}""");

        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
        registry.TryGetDefinition("developer_app.create", out _).Should().BeFalse();
    }

    [Fact]
    public void ParseServiceReauthorize_ShouldRequireExactUserServiceIdentity()
    {
        using var valid = JsonDocument.Parse(
            """{"userServiceId":"us-github-alpha","requestedScopes":["repo","read:org"]}""");

        var parsed = NyxIdAssistantActionRegistry.ParseServiceReauthorize(valid.RootElement);

        parsed.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize);
        parsed.ServiceReauthorize.UserServiceId.Should().Be("us-github-alpha");
        parsed.ServiceReauthorize.RequestedScopes.Should().Equal("repo", "read:org");

        using var obsolete = JsonDocument.Parse(
            """{"keyId":"key-alpha","requestedScopes":["repo"]}""");
        Action parseObsolete = () =>
            NyxIdAssistantActionRegistry.ParseServiceReauthorize(obsolete.RootElement);
        parseObsolete.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");
    }

    [Theory]
    [InlineData("""{"userServiceId":"us-github-alpha","requestedScopes":[]}""")]
    [InlineData("""{"userServiceId":"us-github-alpha","requestedScopes":["repo","repo"]}""")]
    [InlineData("""{"userServiceId":"us-github-alpha","requestedScopes":[" repo"]}""")]
    [InlineData("""{"userServiceId":"us-github-alpha","requestedScopes":["repo",""]}""")]
    [InlineData("""{"userServiceId":" us-github-alpha","requestedScopes":["repo"]}""")]
    [InlineData("""{"userServiceId":"us github","requestedScopes":["repo"]}""")]
    [InlineData("""{"userServiceId":"us/github","requestedScopes":["repo"]}""")]
    [InlineData("""{"userServiceId":"us?github","requestedScopes":["repo"]}""")]
    [InlineData("""{"userServiceId":"","requestedScopes":["repo"]}""")]
    public void ValidateRequest_ShouldRejectLooseServiceReauthorizeParamsAtV8(string paramsJson)
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize());

        Action validate = () => registry.ValidateRequest("service.reauthorize", paramsJson);

        validate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");
    }

    [Fact]
    public void ParseKeyCreate_ShouldRequireAtLeastOneExactAllowedServiceIdentity()
    {
        using var valid = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex","allowedServiceIds":["us-github-alpha"]}""");

        var parsed = NyxIdAssistantActionRegistry.ParseKeyCreate(valid.RootElement);

        parsed.ParamsCase.Should().Be(NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate);
        parsed.KeyCreate.Name.Should().Be("agent-alpha");
        parsed.KeyCreate.Platform.Should().Be("codex");
        parsed.KeyCreate.AllowedServiceIds.Should().Equal("us-github-alpha");

        using var missingServices = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex"}""");
        Action parseMissingServices = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(missingServices.RootElement);
        parseMissingServices.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        using var allServices = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex","allowedServiceIds":[]}""");
        Action parseAllServices = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(allServices.RootElement);
        parseAllServices.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        using var duplicateServices = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex","allowedServiceIds":["us-github-alpha","us-github-alpha"]}""");
        Action parseDuplicates = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(duplicateServices.RootElement);
        parseDuplicates.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        using var nonCanonicalService = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex","allowedServiceIds":[" us-github-alpha"]}""");
        Action parseNonCanonical = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(nonCanonicalService.RootElement);
        parseNonCanonical.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        var overLimitJson = JsonSerializer.Serialize(new
        {
            name = "agent-alpha",
            platform = "codex",
            allowedServiceIds = Enumerable.Range(0, 65).Select(index => $"us-{index}"),
        });
        using var overLimitServices = JsonDocument.Parse(overLimitJson);
        Action parseOverLimit = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(overLimitServices.RootElement);
        parseOverLimit.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");
    }

    [Fact]
    public void ServiceReauthorize_ShouldRemainFailClosedAtExecutableGate()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions());

        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        Action validate = () => registry.ValidateRequest(
            "service.reauthorize",
            """{"userServiceId":"us-github-alpha","requestedScopes":["repo"]}""");
        validate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void StartupSnapshot_ShouldInitializeExactlyOnceAndFailBeforeInitialization()
    {
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();

        Action beforeStartup = () => snapshot.GetRequired();
        beforeStartup.Should().Throw<InvalidOperationException>()
            .WithMessage("*startup snapshot*");

        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());
        snapshot.Initialize(registry);

        snapshot.GetRequired().Should().BeSameAs(registry);
        Action replacement = () => snapshot.Initialize(
            NyxIdAssistantActionRegistry.Load(RegistryJson()));
        replacement.Should().Throw<InvalidOperationException>()
            .WithMessage("*already initialized*");
    }

    [Fact]
    public async Task StartupService_ShouldFetchAndValidateRegistryOnce()
    {
        foreach (var (payload, revision) in new[]
                 {
                     (RegistryJson(), LegacyRevision),
                     (RegistryJsonWithWaveOneActions(), TransitionRevision),
                     (RegistryJsonWithLeastScopeKeyCreate(), LeastScopeRevision),
                     (RegistryJsonWithKeyRotation(), KeyRotationRevision),
                     (RegistryJsonWithServiceReauthorize(), SupportedRevision),
                 })
        {
            var source = new RecordingRegistrySource(payload);
            var snapshot = new NyxIdAssistantActionRegistrySnapshot();
            var service = new NyxIdAssistantActionRegistryStartupService(
                source,
                snapshot,
                NullLogger<NyxIdAssistantActionRegistryStartupService>.Instance);

            await service.StartAsync(CancellationToken.None);

            source.FetchCount.Should().Be(1);
            snapshot.GetRequired().SchemaVersion.Should().Be(4);
            snapshot.GetRequired().RegistryRevision.Should().Be(revision);
            await service.StopAsync(CancellationToken.None);
            source.FetchCount.Should().Be(1);
        }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("read")]
    public async Task StartupService_ShouldDisableAssistantActionsAndScrubDependencyFailureDetails(
        string failureKind)
    {
        const string sensitiveDetail = "response-contained-secret-token";
        Exception failure = failureKind switch
        {
            "http" => new HttpRequestException(sensitiveDetail),
            "timeout" => new TaskCanceledException(sensitiveDetail),
            "read" => new IOException(sensitiveDetail),
            _ => throw new InvalidOperationException("Unsupported test failure kind."),
        };
        var source = new FailingRegistrySource(failure);
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var logger = new RecordingLogger<NyxIdAssistantActionRegistryStartupService>();
        var service = new NyxIdAssistantActionRegistryStartupService(source, snapshot, logger);

        await service.StartAsync(CancellationToken.None);

        var registry = snapshot.GetRequired();
        registry.TryGetDefinition("service.connect", out _).Should().BeFalse();
        Action validate = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"}}""");
        validate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
        logger.Entries.Should().ContainSingle();
        logger.Entries.Single().Level.Should().Be(LogLevel.Error);
        logger.Entries.Single().Message.Should().Contain(failure.GetType().Name);
        logger.Entries.Single().Message.Should().NotContain(sensitiveDetail);
        logger.Entries.Single().Exception.Should().BeNull();
    }

    [Fact]
    public async Task StartupService_ShouldDisableAssistantActionsWhenRegistryContractIsInvalid()
    {
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var service = new NyxIdAssistantActionRegistryStartupService(
            new RecordingRegistrySource("not-json"),
            snapshot,
            NullLogger<NyxIdAssistantActionRegistryStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        snapshot.GetRequired().TryGetDefinition("service.connect", out _).Should().BeFalse();
    }

    [Fact]
    public async Task StartupService_ShouldPropagateHostCancellationWithoutInitializingFallback()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var service = new NyxIdAssistantActionRegistryStartupService(
            new RecordingRegistrySource(RegistryJson()),
            snapshot,
            NullLogger<NyxIdAssistantActionRegistryStartupService>.Instance);

        Func<Task> start = () => service.StartAsync(cts.Token);

        await start.Should().ThrowAsync<OperationCanceledException>();
        Action read = () => snapshot.GetRequired();
        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*not initialized*");
    }

    [Fact]
    public async Task HttpSource_ShouldFetchCanonicalPublicRouteWithoutCredentials()
    {
        var handler = new RecordingHandler(RegistryJsonWithWaveOneActions());
        var client = new HttpClient(handler);
        var source = new NyxIdAssistantActionRegistryHttpSource(
            new StubHttpClientFactory(client),
            new NyxIdToolOptions
            {
                BaseUrl = "http://nyxid.internal:3001/",
                ApiBaseUrl = "https://nyxid.example.test/",
            });

        var json = await source.FetchAsync(CancellationToken.None);

        json.Should().Contain(TransitionRevision);
        handler.Requests.Should().ContainSingle();
        handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
        handler.Requests.Single().RequestUri.Should().Be(
            new Uri("https://nyxid.example.test/api/v1/assistant/actions"));
        handler.Requests.Single().Headers.Authorization.Should().BeNull();
    }

    private static string RegistryJson(
        int schemaVersion = 4,
        string revision = LegacyRevision,
        string action = "service.connect",
        string tier = "v1",
        string paramsSchema = ServiceConnectSchema,
        string risk = "grant",
        bool rememberEligible = true) => $$"""
        {
          "schema_version": {{schemaVersion}},
          "revision": "{{revision}}",
          "actions": [
            {
              "action": "{{action}}",
              "description": "Complete the browser-owned NyxID journey.",
              "params_schema": {{paramsSchema}},
              "risk": "{{risk}}",
              "tier": "{{tier}}",
              "remember_eligible": {{rememberEligible.ToString().ToLowerInvariant()}}
            }
          ]
        }
        """;

    private const string ServiceConnectSchema = """
        {
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["catalogService"],
              "properties": {
                "catalogService": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["serviceSlug"],
                  "properties": {
                    "serviceSlug": {"type": "string"},
                    "requestedScopes": {"type": "array", "items": {"type": "string"}},
                    "viaNodeId": {"type": "string"},
                    "targetOrgId": {"type": "string"}
                  }
                }
              }
            },
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["customService"],
              "properties": {
                "customService": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["name", "endpointUrl", "authMethod"],
                  "properties": {
                    "name": {"type": "string"},
                    "endpointUrl": {"type": "string"},
                    "authMethod": {"type": "string"},
                    "authKeyName": {"type": "string"},
                    "viaNodeId": {"type": "string"},
                    "targetOrgId": {"type": "string"}
                  }
                }
              }
            }
          ]
        }
        """;

    private const string DeveloperAppSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "redirectUris"],
          "properties": {
            "name": {"type": "string"},
            "redirectUris": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string ServiceReauthorizeSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["userServiceId", "requestedScopes"],
          "properties": {
            "userServiceId": {"type": "string"},
            "requestedScopes": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string StaleServiceReauthorizeSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["userServiceId"],
          "properties": {
            "userServiceId": {"type": "string"},
            "requestedScopes": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string KeyCreateSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "platform", "allowedServiceIds"],
          "properties": {
            "name": {"type": "string"},
            "platform": {"type": "string"},
            "allowedServiceIds": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string LeastScopeKeyCreateSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "platform", "allowedServiceIds"],
          "properties": {
            "name": {"type": "string"},
            "platform": {"type": "string"},
            "allowedServiceIds": {
              "type": "array",
              "minItems": 1,
              "maxItems": 64,
              "uniqueItems": true,
              "items": {"type": "string"}
            }
          }
        }
        """;

    private const string RelaxedKeyCreateSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "platform"],
          "properties": {
            "name": {"type": "string"},
            "platform": {"type": "string"},
            "allowedServiceIds": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string KeyRotateSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["keyId"],
          "properties": {
            "keyId": {"type": "string"}
          }
        }
        """;

    private static string RegistryJsonWithWaveOneActions(
        string serviceReauthorizeSchema = ServiceReauthorizeSchema,
        string keyCreateSchema = KeyCreateSchema,
        bool keyCreateRememberEligible = false,
        bool serviceReauthorizeRememberEligible = false,
        string revision = TransitionRevision) => $$"""
        {
          "schema_version": 4,
          "revision": "{{revision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "service.reauthorize",
              "description": "Reauthorize a connected service.",
              "params_schema": {{serviceReauthorizeSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": {{serviceReauthorizeRememberEligible.ToString().ToLowerInvariant()}}
            },
            {
              "action": "key.create",
              "description": "Create a scoped API key.",
              "params_schema": {{keyCreateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": {{keyCreateRememberEligible.ToString().ToLowerInvariant()}}
            },
            {
              "action": "key.rotate",
              "description": "Rotate an API key.",
              "params_schema": {{KeyRotateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;

    private static string RegistryJsonWithLeastScopeKeyCreate(
        string keyCreateSchema = LeastScopeKeyCreateSchema) => $$"""
        {
          "schema_version": 4,
          "revision": "{{LeastScopeRevision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "key.create",
              "description": "Create a least-scope API key.",
              "params_schema": {{keyCreateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;

    private static string RegistryJsonWithKeyRotation(
        string revision = KeyRotationRevision) => $$"""
        {
          "schema_version": 4,
          "revision": "{{revision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "key.create",
              "description": "Create a least-scope API key.",
              "params_schema": {{LeastScopeKeyCreateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            },
            {
              "action": "key.rotate",
              "description": "Rotate an API key.",
              "params_schema": {{KeyRotateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;

    private static string RegistryJsonWithServiceReauthorize(
        string serviceReauthorizeSchema = ServiceReauthorizeSchema,
        string keyCreateSchema = LeastScopeKeyCreateSchema,
        bool serviceReauthorizeRememberEligible = false,
        string serviceReauthorizeRisk = "grant") => $$"""
        {
          "schema_version": 4,
          "revision": "{{SupportedRevision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "key.create",
              "description": "Create a least-scope API key.",
              "params_schema": {{keyCreateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            },
            {
              "action": "key.rotate",
              "description": "Rotate an API key.",
              "params_schema": {{KeyRotateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            },
            {
              "action": "service.reauthorize",
              "description": "Reauthorize a connected service.",
              "params_schema": {{serviceReauthorizeSchema}},
              "risk": "{{serviceReauthorizeRisk}}",
              "tier": "v1",
              "remember_eligible": {{serviceReauthorizeRememberEligible.ToString().ToLowerInvariant()}}
            }
          ]
        }
        """;

    private static string RegistryJsonWithManifestOnlyAction() => $$"""
        {
          "schema_version": 4,
          "revision": "{{LegacyRevision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "developer_app.create",
              "description": "Create a developer app.",
              "params_schema": {{DeveloperAppSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;

    private static string RegistryJsonWithUnknownAction() => $$"""
        {
          "schema_version": 4,
          "revision": "{{LegacyRevision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "workflow.launch",
              "description": "Launch a workflow.",
              "params_schema": {"type": "object"},
              "risk": "execute",
              "tier": "v2",
              "remember_eligible": false
            }
          ]
        }
        """;

    private sealed class RecordingRegistrySource(string json)
        : INyxIdAssistantActionRegistrySource
    {
        public int FetchCount { get; private set; }

        public Task<string> FetchAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FetchCount++;
            return Task.FromResult(json);
        }
    }

    private sealed class FailingRegistrySource(Exception exception)
        : INyxIdAssistantActionRegistrySource
    {
        public Task<string> FetchAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromException<string>(exception);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            });
        }
    }
}
