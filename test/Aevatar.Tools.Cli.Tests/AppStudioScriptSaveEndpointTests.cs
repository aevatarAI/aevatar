using System.Net;
using System.Net.Http.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Hosting;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppStudioScriptSaveEndpointTests
{
    [Fact]
    public async Task AppScriptSaveEndpoint_ShouldReturnAcceptedResponse()
    {
        await using var host = await StudioScriptSaveTestHost.StartAsync("scope-a");

        var response = await host.Client.PostAsJsonAsync("/api/app/scripts", new
        {
            scriptId = "Orders Script",
            sourceText = "public sealed class DemoScript {}",
            revisionId = "rev-1",
        });
        var payload = await response.Content.ReadFromJsonAsync<AppScopeScriptSaveAcceptedResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Be("/api/app/scripts/orders-script/save-observation");
        payload.Should().NotBeNull();
        payload!.ScriptId.Should().Be("orders-script");
        payload.ScopeId.Should().Be("scope-a");
        payload.RevisionId.Should().Be("rev-1");
        payload.SubmittedSource.SourceText.Should().Be("public sealed class DemoScript {}");
        payload.DefinitionCommand.CommandId.Should().Be("definition-command-1");
        payload.CatalogCommand.CommandId.Should().Be("catalog-command-1");
        payload.ProposalId.Should().StartWith("scope-a:orders-script:rev-1:");

        host.CommandPort.LastRequest.Should().NotBeNull();
        host.CommandPort.LastRequest!.ScopeId.Should().Be("scope-a");
        host.CommandPort.LastRequest.ScriptId.Should().Be("orders-script");
    }

    private sealed class StudioScriptSaveTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StudioScriptSaveTestHost(
            WebApplication app,
            HttpClient client,
            RecordingScopeScriptCommandPort commandPort)
        {
            _app = app;
            Client = client;
            CommandPort = commandPort;
        }

        public HttpClient Client { get; }

        public RecordingScopeScriptCommandPort CommandPort { get; }

        public static async Task<StudioScriptSaveTestHost> StartAsync(string scopeId)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var commandPort = new RecordingScopeScriptCommandPort();
            var appScopedScriptService = new AppScopedScriptService(
                new StubHttpClientFactory(new HttpClient()),
                scriptCommandPort: commandPort);

            builder.Services.AddSingleton<IAppScopeResolver>(new StubAppScopeResolver(scopeId));
            builder.Services.AddSingleton(commandPort);
            builder.Services.AddSingleton(appScopedScriptService);
            builder.Services.AddSingleton(new AevatarHostMetadata
            {
                ServiceName = "test-studio",
            });
            builder.Services.AddSingleton<AevatarHostHealthService>();

            var app = builder.Build();
            StudioEndpoints.Map(app, embeddedWorkflowMode: false);
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");

            return new StudioScriptSaveTestHost(
                app,
                new HttpClient
                {
                    BaseAddress = new Uri(addressFeature.Addresses.Single()),
                },
                commandPort);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubAppScopeResolver : IAppScopeResolver
    {
        private readonly AppScopeContext _context;

        public StubAppScopeResolver(string scopeId)
        {
            _context = new AppScopeContext(scopeId, "test");
        }

        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) => _context;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(string name) => _httpClient;
    }

    private sealed class RecordingScopeScriptCommandPort : IScopeScriptCommandPort
    {
        public ScopeScriptUpsertRequest? LastRequest { get; private set; }

        public Task<ScopeScriptUpsertResult> UpsertAsync(
            ScopeScriptUpsertRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;

            return Task.FromResult(new ScopeScriptUpsertResult(
                new ScopeScriptAcceptedSummary(
                    request.ScopeId,
                    request.ScriptId,
                    "catalog-1",
                    "definition-1",
                    request.RevisionId ?? "rev-1",
                    "hash-1",
                    DateTimeOffset.UtcNow,
                    $"{request.ScopeId}:{request.ScriptId}:{request.RevisionId ?? "rev-1"}:{Guid.NewGuid():N}",
                    request.ExpectedBaseRevision ?? string.Empty),
                new ScopeScriptCommandAcceptedHandle(
                    "definition-1",
                    "definition-command-1",
                    "definition-correlation-1"),
                new ScopeScriptCommandAcceptedHandle(
                    "catalog-1",
                    "catalog-command-1",
                    "catalog-correlation-1")));
        }
    }
}
