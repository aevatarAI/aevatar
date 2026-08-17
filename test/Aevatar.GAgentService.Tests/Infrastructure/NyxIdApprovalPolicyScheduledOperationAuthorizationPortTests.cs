using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Infrastructure.Credentials;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class NyxIdApprovalPolicyScheduledOperationAuthorizationPortTests
{
    [Fact]
    public async Task EvaluateAsync_WhenExactLarkPostRuleAutoAllows_ShouldReturnAutoAllow()
    {
        var handler = new PolicyHandler(
            ConfigResponse(
                rules:
                [
                    Rule(
                        methods: ["POST"],
                        resourcePattern: "/open-apis/im/v1/messages",
                        verbs: ["write"],
                        effect: "auto_allow"),
                ],
                defaultEffect: "deny"),
            SettingsResponse(approvalRequired: false));
        var tokens = new RecordingAccessTokenProvider();
        var port = CreatePort(handler, tokens);

        var result = await port.EvaluateAsync(Request(
            NyxIdRequestMethod.Post,
            "/open-apis/im/v1/messages"));

        result.Decision.Should().Be(NyxIdScheduledOperationAuthorizationDecision.AutoAllow);
        handler.Paths.Should().Equal(
            "/api/v1/approvals/service-configs",
            "/api/v1/notifications/settings",
            "/api/v1/user-services");
        handler.AuthorizationSchemes.Should().OnlyContain(value => value == "Bearer test-token");
        tokens.Requests.Should().ContainSingle();
        tokens.Requests[0].Should().BeEquivalentTo(new WorkflowCallerNyxIdAuthority
        {
            Platform = "lark",
            Tenant = "tenant-alpha",
            ExternalUserId = "external-user-alpha",
            BindingId = "binding-alpha",
            Scope = "proxy",
        });
    }

    [Fact]
    public async Task EvaluateAsync_WhenMultipleRulesMatch_ShouldUseFirstRule()
    {
        var port = CreatePort(new PolicyHandler(
            ConfigResponse(rules:
            [
                Rule(["POST"], "*", ["write"], "deny"),
                Rule(["POST"], "*", ["write"], "auto_allow"),
            ]),
            SettingsResponse(false)));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(NyxIdScheduledOperationAuthorizationDecision.Denied);
    }

    [Theory]
    [InlineData("per_request", NyxIdScheduledOperationAuthorizationDecision.PerRequestApprovalRequired)]
    [InlineData("grant", NyxIdScheduledOperationAuthorizationDecision.ReusableGrantRequired)]
    public async Task EvaluateAsync_WhenApprovalIsRequired_ShouldPreserveApprovalMode(
        string mode,
        NyxIdScheduledOperationAuthorizationDecision expected)
    {
        var port = CreatePort(new PolicyHandler(
            ConfigResponse(rules:
            [
                Rule(["POST"], "/messages", ["write"], "require_approval", mode),
            ]),
            SettingsResponse(false)));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(expected);
    }

    [Fact]
    public async Task EvaluateAsync_WhenNoRuleMatches_ShouldUseDefaultEffectAndConfigMode()
    {
        var port = CreatePort(new PolicyHandler(
            ConfigResponse(
                rules: [Rule(["DELETE"], "*", [], "deny")],
                approvalMode: "grant",
                defaultEffect: "require_approval"),
            SettingsResponse(false)));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(
            NyxIdScheduledOperationAuthorizationDecision.ReusableGrantRequired);
    }

    [Fact]
    public async Task EvaluateAsync_WhenNonEmptyRulesDoNotMatchAndNoDefault_ShouldAutoAllow()
    {
        var port = CreatePort(new PolicyHandler(
            ConfigResponse(
                rules: [Rule(["DELETE"], "*", [], "deny")],
                approvalRequired: true),
            SettingsResponse(true)));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(NyxIdScheduledOperationAuthorizationDecision.AutoAllow);
    }

    [Theory]
    [InlineData(false, NyxIdScheduledOperationAuthorizationDecision.AutoAllow)]
    [InlineData(true, NyxIdScheduledOperationAuthorizationDecision.PerRequestApprovalRequired)]
    public async Task EvaluateAsync_WhenServiceHasNoConfig_ShouldUseGlobalSetting(
        bool approvalRequired,
        NyxIdScheduledOperationAuthorizationDecision expected)
    {
        var port = CreatePort(new PolicyHandler(
            JsonSerializer.Serialize(new { configs = Array.Empty<object>() }),
            SettingsResponse(approvalRequired)));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(expected);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldUseNyxIdHttpVerbInsteadOfDeclaredWorkflowRisk()
    {
        var port = CreatePort(new PolicyHandler(
            ConfigResponse(
                rules: [Rule(["POST"], "*", ["read"], "auto_allow")],
                defaultEffect: "deny"),
            SettingsResponse(false)));
        var request = Request(NyxIdRequestMethod.Post, "/messages");
        request.Request.Risk = NyxIdOperationRisk.ReadOnly;

        var result = await port.EvaluateAsync(request);

        result.Decision.Should().Be(NyxIdScheduledOperationAuthorizationDecision.Denied);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldKeepSingleStarWithinOnePathSegment()
    {
        var config = ConfigResponse(
            rules: [Rule(["PUT"], "/repos/*/contents/*", ["write"], "deny")],
            defaultEffect: "auto_allow");

        var single = await CreatePort(new PolicyHandler(config, SettingsResponse(false)))
            .EvaluateAsync(Request(NyxIdRequestMethod.Put, "/repos/nyx/contents/main.cs"));
        var nested = await CreatePort(new PolicyHandler(config, SettingsResponse(false)))
            .EvaluateAsync(Request(NyxIdRequestMethod.Put, "/repos/nyx/contents/src/main.cs"));

        single.Decision.Should().Be(NyxIdScheduledOperationAuthorizationDecision.Denied);
        nested.Decision.Should().Be(NyxIdScheduledOperationAuthorizationDecision.AutoAllow);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldAllowDoubleStarToCrossPathSegments()
    {
        var port = CreatePort(new PolicyHandler(
            ConfigResponse(
                rules: [Rule(["PUT"], "/repos/*/contents/**", ["write"], "deny")],
                defaultEffect: "auto_allow"),
            SettingsResponse(false)));

        var result = await port.EvaluateAsync(
            Request(NyxIdRequestMethod.Put, "/repos/nyx/contents/src/main.cs"));

        result.Decision.Should().Be(NyxIdScheduledOperationAuthorizationDecision.Denied);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAuthorityResponseIsMalformed_ShouldFailClosed()
    {
        var port = CreatePort(new PolicyHandler(
            ConfigResponse(rules:
            [
                Rule(["POST"], "*", ["write"], "future_effect"),
            ]),
            SettingsResponse(false)));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(
            NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAuthorityTransportFails_ShouldFailClosed()
    {
        var port = CreatePort(new PolicyHandler(
            ConfigResponse(rules: []),
            SettingsResponse(false),
            configsStatus: HttpStatusCode.ServiceUnavailable));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(
            NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAuthorityReadExceedsTotalBudget_ShouldFailClosed()
    {
        var handler = new NeverCompletingHandler();
        var port = CreatePort(
            handler,
            authorityReadTimeout: TimeSpan.FromMilliseconds(25));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(
            NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable);
        handler.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_WhenAccessTokenIssueExceedsTotalBudget_ShouldFailClosed()
    {
        var tokens = new NeverCompletingAccessTokenProvider();
        var port = CreatePort(
            new PolicyHandler(ConfigResponse(rules: []), SettingsResponse(false)),
            tokens,
            authorityReadTimeout: TimeSpan.FromMilliseconds(25));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(
            NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable);
        tokens.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_WhenCallerCancelsAccessTokenIssue_ShouldPropagateCancellation()
    {
        var tokens = new NeverCompletingAccessTokenProvider();
        var port = CreatePort(
            new PolicyHandler(ConfigResponse(rules: []), SettingsResponse(false)),
            tokens);
        using var cts = new CancellationTokenSource();

        var evaluation = port.EvaluateAsync(
            Request(NyxIdRequestMethod.Post, "/messages"),
            cts.Token);
        cts.Cancel();

        await FluentActions.Invoking(async () => await evaluation)
            .Should()
            .ThrowAsync<OperationCanceledException>();
        tokens.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_WhenAuthorityResponseExceedsLimit_ShouldFailClosedWithoutReadingBody()
    {
        var content = new ThrowOnReadContent(contentLength: 65);
        var handler = new StaticContentHandler(content);
        var port = CreatePort(handler, authorityResponseMaxBytes: 64);

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(
            NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable);
        handler.Paths.Should().Equal("/api/v1/approvals/service-configs");
        content.ReadAttempted.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_WhenExactConfigIdentityIsAmbiguous_ShouldFailClosed()
    {
        var config = JsonSerializer.Serialize(new
        {
            configs = new[]
            {
                ServicePolicy(userServiceId: "usvc-lark"),
                ServicePolicy(userServiceId: "usvc-lark"),
            },
        });
        var port = CreatePort(new PolicyHandler(config, SettingsResponse(false)));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(
            NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTargetIsCoveredByDominantOrgPolicy_ShouldFailClosed()
    {
        var config = JsonSerializer.Serialize(new
        {
            configs = Array.Empty<object>(),
            dominant_org_policies = new[]
            {
                new { org_id = "org-alpha", service_id = "catalog-lark" },
            },
        });
        var port = CreatePort(new PolicyHandler(
            config,
            SettingsResponse(false),
            userServices: UserServicesResponse(ownerOrgId: "org-alpha")));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(
            NyxIdScheduledOperationAuthorizationDecision.AuthorityContractUnavailable);
    }

    [Fact]
    public async Task EvaluateAsync_WhenDominantPolicyBelongsToAnotherOrg_ShouldUsePersonalPolicy()
    {
        var config = JsonSerializer.Serialize(new
        {
            configs = new[]
            {
                ServicePolicy(rules:
                [
                    Rule(["POST"], "/messages", ["write"], "auto_allow"),
                ]),
            },
            dominant_org_policies = new[]
            {
                new { org_id = "org-other", service_id = "catalog-lark" },
            },
        });
        var port = CreatePort(new PolicyHandler(
            config,
            SettingsResponse(true),
            userServices: UserServicesResponse(ownerOrgId: "org-alpha")));

        var result = await port.EvaluateAsync(Request(NyxIdRequestMethod.Post, "/messages"));

        result.Decision.Should().Be(NyxIdScheduledOperationAuthorizationDecision.AutoAllow);
    }

    [Fact]
    public void AddNyxIdAuthorizationCatalogHosting_ShouldReplaceUnavailableOperationAuthority()
    {
        var services = new ServiceCollection();
        services.AddNyxIdAuthorizationCatalogHosting(new ConfigurationBuilder().Build());

        services.Last(descriptor =>
                descriptor.ServiceType == typeof(INyxIdScheduledOperationAuthorizationPort))
            .ImplementationType
            .Should()
            .Be<NyxIdApprovalPolicyScheduledOperationAuthorizationPort>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IWorkflowCallerAccessTokenProvider>()
            .Should().BeOfType<NyxIdWorkflowCallerAccessTokenProvider>();
        provider.GetRequiredService<INyxIdScheduledOperationAuthorizationPort>()
            .Should().BeOfType<NyxIdApprovalPolicyScheduledOperationAuthorizationPort>();
    }

    private static NyxIdApprovalPolicyScheduledOperationAuthorizationPort CreatePort(
        HttpMessageHandler handler,
        IWorkflowCallerAccessTokenProvider? tokens = null,
        TimeSpan? authorityReadTimeout = null,
        long? authorityResponseMaxBytes = null) =>
        new(
            new TestNyxIdApiClientFactory(handler),
            tokens ?? new RecordingAccessTokenProvider(),
            authorityReadTimeout ??
            NyxIdApprovalPolicyScheduledOperationAuthorizationPort.DefaultAuthorityReadTimeout,
            authorityResponseMaxBytes ??
            NyxIdApprovalPolicyScheduledOperationAuthorizationPort.DefaultAuthorityResponseMaxBytes);

    private static NyxIdScheduledOperationAuthorizationRequest Request(
        NyxIdRequestMethod method,
        string path,
        string userServiceId = "usvc-lark") =>
        new(
            new StudioMemberInvocationTarget(),
            new AuthorizationOwnerIdentity(),
            new AuthorizationOwnerIdentity(),
            "lark",
            "tenant-alpha",
            "external-user-alpha",
            "binding-alpha",
            new NyxIdRequestSelector
            {
                UserServiceId = userServiceId,
                Method = method,
                PathTemplate = path,
                BodyMode = NyxIdRequestBodyMode.Json,
                ResponseMode = NyxIdRequestResponseMode.Text,
            },
            new NyxIdExplicitRequestGrant(),
            DateTimeOffset.Parse("2026-08-17T01:00:00Z"));

    private static object Rule(
        string[] methods,
        string resourcePattern,
        string[] verbs,
        string effect,
        string mode = "per_request") => new
    {
        methods,
        resource_pattern = resourcePattern,
        verbs,
        effect,
        mode,
    };

    private static object ServicePolicy(
        string userServiceId = "usvc-lark",
        string serviceId = "catalog-lark",
        bool approvalRequired = true,
        string approvalMode = "per_request",
        object[]? rules = null,
        string? defaultEffect = null) => new
    {
        service_id = serviceId,
        service_name = "Lark",
        approval_required = approvalRequired,
        approval_mode = approvalMode,
        rules = rules ?? [],
        default_effect = defaultEffect,
        created_at = "2026-08-17T00:00:00Z",
        updated_at = "2026-08-17T00:00:00Z",
        user_service_id = userServiceId,
        user_service_slug = "api-lark-bot-2",
    };

    private static string ConfigResponse(
        object[] rules,
        bool approvalRequired = true,
        string approvalMode = "per_request",
        string? defaultEffect = null) =>
        JsonSerializer.Serialize(new
        {
            configs = new[]
            {
                ServicePolicy(
                    approvalRequired: approvalRequired,
                    approvalMode: approvalMode,
                    rules: rules,
                    defaultEffect: defaultEffect),
            },
        });

    private static string SettingsResponse(bool approvalRequired) =>
        JsonSerializer.Serialize(new { approval_required = approvalRequired });

    private static string UserServicesResponse(
        string userServiceId = "usvc-lark",
        string catalogServiceId = "catalog-lark",
        string? ownerOrgId = null)
    {
        object credentialSource = ownerOrgId is null
            ? new { type = "personal" }
            : new
            {
                type = "org",
                org_id = ownerOrgId,
                org_name = "Example Org",
                avatar_url = (string?)null,
                role = "member",
                allowed = true,
            };
        return JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    id = userServiceId,
                    catalog_service_id = catalogServiceId,
                    is_active = true,
                    credential_source = credentialSource,
                },
            },
        });
    }

    private sealed class RecordingAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        public List<WorkflowCallerNyxIdAuthority> Requests { get; } = [];

        public Task<string> IssueAsync(
            WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default)
        {
            Requests.Add(authority.Clone());
            return Task.FromResult("test-token");
        }
    }

    private sealed class NeverCompletingAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        private readonly TaskCompletionSource<bool> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task<string> IssueAsync(
            WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default)
        {
            try
            {
                await _never.Task.WaitAsync(ct);
                throw new InvalidOperationException("The access token unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class TestNyxIdApiClientFactory(HttpMessageHandler handler)
        : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() =>
            new(
                new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
                new HttpClient(handler, disposeHandler: false));
    }

    private sealed class PolicyHandler(
        string configs,
        string settings,
        HttpStatusCode configsStatus = HttpStatusCode.OK,
        string? userServices = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> AuthorizationSchemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);
            AuthorizationSchemes.Add(request.Headers.Authorization is { } authorization
                ? $"{authorization.Scheme} {authorization.Parameter}"
                : string.Empty);
            var (status, body) = path switch
            {
                "/api/v1/approvals/service-configs" => (configsStatus, configs),
                "/api/v1/notifications/settings" => (HttpStatusCode.OK, settings),
                "/api/v1/user-services" => (
                    HttpStatusCode.OK,
                    userServices ?? UserServicesResponse()),
                _ => (HttpStatusCode.NotFound, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class NeverCompletingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _never.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("The authority response unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class StaticContentHandler(HttpContent content) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }

    private sealed class ThrowOnReadContent : HttpContent
    {
        public ThrowOnReadContent(long contentLength)
        {
            Headers.ContentLength = contentLength;
        }

        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("The oversized authority body must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength!.Value;
            return true;
        }
    }
}
