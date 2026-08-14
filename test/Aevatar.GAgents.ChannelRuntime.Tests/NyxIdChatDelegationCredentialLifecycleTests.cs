using System.Net;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdChatDelegationCredentialLifecycleTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-07T10:00:00Z");

    [Fact]
    public async Task LongRunningTurn_WhenDelegationEntersRefreshWindow_ShouldReplaceEveryCarrierBeforeToolDispatch()
    {
        var time = new FakeTimeProvider(Now);
        var originalToken = CreateJwt(Now.AddSeconds(300));
        var refreshedToken = CreateJwt(Now.AddSeconds(700));
        var handler = new DelegationRefreshHandler(HttpStatusCode.OK, () =>
            $$"""{"access_token":"{{refreshedToken}}","token_type":"Bearer","expires_in":300,"scope":"openid service:read"}""");
        var lifecycle = CreateLifecycle(time, handler);
        var generation = new ToolCallingGenerationExecutor
        {
            BeforeToolDispatch = () =>
                handler.CallCount.Should().Be(1, "refresh must finish before tool dispatch"),
        };
        var executor = new NyxIdChatTurnOperationExecutor(
            generation,
            new UnavailableNyxIdActionPostconditionPort(),
            turnCatalogMaterializer: null,
            lifecycle);
        var session = new NyxIdChatTransientExecutionSession();

        var llm = await executor.ExecuteAsync(
            BuildInitialLlmCommand(originalToken, AgentToolNyxIdCredentialKindPayload.ProxyDelegation),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        llm.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        handler.CallCount.Should().Be(0);
        time.Advance(TimeSpan.FromSeconds(200));
        var call = llm.Result.Llm.ToolCalls.Should().ContainSingle().Which;

        var tool = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildKey("step-tool", "operation-tool"),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson,
                    MayChangeExternalState = true,
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        tool.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Tool);
        handler.CallCount.Should().Be(1);
        handler.LastAuthorization.Should().Be($"Bearer {originalToken}");
        handler.LastBody.Should().BeNull();
        generation.ExecutedToolToken.Should().Be(refreshedToken);
        generation.ToolWorkItem.Should().NotBeNull();
        AssertRefreshedCarriers(generation.ToolWorkItem!.Request, generation.ToolWorkItem.StepState, refreshedToken);
        AssertRefreshedCarriers(session.Request!, session.StepState!, refreshedToken);
    }

    [Fact]
    public async Task DelegationRefreshFailure_ShouldReturnTypedNotAppliedFailureWithoutDispatchOrStaleRetry()
    {
        var time = new FakeTimeProvider(Now);
        var originalToken = CreateJwt(Now.AddSeconds(60));
        var handler = new DelegationRefreshHandler(
            HttpStatusCode.Forbidden,
            static () => """{"error":"forbidden","error_code":1002}""");
        var generation = new ToolCallingGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generation,
            new UnavailableNyxIdActionPostconditionPort(),
            turnCatalogMaterializer: null,
            CreateLifecycle(time, handler));

        var result = await executor.ExecuteAsync(
            BuildInitialLlmCommand(originalToken, AgentToolNyxIdCredentialKindPayload.ProxyDelegation),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.DelegationRefreshFailedCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        generation.LlmDispatchCount.Should().Be(0);
        generation.ToolDispatchCount.Should().Be(0);
        handler.CallCount.Should().Be(1, "the stale delegation must never be retried");
    }

    [Fact]
    public async Task SourceReadableBearer_ShouldRemainBearerOnlyAndNeverCallDelegationRefresh()
    {
        var time = new FakeTimeProvider(Now);
        var bearer = CreateJwt(Now.AddSeconds(1));
        var handler = new DelegationRefreshHandler(
            HttpStatusCode.OK,
            static () => throw new InvalidOperationException("bearer must not be refreshed"));
        var generation = new ToolCallingGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generation,
            new UnavailableNyxIdActionPostconditionPort(),
            turnCatalogMaterializer: null,
            CreateLifecycle(time, handler));

        var result = await executor.ExecuteAsync(
            BuildInitialLlmCommand(bearer, AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        generation.LlmDispatchCount.Should().Be(1);
        handler.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOrConflictingDelegationCarrier_ShouldFailBeforeRefreshOrGeneration(
        bool useMalformedToken)
    {
        var time = new FakeTimeProvider(Now);
        var handler = new DelegationRefreshHandler(
            HttpStatusCode.OK,
            static () => throw new InvalidOperationException("invalid carrier must not reach NyxID"));
        var generation = new ToolCallingGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generation,
            new UnavailableNyxIdActionPostconditionPort(),
            turnCatalogMaterializer: null,
            CreateLifecycle(time, handler));
        var command = BuildInitialLlmCommand(
            useMalformedToken ? "not-a-jwt" : CreateJwt(Now.AddSeconds(300)),
            AgentToolNyxIdCredentialKindPayload.ProxyDelegation);
        if (!useMalformedToken)
            command.Llm.Request.LlmControl.NyxIdAccessToken = CreateJwt(Now.AddSeconds(301));

        var result = await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.DelegationRefreshFailedCode);
        generation.LlmDispatchCount.Should().Be(0);
        handler.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"delegated\"")]
    public async Task DelegationTokenWithNonObjectPayload_ShouldFailClosed(string payloadJson)
    {
        var lifecycle = new NyxIdChatDelegationCredentialLifecyclePort(
            new FakeTimeProvider(Now));

        var result = await lifecycle.ResolveAsync(
            CreateJwtPayload(payloadJson),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("invalid_delegation_token");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshSuccess_WhenReturnedDelegationIsMalformedOrStillExpiring_ShouldFailBeforeDispatch(
        bool malformed)
    {
        var time = new FakeTimeProvider(Now);
        var refreshedToken = malformed
            ? "not-a-jwt"
            : CreateJwt(Now.AddSeconds(120));
        var handler = new DelegationRefreshHandler(HttpStatusCode.OK, () =>
            $$"""{"access_token":"{{refreshedToken}}","token_type":"Bearer","expires_in":300,"scope":"openid service:read"}""");
        var generation = new ToolCallingGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generation,
            new UnavailableNyxIdActionPostconditionPort(),
            turnCatalogMaterializer: null,
            CreateLifecycle(time, handler));

        var result = await executor.ExecuteAsync(
            BuildInitialLlmCommand(
                CreateJwt(Now.AddSeconds(60)),
                AgentToolNyxIdCredentialKindPayload.ProxyDelegation),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.DelegationRefreshFailedCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        generation.LlmDispatchCount.Should().Be(0);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task DelegationRefresh_WhenProviderHangs_ShouldUseIndependentRefreshTimeout()
    {
        var time = new FakeTimeProvider(Now);
        var handler = new BlockingDelegationRefreshHandler();
        var lifecycle = new NyxIdChatDelegationCredentialLifecyclePort(
            time,
            new TestClientFactory(() => new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
                new HttpClient(handler),
                NullLogger<NyxIdApiClient>.Instance)));
        var generation = new ToolCallingGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(
            generation,
            new UnavailableNyxIdActionPostconditionPort(),
            turnCatalogMaterializer: null,
            lifecycle);

        var execution = executor.ExecuteAsync(
            BuildInitialLlmCommand(
                CreateJwt(Now.AddSeconds(60)),
                AgentToolNyxIdCredentialKindPayload.ProxyDelegation),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await handler.Started;

        time.Advance(NyxIdChatDelegationCredentialLifecyclePort.RefreshTimeout);
        var result = await execution;

        result.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.DelegationRefreshFailedCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        generation.LlmDispatchCount.Should().Be(0);
        handler.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(
        AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
        AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        "user-bearer")]
    [InlineData(
        AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        AgentToolNyxIdCredentialKindPayload.Unspecified,
        "replacement")]
    [InlineData(
        AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        " ")]
    [InlineData(
        AgentToolNyxIdCredentialKindPayload.Unspecified,
        AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        "replacement")]
    public async Task InputContinuation_ShouldRejectReplacementWithoutExactKnownCredentialKind(
        AgentToolNyxIdCredentialKindPayload currentKind,
        AgentToolNyxIdCredentialKindPayload replacementKind,
        string replacementToken)
    {
        var token = CreateJwt(Now.AddSeconds(300));
        var credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = token,
            NyxIdCredentialKind = currentKind,
        };
        var request = new NeedsLlmReplyEvent
        {
            RunId = "task-1",
            CorrelationId = "operation-llm",
            TargetActorId = "conversation-1",
            ToolContext = new AgentToolExecutionContextPayload
            {
                Credentials = credentials.Clone(),
            },
            LlmControl = new LLMControlContextPayload { NyxIdAccessToken = token },
        };
        var stepState = new AgentRunReplyStepState
        {
            RunId = "task-1",
            CorrelationId = "operation-llm",
            TargetActorId = "conversation-1",
            Attempt = 1,
            NextStepIndex = 2,
            ToolContext = request.ToolContext.Clone(),
            LlmControl = request.LlmControl.Clone(),
        };
        stepState.PendingToolCalls.Add(new AgentRunToolCall
        {
            Id = "call-input",
            Name = "ask_user",
            ArgumentsJson = "{}",
        });
        var session = new NyxIdChatTransientExecutionSession
        {
            Request = request,
            StepState = stepState,
        };
        var generation = new ToolCallingGenerationExecutor();
        var executor = new NyxIdChatTurnOperationExecutor(generation);

        var result = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = BuildKey("step-input", "operation-input"),
                InputContinuation = new NyxIdChatInputContinuationInput
                {
                    RequestId = "input-1",
                    ToolCallId = "call-input",
                    Answer = new NyxIdChatInputAnswer { FreeText = "continue" },
                    ToolContext = new AgentToolExecutionContextPayload
                    {
                        Credentials = new AgentToolCredentialsPayload
                        {
                            NyxIdAccessToken = replacementToken,
                            NyxIdCredentialKind = replacementKind,
                        },
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        generation.LlmDispatchCount.Should().Be(0);
        session.Request.ToolContext.Credentials.NyxIdAccessToken.Should().Be(token);
        session.Request.ToolContext.Credentials.NyxIdCredentialKind.Should().Be(
            currentKind);
    }

    private static NyxIdChatDelegationCredentialLifecyclePort CreateLifecycle(
        TimeProvider time,
        DelegationRefreshHandler handler) =>
        new(
            time,
            new TestClientFactory(() => new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
                new HttpClient(handler),
                NullLogger<NyxIdApiClient>.Instance)));

    private static NyxIdChatOperationDispatchCommand BuildInitialLlmCommand(
        string token,
        AgentToolNyxIdCredentialKindPayload credentialKind) =>
        new()
        {
            Key = BuildKey("step-llm", "operation-llm"),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "run the connected-service operation",
                    SessionId = "turn-1",
                    ToolContext = new AgentToolExecutionContextPayload
                    {
                        Credentials = new AgentToolCredentialsPayload
                        {
                            NyxIdAccessToken = token,
                            NyxIdCredentialKind = credentialKind,
                        },
                    },
                    LlmControl = new LLMControlContextPayload
                    {
                        NyxIdAccessToken = token,
                    },
                },
            },
        };

    private static NyxIdChatOperationKey BuildKey(string stepId, string operationId) =>
        new()
        {
            ConversationActorId = "conversation-1",
            TurnId = "turn-1",
            TaskId = "task-1",
            StepId = stepId,
            OperationId = operationId,
            OperationGeneration = 1,
        };

    private static void AssertRefreshedCarriers(
        NeedsLlmReplyEvent request,
        AgentRunReplyStepState stepState,
        string expectedToken)
    {
        request.ToolContext.Credentials.NyxIdAccessToken.Should().Be(expectedToken);
        request.ToolContext.Credentials.NyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKindPayload.ProxyDelegation);
        request.ToolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeEmpty();
        request.LlmControl.NyxIdAccessToken.Should().Be(expectedToken);
        stepState.ToolContext.Credentials.NyxIdAccessToken.Should().Be(expectedToken);
        stepState.ToolContext.Credentials.NyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKindPayload.ProxyDelegation);
        stepState.ToolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeEmpty();
        stepState.LlmControl.NyxIdAccessToken.Should().Be(expectedToken);
    }

    private static string CreateJwt(DateTimeOffset expiresAt) =>
        CreateJwtPayload(
            $"{{\"delegated\":true,\"act\":{{\"sub\":\"aevatar\"}},\"exp\":{expiresAt.ToUnixTimeSeconds()}}}");

    private static string CreateJwtPayload(string payloadJson)
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{Encode("{\"alg\":\"none\"}")}." +
               $"{Encode(payloadJson)}." +
               "signature";
    }

    private sealed class TestClientFactory(Func<NyxIdApiClient> createClient)
        : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => createClient();
    }

    private sealed class DelegationRefreshHandler(
        HttpStatusCode statusCode,
        Func<string> responseBody) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class BlockingDelegationRefreshHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            _started.TrySetResult();
            var completion = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state =>
                {
                    var (source, token) =
                        ((TaskCompletionSource<HttpResponseMessage>, CancellationToken))state!;
                    source.TrySetCanceled(token);
                },
                (completion, cancellationToken));
            return await completion.Task;
        }
    }

    private sealed class ToolCallingGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public int LlmDispatchCount { get; private set; }
        public int ToolDispatchCount { get; private set; }
        public string? ExecutedToolToken { get; private set; }
        public AgentRunReplyStepExecutionRequest? ToolWorkItem { get; private set; }
        public Action? BeforeToolDispatch { get; init; }

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 2,
                ToolContext = request.Request.ToolContext?.Clone(),
                LlmControl = request.Request.LlmControl?.Clone(),
            });

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            LlmDispatchCount++;
            var call = new AgentRunToolCall
            {
                Id = "call-1",
                Name = "connected_service_read",
                ArgumentsJson = "{}",
            };
            var result = new AgentRunLlmStepResult { FinishReason = "tool_calls" };
            result.ToolCalls.Add(call.Clone());
            var continuation = new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                LlmStepResult = result,
            };
            var context = AgentToolExecutionContextMapper.FromPayload(request.StepState.ToolContext);
            var authorized = new AgentRunAuthorizedToolStep(
                request.RunId,
                request.Request.CorrelationId,
                request.Attempt,
                continuation.StepIndex,
                [call],
                context,
                (executionContext, _) =>
                {
                    BeforeToolDispatch?.Invoke();
                    ToolDispatchCount++;
                    ExecutedToolToken = executionContext.Credentials.NyxIdAccessToken;
                    var toolResult = new AgentRunToolStepResult { AdvanceRound = true };
                    toolResult.ResultMessages.Add(new AgentRunChatMessage
                    {
                        Role = "tool",
                        ToolCallId = call.Id,
                        Content = "{}",
                    });
                    toolResult.ToolReceipts.Add(new AgentToolReceipt
                    {
                        CallId = call.Id,
                        ToolName = call.Name,
                        Status = AgentToolReceiptStatus.Success,
                        Effect = AgentToolReceiptEffect.Mutating,
                    });
                    return Task.FromResult(toolResult);
                });
            return Task.FromResult(new AgentRunLlmStepExecution(
                continuation,
                authorized,
                [
                    new AgentRunAuthorizedToolCallSafety(
                        call.Id,
                        call.Name,
                        call.ArgumentsJson,
                        new AgentToolCallSafety(
                            RequiresApproval: false,
                            IsReadOnly: false,
                            IsDestructive: false),
                        SideEffectKind: "connected_service.update"),
                ]));
        }

        public async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            ToolWorkItem = request;
            var result = await authorizedToolStep!.ExecuteAsync(ct);
            return new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                ToolStepResult = result,
            };
        }
    }
}
