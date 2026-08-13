using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Bindings are scope-owned data: a scope can register, list (secret
/// redacted), and delete its own route keys; a route owned by another scope
/// is untouchable; and the ingress resolves dynamic bindings without the
/// static Enabled flag or any host configuration change.
/// </summary>
public sealed class WorkflowWebhookBindingEndpointsTests
{
    [Fact]
    public async Task PutListDelete_ShouldRoundTripScopeOwnedBinding_WithSecretRedacted()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()));

        var put = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "hr01-route",
            new WorkflowWebhookBindingEndpoints.PutWorkflowWebhookBindingRequest(
                WorkflowName: "hr_onboarding_email_approval",
                SourceId: "nyxid-trigger",
                PromptTemplate: """{"record_id":"{{payload.record_id}}","submit":false}""",
                PromptJsonPath: null,
                DeliveryIdHeader: "X-NyxID-Delivery-Id",
                DeliveryIdJsonPath: "event_id",
                HmacSecret: "delivery-signing-secret-at-least-32-bytes",
                HmacSignatureHeader: "X-NyxID-Signature",
                HmacTimestampHeader: "X-NyxID-Timestamp",
                MaxTimestampSkewSeconds: 300,
                DefinitionActorId: "actor-hr01",
                TargetRevisionId: "rev-7"));
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)put).StatusCode.Should().Be(StatusCodes.Status200OK);

        var stored = await store.GetAsync("hr01-route");
        stored.Should().NotBeNull();
        stored!.ScopeId.Should().Be("scope-1");
        stored.WorkflowName.Should().Be("hr_onboarding_email_approval");
        stored.HmacSecret.Should().Be("delivery-signing-secret-at-least-32-bytes");

        var list = await WorkflowWebhookBindingEndpoints.HandleListAsync(http, "scope-1");
        var listJson = System.Text.Json.JsonSerializer.Serialize(
            ((Microsoft.AspNetCore.Http.IValueHttpResult)list).Value);
        listJson.Should().Contain("hr01-route");
        listJson.Should().Contain("hmacSecretSet");
        listJson.Should().NotContain("delivery-signing-secret-at-least-32-bytes");

        var delete = await WorkflowWebhookBindingEndpoints.HandleDeleteAsync(http, "scope-1", "hr01-route");
        delete.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        (await store.GetAsync("hr01-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_ShouldRejectRouteOwnedByAnotherScope()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("shared-route", "scope-owner"));
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding(scopeId: "scope-intruder")));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-intruder",
            "shared-route",
            new WorkflowWebhookBindingEndpoints.PutWorkflowWebhookBindingRequest(
                WorkflowName: "wf",
                SourceId: null,
                PromptTemplate: "{}",
                PromptJsonPath: null,
                DeliveryIdHeader: "X-Delivery",
                DeliveryIdJsonPath: "event_id",
                HmacSecret: "scope-intruder-secret-at-least-32-bytes",
                HmacSignatureHeader: null,
                HmacTimestampHeader: null,
                MaxTimestampSkewSeconds: null,
                DefinitionActorId: "actor-hr01",
                TargetRevisionId: "rev-7"));

        await result.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await store.GetAsync("shared-route"))!.ScopeId.Should().Be("scope-owner");
    }

    [Fact]
    public async Task Put_ShouldRejectOversizedRouteKey()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            new string('r', WorkflowWebhookIngressLimits.MaxRouteKeyBytes + 1),
            ExactPutRequest());

        await result.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_ROUTE_REQUIRED");
    }

    [Fact]
    public async Task Delete_ShouldNotTouchAnotherScopesBinding()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("their-route", "scope-owner"));
        var http = CreateHttpContext(store);

        var result = await WorkflowWebhookBindingEndpoints.HandleDeleteAsync(
            http, "scope-intruder", "their-route");

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
        (await store.GetAsync("their-route")).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_WithStaleOwner_ShouldNotRemoveReassignedRoute()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("reassigned-route", "scope-old"));
        (await store.TryDeleteOwnedAsync("reassigned-route", "scope-old")).Should().BeTrue();
        await SeedAsync(store, BindingRecord("reassigned-route", "scope-new"));
        var staleHttp = CreateHttpContext(store);

        var result = await WorkflowWebhookBindingEndpoints.HandleDeleteAsync(
            staleHttp,
            "scope-old",
            "reassigned-route");

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
        (await store.GetAsync("reassigned-route"))!.ScopeId.Should().Be("scope-new");
    }

    [Fact]
    public async Task Put_WithDefinitionActorTarget_ShouldValidateScopeOwnershipAndRevision()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var reader = new FakeActorBindingReader(new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            "actor-hr01",
            "actor-hr01",
            string.Empty,
            "hr_onboarding_email_approval",
            "yaml",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive,
            ScopeId: "scope-1",
            RevisionId: "rev-7"));
        var http = CreateHttpContext(store, bindingReader: reader);

        static WorkflowWebhookBindingEndpoints.PutWorkflowWebhookBindingRequest Request(
            string? revision = null) => new(
            WorkflowName: null,
            SourceId: null,
            PromptTemplate: """{"record_id":"{{record_id}}","submit":false}""",
            PromptJsonPath: null,
            DeliveryIdHeader: "X-NyxID-Delivery-Id",
            DeliveryIdJsonPath: "event_id",
            HmacSecret: "delivery-signing-secret-at-least-32-bytes",
            HmacSignatureHeader: null,
            HmacTimestampHeader: null,
            MaxTimestampSkewSeconds: null,
            DefinitionActorId: "actor-hr01",
            TargetRevisionId: revision);

        // Target owned by another scope is rejected outright.
        var foreign = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http, "scope-intruder", "hr01-route", Request());
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)foreign).StatusCode
            .Should().Be(StatusCodes.Status403Forbidden);

        // A pinned revision that no longer matches the committed target fails.
        var staleRevision = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http, "scope-1", "hr01-route", Request(revision: "rev-6"));
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)staleRevision).StatusCode
            .Should().Be(StatusCodes.Status409Conflict);

        // Owner scope with the current revision binds; workflow name and the
        // committed revision are taken from the validated target.
        var ok = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http, "scope-1", "hr01-route", Request(revision: "rev-7"));
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)ok).StatusCode
            .Should().Be(StatusCodes.Status200OK);
        var stored = await store.GetAsync("hr01-route");
        stored!.DefinitionActorId.Should().Be("actor-hr01");
        stored.TargetRevisionId.Should().Be("rev-7");
        stored.WorkflowName.Should().Be("hr_onboarding_email_approval");
    }

    [Fact]
    public async Task Put_ShouldRejectRunActorTarget()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var runTarget = DefinitionBinding() with
        {
            ActorKind = WorkflowActorKind.Run,
            RunId = "run-1",
        };
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(runTarget));

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "hr01-route",
            ExactPutRequest());

        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode
            .Should().Be(StatusCodes.Status400BadRequest);
        (await store.GetAsync("hr01-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_ShouldRejectHmacSecretShorterThan32Utf8Bytes()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()));
        var request = ExactPutRequest() with { HmacSecret = "short-secret" };

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "hr01-route",
            request);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_SECRET_TOO_SHORT");
        (await store.GetAsync("hr01-route")).Should().BeNull();
    }

    [Fact]
    public async Task Put_ShouldRejectShortPreviousSecretAndHeaderOnlyDeliveryId()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()));

        var shortPrevious = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "hr01-route",
            ExactPutRequest() with { PreviousHmacSecret = "short" });
        await shortPrevious.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_PREVIOUS_SECRET_TOO_SHORT");

        http.Response.Body = new MemoryStream();
        var headerOnly = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "hr01-route",
            ExactPutRequest() with { DeliveryIdJsonPath = null });
        await headerOnly.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_DELIVERY_ID_MAPPING_REQUIRED");
    }

    [Fact]
    public async Task Put_ShouldRejectRouteReservedByStaticConfiguration_AfterCanonicalization()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        var options = new WorkflowWebhookIngressOptions();
        options.Bindings.Add(new WorkflowWebhookIngressBindingOptions { RouteKey = " HR01-ROUTE " });
        var http = CreateHttpContext(
            store,
            bindingReader: new FakeActorBindingReader(DefinitionBinding()),
            ingressOptions: options);

        var result = await WorkflowWebhookBindingEndpoints.HandlePutAsync(
            http,
            "scope-1",
            "hr01-route",
            ExactPutRequest());

        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode
            .Should().Be(StatusCodes.Status409Conflict);
        (await store.GetAsync("hr01-route")).Should().BeNull();
    }

    [Fact]
    public async Task Ingress_ShouldStartExactlyOneRun_ForDefinitionActorBindingWithDerivedRunDate()
    {
        // Issue 3444 acceptance shape: a persisted binding pointing at the
        // scope-published HR-01 definition actor receives a Base automation
        // JSON payload and starts exactly one run; the redelivered duplicate
        // is acknowledged without a second start. run_date comes from the
        // trusted ingress received_at (UTC+8) because Base automation cannot
        // supply "today"; operator_id is a binding constant.
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("hr01-route", "scope-1") with
        {
            WorkflowName = "hr_onboarding_email_approval",
            DefinitionActorId = "actor-hr01",
            TargetRevisionId = "rev-7",
            PromptTemplate =
                """{"record_id":"{{record_id}}","operator_id":"demo-operator","run_date":"{{@run_date}}","submit":false}""",
            DeliveryIdHeader = "X-NyxID-Delivery-Id",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
        });

        var dispatch = new RecordingDispatch();
        dispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("actor-1", "hr_onboarding_email_approval", "cmd-1", "corr-1"));
        var replayStore = new OnceOnlyReplayStore();
        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });

        async Task<int> DeliverAsync()
        {
            var http = CreateHttpContext(
                store,
                replayStore,
                new FakeActorBindingReader(DefinitionBinding()));
            var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","record_id":"rec-123"}""");
            http.Request.Body = new MemoryStream(body);
            http.Request.ContentType = "application/json";
            http.Request.Headers["X-NyxID-Delivery-Id"] = "delivery-1";
            SignNyxId(http, "secret-1", body);
            var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
                http,
                "hr01-route",
                new WorkflowWebhookIngressRequestBuilder(disabledOptions),
                dispatch,
                disabledOptions,
                NullLoggerFactory.Instance,
                CancellationToken.None);
            await result.ExecuteAsync(http);
            return http.Response.StatusCode;
        }

        (await DeliverAsync()).Should().Be(StatusCodes.Status202Accepted);
        (await DeliverAsync()).Should().Be(StatusCodes.Status202Accepted);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        command.Source.ActorId.Should().Be("actor-hr01");
        command.ScopeId.Should().Be("scope-1");
        command.ResolvedDefinitionBinding.Should().NotBeNull();
        command.ResolvedDefinitionBinding!.RevisionId.Should().Be("rev-7");
        command.Prompt.Should().MatchRegex(
            """^\{"record_id":"rec-123","operator_id":"demo-operator","run_date":"\d{4}-\d{2}-\d{2}","submit":false\}$""");
    }

    [Fact]
    public async Task Ingress_ShouldAcceptSignatureFromPreviousSecret_DuringRotation()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("hr01-route", "scope-1") with
        {
            WorkflowName = "hr_onboarding_email_approval",
            DefinitionActorId = "actor-hr01",
            TargetRevisionId = "rev-7",
            HmacSecret = "rotated-new-secret",
            PreviousHmacSecret = "secret-1",
            DeliveryIdHeader = "X-NyxID-Delivery-Id",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
        });

        var dispatch = new RecordingDispatch();
        dispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("actor-1", "wf", "cmd-1", "corr-1"));
        var http = CreateHttpContext(
            store,
            new AcceptingReplayStore(),
            new FakeActorBindingReader(DefinitionBinding()));
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","record_id":"rec-123"}""");
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        http.Request.Headers["X-NyxID-Delivery-Id"] = "delivery-1";
        // Sender still signs with the retired secret mid-rotation.
        SignNyxId(http, "secret-1", body);

        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "hr01-route",
            new WorkflowWebhookIngressRequestBuilder(disabledOptions),
            dispatch,
            disabledOptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        dispatch.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task Ingress_ShouldDispatchViaDynamicBinding_WhenStaticIngressDisabled()
    {
        // The whole point of dynamic bindings: no appsettings change, no
        // Enabled flag — a scope-registered binding is live on its own.
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("hr01-route", "scope-1") with
        {
            WorkflowName = "hr_onboarding_email_approval",
            DefinitionActorId = "actor-hr01",
            TargetRevisionId = "rev-7",
            PromptTemplate = """{"record_id":"{{record_id}}","submit":false}""",
            DeliveryIdHeader = "X-NyxID-Delivery-Id",
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
        });

        var dispatch = new RecordingDispatch();
        dispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("actor-1", "hr_onboarding_email_approval", "cmd-1", "corr-1"));
        var http = CreateHttpContext(
            store,
            new AcceptingReplayStore(),
            new FakeActorBindingReader(DefinitionBinding()));
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","record_id":"rec-123"}""");
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        http.Request.Headers["X-NyxID-Delivery-Id"] = "delivery-1";
        SignNyxId(http, "secret-1", body);

        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "hr01-route",
            new WorkflowWebhookIngressRequestBuilder(disabledOptions),
            dispatch,
            disabledOptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        dispatch.Commands.Should().ContainSingle();
        dispatch.Commands[0].Prompt.Should().Be("""{"record_id":"rec-123","submit":false}""");
        dispatch.Commands[0].Source.WorkflowName.Should().Be("hr_onboarding_email_approval");
        dispatch.Commands[0].ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task Ingress_ShouldFailClosed_WhenPinnedRevisionDrifts()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("hr01-route", "scope-1") with
        {
            WorkflowName = "hr_onboarding_email_approval",
            DefinitionActorId = "actor-hr01",
            TargetRevisionId = "rev-7",
            PromptJsonPath = "record_id",
            PromptTemplate = null,
            HmacSignatureHeader = "X-NyxID-Signature",
            HmacTimestampHeader = "X-NyxID-Timestamp",
        });
        var dispatch = new RecordingDispatch();
        var replay = new CountingReplayStore();
        var reader = new FakeActorBindingReader(DefinitionBinding(revisionId: "rev-8"));
        var http = CreateHttpContext(
            store,
            replay,
            reader);
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","record_id":"rec-123"}""");
        http.Request.Body = new MemoryStream(body);
        SignNyxId(http, "secret-1", body);

        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "HR01-ROUTE",
            new WorkflowWebhookIngressRequestBuilder(disabledOptions),
            dispatch,
            disabledOptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await ReadBodyAsync(http.Response)).Should().Contain("WEBHOOK_TARGET_REVISION_DRIFT");
        reader.ReadCount.Should().Be(1);
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Ingress_ShouldAuthenticateBeforeReadingPinnedTarget()
    {
        var store = new InMemoryWorkflowWebhookBindingStore();
        await SeedAsync(store, BindingRecord("hr01-route", "scope-1") with
        {
            WorkflowName = "hr_onboarding_email_approval",
            DefinitionActorId = "actor-hr01",
            TargetRevisionId = "rev-7",
            PromptJsonPath = "record_id",
            PromptTemplate = null,
        });
        var dispatch = new RecordingDispatch();
        var replay = new CountingReplayStore();
        var reader = new FakeActorBindingReader(DefinitionBinding(revisionId: "rev-8"));
        var http = CreateHttpContext(store, replay, reader);
        var body = Encoding.UTF8.GetBytes("""{"event_id":"delivery-1","record_id":"rec-123"}""");
        http.Request.Body = new MemoryStream(body);
        http.Request.Headers["X-Aevatar-Timestamp"] =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        http.Request.Headers["X-Aevatar-Signature"] = "sha256=invalid";

        var disabledOptions = Options.Create(new WorkflowWebhookIngressOptions { Enabled = false });
        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "hr01-route",
            new WorkflowWebhookIngressRequestBuilder(disabledOptions),
            dispatch,
            disabledOptions,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        reader.ReadCount.Should().Be(0);
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    private static WorkflowWebhookBindingRecord BindingRecord(string routeKey, string scopeId) => new(
        RouteKey: routeKey,
        ScopeId: scopeId,
        WorkflowName: "wf",
        SourceId: "src",
        PromptTemplate: "{}",
        PromptJsonPath: null,
        DeliveryIdHeader: "X-Delivery",
        DeliveryIdJsonPath: "event_id",
        HmacSecret: "secret-1",
        HmacSignatureHeader: null,
        HmacTimestampHeader: null,
        MaxTimestampSkewSeconds: 300,
        UpdatedAtUnixMs: 1);

    private static async Task SeedAsync(
        IWorkflowWebhookBindingStore store,
        WorkflowWebhookBindingRecord record)
    {
        (await store.TryPutOwnedAsync(record)).Should().BeTrue();
    }

    private static WorkflowActorBinding DefinitionBinding(
        string scopeId = "scope-1",
        string revisionId = "rev-7") => new(
        WorkflowActorKind.Definition,
        "actor-hr01",
        "actor-hr01",
        string.Empty,
        "hr_onboarding_email_approval",
        "yaml",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive,
        ScopeId: scopeId,
        SourceKind: "service_revision",
        WorkflowId: "workflow-hr01",
        RevisionId: revisionId);

    private static WorkflowWebhookBindingEndpoints.PutWorkflowWebhookBindingRequest ExactPutRequest() => new(
        WorkflowName: "hr_onboarding_email_approval",
        SourceId: "nyxid-trigger",
        PromptTemplate: "{}",
        PromptJsonPath: null,
        DeliveryIdHeader: "X-NyxID-Delivery-Id",
        DeliveryIdJsonPath: "event_id",
        HmacSecret: "delivery-signing-secret-at-least-32-bytes",
        HmacSignatureHeader: "X-NyxID-Signature",
        HmacTimestampHeader: "X-NyxID-Timestamp",
        MaxTimestampSkewSeconds: 300,
        DefinitionActorId: "actor-hr01",
        TargetRevisionId: "rev-7");

    private static DefaultHttpContext CreateHttpContext(
        IWorkflowWebhookBindingStore? bindingStore = null,
        IWorkflowWebhookReplayStore? replayStore = null,
        IWorkflowActorBindingReader? bindingReader = null,
        WorkflowWebhookIngressOptions? ingressOptions = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "false",
                })
                .Build());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new DevelopmentHostEnvironment());
        if (bindingStore != null)
            services.AddSingleton(bindingStore);
        if (replayStore != null)
            services.AddSingleton(replayStore);
        if (bindingReader != null)
            services.AddSingleton(bindingReader);
        if (ingressOptions != null)
            services.AddSingleton<IOptions<WorkflowWebhookIngressOptions>>(Options.Create(ingressOptions));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static void SignNyxId(HttpContext http, string secret, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var payload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        http.Request.Headers["X-NyxID-Timestamp"] = timestamp;
        http.Request.Headers["X-NyxID-Signature"] =
            "sha256=" + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    private sealed class RecordingDispatch
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];

        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound);

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly WorkflowActorBinding _binding;

        public FakeActorBindingReader(WorkflowActorBinding binding) => _binding = binding;

        public int ReadCount { get; private set; }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(string.Equals(actorId, _binding.ActorId, StringComparison.Ordinal)
                ? _binding
                : null);
        }
    }

    /// <summary>First delivery id is admitted; every replay is a duplicate.</summary>
    private sealed class OnceOnlyReplayStore : IWorkflowWebhookReplayStore
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_seen.Add(request.DeliveryId)
                ? new WorkflowWebhookReplayAdmission(WorkflowWebhookReplayAdmissionStatus.Admitted)
                : new WorkflowWebhookReplayAdmission(
                    WorkflowWebhookReplayAdmissionStatus.DuplicateCompleted,
                    ExistingCommandId: "cmd-1",
                    ExistingCorrelationId: "corr-1"));

        public ValueTask ReleaseAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class AcceptingReplayStore : IWorkflowWebhookReplayStore
    {
        public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.Admitted));

        public ValueTask ReleaseAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class CountingReplayStore : IWorkflowWebhookReplayStore
    {
        public List<WorkflowWebhookReplayAdmissionRequest> Requests { get; } = [];

        public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.Admitted));
        }

        public ValueTask ReleaseAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class DevelopmentHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Microsoft.Extensions.Hosting.Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
