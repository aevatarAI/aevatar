using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Hosting;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppStudioScriptDraftRunEndpointTests
{
    [Fact]
    public async Task ScriptDraftRunEndpoint_ShouldProvisionAndDispatch_WhenEmbeddedModeEnabled()
    {
        await using var host = await StudioScriptDraftRunTestHost.StartAsync(
            embeddedWorkflowMode: true,
            resolvedScopeId: "scope-a");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/scripts/draft-run", new
        {
            scriptId = "Orders Script",
            scriptRevision = "Draft Revision",
            source = "public sealed class DemoScript {}",
            input = "hello",
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var payload = JsonDocument.Parse(body);
        payload.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("scopeId").GetString().Should().Be("scope-a");
        payload.RootElement.GetProperty("scriptId").GetString().Should().Be("orders-script");
        payload.RootElement.GetProperty("scriptRevision").GetString().Should().Be("draft-revision");

        host.DefinitionPort.LastCall.Should().NotBeNull();
        var definitionCall = host.DefinitionPort.LastCall!.Value;
        definitionCall.scopeId.Should().Be("scope-a");
        definitionCall.scriptId.Should().Be("orders-script");
        definitionCall.scriptRevision.Should().Be("draft-revision");
        host.RuntimeProvisioningPort.LastCall.Should().NotBeNull();
        var provisioningCall = host.RuntimeProvisioningPort.LastCall!.Value;
        provisioningCall.scopeId.Should().Be("scope-a");
        host.RuntimeCommandPort.LastCall.Should().NotBeNull();
        var commandCall = host.RuntimeCommandPort.LastCall!.Value;
        commandCall.scopeId.Should().Be("scope-a");
        commandCall.requestedEventType.Should().Be(Any.Pack(new AppScriptCommand()).TypeUrl);
        commandCall.inputPayload.Should().NotBeNull();
        commandCall.inputPayload!.TypeUrl.Should().Be(Any.Pack(new AppScriptCommand()).TypeUrl);
    }

    [Fact]
    public async Task ScriptDraftRunEndpoint_ShouldReturnBadRequest_WhenEmbeddedModeIsDisabled()
    {
        await using var host = await StudioScriptDraftRunTestHost.StartAsync(
            embeddedWorkflowMode: false,
            resolvedScopeId: "scope-a");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/scripts/draft-run", new
        {
            scriptId = "Orders Script",
            source = "public sealed class DemoScript {}",
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        var payload = JsonDocument.Parse(body);
        payload.RootElement.GetProperty("code").GetString().Should().Be("SCRIPT_DRAFT_RUN_UNAVAILABLE");
        host.DefinitionPort.LastCall.Should().BeNull();
        host.RuntimeProvisioningPort.LastCall.Should().BeNull();
        host.RuntimeCommandPort.LastCall.Should().BeNull();
    }

    [Fact]
    public async Task ScriptDraftRunEndpoint_ShouldReturnForbidden_WhenResolvedScopeDoesNotMatchRequest()
    {
        await using var host = await StudioScriptDraftRunTestHost.StartAsync(
            embeddedWorkflowMode: true,
            resolvedScopeId: "scope-b");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/scripts/draft-run", new
        {
            scriptId = "Orders Script",
            source = "public sealed class DemoScript {}",
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        var payload = JsonDocument.Parse(body);
        payload.RootElement.GetProperty("code").GetString().Should().Be("SCOPE_ACCESS_DENIED");
        host.DefinitionPort.LastCall.Should().BeNull();
        host.RuntimeProvisioningPort.LastCall.Should().BeNull();
        host.RuntimeCommandPort.LastCall.Should().BeNull();
    }

    private sealed class StudioScriptDraftRunTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StudioScriptDraftRunTestHost(
            WebApplication app,
            HttpClient client,
            RecordingScriptDefinitionCommandPort definitionPort,
            RecordingScriptRuntimeProvisioningPort runtimeProvisioningPort,
            RecordingScriptRuntimeCommandPort runtimeCommandPort)
        {
            _app = app;
            Client = client;
            DefinitionPort = definitionPort;
            RuntimeProvisioningPort = runtimeProvisioningPort;
            RuntimeCommandPort = runtimeCommandPort;
        }

        public HttpClient Client { get; }

        public RecordingScriptDefinitionCommandPort DefinitionPort { get; }

        public RecordingScriptRuntimeProvisioningPort RuntimeProvisioningPort { get; }

        public RecordingScriptRuntimeCommandPort RuntimeCommandPort { get; }

        public static async Task<StudioScriptDraftRunTestHost> StartAsync(bool embeddedWorkflowMode, string resolvedScopeId)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var definitionPort = new RecordingScriptDefinitionCommandPort();
            var runtimeProvisioningPort = new RecordingScriptRuntimeProvisioningPort();
            var runtimeCommandPort = new RecordingScriptRuntimeCommandPort();

            builder.Services.AddSingleton<IAppScopeResolver>(new StubAppScopeResolver(resolvedScopeId));
            builder.Services.AddSingleton<IScriptDefinitionCommandPort>(definitionPort);
            builder.Services.AddSingleton<IScriptRuntimeProvisioningPort>(runtimeProvisioningPort);
            builder.Services.AddSingleton<IScriptRuntimeCommandPort>(runtimeCommandPort);
            builder.Services.AddSingleton(new AevatarHostMetadata
            {
                ServiceName = "test-studio",
            });
            builder.Services.AddSingleton<AevatarHostHealthService>();

            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("scope_id", resolvedScopeId)],
                    authenticationType: "Test"));
                await next();
            });
            StudioEndpoints.Map(app, embeddedWorkflowMode);
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

            return new StudioScriptDraftRunTestHost(
                app,
                client,
                definitionPort,
                runtimeProvisioningPort,
                runtimeCommandPort);
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

    private sealed class RecordingScriptDefinitionCommandPort : IScriptDefinitionCommandPort
    {
        public (string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, string? scopeId)? LastCall { get; private set; }

        public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct) =>
            UpsertDefinitionWithSnapshotAsync(scriptId, scriptRevision, sourceText, sourceHash, definitionActorId, null, ct);

        public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            string? scopeId,
            CancellationToken ct)
        {
            LastCall = (scriptId, scriptRevision, sourceText, sourceHash, definitionActorId, scopeId);
            var actorId = definitionActorId ?? $"definition:{scriptId}:{scriptRevision}";
            return Task.FromResult(new ScriptDefinitionUpsertResult(
                actorId,
                new ScriptDefinitionSnapshot(
                    scriptId,
                    scriptRevision,
                    sourceText,
                    sourceHash,
                    StateTypeUrl: string.Empty,
                    ReadModelTypeUrl: string.Empty,
                    ReadModelSchemaVersion: string.Empty,
                    ReadModelSchemaHash: string.Empty,
                    DefinitionActorId: actorId,
                    ScopeId: scopeId ?? string.Empty)));
        }
    }

    private sealed class RecordingScriptRuntimeProvisioningPort : IScriptRuntimeProvisioningPort
    {
        public (string definitionActorId, string scriptRevision, string? runtimeActorId, ScriptDefinitionSnapshot definitionSnapshot, string? scopeId)? LastCall { get; private set; }

        public Task<string> EnsureRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            ScriptDefinitionSnapshot definitionSnapshot,
            CancellationToken ct) =>
            EnsureRuntimeAsync(definitionActorId, scriptRevision, runtimeActorId, definitionSnapshot, null, ct);

        public Task<string> EnsureRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            ScriptDefinitionSnapshot definitionSnapshot,
            string? scopeId,
            CancellationToken ct)
        {
            LastCall = (definitionActorId, scriptRevision, runtimeActorId, definitionSnapshot, scopeId);
            return Task.FromResult(runtimeActorId ?? $"runtime:{definitionActorId}:{scriptRevision}");
        }
    }

    private sealed class RecordingScriptRuntimeCommandPort : IScriptRuntimeCommandPort
    {
        public (string runtimeActorId, string runId, Any? inputPayload, string scriptRevision, string definitionActorId, string requestedEventType, string? scopeId)? LastCall { get; private set; }

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct) =>
            RunRuntimeAsync(runtimeActorId, runId, inputPayload, scriptRevision, definitionActorId, requestedEventType, null, ct);

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            string? scopeId,
            CancellationToken ct)
        {
            LastCall = (runtimeActorId, runId, inputPayload, scriptRevision, definitionActorId, requestedEventType, scopeId);
            return Task.CompletedTask;
        }
    }
}
