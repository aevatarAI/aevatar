using System.Net;
using System.Text.Json;
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
    public void Load_ShouldPinSchemaVersionAndPassRevisionThrough()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize());

        registry.SchemaVersion.Should().Be(4);
        registry.RegistryRevision.Should().Be(SupportedRevision);

        var future = NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: "nyxid-assistant-actions.future"));
        future.RegistryRevision.Should().Be("nyxid-assistant-actions.future");
        future.TryGetDefinition("service.connect", out _).Should().BeTrue();

        var unlabeled = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithoutRevision());
        unlabeled.RegistryRevision.Should().BeEmpty();
        unlabeled.TryGetDefinition("service.connect", out _).Should().BeTrue();

        Action act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(schemaVersion: 3));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_SCHEMA_UNSUPPORTED");
    }

    [Fact]
    public void Load_ShouldIgnoreUnknownActionWhenExecutableActionsArePresent()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithUnknownAction());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("workflow.launch", out _).Should().BeFalse();
        registry.SkippedActions.Should().BeEmpty();
    }

    [Fact]
    public void Load_ShouldKeepExecutableActionsWhenServedListIsPartial()
    {
        var connectOnly = NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: SupportedRevision));

        connectOnly.TryGetDefinition("service.connect", out _).Should().BeTrue();
        connectOnly.TryGetDefinition("key.create", out _).Should().BeFalse();
        connectOnly.TryGetDefinition("key.rotate", out _).Should().BeFalse();
        connectOnly.SkippedActions.Should().BeEmpty();
    }

    [Fact]
    public void Load_ShouldDegradeDivergentDescriptorsPerActionAndKeepTheRest()
    {
        var staleReauthorize = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize(
                serviceReauthorizeSchema: StaleServiceReauthorizeSchema));
        staleReauthorize.TryGetDefinition("service.connect", out _).Should().BeTrue();
        staleReauthorize.TryGetDefinition("key.create", out _).Should().BeTrue();
        staleReauthorize.TryGetDefinition("key.rotate", out _).Should().BeTrue();
        staleReauthorize.SkippedActions.Should().ContainSingle(skip =>
            skip.WireAction == "service.reauthorize" &&
            skip.Code == "NYXID_ACTION_REGISTRY_INVALID");

        var relaxedKeyCreate = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize(keyCreateSchema: KeyCreateSchema));
        relaxedKeyCreate.TryGetDefinition("key.create", out _).Should().BeFalse();
        relaxedKeyCreate.TryGetDefinition("service.connect", out _).Should().BeTrue();
        relaxedKeyCreate.TryGetDefinition("key.rotate", out _).Should().BeTrue();
        relaxedKeyCreate.SkippedActions.Should().ContainSingle(skip =>
            skip.WireAction == "key.create" &&
            skip.Code == "NYXID_ACTION_REGISTRY_INVALID");

        var rememberedReauthorization = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithServiceReauthorize(serviceReauthorizeRememberEligible: true));
        rememberedReauthorization.TryGetDefinition("service.connect", out _).Should().BeTrue();
        rememberedReauthorization.SkippedActions.Should().ContainSingle(skip =>
            skip.WireAction == "service.reauthorize");

        var unsupportedTier = NyxIdAssistantActionRegistry.Load(
            RegistryJson(tier: "v2"));
        unsupportedTier.TryGetDefinition("service.connect", out _).Should().BeFalse();
        unsupportedTier.SkippedActions.Should().ContainSingle(skip =>
            skip.WireAction == "service.connect" &&
            skip.Code == "NYXID_ACTION_TIER_UNSUPPORTED");
    }

    [Fact]
    public void Load_ShouldKeepUnimplementedActionsClosedRegardlessOfRevision()
    {
        foreach (var payload in new[]
                 {
                     RegistryJsonWithWaveOneActions(),
                     RegistryJsonWithWaveOneActions(revision: LegacyRevision),
                     RegistryJsonWithServiceReauthorize(),
                 })
        {
            var registry = NyxIdAssistantActionRegistry.Load(payload);
            registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
            registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
            Action validate = () => registry.ValidateRequest(
                "service.reauthorize",
                """{"userServiceId":"us-github-alpha","requestedScopes":["repo"]}""");
            validate.Should().Throw<NyxIdAssistantActionRegistryException>()
                .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
        }
    }

    [Fact]
    public void Load_ShouldExposeExecutableActionsRegardlessOfRevisionLabel()
    {
        var keyRotation = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation());
        keyRotation.TryGetDefinition("service.connect", out _).Should().BeTrue();
        keyRotation.TryGetDefinition("key.create", out _).Should().BeTrue();
        keyRotation.TryGetDefinition("key.rotate", out _).Should().BeTrue();

        var futureRevision = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(revision: "nyxid-assistant-actions.v99"));
        futureRevision.TryGetDefinition("key.rotate", out _).Should().BeTrue();

        var staleKeyCreate = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithLeastScopeKeyCreate(KeyCreateSchema));
        staleKeyCreate.TryGetDefinition("key.create", out _).Should().BeFalse();
        staleKeyCreate.TryGetDefinition("service.connect", out _).Should().BeTrue();
        staleKeyCreate.SkippedActions.Should().ContainSingle(skip =>
            skip.WireAction == "key.create");
    }

    [Fact]
    public void IsActionExecutable_ShouldNotDependOnNyxIdRevisionLabel()
    {
        foreach (var revision in new[]
                 {
                     LegacyRevision,
                     SupportedRevision,
                     "nyxid-assistant-actions.v99",
                     string.Empty,
                 })
        {
            NyxIdAssistantActionRegistry.IsActionExecutable(
                    revision, NyxIdAssistantActionKind.ServiceConnect)
                .Should().BeTrue();
            NyxIdAssistantActionRegistry.IsActionExecutable(
                    revision, NyxIdAssistantActionKind.KeyCreate)
                .Should().BeTrue();
            NyxIdAssistantActionRegistry.IsActionExecutable(
                    revision, NyxIdAssistantActionKind.KeyRotate)
                .Should().BeTrue();
            NyxIdAssistantActionRegistry.IsActionExecutable(
                    revision, NyxIdAssistantActionKind.ServiceReauthorize)
                .Should().BeFalse();
            NyxIdAssistantActionRegistry.IsActionExecutable(
                    revision, NyxIdAssistantActionKind.ServiceAccessReview)
                .Should().BeFalse();
        }

        NyxIdAssistantActionRegistry.IsActionExecutable(
                NyxIdAssistantActionRegistry.ServiceAccessReviewRegistryRevision,
                NyxIdAssistantActionKind.ServiceAccessReview)
            .Should().BeTrue();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                NyxIdAssistantActionRegistry.ServiceAccessReviewRegistryRevision,
                NyxIdAssistantActionKind.ServiceConnect)
            .Should().BeFalse();
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
    public void StartupSnapshot_ShouldUpgradeOnlyTheStartupFallbackRegistry()
    {
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        snapshot.Initialize(NyxIdAssistantActionRegistry.CreateDisabled());

        var served = NyxIdAssistantActionRegistry.Load(RegistryJson());
        snapshot.TryUpgrade(served).Should().BeTrue();
        snapshot.GetRequired().Should().BeSameAs(served);

        snapshot.TryUpgrade(NyxIdAssistantActionRegistry.Load(RegistryJson()))
            .Should().BeFalse();
        snapshot.GetRequired().Should().BeSameAs(served);

        Action fallbackUpgrade = () => snapshot.TryUpgrade(
            NyxIdAssistantActionRegistry.CreateDisabled());
        fallbackUpgrade.Should().Throw<InvalidOperationException>()
            .WithMessage("*fallback*");
    }

    [Fact]
    public async Task StartupService_ShouldFetchAndValidateRegistryOnce()
    {
        foreach (var (payload, revision) in new[]
                 {
                     (RegistryJson(), LegacyRevision),
                     (RegistryJsonWithLeastScopeKeyCreate(), LeastScopeRevision),
                     (RegistryJsonWithKeyRotation(), KeyRotationRevision),
                     (RegistryJsonWithServiceReauthorize(), SupportedRevision),
                 })
        {
            var source = new RecordingRegistrySource(payload);
            var snapshot = new NyxIdAssistantActionRegistrySnapshot();
            using var service = CreateStartupService(source, snapshot);

            await service.StartAsync(CancellationToken.None);

            source.FetchCount.Should().Be(1);
            snapshot.GetRequired().SchemaVersion.Should().Be(4);
            snapshot.GetRequired().RegistryRevision.Should().Be(revision);
            await service.StopAsync(CancellationToken.None);
            source.FetchCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task StartupService_ShouldRetryTransientStartupFailuresBeforeSucceeding()
    {
        var source = new FlakyRegistrySource(RegistryJson(), failuresBeforeSuccess: 2);
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        using var service = CreateStartupService(source, snapshot);

        await service.StartAsync(CancellationToken.None);

        source.FetchCount.Should().Be(3);
        snapshot.GetRequired().TryGetDefinition("service.connect", out _).Should().BeTrue();
        await service.StopAsync(CancellationToken.None);
        source.FetchCount.Should().Be(3);
    }

    [Fact]
    public async Task StartupService_ShouldRecoverServedRegistryAfterStartupFailure()
    {
        var source = new FlakyRegistrySource(RegistryJson(), failuresBeforeSuccess: 4);
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        using var service = CreateStartupService(source, snapshot);

        await service.StartAsync(CancellationToken.None);

        snapshot.GetRequired().TryGetDefinition("service.connect", out _).Should().BeFalse();

        await service.RecoveryCompletion;

        source.FetchCount.Should().Be(5);
        snapshot.GetRequired().TryGetDefinition("service.connect", out _).Should().BeTrue();
        await service.StopAsync(CancellationToken.None);
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
        using var service = CreateStartupService(
            source,
            snapshot,
            logger,
            recoveryRetryDelay: Timeout.InfiniteTimeSpan);

        await service.StartAsync(CancellationToken.None);

        var registry = snapshot.GetRequired();
        registry.TryGetDefinition("service.connect", out _).Should().BeFalse();
        Action validate = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"}}""");
        validate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
        logger.Entries.Should().HaveCount(
            NyxIdAssistantActionRegistryStartupService.StartupFetchAttempts + 1);
        logger.Entries.Take(NyxIdAssistantActionRegistryStartupService.StartupFetchAttempts)
            .Should().OnlyContain(entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains(failure.GetType().Name));
        logger.Entries.Last().Level.Should().Be(LogLevel.Error);
        logger.Entries.Should().OnlyContain(entry =>
            !entry.Message.Contains(sensitiveDetail) && entry.Exception == null);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupService_ShouldDisableAssistantActionsWhenRegistryContractIsInvalid()
    {
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        using var service = CreateStartupService(
            new RecordingRegistrySource("not-json"),
            snapshot,
            recoveryRetryDelay: Timeout.InfiniteTimeSpan);

        await service.StartAsync(CancellationToken.None);

        snapshot.GetRequired().TryGetDefinition("service.connect", out _).Should().BeFalse();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupService_ShouldPropagateHostCancellationWithoutInitializingFallback()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        using var service = CreateStartupService(
            new RecordingRegistrySource(RegistryJson()),
            snapshot);

        Func<Task> start = () => service.StartAsync(cts.Token);

        await start.Should().ThrowAsync<OperationCanceledException>();
        Action read = () => snapshot.GetRequired();
        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*not initialized*");
    }

    private static NyxIdAssistantActionRegistryStartupService CreateStartupService(
        INyxIdAssistantActionRegistrySource source,
        NyxIdAssistantActionRegistrySnapshot snapshot,
        ILogger<NyxIdAssistantActionRegistryStartupService>? logger = null,
        TimeSpan? recoveryRetryDelay = null) =>
        new(
            source,
            snapshot,
            logger ?? NullLogger<NyxIdAssistantActionRegistryStartupService>.Instance,
            startupRetryDelay: TimeSpan.Zero,
            recoveryRetryDelay: recoveryRetryDelay ?? TimeSpan.Zero);

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
        bool serviceReauthorizeRememberEligible = false) => $$"""
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
              "action": "service.reauthorize",
              "description": "Reauthorize a connected service.",
              "params_schema": {{serviceReauthorizeSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": {{serviceReauthorizeRememberEligible.ToString().ToLowerInvariant()}}
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

    private static string RegistryJsonWithoutRevision() => $$"""
        {
          "schema_version": 4,
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
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

    private sealed class FlakyRegistrySource(string json, int failuresBeforeSuccess)
        : INyxIdAssistantActionRegistrySource
    {
        public int FetchCount { get; private set; }

        public Task<string> FetchAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FetchCount++;
            return FetchCount <= failuresBeforeSuccess
                ? Task.FromException<string>(new HttpRequestException("transient-startup-failure"))
                : Task.FromResult(json);
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
