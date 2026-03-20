using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class WorkflowBoundaryTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void WorkflowProjection_ShouldNot_DependOn_AIProjection()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Projection(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.AI\.Projection(\..+)?"))
            .Because("Workflow.Projection must not depend on AI.Projection");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowInfrastructure_ShouldNot_DependOn_ProjectionProviders()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Infrastructure(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Providers(\..+)?"))
            .Because("Workflow.Infrastructure must not reference specific projection providers")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowAbstractions_ShouldNot_DependOn_WorkflowCore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Abstractions(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Workflow\.Core(\..+)?"))
            .Because("Workflow.Abstractions must not depend on Workflow.Core");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingAbstractions_ShouldNot_DependOn_ScriptingCore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Abstractions(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Scripting\.Core(\..+)?"))
            .Because("Scripting.Abstractions must not depend on Scripting.Core");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowRunActorPort_ShouldBe_WriteOnly()
    {
        // IWorkflowRunActorPort must remain write-only.
        // Read-side binding inspection belongs to IWorkflowActorBindingReader.
        IArchRule rule = MethodMembers().That()
            .HaveNameMatching("^(GetAsync|DescribeAsync|IsWorkflowDefinitionActorAsync|IsWorkflowRunActorAsync|GetBoundWorkflowNameAsync)$")
            .And().AreDeclaredIn(
                Types().That().HaveNameContaining("IWorkflowRunActorPort"))
            .Should().NotExist()
            .Because("IWorkflowRunActorPort must be write-only; read binding belongs in IWorkflowActorBindingReader")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowEndpoints_ShouldNot_DependOn_IActorRuntime()
    {
        // Workflow capability endpoints must not bypass CQRS command dispatch
        // with direct runtime/dispatch usage.
        IArchRule rule = Types().That()
            .HaveNameContaining("Endpoint")
            .And().ResideInNamespaceMatching(@"Aevatar\.Workflow\.Infrastructure\.CapabilityApi(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameMatching("^(IActorRuntime|IActorDispatchPort)$"))
            .Because("workflow endpoints must use ICommandDispatchService, not direct actor runtime")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowCore_ShouldNot_DependOn_WorkflowInfrastructure()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Workflow\.Infrastructure(\..+)?"))
            .Because("Workflow.Core must not depend on Workflow.Infrastructure")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingCore_ShouldNot_DependOn_ScriptingInfrastructure()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Scripting\.Infrastructure(\..+)?"))
            .Because("Scripting.Core must not depend on Scripting.Infrastructure")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowApplication_ShouldNot_DependOn_WorkflowInfrastructure()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Application(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Workflow\.Infrastructure(\..+)?"))
            .Because("Workflow.Application must not depend on Workflow.Infrastructure")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingApplication_ShouldNot_DependOn_ScriptingInfrastructure()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Application(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Scripting\.Infrastructure(\..+)?"))
            .Because("Scripting.Application must not depend on Scripting.Infrastructure")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowProjection_ShouldNot_DependOn_WorkflowInfrastructure()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Projection(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Workflow\.Infrastructure(\..+)?"))
            .Because("Workflow.Projection must not depend on Workflow.Infrastructure")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingProjection_ShouldNot_DependOn_ScriptingInfrastructure()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Projection(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Scripting\.Infrastructure(\..+)?"))
            .Because("Scripting.Projection must not depend on Scripting.Infrastructure")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }
}
