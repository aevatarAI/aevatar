using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Bootstrap.Hosting;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeDraftRunWorkflowActorCurrentStateIntegrationTests
{
    [Fact]
    public async Task DraftRunEndpoint_ShouldExposeCompletedWorkflowActorCurrentStateViaWorkflowActorCurrentState()
    {
        await using var host = await DraftRunWorkflowActorCurrentStateHost.StartAsync();
        using var response = await host.Client.PostAsJsonAsync($"/api/scopes/{host.ScopeId}/workflow/draft-run", new
        {
            prompt = "  z\nz\ny  ",
            workflowYamls = MultilevelWorkflowYamls,
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "draft-run body: {0}", body);
        body.Should().Contain("aevatar.run.context");

        var actorId = ExtractRunContextActorId(body);
        actorId.Should().NotBeNullOrWhiteSpace();

        var snapshot = await WaitForCompletedWorkflowActorCurrentStateAsync(host.Client, actorId!);

        snapshot.ActorId.Should().Be(actorId);
        snapshot.CompletionStatus.Should().Be(WorkflowRunCompletionStatus.Completed);
        snapshot.LastSuccess.Should().BeTrue();
        snapshot.LastOutput.Should().Be("y\nz");
        snapshot.LastError.Should().BeEmpty();
        snapshot.RequestedSteps.Should().Be(0);
        snapshot.CompletedSteps.Should().Be(0);
    }

    private static string? ExtractRunContextActorId(string sseBody)
    {
        foreach (var line in sseBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            using var json = JsonDocument.Parse(line["data: ".Length..]);
            if (!json.RootElement.TryGetProperty("custom", out var custom))
                continue;

            if (!custom.TryGetProperty("name", out var nameElement) ||
                !string.Equals(nameElement.GetString(), "aevatar.run.context", StringComparison.Ordinal))
            {
                continue;
            }

            if (!custom.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("actorId", out var actorIdElement))
            {
                continue;
            }

            return actorIdElement.GetString();
        }

        return null;
    }

    private static async Task<WorkflowActorCurrentStateHttpResponse> WaitForCompletedWorkflowActorCurrentStateAsync(
        HttpClient client,
        string actorId)
    {
        using var timeoutCts = new CancellationTokenSource(QueryVisibilityTimeout);
        var path = $"/api/workflow-actors/{Uri.EscapeDataString(actorId)}/current-state";
        WorkflowActorCurrentStateHttpResponse? lastSnapshot = null;
        string? lastBody = null;
        HttpStatusCode? lastStatus = null;

        try
        {
            while (true)
            {
                using var snapshotResponse = await client.GetAsync(path, timeoutCts.Token);
                lastStatus = snapshotResponse.StatusCode;
                lastBody = await snapshotResponse.Content.ReadAsStringAsync(timeoutCts.Token);

                if (snapshotResponse.StatusCode == HttpStatusCode.OK)
                {
                    lastSnapshot = JsonSerializer.Deserialize<WorkflowActorCurrentStateHttpResponse>(
                        lastBody,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));

                    if (lastSnapshot?.CompletionStatus == WorkflowRunCompletionStatus.Completed)
                        return lastSnapshot;
                }
                else if (snapshotResponse.StatusCode != HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(
                        $"Workflow actor current-state query failed. status={(int)snapshotResponse.StatusCode} body={lastBody}");
                }

                await Task.Delay(QueryVisibilityPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Workflow actor current-state did not reach Completed before timeout. actor_id={actorId} last_status={lastStatus?.ToString() ?? "<none>"} last_completion={lastSnapshot?.CompletionStatus.ToString() ?? "<none>"} last_body={lastBody ?? "<none>"}");
        }
    }

    private static readonly TimeSpan QueryVisibilityTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan QueryVisibilityPollInterval = TimeSpan.FromMilliseconds(100);

    private sealed class DraftRunWorkflowActorCurrentStateHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private DraftRunWorkflowActorCurrentStateHost(WebApplication app, HttpClient client, string repoRoot, string scopeId)
        {
            _app = app;
            Client = client;
            RepoRoot = repoRoot;
            ScopeId = scopeId;
        }

        public HttpClient Client { get; }

        public string RepoRoot { get; }

        public string ScopeId { get; }

        public static async Task<DraftRunWorkflowActorCurrentStateHost> StartAsync()
        {
            var repoRoot = FindRepoRoot();
            const string scopeId = "scope-a";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = repoRoot,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GAgentService:Demo:Enabled"] = "false",
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
                ["Projection:Policies:Environment"] = "Development",
            });
            builder.Services.AddNyxIdTools(options =>
            {
                options.BaseUrl = "https://nyx.example.test";
            });
            builder.AddAevatarDefaultHost(options =>
            {
                options.AllowLocalFileSecretsStore = false;
                options.ServiceName = "Aevatar.ScopeDraftRunWorkflowActorCurrentState.Tests";
                options.EnableConnectorBootstrap = false;
                options.EnableHealthEndpoints = false;
                options.MapRootHealthEndpoint = false;
                options.EnableOpenApiDocument = false;
            });
            builder.AddAevatarPlatform(options =>
            {
                options.EnableScriptingCapability = false;
            });
            builder.AddGAgentServiceCapabilityBundle();
            builder.Services.Configure<WorkflowDefinitionFileSourceOptions>(options =>
            {
                options.WorkflowDirectories.Clear();
                options.WorkflowDirectories.Add(Path.Combine(repoRoot, "workflows"));
            });
            builder.Services.AddSingleton<IAuditTrailAppender, AppendedAuditTrail>();
            builder.Services.AddSingleton<IAuditActorIdentityHasher, StableAuditActorIdentityHasher>();
            builder.Services.AddSingleton<InMemoryGAgentActorStore>();
            builder.Services.AddSingleton<IGAgentActorRegistryCommandPort>(sp => sp.GetRequiredService<InMemoryGAgentActorStore>());
            builder.Services.AddSingleton<IGAgentActorRegistryQueryPort>(sp => sp.GetRequiredService<InMemoryGAgentActorStore>());
            builder.Services.AddSingleton<IScopeResourceAdmissionPort>(sp => sp.GetRequiredService<InMemoryGAgentActorStore>());
            builder.Services.AddSingleton<ITeamEntryMemberResolver, DraftRunWorkflowActorCurrentStateTeamEntryMemberResolver>();
            DraftRunProjectionActivationServiceCollectionExtensions.AddWorkflowRunProjectionActivatingInteractionService(
                builder.Services);
            builder.Services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseAevatarDefaultHost();
            app.Use(async (http, next) =>
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("scope_id", scopeId),
                ], "Test"));
                await next();
            });
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

            return new DraftRunWorkflowActorCurrentStateHost(app, client, repoRoot, scopeId);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Unable to locate repository root from test base directory.");
        }
    }

    private sealed class DraftRunWorkflowActorCurrentStateTeamEntryMemberResolver : ITeamEntryMemberResolver
    {
        public Task<TeamEntryMemberResolution> ResolveAsync(
            string scopeId,
            string teamId,
            string endpointId,
            CancellationToken ct = default) =>
            throw new TeamEntryMemberResolutionException(
                TeamEntryMemberErrorCodes.TeamNotFound,
                scopeId,
                teamId,
                $"team '{teamId}' is not configured for the draft-run workflow actor current-state query fixture.");
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(
                record.AuditId,
                record.AuditActorId,
                record.OccurredAt.ToDateTimeOffset()));
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"hashed:{canonicalActorKey}", "kid-test");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == $"hashed:{canonicalActorKey}" && identityKeyId == "kid-test";
    }

    private static class DraftRunProjectionActivationServiceCollectionExtensions
    {
        public static IServiceCollection AddWorkflowRunProjectionActivatingInteractionService(
            IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Singleton<IWorkflowExecutionProjectionPort>(sp =>
                new ActivatingWorkflowExecutionProjectionPort(
                    sp.GetRequiredService<WorkflowExecutionProjectionPort>(),
                    sp.GetRequiredService<IProjectionScopeActivationService<WorkflowExecutionRuntimeLease>>())));
            return services;
        }
    }

    private sealed class ActivatingWorkflowExecutionProjectionPort(
        IWorkflowExecutionProjectionPort inner,
        IProjectionScopeActivationService<WorkflowExecutionRuntimeLease> activationService)
        : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => inner.ProjectionEnabled;

        public async Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = await activationService.EnsureAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = rootActorId,
                    ProjectionKind = "workflow-execution-session",
                    Mode = ProjectionRuntimeMode.SessionObservation,
                    SessionId = commandId,
                },
                ct);

            return await inner.AttachExistingActorProjectionAsync(rootActorId, commandId, sink, ct);
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowExecutionProjectionLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            inner.AttachLiveSinkAsync(lease, sink, ct);

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default) =>
            inner.DetachLiveSinkAsync(liveSinkLease, ct);

        public Task ReleaseActorProjectionAsync(
            IWorkflowExecutionProjectionLease lease,
            CancellationToken ct = default) =>
            inner.ReleaseActorProjectionAsync(lease, ct);
    }

    private static readonly string[] MultilevelWorkflowYamls =
    [
        """
        name: workflow_call_multilevel
        description: Parent workflow calls nested sub-workflows (L1 -> L2 -> L3), then formats final output.

        steps:
          - id: call_level1
            type: workflow_call
            parameters:
              workflow: "subworkflow_level1"

          - id: format_final_output
            type: transform
            parameters:
              op: "join"
              separator: " | "
        """,
        """
        name: subworkflow_level1
        description: Level 1 sub-workflow calls level 2, then reverses line order.

        steps:
          - id: call_level2
            type: workflow_call
            parameters:
              workflow: "subworkflow_level2"

          - id: reverse_lines_level1
            type: transform
            parameters:
              op: "reverse_lines"
        """,
        """
        name: subworkflow_level2
        description: Level 2 sub-workflow calls level 3, then deduplicates lines.

        steps:
          - id: call_level3
            type: workflow_call
            parameters:
              workflow: "subworkflow_level3"

          - id: distinct_level2
            type: transform
            parameters:
              op: "distinct"
        """,
        """
        name: subworkflow_level3
        description: Level 3 sub-workflow normalizes the input text.

        steps:
          - id: trim_level3
            type: transform
            parameters:
              op: "trim"
        """,
    ];

    private sealed class InMemoryGAgentActorStore :
        IGAgentActorRegistryCommandPort,
        IGAgentActorRegistryQueryPort,
        IScopeResourceAdmissionPort
    {
        private readonly List<ActorRegistration> _registrations = [];

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistrySnapshot(
                scopeId,
                BuildGroups(_registrations.Where(registration =>
                    string.Equals(registration.ScopeId, scopeId, StringComparison.Ordinal))),
                0,
                DateTimeOffset.MinValue,
                DateTimeOffset.UtcNow));

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            _registrations.Add(new ActorRegistration(registration.ScopeId, registration.AgentKind, registration.ActorId));
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration target,
            CancellationToken cancellationToken = default)
        {
            _registrations.RemoveAll(registration =>
                string.Equals(registration.ScopeId, target.ScopeId, StringComparison.Ordinal) &&
                string.Equals(registration.AgentKind, target.AgentKind, StringComparison.Ordinal) &&
                string.Equals(registration.ActorId, target.ActorId, StringComparison.Ordinal));
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                target,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            var exists = _registrations.Any(registration =>
                string.Equals(registration.ScopeId, target.ScopeId, StringComparison.Ordinal) &&
                string.Equals(registration.AgentKind, target.AgentKind, StringComparison.Ordinal) &&
                string.Equals(registration.ActorId, target.ActorId, StringComparison.Ordinal));
            return Task.FromResult(exists
                ? ScopeResourceAdmissionResult.Allowed()
                : ScopeResourceAdmissionResult.NotFound());
        }

        private static IReadOnlyList<GAgentActorGroup> BuildGroups(IEnumerable<ActorRegistration> registrations) =>
            registrations
                .GroupBy(static registration => registration.AgentKind, StringComparer.Ordinal)
                .Select(static group => new GAgentActorGroup(
                    group.Key,
                    group.Select(static registration => registration.ActorId).ToArray()))
                .ToArray();

        private sealed record ActorRegistration(string ScopeId, string AgentKind, string ActorId);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity("Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
