using Aevatar.GAgents.Scheduled;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Application.CodexExecution;
using Aevatar.AI.Infrastructure.ChronoSandbox;
using Aevatar.AI.Infrastructure.ToolExecution;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.ToolProviders.AgentCatalog;
using Aevatar.AI.ToolProviders.AevatarInvocation;
using Aevatar.AI.ToolProviders.Binding;
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
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.AI.ToolProviders.Workflow;
using Aevatar.Authentication.Abstractions;
using Aevatar.Audit.Core.Identity;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Bootstrap.Hosting;
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
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Infrastructure.AgentProfiles;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StatusDashboard.Executors;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Integration.AI;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Aevatar.Capabilities.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class MainnetHostCompositionTests
{
    [Fact]
    public void AddAevatarMainnetHost_ShouldExportProjectionAndKafkaTelemetry()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        builder.Services.Should().Contain(descriptor => descriptor.ServiceType == typeof(MeterProvider));
        builder.Services.Should().Contain(descriptor => descriptor.ServiceType == typeof(TracerProvider));
        AevatarHostObservabilityExtensions.CoreMeterNames.Should().Contain("Aevatar.CQRS.Projection");
        AevatarHostObservabilityExtensions.CoreMeterNames.Should().Contain("Aevatar.Kafka.Transport");
    }

    [Fact]
    public void MainnetHost_ShouldExposeAnActorBackedNyxIdChatProfileResolver()
    {
        var resolver = typeof(MainnetHostBuilderExtensions).Assembly.GetType(
            "Aevatar.Mainnet.Host.Api.AgentProfiles.MainnetNyxIdChatAgentProfileResolver");

        resolver.Should().NotBeNull();
        resolver!.GetInterfaces().Should().Contain(typeof(INyxIdChatAgentProfileResolver));
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldRegisterBindingAgentToolSource()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();
        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        builder.Services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolSource) &&
            descriptor.ImplementationType == typeof(BindingAgentToolSource));
    }

    [Fact]
    public void GAgentServiceAndStudioCapabilities_ShouldOwnTheirCompositionDependencies()
    {
        using var home = new TemporaryAevatarHomeScope();
        var customTimeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var builder = CreateBuilder();
        builder.Services.AddSingleton<TimeProvider>(customTimeProvider);

        builder.AddAevatarDefaultHost(options =>
        {
            options.ServiceName = "Aevatar.Mainnet.Host.Api";
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.AddMainnetDistributedOrleansHost();
        builder.AddAevatarPlatform(options => options.EnableMakerExtensions = true);
        builder.AddGAgentServiceCapabilityBundle();
        builder.Services.AddAuditTrailCore(builder.Configuration);
        builder.Services.AddMainnetAgentProjectionDocumentStores(builder.Configuration);
        builder.Services.AddSingleton(Substitute.For<IScheduledAgentCredentialLifecycle>());
        builder.Services.AddSingleton(Substitute.For<INyxIdApiClientFactory>());
        builder.Services.AddScheduledAgents(builder.Configuration);
        builder.AddStudioCapability();
        builder.Services.AddSingleton(Substitute.For<ISecretVault>());

        using var app = builder.Build();

        app.Services.GetRequiredService<IStudioMemberWorkflowSchedulePort>().Should().NotBeNull();
        app.Services.GetRequiredService<IStudioScheduledCredentialMaterializer>()
            .Should()
            .BeOfType<StudioScheduledCredentialMaterializer>();
        app.Services.GetRequiredService<INyxIdAuthorizationCatalogRefreshPort>().Should().NotBeNull();
        app.Services.GetRequiredService<INyxIdChatConversationStateQueryPort>().Should().NotBeNull();
        app.Services.GetRequiredService<IProjectionDocumentReader<
            NyxIdChatConversationCurrentStateDocument,
            string>>().Should().NotBeNull();
        app.Services.GetRequiredService<TimeProvider>().Should().BeSameAs(customTimeProvider);
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldReplaceDefaultProfileResolverAndRegisterStaticRouteToolSet()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.Services.GetRequiredService<INyxIdChatAgentProfileResolver>()
            .Should()
            .BeOfType<MainnetNyxIdChatAgentProfileResolver>();
        var registry = app.Services.GetRequiredService<IToolSetRegistry>();
        registry.GetRegisteredNames().Should().Contain(AgentProfilePolicies.NyxIdChatRouteToolSet);
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldComposeAgentProfilePublishingApplication()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.Services.GetRequiredService<IExactOrnnSkillResolver>()
            .Should()
            .BeOfType<OrnnExactAgentProfileSkillResolver>();
        app.Services.GetRequiredService<IAgentProfileSkillSealer>()
            .Should()
            .BeOfType<AgentProfileSkillSealer>();
        app.Services.GetRequiredService<IAgentProfileActorPort>()
            .Should()
            .BeOfType<AgentProfileActorPort>();
        app.Services.GetRequiredService<AgentProfileApplicationService>().Should().NotBeNull();
    }

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
        builder.Services
            .AddHttpClient("NyxIdAssistantActionRegistry")
            .ConfigurePrimaryHttpMessageHandler(static () =>
                new NyxIdAssistantActionRegistryHandler());

        await using var app = builder.Build();
        app.MapAevatarMainnetHost();
        await app.StartAsync();

        app.Services.GetRequiredService<NyxIdAssistantActionRegistry>()
            .TryGetDefinition("service.connect", out _).Should().BeTrue();
        var brokerOptions = app.Services.GetRequiredService<IOptions<NyxIdBrokerOptions>>().Value;
        brokerOptions.RequiredLlmServiceSlug.Should().Be(LlmDefaults.NyxIdRoute);
        brokerOptions.AdditionalRequiredServiceSlugs.Should().Equal(
            OrnnOptions.DefaultNyxIdSlug,
            NyxIdToolOptions.DefaultSandboxServiceSlug);
        app.Services.GetRequiredService<NyxIdToolOptions>()
            .SandboxServiceSlug.Should().Be(NyxIdToolOptions.DefaultSandboxServiceSlug);
        app.Services.GetRequiredService<IServiceRolloutCommandObservationQueryReader>().Should().NotBeNull();
        app.Services.GetRequiredService<INyxIdChatAgentProfileResolver>()
            .Should()
            .BeOfType<MainnetNyxIdChatAgentProfileResolver>();
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
        readModelDescriptors.Should().HaveCount(18);
        readModelDescriptors.Should()
            .ContainSingle(static descriptor => descriptor.Name == "workflow-external-approval-continuation");
        readModelDescriptors.Should()
            .ContainSingle(static descriptor => descriptor.Name == "user-agent-api-key-revocation");
        readModelDescriptors.Should()
            .ContainSingle(static descriptor => descriptor.Name == "managed-codex-credential");
        readModelDescriptors.Should()
            .ContainSingle(static descriptor => descriptor.Name == "streaming-proxy-chat-session");
        readModelDescriptors.Should()
            .NotContain(static descriptor => descriptor.Name == "script-native-document");
        readModelDescriptors.Should()
            .NotContain(static descriptor => descriptor.Name.Contains("audit", StringComparison.OrdinalIgnoreCase));
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
        app.Services.GetRequiredService<IProjectionDocumentReader<ManagedCodexCredentialDocument, string>>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<IManagedCodexCredentialLifecycle>().Should().NotBeNull();
        var managedCodexPort = app.Services.GetServices<ICodexExecutionPort>()
            .Should()
            .ContainSingle(static port =>
                port.TargetKind == CodexExecutionTarget.TargetOneofCase.ManagedSandbox)
            .Which;
        managedCodexPort.Should().BeOfType<ManagedCodexExecutionCoordinator>();
        app.Services.GetServices<IHealthProbeExecutor>()
            .Select(static executor => executor.Kind)
            .Should()
            .Contain(["aevatar_core_loop", "audit_query_index"]);

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
        routePatterns.Should().NotContain("/api/skill-runners/{agentId}/external-trigger-sources/{sourceId}/deliveries");
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
        app.Services.GetRequiredService<IAgentToolExecutionPort>().Should().NotBeNull();

        await app.StopAsync();
    }

    [Fact]
    public void AddAevatarMainnetHost_WithAdditionalNyxIdServices_ShouldComposeConfiguredMinimumSet()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:AdditionalRequiredServiceSlugs:0"] = "github-api",
            ["Aevatar:NyxId:AdditionalRequiredServiceSlugs:1"] = "lark-api",
        });
        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var brokerOptions = app.Services.GetRequiredService<IOptions<NyxIdBrokerOptions>>().Value;

        brokerOptions.AdditionalRequiredServiceSlugs.Should().Equal(
            "github-api",
            "lark-api",
            OrnnOptions.DefaultNyxIdSlug,
            NyxIdToolOptions.DefaultSandboxServiceSlug);
    }

    [Fact]
    public void AddAevatarMainnetHost_WithInvalidAdditionalNyxIdServiceSlug_ShouldFailStartupValidation()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:AdditionalRequiredServiceSlugs:0"] = "Invalid/Service",
        });
        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var act = () => app.Services.GetRequiredService<IStartupValidator>().Validate();

        act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*AdditionalRequiredServiceSlugs[0]*1-80 character NyxID service slug*");
    }

    [Fact]
    public void AddAevatarMainnetHost_WithLlmResourcePolicyDrift_ShouldFailStartupValidation()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:DefaultRoute"] = "llm-provider-route",
        });
        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.Services.PostConfigure<NyxIdBrokerOptions>(options =>
            options.RequiredLlmServiceSlug = "drifted-authorization-route");

        using var app = builder.Build();
        var act = () => app.Services.GetRequiredService<IStartupValidator>().Validate();

        act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*RequiredLlmServiceSlug*llm-provider-route*Aevatar:NyxId:DefaultRoute*");
    }

    [Fact]
    public void AddAevatarMainnetHost_WithoutOrnnProviderResource_ShouldFailStartupValidation()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();
        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.Services.PostConfigure<NyxIdBrokerOptions>(options =>
            options.AdditionalRequiredServiceSlugs = [NyxIdToolOptions.DefaultSandboxServiceSlug]);

        using var app = builder.Build();
        var act = () => app.Services.GetRequiredService<IStartupValidator>().Validate();

        act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage($"*AdditionalRequiredServiceSlugs*{OrnnOptions.DefaultNyxIdSlug}*Aevatar:Ornn:NyxIdSlug*");
    }

    [Fact]
    public void AddAevatarMainnetHost_WithoutSandboxProviderResource_ShouldFailStartupValidation()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();
        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.Services.PostConfigure<NyxIdBrokerOptions>(options =>
            options.AdditionalRequiredServiceSlugs = [OrnnOptions.DefaultNyxIdSlug]);

        using var app = builder.Build();
        var act = () => app.Services.GetRequiredService<IStartupValidator>().Validate();

        act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage(
                $"*AdditionalRequiredServiceSlugs*{NyxIdToolOptions.DefaultSandboxServiceSlug}*" +
                "Aevatar:NyxId:SandboxServiceSlug*");
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
    public void MapAevatarMainnetHost_ShouldOwnTheSingleChatPostRoute()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();
        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.MapAevatarMainnetHost();

        var chatPosts = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint =>
                endpoint.RoutePattern.RawText == "/api/chat" &&
                endpoint.Metadata
                    .OfType<HttpMethodMetadata>()
                    .Single()
                    .HttpMethods
                    .Contains(HttpMethods.Post, StringComparer.OrdinalIgnoreCase))
            .ToList();

        chatPosts.Should().ContainSingle();
        chatPosts.Single().Metadata.GetMetadata<IEndpointNameMetadata>()
            ?.EndpointName.Should().Be("StartMainnetChat");

        var publicConversationRoutes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith(
                    "/api/chat/conversations",
                    StringComparison.Ordinal) == true)
            .Select(static endpoint => new
            {
                Route = endpoint.RoutePattern.RawText,
                Methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods,
            })
            .ToList();

        publicConversationRoutes.Should().ContainSingle(route =>
            route.Route == "/api/chat/conversations" &&
            route.Methods.Contains(HttpMethods.Get));
        publicConversationRoutes.Should().ContainSingle(route =>
            route.Route == "/api/chat/conversations/{conversationId}" &&
            route.Methods.Contains(HttpMethods.Get));
        publicConversationRoutes.Should().ContainSingle(route =>
            route.Route == "/api/chat/conversations/{conversationId}/state" &&
            route.Methods.Contains(HttpMethods.Get));
        publicConversationRoutes.Should().ContainSingle(route =>
            route.Route == "/api/chat/conversations/{conversationId}" &&
            route.Methods.Contains(HttpMethods.Delete));
        publicConversationRoutes.Should().OnlyContain(route =>
            !route.Route!.Contains("scopeId", StringComparison.OrdinalIgnoreCase));
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
            AgentProfilePolicies.NyxIdChatRouteToolSet,
            ToolSetNames.LarkSelfNotify,
            ToolSetNames.NyxIdConnectedServices,
            ToolSetNames.WorkspaceDefault);

        var workspace = registry.Resolve(ToolSetNames.WorkspaceDefault);
        workspace.IsSuccess.Should().BeTrue(workspace.Error?.Message);
        workspace.Sources.Should().Contain(source => source is InvokeGAgentToolSource);
        workspace.Sources.Should().Contain(source => source is InvokeTeamToolSource);
        workspace.Sources.Should().Contain(source => source is InvokeMemberToolSource);
        workspace.Sources.Should().Contain(source => source is StartWorkflowToolSource);
        workspace.Sources.Should().Contain(source => source is ObserveRunToolSource);
        workspace.Sources.Should().Contain(source => source is ReadWorkflowRunArtifactToolSource);
        workspace.Sources.Should().Contain(source => source is ProvisionWorkflowScheduleToolSource);
        workspace.Sources.Should().Contain(source => source is CreateStudioTeamToolSource);
        workspace.Sources.Should().Contain(source => source is StudioTeamQueryToolSource);
        workspace.Sources.Should().Contain(source => source is CreateStudioMemberToolSource);
        workspace.Sources.Should().Contain(source => source is CreateStudioMemberWorkflowDraftToolSource);
        workspace.Sources.Should().Contain(source => source is StudioMemberQueryToolSource);
        workspace.Sources.Should().Contain(source => source is StudioScheduleQueryToolSource);
        workspace.Sources.Should().Contain(source => source is StudioWorkflowQueryToolSource);
        workspace.Sources.Should().Contain(source => source is WorkflowCatalogAgentToolSource);
        workspace.Sources.Should().Contain(source => source is BindStudioMemberWorkflowToolSource);
        workspace.Sources.Should().Contain(source => source is ScheduleStudioMemberWorkflowToolSource);
        workspace.Sources.Should().Contain(source => source.GetType().Name == "ResponsesAevatarToolProvider");
        workspace.Sources.Should().Contain(source => source is ChannelInteractiveReplyToolSource);
        workspace.Sources.Should().Contain(source => source is ChannelRegistrationToolSource);
        workspace.Sources.Should().Contain(source => source is AgentDeliveryTargetToolSource);
        workspace.Sources.Should().Contain(source => source is NyxIdAgentToolSource);
        workspace.Sources.Should().NotContain(source => source is NyxIdConnectedServiceInventoryToolSource);
        workspace.Sources.Should().Contain(source => source is LarkAgentToolSource);
        workspace.Sources.Should().Contain(source => source is TelegramAgentToolSource);
        workspace.Sources.Should().Contain(source => source is ChronoStorageAgentToolSource);
        workspace.Sources.Should().Contain(source => source is WebAgentToolSource);
        workspace.Sources.Should().Contain(source => source is SkillsAgentToolSource);
        workspace.Sources.Should().Contain(source => source is OrnnAgentToolSource);
        app.Services.GetServices<IAgentToolSource>()
            .Select(static source => source.GetType())
            .Should()
            .NotContain(typeof(NyxIdConnectedServiceInventoryToolSource));
        app.Services.GetRequiredService<NyxIdConnectedServiceInventoryToolSource>()
            .Should()
            .NotBeNull();
        var channelInventorySource = app.Services
            .GetRequiredService<ChannelNyxIdConnectedServiceInventoryToolSource>();
        app.Services.GetRequiredService<ChannelNyxIdConnectedServiceInventoryToolSource>()
            .Should()
            .BeSameAs(channelInventorySource);
        var replyGenerator = app.Services.GetRequiredService<IConversationReplyGenerator>();
        var channelToolSources = replyGenerator.GetType()
            .GetField("_toolSources", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(replyGenerator)
            .Should()
            .BeAssignableTo<IReadOnlyList<IAgentToolSource>>()
            .Subject;
        channelToolSources.Should().ContainSingle(source =>
                source is ChannelNyxIdConnectedServiceInventoryToolSource)
            .Which.Should()
            .BeSameAs(channelInventorySource);
        var scheduleQueries = app.Services.GetRequiredService<IStudioMemberAutomationQueryPort>();
        var scheduleMutations = app.Services.GetRequiredService<IStudioMemberWorkflowSchedulePort>();
        scheduleQueries.Should().BeSameAs(scheduleMutations);
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

        var larkSelfNotify = registry.Resolve(ToolSetNames.LarkSelfNotify);
        larkSelfNotify.IsSuccess.Should().BeTrue(larkSelfNotify.Error?.Message);
        larkSelfNotify.Sources.Select(static source => source.GetType()).Should()
            .Equal(workspace.Sources.Select(static source => source.GetType()));
        larkSelfNotify.Sources.Should().Contain(source => source is LarkAgentToolSource);
        larkSelfNotify.Sources.Should().Contain(source => source is NyxIdAgentToolSource);

        var nyxIdConnectedServices = registry.Resolve(ToolSetNames.NyxIdConnectedServices);
        nyxIdConnectedServices.IsSuccess.Should().BeTrue(nyxIdConnectedServices.Error?.Message);
        nyxIdConnectedServices.Sources.Should().ContainSingle(source => source is NyxIdConnectedServiceToolSource);
        workspace.Sources.Should().NotContain(source => source is NyxIdConnectedServiceToolSource);

        var nyxIdChatProfile = registry.Resolve(AgentProfilePolicies.NyxIdChatRouteToolSet);
        nyxIdChatProfile.IsSuccess.Should().BeTrue(nyxIdChatProfile.Error?.Message);
        nyxIdChatProfile.Sources.Should().Contain(source => source is NyxIdAgentToolSource);
        nyxIdChatProfile.Sources.Should().ContainSingle(source =>
            source is NyxIdConnectedServiceToolSource);

        var voice = registry.Resolve("voice.realtime");
        voice.IsSuccess.Should().BeFalse();
        voice.Error!.Code.Should().Be(ToolSetResolveError.UnknownNameCode);

        var unknown = registry.Resolve("missing.set");
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
    public void AddAevatarMainnetHost_ShouldBindNyxIdRequestDurationCeiling()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:MaxRequestDurationSeconds"] = "420",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.Services.GetRequiredService<NyxIdToolOptions>()
            .MaxRequestDurationSeconds.Should().Be(420);
    }

    [Theory]
    [InlineData(NyxIdManagedWorkflowAdmissionMode.Shadow)]
    [InlineData(NyxIdManagedWorkflowAdmissionMode.Enforce)]
    public void AddAevatarMainnetHost_ShouldBindNyxIdAdmissionMode(
        NyxIdManagedWorkflowAdmissionMode mode)
    {
        using var home = new TemporaryAevatarHomeScope();
        using var admissionMode = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__ManagedWorkflowAdmissionMode",
            mode.ToString());
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.Services.GetRequiredService<NyxIdToolOptions>()
            .ManagedWorkflowAdmissionMode.Should().Be(mode);
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldEnforceNyxIdAdmissionInDistributedImageConfiguration()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(environmentName: "Distributed");

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        app.Services.GetRequiredService<NyxIdToolOptions>()
            .ManagedWorkflowAdmissionMode.Should().Be(NyxIdManagedWorkflowAdmissionMode.Enforce);
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
    public void AddAevatarMainnetHost_WithoutAuditTrailAppender_ShouldFailDuringBuild()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.Services.RemoveAll<IAuditTrailAppender>();

        var act = () => builder.Build();

        act.Should()
            .Throw<Exception>()
            .WithMessage("*IAuditTrailAppender*");
    }

    [Fact]
    public void AddAevatarMainnetHost_WithoutAuditActorIdentityHasher_ShouldFailDuringBuild()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });
        builder.Services.RemoveAll<IAuditActorIdentityHasher>();

        var act = () => builder.Build();

        act.Should()
            .Throw<Exception>()
            .WithMessage("*IAuditActorIdentityHasher*");
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
    public void AddAevatarMainnetHost_ShouldReconcileProjectionIndicesBeforeStartupReaders()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Projection:Document:Providers:InMemory:Enabled"] = "false",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://127.0.0.1:9200",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        var hostedServices = builder.Services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToList();
        var reconcileIndex = hostedServices.FindIndex(static descriptor =>
            descriptor.ImplementationType == typeof(ElasticsearchProjectionIndexReconcileHostedService));
        var revocationMigrationIndex = hostedServices.FindIndex(static descriptor =>
            descriptor.ImplementationType?.Name == "UserAgentApiKeyRevocationReadModelKeyMigrationService");

        reconcileIndex.Should().BeGreaterThanOrEqualTo(0);
        revocationMigrationIndex.Should().BeGreaterThan(reconcileIndex);
        hostedServices.Should().ContainSingle(static descriptor =>
            descriptor.ImplementationType == typeof(ElasticsearchProjectionIndexReconcileHostedService));

        using var app = builder.Build();
        app.Services.GetServices<IProjectionIndexReconcileTarget>()
            .Should()
            .ContainSingle(static target => target.IndexAlias.EndsWith("-audit-trail-current", StringComparison.Ordinal));
        app.Services.GetServices<IProjectionIndexReconcileTarget>()
            .Should()
            .ContainSingle(static target => target.IndexAlias.EndsWith(
                "-health-probe-operational-snapshots",
                StringComparison.Ordinal));
        app.Services.GetRequiredService<IHealthProbeOperationalSnapshotStore>()
            .GetType().Name.Should().Be("ElasticsearchHealthProbeOperationalSnapshotStore");
        app.Services.GetServices<IProjectionReadModelDescriptor>()
            .Should()
            .NotContain(static descriptor => descriptor.Name.Contains("audit", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAevatarMainnetHost_ShouldUseOperatorChannelIdentityElasticsearchAclAttestation(
        bool configuredAttestation)
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [$"{AevatarOAuthClientEsAclOptions.SectionName}:GrantMatchesGrainEventStoreInternal"] =
                configuredAttestation.ToString(),
            [$"{AevatarOAuthClientEsAclOptions.SectionName}:GrantDescription"] =
                "operator supplied ACL attestation",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        using var app = builder.Build();
        var aclOptions = app.Services.GetRequiredService<IOptions<AevatarOAuthClientEsAclOptions>>().Value;

        aclOptions.GrantMatchesGrainEventStoreInternal.Should().Be(configuredAttestation);
        aclOptions.GrantDescription.Should().Be("operator supplied ACL attestation");
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
    [InlineData(" ", " https://nyx-api.example.test ", "https://nyx-api.example.test")]
    [InlineData("urn:custom:aevatar-api", "https://nyx-api.example.test", "urn:custom:aevatar-api")]
    public void AddAevatarMainnetHost_ShouldUseNyxIdApiAudienceWhenDeploymentOmitsIt(
        string configuredAudience,
        string nyxIdApiBaseUrl,
        string expectedAudience)
    {
        using var home = new TemporaryAevatarHomeScope();
        using var audience = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__Authentication__Audience",
            configuredAudience);
        using var apiBaseUrl = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__ApiBaseUrl",
            nyxIdApiBaseUrl);
        var audienceKey = $"{AevatarAuthenticationOptions.SectionName}:Audience";
        var builder = CreateBuilder(environmentName: Environments.Production);

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        builder.Configuration[audienceKey].Should().Be(expectedAudience);
    }

    [Fact]
    public void AddAevatarMainnetHost_WhenAudienceAndNyxIdApiBaseUrlAreMissingInProduction_ShouldFailClosed()
    {
        using var home = new TemporaryAevatarHomeScope();
        using var audience = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__Authentication__Audience",
            " ");
        using var apiBaseUrl = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__ApiBaseUrl",
            " ");
        var builder = CreateBuilder(environmentName: Environments.Production);

        var act = () => builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "Aevatar:Authentication:Audience is required when authentication is enabled outside Development.");
    }

    [Theory]
    [InlineData("Development", typeof(InMemoryAgentToolAdmissionLedger))]
    [InlineData("Testing", typeof(InMemoryAgentToolAdmissionLedger))]
    [InlineData("Production", typeof(DistributedAgentToolAdmissionLedger))]
    public void AddAevatarMainnetHost_ShouldSelectAdmissionLedgerByEnvironment(
        string environmentName,
        System.Type expectedLedgerType)
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(environmentName: environmentName);

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        var descriptor = builder.Services.Last(service =>
            service.ServiceType == typeof(IAgentToolAdmissionLedger));
        descriptor.ImplementationType.Should().Be(expectedLedgerType);
    }

    [Fact]
    public void AddAevatarMainnetHost_ShouldOwnConfiguredAdmissionReplayLifetime()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(environmentName: Environments.Production);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [MainnetHostBuilderExtensions.AgentToolAdmissionMaximumRequestLifetimeKey] = "06:00:00",
            [MainnetHostBuilderExtensions.AgentToolAdmissionFutureClockSkewKey] = "00:02:00",
        });

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        var descriptor = builder.Services.Last(service =>
            service.ServiceType == typeof(AgentToolAdmissionPolicy));
        descriptor.ImplementationInstance.Should().Be(new AgentToolAdmissionPolicy(
            TimeSpan.FromHours(6),
            TimeSpan.FromMinutes(2)));
    }

    [Theory]
    [InlineData(null, "aevatar:mainnet:agent-tool-admission:v1:")]
    [InlineData("aevatar:mainnet-test:agent-tool-admission:v2:", "aevatar:mainnet-test:agent-tool-admission:v2:")]
    public void AddAevatarMainnetHost_ShouldOwnAdmissionLedgerKeyPrefix(
        string? configuredPrefix,
        string expectedPrefix)
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder(environmentName: Environments.Production);
        if (configuredPrefix is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [MainnetHostBuilderExtensions.AgentToolAdmissionKeyPrefixKey] = configuredPrefix,
            });
        }

        builder.AddAevatarMainnetHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        var descriptor = builder.Services.Last(service =>
            service.ServiceType == typeof(AgentToolAdmissionLedgerOptions));
        descriptor.ImplementationInstance.Should().Be(new AgentToolAdmissionLedgerOptions(expectedPrefix));
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class NyxIdAssistantActionRegistryHandler : HttpMessageHandler
    {
        private const string RegistryJson = """
            {
              "schema_version": 4,
              "revision": "nyxid-assistant-actions.v4",
              "actions": [
                {
                  "action": "service.connect",
                  "description": "Connect a service through the NyxID browser journey.",
                  "params_schema": {
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
                              "requestedScopes": {
                                "type": "array",
                                "items": {"type": "string"}
                              },
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
                  },
                  "risk": "grant",
                  "tier": "v1",
                  "remember_eligible": true
                }
              ]
            }
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get ||
                request.RequestUri?.AbsolutePath != "/api/v1/assistant/actions" ||
                request.Headers.Authorization is not null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(RegistryJson),
            });
        }
    }

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
