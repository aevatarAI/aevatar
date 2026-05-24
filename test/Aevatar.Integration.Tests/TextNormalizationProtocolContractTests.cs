using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Integration.Tests.TestDoubles.Protocols;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class TextNormalizationProtocolContractTests
{
    [Fact]
    public async Task ScriptBehavior_ShouldHonorTypedProtocolContract()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        await using var provider = services.BuildServiceProvider();
        var definitionPort = provider.GetRequiredService<IScriptDefinitionCommandPort>();
        var provisioningPort = provider.GetRequiredService<IScriptRuntimeProvisioningPort>();
        var commandPort = provider.GetRequiredService<IScriptRuntimeCommandPort>();
        var queryService = provider.GetRequiredService<Aevatar.Scripting.Abstractions.Queries.IScriptReadModelQueryPort>();
        var projectionPort = provider.GetRequiredService<Aevatar.Scripting.Abstractions.Queries.IScriptExecutionProjectionPort>();

        const string definitionActorId = "text-normalization-definition";
        const string runtimeActorId = "text-normalization-runtime";

        var definition = await definitionPort.UpsertDefinitionWithSnapshotAsync(
            scriptId: "text-normalization",
            scriptRevision: "rev-1",
            scriptPackage: ScriptPackageSpecExtensions.CreateSingleSource(TextNormalizationProtocolSampleActors.Source),
            definitionActorId: definitionActorId,
            ct: CancellationToken.None);
        await provisioningPort.EnsureRuntimeAsync(definitionActorId, "rev-1", runtimeActorId, definition.Snapshot, CancellationToken.None);
        var lease = await provider.EnsureScriptExecutionProjectionAsync(runtimeActorId, CancellationToken.None);
        lease.Should().NotBeNull();
        await using var sink = new EventChannel<Aevatar.Foundation.Abstractions.EventEnvelope>(capacity: 32);
        var liveSinkLease = await projectionPort.AttachLiveSinkAsync(lease!, sink, CancellationToken.None);

        try
        {
            await commandPort.RunRuntimeAsync(
                runtimeActorId,
                "run-1",
                Any.Pack(new TextNormalizationRequested
                {
                    CommandId = "command-1",
                    InputText = "  hello  ",
                }),
                "rev-1",
                definitionActorId,
                "protocol.requested",
                CancellationToken.None);
            var committed = await ScriptRunCommittedObservationTestHelper.WaitForCommittedAsync(
                sink,
                "run-1",
                CancellationToken.None);

            var snapshot = await ScriptReadModelVisibilityTestHelper.WaitForSnapshotAsync(
                token => queryService.GetSnapshotAsync(runtimeActorId, token),
                committed.StateVersion,
                CancellationToken.None);

            snapshot!.ReadModelPayload.Should().NotBeNull();
            var readModel = snapshot.ReadModelPayload!.Unpack<TextNormalizationReadModel>();
            readModel.HasValue.Should().BeTrue();
            readModel.InputText.Should().Be("  hello  ");
            readModel.NormalizedText.Should().Be("HELLO");
            readModel.LastCommandId.Should().Be("command-1");
            readModel.Lookup.Normalized.Should().Be("HELLO");
            readModel.Refs.ProfileId.Should().Be("command-1");
        }
        finally
        {
            await projectionPort.DetachLiveSinkAsync(liveSinkLease, CancellationToken.None);
            await projectionPort.ReleaseActorProjectionAsync(lease!, CancellationToken.None);
        }
    }
}
