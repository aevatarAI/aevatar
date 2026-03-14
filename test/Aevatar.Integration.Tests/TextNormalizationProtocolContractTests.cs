using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Integration.Tests.TestDoubles.Protocols;
using Aevatar.Scripting.Application.Queries;
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
        var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
        var projectionPort = provider.GetRequiredService<Aevatar.Scripting.Abstractions.Queries.IScriptExecutionProjectionPort>();

        const string definitionActorId = "text-normalization-definition";
        const string runtimeActorId = "text-normalization-runtime";

        await definitionPort.UpsertDefinitionAsync(
            scriptId: "text-normalization",
            scriptRevision: "rev-1",
            sourceText: TextNormalizationProtocolSampleActors.Source,
            sourceHash: TextNormalizationProtocolSampleActors.SourceHash,
            definitionActorId: definitionActorId,
            ct: CancellationToken.None);
        await provisioningPort.EnsureRuntimeAsync(definitionActorId, "rev-1", runtimeActorId, CancellationToken.None);
        var lease = await projectionPort.EnsureActorProjectionAsync(runtimeActorId, CancellationToken.None);
        lease.Should().NotBeNull();
        await using var sink = new EventChannel<Aevatar.Foundation.Abstractions.EventEnvelope>(capacity: 32);
        await projectionPort.AttachLiveSinkAsync(lease!, sink, CancellationToken.None);

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
            await ScriptRunCommittedObservationTestHelper.WaitForCommittedAsync(sink, "run-1", CancellationToken.None);

            var result = await queryService.ExecuteDeclaredQueryAsync(
                runtimeActorId,
                Any.Pack(new TextNormalizationQueryRequested
                {
                    RequestId = "request-1",
                    ReplyStreamId = "reply-stream",
                }),
                CancellationToken.None);

            result.Should().NotBeNull();
            result!.Is(TextNormalizationQueryResponded.Descriptor).Should().BeTrue();
            var response = result.Unpack<TextNormalizationQueryResponded>();
            response.RequestId.Should().Be("request-1");
            response.Current.Should().NotBeNull();
            response.Current.HasValue.Should().BeTrue();
            response.Current.InputText.Should().Be("  hello  ");
            response.Current.NormalizedText.Should().Be("HELLO");
            response.Current.LastCommandId.Should().Be("command-1");
            response.Current.Lookup.Normalized.Should().Be("HELLO");
            response.Current.Refs.ProfileId.Should().Be("command-1");
        }
        finally
        {
            await projectionPort.DetachLiveSinkAsync(lease!, sink, CancellationToken.None);
            await projectionPort.ReleaseActorProjectionAsync(lease!, CancellationToken.None);
        }
    }
}
