using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.LLMProviders.MEAI;
using FluentAssertions;
using Microsoft.Extensions.AI;

using AevatarChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;

namespace Aevatar.AI.Tests;

public sealed class AgentToolAIFunctionSchemaSanitizationTests
{
    /// <summary>
    /// Regression for the production crash "The JSON value could not be converted to System.Boolean.
    /// Path: $.additionalProperties": a caller-supplied tool (e.g. codex's) whose parameter schema
    /// expresses a map type (<c>additionalProperties: { …schema… }</c>) used to throw inside MEAI's
    /// <c>ToOpenAIFunctionParameters</c> and fail the whole turn. The schema is coerced to a boolean
    /// before reaching MEAI. Drives the real pipeline: IAgentTool → AgentToolAIFunction →
    /// MEAI OpenAIChatClient → HTTP request body.
    /// </summary>
    [Fact]
    public async Task ObjectAdditionalProperties_IsCoercedToBoolean_SoMeaiRequestDoesNotThrow()
    {
        string? capturedRequestBody = null;
        var handler = new CapturingHttpHandler(async request =>
        {
            capturedRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            var responseContent = "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"test\"," +
                                  "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent, System.Text.Encoding.UTF8, "text/event-stream"),
            };
        });

        var clientOptions = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://test.example.com/v1/"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var openAiClient = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential("test-key"), clientOptions);
        var chatClient = openAiClient.GetChatClient("gpt-5.5").AsIChatClient();
        var executionPort = new RecordingExecutionPort();
        var provider = new MEAILLMProvider("test", chatClient, toolExecutionPort: executionPort);

        // Root-level map (additionalProperties is a schema object) + a nested map — both must be coerced.
        const string mapSchema =
            """{"type":"object","properties":{"env":{"type":"object","additionalProperties":{"type":"string"}}},"additionalProperties":{"type":"string"}}""";
        var tools = new IAgentTool[] { new SchemaStubTool("apply_patch", mapSchema) };

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync(new LLMRequest
            {
                Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
                Tools = tools,
            }))
            {
            }
        };

        // Before the fix this threw the System.Boolean JsonException inside ToOpenAIFunctionParameters.
        await act.Should().NotThrowAsync();

        // A captured body proves MEAI built the request (no crash); no additionalProperties stays an object.
        capturedRequestBody.Should().NotBeNullOrWhiteSpace();
        using var doc = System.Text.Json.JsonDocument.Parse(capturedRequestBody!);
        var parameters = doc.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");
        AssertNoObjectAdditionalProperties(parameters);
        executionPort.Calls.Should().Be(0);
    }

    [Fact]
    public async Task BooleanAdditionalProperties_IsPreserved()
    {
        string? capturedRequestBody = null;
        var handler = new CapturingHttpHandler(async request =>
        {
            capturedRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            var responseContent = "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"test\"," +
                                  "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent, System.Text.Encoding.UTF8, "text/event-stream"),
            };
        });

        var clientOptions = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://test.example.com/v1/"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var openAiClient = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential("test-key"), clientOptions);
        var chatClient = openAiClient.GetChatClient("gpt-5.5").AsIChatClient();
        var executionPort = new RecordingExecutionPort();
        var provider = new MEAILLMProvider("test", chatClient, toolExecutionPort: executionPort);

        const string strictSchema =
            """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""";
        var tools = new IAgentTool[] { new SchemaStubTool("get_weather", strictSchema) };

        await foreach (var _ in provider.ChatStreamAsync(new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
            Tools = tools,
        }))
        {
        }

        capturedRequestBody.Should().NotBeNullOrWhiteSpace();
        using var doc = System.Text.Json.JsonDocument.Parse(capturedRequestBody!);
        var parameters = doc.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");
        parameters.GetProperty("additionalProperties").ValueKind.Should().Be(System.Text.Json.JsonValueKind.False);
        executionPort.Calls.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_WithoutStableAmbientIdentities_ShouldNotEnterExecutionPort()
    {
        var executionPort = new RecordingExecutionPort();
        var functionType = typeof(MEAILLMProvider).Assembly.GetType(
            "Aevatar.AI.LLMProviders.MEAI.AgentToolAIFunction",
            throwOnError: true)!;
        var function = (AIFunction)Activator.CreateInstance(
            functionType,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [new SchemaStubTool("test_tool", "{}"), executionPort],
            culture: null)!;
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty);

        var act = async () => await function.InvokeAsync(new AIFunctionArguments());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stable request and function-call identities*");
        executionPort.Calls.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_WithoutExecutionOwner_ShouldNotEnterExecutionPort()
    {
        var executionPort = new RecordingExecutionPort();
        var functionType = typeof(MEAILLMProvider).Assembly.GetType(
            "Aevatar.AI.LLMProviders.MEAI.AgentToolAIFunction",
            throwOnError: true)!;
        var function = (AIFunction)Activator.CreateInstance(
            functionType,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [new SchemaStubTool("test_tool", "{}"), executionPort],
            culture: null)!;
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-alpha", "call-alpha"),
        });

        var act = async () => await function.InvokeAsync(new AIFunctionArguments());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stable execution owner*");
        executionPort.Calls.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_WithStableContext_ShouldDelegateExactRequestAndReturnPortResult()
    {
        const string safeResult = "{\"ok\":true}";
        var tool = new SchemaStubTool("test_tool", "{}");
        var executionPort = new RecordingExecutionPort(CreateOutcome(
            AgentToolExecutionOutcomeKind.Executed,
            AgentToolReceiptStatus.Success,
            safeResult));
        var function = CreateFunction(tool, executionPort);
        var context = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-alpha", "call-alpha"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-alpha"),
        };
        using var scope = AgentToolContextScope.Push(context);
        using var cancellation = new CancellationTokenSource();
        var arguments = new AIFunctionArguments
        {
            ["city"] = "Paris",
        };

        var result = await function.InvokeAsync(arguments, cancellation.Token);

        result.Should().Be(safeResult);
        var request = executionPort.Requests.Should().ContainSingle().Subject;
        request.Tool.Should().BeSameAs(tool);
        request.ArgumentsJson.Should().Be("{\"city\":\"Paris\"}");
        request.ExecutionContext.Should().BeSameAs(context);
        request.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        request.ExecutionOwner.OwnerId.Should().Be("actor-alpha");
        request.ExecutionContext.Request.RequestId.Should().Be("request-alpha");
        request.ExecutionContext.Request.CallId.Should().Be("call-alpha");
        request.ApprovalContinuationMode.Should().Be(AgentToolApprovalContinuationMode.None);
        request.ApprovalGrant.Should().BeNull();
        executionPort.CancellationTokens.Should().ContainSingle().Which.Should().Be(cancellation.Token);
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_WithFunctionInvocationContext_ShouldUseProviderFunctionCallId()
    {
        const string safeResult = "{\"ok\":true}";
        var tool = new SchemaStubTool("test_tool", "{}");
        var executionPort = new RecordingExecutionPort(CreateOutcome(
            AgentToolExecutionOutcomeKind.Executed,
            AgentToolReceiptStatus.Success,
            safeResult));
        var function = CreateFunction(tool, executionPort);
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-alpha", "ambient-call"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-alpha"),
        });
        var previousContext = FunctionInvokingChatClient.CurrentContext;
        try
        {
            SetFunctionInvocationContext(new FunctionInvocationContext
            {
                CallContent = new FunctionCallContent("provider-call", "test_tool", new Dictionary<string, object>()),
            });

            var result = await function.InvokeAsync(new AIFunctionArguments());

            result.Should().Be(safeResult);
            var request = executionPort.Requests.Should().ContainSingle().Subject;
            request.ExecutionContext.Request.RequestId.Should().Be("request-alpha");
            request.ExecutionContext.Request.CallId.Should().Be("provider-call");
            request.ExecutionOwner.OwnerId.Should().Be("actor-alpha");
        }
        finally
        {
            SetFunctionInvocationContext(previousContext);
        }
    }

    [Fact]
    public async Task InvokeAsync_WithProviderInvocationContextWithoutCallId_ShouldSynthesizeDistinctCallIds()
    {
        const string safeResult = "{\"ok\":true}";
        var tool = new SchemaStubTool("test_tool", "{}");
        var executionPort = new RecordingExecutionPort(CreateOutcome(
            AgentToolExecutionOutcomeKind.Executed,
            AgentToolReceiptStatus.Success,
            safeResult));
        var function = CreateFunction(tool, executionPort);
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-alpha", "ambient-round-call"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-alpha"),
        });
        var previousContext = FunctionInvokingChatClient.CurrentContext;
        try
        {
            SetFunctionInvocationContext(new FunctionInvocationContext
            {
                CallContent = new FunctionCallContent("", "test_tool", new Dictionary<string, object>()),
                Iteration = 2,
                FunctionCallIndex = 0,
                FunctionCount = 2,
            });
            await function.InvokeAsync(new AIFunctionArguments());

            SetFunctionInvocationContext(new FunctionInvocationContext
            {
                CallContent = new FunctionCallContent("", "test_tool", new Dictionary<string, object>()),
                Iteration = 2,
                FunctionCallIndex = 1,
                FunctionCount = 2,
            });
            await function.InvokeAsync(new AIFunctionArguments());

            executionPort.Requests.Select(request => request.ExecutionContext.Request.CallId)
                .Should().Equal(
                    "meai-request-alpha-iteration-2-function-0",
                    "meai-request-alpha-iteration-2-function-1");
            executionPort.Requests.Select(request => request.ExecutionContext.Request.CallId)
                .Should().NotContain("ambient-round-call");
        }
        finally
        {
            SetFunctionInvocationContext(previousContext);
        }
    }

    [Theory]
    [InlineData(AgentToolExecutionOutcomeKind.Denied, AgentToolReceiptStatus.Denied)]
    [InlineData(AgentToolExecutionOutcomeKind.Failed, AgentToolReceiptStatus.Error)]
    public async Task InvokeAsync_WhenPortRejectsExecution_ShouldReturnOnlyPortSafeResult(
        AgentToolExecutionOutcomeKind kind,
        AgentToolReceiptStatus receiptStatus)
    {
        const string safeResult = "{\"error\":\"safe_failure\"}";
        var tool = new SchemaStubTool("test_tool", "{}");
        var executionPort = new RecordingExecutionPort(CreateOutcome(kind, receiptStatus, safeResult));
        var function = CreateFunction(tool, executionPort);
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-alpha", "call-alpha"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-alpha"),
        });

        var result = await function.InvokeAsync(new AIFunctionArguments());

        result.Should().Be(safeResult);
        executionPort.Requests.Should().ContainSingle();
        tool.ExecutionCalls.Should().Be(0);
    }

    private static void SetFunctionInvocationContext(FunctionInvocationContext? context)
    {
        var setter = typeof(FunctionInvokingChatClient)
            .GetProperty(nameof(FunctionInvokingChatClient.CurrentContext))!
            .GetSetMethod(nonPublic: true);
        setter.Should().NotBeNull();
        setter!.Invoke(null, [context]);
    }

    private static AIFunction CreateFunction(
        IAgentTool tool,
        IAgentToolExecutionPort executionPort)
    {
        var functionType = typeof(MEAILLMProvider).Assembly.GetType(
            "Aevatar.AI.LLMProviders.MEAI.AgentToolAIFunction",
            throwOnError: true)!;
        return (AIFunction)Activator.CreateInstance(
            functionType,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [tool, executionPort],
            culture: null)!;
    }

    private static AgentToolExecutionOutcome CreateOutcome(
        AgentToolExecutionOutcomeKind kind,
        AgentToolReceiptStatus receiptStatus,
        string resultJson) =>
        new(
            kind,
            resultJson,
            new AgentToolReceipt
            {
                CallId = "call-alpha",
                ToolName = "test_tool",
                Status = receiptStatus,
                ResultJson = resultJson,
            },
            IsMutation: false,
            FailureCode: kind == AgentToolExecutionOutcomeKind.Executed ? string.Empty : "safe_failure",
            SafeMessage: kind == AgentToolExecutionOutcomeKind.Executed ? string.Empty : "The tool was not executed.",
            kind == AgentToolExecutionOutcomeKind.Denied
                ? AgentToolExecutionFailureStage.Approval
                : kind == AgentToolExecutionOutcomeKind.Failed
                    ? AgentToolExecutionFailureStage.TerminalExecution
                    : AgentToolExecutionFailureStage.None,
            TerminalInvoked: kind == AgentToolExecutionOutcomeKind.Executed,
            Retryable: false,
            AuditCompleted: true);

    private static void AssertNoObjectAdditionalProperties(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("additionalProperties"))
                        property.Value.ValueKind.Should().BeOneOf(
                            System.Text.Json.JsonValueKind.True,
                            System.Text.Json.JsonValueKind.False);
                    AssertNoObjectAdditionalProperties(property.Value);
                }

                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AssertNoObjectAdditionalProperties(item);
                break;
        }
    }

    private sealed class SchemaStubTool(string name, string parametersSchema) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => Name;
        public string ParametersSchema { get; } = parametersSchema;
        public int ExecutionCalls { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionCalls++;
            return Task.FromResult("{}");
        }
    }

    private sealed class RecordingExecutionPort(AgentToolExecutionOutcome? outcome = null) : IAgentToolExecutionPort
    {
        public int Calls => Requests.Count;
        public List<AgentToolExecutionRequest> Requests { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            CancellationTokens.Add(ct);
            return outcome is null
                ? Task.FromException<AgentToolExecutionOutcome>(
                    new InvalidOperationException("The execution port should not be called by this test."))
                : Task.FromResult(outcome);
        }
    }

    private sealed class CapturingHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> onSend)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            onSend(request);
    }
}
