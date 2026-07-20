using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Tests;

public sealed class ToolProviderHttpClientRegistrationTests
{
    [Fact]
    public void AddNyxIdTools_RegistersProductionHttpClientsThroughFactory()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyx.test");

        services.ShouldContainTypedHttpClient<NyxIdApiClient>();
        services.ShouldContainNamedHttpClient(ConnectedServiceSpecCache.HttpClientName);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<NyxIdApiClient>().Should().NotBeNull();
        provider.GetRequiredService<INyxIdApiClientFactory>()
            .CreateClient()
            .Should()
            .NotBeNull();
        provider.GetRequiredService<IRemoteToolApprovalPort>().Should()
            .BeOfType<NyxIdRemoteToolApprovalPort>();
        provider.GetServices<IToolApprovalHandler>().Should().BeEmpty();
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
        var tool = new NyxIdRequireServiceTool();
        const string arguments =
            """{"service_slug":"api-github","service_label":"GitHub","resource_uri":"/repos/private?token=bearer-secret"}""";

        var result = await tool.ExecuteAsync(arguments);
        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
        receipt.AuthorizationRequired.ServiceSlug.Should().Be("api-github");
        receipt.AuthorizationRequired.ServiceLabel.Should().Be("GitHub");
        receipt.AuthorizationRequired.ResourceUri.Should().Be("/repos/private");
        receipt.AuthorizationRequired.ReasonCode.Should().Be("NYXID_SERVICE_NOT_CONNECTED");
        receipt.AuthorizationRequired.SafeMessage.Should().Be("Connect api-github to continue.");
        receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("token=");
    }

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
    public async Task AddNyxIdTools_WithSshBypass_DiscoversSshExecWithoutLocalApprovalHandler()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options =>
        {
            options.BaseUrl = "https://nyx.test";
            options.EnableSshExecTool = true;
            options.BypassSshExecApproval = true;
        });

        await using var provider = services.BuildServiceProvider();
        provider.GetServices<IToolApprovalHandler>().Should().BeEmpty();
        var source = provider.GetServices<IAgentToolSource>().OfType<NyxIdAgentToolSource>().Single();

        var tools = await source.DiscoverToolsAsync();
        var sshExec = tools.Should().ContainSingle(tool => tool is NyxIdSshExecTool).Subject;
        var codexExec = tools.Should().ContainSingle(tool => tool is NyxIdCodexExecTool).Subject;
        codexExec.Name.Should().Be("codex_exec");
        sshExec.RequiresApproval("""{"service":"host","command":"uptime","principal":"ubuntu"}""")
            .Should()
            .BeFalse();
        codexExec.RequiresApproval("""{"target":{"kind":"private_ssh","private_ssh":{"service":"host","principal":"ubuntu"}},"prompt":"check"}""")
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

    public static void ShouldContainNamedHttpClient(
        this IServiceCollection services,
        string name)
    {
        services.ShouldContainHttpClientOptions(name);
    }

    private static void ShouldContainHttpClientOptions(
        this IServiceCollection services,
        string name)
    {
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IConfigureOptions<HttpClientFactoryOptions>) &&
            descriptor.ImplementationInstance is ConfigureNamedOptions<HttpClientFactoryOptions> options &&
            options.Name == name)
            .Should()
            .BeTrue("AddHttpClient should register HttpClientFactoryOptions for '{0}'", name);
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
