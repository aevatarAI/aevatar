using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Materialization;

public sealed class ScriptReadModelMaterializationCompilerTests
{
    [Fact]
    public async Task Compile_ShouldBuildDocumentAndGraphPlans_ForStructuredProfileBehavior()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-profile",
            "rev-1",
            ScriptSources.StructuredProfileBehavior));

        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        var plan = new ScriptReadModelMaterializationCompiler().Compile(
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
    public async Task Compile_ShouldRejectDocumentIndex_WhenPathIsNotDeclaredByDescriptorSchema()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-profile",
            "rev-invalid",
            CreateInvalidIndexedPathPackage()));

        compilation.IsSuccess.Should().BeTrue();
        compilation.Artifact.Should().NotBeNull();

        await using var artifact = compilation.Artifact!;
        var act = () => new ScriptReadModelMaterializationCompiler().Compile(
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

    [Fact]
    public async Task Compile_ShouldReturnEmptyPlan_WhenReadModelHasNoSchemaOptions()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-no-schema",
            "rev-no-schema",
            CreateReadModelWithoutSchemaPackage()));

        compilation.IsSuccess.Should().BeTrue();
        await using var artifact = compilation.Artifact!;

        var plan = new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            schemaHash: string.Empty,
            schemaVersion: string.Empty);

        plan.SchemaId.Should().BeEmpty();
        plan.SchemaVersion.Should().BeEmpty();
        plan.SchemaHash.Should().BeEmpty();
        plan.SupportsDocument.Should().BeFalse();
        plan.SupportsGraph.Should().BeFalse();
        plan.DocumentMetadata.IndexName.Should().Be("script-native-read-models");
    }

    [Fact]
    public async Task Compile_ShouldUseFallbackSchemaHashAndVersion_WhenArgumentsAreBlank()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-provider-filter",
            "rev-provider-filter-fallback",
            CreateProviderFilteredPackage()));

        compilation.IsSuccess.Should().BeTrue();
        await using var artifact = compilation.Artifact!;

        var plan = new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            schemaHash: string.Empty,
            schemaVersion: string.Empty);

        plan.SchemaHash.Should().Be("provider-filter-1");
        plan.SchemaVersion.Should().Be("1");
        plan.DocumentIndexScope.Should().Be("script-native-provider-filter-provider-fil");
    }

    [Fact]
    public async Task Compile_ShouldIgnoreIndexesAndRelations_ForNonNativeProviders()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-provider-filter",
            "rev-provider-filter",
            CreateProviderFilteredPackage()));

        compilation.IsSuccess.Should().BeTrue();
        await using var artifact = compilation.Artifact!;

        var plan = new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            schemaHash: "provider-filter",
            schemaVersion: "1");

        plan.DocumentFields.Should().ContainSingle(x => x.Path == "last_command_id");
        plan.GraphRelations.Should().BeEmpty();
        plan.DocumentMetadata.IndexName.Should().Be("script-native-provider-filter-provider-fil");
    }

    [Fact]
    public async Task Compile_ShouldRejectRelation_WhenNameIsMissing()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-invalid-relation",
            "rev-invalid-relation",
            CreateInvalidRelationPackage("""
                                        source_path: "last_command_id"
                                        target_schema_id: "target"
                                        target_path: "id"
                                        provider: "graph"
                                        """)));

        compilation.IsSuccess.Should().BeTrue();
        await using var artifact = compilation.Artifact!;

        var act = () => new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            schemaHash: "relation-hash",
            schemaVersion: "1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*relation name cannot be empty*");
    }

    [Fact]
    public async Task Compile_ShouldRejectRelation_WhenRequiredFieldsAreMissing()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-invalid-relation-fields",
            "rev-invalid-relation-fields",
            CreateInvalidRelationPackage("""
                                        name: "rel_missing_fields"
                                        provider: "graph"
                                        """)));

        compilation.IsSuccess.Should().BeTrue();
        await using var artifact = compilation.Artifact!;

        var act = () => new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            schemaHash: "relation-fields",
            schemaVersion: "1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Read model path cannot be empty.*");
    }

    [Fact]
    public async Task Compile_ShouldRejectRepeatedNestedMessageTraversal()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-repeated-message",
            "rev-repeated-message",
            CreateRepeatedNestedMessagePackage()));

        compilation.IsSuccess.Should().BeTrue();
        await using var artifact = compilation.Artifact!;

        var act = () => new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            schemaHash: "repeated-message",
            schemaVersion: "1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*repeated message*not supported*");
    }

    [Fact]
    public async Task Compile_ShouldMapAllSupportedLeafStorageTypes()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-field-types",
            "rev-field-types",
            CreateFieldTypesPackage()));

        compilation.IsSuccess.Should().BeTrue();
        await using var artifact = compilation.Artifact!;

        var plan = new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            schemaHash: "field-types",
            schemaVersion: "1");

        var rootProperties = plan.DocumentMetadata.Mappings.Should().ContainKey("properties").WhoseValue
            .Should().BeOfType<Dictionary<string, object?>>().Subject;
        var fields = rootProperties.Should().ContainKey("fields").WhoseValue
            .Should().BeOfType<Dictionary<string, object?>>().Subject;
        var fieldProperties = fields.Should().ContainKey("properties").WhoseValue
            .Should().BeOfType<Dictionary<string, object?>>().Subject;

        GetMappingType(fieldProperties, "keyword_value").Should().Be("keyword");
        GetMappingType(fieldProperties, "keyword_tags").Should().Be("keyword");
        GetMappingType(fieldProperties, "text_value").Should().Be("text");
        GetMappingType(fieldProperties, "flag").Should().Be("boolean");
        GetMappingType(fieldProperties, "int32_value").Should().Be("long");
        GetMappingType(fieldProperties, "int64_value").Should().Be("long");
        GetMappingType(fieldProperties, "long_value").Should().Be("long");
        GetMappingType(fieldProperties, "integer_value").Should().Be("long");
        GetMappingType(fieldProperties, "double_value").Should().Be("double");
        GetMappingType(fieldProperties, "float_value").Should().Be("double");
        GetMappingType(fieldProperties, "number_value").Should().Be("double");
        GetMappingType(fieldProperties, "timestamp_value").Should().Be("date");
        GetMappingType(fieldProperties, "date_value").Should().Be("date");
    }

    [Fact]
    public async Task Compile_ShouldRejectUnsupportedLeafStorageType()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var compilation = compiler.Compile(new ScriptBehaviorCompilationRequest(
            "script-unsupported-type",
            "rev-unsupported-type",
            CreateUnsupportedFieldTypePackage()));

        compilation.IsSuccess.Should().BeTrue();
        await using var artifact = compilation.Artifact!;

        var act = () => new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            schemaHash: "unsupported-type",
            schemaVersion: "1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*field type `uuid` is not supported*");
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
                                : new InvalidProfileState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (_, _) => new InvalidProfileReadModel());
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
                                : new WrapperProfileState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (_, _) => new WrapperProfileReadModel());
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

    private static ScriptSourcePackage CreateReadModelWithoutSchemaPackage()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Aevatar.Scripting.Core.Tests.NoSchemaMessages;

            public sealed class NoSchemaBehavior : ScriptBehavior<NoSchemaState, NoSchemaReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<NoSchemaState, NoSchemaReadModel> builder)
                {
                    builder
                        .OnCommand<NoSchemaCommand>(HandleAsync)
                        .OnEvent<NoSchemaUpdated>(
                            apply: static (_, evt, _) => evt.Current == null ? new NoSchemaState() : new NoSchemaState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (_, _) => new NoSchemaReadModel());
                }

                private static Task HandleAsync(NoSchemaCommand command, ScriptCommandContext<NoSchemaState> context, CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Emit(new NoSchemaUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = new NoSchemaReadModel { LastCommandId = command.CommandId ?? string.Empty },
                    });
                    return Task.CompletedTask;
                }

                private static Task<NoSchemaQueryResponded?> HandleQueryAsync(
                    NoSchemaQueryRequested query,
                    ScriptQueryContext<NoSchemaReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<NoSchemaQueryResponded?>(new NoSchemaQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new NoSchemaReadModel(),
                    });
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package aevatar.scripting.tests.noschema;

            option csharp_namespace = "Aevatar.Scripting.Core.Tests.NoSchemaMessages";

            import "scripting_runtime_options.proto";

            message NoSchemaState {
              string last_command_id = 1;
            }

            message NoSchemaReadModel {
              string last_command_id = 1;
            }

            message NoSchemaCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
              };
              string command_id = 1;
            }

            message NoSchemaUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "aevatar.scripting.tests.noschema.NoSchemaReadModel"
              };
              string command_id = 1;
              NoSchemaReadModel current = 2;
            }

            message NoSchemaQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "aevatar.scripting.tests.noschema.NoSchemaReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "aevatar.scripting.tests.noschema.NoSchemaQueryResponded"
              };
              string request_id = 1;
            }

            message NoSchemaQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "aevatar.scripting.tests.noschema.NoSchemaReadModel"
              };
              string request_id = 1;
              NoSchemaReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("no_schema.proto", protoSource)],
            "NoSchemaBehavior");
    }

    private static ScriptSourcePackage CreateProviderFilteredPackage()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Aevatar.Scripting.Core.Tests.ProviderFilteredMessages;

            public sealed class ProviderFilteredBehavior : ScriptBehavior<ProviderFilteredState, ProviderFilteredReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<ProviderFilteredState, ProviderFilteredReadModel> builder)
                {
                    builder
                        .OnCommand<ProviderFilteredCommand>(HandleAsync)
                        .OnEvent<ProviderFilteredUpdated>(
                            apply: static (_, evt, _) => evt.Current == null ? new ProviderFilteredState() : new ProviderFilteredState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (_, _) => new ProviderFilteredReadModel());
                }

                private static Task HandleAsync(
                    ProviderFilteredCommand command,
                    ScriptCommandContext<ProviderFilteredState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Emit(new ProviderFilteredUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = new ProviderFilteredReadModel { LastCommandId = command.CommandId ?? string.Empty },
                    });
                    return Task.CompletedTask;
                }

                private static Task<ProviderFilteredQueryResponded?> HandleQueryAsync(
                    ProviderFilteredQueryRequested query,
                    ScriptQueryContext<ProviderFilteredReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<ProviderFilteredQueryResponded?>(new ProviderFilteredQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new ProviderFilteredReadModel(),
                    });
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package aevatar.scripting.tests.providerfiltered;

            option csharp_namespace = "Aevatar.Scripting.Core.Tests.ProviderFilteredMessages";

            import "scripting_schema_options.proto";
            import "scripting_runtime_options.proto";

            message ProviderFilteredState {
              string last_command_id = 1;
            }

            message ProviderFilteredReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "provider_filter"
                schema_version: "1"
                store_kinds: "document"
                document_indexes: {
                  name: "idx_search_only"
                  paths: "last_command_id"
                  provider: "search"
                }
                graph_relations: {
                  name: "rel_graph_only"
                  source_path: "last_command_id"
                  target_schema_id: "target"
                  target_path: "id"
                  provider: "edges"
                }
              };
              string last_command_id = 1 [(aevatar.scripting.schema.scripting_field) = { storage_type: "keyword" }];
            }

            message ProviderFilteredCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
              };
              string command_id = 1;
            }

            message ProviderFilteredUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "aevatar.scripting.tests.providerfiltered.ProviderFilteredReadModel"
              };
              string command_id = 1;
              ProviderFilteredReadModel current = 2;
            }

            message ProviderFilteredQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "aevatar.scripting.tests.providerfiltered.ProviderFilteredReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "aevatar.scripting.tests.providerfiltered.ProviderFilteredQueryResponded"
              };
              string request_id = 1;
            }

            message ProviderFilteredQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "aevatar.scripting.tests.providerfiltered.ProviderFilteredReadModel"
              };
              string request_id = 1;
              ProviderFilteredReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("provider_filtered.proto", protoSource)],
            "ProviderFilteredBehavior");
    }

    private static ScriptSourcePackage CreateInvalidRelationPackage(string relationOptions)
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Aevatar.Scripting.Core.Tests.InvalidRelationMessages;

            public sealed class InvalidRelationBehavior : ScriptBehavior<InvalidRelationState, InvalidRelationReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<InvalidRelationState, InvalidRelationReadModel> builder)
                {
                    builder
                        .OnCommand<InvalidRelationCommand>(HandleAsync)
                        .OnEvent<InvalidRelationUpdated>(
                            apply: static (_, evt, _) => evt.Current == null ? new InvalidRelationState() : new InvalidRelationState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (_, _) => new InvalidRelationReadModel());
                }

                private static Task HandleAsync(
                    InvalidRelationCommand command,
                    ScriptCommandContext<InvalidRelationState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Emit(new InvalidRelationUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = new InvalidRelationReadModel { LastCommandId = command.CommandId ?? string.Empty },
                    });
                    return Task.CompletedTask;
                }

                private static Task<InvalidRelationQueryResponded?> HandleQueryAsync(
                    InvalidRelationQueryRequested query,
                    ScriptQueryContext<InvalidRelationReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<InvalidRelationQueryResponded?>(new InvalidRelationQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new InvalidRelationReadModel(),
                    });
                }
            }
            """;

        var protoSource =
            $$"""
            syntax = "proto3";

            package aevatar.scripting.tests.invalidrelation;

            option csharp_namespace = "Aevatar.Scripting.Core.Tests.InvalidRelationMessages";

            import "scripting_schema_options.proto";
            import "scripting_runtime_options.proto";

            message InvalidRelationState {
              string last_command_id = 1;
            }

            message InvalidRelationReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "invalid_relation"
                schema_version: "1"
                store_kinds: "graph"
                graph_relations: {
            {{relationOptions}}
                }
              };
              string last_command_id = 1 [(aevatar.scripting.schema.scripting_field) = { storage_type: "keyword" }];
            }

            message InvalidRelationCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
              };
              string command_id = 1;
            }

            message InvalidRelationUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "aevatar.scripting.tests.invalidrelation.InvalidRelationReadModel"
              };
              string command_id = 1;
              InvalidRelationReadModel current = 2;
            }

            message InvalidRelationQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "aevatar.scripting.tests.invalidrelation.InvalidRelationReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "aevatar.scripting.tests.invalidrelation.InvalidRelationQueryResponded"
              };
              string request_id = 1;
            }

            message InvalidRelationQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "aevatar.scripting.tests.invalidrelation.InvalidRelationReadModel"
              };
              string request_id = 1;
              InvalidRelationReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("invalid_relation.proto", protoSource)],
            "InvalidRelationBehavior");
    }

    private static ScriptSourcePackage CreateRepeatedNestedMessagePackage()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Aevatar.Scripting.Core.Tests.RepeatedNestedMessages;

            public sealed class RepeatedNestedBehavior : ScriptBehavior<RepeatedNestedState, RepeatedNestedReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<RepeatedNestedState, RepeatedNestedReadModel> builder)
                {
                    builder
                        .OnCommand<RepeatedNestedCommand>(HandleAsync)
                        .OnEvent<RepeatedNestedUpdated>(
                            apply: static (_, evt, _) => evt.Current == null ? new RepeatedNestedState() : new RepeatedNestedState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (_, _) => new RepeatedNestedReadModel());
                }

                private static Task HandleAsync(
                    RepeatedNestedCommand command,
                    ScriptCommandContext<RepeatedNestedState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Emit(new RepeatedNestedUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = new RepeatedNestedReadModel
                        {
                            Search = { new RepeatedNestedSearch { LookupKey = command.CommandId ?? string.Empty } },
                        },
                    });
                    return Task.CompletedTask;
                }

                private static Task<RepeatedNestedQueryResponded?> HandleQueryAsync(
                    RepeatedNestedQueryRequested query,
                    ScriptQueryContext<RepeatedNestedReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<RepeatedNestedQueryResponded?>(new RepeatedNestedQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new RepeatedNestedReadModel(),
                    });
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package aevatar.scripting.tests.repeatednested;

            option csharp_namespace = "Aevatar.Scripting.Core.Tests.RepeatedNestedMessages";

            import "scripting_schema_options.proto";
            import "scripting_runtime_options.proto";

            message RepeatedNestedState {
              string last_command_id = 1;
            }

            message RepeatedNestedSearch {
              string lookup_key = 1;
            }

            message RepeatedNestedReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "repeated_nested"
                schema_version: "1"
                store_kinds: "document"
              };
              repeated RepeatedNestedSearch search = 1;
            }

            message RepeatedNestedCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
              };
              string command_id = 1;
            }

            message RepeatedNestedUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "aevatar.scripting.tests.repeatednested.RepeatedNestedReadModel"
              };
              string command_id = 1;
              RepeatedNestedReadModel current = 2;
            }

            message RepeatedNestedQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "aevatar.scripting.tests.repeatednested.RepeatedNestedReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "aevatar.scripting.tests.repeatednested.RepeatedNestedQueryResponded"
              };
              string request_id = 1;
            }

            message RepeatedNestedQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "aevatar.scripting.tests.repeatednested.RepeatedNestedReadModel"
              };
              string request_id = 1;
              RepeatedNestedReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("repeated_nested.proto", protoSource)],
            "RepeatedNestedBehavior");
    }

    private static ScriptSourcePackage CreateFieldTypesPackage()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Aevatar.Scripting.Core.Tests.FieldTypeMessages;
            using Google.Protobuf.WellKnownTypes;

            public sealed class FieldTypesBehavior : ScriptBehavior<FieldTypesState, FieldTypesReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<FieldTypesState, FieldTypesReadModel> builder)
                {
                    builder
                        .OnCommand<FieldTypesCommand>(HandleAsync)
                        .OnEvent<FieldTypesUpdated>(
                            apply: static (_, evt, _) => evt.Current == null ? new FieldTypesState() : new FieldTypesState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (_, _) => new FieldTypesReadModel());
                }

                private static Task HandleAsync(
                    FieldTypesCommand command,
                    ScriptCommandContext<FieldTypesState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Emit(new FieldTypesUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = new FieldTypesReadModel
                        {
                            KeywordValue = command.CommandId ?? string.Empty,
                            TextValue = "hello",
                            Flag = true,
                            Int32Value = 1,
                            Int64Value = 2,
                            LongValue = 3,
                            IntegerValue = 4,
                            DoubleValue = 1.5,
                            FloatValue = 2.5f,
                            NumberValue = 3.5,
                            TimestampValue = Timestamp.FromDateTime(System.DateTime.UtcNow),
                            DateValue = Timestamp.FromDateTime(System.DateTime.UtcNow),
                        },
                    });
                    return Task.CompletedTask;
                }

                private static Task<FieldTypesQueryResponded?> HandleQueryAsync(
                    FieldTypesQueryRequested query,
                    ScriptQueryContext<FieldTypesReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<FieldTypesQueryResponded?>(new FieldTypesQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new FieldTypesReadModel(),
                    });
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package aevatar.scripting.tests.fieldtypes;

            option csharp_namespace = "Aevatar.Scripting.Core.Tests.FieldTypeMessages";

            import "google/protobuf/timestamp.proto";
            import "scripting_schema_options.proto";
            import "scripting_runtime_options.proto";

            message FieldTypesState {
              string last_command_id = 1;
            }

            message FieldTypesReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "field_types"
                schema_version: "1"
                store_kinds: "document"
              };
              string keyword_value = 1 [(aevatar.scripting.schema.scripting_field) = { storage_type: "keyword" }];
              repeated string keyword_tags = 2 [(aevatar.scripting.schema.scripting_field) = { storage_type: "keyword[]" }];
              string text_value = 3 [(aevatar.scripting.schema.scripting_field) = { storage_type: "text" }];
              bool flag = 4 [(aevatar.scripting.schema.scripting_field) = { storage_type: "boolean" }];
              int32 int32_value = 5 [(aevatar.scripting.schema.scripting_field) = { storage_type: "int32" }];
              int64 int64_value = 6 [(aevatar.scripting.schema.scripting_field) = { storage_type: "int64" }];
              int64 long_value = 7 [(aevatar.scripting.schema.scripting_field) = { storage_type: "long" }];
              int32 integer_value = 8 [(aevatar.scripting.schema.scripting_field) = { storage_type: "integer" }];
              double double_value = 9 [(aevatar.scripting.schema.scripting_field) = { storage_type: "double" }];
              float float_value = 10 [(aevatar.scripting.schema.scripting_field) = { storage_type: "float" }];
              double number_value = 11 [(aevatar.scripting.schema.scripting_field) = { storage_type: "number" }];
              google.protobuf.Timestamp timestamp_value = 12 [(aevatar.scripting.schema.scripting_field) = { storage_type: "timestamp" }];
              google.protobuf.Timestamp date_value = 13 [(aevatar.scripting.schema.scripting_field) = { storage_type: "date" }];
            }

            message FieldTypesCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
              };
              string command_id = 1;
            }

            message FieldTypesUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "aevatar.scripting.tests.fieldtypes.FieldTypesReadModel"
              };
              string command_id = 1;
              FieldTypesReadModel current = 2;
            }

            message FieldTypesQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "aevatar.scripting.tests.fieldtypes.FieldTypesReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "aevatar.scripting.tests.fieldtypes.FieldTypesQueryResponded"
              };
              string request_id = 1;
            }

            message FieldTypesQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "aevatar.scripting.tests.fieldtypes.FieldTypesReadModel"
              };
              string request_id = 1;
              FieldTypesReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("field_types.proto", protoSource)],
            "FieldTypesBehavior");
    }

    private static ScriptSourcePackage CreateUnsupportedFieldTypePackage()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Aevatar.Scripting.Core.Tests.UnsupportedFieldTypeMessages;

            public sealed class UnsupportedFieldTypeBehavior : ScriptBehavior<UnsupportedFieldTypeState, UnsupportedFieldTypeReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<UnsupportedFieldTypeState, UnsupportedFieldTypeReadModel> builder)
                {
                    builder
                        .OnCommand<UnsupportedFieldTypeCommand>(HandleAsync)
                        .OnEvent<UnsupportedFieldTypeUpdated>(
                            apply: static (_, evt, _) => evt.Current == null ? new UnsupportedFieldTypeState() : new UnsupportedFieldTypeState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (_, _) => new UnsupportedFieldTypeReadModel());
                }

                private static Task HandleAsync(
                    UnsupportedFieldTypeCommand command,
                    ScriptCommandContext<UnsupportedFieldTypeState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Emit(new UnsupportedFieldTypeUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = new UnsupportedFieldTypeReadModel { UnsupportedValue = command.CommandId ?? string.Empty },
                    });
                    return Task.CompletedTask;
                }

                private static Task<UnsupportedFieldTypeQueryResponded?> HandleQueryAsync(
                    UnsupportedFieldTypeQueryRequested query,
                    ScriptQueryContext<UnsupportedFieldTypeReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<UnsupportedFieldTypeQueryResponded?>(new UnsupportedFieldTypeQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new UnsupportedFieldTypeReadModel(),
                    });
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package aevatar.scripting.tests.unsupportedfieldtype;

            option csharp_namespace = "Aevatar.Scripting.Core.Tests.UnsupportedFieldTypeMessages";

            import "scripting_schema_options.proto";
            import "scripting_runtime_options.proto";

            message UnsupportedFieldTypeState {
              string last_command_id = 1;
            }

            message UnsupportedFieldTypeReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "unsupported_field_type"
                schema_version: "1"
                store_kinds: "document"
              };
              string unsupported_value = 1 [(aevatar.scripting.schema.scripting_field) = { storage_type: "uuid" }];
            }

            message UnsupportedFieldTypeCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
              };
              string command_id = 1;
            }

            message UnsupportedFieldTypeUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "aevatar.scripting.tests.unsupportedfieldtype.UnsupportedFieldTypeReadModel"
              };
              string command_id = 1;
              UnsupportedFieldTypeReadModel current = 2;
            }

            message UnsupportedFieldTypeQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "aevatar.scripting.tests.unsupportedfieldtype.UnsupportedFieldTypeReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "aevatar.scripting.tests.unsupportedfieldtype.UnsupportedFieldTypeQueryResponded"
              };
              string request_id = 1;
            }

            message UnsupportedFieldTypeQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "aevatar.scripting.tests.unsupportedfieldtype.UnsupportedFieldTypeReadModel"
              };
              string request_id = 1;
              UnsupportedFieldTypeReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("unsupported_field_type.proto", protoSource)],
            "UnsupportedFieldTypeBehavior");
    }

    private static string GetMappingType(
        IReadOnlyDictionary<string, object?> propertyMap,
        string fieldName)
    {
        return propertyMap.Should().ContainKey(fieldName).WhoseValue
            .Should().BeOfType<Dictionary<string, object?>>().Subject["type"]
            .Should().BeOfType<string>().Subject;
    }
}
