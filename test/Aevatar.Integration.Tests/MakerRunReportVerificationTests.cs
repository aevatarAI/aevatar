using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Agents;
using Aevatar.Demos.Maker;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Maker.Projection;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class MakerRunReportVerificationTests
{
    [Fact]
    public async Task Verification_ShouldPass_WhenRunCoversFullMakerFlow()
    {
        await using var env = BuildEnvironment(new DeterministicMakerProvider());

        var run = await RunWorkflowAndBuildReportAsync(
            env.Provider,
            env.Runtime,
            BuildWorkflowYaml(),
            "Analyze the paper and provide a complete MAKER-style answer.");

        run.WorkflowCompleted.Success.Should().BeTrue();
        run.Report.Verification.FullFlowPassed.Should().BeTrue();
        run.Report.Verification.FailedChecks.Should().BeEmpty();
        run.Report.Verification.Checks.Should().Contain(x => x.Name == "recursive_stage_coverage" && x.Passed);
        run.Report.Verification.Checks.Should().Contain(x => x.Name == "internal_vote_step_coverage" && x.Passed);
        run.Report.Verification.Checks.Should().Contain(x => x.Name == "child_recursion_presence" && x.Passed);
    }

    [Fact]
    public async Task Verification_ShouldFail_WhenRunStaysAtomicOnly()
    {
        await using var env = BuildEnvironment(new AtomicOnlyProvider());

        var run = await RunWorkflowAndBuildReportAsync(
            env.Provider,
            env.Runtime,
            BuildWorkflowYaml(),
            "Provide a concise answer.");

        run.WorkflowCompleted.Success.Should().BeTrue();
        run.Report.Verification.FullFlowPassed.Should().BeFalse();
        run.Report.Verification.FailedChecks.Should().Contain("recursive_stage_coverage");
        run.Report.Verification.FailedChecks.Should().Contain("internal_vote_step_coverage");
        run.Report.Verification.FailedChecks.Should().Contain("child_recursion_presence");
    }

    private static TestEnvironment BuildEnvironment(ILLMProvider provider)
    {
        if (provider is not ILLMProviderFactory providerFactory)
            throw new InvalidOperationException("Provider must implement ILLMProviderFactory.");

        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddAevatarWorkflow();
        services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();
        services.AddWorkflowMakerExtensions();
        services.AddSingleton(provider);
        services.AddSingleton<ILLMProvider>(_ => provider);
        services.AddSingleton<ILLMProviderFactory>(_ => providerFactory);

        var sp = services.BuildServiceProvider();
        var runtime = sp.GetRequiredService<IActorRuntime>();
        return new TestEnvironment(sp, runtime);
    }

    private static async Task<VerificationRunResult> RunWorkflowAndBuildReportAsync(
        ServiceProvider provider,
        IActorRuntime runtime,
        string workflowYaml,
        string input)
    {
        var definitionActor = await runtime.CreateAsync<WorkflowGAgent>("wf-maker-definition-" + Guid.NewGuid().ToString("N")[..8]);
        var runActor = await runtime.CreateAsync<WorkflowRunGAgent>("wf-maker-run-" + Guid.NewGuid().ToString("N")[..8]);
        var recorder = new MakerRunProjectionAccumulator(runActor.Id);

        await definitionActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowDefinitionEvent
            {
                WorkflowYaml = workflowYaml,
                WorkflowName = "maker_report_verification",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        });

        await runActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = definitionActor.Id,
                WorkflowYaml = workflowYaml,
                WorkflowName = "maker_report_verification",
                RunId = "maker-report-run",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        });

        var stream = provider.GetRequiredService<IStreamProvider>().GetStream(runActor.Id);
        var workflowCompleted = new TaskCompletionSource<WorkflowCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            recorder.RecordEnvelope(envelope);
            var payload = envelope.Payload;
            if (payload?.Is(WorkflowCompletedEvent.Descriptor) == true)
                workflowCompleted.TrySetResult(payload.Unpack<WorkflowCompletedEvent>());

            return Task.CompletedTask;
        });

        var startedAt = DateTimeOffset.UtcNow;
        await runActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent { Prompt = input, SessionId = "maker-verification" }),
            PublisherId = "test",
            Direction = EventDirection.Self,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var completed = await workflowCompleted.Task.WaitAsync(timeout.Token);
        var endedAt = DateTimeOffset.UtcNow;

        var topology = await CollectTopologyAsync(runtime, runActor.Id);
        var report = recorder.BuildReport(
            "maker_report_verification",
            "inline:test",
            "test-provider",
            "test-model",
            input,
            startedAt,
            endedAt,
            timedOut: false,
            topology);

        await runtime.DestroyAsync(runActor.Id);
        await runtime.DestroyAsync(definitionActor.Id);
        return new VerificationRunResult(completed, report);
    }

    private static async Task<List<MakerTopologyEdge>> CollectTopologyAsync(IActorRuntime runtime, string rootActorId)
    {
        var topology = new List<MakerTopologyEdge>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(rootActorId);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!visited.Add(parentId))
                continue;

            var parentActor = await runtime.GetAsync(parentId);
            if (parentActor == null)
                continue;

            var children = await parentActor.GetChildrenIdsAsync();
            foreach (var childId in children)
            {
                topology.Add(new MakerTopologyEdge(parentId, childId));
                queue.Enqueue(childId);
            }
        }

        return topology;
    }

    private static string BuildWorkflowYaml() => """
        name: maker_report_verification
        roles:
          - id: coordinator
            name: Coordinator
            system_prompt: "coordinator"
            provider: test-provider
            connectors:
              - maker_post_processor
          - id: worker_a
            name: WorkerA
            system_prompt: "worker"
            provider: test-provider
          - id: worker_b
            name: WorkerB
            system_prompt: "worker"
            provider: test-provider
          - id: worker_c
            name: WorkerC
            system_prompt: "worker"
            provider: test-provider
        steps:
          - id: solve_root
            type: maker_recursive
            parameters:
              depth: "0"
              max_depth: "3"
              max_subtasks: "4"
              delimiter: "\n---\n"
              k: "1"
              max_response_length: "2200"
              parallel_step_type: "parallel"
              vote_step_type: "maker_vote"
              atomic_workers: "coordinator,coordinator,coordinator"
              decompose_workers: "coordinator,coordinator,coordinator"
              solve_workers: "worker_a,worker_b,worker_c"
              compose_workers: "coordinator,coordinator,coordinator"
              atomic_prompt: "You are a MAKER atomicity judge. Return exactly one token: ATOMIC or DECOMPOSE."
              decompose_prompt: "You are a MAKER decomposer. Output only subtasks as a numbered list."
              solve_prompt: "You are a MAKER worker. Solve this atomic task directly."
              compose_prompt: "You are a MAKER composer. Merge child solutions into one coherent answer."
          - id: connector_post
            type: connector_call
            role: coordinator
            parameters:
              connector: maker_post_processor
              timeout_ms: "2000"
              retry: "0"
              on_missing: "skip"
              on_error: "continue"
        """;

    private sealed class AtomicOnlyProvider : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "atomic-only";

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            var userMessage = request.Messages.LastOrDefault(x => x.Role == "user")?.Content ?? "";
            var content = Resolve(userMessage);
            return Task.FromResult(new LLMResponse
            {
                Content = content,
                FinishReason = "stop",
                Usage = new TokenUsage(16, 16, 32),
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
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        private static string Resolve(string userMessage)
        {
            if (userMessage.Contains("ATOMIC or DECOMPOSE", StringComparison.OrdinalIgnoreCase))
                return "ATOMIC";

            if (userMessage.Contains("MAKER worker", StringComparison.OrdinalIgnoreCase))
                return "ATOMIC_ONLY_ANSWER";

            if (userMessage.Contains("MAKER composer", StringComparison.OrdinalIgnoreCase))
                return "ATOMIC_ONLY_COMPOSE";

            if (userMessage.Contains("MAKER decomposer", StringComparison.OrdinalIgnoreCase))
                return "1. SHOULD_NOT_REACH";

            return "ATOMIC";
        }
    }

    private sealed record TestEnvironment(ServiceProvider Provider, IActorRuntime Runtime) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record VerificationRunResult(
        WorkflowCompletedEvent WorkflowCompleted,
        MakerRunReport Report);
}
