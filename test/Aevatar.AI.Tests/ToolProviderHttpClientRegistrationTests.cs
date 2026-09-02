using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class ToolProviderHttpClientRegistrationTests
{
    [Fact]
    public void AddNyxIdTools_RegistersProductionHttpClientsThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        services.ShouldContainTypedHttpClient<NyxIdApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<NyxIdApiClient>().Should().NotBeNull();
        provider.GetRequiredService<NyxIdServiceInstanceClient>().Should().NotBeNull();
        provider.GetRequiredService<INyxIdApiClientFactory>()
            .CreateClient()
            .Should()
            .NotBeNull();
        provider.GetRequiredService<IRemoteToolApprovalPort>().Should()
            .BeOfType<NyxIdRemoteToolApprovalPort>();
    }

    [Fact]
    public void AddNyxIdTools_GivesTheNyxIdClientRoomForTheLongestCodexRun()
    {
        // The 100s HttpClient default aborts long codex_exec runs before their own deadline
        // reports the failure. The managed request deadline is 300s, and the ingress layer needs
        // at least 315s to return its terminal response.
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        using var provider = services.BuildServiceProvider();
        var timeout = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(NyxIdApiClient))
            .Timeout;

        timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(315));
        timeout.Should().Be(TimeSpan.FromSeconds(NyxIdToolOptions.DefaultMaxRequestDurationSeconds));
    }

    [Fact]
    public void AddNyxIdTools_HonoursAConfiguredNyxIdRequestCeiling()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.MaxRequestDurationSeconds = 420;
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(NyxIdApiClient))
            .Timeout
            .Should().Be(TimeSpan.FromSeconds(420));
    }

    [Fact]
    public void AddNyxIdTools_ShouldRegisterFileArtifactIngressOnlyWhenWorkflowIngressExists()
    {
        var withoutWorkflowIngress = new ServiceCollection();
        withoutWorkflowIngress.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        withoutWorkflowIngress.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(INyxIdProxyFileArtifactIngress));

        var withWorkflowIngress = new ServiceCollection();
        withWorkflowIngress.AddSingleton<IFileArtifactIngressPort, StubWorkflowFileIngressPort>();
        withWorkflowIngress.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        withWorkflowIngress.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdProxyFileArtifactIngress) &&
            descriptor.ImplementationFactory != null);
        using var provider = withWorkflowIngress.BuildServiceProvider();
        provider.GetRequiredService<INyxIdProxyFileArtifactIngress>()
            .Should()
            .BeOfType<NyxIdProxyWorkflowFileArtifactIngress>();
    }

    [Fact]
    public async Task AddNyxIdTools_ResolvesToolSourceWithoutDeletedCatalogServices()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        services.Any(HttpClientRegistrationAssertions.IsDeletedNyxIdDiscoveryRegistration)
            .Should()
            .BeFalse("AddNyxIdTools must not expose deleted catalog/cache services");

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IAgentToolSource>()
            .Should()
            .ContainSingle(toolSource => toolSource is NyxIdAgentToolSource)
            .Which;

        var tools = await source.DiscoverToolsAsync();
        var names = tools.Select(tool => tool.Name).ToList();

        names.Should().Contain("nyxid_proxy");
        names.Should().Contain("nyxid_require_service");
        names.Should().NotContain("nyxid_search_capabilities");
        names.Should().NotContain("nyxid_proxy_execute");
        tools.Should().ContainSingle(tool => tool is NyxIdProxyTool);
        tools.Should().ContainSingle(tool => tool is NyxIdRequireServiceTool);
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldCreateDeterministicAuthorizationReceipt()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [{ "id": "us-other-alpha", "slug": "api-slack" }] }""");
        var tool = CreateRequireServiceTool(handler);
        const string arguments =
            """{"service_slug":"catalog-finops-alpha","service_label":"FinOps Alpha","resource_uri":"/billing/private?token=bearer-secret"}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            handler.Requests.Should().NotBeEmpty();
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.AuthorizationRequired.ServiceSlug.Should().Be("catalog-finops-alpha");
            receipt.AuthorizationRequired.ServiceLabel.Should().Be("FinOps Alpha");
            receipt.AuthorizationRequired.ResourceUri.Should().Be("/billing/private");
            receipt.AuthorizationRequired.ReasonCode.Should().Be("USER_SERVICE_NOT_VISIBLE");
            receipt.AuthorizationRequired.SafeMessage.Should().Be("No caller-visible NyxID UserService matches the requested service.");
            receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("token=");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldNotFabricateAuthorization_WhenReadinessSourceIsStale()
    {
        var handler = new StubUserServiceListHandler("""{ "error": true, "status": 503 }""");
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"api-github"}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            handler.Requests.Should().NotBeEmpty();
            result.Should().Contain("NYXID_SOURCE_UNAVAILABLE");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_SOURCE_UNAVAILABLE");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldCreateSuccessReceipt_WhenServiceIsAlreadyVisible()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [{ "id": "us-github-alpha", "slug": "api-github" }] }""");
        var tool = CreateRequireServiceTool(handler);
        const string arguments = """{"service_slug":"api-github"}""";

        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            result.Should().Contain("\"blocked\":false");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
            receipt.ResultJson.Should().Be(result);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldRejectOwnerSubjectWithoutNyxIdAuthority()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""");
        var tool = CreateRequireServiceTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext() with
        {
            NyxIdAuthority = AgentToolNyxIdAuthorityContext.Empty,
        };

        try
        {
            const string arguments = """{"service_slug":"api-github"}""";
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            result.Should().Contain("verified caller identity not available");
            handler.Requests.Should().BeEmpty();
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_CONTEXT_UNAVAILABLE");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Theory]
    [InlineData(AgentToolNyxIdCredentialKind.ProxyDelegation)]
    [InlineData(AgentToolNyxIdCredentialKind.Unspecified)]
    public async Task NyxIdRequireServiceTool_WhenCredentialIsNotSourceReadable_ShouldNotReadNyxIdSource(
        AgentToolNyxIdCredentialKind credentialKind)
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""");
        var tool = CreateRequireServiceTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext() with
        {
            Credentials = new AgentToolCredentials(
                "runtime-caller-credential",
                null,
                null,
                credentialKind),
        };

        try
        {
            var result = await tool.ExecuteAsync("""{"service_slug":"api-github"}""");

            result.Should().Contain("NYXID_SOURCE_UNAVAILABLE");
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenOwnerScopeIsMissing()
    {
        var handler = new StubUserServiceListHandler("""{ "keys": [] }""");
        var tool = CreateRequireServiceTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext() with
        {
            Caller = CapabilityContext().Caller with { OwnerScopeId = null },
        };

        try
        {
            const string arguments = """{"service_slug":"catalog-finops-alpha"}""";
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

            result.Should().Contain("owner_scope_id not available");
            handler.Requests.Should().BeEmpty();
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_CONTEXT_UNAVAILABLE");
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenReadinessResultIsMalformed()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"catalog-finops-alpha"}""";

        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            arguments,
            """{"blocked":true,"readiness_status":"ServiceRegistrationRequired"}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenReadinessFieldsHaveWrongTypes()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"catalog-finops-alpha"}""";

        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            arguments,
            """{"blocked":true,"service_slug":42,"readiness_status":[],"reason_code":{},"safe_message":false}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenReadinessStatusIsNumericText()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"catalog-finops-alpha"}""";

        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            arguments,
            """{"blocked":false,"service_slug":"catalog-finops-alpha","readiness_status":"13","reason_code":"","safe_message":""}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    [Fact]
    public void NyxIdRequireServiceTool_ShouldReturnTypedFailure_WhenResultSlugDoesNotMatchArguments()
    {
        var tool = CreateRequireServiceTool(new StubUserServiceListHandler("""{ "keys": [] }"""));
        const string arguments = """{"service_slug":"catalog-finops-alpha"}""";
        const string result =
            """{"blocked":true,"service_slug":"catalog-finops-beta","readiness_status":"ServiceRegistrationRequired","reason_code":"USER_SERVICE_NOT_VISIBLE","safe_message":"No caller-visible NyxID UserService matches the requested service."}""";

        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_REQUIRE_SERVICE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    private static IAgentTool CreateRequireServiceTool(StubUserServiceListHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdRequireServiceTool(client);
    }

    private sealed class StubUserServiceListHandler(string responseJson) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AgentToolExecutionContext CapabilityContext() =>
        AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "caller-alpha",
                null,
                "scope-alpha"),
            Credentials = new AgentToolCredentials(
                "runtime-caller-credential",
                "runtime-organization-credential",
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "nyx-user-alpha"),
        };

    [Fact]
    public void NyxIdProxyTool_AuthorizationError_ShouldCreateCredentialFreeTypedReceipt()
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" });
        var tool = new NyxIdProxyTool(client);
        const string arguments =
            """{"slug":"api-github","path":"/repos/private?access_token=bearer-secret#details"}""";
        const string result =
            """{"error":true,"status":401,"body":"{\"error\":\"unauthorized\",\"error_code\":1001,\"message\":\"expired bearer-secret\"}"}""";

        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
        receipt.AuthorizationRequired.ServiceSlug.Should().Be("api-github");
        receipt.AuthorizationRequired.ResourceUri.Should().Be("/repos/private");
        receipt.AuthorizationRequired.ReasonCode.Should().Be("NYXID_UNAUTHORIZED");
        receipt.ResultJson.Should().Contain("NYXID_UNAUTHORIZED");
        receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("access_token");
    }

    [Fact]
    public void NyxIdProxyTool_ForbiddenError_ShouldRemainCredentialFreeTypedFailure()
    {
        using var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" });
        var tool = new NyxIdProxyTool(client);
        const string arguments =
            """{"slug":"api-github","path":"/repos/private?access_token=bearer-secret#details"}""";
        const string result =
            """{"error":true,"status":403,"body":"{\"error\":\"forbidden\",\"error_code\":1002,\"message\":\"approval timed out bearer-secret\"}"}""";

        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.AuthorizationRequired.Should().BeNull();
        receipt.ErrorCode.Should().Be("NYXID_PROXY_FORBIDDEN");
        receipt.ResultJson.Should().Contain("NYXID_PROXY_FORBIDDEN");
        receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("access_token");
    }

    [Fact]
    public async Task AddNyxIdTools_WithSshOptIn_DiscoversToolsThatAlwaysRequireApproval()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.EnableSshExecTool = true;
        });

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IAgentToolSource>().OfType<NyxIdAgentToolSource>().Single();

        var tools = await source.DiscoverToolsAsync();
        var sshExec = tools.Should().ContainSingle(tool => tool is NyxIdSshExecTool).Subject;
        var codexExec = tools.Should().ContainSingle(tool => tool is NyxIdCodexExecTool).Subject;
        codexExec.Name.Should().Be("codex_exec");
        sshExec.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        sshExec.IsDestructive.Should().BeTrue();
        codexExec.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        codexExec.RequiresApproval("""{"target":{"kind":"private_ssh","private_ssh":{"service":"host","principal":"ubuntu"}},"prompt":"check"}""")
            .Should()
            .BeTrue();
        codexExec.RequiresApproval("""{"target":{"kind":"managed_sandbox"},"workspace":{"kind":"empty_git"},"prompt":"check"}""")
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task AddNyxIdTools_WithManagedPort_DiscoversCodexWithoutSshTool()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodexExecutionPort>(new ManagedCodexPortStub());
        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.EnableManagedCodexExecTool = true;
        });

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IAgentToolSource>().OfType<NyxIdAgentToolSource>().Single();
        var tools = await source.DiscoverToolsAsync();

        tools.Should().NotContain(tool => tool is NyxIdSshExecTool);
        var codexExec = tools.Should().ContainSingle(tool => tool is NyxIdCodexExecTool).Subject;
        codexExec.RequiresApproval("""{"target":{"kind":"managed_sandbox"},"workspace":{"kind":"empty_git"},"prompt":"check"}""")
            .Should().BeFalse();
    }

    [Fact]
    public async Task AddNyxIdTools_WhenManagedEnabledWithoutPort_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.EnableManagedCodexExecTool = true;
        });

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IAgentToolSource>().OfType<NyxIdAgentToolSource>().Single();

        var act = () => source.DiscoverToolsAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one managed-sandbox ICodexExecutionPort*");
    }

    [Fact]
    public void AddWebTools_RegistersWebApiClientThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddWebTools();

        services.ShouldContainTypedHttpClient<WebApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<WebApiClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddChronoStorageTools_RegistersChronoStorageApiClientThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddChronoStorageTools(options => options.ApiBaseUrl = "https://storage.test");

        services.ShouldContainTypedHttpClient<ChronoStorageApiClient>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<ChronoStorageApiClient>().Should().NotBeNull();
    }
}

file static class HttpClientRegistrationAssertions
{
    public static void ShouldContainTypedHttpClient<TClient>(
        this IServiceCollection services)
        where TClient : class
    {
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(TClient) &&
            descriptor.Lifetime == ServiceLifetime.Transient);
    }

    public static bool IsDeletedNyxIdDiscoveryRegistration(ServiceDescriptor descriptor)
    {
        var serviceName = descriptor.ServiceType.Name;
        var implementationName = descriptor.ImplementationType?.Name;

        return serviceName is "NyxIdSpecCatalog" or "IServiceDiscoveryCache" ||
               implementationName is "NyxIdSpecCatalog" or "InMemoryServiceDiscoveryCache";
    }

}

file sealed class StubWorkflowFileIngressPort : IFileArtifactIngressPort
{
    public ValueTask<FileArtifactIngressResult> IngestAsync(
        FileArtifactIngressRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new FileArtifactIngressResult(new FileArtifactRef
        {
            FileId = "file-1",
            ArtifactId = "artifact-1",
            SourceKind = request.SourceKind,
        }));
}

file sealed class ManagedCodexPortStub : ICodexExecutionPort
{
    public CodexExecutionTarget.TargetOneofCase TargetKind =>
        CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

    public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
        CodexExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
