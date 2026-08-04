using System.Diagnostics;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Observability;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Observability;

public class GenAIObservabilityMiddlewareTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];
    private readonly GenAIObservabilityMiddleware _middleware = new();
    private readonly bool _originalSensitiveFlag;

    public GenAIObservabilityMiddlewareTests()
    {
        _originalSensitiveFlag = GenAIActivitySource.EnableSensitiveData;
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Aevatar.GenAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        GenAIActivitySource.EnableSensitiveData = _originalSensitiveFlag;
        _listener.Dispose();
    }

    [Fact]
    public async Task AgentRun_EmitsInvokeAgentSpan()
    {
        _activities.Clear();

        var ctx = new AgentRunContext
        {
            UserMessage = "hello", AgentId = "agent-1", AgentName = "TestAgent",
        };

        await _middleware.InvokeAsync(ctx, () =>
        {
            ctx.Result = "world";
            return Task.CompletedTask;
        });

        var matching = _activities.Where(a =>
            a.GetTagItem("gen_ai.operation.name")?.ToString() == "invoke_agent").ToList();
        matching.Should().NotBeEmpty();
        matching.Last().GetTagItem("gen_ai.response.status").Should().Be("ok");
    }

    [Fact]
    public async Task AgentRun_OnError_SetsErrorTag()
    {
        _activities.Clear();

        var ctx = new AgentRunContext { UserMessage = "fail" };

        var act = async () => await _middleware.InvokeAsync(ctx, () =>
            throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();

        _activities.Should().NotBeEmpty();
        _activities.Last().GetTagItem("gen_ai.response.status").Should().Be("error");
        _activities.Last().GetTagItem("error.message").Should().Be("boom");
    }

    [Fact]
    public async Task AgentRun_WithProviderNameItem_SetsProviderTag()
    {
        _activities.Clear();

        var ctx = new AgentRunContext
        {
            UserMessage = "hello",
            AgentId = "agent-1",
            AgentName = "TestAgent",
        };
        ctx.Items["gen_ai.provider.name"] = "openai";

        await _middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        var activity = _activities.Last();
        activity.GetTagItem("gen_ai.provider.name").Should().Be("openai");
    }

    [Fact]
    public async Task AgentRun_WithInvalidProviderNameItem_DoesNotSetProviderTag()
    {
        _activities.Clear();

        var ctx = new AgentRunContext
        {
            UserMessage = "hello",
            AgentId = "agent-1",
            AgentName = "TestAgent",
        };
        ctx.Items["gen_ai.provider.name"] = "   ";

        await _middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        var activity = _activities.Last();
        activity.GetTagItem("gen_ai.provider.name").Should().BeNull();
    }

    [Fact]
    public async Task LLMCall_EmitsChatSpan()
    {
        _activities.Clear();

        var ctx = new LLMCallContext
        {
            Request = new LLMRequest { Messages = [], Model = "gpt-4" },
            Provider = new FakeLLMProvider(),
        };

        await _middleware.InvokeAsync(ctx, () =>
        {
            ctx.Response = new LLMResponse
            {
                Content = "answer",
                Usage = new TokenUsage(10, 5, 15),
                FinishReason = "stop",
            };
            return Task.CompletedTask;
        });

        var chatActivities = _activities.Where(a =>
            a.GetTagItem("gen_ai.operation.name")?.ToString() == "chat").ToList();
        chatActivities.Should().NotBeEmpty();
        var activity = chatActivities.Last();
        activity.GetTagItem("gen_ai.provider.name").Should().Be("fake");
        activity.GetTagItem("gen_ai.usage.input_tokens").Should().Be(10);
        activity.GetTagItem("gen_ai.usage.output_tokens").Should().Be(5);
        activity.GetTagItem("gen_ai.response.finish_reason").Should().Be("stop");
    }

    [Fact]
    public async Task LLMCall_WithRequestId_ShouldSetRequestIdTag()
    {
        _activities.Clear();

        var ctx = new LLMCallContext
        {
            Request = new LLMRequest { Messages = [], Model = "gpt-4", RequestId = "session-42" },
            Provider = new FakeLLMProvider(),
        };

        await _middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        var activity = _activities.Where(a =>
            a.GetTagItem("gen_ai.operation.name")?.ToString() == "chat").Last();
        activity.GetTagItem("gen_ai.request.id").Should().Be("session-42");
    }

    [Fact]
    public async Task LLMCall_WithBlankProviderName_UsesUnknownAndHandlesNullResponse()
    {
        _activities.Clear();
        GenAIActivitySource.EnableSensitiveData = true;

        var ctx = new LLMCallContext
        {
            Request = new LLMRequest { Messages = null!, Model = "gpt-5.4" },
            Provider = new FakeLLMProvider("   "),
        };

        await _middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        var activity = _activities.Where(a =>
            a.GetTagItem("gen_ai.operation.name")?.ToString() == "chat").Last();
        activity.GetTagItem("gen_ai.provider.name").Should().Be("unknown");
        activity.GetTagItem("gen_ai.request.message_count").Should().Be(0);
        activity.GetTagItem("gen_ai.response.finish_reason").Should().BeNull();
        activity.GetTagItem("gen_ai.response.content").Should().BeNull();
    }

    [Fact]
    public async Task LLMCall_OnError_SetsErrorTag()
    {
        _activities.Clear();

        var ctx = new LLMCallContext
        {
            Request = new LLMRequest { Messages = [], Model = "gpt-4" },
            Provider = new FakeLLMProvider(),
        };

        var act = async () => await _middleware.InvokeAsync(ctx, () =>
            throw new InvalidOperationException("llm boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = _activities.Where(a =>
            a.GetTagItem("gen_ai.operation.name")?.ToString() == "chat").Last();
        activity.GetTagItem("error.message").Should().Be("llm boom");
        activity.GetTagItem("error.type")?.ToString().Should().Contain("InvalidOperationException");
    }

    [Fact]
    public async Task LLMCall_WithSensitiveDataEnabled_IncludesResponseContent()
    {
        _activities.Clear();
        GenAIActivitySource.EnableSensitiveData = true;

        var ctx = new LLMCallContext
        {
            Request = new LLMRequest { Messages = [], Model = "gpt-4" },
            Provider = new FakeLLMProvider(),
        };

        await _middleware.InvokeAsync(ctx, () =>
        {
            ctx.Response = new LLMResponse
            {
                Content = "sensitive-content",
            };
            return Task.CompletedTask;
        });

        var activity = _activities.Where(a =>
            a.GetTagItem("gen_ai.operation.name")?.ToString() == "chat").Last();
        activity.GetTagItem("gen_ai.response.content").Should().Be("sensitive-content");
    }

    [Fact]
    public async Task SensitiveData_IncludesContent_WhenEnabled()
    {
        _activities.Clear();
        GenAIActivitySource.EnableSensitiveData = true;

        var ctx = new AgentRunContext { UserMessage = "secret question" };
        await _middleware.InvokeAsync(ctx, () =>
        {
            ctx.Result = "secret answer";
            return Task.CompletedTask;
        });

        var activity = _activities.Last();
        activity.GetTagItem("gen_ai.request.input").Should().Be("secret question");
        activity.GetTagItem("gen_ai.response.output").Should().Be("secret answer");
    }

    [Fact]
    public async Task SensitiveData_ExcludesContent_WhenDisabled()
    {
        _activities.Clear();
        GenAIActivitySource.EnableSensitiveData = false;

        var ctx = new AgentRunContext { UserMessage = "secret question" };
        await _middleware.InvokeAsync(ctx, () =>
        {
            ctx.Result = "secret answer";
            return Task.CompletedTask;
        });

        var activity = _activities.Last();
        activity.GetTagItem("gen_ai.request.input").Should().BeNull();
        activity.GetTagItem("gen_ai.response.output").Should().BeNull();
    }

    private sealed class FakeLLMProvider(string name = "fake") : ILLMProvider
    {
        public string Name => name;
        public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, CancellationToken ct) =>
            AsyncEnumerable.Empty<LLMStreamChunk>();
    }
}
