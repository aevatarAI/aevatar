using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class WorkflowYamlScriptParityTests
{
    [Fact]
    public async Task YamlAndScript_ShouldProduceSameOutput_ForSameInput()
    {
        const string input = "case-b-manual-review";

        var workflowOutput = await RunWorkflowUppercaseAsync(input);
        var scriptOutput = await RunScriptUppercaseAsync(input);

        workflowOutput.Should().Be("CASE-B-MANUAL-REVIEW");
        scriptOutput.Should().Be(workflowOutput);
    }

    [Fact]
    public async Task WorkflowYamlPath_ShouldRemainStable_AfterScriptExecution()
    {
        const string input = "case-c-auto-approve";

        var beforeMigration = await RunWorkflowUppercaseAsync(input);
        var scriptOutput = await RunScriptUppercaseAsync(input);
        var afterMigration = await RunWorkflowUppercaseAsync(input);

        beforeMigration.Should().Be("CASE-C-AUTO-APPROVE");
        scriptOutput.Should().Be(beforeMigration);
        afterMigration.Should().Be(beforeMigration);
    }

    private static async Task<string> RunWorkflowUppercaseAsync(string prompt)
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddAevatarWorkflow();
        services.AddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        var actor = await runtime.CreateAsync<WorkflowGAgent>("wf-parity-" + Guid.NewGuid().ToString("N")[..8]);
        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowDefinitionEvent
            {
                WorkflowYaml = BuildParityWorkflowYaml(),
                WorkflowName = "yaml_script_parity",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        });

        var stream = provider.GetRequiredService<IStreamProvider>().GetStream(actor.Id);
        var completedTcs = new TaskCompletionSource<WorkflowCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) == true)
            {
                completedTcs.TrySetResult(envelope.Payload.Unpack<WorkflowCompletedEvent>());
            }

            return Task.CompletedTask;
        });

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = prompt,
                SessionId = "parity-session",
            }),
            PublisherId = "test",
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completed = await completedTcs.Task.WaitAsync(timeout.Token);
        await runtime.DestroyAsync(actor.Id);
        return completed.Output ?? string.Empty;
    }

    private static async Task<string> RunScriptUppercaseAsync(string prompt)
    {
        var compiler = new RoslynScriptPackageCompiler(new ScriptSandboxPolicy());
        var compilation = await compiler.CompileAsync(
            new ScriptPackageCompilationRequest(
                ScriptId: "parity-uppercase-script",
                Revision: "rev-1",
                Source: BuildParityScriptSource()),
            CancellationToken.None);

        compilation.IsSuccess.Should().BeTrue("script must compile for parity check");
        compilation.CompiledDefinition.Should().NotBeNull();
        var definition = compilation.CompiledDefinition!;
        await using var _ = definition as IAsyncDisposable;

        var payload = Any.Pack(new Struct
        {
            Fields =
            {
                ["prompt"] = Google.Protobuf.WellKnownTypes.Value.ForString(prompt),
            },
        });

        var decision = await definition.HandleRequestedEventAsync(
            new ScriptRequestedEventEnvelope(
                EventType: "chat.requested",
                Payload: payload,
                EventId: "evt-parity-1",
                CorrelationId: "corr-parity-1",
                CausationId: "cause-parity-1"),
            new ScriptExecutionContext(
                ActorId: "runtime-script-parity",
                ScriptId: "parity-uppercase-script",
                Revision: "rev-1",
                RunId: "run-parity-1",
                CorrelationId: "corr-parity-1",
                InputPayload: payload),
            CancellationToken.None);

        var outputEvent = decision.DomainEvents.OfType<StringValue>().Single();
        return outputEvent.Value;
    }

    private static string BuildParityWorkflowYaml() => """
        name: yaml_script_parity
        roles:
          - id: transformer
            name: Transformer
            system_prompt: "deterministic transform only"
        steps:
          - id: to_upper
            type: transform
            parameters:
              op: uppercase
        """;

    private static string BuildParityScriptSource() => """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions.Definitions;
        using Google.Protobuf;
        using Google.Protobuf.WellKnownTypes;

        public sealed class ParityUppercaseScript : IScriptPackageRuntime
        {
            public Task<ScriptHandlerResult> HandleRequestedEventAsync(
                ScriptRequestedEventEnvelope requestedEvent,
                ScriptExecutionContext context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var prompt = string.Empty;
                if (context.InputPayload != null && context.InputPayload.Is(Struct.Descriptor))
                {
                    var input = context.InputPayload.Unpack<Struct>();
                    if (input.Fields.TryGetValue("prompt", out var promptValue))
                        prompt = promptValue.StringValue ?? string.Empty;
                }

                return Task.FromResult(new ScriptHandlerResult(
                    new IMessage[] { new StringValue { Value = prompt.ToUpperInvariant() } }));
            }

            public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
                IReadOnlyDictionary<string, Any> currentState,
                ScriptDomainEventEnvelope domainEvent,
                CancellationToken ct)
                => ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);

            public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
                IReadOnlyDictionary<string, Any> currentReadModel,
                ScriptDomainEventEnvelope domainEvent,
                CancellationToken ct)
                => ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
        }
        """;
}
