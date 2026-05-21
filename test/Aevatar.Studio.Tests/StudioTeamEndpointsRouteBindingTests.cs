using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Regression guard mirroring <see cref="StudioMemberEndpointsRouteBindingTests"/>
/// for the team-first surface (ADR-0017). Forces endpoint construction so a
/// future drop of <c>[FromServices]</c> on any team handler — which would
/// re-trigger the <c>RequestDelegateFactory</c> BindAsync collision on
/// <see cref="IStudioTeamService"/> — fails this test before mainnet startup.
/// </summary>
public sealed class StudioTeamEndpointsRouteBindingTests
{
    [Fact]
    public void Map_ShouldBuildAllRoutes_WithoutBindAsyncCollision()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IStudioTeamService, NoOpTeamService>();
        builder.Services.AddSingleton<IStudioMemberService, NoOpMemberServiceForTeam>();
        builder.Services.AddSingleton<IStudioTeamGAgentStreamInvocationService, NoOpTeamStreamInvocationService>();
        builder.Services.AddRouting();
        var app = builder.Build();

        StudioTeamEndpoints.Map(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .ToList();

        // Nine routes: create, list, get, patch, archive, entry set,
        // entry clear, list-members, stream invoke.
        endpoints.Should().HaveCount(9);
        endpoints.OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Should()
            .Contain("/api/scopes/{scopeId}/teams/{teamId}/invoke/{endpointId}:stream");
    }

    [Fact]
    public async Task HandleInvokeTeamStreamAsync_ShouldDelegateToStudioGAgentStreamService()
    {
        var service = new RecordingTeamStreamInvocationService();
        await using var provider = CreateRequestServices();
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        await using var body = new MemoryStream();
        http.Response.Body = body;
        http.Request.Headers.Authorization = "Bearer token-1";

        await StudioTeamEndpoints.HandleInvokeTeamStreamAsync(
            http,
            "scope-a",
            "team-a",
            "chat",
            new StudioTeamEndpoints.StudioTeamGAgentStreamHttpRequest(
                Prompt: "  hello team  ",
                ActorId: "actor-preferred",
                SessionId: "session-1",
                Headers: new Dictionary<string, string>
                {
                    ["scope_id"] = "spoofed",
                    [WorkflowRunCommandMetadataKeys.ScopeId] = "spoofed-workflow",
                    ["x-trace"] = "trace-1",
                },
                RevisionId: "rev-1",
                InputParts:
                [
                    new StudioTeamEndpoints.StudioTeamStreamContentPartHttpRequest(
                        Type: "image",
                        DataBase64: "aGVsbG8=",
                        MediaType: "image/png",
                        Name: "sample.png"),
                ]),
            service,
            CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        http.Response.ContentType.Should().Be("text/event-stream; charset=utf-8");
        http.Response.Headers["X-Correlation-Id"].ToString().Should().Be("corr-1");
        text.Should().Contain("runStarted");
        text.Should().Contain("cmd-1");

        service.LastRequest.Should().NotBeNull();
        service.LastRequest!.ScopeId.Should().Be("scope-a");
        service.LastRequest.TeamId.Should().Be("team-a");
        service.LastRequest.EndpointId.Should().Be("chat");
        service.LastRequest.Input.Prompt.Should().Be("hello team");
        service.LastRequest.Input.PreferredActorId.Should().Be("actor-preferred");
        service.LastRequest.Input.SessionId.Should().Be("session-1");
        service.LastRequest.Input.RevisionId.Should().Be("rev-1");
        service.LastRequest.Input.Headers.Should().Contain("x-trace", "trace-1");
        service.LastRequest.Input.Headers.Should().Contain("nyxid.access_token", "token-1");
        service.LastRequest.Input.Headers.Should().Contain(ConnectorRequest.HttpAuthorizationMetadataKey, "Bearer token-1");
        service.LastRequest.Input.Headers.Should().NotContainKey("scope_id");
        service.LastRequest.Input.Headers.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        service.LastRequest.Input.InputParts.Should().ContainSingle()
            .Which.Kind.Should().Be(GAgentDraftRunInputPartKind.Image);
    }

    [Fact]
    public async Task HandleInvokeTeamStreamAsync_ShouldInjectOwnerLlmPreferencesWithoutOverridingClientHeaders()
    {
        var service = new RecordingTeamStreamInvocationService();
        await using var provider = CreateRequestServices(services =>
            services.AddSingleton<INyxIdUserLlmPreferencesStore>(
                new StubOwnerLlmPreferencesStore("gpt-5.5", "/api/v1/proxy/s/nyx")));
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        await using var body = new MemoryStream();
        http.Response.Body = body;

        await StudioTeamEndpoints.HandleInvokeTeamStreamAsync(
            http,
            "scope-a",
            "team-a",
            "chat",
            new StudioTeamEndpoints.StudioTeamGAgentStreamHttpRequest(
                Prompt: "hello",
                Headers: new Dictionary<string, string>
                {
                    [LLMRequestMetadataKeys.ModelOverride] = "client-model",
                }),
            service,
            CancellationToken.None);

        service.LastRequest.Should().NotBeNull();
        service.LastRequest!.Input.Headers.Should().Contain(
            LLMRequestMetadataKeys.ModelOverride,
            "client-model");
        service.LastRequest.Input.Headers.Should().Contain(
            LLMRequestMetadataKeys.NyxIdRoutePreference,
            "/api/v1/proxy/s/nyx");
    }

    [Fact]
    public async Task HandleInvokeTeamStreamAsync_ShouldMapEntryMemberFailureBeforeSseStarts()
    {
        var service = new RecordingTeamStreamInvocationService
        {
            Exception = new TeamEntryMemberResolutionException(
                TeamEntryMemberErrorCodes.EntryMemberNotConfigured,
                "scope-a",
                "team-a",
                "team 'team-a' has no entry member configured."),
        };
        await using var provider = CreateRequestServices();
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        await using var body = new MemoryStream();
        http.Response.Body = body;

        await StudioTeamEndpoints.HandleInvokeTeamStreamAsync(
            http,
            "scope-a",
            "team-a",
            "chat",
            new StudioTeamEndpoints.StudioTeamGAgentStreamHttpRequest("hello"),
            service,
            CancellationToken.None);

        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        document.RootElement.GetProperty("code").GetString()
            .Should().Be(TeamEntryMemberErrorCodes.EntryMemberNotConfigured);
    }

    [Fact]
    public async Task HandleInvokeTeamStreamAsync_ShouldWriteRunError_WhenServiceFailsAfterSseStarts()
    {
        var service = new RecordingTeamStreamInvocationService
        {
            ExceptionAfterAccepted = new InvalidOperationException("stream failed"),
        };
        await using var provider = CreateRequestServices();
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        await using var body = new MemoryStream();
        http.Response.Body = body;

        await StudioTeamEndpoints.HandleInvokeTeamStreamAsync(
            http,
            "scope-a",
            "team-a",
            "chat",
            new StudioTeamEndpoints.StudioTeamGAgentStreamHttpRequest("hello"),
            service,
            CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        text.Should().Contain("runStarted");
        text.Should().Contain("runError");
        text.Should().Contain("stream failed");
    }

    [Fact]
    public async Task HandleInvokeTeamStreamAsync_ShouldWriteAuthRunError_WhenNyxIdAuthFailsAfterSseStarts()
    {
        var service = new RecordingTeamStreamInvocationService
        {
            ExceptionAfterAccepted = new NyxIdAuthenticationRequiredException("nyx"),
        };
        await using var provider = CreateRequestServices();
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        await using var body = new MemoryStream();
        http.Response.Body = body;

        await StudioTeamEndpoints.HandleInvokeTeamStreamAsync(
            http,
            "scope-a",
            "team-a",
            "chat",
            new StudioTeamEndpoints.StudioTeamGAgentStreamHttpRequest("hello"),
            service,
            CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        text.Should().Contain("runStarted");
        text.Should().Contain("runError");
        text.Should().Contain("authentication_required");
        text.Should().Contain("NyxID authentication required. Please sign in.");
    }

    [Fact]
    public async Task HandleInvokeTeamStreamAsync_ShouldWriteTimeoutRunError_WhenServiceTimesOut()
    {
        var service = new RecordingTeamStreamInvocationService
        {
            ExceptionAfterAccepted = new OperationCanceledException("interaction timeout"),
        };
        await using var provider = CreateRequestServices();
        var http = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        await using var body = new MemoryStream();
        http.Response.Body = body;

        await StudioTeamEndpoints.HandleInvokeTeamStreamAsync(
            http,
            "scope-a",
            "team-a",
            "chat",
            new StudioTeamEndpoints.StudioTeamGAgentStreamHttpRequest("hello"),
            service,
            CancellationToken.None);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        text.Should().Contain("runStarted");
        text.Should().Contain("runError");
        text.Should().Contain("Studio team GAgent stream timed out.");
    }

    private static ServiceProvider CreateRequestServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "false",
                })
                .Build());
        services.AddSingleton<IHostEnvironment>(
            new TestHostEnvironment
            {
                EnvironmentName = Environments.Development,
                ApplicationName = "Aevatar.Studio.Tests",
                ContentRootPath = Directory.GetCurrentDirectory(),
            });
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private sealed class NoOpTeamService : IStudioTeamService
    {
        public Task<StudioTeamSummaryResponse> CreateAsync(
            string scopeId, CreateStudioTeamRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, []));

        public Task<StudioTeamSummaryResponse> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());

        public Task<StudioTeamSummaryResponse> UpdateAsync(
            string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());

        public Task<StudioTeamSummaryResponse> ArchiveAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());

        public Task<StudioTeamSummaryResponse> SetEntryMemberAsync(
            string scopeId,
            string teamId,
            SetStudioTeamEntryMemberRequest request,
            CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());

        public Task<StudioTeamSummaryResponse> ClearEntryMemberAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());
    }

    private sealed class NoOpMemberServiceForTeam : IStudioMemberService
    {
        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioMemberSummaryResponse>(new NotImplementedException());

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, []));

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberDetailResponse>(new NotImplementedException());

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingAcceptedResponse>(new NotImplementedException());

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberBindingViewResponse(null));

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId, string memberId, string bindingRunId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingRunStatusResponse>(new NotImplementedException());

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId, string memberId, string endpointId, CancellationToken ct = default) =>
            Task.FromResult<StudioMemberEndpointContractResponse?>(null);

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingActivationResponse>(new NotImplementedException());

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingRevisionActionResponse>(new NotImplementedException());

        public Task<StudioMemberDetailResponse> UpdateAsync(
            string scopeId, string memberId, UpdateStudioMemberRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioMemberDetailResponse>(new NotImplementedException());
    }

    private sealed class NoOpTeamStreamInvocationService : IStudioTeamGAgentStreamInvocationService
    {
        public Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StudioTeamGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default) =>
            Task.FromException<StaticGAgentStreamInvocationResult>(new NotImplementedException());
    }

    private sealed class RecordingTeamStreamInvocationService : IStudioTeamGAgentStreamInvocationService
    {
        public StudioTeamGAgentStreamInvocationRequest? LastRequest { get; private set; }

        public TeamEntryMemberResolutionException? Exception { get; set; }

        public Exception? ExceptionAfterAccepted { get; set; }

        public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StudioTeamGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            if (Exception != null)
                throw Exception;

            var accepted = new StaticGAgentStreamAcceptedReceipt(
                new ServiceInvocationAcceptedReceipt
                {
                    RequestId = "cmd-1",
                    CommandId = "cmd-1",
                    CorrelationId = "corr-1",
                    TargetActorId = "actor-1",
                    EndpointId = request.EndpointId,
                },
                new GAgentDraftRunAcceptedReceipt("actor-1", "RoleGAgent", "cmd-1", "corr-1"));

            if (onAcceptedAsync != null)
                await onAcceptedAsync(accepted, ct);

            if (ExceptionAfterAccepted != null)
                throw ExceptionAfterAccepted;

            return new StaticGAgentStreamInvocationResult(
                accepted,
                GAgentDraftRunStartError.None,
                GAgentDraftRunCompletionStatus.RunFinished,
                CompletionObserved: true);
        }
    }

    private sealed class StubOwnerLlmPreferencesStore(
        string defaultModel,
        string preferredRoute) : INyxIdUserLlmPreferencesStore
    {
        public Task<NyxIdUserLlmPreferences> GetOwnerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NyxIdUserLlmPreferences(defaultModel, preferredRoute));

        public Task<NyxIdUserLlmPreferences> GetForBindingAsync(
            string bindingId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
