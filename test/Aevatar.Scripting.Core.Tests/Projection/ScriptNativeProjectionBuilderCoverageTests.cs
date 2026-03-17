using System.Collections;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptNativeProjectionBuilderCoverageTests
{
    [Fact]
    public void BuildDocument_ShouldReturnNull_WhenPlanDoesNotSupportDocument_OrReadModelIsMissing()
    {
        var builder = new ScriptNativeProjectionBuilder();
        var graphOnlyPlan = BuildClaimPlan() with
        {
            DocumentFields = [],
        };

        builder.BuildDocument(null, graphOnlyPlan).Should().BeNull();
        builder.BuildDocument(new ClaimReadModel(), graphOnlyPlan).Should().BeNull();
    }

    [Fact]
    public void BuildDocument_ShouldAssignNestedAndRepeatedFields_AndIgnoreInvalidTargetSegments()
    {
        var builder = new ScriptNativeProjectionBuilder();
        var readModel = new ScriptProfileReadModel
        {
            HasValue = true,
            ActorId = "actor-1",
            PolicyId = "policy-1",
            Search = new ScriptProfileSearchIndex
            {
                LookupKey = "actor-1:policy-1",
                SortKey = "HELLO",
            },
        };
        readModel.Tags.Add("gold");
        readModel.Tags.Add("vip");

        var compiledPlan = BuildProfilePlan();
        var actorField = compiledPlan.DocumentFields.Single(static x => x.Path == "actor_id");
        var lookupField = compiledPlan.DocumentFields.Single(static x => x.Path == "search.lookup_key");
        var tagsField = compiledPlan.DocumentFields.Single(static x => x.Path == "tags[]");
        var plan = compiledPlan with
        {
            SchemaId = "profile schema",
            SchemaHash = "hash-document",
            DocumentIndexScope = "script-native-profile",
            DocumentFields =
            [
                actorField with { Path = "actor.id" },
                lookupField,
                tagsField,
                actorField with { Name = "ignored", Path = "[]" },
            ],
            GraphRelations = [],
        };

        var projection = builder.BuildDocument(readModel, plan);

        projection.Should().NotBeNull();
        projection!.SchemaId.Should().Be("profile schema");
        projection.DocumentIndexScope.Should().Be("script-native-profile");
        projection.FieldsValue.Fields.Should().ContainKey("actor");
        projection.FieldsValue.Fields["actor"].StructValue.Fields["id"].StringValue.Should().Be("actor-1");
        projection.FieldsValue.Fields["search"].StructValue.Fields["lookup_key"].StringValue.Should().Be("actor-1:policy-1");
        projection.FieldsValue.Fields["tags"].ListValue.Values.Select(static x => x.StringValue)
            .Should().Equal("gold", "vip");
        projection.FieldsValue.Fields.Should().NotContainKey("ignored");
    }

    [Fact]
    public void BuildGraph_ShouldReturnNull_WhenPlanDoesNotSupportGraph_OrReadModelIsMissing()
    {
        var builder = new ScriptNativeProjectionBuilder();
        var documentOnlyPlan = BuildClaimPlan() with
        {
            GraphRelations = [],
        };

        builder.BuildGraph("actor-1", "script-1", "definition-1", "rev-1", null, documentOnlyPlan).Should().BeNull();
        builder.BuildGraph("actor-1", "script-1", "definition-1", "rev-1", new ClaimReadModel(), documentOnlyPlan).Should().BeNull();
    }

    [Fact]
    public void BuildGraph_ShouldNormalizeFallbackTokens_SkipBlankValues_AndDeduplicateTargets()
    {
        var builder = new ScriptNativeProjectionBuilder();
        var readModel = new ClaimReadModel
        {
            HasValue = true,
            CaseId = "Case-42",
            PolicyId = "POLICY-42",
            Refs = new ClaimRefs
            {
                PolicyId = "POLICY-42",
                OwnerActorId = "claim-runtime",
            },
        };
        readModel.TraceSteps.Add("review");
        readModel.TraceSteps.Add(" review ");
        readModel.TraceSteps.Add(" ");
        readModel.TraceSteps.Add("archive");

        var compiledPlan = BuildClaimPlan();
        var policyRelation = compiledPlan.GraphRelations.Single(static x => x.SourcePath == "refs.policy_id");
        var traceRelation = new ScriptGraphRelationMaterialization(
            string.Empty,
            "trace_steps[]",
            string.Empty,
            "step_id",
            "many_to_many",
            compiledPlan.DocumentFields.Single(static x => x.Path == "trace_steps[]").Accessor);
        var plan = compiledPlan with
        {
            SchemaId = "Claim Case!",
            SchemaVersion = "5",
            SchemaHash = "hash-graph",
            DocumentFields = [],
            GraphRelations =
            [
                policyRelation with
                {
                    Name = "rel policy",
                    TargetSchemaId = "policy ref",
                },
                traceRelation,
            ],
        };

        var projection = builder.BuildGraph(
            "claim-runtime",
            "claim-script",
            "definition-1",
            "rev-1",
            readModel,
            plan);

        projection.Should().NotBeNull();
        projection!.GraphScope.Should().Be("script-native-claim_case");
        projection.NodeEntries.Should().Contain(x => x.NodeId == "script:claim_case:claim-runtime");
        projection.NodeEntries.Should().Contain(x => x.NodeId == "ref:policy_ref:POLICY-42");
        projection.NodeEntries.Should().Contain(x => x.NodeId == "ref:external:review");
        projection.NodeEntries.Should().Contain(x => x.NodeId == "ref:external:archive");
        projection.NodeEntries.Should().HaveCount(4);
        projection.EdgeEntries.Should().Contain(x => x.EdgeType == "rel_policy" && x.ToNodeId == "ref:policy_ref:POLICY-42");
        projection.EdgeEntries.Should().Contain(x => x.EdgeType == "related_to" && x.ToNodeId == "ref:external:review");
        projection.EdgeEntries.Should().Contain(x => x.EdgeType == "related_to" && x.ToNodeId == "ref:external:archive");
        projection.EdgeEntries.Should().HaveCount(3);
    }

    private static ScriptReadModelMaterializationPlan BuildProfilePlan()
    {
        var artifactResolver = new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));
        var artifact = artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            "script-1",
            "rev-1",
            ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.StructuredProfileBehavior),
            ScriptSources.StructuredProfileBehaviorHash));
        return new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            "structured-schema",
            "3");
    }

    private static ScriptReadModelMaterializationPlan BuildClaimPlan()
    {
        var artifactResolver = new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));
        var artifact = artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            "claim_orchestrator",
            "rev-claim-1",
            ScriptPackageSpecExtensions.CreateSingleSource(ClaimScriptSources.DecisionBehavior),
            ClaimScriptSources.DecisionBehaviorHash));
        return new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            "claim-schema",
            "3");
    }
}
