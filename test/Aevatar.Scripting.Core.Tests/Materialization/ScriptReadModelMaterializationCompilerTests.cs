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

    [Fact]
    public void Compile_ShouldRejectWrapperField_WhenReadModelUsesProtobufWrapperLeaf()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-profile",
            "rev-wrapper",
            CreateWrapperReadModelPackage()));

        compilation.IsSuccess.Should().BeFalse();
        compilation.Diagnostics.Should().ContainSingle(x =>
            x.Contains("must not reference protobuf wrapper leaf types", StringComparison.Ordinal) &&
            x.Contains("wrapper_profile.proto", StringComparison.Ordinal));
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
            import "scripting_runtime_options.proto";

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
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
              };
              string command_id = 1;
            }

            message InvalidProfileUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "aevatar.scripting.tests.invalid.InvalidProfileReadModel"
              };
              string command_id = 1;
              InvalidProfileReadModel current = 2;
            }

            message InvalidProfileQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "aevatar.scripting.tests.invalid.InvalidProfileReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "aevatar.scripting.tests.invalid.InvalidProfileQueryResponded"
              };
              string request_id = 1;
            }

            message InvalidProfileQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "aevatar.scripting.tests.invalid.InvalidProfileReadModel"
              };
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

    private static ScriptSourcePackage CreateWrapperReadModelPackage()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Aevatar.Scripting.Core.Tests.WrapperMessages;
            using Google.Protobuf.WellKnownTypes;

            public sealed class WrapperReadModelBehavior : ScriptBehavior<WrapperProfileState, WrapperProfileReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<WrapperProfileState, WrapperProfileReadModel> builder)
                {
                    builder
                        .OnCommand<WrapperProfileCommand>(HandleAsync)
                        .OnEvent<WrapperProfileUpdated>(
                            apply: static (_, evt, _) => evt.Current == null
                                ? new WrapperProfileState()
                                : new WrapperProfileState { LastCommandId = evt.CommandId ?? string.Empty },
                            reduce: static (_, evt, _) => evt.Current)
                        .OnQuery<WrapperProfileQueryRequested, WrapperProfileQueryResponded>(HandleQueryAsync);
                }

                private static Task HandleAsync(
                    WrapperProfileCommand command,
                    ScriptCommandContext<WrapperProfileState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = new WrapperProfileReadModel
                    {
                        ExternalKey = new StringValue { Value = command.CommandId ?? string.Empty },
                    };
                    context.Emit(new WrapperProfileUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = current,
                    });
                    return Task.CompletedTask;
                }

                private static Task<WrapperProfileQueryResponded?> HandleQueryAsync(
                    WrapperProfileQueryRequested query,
                    ScriptQueryContext<WrapperProfileReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<WrapperProfileQueryResponded?>(new WrapperProfileQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new WrapperProfileReadModel(),
                    });
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package aevatar.scripting.tests.wrapper;

            option csharp_namespace = "Aevatar.Scripting.Core.Tests.WrapperMessages";

            import "google/protobuf/wrappers.proto";
            import "scripting_schema_options.proto";
            import "scripting_runtime_options.proto";

            message WrapperProfileState {
              string last_command_id = 1;
            }

            message WrapperProfileReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "wrapper_profile"
                schema_version: "1"
                store_kinds: "document"
              };
              google.protobuf.StringValue external_key = 1;
            }

            message WrapperProfileCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
              };
              string command_id = 1;
            }

            message WrapperProfileUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "aevatar.scripting.tests.wrapper.WrapperProfileReadModel"
              };
              string command_id = 1;
              WrapperProfileReadModel current = 2;
            }

            message WrapperProfileQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "aevatar.scripting.tests.wrapper.WrapperProfileReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "aevatar.scripting.tests.wrapper.WrapperProfileQueryResponded"
              };
              string request_id = 1;
            }

            message WrapperProfileQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "aevatar.scripting.tests.wrapper.WrapperProfileReadModel"
              };
              string request_id = 1;
              WrapperProfileReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("wrapper_profile.proto", protoSource)],
            "WrapperReadModelBehavior");
    }
}
