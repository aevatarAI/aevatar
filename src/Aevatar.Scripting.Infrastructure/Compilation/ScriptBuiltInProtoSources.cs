namespace Aevatar.Scripting.Infrastructure.Compilation;

internal static class ScriptBuiltInProtoSources
{
    public const string ScriptingSchemaOptionsFileName = "scripting_schema_options.proto";
    public const string ScriptingRuntimeOptionsFileName = "scripting_runtime_options.proto";

    public const string ScriptingSchemaOptionsContent =
        """
        syntax = "proto3";

        package aevatar.scripting.schema;

        option csharp_namespace = "Aevatar.Scripting.Abstractions.Schema";

        import "google/protobuf/descriptor.proto";

        message ScriptingDocumentIndexOptions {
          string name = 1;
          repeated string paths = 2;
          bool unique = 3;
          string provider = 4;
        }

        message ScriptingGraphRelationOptions {
          string name = 1;
          string source_path = 2;
          string target_schema_id = 3;
          string target_path = 4;
          string cardinality = 5;
          string provider = 6;
        }

        message ScriptingReadModelOptions {
          string schema_id = 1;
          string schema_version = 2;
          repeated string store_kinds = 3;
          repeated ScriptingDocumentIndexOptions document_indexes = 4;
          repeated ScriptingGraphRelationOptions graph_relations = 5;
        }

        message ScriptingFieldOptions {
          string storage_type = 1;
          bool nullable = 2;
        }

        extend google.protobuf.MessageOptions {
          ScriptingReadModelOptions scripting_read_model = 51001;
        }

        extend google.protobuf.FieldOptions {
          ScriptingFieldOptions scripting_field = 51002;
        }
        """;

    public const string ScriptingRuntimeOptionsContent =
        """
        syntax = "proto3";

        package aevatar.scripting.runtime;

        option csharp_namespace = "Aevatar.Scripting.Abstractions.RuntimeSemantics";

        import "google/protobuf/descriptor.proto";

        enum ScriptingMessageKind {
          SCRIPTING_MESSAGE_KIND_UNSPECIFIED = 0;
          SCRIPTING_MESSAGE_KIND_COMMAND = 1;
          SCRIPTING_MESSAGE_KIND_INTERNAL_SIGNAL = 2;
          SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT = 3;
          SCRIPTING_MESSAGE_KIND_QUERY_REQUEST = 4;
          SCRIPTING_MESSAGE_KIND_QUERY_RESULT = 5;
        }

        message ScriptingMessageRuntimeOptions {
          ScriptingMessageKind kind = 1;
          optional bool projectable = 2;
          optional bool replay_safe = 3;
          optional bool snapshot_candidate = 4;
          string aggregate_id_field = 5;
          string command_id_field = 6;
          string correlation_id_field = 7;
          string causation_id_field = 8;
          string read_model_scope = 9;
        }

        message ScriptingQueryRuntimeOptions {
          string result_full_name = 1;
        }

        message ScriptingFieldRuntimeOptions {
          bool aggregate_identity = 1;
          bool correlation_identity = 2;
        }

        extend google.protobuf.MessageOptions {
          ScriptingMessageRuntimeOptions scripting_runtime = 51011;
          ScriptingQueryRuntimeOptions scripting_query = 51012;
        }

        extend google.protobuf.FieldOptions {
          ScriptingFieldRuntimeOptions scripting_runtime_field = 51013;
        }
        """;
}
