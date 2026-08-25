using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class AIGAgentBaseToolRefreshTests
{
    [Fact]
    public async Task RefreshRuntime_WhenSourceToolsShrink_ShouldRemoveStaleTools()
    {
        var source = new MutableToolSource("tool-a", "tool-b");
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolSource>(source);
        services.AddSingleton<IEventStore, InMemoryEventStoreForTests>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent(provider.GetServices<IAgentToolSource>())
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        await agent.ActivateAsync();
        agent.GetRegisteredToolNames().Should().Equal("tool-a", "tool-b");

        source.SetTools("tool-b");
        await agent.TriggerRuntimeRefreshAsync();

        agent.GetRegisteredToolNames().Should().Equal("tool-b");
    }

    [Fact]
    public async Task RefreshRuntime_WhenSourceToolsChanged_ShouldKeepManualTools()
    {
        var source = new MutableToolSource("source-old");
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolSource>(source);
        services.AddSingleton<IEventStore, InMemoryEventStoreForTests>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent(provider.GetServices<IAgentToolSource>())
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        await agent.ActivateAsync();
        agent.RegisterManualTool("manual-tool");
        agent.GetRegisteredToolNames().Should().Equal("manual-tool", "source-old");

        source.SetTools("source-new");
        await agent.TriggerRuntimeRefreshAsync();

        agent.GetRegisteredToolNames().Should().Equal("manual-tool", "source-new");
    }

    [Fact]
    public async Task ActivateAsync_WhenSourceToolNamesCollide_ShouldKeepBuiltInToolsAndDegradeSources()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStoreForTests>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent(
            [new MutableToolSource("source-collision"), new MutableToolSource("SOURCE-COLLISION")])
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        agent.RegisterManualTool("built-in-tool");

        await agent.ActivateAsync();

        agent.GetRegisteredToolNames().Should().Equal("built-in-tool");
    }

    [Fact]
    public async Task ActivateAsync_WhenSourceFails_ShouldRemainFailClosed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStoreForTests>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent([new FailingToolSource()])
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        Func<Task> act = () => agent.ActivateAsync();

        var exception = await act.Should().ThrowAsync<AgentToolDiscoveryException>();
        exception.Which.Failure.Code.Should().Be(AgentToolDiscoveryFailureCode.SourceFailed);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenApprovalHandlerMissingAndToolRequiresApproval_ShouldDenyWithoutExecutingTool()
    {
        var tool = new CountingApprovalRequiredTool();
        var providerFactory = new ToolCallingLLMProviderFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, InMemoryEventStoreForTests>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddSingleton<IAuditTrailAppender, AppendedAuditTrail>();
        services.AddSingleton<IAuditActorIdentityHasher, StableIdentityHasher>();
        services.AddSingleton<IAgentToolAdmissionLedger>(AlwaysStartingAgentToolAdmissionLedger.Instance);
        services.AddSingleton<IAgentToolExecutionPort, AdmittedAgentToolExecutor>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        using var provider = services.BuildServiceProvider();
        var agent = new TestAIGAgent(
            [],
            providerFactory,
            provider.GetRequiredService<IAgentToolExecutionPort>())
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        await agent.ActivateAsync();
        agent.RegisterToolForTest(tool);

        var chunks = await agent.StreamAsync("run tool");

        chunks.Select(x => x.DeltaContent).Where(x => x is not null).Should()
            .ContainSingle()
            .Which.Should().Contain("actor-owned durable approval continuation");
        tool.ExecuteCount.Should().Be(0);
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
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "test-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private sealed class TestAIGAgent : AIGAgentBase<RoleGAgentState>
    {
        public TestAIGAgent(
            IEnumerable<IAgentToolSource> toolSources,
            ILLMProviderFactory? llmProviderFactory = null,
            IAgentToolExecutionPort? toolExecutionPort = null)
            : base(
                toolExecutionPort ?? TestAgentToolExecutionPort.Instance,
                llmProviderFactory ?? new StubLLMProviderFactory(),
                Array.Empty<IAIGAgentExecutionHook>(),
                Array.Empty<IAgentRunMiddleware>(),
                Array.Empty<ILLMCallMiddleware>(),
                toolSources)
        {
            InitializeId();
        }

        public IReadOnlyList<string> GetRegisteredToolNames() => Tools.GetAll()
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        public void RegisterToolForTest(IAgentTool tool) => RegisterTool(tool);

        public void RegisterManualTool(string name) => RegisterTool(new NamedTool(name));

        public Task TriggerRuntimeRefreshAsync() => OnEffectiveConfigChangedAsync(EffectiveConfig, CancellationToken.None);

        public async Task<IReadOnlyList<LLMStreamChunk>> StreamAsync(string userMessage)
        {
            var chunks = new List<LLMStreamChunk>();
            await foreach (var chunk in ChatStreamAsync(
                               userMessage,
                               requestId: "request-approval",
                               turnCatalog: null))
                chunks.Add(chunk);
            return chunks;
        }

        protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(RoleGAgentState state)
        {
            _ = state;
            return new AIAgentConfigStateOverrides();
        }
    }

    private sealed class MutableToolSource : IAgentToolSource
    {
        private IReadOnlyList<IAgentTool> _tools;

        public MutableToolSource(params string[] toolNames)
        {
            _tools = ToTools(toolNames);
        }

        public void SetTools(params string[] toolNames)
        {
            _tools = ToTools(toolNames);
        }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_tools);
        }

        private static IReadOnlyList<IAgentTool> ToTools(IEnumerable<string> toolNames) =>
            toolNames.Select(name => (IAgentTool)new NamedTool(name)).ToList();
    }

    private sealed class FailingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("synthetic discovery failure");
        }
    }

    private sealed class NamedTool : IAgentTool
    {
        public NamedTool(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string Description => Name;
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{}");
        }
    }

    private sealed class CountingApprovalRequiredTool : IAgentTool
    {
        public const string ToolName = "approval_required_tool";

        public int ExecuteCount { get; private set; }

        public string Name => ToolName;
        public string Description => "Requires approval.";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;
        public bool IsDestructive => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCount++;
            return Task.FromResult("""{"executed":true}""");
        }
    }

    private sealed class StubLLMProviderFactory : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => new StubLLMProvider(name);
        public ILLMProvider GetDefault() => new StubLLMProvider("default");
        public IReadOnlyList<string> GetAvailableProviders() => ["default"];
    }

    private sealed class StubLLMProvider(string name) : ILLMProvider
    {
        public string Name => name;

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = request;
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ToolCallingLLMProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "tool-calling";

        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var toolResult = request.Messages.LastOrDefault(static message => message.Role == "tool")?.Content;
            if (toolResult is not null)
            {
                yield return new LLMStreamChunk { DeltaContent = toolResult };
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-approval",
                    Name = CountingApprovalRequiredTool.ToolName,
                    ArgumentsJson = "{}",
                },
            };
            yield return new LLMStreamChunk { IsLast = true };
            await Task.CompletedTask;
        }
    }
}

internal sealed class TestAgentToolExecutionPort : IAgentToolExecutionPort
{
    public static TestAgentToolExecutionPort Instance { get; } = new();

    public async Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default)
    {
        var safety = request.Tool.GetCallSafety(request.ArgumentsJson);
        try
        {
            string resultJson;
            using (AgentToolContextScope.Push(request.ExecutionContext))
                resultJson = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
            return CreateOutcome(
                request,
                safety,
                AgentToolExecutionOutcomeKind.Executed,
                AgentToolReceiptStatus.Success,
                resultJson,
                string.Empty,
                string.Empty);
        }
        catch (Exception ex)
        {
            var resultJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                error = "tool_execution_failed",
                code = "tool_execution_failed",
                message = ex.GetType().Name,
                tool_name = request.Tool.Name,
            });
            return CreateOutcome(
                request,
                safety,
                AgentToolExecutionOutcomeKind.Failed,
                AgentToolReceiptStatus.Error,
                resultJson,
                "tool_execution_failed",
                ex.GetType().Name);
        }
    }

    private static AgentToolExecutionOutcome CreateOutcome(
        AgentToolExecutionRequest request,
        AgentToolCallSafety safety,
        AgentToolExecutionOutcomeKind kind,
        AgentToolReceiptStatus status,
        string resultJson,
        string failureCode,
        string safeMessage) =>
        new(
            kind,
            resultJson,
            new AgentToolReceipt
            {
                CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                ToolName = request.Tool.Name,
                Status = status,
                ResultJson = resultJson,
                ErrorCode = failureCode,
                ErrorMessage = safeMessage,
                IsDestructive = safety.IsDestructive,
            },
            IsMutation: !safety.IsReadOnly,
            failureCode,
            safeMessage,
            kind == AgentToolExecutionOutcomeKind.Executed
                ? AgentToolExecutionFailureStage.None
                : AgentToolExecutionFailureStage.TerminalExecution,
            TerminalInvoked: true,
            Retryable: false,
            AuditCompleted: true);
}

internal sealed class AlwaysStartingAgentToolAdmissionLedger : IAgentToolAdmissionLedger
{
    public static AlwaysStartingAgentToolAdmissionLedger Instance { get; } = new();

    public Task<AgentToolAdmissionResult> TryStartAsync(
        AgentToolAdmissionFact fact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
    }
}
