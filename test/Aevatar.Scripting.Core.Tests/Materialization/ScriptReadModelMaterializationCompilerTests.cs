using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Materialization;

public sealed class ScriptReadModelMaterializationCompilerTests
{
    [Fact]
    public async Task GetOrCompile_ShouldBuildDocumentAndGraphPlans_ForStructuredProfileBehavior()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-profile",
            "rev-1",
            ScriptSources.StructuredProfileBehavior));

        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        var plan = new ScriptReadModelMaterializationCompiler().GetOrCompile(
            artifact,
            schemaHash: "abc123schema",
            schemaVersion: "3");

        plan.SchemaId.Should().Be("script_profile");
        plan.SchemaVersion.Should().Be("3");
        plan.SchemaHash.Should().Be("abc123schema");
        plan.SupportsDocument.Should().BeTrue();
        plan.SupportsGraph.Should().BeTrue();
        plan.DocumentIndexScope.Should().Be("script-native-script-profile-abc123schema");
        plan.DocumentFields.Should().Contain(x => x.Path == "actor_id");
        plan.DocumentFields.Should().Contain(x => x.Path == "tags[]");
        plan.GraphRelations.Should().ContainSingle(x => x.SourcePath == "refs.policy_id");

        var properties = plan.DocumentMetadata.Mappings.Should().ContainKey("properties").WhoseValue
            .Should().BeOfType<Dictionary<string, object?>>().Subject;
        var fields = properties.Should().ContainKey("fields").WhoseValue
            .Should().BeOfType<Dictionary<string, object?>>().Subject;
        fields.Should().ContainKey("properties");
    }

    [Fact]
    public async Task GetOrCompile_ShouldRejectDocumentIndex_WhenPathIsNotDeclaredByDescriptorSchema()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-profile",
            "rev-invalid",
            CreateInvalidIndexedPathPackage()));

        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        var act = () => new ScriptReadModelMaterializationCompiler().GetOrCompile(
            artifact,
            schemaHash: "invalid",
            schemaVersion: "1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*references path `search.lookup`*");
    }

    private static ScriptSourcePackage CreateInvalidIndexedPathPackage()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Aevatar.Scripting.Core.Tests.InvalidMessages;

            public sealed class InvalidIndexedPathBehavior : ScriptBehavior<InvalidProfileState, InvalidProfileReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<InvalidProfileState, InvalidProfileReadModel> builder)
                {
                    builder
                        .OnCommand<InvalidProfileCommand>(HandleAsync)
                        .OnEvent<InvalidProfileUpdated>(
                            apply: static (_, evt, _) => evt.Current == null
                                ? new InvalidProfileState()
                                : new InvalidProfileState { LastCommandId = evt.CommandId ?? string.Empty },
                            reduce: static (_, evt, _) => evt.Current)
                        .OnQuery<InvalidProfileQueryRequested, InvalidProfileQueryResponded>(HandleQueryAsync);
                }

                private static Task HandleAsync(
                    InvalidProfileCommand command,
                    ScriptCommandContext<InvalidProfileState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = new InvalidProfileReadModel
                    {
                        LastCommandId = command.CommandId ?? string.Empty,
                        Search = new InvalidProfileSearch
                        {
                            LookupKey = command.CommandId ?? string.Empty,
                        },
                    };
                    context.Emit(new InvalidProfileUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = current,
                    });
                    return Task.CompletedTask;
                }

                private static Task<InvalidProfileQueryResponded?> HandleQueryAsync(
                    InvalidProfileQueryRequested query,
                    ScriptQueryContext<InvalidProfileReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<InvalidProfileQueryResponded?>(new InvalidProfileQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new InvalidProfileReadModel(),
                    });
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package aevatar.scripting.tests.invalid;

            option csharp_namespace = "Aevatar.Scripting.Core.Tests.InvalidMessages";

            import "scripting_schema_options.proto";

            message InvalidProfileState {
              string last_command_id = 1;
            }

            message InvalidProfileSearch {
              string lookup_key = 1;
            }

            message InvalidProfileReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "invalid_profile"
                schema_version: "1"
                store_kinds: "document"
                document_indexes: {
                  name: "idx_bad_path"
                  paths: "search.lookup"
                  provider: "document"
                }
              };
              string last_command_id = 1 [(aevatar.scripting.schema.scripting_field) = { storage_type: "keyword" }];
              InvalidProfileSearch search = 2;
            }

            message InvalidProfileCommand {
              string command_id = 1;
            }

            message InvalidProfileUpdated {
              string command_id = 1;
              InvalidProfileReadModel current = 2;
            }

            message InvalidProfileQueryRequested {
              string request_id = 1;
            }

            message InvalidProfileQueryResponded {
              string request_id = 1;
              InvalidProfileReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("invalid_profile.proto", protoSource)],
            "InvalidIndexedPathBehavior");
    }
}
