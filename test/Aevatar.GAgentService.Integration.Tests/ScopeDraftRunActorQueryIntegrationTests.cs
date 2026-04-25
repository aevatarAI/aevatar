using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.Bootstrap.Hosting;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
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
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeDraftRunActorQueryIntegrationTests
{
    [Fact]
    public async Task DraftRunEndpoint_ShouldExposeCompletedActorSnapshotViaActorQuery()
    {
        await using var host = await DraftRunActorQueryHost.StartAsync();
        var workflowYamls = host.LoadWorkflowYamls(
        [
            "workflow_call_multilevel.yaml",
            "subworkflow_level1.yaml",
            "subworkflow_level2.yaml",
            "subworkflow_level3.yaml",
        ]);

        using var response = await host.Client.PostAsJsonAsync($"/api/scopes/{host.ScopeId}/workflow/draft-run", new
        {
            prompt = "  z\nz\ny  ",
            workflowYamls,
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "draft-run body: {0}", body);
        body.Should().Contain("aevatar.run.context");

        var actorId = ExtractRunContextActorId(body);
        actorId.Should().NotBeNullOrWhiteSpace();

        using var snapshotResponse = await host.Client.GetAsync($"/api/actors/{Uri.EscapeDataString(actorId!)}");
        var snapshot = await snapshotResponse.Content.ReadFromJsonAsync<WorkflowActorSnapshotHttpResponse>();

        snapshotResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be(actorId);
        snapshot.CompletionStatus.Should().Be(WorkflowRunCompletionStatus.Completed);
        snapshot.LastSuccess.Should().BeTrue();
        snapshot.LastOutput.Should().Be("y\nz");
        snapshot.LastError.Should().BeEmpty();
        snapshot.RequestedSteps.Should().Be(2);
        snapshot.CompletedSteps.Should().Be(2);
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

    private sealed class DraftRunActorQueryHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private DraftRunActorQueryHost(WebApplication app, HttpClient client, string repoRoot, string scopeId)
        {
            _app = app;
            Client = client;
            RepoRoot = repoRoot;
            ScopeId = scopeId;
        }

        public HttpClient Client { get; }

        public string RepoRoot { get; }

        public string ScopeId { get; }

        public static async Task<DraftRunActorQueryHost> StartAsync()
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
            builder.AddAevatarDefaultHost(options =>
            {
                options.ServiceName = "Aevatar.ScopeDraftRunActorQuery.Tests";
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
            builder.Services.AddSingleton<IGAgentActorStore, InMemoryGAgentActorStore>();
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

            return new DraftRunActorQueryHost(app, client, repoRoot, scopeId);
        }

        public IReadOnlyList<string> LoadWorkflowYamls(IReadOnlyList<string> names)
        {
            var workflowDir = Path.Combine(RepoRoot, "demos", "Aevatar.Demos.Workflow", "workflows");
            return names
                .Select(name => File.ReadAllText(Path.Combine(workflowDir, name)))
                .ToArray();
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

    private sealed class InMemoryGAgentActorStore : IGAgentActorStore
    {
        private readonly List<ActorRegistration> _registrations = [];

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildGroups(_registrations));

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(
            string scopeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildGroups(_registrations.Where(registration =>
                string.Equals(registration.ScopeId, scopeId, StringComparison.Ordinal))));

        public Task AddActorAsync(
            string gagentType,
            string actorId,
            CancellationToken cancellationToken = default) =>
            AddActorAsync(string.Empty, gagentType, actorId, cancellationToken);

        public Task AddActorAsync(
            string scopeId,
            string gagentType,
            string actorId,
            CancellationToken cancellationToken = default)
        {
            _registrations.Add(new ActorRegistration(scopeId, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task RemoveActorAsync(
            string gagentType,
            string actorId,
            CancellationToken cancellationToken = default) =>
            RemoveActorAsync(string.Empty, gagentType, actorId, cancellationToken);

        public Task RemoveActorAsync(
            string scopeId,
            string gagentType,
            string actorId,
            CancellationToken cancellationToken = default)
        {
            _registrations.RemoveAll(registration =>
                string.Equals(registration.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(registration.GAgentType, gagentType, StringComparison.Ordinal) &&
                string.Equals(registration.ActorId, actorId, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        private static IReadOnlyList<GAgentActorGroup> BuildGroups(IEnumerable<ActorRegistration> registrations) =>
            registrations
                .GroupBy(static registration => registration.GAgentType, StringComparer.Ordinal)
                .Select(static group => new GAgentActorGroup(
                    group.Key,
                    group.Select(static registration => registration.ActorId).ToArray()))
                .ToArray();

        private sealed record ActorRegistration(string ScopeId, string GAgentType, string ActorId);
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
