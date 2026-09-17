using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatControlEndpointsTests
{
    private const string StopRoute =
        "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:stop";
    private const string SteeringRoute =
        "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:steer";
    private const string RetryRoute =
        "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}/turns/{turnId}/steps/{stepId}:retry";
    private const string SkipRoute =
        "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}/turns/{turnId}/steps/{stepId}:skip";
    private const string CanaryEffectFaultArmRoute =
        "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:arm-effect-fault-canary";
    private const string ShareOpsOwnerSubject = "ce646b72-dd49-4ea8-bc1e-8273672c102c";
    private const string ValidStopBody = """
        {
          "turnId": "turn-alpha",
          "stopRequestId": "stop-alpha",
          "clientRequestId": "client-stop-alpha",
          "expectedStateVersion": 7
        }
        """;

    [Fact]
    public void ControlEndpoints_ShouldUseNarrowAcceptedOnlyBoundary()
    {
        var assembly = typeof(NyxIdChatEndpoints).Assembly;
        assembly.GetType("Aevatar.GAgents.NyxidChat.INyxIdChatControlCommandPort")
            .Should().NotBeNull();
        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatControlCommandPort")
            .Should().NotBeNull();
        Enum.GetNames<ScopeResourceOperation>().Should().Contain("Control");

        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Controls.cs"));
        source.Should().Contain("INyxIdChatControlCommandPort");
        source.Should().NotContain("[FromServices] IActorRuntime");
        source.Should().NotContain("IEventStore");
        source.Should().NotContain("INyxIdChatConversationStateQueryPort");
    }

    [Fact]
    public async Task Stop_ShouldAuthorizeDispatchAndReturnAcceptedReceipt()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            StopRoute,
            ConversationRouteValues(),
            ValidStopBody,
            admission,
            dispatch);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Location.Should().Be(
            "/api/scopes/scope-alpha/nyxid-chat/conversations/conversation-alpha/state");
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("accepted");
        root.GetProperty("requestId").GetString().Should().Be("stop-alpha");
        root.GetProperty("commandId").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("stateUrl").GetString().Should().Be(response.Location);

        admission.Targets.Should().ContainSingle(target =>
            target.ScopeId == "scope-alpha" &&
            target.ActorId == "conversation-alpha" &&
            target.Operation == ScopeResourceOperation.Control);
        var dispatched = dispatch.Dispatches.Should().ContainSingle().Which;
        dispatched.ActorId.Should().Be("conversation-alpha");
        var command = dispatched.Envelope.Payload.Unpack<NyxIdChatStopCommand>();
        command.ScopeId.Should().Be("scope-alpha");
        command.ConversationActorId.Should().Be("conversation-alpha");
        command.TurnId.Should().Be("turn-alpha");
        command.StopRequestId.Should().Be("stop-alpha");
        command.ClientRequestId.Should().Be("client-stop-alpha");
        command.ExpectedStateVersion.Should().Be(7);
        command.CommandId.Should().Be(root.GetProperty("commandId").GetString());
        command.CorrelationId.Should().Be(root.GetProperty("correlationId").GetString());
        dispatched.Envelope.Id.Should().Be(command.CommandId);
        dispatched.Envelope.Route.Direct.TargetActorId.Should().Be("conversation-alpha");
        dispatched.Envelope.Propagation.CorrelationId.Should().Be(command.CorrelationId);
        dispatched.Envelope.Timestamp.Should().NotBeNull();
    }

    [Fact]
    public async Task Stop_ShouldRejectMalformedIdentityBeforeAdmissionOrDispatch()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();
        var routeValues = ConversationRouteValues();
        routeValues["actorId"] = "conversation/escape";

        var response = await ExecuteAsync(
            StopRoute,
            routeValues,
            ValidStopBody,
            admission,
            dispatch);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        admission.Targets.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_ShouldRejectScopeMismatchBeforeAdmissionOrDispatch()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            StopRoute,
            ConversationRouteValues(),
            ValidStopBody,
            admission,
            dispatch,
            authenticatedScopeId: "scope-other");

        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("SCOPE_ACCESS_DENIED");
        admission.Targets.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScopeResourceAdmissionStatus.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ScopeResourceAdmissionStatus.Denied, StatusCodes.Status403Forbidden)]
    [InlineData(ScopeResourceAdmissionStatus.ScopeMismatch, StatusCodes.Status403Forbidden)]
    [InlineData(ScopeResourceAdmissionStatus.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public async Task Stop_ShouldMapAdmissionFailureBeforeDispatch(
        ScopeResourceAdmissionStatus admissionStatus,
        int expectedStatusCode)
    {
        var admission = new RecordingAdmissionPort
        {
            Result = new ScopeResourceAdmissionResult(admissionStatus),
        };
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            StopRoute,
            ConversationRouteValues(),
            ValidStopBody,
            admission,
            dispatch);

        response.StatusCode.Should().Be(expectedStatusCode);
        admission.Targets.Should().ContainSingle(target =>
            target.ScopeId == "scope-alpha" &&
            target.ActorId == "conversation-alpha" &&
            target.Operation == ScopeResourceOperation.Control);
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Steering_ShouldMapTransientCapabilityWithoutReturningSecret()
    {
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            SteeringRoute,
            ConversationRouteValues(),
            """
            {
              "turnId": "turn-alpha",
              "steeringId": "steering-alpha",
              "clientRequestId": "client-steering-alpha",
              "instruction": "Use the safer read-only approach.",
              "inputParts": [{"type":"text","text":"Preserve completed work."}],
              "expectedStateVersion": 9
            }
            """,
            new RecordingAdmissionPort(),
            dispatch,
            accessToken: "steering-runtime-token-alpha");

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().NotContain("steering-runtime-token-alpha");
        var command = dispatch.Dispatches.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatSteeringCommand>();
        command.TurnId.Should().Be("turn-alpha");
        command.SteeringId.Should().Be("steering-alpha");
        command.ClientRequestId.Should().Be("client-steering-alpha");
        command.Instruction.Should().Be("Use the safer read-only approach.");
        command.InputParts.Should().ContainSingle(part =>
            part.Kind == ChatContentPartKind.Text &&
            part.Text == "Preserve completed work.");
        command.ExpectedStateVersion.Should().Be(9);
        command.LlmControl.NyxIdAccessToken.Should().Be("steering-runtime-token-alpha");
        command.ToolContext.Credentials.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        command.ToolContext.Credentials.NyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKindPayload.ProxyDelegation);
    }

    [Fact]
    public async Task CanaryEffectFaultArm_DefaultDisabled_ShouldReturnNotFoundWithoutAdmission()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            CanaryEffectFaultArmRoute,
            ConversationRouteValues(),
            ValidCanaryEffectFaultArmBody(),
            admission,
            dispatch,
            authenticatedScopeId: "scope-alpha",
            authenticatedOwnerSubject: ShareOpsOwnerSubject);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        admission.Targets.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task CanaryEffectFaultArm_NonAllowlistedOwner_ShouldReturnNotFoundWithoutAdmission()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            CanaryEffectFaultArmRoute,
            ConversationRouteValues(),
            ValidCanaryEffectFaultArmBody(),
            admission,
            dispatch,
            authenticatedScopeId: "scope-alpha",
            authenticatedOwnerSubject: "owner-not-allowed",
            canaryOptions: NyxIdChatCanaryEffectFaultOptions.EnabledFor(ShareOpsOwnerSubject));

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        admission.Targets.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task CanaryEffectFaultArm_ShareOpsOwner_ShouldDispatchExactAcceptedCommand()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            CanaryEffectFaultArmRoute,
            ConversationRouteValues(),
            ValidCanaryEffectFaultArmBody(),
            admission,
            dispatch,
            authenticatedScopeId: "scope-alpha",
            authenticatedOwnerSubject: ShareOpsOwnerSubject,
            canaryOptions: NyxIdChatCanaryEffectFaultOptions.EnabledFor(ShareOpsOwnerSubject));

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        admission.Targets.Should().ContainSingle(target =>
            target.ScopeId == "scope-alpha" &&
            target.ActorId == "conversation-alpha" &&
            target.Operation == ScopeResourceOperation.Control);
        var dispatched = dispatch.Dispatches.Should().ContainSingle().Which;
        var command = dispatched.Envelope.Payload.Unpack<NyxIdChatCanaryEffectFaultArmCommand>();
        command.ScopeId.Should().Be("scope-alpha");
        command.ConversationActorId.Should().Be("conversation-alpha");
        command.ArmId.Should().Be("arm-alpha");
        command.ClientRequestId.Should().Be("client-arm-alpha");
        command.SourceOperationKey.Should().BeEquivalentTo(new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            OperationId = "operation-alpha",
            OperationGeneration = 1,
        });
        command.ServiceInstanceId.Should().Be("connected-service-alpha");
        command.OwnerSubject.Should().Be(ShareOpsOwnerSubject);
        command.ExpectedStateVersion.Should().Be(23);
        command.CommandId.Should().NotBeNullOrWhiteSpace();
        command.CorrelationId.Should().NotBeNullOrWhiteSpace();
        dispatched.ActorId.Should().Be("conversation-alpha");
        dispatched.Envelope.Id.Should().Be(command.CommandId);
    }

    [Fact]
    public async Task Steering_ShouldRequireTransientCapabilityBeforeAdmission()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            SteeringRoute,
            ConversationRouteValues(),
            """
            {
              "turnId": "turn-alpha",
              "steeringId": "steering-alpha",
              "clientRequestId": "client-steering-alpha",
              "instruction": "Use the safer read-only approach.",
              "expectedStateVersion": 9
            }
            """,
            admission,
            dispatch,
            accessToken: null);

        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        admission.Targets.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Steering_ShouldRejectInvalidInputPartBeforeAdmission()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            SteeringRoute,
            ConversationRouteValues(),
            """
            {
              "turnId": "turn-alpha",
              "steeringId": "steering-alpha",
              "clientRequestId": "client-steering-alpha",
              "instruction": "Continue.",
              "inputParts": [{"type":"unsupported","text":"must reject"}],
              "expectedStateVersion": 9
            }
            """,
            admission,
            dispatch);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        admission.Targets.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_ShouldMapPathIdentityAndTransientCapability()
    {
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            RetryRoute,
            StepRouteValues(),
            ValidRetryBody(generation: 2),
            new RecordingAdmissionPort(),
            dispatch,
            accessToken: "retry-runtime-token-alpha",
            authenticatedScopeId: "scope-alpha",
            authenticatedOwnerSubject: "owner-alpha");

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Body.Should().NotContain("retry-runtime-token-alpha");
        var command = dispatch.Dispatches.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatRetryStepCommand>();
        command.ConversationActorId.Should().Be("conversation-alpha");
        command.TurnId.Should().Be("turn-alpha");
        command.TaskId.Should().Be("task-alpha");
        command.StepId.Should().Be("step-alpha");
        command.RetryRequestId.Should().Be("retry-alpha");
        command.OwnerSubject.Should().Be("owner-alpha");
        command.ExpectedOperationGeneration.Should().Be(2);
        command.ExpectedStateVersion.Should().Be(11);
        command.LlmControl.NyxIdAccessToken.Should().Be("retry-runtime-token-alpha");
        command.ToolContext.Credentials.NyxIdAccessToken.Should().Be(
            "retry-runtime-token-alpha");
        command.ToolContext.Channel.SenderId.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_ToolContext_ShouldExecuteThroughRealAdmissionBoundaryWithActorOwner()
    {
        var dispatch = new RecordingDispatchPort();
        var response = await ExecuteAsync(
            RetryRoute,
            StepRouteValues(),
            ValidRetryBody(generation: 2),
            new RecordingAdmissionPort(),
            dispatch,
            accessToken: "retry-runtime-token-alpha",
            authenticatedScopeId: "scope-alpha",
            authenticatedOwnerSubject: "owner-alpha");

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var command = dispatch.Dispatches.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatRetryStepCommand>();
        var endpointContext = AgentToolExecutionContextMapper.FromPayload(command.ToolContext);
        endpointContext.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        endpointContext.ExecutionOwner.OwnerId.Should().Be("conversation-alpha");

        var tool = new CountingReadOnlyTool();
        var executor = new AdmittedAgentToolExecutor(
            AlwaysStartingAgentToolAdmissionLedger.Instance,
            new AppendedAuditTrail(),
            new StableIdentityHasher());
        var executionContext = endpointContext with
        {
            Request = endpointContext.Request with { CallId = "call-retry-alpha" },
        };

        var outcome = await executor.ExecuteAsync(new AgentToolExecutionRequest(
            tool,
            "{}",
            executionContext,
            AgentToolApprovalContinuationMode.ActorOwned,
            null));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.FailureCode.Should().NotBe("invalid_tool_execution_identity");
        tool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task Retry_ShouldRequirePositiveGenerationBeforeAdmission()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            RetryRoute,
            StepRouteValues(),
            ValidRetryBody(generation: 0),
            admission,
            dispatch);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        admission.Targets.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_ShouldRequireTransientCapabilityBeforeAdmission()
    {
        var admission = new RecordingAdmissionPort();
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            RetryRoute,
            StepRouteValues(),
            ValidRetryBody(generation: 2),
            admission,
            dispatch,
            accessToken: null);

        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        admission.Targets.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Skip_ShouldMapPathIdentityWithoutRuntimeCapability()
    {
        var dispatch = new RecordingDispatchPort();

        var response = await ExecuteAsync(
            SkipRoute,
            StepRouteValues(),
            """
            {
              "taskId": "task-alpha",
              "skipRequestId": "skip-alpha",
              "clientRequestId": "client-skip-alpha",
              "expectedOperationGeneration": 2,
              "expectedStateVersion": 12
            }
            """,
            new RecordingAdmissionPort(),
            dispatch);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var command = dispatch.Dispatches.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatSkipStepCommand>();
        command.TurnId.Should().Be("turn-alpha");
        command.TaskId.Should().Be("task-alpha");
        command.StepId.Should().Be("step-alpha");
        command.SkipRequestId.Should().Be("skip-alpha");
        command.ExpectedOperationGeneration.Should().Be(2);
        command.ExpectedStateVersion.Should().Be(12);
    }

    private static Dictionary<string, object?> ConversationRouteValues() => new()
    {
        ["scopeId"] = "scope-alpha",
        ["actorId"] = "conversation-alpha",
    };

    private static Dictionary<string, object?> StepRouteValues() => new()
    {
        ["scopeId"] = "scope-alpha",
        ["actorId"] = "conversation-alpha",
        ["turnId"] = "turn-alpha",
        ["stepId"] = "step-alpha",
    };

    private static string ValidRetryBody(long generation) => $$"""
        {
          "taskId": "task-alpha",
          "retryRequestId": "retry-alpha",
          "clientRequestId": "client-retry-alpha",
          "expectedOperationGeneration": {{generation}},
          "expectedStateVersion": 11
        }
        """;

    private static string ValidCanaryEffectFaultArmBody() => $$"""
        {
          "armId": "arm-alpha",
          "clientRequestId": "client-arm-alpha",
          "sourceTurnId": "turn-alpha",
          "sourceTaskId": "task-alpha",
          "sourceStepId": "step-alpha",
          "sourceOperationId": "operation-alpha",
          "sourceOperationGeneration": 1,
          "serviceInstanceId": "connected-service-alpha",
          "expiresAt": "{{DateTimeOffset.UtcNow.AddMinutes(10):O}}",
          "expectedStateVersion": 23
        }
        """;

    private static async Task<(int StatusCode, string Body, string? Location)> ExecuteAsync(
        string routePattern,
        IReadOnlyDictionary<string, object?> routeValues,
        string jsonBody,
        IScopeResourceAdmissionPort admissionPort,
        IActorDispatchPort dispatchPort,
        string? accessToken = "control-runtime-token-alpha",
        string? authenticatedScopeId = null,
        string? authenticatedOwnerSubject = null,
        NyxIdChatCanaryEffectFaultOptions? canaryOptions = null)
    {
        await using var services = CreateServices(
            admissionPort,
            dispatchPort,
            authenticationEnabled:
                authenticatedScopeId is not null || authenticatedOwnerSubject is not null,
            canaryOptions);
        var context = new DefaultHttpContext { RequestServices = services };
        if (authenticatedScopeId is not null || authenticatedOwnerSubject is not null)
        {
            var claims = new List<Claim>();
            if (authenticatedScopeId is not null)
                claims.Add(new Claim("scope_id", authenticatedScopeId));
            if (authenticatedOwnerSubject is not null)
                claims.Add(new Claim("uid", authenticatedOwnerSubject));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                authenticationType: "test"));
        }

        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        if (accessToken is not null)
            context.Request.Headers["X-NyxID-Delegation-Token"] = accessToken;
        context.Request.RouteValues = new RouteValueDictionary(routeValues);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await BuildRouteEndpoint(routePattern).RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (
            context.Response.StatusCode,
            await new StreamReader(context.Response.Body).ReadToEndAsync(),
            context.Response.Headers.Location.ToString());
    }

    private static ServiceProvider CreateServices(
        IScopeResourceAdmissionPort admissionPort,
        IActorDispatchPort dispatchPort,
        bool authenticationEnabled,
        NyxIdChatCanaryEffectFaultOptions? canaryOptions) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = authenticationEnabled ? "true" : "false",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment
            {
                EnvironmentName = authenticationEnabled
                    ? Environments.Production
                    : Environments.Development,
            })
            .AddSingleton(admissionPort)
            .AddSingleton(dispatchPort)
            .AddSingleton(canaryOptions ?? new NyxIdChatCanaryEffectFaultOptions())
            .AddSingleton<INyxIdChatCanaryEffectFaultAuthorizationPolicy,
                NyxIdChatCanaryEffectFaultAuthorizationPolicy>()
            .AddSingleton<INyxIdChatControlCommandPort, NyxIdChatControlCommandPort>()
            .BuildServiceProvider();

    private static RouteEndpoint BuildRouteEndpoint(string routePattern)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();
        app.MapNyxIdChatEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                routePattern,
                StringComparison.Ordinal));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root could not be resolved.");
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class RecordingAdmissionPort : IScopeResourceAdmissionPort
    {
        public ScopeResourceAdmissionResult Result { get; init; } =
            ScopeResourceAdmissionResult.Allowed();
        public List<ScopeResourceTarget> Targets { get; } = [];

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Targets.Add(target);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class CountingReadOnlyTool : IAgentTool
    {
        public int ExecuteCount { get; private set; }
        public string Name => "retry-context-fixture";
        public string Description => "Exercises the admitted execution identity boundary.";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCount++;
            return Task.FromResult("{}");
        }
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.AI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
