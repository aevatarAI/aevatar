using Aevatar.CQRS.Projection.Core.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionMaterializerRegistrationTests
{
    [Fact]
    public void AddCurrentStateProjectionMaterializer_ShouldRegisterBaseAndTypedContracts()
    {
        var services = new ServiceCollection();

        services.AddCurrentStateProjectionMaterializer<TestContext, TestCurrentStateMaterializer>();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionMaterializer<TestContext>) &&
            x.ImplementationType == typeof(TestCurrentStateMaterializer));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICurrentStateProjectionMaterializer<TestContext>) &&
            x.ImplementationType == typeof(TestCurrentStateMaterializer));
    }

    [Fact]
    public void AddProjectionArtifactMaterializer_ShouldRegisterBaseAndTypedContracts()
    {
        var services = new ServiceCollection();

        services.AddProjectionArtifactMaterializer<TestContext, TestArtifactMaterializer>();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionMaterializer<TestContext>) &&
            x.ImplementationType == typeof(TestArtifactMaterializer));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<TestContext>) &&
            x.ImplementationType == typeof(TestArtifactMaterializer));
    }

    private sealed class TestContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = "actor-1";

        public string ProjectionKind { get; init; } = "projection";
    }

    private sealed class TestCurrentStateMaterializer : ICurrentStateProjectionMaterializer<TestContext>
    {
        public ValueTask ProjectAsync(TestContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestArtifactMaterializer : IProjectionArtifactMaterializer<TestContext>
    {
        public ValueTask ProjectAsync(TestContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
