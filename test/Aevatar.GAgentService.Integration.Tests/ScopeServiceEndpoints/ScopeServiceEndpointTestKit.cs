using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Audit;
using Aevatar.Bootstrap.Hosting;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

public abstract class ScopeServiceEndpointTestKit
{
    protected static ServiceCatalogSnapshot BuildService(string scopeId, string serviceId, string primaryActorId) =>
        new(
            $"{scopeId}:default:default:{serviceId}",
            scopeId,
            "default",
            "default",
            serviceId,
            serviceId,
            "rev-1",
            "rev-1",
            "dep-1",
            primaryActorId,
            "Active",
            [],
            [],
            DateTimeOffset.UtcNow);

    protected static ServiceDeploymentCatalogSnapshot BuildDeployments(
        string serviceKey,
        string deploymentId,
        string revisionId,
        string primaryActorId) =>
        new(
            serviceKey,
            [
                new ServiceDeploymentSnapshot(
                    deploymentId,
                    revisionId,
                    primaryActorId,
                    "Active",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);

    protected static ServiceInvocationCatalogSnapshot BuildScopeInvocationCatalog(
        string serviceKey,
        string endpointId,
        ServiceInvokeReadinessStatus status,
        ServiceInvokeUnavailableReason reason,
        string revisionId,
        string deploymentId,
        string actorId,
        long stateVersion) =>
        new(
            serviceKey,
            [
                new ServiceInvokeReadinessSnapshot(
                    serviceKey,
                    endpointId,
                    status,
                    reason,
                    revisionId,
                    deploymentId,
                    actorId,
                    DateTimeOffset.Parse("2026-03-14T00:05:00+00:00"),
                    stateVersion,
                    $"evt-{stateVersion}",
                    stateVersion - 2,
                    stateVersion - 1,
                    stateVersion),
            ],
            DateTimeOffset.Parse("2026-03-14T00:05:00+00:00"),
            stateVersion,
            $"evt-{stateVersion}",
            stateVersion - 2,
            stateVersion - 1,
            stateVersion);

    protected static HttpRequestMessage CreateAuthenticatedJsonRequest(
        HttpMethod method,
        string requestUri,
        object body,
        params string[] claimedScopeIds)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        foreach (var claimedScopeId in claimedScopeIds)
        {
            request.Headers.Add("X-Test-Scope-Id", claimedScopeId);
        }

        return request;
    }

    protected static HttpRequestMessage CreateUnauthenticatedJsonRequest(
        HttpMethod method,
        string requestUri,
        object body)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Test-Authenticated", "false");
        return request;
    }

    protected static ByteString ScopeBuildProtocolDescriptorSetFor(MessageDescriptor descriptor)
    {
        var fds = new FileDescriptorSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ScopeCollectFileProto(descriptor.File, fds, seen);
        return fds.ToByteString();
    }

    private static void ScopeCollectFileProto(FileDescriptor file, FileDescriptorSet fds, HashSet<string> seen)
    {
        if (!seen.Add(file.Name))
            return;
        foreach (var dep in file.Dependencies)
        {
            ScopeCollectFileProto(dep, fds, seen);
        }

        fds.File.Add(FileDescriptorProto.Parser.ParseFrom(file.SerializedData));
    }

    protected static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(ScopeServiceEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        return (T)method.Invoke(null, args)!;
    }

    protected static void InvokePrivateStaticVoid(string methodName, params object?[] args)
    {
        var method = typeof(ScopeServiceEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        method.Invoke(null, args);
    }

    protected static async Task InvokePrivateStaticTask(string methodName, params object?[] args)
    {
        var method = typeof(ScopeServiceEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        var result = method.Invoke(null, args);
        switch (result)
        {
            case Task task:
                await task;
                return;
            case ValueTask valueTask:
                await valueTask;
                return;
            default:
                throw new InvalidOperationException($"Unexpected return type for {methodName}.");
        }
    }

    protected static async Task<T> InvokePrivateStaticTask<T>(string methodName, params object?[] args)
    {
        var method = typeof(ScopeServiceEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        var result = method.Invoke(null, args);
        return result switch
        {
            Task<T> task => await task,
            ValueTask<T> valueTask => await valueTask,
            _ => throw new InvalidOperationException($"Unexpected return type for {methodName}."),
        };
    }

    protected static async Task<(HttpStatusCode StatusCode, string Body)> ExecutePrivateResultAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return ((HttpStatusCode)context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    protected sealed class ScopeServiceEndpointTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScopeServiceEndpointTestHost(
            WebApplication app,
            HttpClient client,
            RecordingServiceGovernanceCommandPort commandPort,
            RecordingServiceGovernanceQueryPort queryPort,
            RecordingScopeBindingCommandPort scopeBindingPort,
            RecordingServiceCommandPort serviceCommandPort,
            RecordingServiceInvocationPort invocationPort,
            RecordingServiceLifecycleQueryPort lifecycleQueryPort,
            RecordingServiceServingQueryPort servingQueryPort,
            FakeServiceCatalogQueryReader serviceCatalogReader,
            FakeServiceTrafficViewQueryReader trafficViewReader,
            FakeServiceInvocationCatalogQueryReader invocationCatalogReader,
            FakeServiceRevisionCatalogQueryReader revisionCatalog,
            FakeMemberPublishedServiceResolver memberPublishedServiceResolver,
            FakeTeamEntryMemberResolver teamEntryMemberResolver,
            FakeCommandInteractionService interactionService,
            FakeWorkflowDefinitionParser workflowDefinitionParser,
            FakeStaticGAgentStreamInvocationPort staticGAgentStreamInvocationPort,
            FakeWorkflowExecutionQueryApplicationService workflowQueryService,
            FakeWorkflowRunBindingReader runBindingReader,
            RecordingResumeDispatchService resumeDispatchService,
            RecordingSignalDispatchService signalDispatchService,
            RecordingStopDispatchService stopDispatchService,
            RecordingRetryCompensationDispatchService retryCompensationDispatchService,
            RecordingServiceRunRegistrationPort serviceRunRegistrationPort,
            FakeServiceRunQueryPort serviceRunQueryPort,
            RecordingWorkflowFileIngressPort workflowFileIngressPort,
            RecordingAuditTrailAppender auditTrailAppender)
        {
            _app = app;
            Client = client;
            CommandPort = commandPort;
            QueryPort = queryPort;
            ScopeBindingPort = scopeBindingPort;
            ServiceCommandPort = serviceCommandPort;
            InvocationPort = invocationPort;
            LifecycleQueryPort = lifecycleQueryPort;
            ServingQueryPort = servingQueryPort;
            ServiceCatalogReader = serviceCatalogReader;
            TrafficViewReader = trafficViewReader;
            InvocationCatalogReader = invocationCatalogReader;
            RevisionCatalog = revisionCatalog;
            MemberPublishedServiceResolver = memberPublishedServiceResolver;
            TeamEntryMemberResolver = teamEntryMemberResolver;
            InteractionService = interactionService;
            WorkflowDefinitionParser = workflowDefinitionParser;
            StaticGAgentStreamInvocationPort = staticGAgentStreamInvocationPort;
            WorkflowQueryService = workflowQueryService;
            RunBindingReader = runBindingReader;
            ResumeDispatchService = resumeDispatchService;
            SignalDispatchService = signalDispatchService;
            StopDispatchService = stopDispatchService;
            RetryCompensationDispatchService = retryCompensationDispatchService;
            ServiceRunRegistrationPort = serviceRunRegistrationPort;
            ServiceRunQueryPort = serviceRunQueryPort;
            WorkflowFileIngressPort = workflowFileIngressPort;
            AuditTrailAppender = auditTrailAppender;
        }

        public HttpClient Client { get; }

        public IReadOnlyList<string> RoutePatterns => ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        public RecordingServiceGovernanceCommandPort CommandPort { get; }

        public RecordingServiceGovernanceQueryPort QueryPort { get; }

        public RecordingScopeBindingCommandPort ScopeBindingPort { get; }

        public RecordingServiceCommandPort ServiceCommandPort { get; }

        public RecordingServiceInvocationPort InvocationPort { get; }

        public RecordingServiceLifecycleQueryPort LifecycleQueryPort { get; }

        public RecordingServiceServingQueryPort ServingQueryPort { get; }

        public FakeServiceCatalogQueryReader ServiceCatalogReader { get; }

        public FakeServiceTrafficViewQueryReader TrafficViewReader { get; }

        public FakeServiceInvocationCatalogQueryReader InvocationCatalogReader { get; }

        public FakeServiceRevisionCatalogQueryReader RevisionCatalog { get; }

        public FakeMemberPublishedServiceResolver MemberPublishedServiceResolver { get; }

        public FakeTeamEntryMemberResolver TeamEntryMemberResolver { get; }

        public FakeCommandInteractionService InteractionService { get; }

        public FakeWorkflowDefinitionParser WorkflowDefinitionParser { get; }

        public FakeStaticGAgentStreamInvocationPort StaticGAgentStreamInvocationPort { get; }

        public FakeWorkflowExecutionQueryApplicationService WorkflowQueryService { get; }

        public FakeWorkflowRunBindingReader RunBindingReader { get; }

        public RecordingResumeDispatchService ResumeDispatchService { get; }

        public RecordingSignalDispatchService SignalDispatchService { get; }

        public RecordingStopDispatchService StopDispatchService { get; }

        public RecordingRetryCompensationDispatchService RetryCompensationDispatchService { get; }

        public RecordingServiceRunRegistrationPort ServiceRunRegistrationPort { get; }

        public FakeServiceRunQueryPort ServiceRunQueryPort { get; }

        public RecordingWorkflowFileIngressPort WorkflowFileIngressPort { get; }

        public RecordingAuditTrailAppender AuditTrailAppender { get; }

        public static async Task<ScopeServiceEndpointTestHost> StartAsync(
            bool authenticationEnabled = true,
            IUserConfigQueryPort? userConfigQueryPort = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Aevatar:Authentication:Enabled"] = authenticationEnabled ? "true" : "false";

            var commandPort = new RecordingServiceGovernanceCommandPort();
            var queryPort = new RecordingServiceGovernanceQueryPort();
            var scopeBindingPort = new RecordingScopeBindingCommandPort();
            var serviceCommandPort = new RecordingServiceCommandPort();
            var invocationPort = new RecordingServiceInvocationPort();
            var lifecycleQueryPort = new RecordingServiceLifecycleQueryPort();
            var servingQueryPort = new RecordingServiceServingQueryPort();
            var serviceCatalogReader = new FakeServiceCatalogQueryReader();
            var trafficViewReader = new FakeServiceTrafficViewQueryReader();
            var invocationCatalogReader = new FakeServiceInvocationCatalogQueryReader(serviceCatalogReader, trafficViewReader);
            var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
            var memberPublishedServiceResolver = new FakeMemberPublishedServiceResolver();
            var teamEntryMemberResolver = new FakeTeamEntryMemberResolver();
            var workflowDefinitionParser = new FakeWorkflowDefinitionParser();
            var interactionService = new FakeCommandInteractionService(workflowDefinitionParser);
            var gagentDraftRunInteractionService = new FakeGAgentDraftRunInteractionService();
            var scriptServiceRunInteractionService = new FakeScriptServiceRunInteractionService();
            var staticGAgentStreamInvocationPort = new FakeStaticGAgentStreamInvocationPort(
                gagentDraftRunInteractionService);
            var workflowQueryService = new FakeWorkflowExecutionQueryApplicationService();
            var runBindingReader = new FakeWorkflowRunBindingReader();
            var resumeDispatchService = new RecordingResumeDispatchService();
            var signalDispatchService = new RecordingSignalDispatchService();
            var stopDispatchService = new RecordingStopDispatchService();
            var retryCompensationDispatchService = new RecordingRetryCompensationDispatchService();
            var actorRuntime = new NoOpActorRuntime();
            var eventSubscriptionProvider = new NoOpActorEventSubscriptionProvider();
            var serviceRunQueryPort = new FakeServiceRunQueryPort
            {
                WorkflowBindingFallback = runBindingReader,
                DeploymentResolver = binding =>
                {
                    var deployment = lifecycleQueryPort.Deployments?.Deployments.FirstOrDefault(d =>
                        string.Equals(d.PrimaryActorId, binding.EffectiveDefinitionActorId, StringComparison.Ordinal));
                    return (deployment?.DeploymentId ?? string.Empty, deployment?.RevisionId ?? string.Empty);
                },
            };
            var serviceRunRegistrationPort = new RecordingServiceRunRegistrationPort
            {
                LinkedQueryPort = serviceRunQueryPort,
            };
            var workflowFileIngressPort = new RecordingWorkflowFileIngressPort();
            var auditTrailAppender = new RecordingAuditTrailAppender();
            builder.Services.AddSingleton<IServiceGovernanceCommandPort>(commandPort);
            builder.Services.AddSingleton<IServiceGovernanceQueryPort>(queryPort);
            builder.Services.AddSingleton<IScopeBindingCommandPort>(scopeBindingPort);
            builder.Services.AddSingleton<IServiceCommandPort>(serviceCommandPort);
            builder.Services.AddSingleton<IServiceInvocationPort>(invocationPort);
            builder.Services.AddSingleton<IServiceLifecycleQueryPort>(lifecycleQueryPort);
            builder.Services.AddSingleton<IServiceServingQueryPort>(servingQueryPort);
            builder.Services.AddSingleton<IMemberPublishedServiceResolver>(memberPublishedServiceResolver);
            builder.Services.AddSingleton<IServiceCatalogQueryReader>(serviceCatalogReader);
            builder.Services.AddSingleton<IServiceTrafficViewQueryReader>(trafficViewReader);
            builder.Services.AddSingleton<IServiceInvocationCatalogQueryReader>(invocationCatalogReader);
            builder.Services.AddSingleton<IServiceRevisionCatalogQueryReader>(revisionCatalog);
            builder.Services.AddSingleton<ITeamEntryMemberResolver>(teamEntryMemberResolver);
            builder.Services.AddSingleton<ServiceInvocationResolutionService>();
            builder.Services.AddSingleton<ServiceInvokeReadinessErrorMapper>();
            builder.Services.AddSingleton<IInvokeAdmissionAuthorizer, AllowAllInvokeAdmissionAuthorizer>();
            builder.Services.AddSingleton<IWorkflowDefinitionParser>(workflowDefinitionParser);
            builder.Services.AddSingleton<IWorkflowChatRunInteractionPort>(interactionService);
            builder.Services.AddSingleton<IGAgentDraftRunInteractionPort>(gagentDraftRunInteractionService);
            builder.Services.AddSingleton<ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>>(scriptServiceRunInteractionService);
            builder.Services.AddSingleton<IStaticGAgentStreamInvocationPort<AGUIEvent>>(staticGAgentStreamInvocationPort);
            builder.Services.AddSingleton<IWorkflowExecutionQueryApplicationService>(workflowQueryService);
            builder.Services.AddSingleton<IWorkflowRunBindingReader>(runBindingReader);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(resumeDispatchService);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(signalDispatchService);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(stopDispatchService);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(retryCompensationDispatchService);
            builder.Services.AddSingleton<IActorRuntime>(actorRuntime);
            builder.Services.AddSingleton<IActorEventSubscriptionProvider>(eventSubscriptionProvider);
            builder.Services.AddSingleton<IServiceRunRegistrationPort>(serviceRunRegistrationPort);
            builder.Services.AddSingleton<IServiceRunQueryPort>(serviceRunQueryPort);
            builder.Services.AddSingleton<IFileArtifactIngressPort>(workflowFileIngressPort);
            builder.Services.AddSingleton<IAuditTrailAppender>(auditTrailAppender);
            builder.Services.AddSingleton<IAuditActorIdentityHasher>(new StableAuditActorIdentityHasher());
            builder.Services.AddSingleton<WorkflowMultipartFileInputParser>();
            builder.Services.AddSingleton(Options.Create(new WorkflowMultipartFileIngressOptions()));
            builder.Services.AddSingleton(Options.Create(new WorkflowFormFileIngressOptions()));
            if (userConfigQueryPort != null)
                builder.Services.AddSingleton(userConfigQueryPort);
            builder.Services.AddSingleton<IOptions<ScopeWorkflowCapabilityOptions>>(
                Options.Create(new ScopeWorkflowCapabilityOptions
                {
                    DefaultServiceId = "default",
                    ServiceAppId = "default",
                    ServiceNamespace = "default",
                }));
            builder.Services.AddAuthorization();
            if (authenticationEnabled)
            {
                builder.Services.AddAuthentication("Test")
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            }

            var app = builder.Build();
            app.UseRouting();
            if (authenticationEnabled)
            {
                app.UseAuthentication();
                app.Use(async (http, next) =>
                {
                    var hasExplicitAuthenticationHeader = http.Request.Headers.TryGetValue("X-Test-Authenticated", out var authenticatedValues);
                    var shouldAuthenticate = !hasExplicitAuthenticationHeader ||
                        (bool.TryParse(authenticatedValues, out var authenticated) && authenticated);
                    if (shouldAuthenticate)
                    {
                        var claims = new List<Claim>();
                        if (http.Request.Headers.TryGetValue("X-Test-Scope-Id", out var claimedScopeValues))
                        {
                            var claimedScopeIds = claimedScopeValues
                                .ToString()
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            foreach (var claimedScopeId in claimedScopeIds)
                            {
                                claims.Add(new Claim(WorkflowRunCommandMetadataKeys.ScopeId, claimedScopeId));
                            }
                        }
                        else if (!hasExplicitAuthenticationHeader &&
                            TryGetRequestedScopeId(http.Request.Path.Value, out var requestedScopeId))
                        {
                            claims.Add(new Claim(WorkflowRunCommandMetadataKeys.ScopeId, requestedScopeId));
                        }

                        if (http.Request.Headers.TryGetValue("X-Test-Member-Id", out var claimedMemberValues))
                        {
                            var claimedMemberIds = claimedMemberValues
                                .ToString()
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            foreach (var claimedMemberId in claimedMemberIds)
                            {
                                claims.Add(new Claim("member_id", claimedMemberId));
                            }
                        }
                        else if (!hasExplicitAuthenticationHeader &&
                            TryGetRequestedMemberId(http.Request.Path.Value, out var requestedMemberId))
                        {
                            claims.Add(new Claim("member_id", requestedMemberId));
                        }

                        if (http.Request.Headers.TryGetValue("X-Test-Role", out var claimedRoleValues))
                        {
                            var claimedRoles = claimedRoleValues
                                .ToString()
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            foreach (var claimedRole in claimedRoles)
                            {
                                claims.Add(new Claim("role", claimedRole));
                            }
                        }

                        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
                    }

                    await next();
                });
            }
            app.UseMiddleware<EndpointAuditCaptureMiddleware>();
            app.UseAuthorization();
            app.MapScopeServiceEndpoints();
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new ScopeServiceEndpointTestHost(
                app,
                client,
                commandPort,
                queryPort,
                scopeBindingPort,
                serviceCommandPort,
                invocationPort,
                lifecycleQueryPort,
                servingQueryPort,
                serviceCatalogReader,
                trafficViewReader,
                invocationCatalogReader,
                revisionCatalog,
                memberPublishedServiceResolver,
                teamEntryMemberResolver,
                interactionService,
                workflowDefinitionParser,
                staticGAgentStreamInvocationPort,
                workflowQueryService,
                runBindingReader,
                resumeDispatchService,
                signalDispatchService,
                stopDispatchService,
                retryCompensationDispatchService,
                serviceRunRegistrationPort,
                serviceRunQueryPort,
                workflowFileIngressPort,
                auditTrailAppender);
        }

        private static bool TryGetRequestedScopeId(string? path, out string scopeId)
        {
            var segments = path?
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments is { Length: >= 3 } &&
                string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[1], "scopes", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(segments[2]))
            {
                scopeId = segments[2];
                return true;
            }

            scopeId = string.Empty;
            return false;
        }

        private static bool TryGetRequestedMemberId(string? path, out string memberId)
        {
            var segments = path?
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments is { Length: >= 5 } &&
                string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[1], "scopes", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[3], "members", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(segments[4]))
            {
                memberId = segments[4];
                return true;
            }

            memberId = string.Empty;
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    protected sealed class RecordingScopeBindingCommandPort : IScopeBindingCommandPort
    {
        public ScopeBindingUpsertRequest? LastRequest { get; private set; }

        public Task<ScopeBindingUpsertResult> UpsertAsync(ScopeBindingUpsertRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ScopeBindingUpsertResult(
                request.ScopeId,
                "default",
                request.DisplayName?.Trim() ?? "main",
                request.RevisionId?.Trim() ?? "rev-1",
                request.ImplementationKind,
                "scope-binding:expected-actor",
                WorkflowName: request.Workflow?.WorkflowYamls.FirstOrDefault() is { } firstWorkflowYaml && firstWorkflowYaml.Contains("name:", StringComparison.Ordinal)
                    ? "main"
                    : string.Empty,
                DefinitionActorIdPrefix: request.ImplementationKind == ScopeBindingImplementationKind.Workflow
                    ? "scope-workflow:scope-a:default"
                    : string.Empty,
                Workflow: request.ImplementationKind == ScopeBindingImplementationKind.Workflow
                    ? new ScopeBindingWorkflowResult("main", "scope-workflow:scope-a:default")
                    : null,
                Script: request.Script == null
                    ? null
                    : new ScopeBindingScriptResult(
                        request.Script.ScriptId,
                        request.Script.ScriptRevision ?? "script-rev-1",
                        "definition-script-1"),
                GAgent: request.GAgent == null
                    ? null
                    : new ScopeBindingGAgentResult(
                        request.GAgent.AgentKind)));
        }
    }

    protected sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(
                record.AuditId,
                record.AuditActorId,
                record.OccurredAt.ToDateTimeOffset()));
        }
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey)
        {
            return new AuditActorIdentity($"hashed:{canonicalActorKey}", "kid-test");
        }

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId)
        {
            return auditActorId == $"hashed:{canonicalActorKey}" &&
                   identityKeyId == "kid-test";
        }
    }

    protected sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        public RetireServiceRevisionCommand? RetireRevisionCommand { get; private set; }

        public SetDefaultServingRevisionCommand? SetDefaultServingCommand { get; private set; }

        public ActivateServiceRevisionCommand? ActivateRevisionCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            SetDefaultServingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("service-actor", "cmd-default-serving", "corr-default-serving"));
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            ActivateRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("service-actor", "cmd-activate", "corr-activate"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default)
        {
            RetireRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("service-actor", "cmd-retire", "corr-retire"));
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    protected sealed class RecordingServiceGovernanceCommandPort : IServiceGovernanceCommandPort
    {
        public CreateServiceBindingCommand? CreateBindingCommand { get; private set; }

        public UpdateServiceBindingCommand? UpdateBindingCommand { get; private set; }

        public RetireServiceBindingCommand? RetireBindingCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(CreateServiceBindingCommand command, CancellationToken ct = default)
        {
            CreateBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-create-binding", "corr-create-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(UpdateServiceBindingCommand command, CancellationToken ct = default)
        {
            UpdateBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-update-binding", "corr-update-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(RetireServiceBindingCommand command, CancellationToken ct = default)
        {
            RetireBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-retire-binding", "corr-retire-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(CreateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(UpdateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(CreateServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(UpdateServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(RetireServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    protected sealed class RecordingServiceGovernanceQueryPort : IServiceGovernanceQueryPort
    {
        public ServiceIdentity? LastBindingsIdentity { get; private set; }

        public ServiceBindingCatalogSnapshot? BindingsResult { get; set; }

        public Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastBindingsIdentity = identity;
            return Task.FromResult(BindingsResult);
        }

        public Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    protected sealed class RecordingServiceRunRegistrationPort : IServiceRunRegistrationPort
    {
        public List<ServiceRunRecord> RegisterCalls { get; } = [];
        public List<(string runActorId, string runId, ServiceRunStatus status, string lastOutput, string lastError)> StatusCalls { get; } = [];

        public FakeServiceRunQueryPort? LinkedQueryPort { get; set; }

        public Task<ServiceRunRegistrationResult> RegisterAsync(ServiceRunRecord record, CancellationToken ct = default)
        {
            RegisterCalls.Add(record.Clone());
            LinkedQueryPort?.Upsert(BuildSnapshot(record));
            return Task.FromResult(new ServiceRunRegistrationResult($"service-run:{record.ScopeId}:{record.ServiceId}:{record.RunId}", record.RunId));
        }

        public Task UpdateStatusAsync(string runActorId, string runId, ServiceRunStatus status, CancellationToken ct = default) =>
            UpdateStatusAsync(runActorId, runId, status, null, null, ct);

        public Task UpdateStatusAsync(
            string runActorId,
            string runId,
            ServiceRunStatus status,
            string? lastOutput,
            string? lastError,
            CancellationToken ct = default)
        {
            StatusCalls.Add((runActorId, runId, status, lastOutput ?? string.Empty, lastError ?? string.Empty));
            LinkedQueryPort?.UpdateStatus(runActorId, runId, status, lastOutput, lastError);
            return Task.CompletedTask;
        }

        private static ServiceRunSnapshot BuildSnapshot(ServiceRunRecord record) =>
            new(
                record.ScopeId,
                record.ServiceId,
                record.ServiceKey,
                record.RunId,
                record.CommandId,
                record.CorrelationId,
                record.EndpointId,
                record.ScheduleId ?? string.Empty,
                record.ImplementationKind,
                record.TargetActorId,
                record.RevisionId,
                record.DeploymentId,
                record.Status,
                $"service-run:{record.ScopeId}:{record.ServiceId}:{record.RunId}",
                record.Identity?.TenantId ?? string.Empty,
                record.Identity?.AppId ?? string.Empty,
                record.Identity?.Namespace ?? string.Empty,
                StateVersion: 1,
                LastEventId: $"{record.RunId}:registered",
                CreatedAt: record.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
                UpdatedAt: record.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
                LastOutput: record.LastOutput ?? string.Empty,
                LastError: record.LastError ?? string.Empty);
    }

    protected sealed class FakeServiceRunQueryPort : IServiceRunQueryPort
    {
        private readonly List<ServiceRunSnapshot> _snapshots = [];

        // Bridge to existing FakeWorkflowRunBindingReader fixtures so tests that pre-populate
        // workflow run bindings also see the runs through the new IServiceRunQueryPort surface.
        public FakeWorkflowRunBindingReader? WorkflowBindingFallback { get; set; }

        // Optional resolver that maps a workflow run binding to (deploymentId, revisionId) so the
        // bridged snapshot mirrors what production projector would write from the dispatcher.
        public Func<WorkflowActorBinding, (string DeploymentId, string RevisionId)>? DeploymentResolver { get; set; }

        public IReadOnlyList<ServiceRunSnapshot> Snapshots => _snapshots;

        public List<ServiceRunQuery> Queries { get; } = [];

        public void Upsert(ServiceRunSnapshot snapshot)
        {
            _snapshots.RemoveAll(x =>
                string.Equals(x.ScopeId, snapshot.ScopeId, StringComparison.Ordinal) &&
                string.Equals(x.ServiceId, snapshot.ServiceId, StringComparison.Ordinal) &&
                string.Equals(x.RunId, snapshot.RunId, StringComparison.Ordinal));
            _snapshots.Add(snapshot);
        }

        public void UpdateStatus(
            string runActorId,
            string runId,
            ServiceRunStatus status,
            string? lastOutput,
            string? lastError)
        {
            var index = _snapshots.FindIndex(x =>
                string.Equals(x.ActorId, runActorId, StringComparison.Ordinal) &&
                string.Equals(x.RunId, runId, StringComparison.Ordinal));
            if (index < 0)
                return;

            var current = _snapshots[index];
            _snapshots[index] = current with
            {
                Status = status,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastOutput = lastOutput ?? current.LastOutput,
                LastError = lastError ?? current.LastError,
                StateVersion = current.StateVersion + 1,
                LastEventId = $"{current.RunId}:status:{(int)status}",
            };
        }

        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(ServiceRunQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            var bridged = MaterializeForQuery(query.ScopeId, query.ServiceId).ToList();
            IEnumerable<ServiceRunSnapshot> results = bridged;
            if (!string.IsNullOrWhiteSpace(query.ScopeId))
                results = results.Where(s => string.Equals(s.ScopeId, query.ScopeId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(query.ServiceId))
                results = results.Where(s => string.Equals(s.ServiceId, query.ServiceId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(query.ScheduleId))
                results = results.Where(s => string.Equals(s.ScheduleId, query.ScheduleId, StringComparison.Ordinal));
            if (query.Status.HasValue)
                results = results.Where(s => s.Status == query.Status.Value);
            if (query.UpdatedFrom.HasValue)
                results = results.Where(s => s.UpdatedAt >= query.UpdatedFrom.Value);
            if (query.UpdatedTo.HasValue)
                results = results.Where(s => s.UpdatedAt <= query.UpdatedTo.Value);
            return Task.FromResult<IReadOnlyList<ServiceRunSnapshot>>(
                results.OrderByDescending(s => s.UpdatedAt).Take(query.Take).ToList());
        }

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(string scopeId, string serviceId, string runId, CancellationToken ct = default) =>
            Task.FromResult(MaterializeForQuery(scopeId, serviceId).FirstOrDefault(s =>
                string.Equals(s.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(s.ServiceId, serviceId, StringComparison.Ordinal) &&
                string.Equals(s.RunId, runId, StringComparison.Ordinal)));

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(string scopeId, string serviceId, string commandId, CancellationToken ct = default) =>
            Task.FromResult(MaterializeForQuery(scopeId, serviceId).FirstOrDefault(s =>
                string.Equals(s.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(s.ServiceId, serviceId, StringComparison.Ordinal) &&
                string.Equals(s.CommandId, commandId, StringComparison.Ordinal)));

        // Materializes snapshots, treating any workflow binding fixtures as belonging to the queried service
        // (workflow bindings predate the service-run registry and don't carry serviceId in the test fixtures).
        private IEnumerable<ServiceRunSnapshot> MaterializeForQuery(string scopeId, string serviceId)
        {
            foreach (var snapshot in _snapshots)
                yield return snapshot;
            if (WorkflowBindingFallback != null)
            {
                foreach (var binding in WorkflowBindingFallback.AllBindings())
                {
                    if (_snapshots.Any(s => string.Equals(s.RunId, binding.RunId, StringComparison.Ordinal) &&
                                            string.Equals(s.ServiceId, serviceId, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    var (deploymentId, revisionId) = DeploymentResolver?.Invoke(binding) ?? (string.Empty, string.Empty);
                    yield return BuildSnapshotFromBinding(binding, scopeId, serviceId, deploymentId, revisionId);
                }
            }
        }

        private static ServiceRunSnapshot BuildSnapshotFromBinding(
            WorkflowActorBinding binding,
            string scopeId,
            string serviceId,
            string deploymentId,
            string revisionId) =>
            new(
                ScopeId: string.IsNullOrWhiteSpace(scopeId) ? binding.ScopeId ?? string.Empty : scopeId,
                ServiceId: serviceId ?? string.Empty,
                ServiceKey: string.Empty,
                RunId: binding.RunId,
                CommandId: binding.RunId,
                CorrelationId: binding.RunId,
                EndpointId: string.Empty,
                ScheduleId: string.Empty,
                ImplementationKind: ServiceImplementationKind.Workflow,
                TargetActorId: binding.ActorId,
                RevisionId: revisionId,
                DeploymentId: deploymentId,
                Status: ServiceRunStatus.Accepted,
                ActorId: binding.ActorId,
                TenantId: binding.ScopeId ?? string.Empty,
                AppId: string.Empty,
                Namespace: string.Empty,
                StateVersion: binding.SourceVersion,
                LastEventId: binding.SourceEventId ?? string.Empty,
                CreatedAt: binding.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt: binding.UpdatedAt ?? DateTimeOffset.UtcNow,
                LastOutput: string.Empty,
                LastError: string.Empty);
    }

    protected sealed class FakeMemberPublishedServiceResolver : IMemberPublishedServiceResolver
    {
        private readonly DefaultMemberPublishedServiceResolver _fallback = new();

        public List<MemberPublishedServiceResolveRequest> Calls { get; } = [];

        public MemberPublishedServiceResolution? Result { get; set; }

        public Exception? Exception { get; set; }

        public Task<MemberPublishedServiceResolution> ResolveAsync(
            MemberPublishedServiceResolveRequest request,
            CancellationToken ct = default)
        {
            Calls.Add(request);
            if (Exception != null)
                throw Exception;

            return Result == null
                ? _fallback.ResolveAsync(request, ct)
                : Task.FromResult(Result);
        }
    }

    protected sealed class FakeTeamEntryMemberResolver : ITeamEntryMemberResolver
    {
        public List<(string ScopeId, string TeamId, string EndpointId)> Calls { get; } = [];

        public TeamEntryMemberResolution Result { get; set; } =
            new("scope-a", "team-a", "member-a", "member-a");

        public TeamEntryMemberResolutionException? Exception { get; set; }

        public Task<TeamEntryMemberResolution> ResolveAsync(
            string scopeId,
            string teamId,
            string endpointId,
            CancellationToken ct = default)
        {
            Calls.Add((scopeId, teamId, endpointId));
            if (Exception != null)
                throw Exception;

            return Task.FromResult(Result);
        }
    }

    protected sealed class RecordingServiceInvocationPort : IServiceInvocationPort
    {
        public ServiceInvocationRequest? LastRequest { get; private set; }

        public Func<ServiceInvocationRequest, Exception?>? ExceptionFactory { get; set; }

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var exception = ExceptionFactory?.Invoke(request);
            if (exception != null)
                throw exception;
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                DeploymentId = "dep-1",
                TargetActorId = "actor-1",
                CommandId = "cmd-1",
                CorrelationId = "corr-1",
                RunId = "run-1",
            });
        }
    }

    protected sealed class RecordingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public ServiceCatalogSnapshot? Service { get; set; }

        public ServiceRevisionCatalogSnapshot? Revisions { get; set; }

        public ServiceDeploymentCatalogSnapshot? Deployments { get; set; }

        public IReadOnlyList<ServiceCatalogSnapshot> Services { get; set; } = [];

        public ServiceIdentity? LastServiceIdentity { get; private set; }

        public ServiceIdentity? LastRevisionsIdentity { get; private set; }

        public ServiceIdentity? LastDeploymentsIdentity { get; private set; }

        public string? LastListTenantId { get; private set; }

        public string? LastListAppId { get; private set; }

        public string? LastListNamespace { get; private set; }

        public int LastListTake { get; private set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastServiceIdentity = identity;
            return Task.FromResult(Service);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default)
        {
            LastListTenantId = tenantId;
            LastListAppId = appId;
            LastListNamespace = @namespace;
            LastListTake = take;
            return Task.FromResult(Services);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastRevisionsIdentity = identity;
            return Task.FromResult(Revisions);
        }

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastDeploymentsIdentity = identity;
            return Task.FromResult(Deployments);
        }
    }

    protected sealed class RecordingServiceServingQueryPort : IServiceServingQueryPort
    {
        public ServiceServingSetSnapshot? ServingSet { get; set; }

        public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(ServingSet);

        public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
            ServiceIdentity identity,
            string commandId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    protected sealed class FakeServiceCatalogQueryReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? Service { get; set; }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Service);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(Service == null ? [] : [Service]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(Service == null ? [] : [Service]);
    }

    protected sealed class FakeServiceTrafficViewQueryReader : IServiceTrafficViewQueryReader
    {
        public ServiceTrafficViewSnapshot? View { get; set; }

        public Task<ServiceTrafficViewSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(View);
    }

    protected sealed class FakeServiceInvocationCatalogQueryReader(
        FakeServiceCatalogQueryReader catalogReader,
        FakeServiceTrafficViewQueryReader trafficViewReader) : IServiceInvocationCatalogQueryReader
    {
        public Dictionary<string, ServiceInvocationCatalogSnapshot?> CatalogsByServiceKey { get; } = new(StringComparer.Ordinal);

        public ServiceInvocationCatalogSnapshot? Catalog { get; set; }

        public Task<ServiceInvocationCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            if (Catalog != null)
                return Task.FromResult<ServiceInvocationCatalogSnapshot?>(Catalog);

            var serviceKey = ServiceKeys.Build(identity);
            if (CatalogsByServiceKey.TryGetValue(serviceKey, out var configuredCatalog))
                return Task.FromResult(configuredCatalog);

            var service = catalogReader.Service;
            var trafficEndpoint = trafficViewReader.View?.Endpoints.FirstOrDefault();
            var target = trafficEndpoint?.Targets.FirstOrDefault(x =>
                string.Equals(x.ServingState, ServiceServingState.Active.ToString(), StringComparison.Ordinal) &&
                x.AllocationWeight > 0);
            if (service == null && target == null)
                return Task.FromResult<ServiceInvocationCatalogSnapshot?>(null);

            return Task.FromResult<ServiceInvocationCatalogSnapshot?>(new ServiceInvocationCatalogSnapshot(
                serviceKey,
                [
                    new ServiceInvokeReadinessSnapshot(
                        serviceKey,
                        trafficEndpoint?.EndpointId ?? service?.Endpoints.FirstOrDefault()?.EndpointId ?? "chat",
                        ServiceInvokeReadinessStatus.Ready,
                        ServiceInvokeUnavailableReason.Unspecified,
                        target?.RevisionId ?? service?.ActiveServingRevisionId ?? "rev-active",
                        target?.DeploymentId ?? service?.DeploymentId ?? "dep-1",
                        target?.PrimaryActorId ?? service?.PrimaryActorId ?? "actor-1",
                        DateTimeOffset.UtcNow,
                        1,
                        $"{serviceKey}:invocation-catalog:1",
                        1,
                        1,
                        1),
                ],
                DateTimeOffset.UtcNow,
                1,
                $"{serviceKey}:invocation-catalog:1",
                1,
                1,
                1));
        }
    }

    protected sealed class FakeServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
    {
        private readonly Dictionary<string, PreparedServiceRevisionArtifact> _revisionCatalog = new(StringComparer.Ordinal);

        public Task UpsertRevisionAsync(string serviceKey, string revisionId, PreparedServiceRevisionArtifact artifact, CancellationToken ct = default)
        {
            var clone = artifact.Clone();
            clone.RevisionId = revisionId;
            _revisionCatalog[$"{serviceKey}:{revisionId}"] = clone;
            return Task.CompletedTask;
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            var serviceKey = ServiceKeys.Build(identity);
            var revisions = _revisionCatalog
                .Where(x => x.Key.StartsWith(serviceKey + ":", StringComparison.Ordinal))
                .Select(x => x.Value)
                .Select(artifact => new ServiceRevisionSnapshot(
                    artifact.RevisionId,
                    artifact.ImplementationKind.ToString(),
                    ServiceRevisionStatus.Prepared.ToString(),
                    artifact.ArtifactHash,
                    string.Empty,
                    artifact.Endpoints.Select(endpoint => new ServiceEndpointSnapshot(
                        endpoint.EndpointId,
                        endpoint.DisplayName,
                        endpoint.Kind.ToString(),
                        endpoint.RequestTypeUrl,
                        endpoint.ResponseTypeUrl,
                        endpoint.Description)).ToList(),
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null,
                    artifact.Clone()))
                .ToList();

            return Task.FromResult<ServiceRevisionCatalogSnapshot?>(new ServiceRevisionCatalogSnapshot(
                serviceKey,
                revisions,
                DateTimeOffset.UtcNow,
                revisions.Count,
                string.Empty));
        }
    }

    protected sealed class FakeWorkflowRunBindingReader : IWorkflowRunBindingReader
    {
        public Dictionary<string, IReadOnlyList<WorkflowActorBinding>> BindingsByRunId { get; } =
            new(StringComparer.Ordinal);

        public List<WorkflowRunBindingQuery> Queries { get; } = [];

        public IEnumerable<WorkflowActorBinding> AllBindings() =>
            BindingsByRunId.Values.SelectMany(x => x);

        public Task<IReadOnlyList<WorkflowActorBinding>> ListByRunIdAsync(
            string runId,
            int take = 20,
            CancellationToken ct = default)
        {
            BindingsByRunId.TryGetValue(runId, out var bindings);
            return Task.FromResult<IReadOnlyList<WorkflowActorBinding>>(bindings ?? []);
        }

        public Task<IReadOnlyList<WorkflowActorBinding>> QueryAsync(
            WorkflowRunBindingQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            var definitionActorIds = new HashSet<string>(query.DefinitionActorIds, StringComparer.Ordinal);
            var bindings = BindingsByRunId.Values
                .SelectMany(x => x)
                .Where(x => x.ActorKind == WorkflowActorKind.Run)
                .Where(x => string.IsNullOrWhiteSpace(query.ScopeId) || string.Equals(x.ScopeId, query.ScopeId, StringComparison.Ordinal))
                .Where(x => definitionActorIds.Count == 0 || definitionActorIds.Contains(x.EffectiveDefinitionActorId))
                .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.ActorId, StringComparer.Ordinal)
                .Take(query.Take)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowActorBinding>>(bindings);
        }
    }

    protected sealed class FakeWorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;

        public Dictionary<string, WorkflowActorSnapshot> SnapshotsByActorId { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorkflowRunReport> ReportsByActorId { get; } = new(StringComparer.Ordinal);

        public List<string> SnapshotCalls { get; } = [];

        public List<string> ReportCalls { get; } = [];

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

        public IReadOnlyList<string> ListWorkflows() => [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowCatalogItem>>([]);

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult<WorkflowCatalogItemDetail?>(null);

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCapabilitiesDocument());

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            SnapshotCalls.Add(actorId);
            SnapshotsByActorId.TryGetValue(actorId, out var snapshot);
            return Task.FromResult<WorkflowActorSnapshot?>(snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string actorId, CancellationToken ct = default)
        {
            ReportCalls.Add(actorId);
            ReportsByActorId.TryGetValue(actorId, out var report);
            return Task.FromResult<WorkflowRunReport?>(report);
        }

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(string actorId, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunTimelineExportItem>>([]);

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(string actorId, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunGraphExportEdge>>([]);

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(string actorId, int depth = 2, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowRunGraphExportSubgraph());
    }

    protected sealed class FakeWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        public Dictionary<string, WorkflowYamlParseResult> ParseResults { get; } = new(StringComparer.Ordinal);

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ParseResults.TryGetValue(workflowYaml, out var result))
                return Task.FromResult(result);

            var workflowName = ResolveWorkflowName(workflowYaml);
            return Task.FromResult(WorkflowYamlParseResult.Success(
                string.IsNullOrWhiteSpace(workflowName) ? "main" : workflowName));
        }

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var workflowByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string entryWorkflowName = string.Empty;
            string entryWorkflowYaml = string.Empty;
            for (var i = 0; i < inlineWorkflowDocuments.Count; i++)
            {
                var document = inlineWorkflowDocuments[i];
                if (string.IsNullOrWhiteSpace(document.Yaml))
                    return WorkflowInlineYamlBundleParseResult.Invalid($"workflowYamls[{i}] is required.");

                var parseResult = await ParseWorkflowYamlAsync(document.Yaml, ct);
                if (!parseResult.Succeeded)
                    return WorkflowInlineYamlBundleParseResult.Invalid(parseResult.Error, parseResult.ExternalCapabilityReadiness);

                var workflowName = parseResult.WorkflowName.Trim();
                if (!workflowByName.TryAdd(workflowName, document.Yaml))
                    return WorkflowInlineYamlBundleParseResult.Invalid($"Duplicate workflow name '{workflowName}' in workflowYamls.");

                if (i == 0)
                {
                    entryWorkflowName = workflowName;
                    entryWorkflowYaml = document.Yaml;
                }
            }

            return WorkflowInlineYamlBundleParseResult.Success(entryWorkflowName, entryWorkflowYaml, workflowByName);
        }

        private static string ResolveWorkflowName(string workflowYaml) =>
            workflowYaml
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(static line => line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1]
                .Trim() ?? string.Empty;
    }

    protected sealed class FakeCommandInteractionService : IWorkflowChatRunInteractionPort
    {
        private readonly FakeWorkflowDefinitionParser _workflowDefinitionParser;

        public FakeCommandInteractionService(FakeWorkflowDefinitionParser workflowDefinitionParser)
        {
            _workflowDefinitionParser = workflowDefinitionParser;
            ResultFactory = DefaultResultFactoryAsync;
        }

        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public Func<WorkflowChatRunRequest, Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask>, Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<WorkflowChatRunInteractionResult>> ResultFactory { get; set; }

        public Task<WorkflowChatRunInteractionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return ResultFactory(request, emitAsync, onAcceptedAsync, ct);
        }

        private async Task<WorkflowChatRunInteractionResult> DefaultResultFactoryAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
            CancellationToken ct)
        {
            _ = emitAsync;
            _ = onAcceptedAsync;
            var documents = request.Source.InlineBundle?.YamlDocuments;
            if (documents is { Count: > 0 })
            {
                var parse = await _workflowDefinitionParser.ParseInlineWorkflowBundleAsync(documents, ct);
                if (!parse.Succeeded)
                {
                    return WorkflowChatRunInteractionResult.Failure(
                        WorkflowChatRunStartError.InvalidWorkflowYaml,
                        WorkflowChatRunStartFailureDetail.Create(
                            WorkflowChatRunStartError.InvalidWorkflowYaml,
                            parse.Error,
                            parse.ExternalCapabilityReadiness));
                }
            }

            return WorkflowChatRunInteractionResult.Failure(WorkflowChatRunStartError.AgentNotFound);
        }
    }

    protected sealed class FakeGAgentDraftRunInteractionService : IGAgentDraftRunInteractionPort
    {
        public GAgentDraftRunInteractionRequest? LastRequest { get; private set; }

        public Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
            GAgentDraftRunInteractionRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            _ = emitAsync;
            _ = onAcceptedAsync;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>
                    .Failure(GAgentDraftRunStartError.UnknownAgentKind));
        }
    }

    protected sealed class FakeStaticGAgentStreamInvocationPort(
        IGAgentDraftRunInteractionPort interactionService)
        : IStaticGAgentStreamInvocationPort<AGUIEvent>
    {
        public List<StaticGAgentStreamInvocationRequest> Requests { get; } = [];

        public Func<StaticGAgentStreamInvocationRequest, Func<AGUIEvent, CancellationToken, ValueTask>, Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<StaticGAgentStreamInvocationResult>>? ResultFactory { get; set; }

        public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StaticGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (ResultFactory != null)
                return await ResultFactory(request, emitAsync, onAcceptedAsync, ct);

            var input = request.Input;
            var result = await interactionService.ExecuteAsync(
                new GAgentDraftRunInteractionRequest(
                    ScopeId: request.Identity.TenantId,
                    AgentKind: "TestStaticGAgent",
                    Prompt: input.Prompt,
                    PreferredActorId: input.PreferredActorId,
                    SessionId: input.SessionId,
                    Headers: input.Headers,
                    InputParts: input.InputParts),
                emitAsync,
                async (receipt, token) =>
                {
                    if (onAcceptedAsync == null)
                        return;

                    var serviceReceipt = new ServiceInvocationAcceptedReceipt
                    {
                        CommandId = receipt.CommandId,
                        CorrelationId = receipt.CorrelationId,
                        TargetActorId = receipt.ActorId,
                        EndpointId = request.EndpointId,
                    };
                    await onAcceptedAsync(
                        new StaticGAgentStreamAcceptedReceipt(serviceReceipt, receipt),
                        token);
                },
                ct);

            return new StaticGAgentStreamInvocationResult(
                result.Receipt == null
                    ? null
                    : new StaticGAgentStreamAcceptedReceipt(
                        new ServiceInvocationAcceptedReceipt
                        {
                            CommandId = result.Receipt.CommandId,
                            CorrelationId = result.Receipt.CorrelationId,
                            TargetActorId = result.Receipt.ActorId,
                            EndpointId = request.EndpointId,
                        },
                        result.Receipt),
                result.Error,
                result.FinalizeResult?.Completion ?? GAgentDraftRunCompletionStatus.Unknown,
                result.FinalizeResult?.Completed ?? false);
        }
    }

    protected sealed class NoOpServiceRunRegistrationPort : IServiceRunRegistrationPort
    {
        public Task<ServiceRunRegistrationResult> RegisterAsync(ServiceRunRecord record, CancellationToken ct = default) =>
            Task.FromResult(new ServiceRunRegistrationResult($"service-run:{record.RunId}", record.RunId));

        public Task UpdateStatusAsync(string runActorId, string runId, ServiceRunStatus status, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    protected sealed class FakeScriptServiceRunInteractionService
        : ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>
    {
        public ScriptServiceRunStartError? StartError { get; init; }

        public async Task<CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> ExecuteAsync(
            ScriptServiceRunCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            if (StartError != null)
                return CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>.Failure(StartError);

            var receipt = new ScriptServiceRunAcceptedReceipt(
                command.RuntimeActorId,
                command.RunId,
                command.CommandId,
                command.CorrelationId);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<ScriptServiceRunCompletionStatus>(
                    ScriptServiceRunCompletionStatus.Incomplete,
                    false));
        }

        async Task<RealtimeSessionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>>
            IRealtimeSession<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>.ExecuteAsync(
                ScriptServiceRunCommand inbound,
                Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
                Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
        }
    }

    protected sealed class AllowAllInvokeAdmissionAuthorizer : IInvokeAdmissionAuthorizer
    {
        public Task AuthorizeAsync(
            string serviceKey,
            string deploymentId,
            PreparedServiceRevisionArtifact artifact,
            ServiceEndpointDescriptor endpoint,
            ServiceInvocationRequest request,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    protected sealed class NoOpActorEventSubscriptionProvider : IActorEventSubscriptionProvider
    {
        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, Google.Protobuf.IMessage, new()
        {
            _ = actorId;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new NoOpAsyncDisposable());
        }
    }

    protected sealed class NoOpActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IActor>(new NoOpActor(id ?? "noop-actor"));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _ = id;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(new NoOpActor(id));

        public Task<bool> ExistsAsync(string id)
        {
            _ = id;
            return Task.FromResult(true);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            _ = parentId;
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    protected sealed class MissingActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IActor>(new NoOpActor(id ?? "missing-actor"));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _ = id;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _ = id;
            return Task.FromResult<IActor?>(null);
        }

        public Task<bool> ExistsAsync(string id)
        {
            _ = id;
            return Task.FromResult(false);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            _ = parentId;
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    protected sealed class NoOpActor : IActor
    {
        public NoOpActor(string id)
        {
            Id = id;
            Agent = new NoOpAgent(id);
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    protected sealed class NoOpAgent : IAgent
    {
        public NoOpAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("noop");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    protected sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    protected sealed class StubUserConfigStore : IUserConfigQueryPort
    {
        private readonly UserConfig _config;

        public StubUserConfigStore(UserConfig config)
        {
            _config = config;
        }

        public Task<UserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default) => GetAsync(ct);
    }

    protected sealed class ThrowingUserConfigStore : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("config unavailable");

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default) =>
            GetAsync(ct);
    }

    protected sealed class RecordingResumeDispatchService
        : ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowResumeCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowResumeCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-resume", "corr-resume")));
        }
    }

    protected sealed class RecordingSignalDispatchService
        : ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowSignalCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowSignalCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-signal", "corr-signal")));
        }
    }

    protected sealed class RecordingStopDispatchService
        : ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowStopCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowStopCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-stop", "corr-stop")));
        }
    }

    protected sealed class RecordingRetryCompensationDispatchService
        : ICommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowRetryCompensationCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowRetryCompensationCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-retry-compensation", "corr-retry-compensation")));
        }
    }

    protected sealed class TestAuthHandler
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            // The custom middleware after UseAuthentication() overrides http.User.
            // This handler returns NoResult so it does not interfere.
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
        }
    }

    protected sealed class RecordingWorkflowFileIngressPort : IFileArtifactIngressPort
    {
        public List<FileArtifactIngressRequest> Requests { get; } = [];

        public ValueTask<FileArtifactIngressResult> IngestAsync(
            FileArtifactIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var index = Requests.Count;
            return ValueTask.FromResult(new FileArtifactIngressResult(new FileArtifactRef
            {
                FileId = $"file-{index}",
                ArtifactId = $"workflow-file://file-{index}",
                SourceKind = request.SourceKind,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = request.Content.Length,
                Sha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                CreatedAtUnixMs = 1710000000000,
                ExpiresAtUnixMs = 1710003600000,
                OwnerScopeId = request.OwnerScopeId,
            }));
        }
    }

    protected static MultipartFormDataContent CreateMultipartScopeStreamContent(
        string? payloadJson,
        IReadOnlyList<(string FileName, string ContentType, string Content)> files)
    {
        var content = new MultipartFormDataContent();
        if (payloadJson != null)
            content.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload");

        foreach (var file in files)
        {
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(file.Content));
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            content.Add(fileContent, "file", file.FileName);
        }

        return content;
    }
}
