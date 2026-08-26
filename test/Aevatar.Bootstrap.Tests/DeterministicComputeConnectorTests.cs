using Aevatar.Bootstrap.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Bootstrap.Tests;

// Implement (issue #3526):
//   Behavior: Verify the built-in versioned algorithm and reject catalog configurations that drift from its descriptor.
//   Why this shape: Behavior-focused tests keep deterministic admission coverage outside the frozen legacy coverage bucket.
public sealed class DeterministicComputeConnectorTests
{
    [Fact]
    public async Task SHA256DeterministicComputeHandler_ShouldMatchGoldenVector_AndExposeVersion()
    {
        var handler = new SHA256DeterministicComputeHandler();
        var descriptor = handler.Algorithms.Should().ContainSingle().Subject;
        descriptor.AlgorithmId.Should().Be(SHA256DeterministicComputeHandler.OperationId);
        descriptor.AlgorithmVersion.Should().Be(1);
        descriptor.InputSchemaDigest.Should().Be(
            "sha256:54669b5e6a1bfebb4d15788d41b5cd5fb8e51fc2d982eb2383a42262c748c90a");
        descriptor.OutputSchemaDigest.Should().Be(
            "sha256:6ece1c260f47c45b60dcac33ad9b45ad37e52e71a09c7f9924478d4c4b347852");

        var connector = new HostCallbackConnector(
            "deterministic-hash",
            handler.Name,
            handler,
            [SHA256DeterministicComputeHandler.OperationId],
            ["text"]);
        var response = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = SHA256DeterministicComputeHandler.OperationId,
            Payload = """{"text":"abc"}""",
        });

        response.Success.Should().BeTrue();
        response.Output.Should().Be(
            """{"sha256":"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"}""");
        response.Metadata["host_callback.algorithm_version"].Should().Be("1");
        response.Metadata["host_callback.result.sha256"].Should().Be(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

        var invalid = await connector.ExecuteAsync(new ConnectorRequest
        {
            Operation = SHA256DeterministicComputeHandler.OperationId,
            Payload = """{"text":42}""",
        });
        invalid.Success.Should().BeFalse();
        invalid.Metadata["host_callback.algorithm_version"].Should().Be("1");
    }

    [Fact]
    public void HostCallbackConnectorBuilder_ShouldFailClosedOnDeterministicDescriptorDrift()
    {
        var deterministicHandler = new SHA256DeterministicComputeHandler();
        var builder = new HostCallbackConnectorBuilder([deterministicHandler]);

        var deterministicWithoutOperations = CreateConfig(deterministicHandler.Name, []);
        builder.TryBuild(
            deterministicWithoutOperations,
            NullLogger.Instance,
            out var deterministicWithoutOperationsConnector).Should().BeFalse();
        deterministicWithoutOperationsConnector.Should().BeNull();

        var deterministicMismatchedOperations = CreateConfig(deterministicHandler.Name, ["different_algorithm"]);
        builder.TryBuild(
            deterministicMismatchedOperations,
            NullLogger.Instance,
            out var deterministicMismatchedConnector).Should().BeFalse();
        deterministicMismatchedConnector.Should().BeNull();

        var deterministicValid = CreateConfig(
            deterministicHandler.Name,
            [SHA256DeterministicComputeHandler.OperationId]);
        builder.TryBuild(
            deterministicValid,
            NullLogger.Instance,
            out var deterministicConnector).Should().BeTrue();
        deterministicConnector.Should().NotBeNull();
    }

    private static ConnectorConfigEntry CreateConfig(string handler, IReadOnlyList<string> allowedOperations)
    {
        return new ConnectorConfigEntry
        {
            Name = "deterministic-hash",
            Type = "host_callback",
            HostCallback = new HostCallbackConnectorConfig
            {
                Handler = handler,
                AllowedOperations = [.. allowedOperations],
                AllowedInputKeys = ["text"],
            },
        };
    }
}
