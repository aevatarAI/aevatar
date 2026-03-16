using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Core.Agents;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Integration.Tests.TestDoubles.Protocols;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
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
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        var definitionActor = await runtime.CreateAsync<WorkflowGAgent>("wf-parity-definition-" + Guid.NewGuid().ToString("N")[..8]);
        var runActor = await runtime.CreateAsync<WorkflowRunGAgent>("wf-parity-run-" + Guid.NewGuid().ToString("N")[..8]);

        await definitionActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowDefinitionEvent
            {
                WorkflowYaml = BuildParityWorkflowYaml(),
                WorkflowName = "yaml_script_parity",
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        });

        await runActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = definitionActor.Id,
                WorkflowYaml = BuildParityWorkflowYaml(),
                WorkflowName = "yaml_script_parity",
                RunId = "yaml-script-parity-run",
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        });

        var stream = provider.GetRequiredService<IStreamProvider>().GetStream(runActor.Id);
        var completedTcs = new TaskCompletionSource<WorkflowCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) == true)
                completedTcs.TrySetResult(envelope.Payload.Unpack<WorkflowCompletedEvent>());

            return Task.CompletedTask;
        });

        await runActor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = prompt,
                SessionId = "parity-session",
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completed = await completedTcs.Task.WaitAsync(timeout.Token);
        await runtime.DestroyAsync(runActor.Id);
        await runtime.DestroyAsync(definitionActor.Id);
        return completed.Output ?? string.Empty;
    }

    private static async Task<string> RunScriptUppercaseAsync(string prompt)
    {
        await using var provider = ClaimIntegrationTestKit.BuildProvider();
        var definitionPort = provider.GetRequiredService<IScriptDefinitionCommandPort>();
        var provisioningPort = provider.GetRequiredService<IScriptRuntimeProvisioningPort>();
        var commandPort = provider.GetRequiredService<IScriptRuntimeCommandPort>();
        var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
        var projectionPort = provider.GetRequiredService<IScriptExecutionProjectionPort>();

        const string definitionActorId = "yaml-script-parity-definition";
        const string runtimeActorId = "yaml-script-parity-runtime";
        const string revision = "rev-1";

        var definition = await definitionPort.UpsertDefinitionWithSnapshotAsync(
            scriptId: "yaml-script-parity",
            scriptRevision: revision,
            sourceText: TextNormalizationProtocolSampleActors.Source,
            sourceHash: TextNormalizationProtocolSampleActors.SourceHash,
            definitionActorId: definitionActorId,
            ct: CancellationToken.None);
        await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, definition.Snapshot, CancellationToken.None);
        var lease = await projectionPort.EnsureActorProjectionAsync(runtimeActorId, CancellationToken.None);
        lease.Should().NotBeNull();
        await using var sink = new EventChannel<EventEnvelope>(capacity: 16);
        await projectionPort.AttachLiveSinkAsync(lease!, sink, CancellationToken.None);

        try
        {
            await commandPort.RunRuntimeAsync(
                runtimeActorId,
                "run-parity",
                Any.Pack(new TextNormalizationRequested
                {
                    CommandId = "run-parity",
                    InputText = prompt,
                }),
                revision,
                definitionActorId,
                TextNormalizationRequested.Descriptor.FullName,
                CancellationToken.None);
            await ScriptRunCommittedObservationTestHelper.WaitForCommittedAsync(
                sink,
                "run-parity",
                CancellationToken.None);

            var snapshot = await queryService.GetSnapshotAsync(runtimeActorId, CancellationToken.None);
            snapshot.Should().NotBeNull();
            return snapshot!.ReadModelPayload!.Unpack<TextNormalizationReadModel>().NormalizedText;
        }
        finally
        {
            await projectionPort.DetachLiveSinkAsync(lease!, sink, CancellationToken.None);
            await projectionPort.ReleaseActorProjectionAsync(lease!, CancellationToken.None);
        }
    }

    private static string BuildParityWorkflowYaml() =>
        """
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
}
