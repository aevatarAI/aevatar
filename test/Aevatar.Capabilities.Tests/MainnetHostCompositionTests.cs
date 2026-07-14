using Aevatar.GAgents.Scheduled;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.ToolProviders.AgentCatalog;
using Aevatar.AI.ToolProviders.AevatarInvocation;
using Aevatar.AI.ToolProviders.Channel;
using Aevatar.AI.ToolProviders.ChannelAdmin;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.ToolProviders.StudioProvisioning;
using Aevatar.AI.ToolProviders.Telegram;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.Audit.Core.Identity;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Configuration;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Runtime.Hosting.Maintenance;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Modules;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.StatusDashboard.Executors;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Integration.AI;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Capabilities.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class MainnetHostCompositionTests
{
    [Fact]
    public async Task AddAevatarMainnetHost_WithInMemoryDependencies_ShouldBuildAndStartFullComposition()
    {
        using var home = new TemporaryAevatarHomeScope();
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var documentProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__InMemory__Enabled", "true");
        using var documentElasticsearch = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__Elasticsearch__Enabled", "false");
        using var graphProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__InMemory__Enabled", "true");
        using var graphNeo4j = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__Neo4j__Enabled", "false");
        using var projectionEnvironment = new EnvironmentVariableScope(
            "Projection__Policies__Environment", "Development");
        using var denyInMemoryDocument = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryDocumentReadStore", "false");
        using var denyInMemoryGraph = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryGraphFactStore", "false");
        var builder = CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        await using var app = builder.Build();
        app.MapAevatarMainnetHost();
        await app.StartAsync();

        app.Services.GetRequiredService<IServiceRolloutCommandObservationQueryReader>().Should().NotBeNull();
        app.Services.GetRequiredService<IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string>>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<IProjectionDocumentReader<UserAgentApiKeyRevocationDocument, string>>()
            .Should()
            .NotBeNull();
        var readModelDescriptors = app.Services.GetServices<IProjectionReadModelDescriptor>().ToList();
        readModelDescriptors.Select(static descriptor => descriptor.Name)
            .Should()
            .OnlyHaveUniqueItems();
        readModelDescriptors.Should().HaveCount(19);
        readModelDescriptors.Should()
            .ContainSingle(static descriptor => descriptor.Name == "workflow-external-approval-continuation");
        readModelDescriptors.Should()
            .ContainSingle(static descriptor => descriptor.Name == "user-agent-api-key-revocation");
        readModelDescriptors.Should()
            .ContainSingle(static descriptor => descriptor.Name == "streaming-proxy-chat-session");
        readModelDescriptors.Should()
            .NotContain(static descriptor => descriptor.Name == "script-native-document");
        app.Services.GetService<IProjectionDocumentReader<ScriptNativeDocumentReadModel, string>>()
            .Should()
            .BeNull();
        app.Services.GetRequiredService<IExternalIdentityBindingQueryPort>().Should().NotBeNull();
        app.Services.GetRequiredService<ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<IProjectionDocumentReader<ExternalIdentityBindingDocument, string>>()
            .Should()
            .NotBeNull();
        app.Services.GetServices<IHealthProbeExecutor>()
            .Select(static executor => executor.Kind)
            .Should()
            .Contain("aevatar_core_loop");

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Where(x => x is not null)
            .ToList();

        routePatterns.Should().Contain("/api/webhooks/nyxid-relay/health");
        routePatterns.Should().Contain("/api/channels/registrations");
        routePatterns.Should().Contain("/api/oauth/nyxid-callback");
        routePatterns.Should().Contain("/api/services/");
        routePatterns.Should().Contain("/api/skill-runners/{agentId}/external-trigger-sources/{sourceId}/deliveries");
        routePatterns.Should().Contain("/v1/responses");
        routePatterns.Should().Contain("/v1/chat/completions");
        routePatterns.Should().NotContain("/v1/chat/completion");

        // Both Lark and Telegram tool providers must register with IAgentToolSource so the
        // declared agent tools (lark_messages_send / telegram_messages_send / telegram_chats_lookup
        // / etc.) are actually discoverable at runtime. Without this assertion the host can
        // silently drop a tool provider after a project-reference change and tests still pass.
        var toolSources = app.Services.GetServices<IAgentToolSource>().ToList();
        toolSources.Should().Contain(source => source is LarkAgentToolSource);
        toolSources.Should().Contain(source => source is TelegramAgentToolSource);
        toolSources.Should().Contain(source => source is SkillsAgentToolSource);
        toolSources.Should().Contain(source => source is OrnnAgentToolSource);
        toolSources.Should().Contain(source => source is HumanInteractionChannelToolSource);
        app.Services.GetRequiredService<IHumanInteractionPort>()
            .Should()
            .BeOfType<SkillBackedHumanInteractionPort>();
        app.Services.GetRequiredService<IChannelInteractionNotificationPort>()
            .Should()
            .BeOfType<NyxIdRelayChannelInteractionNotificationPort>();
        app.Services.GetRequiredService<IRemoteToolApprovalNotificationPort>()
            .Should()
            .BeOfType<NyxIdRelayRemoteToolApprovalNotificationPort>();
        // Yield capability follows the actor, never the container (#2004): a DI-global
        // yielding handler hands "I will resume you" to surfaces with no pending-approval
        // continuation, stranding dead-letter approvals. RoleGAgent wires its own handler;
        // every other surface must fall through to MissingApprovalHandler and fail closed.
        app.Services.GetServices<IToolApprovalHandler>().Should().BeEmpty();
        app.Services.GetServices<IToolCallMiddleware>()
            .Should()
            .NotContain(middleware => middleware is ToolApprovalMiddleware);

        await app.StopAsync();
    }

    [Fact]
    public void AddAevatarMainnetHost_WhenVoiceRealtimeConfigured_ShouldMapPolicyAwareWhipOfferEndpoint()
    {
        using var home = new TemporaryAevatarHomeScope();
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var documentProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__InMemory__Enabled", "true");
        using var documentElasticsearch = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__Elasticsearch__Enabled", "false");
        using var graphProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__InMemory__Enabled", "true");
        using var graphNeo4j = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__Neo4j__Enabled", "false");
        using var projectionEnvironment = new EnvironmentVariableScope(
            "Projection__Policies__Environment", "Development");
        using var denyInMemoryDocument = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryDocumentReadStore", "false");
        using var denyInMemoryGraph = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryGraphFactStore", "false");
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:VoicePresence:OpenAI:ApiKey"] = "voice-openai-key",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.MapAevatarMainnetHost();

        app.Services.GetRequiredService<IWebRtcVoiceTransportFactory>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<VoiceWhipAttachExecutor>()
            .Should()
            .NotBeNull();
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Should()
            .ContainSingle(endpoint =>
                endpoint.RoutePattern.RawText == "/whip/offer" &&
                endpoint.Metadata
                    .OfType<HttpMethodMetadata>()
                    .Single()
                    .HttpMethods
                    .Contains(HttpMethods.Post, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddAevatarMainnetHost_ShouldRegisterDefaultToolSets()
    {
        using var home = new TemporaryAevatarHomeScope();
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var documentProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__InMemory__Enabled", "true");
        using var documentElasticsearch = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__Elasticsearch__Enabled", "false");
        using var graphProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__InMemory__Enabled", "true");
        using var graphNeo4j = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__Neo4j__Enabled", "false");
        using var projectionEnvironment = new EnvironmentVariableScope(
            "Projection__Policies__Environment", "Development");
        using var denyInMemoryDocument = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryDocumentReadStore", "false");
        using var denyInMemoryGraph = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryGraphFactStore", "false");
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var registry = app.Services.GetRequiredService<IToolSetRegistry>();
        app.Services.GetRequiredService<IOptions<ChatRoutingOptions>>()
            .Value.Defaults.DefaultForwardToModelToolSetName.Should().Be(ToolSetNames.WorkspaceDefault);

        registry.GetRegisteredNames().Should().Equal(
            ToolSetNames.LarkSelfNotify,
            ToolSetNames.NyxIdConnectedServices,
            ToolSetNames.WorkspaceDefault);

        var workspace = registry.Resolve(new ChatRouteToolSetRef { Name = ToolSetNames.WorkspaceDefault });
        workspace.IsSuccess.Should().BeTrue(workspace.Error?.Message);
        workspace.Sources.Should().Contain(source => source is InvokeGAgentToolSource);
        workspace.Sources.Should().Contain(source => source is InvokeTeamToolSource);
        workspace.Sources.Should().Contain(source => source is StartWorkflowToolSource);
        workspace.Sources.Should().Contain(source => source is ObserveRunToolSource);
        workspace.Sources.Should().Contain(source => source is ReadWorkflowRunArtifactToolSource);
        workspace.Sources.Should().Contain(source => source is ProvisionWorkflowScheduleToolSource);
        workspace.Sources.Should().Contain(source => source is CreateStudioTeamToolSource);
        workspace.Sources.Should().Contain(source => source is CreateStudioMemberToolSource);
        workspace.Sources.Should().Contain(source => source is BindStudioMemberWorkflowToolSource);
        workspace.Sources.Should().Contain(source => source is ScheduleStudioMemberWorkflowToolSource);
        workspace.Sources.Should().Contain(source => source.GetType().Name == "ResponsesAevatarToolProvider");
        workspace.Sources.Should().Contain(source => source is ChannelInteractiveReplyToolSource);
        workspace.Sources.Should().Contain(source => source is ChannelRegistrationToolSource);
        workspace.Sources.Should().Contain(source => source is AgentDeliveryTargetToolSource);
        workspace.Sources.Should().Contain(source => source is NyxIdAgentToolSource);
        workspace.Sources.Should().Contain(source => source is LarkAgentToolSource);
        workspace.Sources.Should().Contain(source => source is TelegramAgentToolSource);
        workspace.Sources.Should().Contain(source => source is ChronoStorageAgentToolSource);
        workspace.Sources.Should().Contain(source => source is WebAgentToolSource);
        workspace.Sources.Should().Contain(source => source is SkillsAgentToolSource);
        workspace.Sources.Should().Contain(source => source is OrnnAgentToolSource);
        app.Services.GetRequiredService<LarkToolOptions>()
            .EnableWorkflowFileSubmit.Should().BeFalse();
        app.Services.GetServices<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowConnectedServiceResourceFetchAdapter>()
            .Should()
            .ContainSingle(adapter => adapter.GetType().Name == "LarkMessageResourceFetchAdapter");
        var workflowToolNames = new List<string>();
        foreach (var source in app.Services.GetServices<Aevatar.Workflow.Core.Modules.IWorkflowToolSource>())
        {
            var tools = await source.GetToolsAsync();
            workflowToolNames.AddRange(tools.Select(static tool => tool.Name));
        }
        workflowToolNames.Should().ContainSingle(name => name == "workflow_connected_service_resource_fetch");

        var larkSelfNotify = registry.Resolve(new ChatRouteToolSetRef { Name = ToolSetNames.LarkSelfNotify });
        larkSelfNotify.IsSuccess.Should().BeTrue(larkSelfNotify.Error?.Message);
        larkSelfNotify.Sources.Select(static source => source.GetType()).Should()
            .Equal(workspace.Sources.Select(static source => source.GetType()));
        larkSelfNotify.Sources.Should().Contain(source => source is LarkAgentToolSource);
        larkSelfNotify.Sources.Should().Contain(source => source is NyxIdAgentToolSource);

        var nyxIdConnectedServices = registry.Resolve(new ChatRouteToolSetRef { Name = ToolSetNames.NyxIdConnectedServices });
        nyxIdConnectedServices.IsSuccess.Should().BeTrue(nyxIdConnectedServices.Error?.Message);
        nyxIdConnectedServices.Sources.Should().ContainSingle(source => source is NyxIdConnectedServiceToolSource);
        workspace.Sources.Should().NotContain(source => source is NyxIdConnectedServiceToolSource);

        var voice = registry.Resolve(new ChatRouteToolSetRef { Name = "voice.realtime" });
        voice.IsSuccess.Should().BeFalse();
        voice.Error!.Code.Should().Be(ToolSetResolveError.UnknownNameCode);

        var unknown = registry.Resolve(new ChatRouteToolSetRef { Name = "missing.set" });
        unknown.IsSuccess.Should().BeFalse();
        unknown.Error!.Code.Should().Be(ToolSetResolveError.UnknownNameCode);
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldIgnoreLegacyLarkWorkflowFileSubmitOptIn()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:Lark:EnableWorkflowFileSubmit"] = "true",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.Services.GetRequiredService<LarkToolOptions>()
            .EnableWorkflowFileSubmit.Should().BeTrue();
        app.Services.GetServices<IWorkflowFileMultipartUploadPort>()
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .BeOfType<NyxIdWorkflowFileMultipartUploadPort>();
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldIgnoreLegacyWorkflowConnectedServiceFileSubmitSection()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(BuildWorkflowFileSubmitTargetConfiguration());

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.Services.GetServices<IWorkflowFileMultipartUploadPort>()
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .BeOfType<NyxIdWorkflowFileMultipartUploadPort>();
        app.Services.GetServices<IWorkflowFileMultipartUploadPolicyResolver>()
            .Should()
            .ContainSingle();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task AddAevatarMainnetHost_ShouldResolveWorkflowFileMultipartUploadSafetyPolicyFromCandidate(
        string method)
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var resolver = app.Services.GetRequiredService<IWorkflowFileMultipartUploadPolicyResolver>();
        resolver.Should().BeOfType<MainnetWorkflowFileMultipartUploadSafetyPolicyResolver>();

        var resolution = await resolver.ResolveAsync(
            new WorkflowFileMultipartUploadCandidate(
                BuildWorkflowFileRef(),
                ServiceSlug: "api-custom-service",
                Path: "custom/files/upload",
                Method: method,
                FileFieldName: "asset",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["folder"] = "reports",
                    ["purpose"] = "extract",
                },
                OutputKind: "external_resource_id",
                OutputSelector: "data.document_id",
                MaxFileBytes: 512),
            BuildWorkflowFileRef(),
            BuildMultipartUploadContext());

        resolution.IsAllowed.Should().BeTrue(resolution.Detail);
        resolution.Policy.Should().NotBeNull();
        resolution.Policy!.ServiceSlug.Should().Be("api-custom-service");
        resolution.Policy.Path.Should().Be("custom/files/upload");
        resolution.Policy.Method.Should().Be(method);
        resolution.Policy.FileFieldName.Should().Be("asset");
        resolution.Policy.FormFields.Should().ContainKey("folder").WhoseValue.Should().Be("reports");
        resolution.Policy.FormFields.Should().ContainKey("purpose").WhoseValue.Should().Be("extract");
        resolution.Policy.OutputKind.Should().Be("external_resource_id");
        resolution.Policy.OutputSelector.Should().Be("data.document_id");
        resolution.Policy.MaxFileBytes.Should().Be(512);
    }

    [Fact]
    public async Task AddAevatarMainnetHost_ShouldUseMainnetWorkflowFileMultipartUploadSafetyLimitWhenCandidateDoesNotNarrow()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var resolver = app.Services.GetRequiredService<IWorkflowFileMultipartUploadPolicyResolver>();
        resolver.Should().BeOfType<MainnetWorkflowFileMultipartUploadSafetyPolicyResolver>();

        var resolution = await resolver.ResolveAsync(
            new WorkflowFileMultipartUploadCandidate(
                BuildWorkflowFileRef(),
                ServiceSlug: "storage",
                Path: "files/upload",
                Method: "POST",
                FileFieldName: "upload",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal),
                OutputKind: "external_resource_id",
                OutputSelector: "data.document_id",
                MaxFileBytes: null),
            BuildWorkflowFileRef(),
            BuildMultipartUploadContext());

        resolution.IsAllowed.Should().BeTrue(resolution.Detail);
        resolution.Policy.Should().NotBeNull();
        resolution.Policy!.MaxFileBytes.Should().Be(
            MainnetWorkflowFileMultipartUploadSafetyPolicyResolver.MaxFileBytes);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task AddAevatarMainnetHost_ShouldRejectNonUploadWorkflowFileMultipartUploadMethod(
        string method)
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var resolver = app.Services.GetRequiredService<IWorkflowFileMultipartUploadPolicyResolver>();

        var resolution = await resolver.ResolveAsync(
            new WorkflowFileMultipartUploadCandidate(
                BuildWorkflowFileRef(),
                ServiceSlug: "storage",
                Path: "files/upload",
                Method: method,
                FileFieldName: "upload",
                FormFields: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["folder"] = "reports",
                },
                OutputKind: "external_resource_id",
                OutputSelector: "data.document_id",
                MaxFileBytes: null),
            BuildWorkflowFileRef(),
            BuildMultipartUploadContext());

        resolution.IsAllowed.Should().BeFalse();
        resolution.Error.Should().Be("unsupported_method");
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldRegisterNyxIdProxyFileArtifactIngressWithWorkflowFileStorage()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:ProxyFileArtifactMaxBytes"] = "12345",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.Services.GetRequiredService<INyxIdProxyFileArtifactIngress>()
            .Should()
            .BeOfType<NyxIdProxyWorkflowFileArtifactIngress>();
        app.Services.GetRequiredService<NyxIdToolOptions>()
            .ProxyFileArtifactMaxBytes.Should().Be(12345);
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldNotBindMalformedLegacyWorkflowFileSubmitEndpointPolicy()
    {
        using var home = new TemporaryAevatarHomeScope();
        var configurationValues = BuildWorkflowFileSubmitTargetConfiguration();
        configurationValues["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:Path"] = "https://storage.example.test/files/upload";
        var builder = CreateBuilder(configurationValues);

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        builder.Services.Should().NotContain(descriptor =>
            descriptor.ServiceType.FullName != null &&
            descriptor.ServiceType.FullName.Contains("WorkflowConnectedServiceFileSubmit", StringComparison.Ordinal));

        using var app = builder.Build();
        app.Services.GetServices<IWorkflowFileMultipartUploadPort>()
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .BeOfType<NyxIdWorkflowFileMultipartUploadPort>();
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldEnableFailFastDiValidation()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.Services.AddSingleton<BrokenMainnetService>();

        var act = () => builder.Build();

        act.Should()
            .Throw<Exception>()
            .WithMessage("*MissingMainnetDependency*");
    }

    [Fact]
    public void AddAevatarMainnetHost_WhenSkipHmacVerificationEnabledInProduction_ShouldThrow()
    {
        // Security fail-fast wiring: the host must abort startup if device-event HMAC
        // verification is disabled in a Production environment. This exercises the real
        // wiring (config section "Aevatar:DeviceEvents" + builder.Environment.IsProduction()),
        // not just the DeviceEventOptions.EnsureNotSkippingHmacInProduction helper.
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(
            new Dictionary<string, string?>
            {
                ["Aevatar:DeviceEvents:SkipHmacVerification"] = "true",
            },
            environmentName: Environments.Production);

        var act = () => builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*SkipHmacVerification*");
    }

    [Fact]
    public void AddAevatarMainnetHost_WhenSkipHmacVerificationEnabledOutsideProduction_ShouldNotThrow()
    {
        // The same flag is permitted outside Production, proving the guard is
        // environment-gated rather than unconditional.
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(
            new Dictionary<string, string?>
            {
                ["Aevatar:DeviceEvents:SkipHmacVerification"] = "true",
            },
            environmentName: Environments.Development);

        var act = () => builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        act.Should().NotThrow();
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldStartHostedServicesSequentially()
    {
        // Regression guard (2026-06-03 prod incident): enabling
        // HostOptions.ServicesStartConcurrently raced the co-hosted Orleans silo
        // reaching Active. Grain-calling startup services (WorkflowDefinitionBootstrap,
        // ChannelBotRegistration, AevatarOAuthClientBootstrap, HealthProbeStartup,
        // StreamingProxyChatLifecycleContinuationRunner) fired before the silo could
        // create activations -> "Unable to create local activation. Rejecting now."
        // -> AggregateException -> CrashLoopBackOff. Sequential startup (the Generic
        // Host default) runs hosted services in registration order so Kestrel binds
        // the probe port and the Orleans silo reaches Active before grain-callers run.
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var hostOptions = app.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        hostOptions.ServicesStartConcurrently.Should().BeFalse();
        hostOptions.ServicesStopConcurrently.Should().BeFalse();
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldAssertChannelIdentityElasticsearchAcl()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var aclOptions = app.Services.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>().Value;

        aclOptions.GrantMatchesGrainEventStoreInternal.Should().BeTrue();
        aclOptions.GrantDescription.Should().Contain("aevatar-oauth-clients");
    }

    [Fact]
    public void AddAevatarMainnetHost_InProduction_ShouldRegisterDistributedIdentityAssertionReplayGuard()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(environmentName: Environments.Production);

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        var descriptor = builder.Services.Last(service =>
            service.ServiceType == typeof(IIdentityAssertionReplayGuard));
        descriptor.ImplementationType.Should().Be(typeof(DistributedIdentityAssertionReplayGuard));
    }

    [Theory]
    [InlineData(null, true, "http://+:8080")]
    [InlineData("", true, "http://+:8080")]
    [InlineData("http://127.0.0.1:5080", true, "http://127.0.0.1:5080;http://+:8080")]
    [InlineData("http://+:8080", true, "http://+:8080")]
    [InlineData("http://127.0.0.1:5080; http://+:8080", true, "http://127.0.0.1:5080; http://+:8080")]
    [InlineData(null, false, "http://127.0.0.1:5080")]
    [InlineData("http://127.0.0.1:5099", false, "http://127.0.0.1:5099")]
    public void ResolveMainnetListenUrls_ShouldKeepContainerProbePortReachable(
        string? configuredUrls,
        bool runningInContainer,
        string expected)
    {
        MainnetHostBuilderExtensions.ResolveMainnetListenUrls(configuredUrls, runningInContainer)
            .Should()
            .Be(expected);
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldRegisterRetiredActorCleanupHostedService()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        // Refactor (issue1290-first): Old: strict cleanup-before-module-startup invariant.  New: cleanup is best-effort restart-idempotent, not a cross-pod completion barrier.
        HostedServiceDescriptors<RetiredActorCleanupHostedService>(builder.Services).Should().ContainSingle();
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldEnableDeviceInboundDirectVoiceAdmissionOnce()
    {
        using var home = new TemporaryAevatarHomeScope();
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var documentProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__InMemory__Enabled", "true");
        using var documentElasticsearch = new EnvironmentVariableScope(
            "AEVATAR_Projection__Document__Providers__Elasticsearch__Enabled", "false");
        using var graphProvider = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__InMemory__Enabled", "true");
        using var graphNeo4j = new EnvironmentVariableScope(
            "AEVATAR_Projection__Graph__Providers__Neo4j__Enabled", "false");
        using var projectionEnvironment = new EnvironmentVariableScope(
            "Projection__Policies__Environment", "Development");
        using var denyInMemoryDocument = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryDocumentReadStore", "false");
        using var denyInMemoryGraph = new EnvironmentVariableScope(
            "Projection__Policies__DenyInMemoryGraphFactStore", "false");
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:VoicePresence:OpenAI:ApiKey"] = "voice-openai-key",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var factory = app.Services.GetServices<IEventModuleFactory<IEventHandlerContext>>()
            .OfType<VoicePresenceModuleFactory>()
            .Single();
        factory.TryCreate("voice_presence", out var module).Should().BeTrue();
        var voiceModule = module.Should().BeOfType<VoicePresenceModule>().Subject;
        var deviceInboundEnvelope = new EventEnvelope
        {
            Payload = new Any
            {
                TypeUrl = "type.googleapis.com/aevatar.gagents.household.DeviceInbound",
                Value = ByteString.CopyFromUtf8("device-inbound"),
            },
            Route = EnvelopeRouteSemantics.CreateDirect("device-events.callback", "voice-agent"),
        };

        voiceModule.CanHandle(deviceInboundEnvelope).Should().BeTrue();
        app.Services.GetServices<VoicePresenceModuleRegistration>()
            .SelectMany(static registration => registration.Names)
            .Should()
            .ContainSingle(static name => string.Equals(name, "voice_presence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MapAevatarMainnetHost_WithVoiceConfigured_ShouldExposeOnlyPolicyAwareVoiceIngress()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:VoicePresence:OpenAI:ApiKey"] = "voice-openai-key",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.MapAevatarMainnetHost();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        routePatterns.Should().Contain("/ws/voice");
        routePatterns.Should().NotContain("/ws/voice/{actorId}");
    }

    [Fact]
    public void ConfigureMainnetAIFeatures_ShouldPreserveConfiguredVoiceDrainTimeout()
    {
        var options = new AevatarAIFeatureOptions();
        options.VoicePresence.Module = new VoicePresenceModuleOptions
        {
            DrainTimeout = TimeSpan.FromSeconds(17),
        };

        InvokeConfigureMainnetAIFeatures(options);

        options.VoicePresence.Module.DrainTimeout.Should().Be(TimeSpan.FromSeconds(17));
        options.VoicePresence.Module.DirectExternalEventTypeUrls
            .Should()
            .ContainSingle("type.googleapis.com/aevatar.gagents.household.DeviceInbound");
    }

    private static WebApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?>? overrides = null,
        string? environmentName = null)
    {
        var options = new WebApplicationOptions
        {
            EnvironmentName = environmentName ?? Environments.Development,
        };

        var builder = WebApplication.CreateBuilder(options);
        var values = new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "InMemory",
            ["GAgentService:Demo:Enabled"] = "false",
            [$"{AuditActorIdentityHasherOptions.SectionName}:ActiveKeyId"] = "test-key-1",
            [$"{AuditActorIdentityHasherOptions.SectionName}:Keys:0:KeyId"] = "test-key-1",
            [$"{AuditActorIdentityHasherOptions.SectionName}:Keys:0:Key"] = "mainnet composition audit identity key",
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
            ["Aevatar:NyxId:Authority"] = "https://nyxid.example.test",
        };
        if (overrides != null)
        {
            foreach (var (key, value) in overrides)
                values[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static Dictionary<string, string?> BuildWorkflowFileSubmitTargetConfiguration() =>
        new()
        {
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Target"] = "submit_invoice",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Provider"] = "nyxid_connected_service",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:OutputField"] = "document_id",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:MaxFileBytes"] = "1024",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:AllowedMediaTypes:0"] = "text/plain",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Arguments:folder:Name"] = "folder",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Arguments:folder:Required"] = "true",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Arguments:folder:AllowedValues:0"] = "reports",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:ServiceSlug"] = "storage",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:Path"] = "files/upload",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:Method"] = "POST",
            ["WorkflowConnectedServiceFileSubmit:Targets:0:Endpoint:FileFieldName"] = "upload",
        };

    private static FileArtifactRef BuildWorkflowFileRef() =>
        new()
        {
            FileId = "file-1",
            ArtifactId = "artifact-1",
            SourceKind = FileArtifactSourceKind.FormUpload,
            FileName = "report.txt",
            MediaType = "text/plain",
            SizeBytes = 12,
            Sha256 = "sha256-value",
            OwnerRunId = "run-1",
            OwnerScopeId = "scope-1",
        };

    private static WorkflowFileMultipartUploadExecutionContext BuildMultipartUploadContext() =>
        new(
            RunId: "run-1",
            ParentRunId: null,
            RootRunId: null,
            ScopeId: "scope-1",
            StepId: "step-1",
            ExecutionId: "exec-1",
            CallId: "call-1",
            IdempotencyKey: "idem-1");

    private static void InvokeConfigureMainnetAIFeatures(AevatarAIFeatureOptions options)
    {
        var method = typeof(MainnetHostBuilderExtensions).GetMethod(
            "ConfigureMainnetAIFeatures",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        method!.Invoke(null, [options]);
    }

    private static IEnumerable<ServiceDescriptor> HostedServiceDescriptors<THostedService>(IServiceCollection services)
        where THostedService : IHostedService
        => services.Where(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(THostedService));

    private sealed class BrokenMainnetService(MissingMainnetDependency dependency)
    {
        public MissingMainnetDependency Dependency { get; } = dependency;
    }

    private sealed class MissingMainnetDependency;

    private sealed class TemporaryAevatarHomeScope : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TemporaryAevatarHomeScope()
        {
            _previous = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            _path = Path.Combine(Path.GetTempPath(), $"aevatar-mainnet-composition-tests-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _previous);
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
