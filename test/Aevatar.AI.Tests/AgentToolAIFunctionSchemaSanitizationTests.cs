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

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{}");
        }
    }

    private sealed class RecordingExecutionPort : IAgentToolExecutionPort
    {
        public int Calls { get; private set; }

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("The execution port should not be called by this test.");
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
