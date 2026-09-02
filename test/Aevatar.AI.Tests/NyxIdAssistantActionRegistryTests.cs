using System.Net;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdAssistantActionRegistryTests
{
    private const string SupportedRevision = "nyxid-assistant-actions.v4";

    [Fact]
    public void Load_ShouldPinSchemaVersionAndRevision()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        registry.SchemaVersion.Should().Be(4);
        registry.RegistryRevision.Should().Be(SupportedRevision);

        Action act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(schemaVersion: 3));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_SCHEMA_UNSUPPORTED");

        act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: "nyxid-assistant-actions.future"));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_REVISION_UNSUPPORTED");
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
        var source = new RecordingRegistrySource(RegistryJson());
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var service = new NyxIdAssistantActionRegistryStartupService(source, snapshot);

        await service.StartAsync(CancellationToken.None);

        source.FetchCount.Should().Be(1);
        snapshot.GetRequired().SchemaVersion.Should().Be(4);
        snapshot.GetRequired().RegistryRevision.Should().Be(SupportedRevision);
        await service.StopAsync(CancellationToken.None);
        source.FetchCount.Should().Be(1);
    }

    [Fact]
    public async Task HttpSource_ShouldFetchCanonicalPublicRouteWithoutCredentials()
    {
        var handler = new RecordingHandler(RegistryJson());
        var client = new HttpClient(handler);
        var source = new NyxIdAssistantActionRegistryHttpSource(
            new StubHttpClientFactory(client),
            new NyxIdToolOptions { BaseUrl = "https://nyxid.example.test/" });

        var json = await source.FetchAsync(CancellationToken.None);

        json.Should().Contain(SupportedRevision);
        handler.Requests.Should().ContainSingle();
        handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
        handler.Requests.Single().RequestUri.Should().Be(
            new Uri("https://nyxid.example.test/api/v1/assistant/actions"));
        handler.Requests.Single().Headers.Authorization.Should().BeNull();
    }

    private static string RegistryJson(
        int schemaVersion = 4,
        string revision = SupportedRevision,
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

    private static string RegistryJsonWithManifestOnlyAction() => $$"""
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
