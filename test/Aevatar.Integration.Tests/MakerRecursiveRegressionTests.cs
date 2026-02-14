using Aevatar;
using Aevatar.AI;
using Aevatar.AI.LLM;
using Aevatar.Cognitive;
using Aevatar.DependencyInjection;
using Aevatar.EventModules;
using Aevatar.Sample.Maker;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class MakerRecursiveRegressionTests
{
    [Fact]
    public async Task MakerRecursive_ShouldFinishAsLeaf_WhenRootIsAtomic()
    {
        var provider = new DeterministicMakerProvider(rootAtomic: true);
        await using var env = BuildEnvironment(provider);

        var result = await RunWorkflowAsync(env.Provider, env.Runtime, BuildWorkflowYaml(), "ROOT_ATOMIC");
        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be("ANSWER_ROOT");

        var recursiveResults = result.StepCompletions
            .Where(IsRecursiveResult)
            .ToList();

        recursiveResults.Should().ContainSingle(x => x.StepId == "solve_root");
        var root = recursiveResults.Single(x => x.StepId == "solve_root");
        root.Metadata["maker.stage"].Should().Be("leaf");
        root.Metadata["maker.atomic_decision"].Should().Be("True");
        recursiveResults.Should().NotContain(x => x.StepId.StartsWith("solve_root_child_", StringComparison.Ordinal));

        provider.StageCounters["decompose"].Should().Be(0);
        provider.StageCounters["compose"].Should().Be(0);
    }

    [Fact]
    public async Task MakerRecursive_ShouldDecomposeRecursively_ThenCompose()
    {
        var provider = new DeterministicMakerProvider(rootAtomic: false);
        await using var env = BuildEnvironment(provider);

        var result = await RunWorkflowAsync(env.Provider, env.Runtime, BuildWorkflowYaml(), "ROOT_COMPLEX");
        result.WorkflowCompleted.Should().NotBeNull();
        result.WorkflowCompleted!.Success.Should().BeTrue();
        result.WorkflowCompleted.Output.Should().Be("COMPOSED_ANSWER");

        var recursiveResults = result.StepCompletions
            .Where(IsRecursiveResult)
            .ToList();

        provider.StageCounters["decompose"].Should().BeGreaterThan(0);
        provider.StageCounters["compose"].Should().BeGreaterThan(0);

        var root = recursiveResults.Single(x => x.StepId == "solve_root");
        root.Metadata["maker.stage"].Should().Be("composed");
        root.Metadata["maker.atomic_decision"].Should().Be("False");

        var childResults = recursiveResults
            .Where(x => x.StepId.StartsWith("solve_root_child_", StringComparison.Ordinal))
            .ToList();
        childResults.Should().HaveCount(2);
        childResults.Should().OnlyContain(x => x.Metadata["maker.stage"] == "leaf");
    }

    private static bool IsRecursiveResult(StepCompletedEvent step) =>
        step.Metadata.TryGetValue("maker.recursive", out var value) && value == "true";

    private static TestEnvironment BuildEnvironment(DeterministicMakerProvider provider)
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddAevatarCognitive();
        services.AddSingleton<IEventModuleFactory, MakerModuleFactory>();
        services.AddSingleton<ILLMProvider>(provider);
        services.AddSingleton<ILLMProviderFactory>(provider);

        var sp = services.BuildServiceProvider();
        var runtime = sp.GetRequiredService<IActorRuntime>();
        return new TestEnvironment(sp, runtime);
    }

    private static async Task<WorkflowRunResult> RunWorkflowAsync(
        ServiceProvider provider,
        IActorRuntime runtime,
        string workflowYaml,
        string input)
    {
        var actor = await runtime.CreateAsync<WorkflowGAgent>("wf-maker-" + Guid.NewGuid().ToString("N")[..8]);
        var setWf = new SetWorkflowEvent { WorkflowYaml = workflowYaml, WorkflowName = "maker_regression" };
        var initEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(setWf),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };
        await actor.HandleEventAsync(initEnvelope);

        var stream = provider.GetRequiredService<IStreamProvider>().GetStream(actor.Id);
        var stepCompletions = new List<StepCompletedEvent>();
        var workflowCompleted = new TaskCompletionSource<WorkflowCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload == null) return Task.CompletedTask;

            if (envelope.Payload.TypeUrl.Contains("StepCompletedEvent"))
                stepCompletions.Add(envelope.Payload.Unpack<StepCompletedEvent>());
            else if (envelope.Payload.TypeUrl.Contains("WorkflowCompletedEvent"))
                workflowCompleted.TrySetResult(envelope.Payload.Unpack<WorkflowCompletedEvent>());

            return Task.CompletedTask;
        });

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent { Prompt = input, SessionId = "maker-regression" }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completed = await workflowCompleted.Task.WaitAsync(timeout.Token);
        await runtime.DestroyAsync(actor.Id);
        return new WorkflowRunResult(completed, stepCompletions);
    }

    private static string BuildWorkflowYaml() => """
        name: maker_regression
        roles:
          - id: coordinator
            name: Coordinator
            system_prompt: "coordinator"
            provider: mock-maker
          - id: worker_a
            name: WorkerA
            system_prompt: "worker"
            provider: mock-maker
          - id: worker_b
            name: WorkerB
            system_prompt: "worker"
            provider: mock-maker
          - id: worker_c
            name: WorkerC
            system_prompt: "worker"
            provider: mock-maker
        steps:
          - id: solve_root
            type: maker_recursive
            parameters:
              depth: "0"
              max_depth: "3"
              max_subtasks: "2"
              delimiter: "\n---\n"
              k: "1"
              max_response_length: "5000"
              parallel_step_type: "parallel"
              vote_step_type: "maker_vote"
              atomic_workers: "coordinator,coordinator,coordinator"
              decompose_workers: "coordinator,coordinator,coordinator"
              solve_workers: "worker_a,worker_b,worker_c"
              compose_workers: "coordinator,coordinator,coordinator"
              atomic_prompt: "[ATOMIC_CHECK] return ATOMIC or DECOMPOSE."
              decompose_prompt: "[DECOMPOSE_TASK] split task by delimiter."
              solve_prompt: "[SOLVE_TASK] solve this atomic task."
              compose_prompt: "[COMPOSE_TASK] merge child solutions."
        """;

    private sealed class DeterministicMakerProvider(bool rootAtomic) : ILLMProvider, ILLMProviderFactory
    {
        public Dictionary<string, int> StageCounters { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["atomic"] = 0,
            ["decompose"] = 0,
            ["solve"] = 0,
            ["compose"] = 0,
        };

        public string Name => "mock-maker";

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            var userMessage = request.Messages.LastOrDefault(x => x.Role == "user")?.Content ?? "";
            var response = Resolve(userMessage);
            return Task.FromResult(new LLMResponse
            {
                Content = response,
                FinishReason = "stop",
                Usage = new TokenUsage(50, 20, 70),
            });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var full = await ChatAsync(request, ct);
            foreach (var ch in full.Content ?? "")
                yield return new LLMStreamChunk { DeltaContent = ch.ToString() };
            yield return new LLMStreamChunk { IsLast = true, Usage = full.Usage };
        }

        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => ["mock-maker"];

        private string Resolve(string userMessage)
        {
            if (ContainsAny(userMessage, "[ATOMIC_CHECK]", "ATOMIC or DECOMPOSE"))
            {
                StageCounters["atomic"]++;
                if (rootAtomic) return "ATOMIC";
                return userMessage.Contains("ROOT_COMPLEX", StringComparison.OrdinalIgnoreCase)
                    ? "DECOMPOSE"
                    : "ATOMIC";
            }

            if (ContainsAny(userMessage, "[DECOMPOSE_TASK]", "Break the task into"))
            {
                StageCounters["decompose"]++;
                // Avoid using the same delimiter as candidate-vote merging.
                return "1. SUBTASK_A\n2. SUBTASK_B";
            }

            if (ContainsAny(userMessage, "[SOLVE_TASK]", "Solve this atomic task"))
            {
                StageCounters["solve"]++;
                if (userMessage.Contains("SUBTASK_A", StringComparison.Ordinal)) return "ANSWER_A";
                if (userMessage.Contains("SUBTASK_B", StringComparison.Ordinal)) return "ANSWER_B";
                if (userMessage.Contains("ROOT_ATOMIC", StringComparison.Ordinal)) return "ANSWER_ROOT";
                return "ANSWER_GENERIC";
            }

            if (ContainsAny(userMessage, "[COMPOSE_TASK]", "Merge child solutions"))
            {
                StageCounters["compose"]++;
                return "COMPOSED_ANSWER";
            }

            return "ATOMIC";
        }

        private static bool ContainsAny(string text, params string[] probes) =>
            probes.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record TestEnvironment(ServiceProvider Provider, IActorRuntime Runtime) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record WorkflowRunResult(
        WorkflowCompletedEvent? WorkflowCompleted,
        List<StepCompletedEvent> StepCompletions);
}
