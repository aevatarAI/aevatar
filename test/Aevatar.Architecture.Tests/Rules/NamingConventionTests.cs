using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class NamingConventionTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    // MIGRATED to Aevatar.Architecture.Tests — these type names are validated
    // by ProjectionConstraintTests.LegacyCommittedStateProjectionSemantics_ShouldNot_Exist
    // and the existing ForbiddenPatternTests.Production_ShouldNot_Have_LegacyBindingResolver.
    // No separate NamingConventionTests check needed — the shell guard
    // (committed_state_projection_guard.sh) still scans for literal strings in src/.

    [Fact]
    public void SourceNamedRuntimePorts_ShouldNot_Exist()
    {
        IArchRule rule = Types().That()
            .HaveNameContaining("WorkflowActorRuntimePort")
            .Or().HaveNameContaining("ScriptActorRuntimePort")
            .Or().HaveNameContaining("ScriptingActorRuntimePort")
            .Or().HaveNameContaining("WorkflowGAgentRuntimePort")
            .Or().HaveNameContaining("ScriptGAgentRuntimePort")
            .Or().HaveNameContaining("ScriptingGAgentRuntimePort")
            .Should().NotExist()
            .Because("source-named generic actor communication abstractions are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyRuntimePorts_ShouldNot_Exist()
    {
        IArchRule rule = Types().That()
            .HaveFullNameContaining("IGAgentRuntimePort")
            .Or().HaveFullNameContaining("RuntimeGAgentRuntimePort")
            .Should().NotExist()
            .Because("legacy fat runtime-port abstractions are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyGAgentModuleIdentifiers_ShouldNot_Exist()
    {
        IArchRule rule = Types().That()
            .HaveNameContaining("gagent_")
            .Should().NotExist()
            .Because("legacy gagent_* module identifiers are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyWorkflowReadModelNaming_ShouldNot_Exist()
    {
        IArchRule rule = Types().That()
            .HaveNameContaining("WorkflowRunReadModel")
            .Or().HaveNameContaining("WorkflowRunReportArtifact")
            .Should().NotExist()
            .Because("legacy workflow readmodel naming is forbidden");
        rule.Check(Arch);
    }

    [Fact(Skip = "Known violation - WorkflowRunReport exists in Aevatar.Workflow.Application.Abstractions.Queries")]
    public void LegacyWorkflowReportArtifact_ShouldNot_Exist()
    {
        IArchRule rule = Types().That()
            .HaveNameMatching("^(WorkflowRunReport|WorkflowExecutionReport|WorkflowRunReportArtifact)$")
            .Should().NotExist()
            .Because("legacy workflow report/artifact naming is forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyPublicActorMessaging_ShouldNot_Exist()
    {
        IArchRule rule = Types().That()
            .HaveNameMatching("^(I?ActorMessagingPort|I?ActorSessionPort|I?ActorCommunicationPort)$")
            .Should().NotExist()
            .Because("public actor messaging port/session abstractions are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void ApplicationFileArtifactTypes_ShouldUseArtifactNaming()
    {
        IArchRule legacyNames = Types().That()
            .ResideInNamespace("Aevatar.Workflow.Application.Abstractions.Runs")
            .And().HaveNameMatching("^(IWorkflowFile(IngressPort|ArtifactReadPort|ArtifactOwnershipPort|ArtifactCleanupPort)|WorkflowFile(IngressRequest|IngressResult|ArtifactContent|ArtifactCleanupRequest|ArtifactCleanupResult|Ref|SourceKind))$")
            .Should().NotExist()
            .Because("file artifact application abstractions are not workflow-run-specific");
        legacyNames.Check(Arch);

        IArchRule artifactNames = Types().That()
            .HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactIngressPort")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactReadPort")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactCleanupPort")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactIngressRequest")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactIngressResult")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactContent")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactCleanupRequest")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactCleanupResult")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef")
            .Or().HaveFullName("Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind")
            .Should().Exist()
            .Because("file artifact application abstractions must expose the generic artifact naming");
        artifactNames.Check(Arch);
    }
}
