using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Projection.Runtime;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests.Runtime;

public sealed class ProjectionRuntimeFleetCapabilityAdmissionReaderTests
{
    [Fact]
    public async Task AddRuntimeFleetCapabilityProjection_WithoutDocumentReader_ShouldReturnNoAdmission()
    {
        var services = new ServiceCollection();
        services.AddRuntimeFleetCapabilityProjection();
        using var provider = services.BuildServiceProvider();

        var reader = provider.GetRequiredService<IRuntimeFleetCapabilityAdmissionReader>();

        reader.Should().BeOfType<ProjectionRuntimeFleetCapabilityAdmissionReader>();
        (await reader.GetAsync(RuntimeFleetCapability.WorkflowNormalizedStateWritesV1))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WithMultipleDocumentReaders_ShouldFailClosedWithoutChoosingOne()
    {
        var first = new RecordingDocumentReader();
        var second = new RecordingDocumentReader();
        var reader = new ProjectionRuntimeFleetCapabilityAdmissionReader(
            new IProjectionDocumentReader<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>[]
            {
                first,
                second,
            });

        var admission = await reader.GetAsync(
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1);

        admission.Should().BeNull();
        first.ReadCount.Should().Be(0);
        second.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_WithSingleDocumentReader_ShouldReadAuthorityDocument()
    {
        var documentReader = new RecordingDocumentReader();
        var reader = new ProjectionRuntimeFleetCapabilityAdmissionReader([documentReader]);

        var admission = await reader.GetAsync(
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1);

        admission.Should().BeNull();
        documentReader.ReadCount.Should().Be(1);
        documentReader.LastKey.Should().Be(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
    }

    private sealed class RecordingDocumentReader
        : IProjectionDocumentReader<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>
    {
        public int ReadCount { get; private set; }

        public string? LastKey { get; private set; }

        public Task<RuntimeFleetCapabilityAuthorityCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            LastKey = key;
            return Task.FromResult<RuntimeFleetCapabilityAuthorityCurrentStateDocument?>(null);
        }

        public Task<ProjectionDocumentQueryResult<RuntimeFleetCapabilityAuthorityCurrentStateDocument>>
            QueryAsync(
                ProjectionDocumentQuery query,
                CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
