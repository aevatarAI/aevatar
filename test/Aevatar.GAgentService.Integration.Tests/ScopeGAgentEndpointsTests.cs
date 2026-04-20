using System.Net;
using System.Reflection;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Type = System.Type;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

using AiTextEnd = Aevatar.AI.Abstractions.TextMessageEndEvent;
using AiTextContent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextReasoning = Aevatar.AI.Abstractions.TextMessageReasoningEvent;
using AiTextStart = Aevatar.AI.Abstractions.TextMessageStartEvent;
using AiToolCall = Aevatar.AI.Abstractions.ToolCallEvent;
using AiToolResult = Aevatar.AI.Abstractions.ToolResultEvent;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeGAgentEndpointsTests
{
    [Fact]
    public void MapScopeGAgentCapabilityEndpoints_ShouldRegisterExpectedRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        using var app = builder.Build();

        app.MapScopeGAgentCapabilityEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .Where(r => r != null)
            .ToHashSet(StringComparer.Ordinal);

        routes.Should().Contain(route => route.Contains("gagent-types"));
        routes.Should().Contain(route => route.Contains("gagent/draft-run"));
        routes.Should().Contain(route => route.Contains("gagent-actors"));
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldRejectUnknownActorTypeWithJsonError()
    {
        var runtime = new FakeActorRuntime(_ => null);
        var subscription = new FakeActorEventSubscriptionProvider();
        var actorStore = new RecordingGAgentActorStore();
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.IamNotReal, Aevatar.IamNotReal",
                "hello"),
            runtime,
            subscription,
            actorStore,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        context.Response.ContentType.Should().Be("application/json");
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("UNKNOWN_GAGENT_TYPE");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldTimeoutWhenNoCompletionEventReceived()
    {
        var actor = new FakeActor("existing-actor");
        var runtime = new FakeActorRuntime(id => id == actor.Id ? actor : null);
        var subscription = new FakeActorEventSubscriptionProvider();
        var actorStore = new RecordingGAgentActorStore();
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                "hello",
                PreferredActorId: actor.Id,
                TimeoutMs: 1),
            runtime,
            subscription,
            actorStore,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        context.Response.ContentType.Should().StartWith("text/event-stream");
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("GAgent draft-run timed out");
        actorStore.AddedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldFinishWhenEnvelopeEmitsCompletionEvent()
    {
        var actor = new FakeActor("existing-actor");
        var runtime = new FakeActorRuntime(id => id == actor.Id ? actor : null);
        var subscription = new FakeActorEventSubscriptionProvider(
            BuildEventEnvelope(new AiTextEnd
            {
                Content = string.Empty,
                SessionId = "session-1",
            }));
        var actorStore = new RecordingGAgentActorStore();
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext("Bearer token-abc");

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                "hello",
                PreferredActorId: actor.Id,
                TimeoutMs: 200),
            runtime,
            subscription,
            actorStore,
            logger,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("runStarted");
        body.Should().Contain("runFinished");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldRejectBlankActorTypeAndPrompt()
    {
        var runtime = new FakeActorRuntime(_ => null);
        var subscription = new FakeActorEventSubscriptionProvider();
        var actorStore = new RecordingGAgentActorStore();
        var logger = LoggerFactory.Create(_ => { });

        var missingTypeContext = CreateDraftRunContext();
        await InvokeHandleDraftRunAsync(
            missingTypeContext,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(" ", "hello"),
            runtime,
            subscription,
            actorStore,
            logger,
            CancellationToken.None);
        missingTypeContext.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var missingPromptContext = CreateDraftRunContext();
        await InvokeHandleDraftRunAsync(
            missingPromptContext,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                " "),
            runtime,
            subscription,
            actorStore,
            logger,
            CancellationToken.None);
        missingPromptContext.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldWriteAuthRequiredErrorWhenActorThrows()
    {
        var actor = new ThrowingActor("auth-actor", new NyxIdAuthenticationRequiredException("sign in"));
        var runtime = new FakeActorRuntime(id => id == actor.Id ? actor : null, actor);
        var subscription = new FakeActorEventSubscriptionProvider();
        var actorStore = new RecordingGAgentActorStore();
        var logger = LoggerFactory.Create(_ => { });
        var context = CreateDraftRunContext();

        await InvokeHandleDraftRunAsync(
            context,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core",
                "hello",
                PreferredActorId: actor.Id,
                TimeoutMs: 50),
            runtime,
            subscription,
            actorStore,
            logger,
            CancellationToken.None);

        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("authentication_required");
        body.Should().Contain("NyxID authentication required");
    }

    [Fact]
    public void ResolveAgentType_ShouldFindAndNotFindTypes()
    {
        ScopeGAgentEndpoints.ResolveAgentType("Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core").Should().NotBeNull();
        ScopeGAgentEndpoints.ResolveAgentType("Aevatar.IamNotReal, Aevatar.IamNotReal").Should().BeNull();
    }

    [Fact]
    public void TryMapEnvelopeToAguiEvent_ShouldMapAIAndToolingEvents()
    {
        var textStart = TryMap(BuildEventEnvelope(new AiTextStart { SessionId = "s1", AgentId = "agent-1" }));
        textStart!.TextMessageStart.Should().NotBeNull();
        textStart.TextMessageStart!.MessageId.Should().Be("s1");

        var textContent = TryMap(BuildEventEnvelope(new AiTextContent { Delta = "d", SessionId = "s1" }));
        textContent!.TextMessageContent.Should().NotBeNull();
        textContent.TextMessageContent!.Delta.Should().Be("d");

        var reasoning = TryMap(BuildEventEnvelope(new AiTextReasoning { Delta = "r", SessionId = "s1" }));
        reasoning!.Custom.Should().NotBeNull();
        reasoning.Custom!.Name.Should().Be("TEXT_MESSAGE_REASONING");

        var textEnd = TryMap(BuildEventEnvelope(new AiTextEnd { Content = "done", SessionId = "s1" }));
        textEnd!.TextMessageEnd.Should().NotBeNull();

        var textEndError = TryMap(BuildEventEnvelope(new AiTextEnd { Content = "[[AEVATAR_LLM_ERROR]] boom", SessionId = "s2" }));
        textEndError!.RunError.Should().NotBeNull();
        textEndError.RunError!.Message.Should().Be("boom");

        var textEndFailed = TryMap(BuildEventEnvelope(new AiTextEnd { Content = "LLM request failed: upstream", SessionId = "s2" }));
        textEndFailed!.RunError.Should().NotBeNull();
        textEndFailed.RunError!.Message.Should().Be("LLM request failed: upstream");

        var toolCall = TryMap(BuildEventEnvelope(new AiToolCall
        {
            ToolName = "search",
            CallId = "call-1",
        }));
        toolCall!.ToolCallStart.Should().NotBeNull();

        var toolResult = TryMap(BuildEventEnvelope(new AiToolResult
        {
            CallId = "call-1",
            ResultJson = "{\"ok\":true}",
        }));
        toolResult!.ToolCallEnd.Should().NotBeNull();

        var approval = TryMap(BuildToolApprovalEventEnvelope(new ToolApprovalRequestEvent
        {
            RequestId = "req-1",
            SessionId = "s1",
            ToolName = "connector.run",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            IsDestructive = true,
            TimeoutSeconds = 30,
        }));
        approval.Custom.Should().NotBeNull();
        approval.Custom!.Name.Should().Be("TOOL_APPROVAL_REQUEST");
        approval.Custom.Payload.Should().NotBeNull();
        var approvalStruct = approval.Custom.Payload!.Unpack<Struct>();
        approvalStruct.Fields["toolName"].StringValue.Should().Be("connector.run");
        approvalStruct.Fields["isDestructive"].BoolValue.Should().BeTrue();
        approvalStruct.Fields["timeoutSeconds"].NumberValue.Should().Be(30);

        var agui = TryMap(BuildEventEnvelope(new AGUIEvent
        {
            TextMessageEnd = new Aevatar.Presentation.AGUI.TextMessageEndEvent { MessageId = "m2" }
        }));
        agui.TextMessageEnd.Should().NotBeNull();

        var none = TryMap(new EventEnvelope());
        none.Should().BeNull();
    }

    [Fact]
    public void TryMapEnvelopeToAguiEvent_ShouldHandleUnknownPayloadAndWrappedAguiEvent()
    {
        TryMap(new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "unknown" }),
        }).Should().BeNull();

        var wrapped = new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "thread-1",
                RunId = "run-1",
            },
        };

        TryMap(new EventEnvelope
        {
            Payload = Any.Pack(wrapped),
        }).Should().BeEquivalentTo(wrapped);
    }

    [Fact]
    public void BuildToolApprovalStruct_ShouldHandleDecodeFailure()
    {
        var invalidAny = new Any
        {
            TypeUrl = "type.googleapis.com/aevatar.ai.ToolApprovalRequestEvent",
            Value = ByteString.CopyFromUtf8("broken"),
        };

        var structure = InvokeBuildToolApprovalStruct(invalidAny);
        structure.Fields.Should().ContainKey("error");
        structure.Fields["error"].StringValue.Should().Contain("Failed to decode approval request");
    }

    [Fact]
    public void ExtractBearerToken_ShouldParseBearerHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer token-123";
        var actual = InvokeExtractBearerToken(context);
        actual.Should().Be("token-123");
    }

    [Fact]
    public void ExtractBearerToken_ShouldReturnNullWithoutBearerPrefix()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Basic abc";
        var actual = InvokeExtractBearerToken(context);
        actual.Should().BeNull();
    }

    [Fact]
    public void IsNyxIdAuthenticationRequired_ShouldDetectDirectInnerAndAggregate()
    {
        IsNyxIdAuthenticationRequired(new NyxIdAuthenticationRequiredException("test")).Should().BeTrue();
        IsNyxIdAuthenticationRequired(new InvalidOperationException("bad", new NyxIdAuthenticationRequiredException("test"))).Should().BeTrue();
        IsNyxIdAuthenticationRequired(new AggregateException([new InvalidOperationException("x"), new NyxIdAuthenticationRequiredException("test")])).Should().BeTrue();
        IsNyxIdAuthenticationRequired(new InvalidOperationException("nope")).Should().BeFalse();
    }

    [Fact]
    public async Task HandleActorStoreEndpoints_ShouldCoverSuccessAndFailureBranches()
    {
        var store = new RecordingGAgentActorStore
        {
            Actors =
            [
                new GAgentActorGroup("gagent-a", ["actor-1", "actor-2"])
            ]
        };
        var logger = LoggerFactory.Create(_ => { });

        var listResult = await InvokeHandleListActorsAsync("scope-a", store, logger, CancellationToken.None);
        ((IStatusCodeHttpResult)listResult).StatusCode.Should().Be((int)HttpStatusCode.OK);
        store.LastRequestedScopeId.Should().Be("scope-a");

        var addResult = await InvokeHandleAddActorAsync(
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest("gagent-a", "actor-3"),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)addResult).StatusCode.Should().Be((int)HttpStatusCode.OK);
        store.AddedActors.Should().ContainSingle(x =>
            x.ScopeId == "scope-a" &&
            x.GAgentType == "gagent-a" &&
            x.ActorId == "actor-3");

        var removeResult = await InvokeHandleRemoveActorAsync(
            "scope-a",
            "actor-1",
            "gagent-a",
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)removeResult).StatusCode.Should().Be((int)HttpStatusCode.OK);
        store.RemovedActors.Should().ContainSingle(x =>
            x.ScopeId == "scope-a" &&
            x.GAgentType == "gagent-a" &&
            x.ActorId == "actor-1");

        var invalidAdd = await InvokeHandleAddActorAsync(
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest(" ", " "),
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)invalidAdd).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var invalidRemove = await InvokeHandleRemoveActorAsync(
            "scope-a",
            "actor-1",
            " ",
            store,
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)invalidRemove).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var throwingStore = new RecordingGAgentActorStore { ThrowOnGet = new InvalidOperationException("get failed") };
        var throwList = await InvokeHandleListActorsAsync("scope-a", throwingStore, logger, CancellationToken.None);
        ((IStatusCodeHttpResult)throwList).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var throwAdd = await InvokeHandleAddActorAsync(
            "scope-a",
            new ScopeGAgentEndpoints.AddGAgentActorHttpRequest("gagent-a", "actor-1"),
            new RecordingGAgentActorStore { ThrowOnAdd = new InvalidOperationException("add failed") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwAdd).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        var throwRemove = await InvokeHandleRemoveActorAsync(
            "scope-a",
            "actor-1",
            "gagent-a",
            new RecordingGAgentActorStore { ThrowOnRemove = new InvalidOperationException("remove failed") },
            logger,
            CancellationToken.None);
        ((IStatusCodeHttpResult)throwRemove).StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public void ToCamelCaseAndStripEventSuffix_ShouldTransformWords()
    {
        InvokeToCamelCase("").Should().BeEmpty();
        InvokeToCamelCase("TextEvent").Should().Be("textEvent");

        InvokeStripEventSuffix("ToolResultEvent").Should().Be("ToolResult");
        InvokeStripEventSuffix("NoSuffix").Should().Be("NoSuffix");
    }

    [Fact]
    public void HandleListGAgentTypesAsync_ShouldReturnOkResult()
    {
        var result = InvokeHandleListGAgentTypesAsync();
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    private static AGUIEvent? TryMap(EventEnvelope envelope)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            nameof(ScopeGAgentEndpoints.TryMapEnvelopeToAguiEvent),
            BindingFlags.NonPublic | BindingFlags.Static);
        return (AGUIEvent?)method!.Invoke(null, new object[] { envelope });
    }

    private static Struct InvokeBuildToolApprovalStruct(Any payload)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "BuildToolApprovalStruct",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (Struct)method!.Invoke(null, new object[] { payload })!;
    }

    private static string? InvokeExtractBearerToken(HttpContext context)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "ExtractBearerToken",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method!.Invoke(null, new object[] { context });
    }

    private static bool IsNyxIdAuthenticationRequired(Exception ex)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "IsNyxIdAuthenticationRequired",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)method!.Invoke(null, new object[] { ex })!;
    }

    private static string InvokeToCamelCase(string value)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "ToCamelCase",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { value })!;
    }

    private static string InvokeStripEventSuffix(string value)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "StripEventSuffix",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { value })!;
    }

    private static async Task<IResult> InvokeHandleListActorsAsync(
        string scopeId,
        IGAgentActorStore actorStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleListActorsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return await (Task<IResult>)method!.Invoke(null, new object[]
        {
            scopeId,
            actorStore,
            loggerFactory,
            ct,
        })!;
    }

    private static IResult InvokeHandleListGAgentTypesAsync()
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleListGAgentTypesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (IResult)method!.Invoke(null, [])!;
    }

    private static async Task<IResult> InvokeHandleAddActorAsync(
        string scopeId,
        ScopeGAgentEndpoints.AddGAgentActorHttpRequest request,
        IGAgentActorStore actorStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleAddActorAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return await (Task<IResult>)method!.Invoke(null, new object[]
        {
            scopeId,
            request,
            actorStore,
            loggerFactory,
            ct,
        })!;
    }

    private static async Task<IResult> InvokeHandleRemoveActorAsync(
        string scopeId,
        string actorId,
        string? gagentType,
        IGAgentActorStore actorStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleRemoveActorAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        return await (Task<IResult>)method!.Invoke(null, new object[]
        {
            scopeId,
            actorId,
            gagentType,
            actorStore,
            loggerFactory,
            ct,
        })!;
    }

    private static async Task InvokeHandleDraftRunAsync(
        HttpContext context,
        string scopeId,
        ScopeGAgentEndpoints.GAgentDraftRunHttpRequest request,
        IActorRuntime actorRuntime,
        IActorEventSubscriptionProvider subscriptionProvider,
        IGAgentActorStore actorStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var method = typeof(ScopeGAgentEndpoints).GetMethod(
            "HandleDraftRunAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        await (Task)method!.Invoke(
            null,
            new object[]
            {
                context,
                scopeId,
                request,
                actorRuntime,
                subscriptionProvider,
                actorStore,
                loggerFactory,
                ct,
            })!;
    }

    private static HttpContext CreateDraftRunContext(string? authorization = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private static EventEnvelope BuildEventEnvelope(IMessage message)
    {
        return new EventEnvelope { Payload = Any.Pack(message) };
    }

    private static EventEnvelope BuildToolApprovalEventEnvelope(ToolApprovalRequestEvent approvalRequest)
    {
        return BuildEventEnvelope(approvalRequest);
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class RecordingGAgentActorStore : IGAgentActorStore
    {
        public List<GAgentActorGroup> Actors { get; set; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> RemovedActors { get; } = [];
        public Exception? ThrowOnGet { get; set; }
        public Exception? ThrowOnAdd { get; set; }
        public Exception? ThrowOnRemove { get; set; }
        public string? LastRequestedScopeId { get; private set; }

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet != null) throw ThrowOnGet;
            return Task.FromResult<IReadOnlyList<GAgentActorGroup>>(Actors);
        }

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet != null) throw ThrowOnGet;
            LastRequestedScopeId = scopeId;
            return Task.FromResult<IReadOnlyList<GAgentActorGroup>>(Actors);
        }

        public Task AddActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd != null)
                throw ThrowOnAdd;

            AddedActors.Add((string.Empty, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task AddActorAsync(
            string scopeId,
            string gagentType,
            string actorId,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd != null)
                throw ThrowOnAdd;

            AddedActors.Add((scopeId, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task RemoveActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRemove != null)
                throw ThrowOnRemove;

            RemovedActors.Add((string.Empty, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task RemoveActorAsync(
            string scopeId,
            string gagentType,
            string actorId,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnRemove != null)
                throw ThrowOnRemove;

            RemovedActors.Add((scopeId, gagentType, actorId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        private readonly Func<string, IActor?> _getAsync;
        private readonly IActor _createdActor;

        public FakeActorRuntime(Func<string, IActor?> getAsync, IActor? createdActor = null)
        {
            _getAsync = getAsync;
            _createdActor = createdActor ?? new FakeActor("created");
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_createdActor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_createdActor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            return Task.FromResult(_getAsync(id));
        }

        public Task<bool> ExistsAsync(string id)
        {
            return Task.FromResult(_getAsync(id) is not null);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent();
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class ThrowingActor : IActor
    {
        private readonly Exception _exception;

        public ThrowingActor(string id, Exception exception)
        {
            Id = id;
            _exception = exception;
            Agent = new FakeAgent();
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.FromException(_exception);

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public string Id { get; } = "agent";
    }

    private sealed class FakeActorEventSubscriptionProvider : IActorEventSubscriptionProvider
    {
        private readonly EventEnvelope[] _envelopes;

        public FakeActorEventSubscriptionProvider(params EventEnvelope[] envelopes)
        {
            _envelopes = envelopes;
        }

        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            if (ct.IsCancellationRequested)
                return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());

            if (typeof(TMessage) == typeof(EventEnvelope) && _envelopes.Length > 0)
            {
                var eventHandler = (Func<EventEnvelope, Task>)(object)handler;
                _ = Task.Run(async () =>
                {
                    foreach (var envelope in _envelopes)
                    {
                        ct.ThrowIfCancellationRequested();
                        await eventHandler(envelope);
                    }
                }, ct);
            }

            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
