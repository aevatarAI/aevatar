using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

[Collection(ChannelRuntimeTestCollections.NyxIdInventoryRequestContext)]
public sealed class ChannelSenderRegistrationReadToolSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterializeAndExecute_WithRegistrationAgentKey_ReadsOnlyTheBoundSendersRegistrations(bool omittedFromMcp)
    {
        var fixture = new Fixture();
        fixture.Handler.OmitMcpService = omittedFromMcp;
        fixture.Handler.IncludeUnrelatedService = true;
        var registry = Substitute.For<IToolSetRegistry>();
        registry.Resolve(ToolSetNames.ChannelReplyDefault).Returns(ToolSetResolveResult.Success(
            ToolSetNames.ChannelReplyDefault, [fixture.Source]));
        var config = new ChannelRuntimeConfigProof { RegistrationId = "platform-registration" };
        config.ToolSetRefs.Add(ToolSetNames.ChannelReplyDefault);
        var catalog = await new ChannelRuntimeToolCatalogMaterializer(registry)
            .MaterializeAsync(config, [], fixture.Context);
        var tool = catalog.ExactTools.Values.Should().ContainSingle().Subject;

        var outcome = await fixture.Executor.ExecuteAsync(new AgentToolExecutionRequest(
            tool, "{}", fixture.Context, AgentToolApprovalContinuationMode.None, ApprovalGrant: null));

        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success, outcome.ResultJson);
        outcome.ResultJson.Should().Contain("sender-registration");
        tool.Presentation.NyxIdOperation.ConnectedServiceId.Should().Be("sender-aevatar-service");
        fixture.Handler.Requests.Should().OnlyContain(request => request.Bearer == "sender-token");
        fixture.Handler.Requests.Should().NotContain(request => request.HasApiKey);
        fixture.Handler.Requests.Should().NotContain(request => request.Path.Contains("/unrelated-route/", StringComparison.Ordinal));
        fixture.Handler.Requests.Where(request => request.Path.Contains("/proxy/", StringComparison.Ordinal))
            .Should().OnlyContain(request => request.Query == "?_nyxid_via=sender-aevatar-service");
        var proxy = fixture.Handler.Requests.Should().ContainSingle(request => request.Path.EndsWith("/api/channels/registrations", StringComparison.Ordinal)).Subject;
        proxy.Path.Should().EndWith("/api/channels/registrations");
        proxy.Query.Should().Be("?_nyxid_via=sender-aevatar-service");
        proxy.Context.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        proxy.Context.CredentialSource.Should().Be(AgentToolCredentialSource.BearerToken);
        proxy.Context.DurableNyxIdCredential.Should().BeNull();
        proxy.Context.OperationAdmission!.ServiceInstanceId.Should().Be("sender-aevatar-service");
        fixture.Context.Credentials.NyxIdAccessToken.Should().Be("platform-agent-key");
        fixture.Audits.Where(record => record.LifecyclePhase == AuditLifecyclePhase.Terminal)
            .Should().HaveCount(2).And.OnlyContain(record => record.Outcome == AuditOutcome.Success);
        fixture.Issuer.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Discover_WithoutBinding_DoesNotReadAnyAccount()
    {
        var fixture = new Fixture();
        using var scope = AgentToolContextScope.Push(fixture.Context with { SenderBinding = AgentToolSenderBindingContext.Empty });

        (await fixture.Source.DiscoverToolsAsync()).Should().BeEmpty();

        fixture.Issuer.Calls.Should().Be(0);
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"scope\":\"all\"}")]
    [InlineData("{\"user_service_id\":\"platform-service\"}")]
    [InlineData("{\"headers\":{\"X-NyxID-User-ID\":\"other-account\"}}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task Execute_WithAccountOrRouteOverrides_RejectsBeforeReading(string arguments)
    {
        var fixture = new Fixture();
        using var scope = AgentToolContextScope.Push(fixture.Context);
        var tool = (await fixture.Source.DiscoverToolsAsync()).Should().ContainSingle().Subject;
        var requestCount = fixture.Handler.Requests.Count;

        var outcome = await tool.ExecuteWithOutcomeAsync("call", tool.Name, arguments);

        outcome.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ErrorCode.Should().Be("CHANNEL_SENDER_REGISTRATIONS_INVALID_ARGUMENTS");
        fixture.Handler.Requests.Should().HaveCount(requestCount);
        fixture.Issuer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Execute_AfterSenderBindingChanges_RejectsThePreviousTool()
    {
        var fixture = new Fixture();
        IAgentTool tool;
        using (AgentToolContextScope.Push(fixture.Context))
            tool = (await fixture.Source.DiscoverToolsAsync()).Single();
        using var scope = AgentToolContextScope.Push(fixture.Context with
        {
            SenderBinding = new AgentToolSenderBindingContext("other-binding", "other-user", "tenant"),
        });

        var result = await tool.ExecuteWithOutcomeAsync("call", tool.Name, "{}");

        result.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Denied);
        fixture.Issuer.Calls.Should().Be(1);
        fixture.Handler.Requests.Should().NotContain(request => request.Path.Contains("/proxy/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_AfterBindingRevocation_DoesNotReuseDiscoveryTokenOrPlatformKey()
    {
        var fixture = new Fixture();
        using var scope = AgentToolContextScope.Push(fixture.Context);
        var tool = (await fixture.Source.DiscoverToolsAsync()).Single();
        fixture.Issuer.Fail = true;

        var outcome = await tool.ExecuteWithOutcomeAsync("call", tool.Name, "{}");

        outcome.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.ResultJson.Should().NotContain("private-failure-detail");
        fixture.Issuer.Calls.Should().Be(2);
        fixture.Handler.Requests.Should().NotContain(request => request.Path.Contains("/proxy/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("POST", "/api/channels/registrations", "aevatar")]
    [InlineData("GET", "/api/channels/registrations/other/status", "aevatar")]
    [InlineData("GET", "/api/channels/registrations", "other-service")]
    public async Task Discover_DoesNotExposeOtherCapabilities(string method, string path, string catalogSlug)
    {
        var fixture = new Fixture();
        fixture.Handler.Method = method;
        fixture.Handler.OperationPath = path;
        fixture.Handler.CatalogSlug = catalogSlug;
        using var scope = AgentToolContextScope.Push(fixture.Context);

        (await fixture.Source.DiscoverToolsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_AfterExactServiceOrOperationChanges_RejectsBeforeProxy(bool changeOperation)
    {
        var fixture = new Fixture();
        using var scope = AgentToolContextScope.Push(fixture.Context);
        var tool = (await fixture.Source.DiscoverToolsAsync()).Single();
        if (changeOperation)
            fixture.Handler.EndpointId = "replacement-operation";
        else
            fixture.Handler.HasService = false;

        var outcome = await tool.ExecuteWithOutcomeAsync("call", tool.Name, "{}");

        outcome.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Denied);
        outcome.Receipt.ErrorCode.Should().Be("CHANNEL_SENDER_REGISTRATIONS_OPERATION_CHANGED");
        fixture.Handler.Requests.Should().NotContain(request => request.Path.Contains("/proxy/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_WhenInnerReadAuditFails_DoesNotReportOrdinarySuccess()
    {
        var fixture = new Fixture { FailInnerAudit = true };
        using var scope = AgentToolContextScope.Push(fixture.Context);
        var tool = (await fixture.Source.DiscoverToolsAsync()).Single();

        var outcome = await fixture.Executor.ExecuteAsync(new AgentToolExecutionRequest(
            tool, "{}", fixture.Context, AgentToolApprovalContinuationMode.None, ApprovalGrant: null));

        fixture.Handler.Requests.Should().ContainSingle(request => request.Path.EndsWith("/api/channels/registrations", StringComparison.Ordinal));
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ErrorCode.Should().Be("CHANNEL_SENDER_REGISTRATIONS_AUDIT_INCOMPLETE");
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            var ledger = Substitute.For<IAgentToolAdmissionLedger>();
            ledger.TryStartAsync(Arg.Any<AgentToolAdmissionFact>(), Arg.Any<CancellationToken>())
                .Returns(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
            var audit = Substitute.For<IAuditTrailAppender>();
            audit.AppendAsync(Arg.Any<AuditRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var record = call.Arg<AuditRecord>();
                Audits.Add(record);
                if (FailInnerAudit && record.LifecyclePhase == AuditLifecyclePhase.Terminal &&
                    !record.OperationName.StartsWith("sender_", StringComparison.Ordinal))
                {
                    return AuditTrailAppendResult.StoreUnavailable(record.AuditId, "test-audit-unavailable");
                }
                return AuditTrailAppendResult.Appended(record.AuditId);
            });
            var hasher = Substitute.For<IAuditActorIdentityHasher>();
            hasher.Hash(Arg.Any<string>()).Returns(new AuditActorIdentity("actor-hash", "key-1"));
            Executor = new AdmittedAgentToolExecutor(ledger, audit, hasher,
                channelRegistrationAuthorityAdmissionPort: Substitute.For<IChannelRegistrationAuthorityAdmissionPort>());
            var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
            Source = new ChannelSenderRegistrationReadToolSource(Executor, options,
                new ClientFactory(new NyxIdApiClient(options, new HttpClient(Handler))), Issuer);
        }

        public RecordingHandler Handler { get; } = new();
        public ReadIssuer Issuer { get; } = new();
        public List<AuditRecord> Audits { get; } = [];
        public bool FailInnerAudit { get; set; }
        public AdmittedAgentToolExecutor Executor { get; }
        public ChannelSenderRegistrationReadToolSource Source { get; }
        public AgentToolExecutionContext Context { get; } = NewContext();

        private static AgentToolExecutionContext NewContext()
        {
            var secret = new SecretReference
            {
                Ref = "vault://platform/key", Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
                Fingerprint = "fp-platform", Version = 1, OwnerScopeKey = "platform-scope", CreatedAtUnixMs = 1,
            };
            return AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request", "outer-call"),
                Credentials = new AgentToolCredentials("platform-agent-key", null, null, AgentToolNyxIdCredentialKind.AgentKey),
                CredentialSource = AgentToolCredentialSource.ChannelRegistration,
                DurableNyxIdCredential = new DurableCallerCredentialRef
                {
                    Ref = secret.Ref, Purpose = secret.Purpose, OwnerScopeKey = secret.OwnerScopeKey,
                    SubjectId = "platform-key", SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
                    SecretReference = secret,
                },
                Caller = new AgentToolCallerContext("platform-scope", "platform-owner", "response"),
                Channel = new AgentToolChannelContext("telegram", "external-sender", "platform-scope", "message", null,
                    BotRegistrationId: "platform-registration"),
                SenderBinding = new AgentToolSenderBindingContext("sender-binding", "nyx-sender", "tenant"),
                NyxIdAuthority = new AgentToolNyxIdAuthorityContext("telegram", "tenant", "external-sender"),
                ExecutionOwner = AgentToolExecutionOwners.ChannelRegistration("platform-registration"),
            };
        }
    }

    private sealed class ReadIssuer : INyxIdChannelRegistrationReadCapabilityIssuer
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<CapabilityHandle> IssueByBindingIdAsync(ExternalSubjectRef subject, string bindingId, CancellationToken ct = default)
        {
            Calls++;
            subject.ExternalUserId.Should().Be("external-sender");
            bindingId.Should().Be("sender-binding");
            if (Fail)
                throw new InvalidOperationException("private-failure-detail");
            return Task.FromResult(new CapabilityHandle { AccessToken = "sender-token", Scope = "proxy" });
        }
    }

    private sealed class ClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool HasService { get; set; } = true;
        public bool OmitMcpService { get; set; }
        public bool IncludeUnrelatedService { get; set; }
        public string CatalogSlug { get; set; } = "aevatar";
        public string Method { get; set; } = "GET";
        public string OperationPath { get; set; } = "/api/channels/registrations";
        public string EndpointId { get; set; } = "list-own-registrations";
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new RequestRecord(path, request.RequestUri.Query, request.Headers.Authorization?.Parameter,
                request.Headers.Contains("X-API-Key"), AgentToolRequestContext.Current!));
            var body = path switch
            {
                "/api/v1/keys" => JsonSerializer.Serialize(new
                {
                    keys = InventoryKeys(),
                }),
                "/api/v1/mcp/config" when OmitMcpService =>
                    """{"contract_version":"1.0","catalog_digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","user_id":"nyx-sender","services":[]}""",
                "/api/v1/mcp/config" => JsonSerializer.Serialize(new
                {
                    contract_version = "1.0", catalog_digest = "sha256:" + new string('a', 64), user_id = "nyx-sender",
                    services = new[] { new
                    {
                        service_id = "sender-aevatar-service", service_name = "Aevatar", service_slug = "sender-aevatar-route",
                        is_user_service = true, is_generic_proxy = false,
                        endpoints = new[] { new
                        {
                            endpoint_id = EndpointId, name = "list_registrations", method = Method, path = OperationPath,
                            parameters = new[] { new { name = "scope", @in = "query", required = false, schema = new { type = "string" } } },
                            request_body_schema = (object?)null, request_content_type = (string?)null, request_body_required = false,
                            response = new { content_types = new[] { "application/json" }, binary_artifact = false },
                        } },
                    } },
                }),
                "/api/v1/proxy/s/sender-aevatar-route/api/channels/registrations" =>
                    """[{"id":"sender-registration","platform":"telegram","scope_id":"nyx-sender","owned":true,"state_version":7}]""",
                "/api/v1/proxy/s/sender-aevatar-route/api/openapi.json" => """
                    {"openapi":"3.0.1","info":{"title":"Aevatar","version":"1"},"paths":{
                      "/api/channels/registrations":{"get":{"operationId":"list_registrations",
                        "parameters":[{"name":"scope","in":"query","required":false,"schema":{"type":"string"}}],
                        "responses":{"200":{"description":"Own Channel registrations","content":{"application/json":{"schema":{"type":"array","items":{"type":"object"}}}}}}}}
                    }}
                    """,
                _ => throw new InvalidOperationException($"Unexpected request {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private object[] InventoryKeys()
        {
            if (!HasService)
                return [];
            var aevatar = new
            {
                id = "sender-aevatar-service", slug = "sender-aevatar-route", catalog_service_id = "aevatar-catalog",
                catalog_service_slug = CatalogSlug, label = "My Aevatar", is_active = true, connected = true,
                status = "active", credential_source = new { type = "personal" },
                endpoint_url = "https://aevatar.test", openapi_spec_url = "https://aevatar.test/api/openapi.json",
            };
            if (!IncludeUnrelatedService)
                return [aevatar];
            return [aevatar, new
            {
                id = "unrelated-service", slug = "unrelated-route", catalog_service_id = "unrelated-catalog",
                catalog_service_slug = "unrelated", is_active = true, connected = true, status = "active",
                credential_source = new { type = "personal" }, endpoint_url = "https://unrelated.test",
                openapi_spec_url = "https://unrelated.test/api/openapi.json",
            }];
        }
    }

    private sealed record RequestRecord(string Path, string Query, string? Bearer, bool HasApiKey, AgentToolExecutionContext Context);
}
