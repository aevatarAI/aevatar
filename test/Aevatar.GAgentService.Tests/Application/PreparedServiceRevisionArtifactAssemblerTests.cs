using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class PreparedServiceRevisionArtifactAssemblerTests
{
    [Fact]
    public void Assemble_ShouldPopulateHash()
    {
        var assembler = new PreparedServiceRevisionArtifactAssembler();
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = "tenant",
                AppId = "app",
                Namespace = "default",
                ServiceId = "svc",
            },
            RevisionId = "r1",
            ImplementationKind = ServiceImplementationKind.Static,
            Endpoints =
            {
                new ServiceEndpointDescriptor
                {
                    EndpointId = "run",
                    DisplayName = "run",
                    Kind = ServiceEndpointKind.Command,
                    RequestTypeUrl = "type.googleapis.com/test.command",
                },
            },
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    ActorTypeName = typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
                },
            },
        };

        var assembled = assembler.Assemble(artifact);

        assembled.ArtifactHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Assemble_ShouldRejectNullArtifact()
    {
        var assembler = new PreparedServiceRevisionArtifactAssembler();

        var act = () => assembler.Assemble(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Assemble_ShouldRejectMissingIdentity()
    {
        var assembler = new PreparedServiceRevisionArtifactAssembler();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            GAgentServiceTestKit.CreateIdentity(),
            "r1");
        artifact.Identity = null;

        var act = () => assembler.Assemble(artifact);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*identity is required*");
    }

    [Fact]
    public void Assemble_ShouldRejectMissingRevisionId()
    {
        var assembler = new PreparedServiceRevisionArtifactAssembler();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            GAgentServiceTestKit.CreateIdentity(),
            "r1");
        artifact.RevisionId = string.Empty;

        var act = () => assembler.Assemble(artifact);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*revision_id is required*");
    }

    [Fact]
    public void Assemble_ShouldRejectMissingImplementationKind()
    {
        var assembler = new PreparedServiceRevisionArtifactAssembler();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            GAgentServiceTestKit.CreateIdentity(),
            "r1");
        artifact.ImplementationKind = ServiceImplementationKind.Unspecified;

        var act = () => assembler.Assemble(artifact);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*implementation_kind is required*");
    }

    [Fact]
    public void Assemble_ShouldRejectMissingEndpoints()
    {
        var assembler = new PreparedServiceRevisionArtifactAssembler();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            GAgentServiceTestKit.CreateIdentity(),
            "r1");
        artifact.Endpoints.Clear();

        var act = () => assembler.Assemble(artifact);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least one endpoint*");
    }

    [Fact]
    public void Assemble_ShouldRejectMissingDeploymentPlan()
    {
        var assembler = new PreparedServiceRevisionArtifactAssembler();
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            GAgentServiceTestKit.CreateIdentity(),
            "r1");
        artifact.DeploymentPlan = new ServiceDeploymentPlan();

        var act = () => assembler.Assemble(artifact);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*deployment plan is required*");
    }
}
