using System.Security.Claims;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Mainnet.Host.Api.Chat;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetChatEndpointsTests
{
    [Theory]
    [InlineData("{}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"prompt\":\"hello\"}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"workflow\":\"direct\"}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"workflowYamls\":[\"name: inline\"]}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"type\":\"text\"}", "Assistant")]
    [InlineData("{\"type\":\"input.resolve\"}", "Assistant")]
    [InlineData("{\"type\":\"text\",\"workflow\":\"studio\"}", "Assistant")]
    [InlineData("{\"type\":\"action.continue\"}", "Assistant")]
    [InlineData("{\"type\":\"approval.resolve\"}", "Assistant")]
    [InlineData("{\"type\":\"plan.resolve\"}", "Unsupported")]
    [InlineData("{\"type\":\"task.stop\"}", "Assistant")]
    [InlineData("{\"type\":\"task.steer\"}", "Assistant")]
    [InlineData("{\"type\":\"step.retry\"}", "Assistant")]
    [InlineData("{\"type\":\"step.skip\"}", "Assistant")]
    [InlineData("{\"type\":\"future.type\"}", "Unsupported")]
    public async Task RequestShape_ShouldSelectOneExplicitBoundary(
        string json,
        string expected)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.ToString().Should().Be(expected);
        if (expected == "ExternalWorkflowCompatibility")
        {
            http.Request.Body.Position.Should().Be(0);
            using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
            (await reader.ReadToEndAsync()).Should().Be(json);
        }
    }

    [Fact]
    public async Task ExplicitStudioWorkflowJson_ShouldUseFrozenCompatibilityAdapter()
    {
        const string json = "{\"commandId\":\"cmd-1\",\"conversation\":{\"conversationId\":null},\"prompt\":\"hello\",\"sessionId\":\"session-1\",\"workflow\":\"studio\"}";
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.ExternalWorkflowCompatibility);
        http.Request.Body.Position.Should().Be(0);
        using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
        (await reader.ReadToEndAsync()).Should().Be(json);
    }

    [Fact]
    public async Task Multipart_ShouldRemainInFrozenExternalCompatibilityAdapterWithoutReadingBody()
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=test";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("unchanged"));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.ExternalWorkflowCompatibility);
        http.Request.Body.Position.Should().Be(0);
    }

    [Theory]
    [InlineData("text/plain", "hello")]
    [InlineData("application/json", "not-json")]
    [InlineData("application/json", "[]")]
    public async Task UnsupportedOrMalformedInput_ShouldNotFallThroughToWorkflow(
        string contentType,
        string body)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = contentType;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.Unsupported);
    }

    [Fact]
    public async Task ExternalWorkflowCompatibility_ShouldPassResolvedWorkflowBindingToWorkflowHandler()
    {
        const string json = "{\"prompt\":\"hello\",\"workflow\":\"dinner_date\"}";
        var workflow = CreateDinnerWorkflowSummary();
        var ensurePort = new RecordingScopeWorkflowTemplateEnsurePort(
            ScopeWorkflowTemplateEnsureResult.AlreadyCurrent(workflow, workflow.ActiveRevisionId));
        var queryPort = new FixedScopeWorkflowQueryPort(
            new ScopeWorkflowLookupResult(ScopeWorkflowLookupStatus.Runnable, workflow, "workflow_runnable"));
        var bindingReader = new FixedWorkflowActorBindingReader(new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            "actor-dinner",
            "actor-dinner",
            string.Empty,
            "dinner_date_mock",
            "name: dinner_date_mock\nsteps: []",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Interactive,
            ScopeId: "scope-1",
            SourceKind: "service_revision",
            WorkflowId: "default",
            RevisionId: "dinner-date-mock-v2"));
        var chatRunService = new RecordingWorkflowChatRunInteractionPort();
        var http = CreateAuthenticatedJsonContext(
            json,
            services =>
            {
                services.AddSingleton<IScopeWorkflowTemplateEnsurePort>(ensurePort);
                services.AddSingleton<IScopeWorkflowQueryPort>(queryPort);
                services.AddSingleton<IWorkflowActorBindingReader>(bindingReader);
                services.AddSingleton<IWorkflowChatRunInteractionPort>(chatRunService);
            });
        var classification = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        await ExternalWorkflowChatCompatibilityAdapter.HandleAsync(http, classification.Body, CancellationToken.None);

        ensurePort.Requests.Should().ContainSingle().Which.WorkflowId.Should().Be("dinner_date");
        queryPort.Requests.Should().ContainSingle().Which.Should().Be(("scope-1", "dinner_date"));
        chatRunService.LastRequest.Should().NotBeNull();
        chatRunService.LastRequest!.Source.CatalogName!.WorkflowName.Should().Be("dinner_date_mock");
        chatRunService.LastRequest.ResolvedDefinitionBinding.Should().NotBeNull();
        chatRunService.LastRequest.ResolvedDefinitionBinding!.DefinitionActorId.Should().Be("actor-dinner");
        chatRunService.LastRequest.ResolvedDefinitionBinding.WorkflowId.Should().Be("dinner_date");
        chatRunService.LastRequest.ResolvedDefinitionBinding.WorkflowName.Should().Be("dinner_date_mock");
    }

    [Fact]
    public async Task ExternalWorkflowCompatibility_ShouldEnsureConfiguredTemplateBeforeWorkflowHandler()
    {
        const string json = "{\"prompt\":\"hello\",\"workflow\":\"dinner_date\"}";
        var ensurePort = new RecordingScopeWorkflowTemplateEnsurePort(
            ScopeWorkflowTemplateEnsureResult.Failed(
                "scope-1",
                "dinner_date",
                "dinner-date-mock-v2",
                "workflow_template_readmodel_not_observed"));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
            })
            .Build());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);
        services.AddSingleton<IScopeWorkflowTemplateEnsurePort>(ensurePort);
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("scope_id", "scope-1"),
                    new Claim("sub", "caller-1"),
                ],
                authenticationType: "test")),
        };
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var classification = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        await ExternalWorkflowChatCompatibilityAdapter.HandleAsync(http, classification.Body, CancellationToken.None);

        ensurePort.Requests.Should().ContainSingle().Which.Should().Match<ScopeWorkflowTemplateEnsureRequest>(request =>
            request.ScopeId == "scope-1" && request.WorkflowId == "dinner_date");
        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    private static DefaultHttpContext CreateAuthenticatedJsonContext(
        string json,
        Action<ServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
            })
            .Build());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);
        configureServices?.Invoke(services);

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("scope_id", "scope-1"),
                new Claim("sub", "caller-1"),
            ], authenticationType: "test")),
        };
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Headers.Authorization = "Bearer caller-token";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static ScopeWorkflowSummary CreateDinnerWorkflowSummary() =>
        new(
            "scope-1",
            "dinner_date",
            "Dinner Date Mock",
            "scope-1:default:default:default",
            "dinner_date_mock",
            "actor-dinner",
            "dinner-date-mock-v2",
            "deployment-dinner",
            "Active",
            DateTimeOffset.UtcNow)
        {
            ServiceAppId = "default",
            ServiceNamespace = "default",
            PublishedServiceId = "default",
        };

    private sealed class RecordingWorkflowChatRunInteractionPort : IWorkflowChatRunInteractionPort
    {
        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public Task<WorkflowChatRunInteractionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(WorkflowChatRunInteractionResult.Failure(
                WorkflowChatRunStartError.WorkflowBindingMismatch));
        }
    }

    private sealed class FixedScopeWorkflowQueryPort(
        ScopeWorkflowLookupResult result) : IScopeWorkflowQueryPort
    {
        public List<(string ScopeId, string WorkflowId)> Requests { get; } = [];

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
            string scopeId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>([]);

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default)
        {
            Requests.Add((scopeId, workflowId));
            return Task.FromResult(result);
        }

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            Task.FromResult(result.Workflow);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult(result.Workflow);
    }

    private sealed class FixedWorkflowActorBindingReader(
        WorkflowActorBinding? binding) : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult(binding);
    }

    private sealed class RecordingScopeWorkflowTemplateEnsurePort(
        ScopeWorkflowTemplateEnsureResult result) : IScopeWorkflowTemplateEnsurePort
    {
        public List<ScopeWorkflowTemplateEnsureRequest> Requests { get; } = [];

        public Task<ScopeWorkflowTemplateEnsureResult> EnsureAsync(
            ScopeWorkflowTemplateEnsureRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
