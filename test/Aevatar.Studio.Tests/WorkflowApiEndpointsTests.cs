using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aevatar.Bootstrap.Hosting;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowApiEndpointsTests
{
    private static readonly string[] Operations =
    [
        "list_workflows", "get_workflow_draft", "create_workflow_draft", "save_workflow_draft",
        "parse_workflow_yaml", "preview_workflow_external_requests", "publish_workflow",
        "get_published_workflow", "invoke_workflow", "list_activity_runs", "get_activity_run",
    ];

    [Fact]
    public async Task GeneratedOpenApi_ShouldExposeExactlyElevenOperationsWithSchemasAndReadOnlyFlags()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        using var response = await app.GetTestClient().GetAsync("/api/openapi.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        var operations = paths.EnumerateObject().SelectMany(path => path.Value.EnumerateObject()).ToArray();
        operations.Select(operation => operation.Value.GetProperty("operationId").GetString()).Should().BeEquivalentTo(Operations);
        operations.Count(operation => operation.Value.GetProperty("x-aevatar-tool").GetProperty("readOnly").GetBoolean()).Should().Be(7);
        foreach (var operation in operations)
        {
            var value = operation.Value;
            value.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
            var status = value.GetProperty("x-aevatar-tool").GetProperty("readOnly").GetBoolean() ? "200" : "202";
            value.GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema").ValueKind.Should().Be(JsonValueKind.Object);
            if (value.TryGetProperty("parameters", out var parameters))
                parameters.EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()).Should().NotContain(["scope", "scopeId", "definition"]);
        }
        var invoke = paths.GetProperty("/api/v1/workflows/{workflowId}/invoke").GetProperty("post");
        var invokeSchema = Resolve(root, invoke.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        invokeSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).Should().Contain("prompt");
        foreach (var path in new[] { "/api/v1/workflows/drafts", "/api/v1/workflows/drafts/{workflowId}" })
        {
            var mutation = paths.GetProperty(path).GetProperty(path.EndsWith('}') ? "put" : "post");
            var receipt = Resolve(root, mutation.GetProperty("responses").GetProperty("202").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
            receipt.GetProperty("properties").EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("workflowId", "commandId");
            var input = Resolve(root, mutation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
            input.GetProperty("properties").TryGetProperty("directoryId", out _).Should().BeFalse();
        }
        paths.TryGetProperty("/api/v1/workflows", out _).Should().BeFalse("optional section 5 APIs are out of scope");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task Catalogue_ShouldRejectUnauthenticatedMissingOrAmbiguousScope(string? identity)
    {
        var catalogue = new RecordingCatalogue();
        await using var app = CreateApp(catalogue);
        await app.StartAsync();
        using var client = app.GetTestClient();
        if (identity != null) client.DefaultRequestHeaders.Add("Test-Identity", identity);
        using var response = await client.GetAsync("/api/v1/workflows/catalogue");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        catalogue.LastQuery.Should().BeNull();
    }

    [Fact]
    public async Task StaticCatalogueRoute_ShouldUseAuthenticatedScopeAndExistingFilters()
    {
        var catalogue = new RecordingCatalogue();
        await using var app = CreateApp(catalogue);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "scope-alpha");
        using var response = await client.GetAsync("/api/v1/workflows/catalogue?view=drafts&query=hello&take=7&scopeId=scope-other");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        catalogue.LastQuery.Should().Be(new ScopeWorkflowCatalogueQuery("scope-alpha", ScopeWorkflowCatalogueView.Drafts, "hello", null, 7));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invoke_ShouldReturnBadRequestWhenApplicationRejectsBlankPrompt(string prompt)
    {
        var invocation = new RecordingInvocation { Error = new ArgumentException("Prompt cannot be blank.", "Prompt") };
        await using var app = CreateApp(invocation: invocation);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "scope-alpha");

        using var response = await client.PostAsJsonAsync("/api/v1/workflows/wf-alpha/invoke", new { prompt });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invocation.Request!.Prompt.Should().Be(prompt);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("message").GetString().Should().Contain("Prompt cannot be blank");
    }

    [Fact]
    public async Task Invoke_ShouldUseAuthenticatedScopeExtractCredentialsAndReturnOnlyActualReceiptIds()
    {
        var invocation = new RecordingInvocation();
        await using var app = CreateApp(invocation: invocation);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "scope-alpha");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer source-alpha");
        client.DefaultRequestHeaders.Add("X-NyxID-Delegation-Token", "delegation-alpha");

        using var response = await client.PostAsJsonAsync("/api/v1/workflows/wf-alpha/invoke?scopeId=scope-other", new
        {
            prompt = "run this text", revisionId = "revision-target", commandId = "cmd-request", correlationId = "corr-request",
            scopeId = "scope-other",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        invocation.Request.Should().Be(new ScopeWorkflowTextInvocationRequest(
            "scope-alpha", "wf-alpha", "run this text", "revision-target", "cmd-request", "corr-request"));
        invocation.Credential!.BearerToken.Should().Be("delegation-alpha");
        invocation.Credential.SourceReadableUserBearerToken.Should().Be("source-alpha");
        invocation.Credential.Kind.Should().Be(Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.ProxyDelegation);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var receipt = document.RootElement;
        receipt.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("runId", "commandId", "correlationId");
        receipt.GetProperty("runId").GetString().Should().Be("run-alpha");
        receipt.GetProperty("commandId").GetString().Should().Be("cmd-dispatched");
        receipt.GetProperty("correlationId").GetString().Should().Be("corr-dispatched");
        response.Headers.Location.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task Invoke_ShouldRejectUnauthenticatedMissingOrAmbiguousScopeBeforeApplication(string? identity)
    {
        var invocation = new RecordingInvocation();
        await using var app = CreateApp(invocation: invocation);
        await app.StartAsync();
        using var client = app.GetTestClient();
        if (identity != null) client.DefaultRequestHeaders.Add("Test-Identity", identity);

        using var response = await client.PostAsJsonAsync("/api/v1/workflows/wf-alpha/invoke", new { prompt = "input" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        invocation.Request.Should().BeNull();
    }

    [Fact]
    public async Task Publish_ShouldPreserveTargetRevisionAndEveryCommandHandleWithoutTechnicalReceiptFields()
    {
        var publication = new RecordingPublication();
        await using var app = CreateApp(publication: publication);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Identity", "scope-alpha");
        const string yaml = "name: alpha\nsteps: []\n";

        using var response = await client.PutAsJsonAsync("/api/v1/workflows/wf-alpha?scopeId=scope-other", new
        {
            workflowYaml = yaml, workflowName = "alpha", displayName = "Alpha", revisionId = "revision-target", executionMode = "interactive",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        publication.Request!.ScopeId.Should().Be("scope-alpha");
        publication.Request.WorkflowId.Should().Be("wf-alpha");
        publication.Request.WorkflowYaml.Should().Be(yaml);
        publication.Request.RevisionId.Should().Be("revision-target");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var receipt = document.RootElement;
        receipt.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("workflowId", "revisionId", "acceptedAtUtc", "commandHandles");
        receipt.GetProperty("workflowId").GetString().Should().Be("wf-alpha");
        receipt.GetProperty("revisionId").GetString().Should().Be("revision-target");
        receipt.GetProperty("acceptedAtUtc").GetDateTimeOffset().Should().Be(publication.Receipt.AcceptedAtUtc);
        var handles = receipt.GetProperty("commandHandles").EnumerateArray().ToArray();
        handles.Should().HaveCount(publication.Receipt.CommandHandles.Count);
        for (var index = 0; index < handles.Length; index++)
        {
            var expected = publication.Receipt.CommandHandles[index];
            handles[index].EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("stage", "commandId", "correlationId");
            handles[index].GetProperty("stage").GetString().Should().Be(expected.Stage);
            handles[index].GetProperty("commandId").GetString().Should().Be(expected.CommandId);
            handles[index].GetProperty("correlationId").GetString().Should().Be(expected.CorrelationId);
        }
        response.Headers.Location.Should().BeNull();
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema) => schema.TryGetProperty("$ref", out var reference)
        ? root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/').Last()) : schema;

    private static WebApplication CreateApp(RecordingCatalogue? catalogue = null,
        RecordingInvocation? invocation = null, RecordingPublication? publication = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Aevatar:Authentication:Enabled"] = "true" });
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ScopeAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAppScopedWorkflowCatalogueService>(catalogue ?? new RecordingCatalogue());
        builder.Services.AddSingleton<IScopeWorkflowTextInvocationPort>(invocation ?? new RecordingInvocation());
        builder.Services.AddSingleton<IScopeWorkflowCommandPort>(publication ?? new RecordingPublication());
        builder.Services.AddSingleton<ServiceInvokeReadinessErrorMapper>();
        builder.Services.AddOpenApi(options => options.AddOperationTransformer<AevatarToolOpenApiOperationTransformer>());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorkflowApi();
        app.MapWorkflowYamlApi();
        app.MapOpenApi("/api/openapi.json");
        return app;
    }

    private sealed class RecordingCatalogue : IAppScopedWorkflowCatalogueService
    {
        public ScopeWorkflowCatalogueQuery? LastQuery { get; private set; }
        public Task<ScopeWorkflowCatalogueResponse> QueryAsync(ScopeWorkflowCatalogueQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(new ScopeWorkflowCatalogueResponse([], null, new(null, "existing"), new([], "ordinal", "none", 100, "all", "exact")));
        }
    }

    private sealed class RecordingInvocation : IScopeWorkflowTextInvocationPort
    {
        public ScopeWorkflowTextInvocationRequest? Request { get; private set; }
        public CallerCredential? Credential { get; private set; }
        public Exception? Error { get; init; }

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ScopeWorkflowTextInvocationRequest request,
            CallerCredential? callerCredential, CancellationToken ct = default)
        {
            Request = request;
            Credential = callerCredential;
            return Error != null ? Task.FromException<ServiceInvocationAcceptedReceipt>(Error) : Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                RunId = "run-alpha", CommandId = "cmd-dispatched", CorrelationId = "corr-dispatched",
                TargetActorId = "actor-alpha", ServiceKey = "svc-alpha", RequestId = "request-alpha", StatusUrl = "/old/status",
            });
        }
    }

    private sealed class RecordingPublication : IScopeWorkflowCommandPort
    {
        public ScopeWorkflowUpsertRequest? Request { get; private set; }
        public ScopeWorkflowUpsertResult Receipt { get; } = new(
            "scope-alpha", "wf-alpha", "svc-alpha", "revision-target", "definition-alpha", "actor-alpha", "deployment-alpha",
            DateTimeOffset.Parse("2026-10-09T02:00:00Z"),
            [
                new("create_definition", "actor-definition", "cmd-definition", "corr-definition"),
                new("publish_revision", "actor-revision", "cmd-publish", "corr-publish"),
                new("deploy_revision", "actor-deployment", "cmd-deploy", "corr-deploy"),
            ], "/old/read-model");

        public Task<ScopeWorkflowUpsertResult> UpsertAsync(ScopeWorkflowUpsertRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(Receipt);
        }
    }

    private sealed class ScopeAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = Request.Headers["Test-Identity"].ToString();
            if (identity.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new("sub", "local-test-user") };
            if (identity != "missing") claims.Add(new("scope_id", identity));
            if (identity == "ambiguous") claims.Add(new("workflow.scope_id", "scope-other"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), "test")));
        }
    }
}
