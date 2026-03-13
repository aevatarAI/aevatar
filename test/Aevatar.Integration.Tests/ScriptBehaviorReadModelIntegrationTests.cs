using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class ScriptBehaviorReadModelIntegrationTests
{
    [Fact]
    public async Task ProvisionRunAndQuery_ShouldProduceProjectedReadModel()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        await using var provider = services.BuildServiceProvider();
        var definitionPort = provider.GetRequiredService<IScriptDefinitionCommandPort>();
        var provisioningPort = provider.GetRequiredService<IScriptRuntimeProvisioningPort>();
        var commandPort = provider.GetRequiredService<IScriptRuntimeCommandPort>();
        var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
        var projectionPort = provider.GetRequiredService<IScriptExecutionProjectionPort>();

        const string definitionActorId = "integration-script-definition";
        const string runtimeActorId = "integration-script-runtime";
        const string revision = "rev-1";
        const string runId = "run-1";

        var resolvedDefinitionActorId = await definitionPort.UpsertDefinitionAsync(
            scriptId: "integration-script",
            scriptRevision: revision,
            sourceText: ScriptingCommandEnvelopeTestKit.UppercaseBehaviorSource,
            sourceHash: ScriptingCommandEnvelopeTestKit.UppercaseBehaviorHash,
            definitionActorId: definitionActorId,
            ct: CancellationToken.None);
        resolvedDefinitionActorId.Should().Be(definitionActorId);

        var resolvedRuntimeActorId = await provisioningPort.EnsureRuntimeAsync(
            definitionActorId,
            revision,
            runtimeActorId,
            CancellationToken.None);
        resolvedRuntimeActorId.Should().Be(runtimeActorId);

        var lease = await projectionPort.EnsureActorProjectionAsync(runtimeActorId, CancellationToken.None);
        lease.Should().NotBeNull();
        await using var sink = new EventChannel<EventEnvelope>(capacity: 32);
        await projectionPort.AttachLiveSinkAsync(lease!, sink, CancellationToken.None);

        try
        {
            await commandPort.RunRuntimeAsync(
                runtimeActorId,
                runId,
                Any.Pack(new TextNormalizationRequested
                {
                    CommandId = "command-1",
                    InputText = "  hello ",
                }),
                revision,
                definitionActorId,
                requestedEventType: "integration.requested",
                ct: CancellationToken.None);

            var committed = await WaitForCommittedFactAsync(sink, runId, CancellationToken.None);
            committed.ActorId.Should().Be(runtimeActorId);
            committed.DomainEventPayload.Should().NotBeNull();
            committed.DomainEventPayload.Unpack<TextNormalizationCompleted>().Current.NormalizedText.Should().Be("HELLO");

            var snapshot = await queryService.GetSnapshotAsync(runtimeActorId, CancellationToken.None);
            snapshot.Should().NotBeNull();
            snapshot!.ReadModelPayload.Should().NotBeNull();
            snapshot.ReadModelPayload.Unpack<TextNormalizationReadModel>().NormalizedText.Should().Be("HELLO");

            var listed = await queryService.ListSnapshotsAsync(10, CancellationToken.None);
            listed.Should().Contain(x => x.ActorId == runtimeActorId);

            var queryResult = await queryService.ExecuteDeclaredQueryAsync(
                runtimeActorId,
                Any.Pack(new TextNormalizationQueryRequested
                {
                    RequestId = "request-1",
                    ReplyStreamId = "reply-stream",
                }),
                CancellationToken.None);
            queryResult.Should().NotBeNull();
            queryResult!.Unpack<TextNormalizationQueryResponded>().Current.NormalizedText.Should().Be("HELLO");
        }
        finally
        {
            await projectionPort.DetachLiveSinkAsync(lease!, sink, CancellationToken.None);
            await projectionPort.ReleaseActorProjectionAsync(lease!, CancellationToken.None);
        }
    }

    private static async Task<ScriptDomainFactCommitted> WaitForCommittedFactAsync(
        IEventSink<EventEnvelope> sink,
        string runId,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await foreach (var envelope in sink.ReadAllAsync(timeout.Token))
        {
            if (envelope.Payload?.Is(ScriptDomainFactCommitted.Descriptor) != true)
                continue;

            var fact = envelope.Payload.Unpack<ScriptDomainFactCommitted>();
            if (string.Equals(fact.RunId, runId, StringComparison.Ordinal))
                return fact;
        }

        throw new InvalidOperationException($"Timed out waiting for committed script fact. run_id={runId}");
    }
}
