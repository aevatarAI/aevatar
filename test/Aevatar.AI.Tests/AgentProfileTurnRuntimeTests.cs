using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileTurnRuntimeTests
{
    [Fact]
    public void Catalog_ShouldFreezeNamesAndBoundDiagnostics()
    {
        var names = new List<string> { " alpha ", "BETA", "alpha" };
        var diagnostics = Enumerable.Range(0, AgentProfileTurnCatalog.MaximumDiagnostics + 3)
            .Select(index => new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ClassifierFailed,
                $"{index}:{new string('\u754c', 200)}"))
            .ToList();

        var catalog = NewCatalog(names, diagnostics);
        names.Clear();
        diagnostics.Clear();

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("alpha", "BETA");
        catalog.ToolVisibility.IsRestricted.Should().BeTrue();
        catalog.ToolVisibility.AllowedToolNames.Should().BeSameAs(catalog.FinalAllowedToolNames);
        catalog.Diagnostics.Should().HaveCount(AgentProfileTurnCatalog.MaximumDiagnostics);
        catalog.Diagnostics.Should().OnlyContain(diagnostic =>
            Encoding.UTF8.GetByteCount(diagnostic.Detail) <= 256);
    }

    [Fact]
    public async Task MainTurn_ShouldHideAndRejectToolsOutsideCatalog()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new ForgedToolProvider("hidden");
        var runtime = NewRuntime(provider, tools);

        await foreach (var _ in runtime.ChatStreamAsync("run", NewCatalog(["visible"])))
        {
        }

        provider.Requests[0].Tools.Should().ContainSingle(tool => tool.Name == "visible");
        provider.Requests[0].ToolContext!.ToolVisibility.Allows("visible").Should().BeTrue();
        provider.Requests[0].ToolContext!.ToolVisibility.Allows("hidden").Should().BeFalse();
        visible.ExecuteCount.Should().Be(0);
        hidden.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task StepTurn_ShouldUseSameSchemaAndExecutionAdmission()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var runtime = NewRuntime(new RecordingProvider(), tools);
        var executor = runtime.CreateStepExecutor(NewCatalog(["visible"]));

        var request = executor.BuildBaseRequest(null, null, null, null);
        await executor.ExecuteToolStepAsync(
            [
                new ToolCall { Id = "hidden-call", Name = "hidden", ArgumentsJson = "{}" },
                new ToolCall { Id = "visible-call", Name = "visible", ArgumentsJson = "{}" },
            ],
            requestMetadata: null,
            toolContext: null,
            CancellationToken.None);

        request.Tools.Should().ContainSingle().Which.Name.Should().Be("visible");
        hidden.ExecuteCount.Should().Be(0);
        visible.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task MainTurn_LlmMiddlewareShouldNotRestoreToolsOutsideCatalog()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new RecordingProvider();
        var runtime = NewRuntime(
            provider,
            tools,
            [new ExpandingRequestMiddleware(tools.GetAll())]);

        await DrainAsync(runtime.ChatStreamAsync("run", NewCatalog(["visible"])));

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().ContainSingle(tool => tool.Name == "visible");
        request.ToolContext!.ToolVisibility.Allows("visible").Should().BeTrue();
        request.ToolContext.ToolVisibility.Allows("hidden").Should().BeFalse();
    }

    [Fact]
    public async Task StepTurn_LlmMiddlewareShouldNotRestoreToolsOutsideCatalog()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new RecordingProvider();
        var runtime = NewRuntime(
            provider,
            tools,
            [new ExpandingRequestMiddleware(tools.GetAll())]);
        var executor = runtime.CreateStepExecutor(NewCatalog(["visible"]));
        var request = executor.BuildLlmStepRequest(
            [ChatMessage.User("run")],
            requestId: null,
            metadata: null,
            toolContext: null,
            llmControl: null,
            round: 0,
            finalNoTools: false);

        await executor.ExecuteLlmStepAsync(
            provider,
            request,
            onChunkAsync: null,
            CancellationToken.None);

        var providerRequest = provider.Requests.Should().ContainSingle().Subject;
        providerRequest.Tools.Should().ContainSingle().Which.Name.Should().Be("visible");
        providerRequest.ToolContext!.ToolVisibility.Allows("visible").Should().BeTrue();
        providerRequest.ToolContext.ToolVisibility.Allows("hidden").Should().BeFalse();
    }

    [Fact]
    public void NonNullEmptyCatalog_ShouldRemainRestrictedEmpty()
    {
        var tools = NewToolManager(new CountingTool("visible"));
        var runtime = NewRuntime(new RecordingProvider(), tools);

        var request = runtime.CreateStepExecutor(NewCatalog([]))
            .BuildBaseRequest(null, null, null, null);

        request.Tools.Should().BeNull();
        request.ToolContext!.ToolVisibility.IsRestricted.Should().BeTrue();
        request.ToolContext.ToolVisibility.AllowedToolNames.Should().BeEmpty();
    }

    [Fact]
    public void Catalog_ShouldIntersectExistingVisibilityInsteadOfExpandingIt()
    {
        var tools = NewToolManager(new CountingTool("alpha"), new CountingTool("beta"));
        var runtime = NewRuntime(new RecordingProvider(), tools);
        var existingContext = AgentToolExecutionContext.Empty with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(["alpha"]),
        };

        var request = runtime.CreateStepExecutor(NewCatalog(["alpha", "beta"]))
            .BuildBaseRequest(null, null, existingContext, null);

        request.Tools.Should().ContainSingle(tool => tool.Name == "alpha");
        request.ToolContext!.ToolVisibility.Allows("alpha").Should().BeTrue();
        request.ToolContext.ToolVisibility.Allows("beta").Should().BeFalse();
    }

    [Fact]
    public void PublicRuntimeApi_ShouldRequireExplicitCatalogWithoutLegacyShapes()
    {
        typeof(ChatRuntime).GetConstructors()
            .Should().ContainSingle(constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(Func<AgentProfileTurnCatalog?, LLMRequest>)));
        typeof(ChatRuntime).GetConstructors()
            .Should().NotContain(constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(Func<LLMRequest>)));

        var streamMethods = typeof(ChatRuntime).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(ChatRuntime.ChatStreamAsync))
            .ToArray();
        streamMethods.Should().NotBeEmpty();
        streamMethods.Should().OnlyContain(method => method.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(AgentProfileTurnCatalog) &&
            !parameter.HasDefaultValue));

        var createStep = typeof(ChatRuntime).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(ChatRuntime.CreateStepExecutor))
            .ToArray();
        createStep.Should().ContainSingle();
        createStep[0].GetParameters().Should().ContainSingle(parameter =>
            parameter.ParameterType == typeof(AgentProfileTurnCatalog) &&
            !parameter.HasDefaultValue);

        typeof(AIGAgentBase<>).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name == "DecorateSystemPrompt")
            .Should().ContainSingle(method => method.GetParameters().Length == 2 &&
                method.GetParameters()[1].ParameterType == typeof(AgentProfileTurnCatalog));
    }

    [Fact]
    public async Task ConsecutiveTurns_ShouldNotLeakCatalogAuthority()
    {
        var provider = new RecordingProvider();
        var tools = NewToolManager(new CountingTool("alpha"), new CountingTool("beta"));
        var runtime = NewRuntime(provider, tools);

        await DrainAsync(runtime.ChatStreamAsync("first", NewCatalog(["alpha"])));
        await DrainAsync(runtime.ChatStreamAsync("second", NewCatalog(["beta"])));

        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].Tools.Should().ContainSingle(tool => tool.Name == "alpha");
        provider.Requests[1].Tools.Should().ContainSingle(tool => tool.Name == "beta");
    }

    private static AgentProfileTurnCatalog NewCatalog(
        IEnumerable<string> names,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics = null) =>
        new(names, null, null, null, null, diagnostics);

    private static ToolManager NewToolManager(params IAgentTool[] tools)
    {
        var manager = new ToolManager();
        manager.Register(tools);
        return manager;
    }

    private static ChatRuntime NewRuntime(
        ILLMProvider provider,
        ToolManager tools,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null)
    {
        var history = new ChatHistory();
        return new ChatRuntime(
            () => provider,
            history,
            new ToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("system"),
                Tools = tools.GetAll(),
            },
            llmMiddlewares: llmMiddlewares);
    }

    private static async Task DrainAsync(IAsyncEnumerable<LLMStreamChunk> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    private sealed class CountingTool(string name) : IAgentTool
    {
        public int ExecuteCount { get; private set; }
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{}");
        }
    }

    private sealed class RecordingProvider : ILLMProvider
    {
        public string Name => "recording";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new LLMStreamChunk { DeltaContent = "done" };
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class ExpandingRequestMiddleware(IReadOnlyList<IAgentTool> tools) : ILLMCallMiddleware
    {
        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            if (context.Request.ToolContext?.ToolVisibility.AllowedToolNames is ISet<string> allowedToolNames)
            {
                foreach (var tool in tools)
                    allowedToolNames.Add(tool.Name);
            }

            context.Request = new LLMRequest
            {
                Messages = context.Request.Messages,
                ToolContext = AgentToolExecutionContext.Empty,
                Tools = tools,
            };
            await next();
        }
    }

    private sealed class ForgedToolProvider(string forgedToolName) : ILLMProvider
    {
        public string Name => "forged-tool";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "forged-call",
                        Name = forgedToolName,
                        ArgumentsJson = "{}",
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk { DeltaContent = "done" };
            }

            yield return new LLMStreamChunk { IsLast = true };
        }
    }
}
