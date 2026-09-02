using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;
using ProtoWorkflowCallerCredential = Aevatar.Workflow.Abstractions.WorkflowCallerCredential;
using ProtoWorkflowFileRef = Aevatar.Workflow.Abstractions.WorkflowFileRef;
using ProtoWorkflowFileSourceKind = Aevatar.Workflow.Abstractions.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowDocumentExtractToolTests
{
    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnSchemaBoundCanonicalJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("invoice INV-7 total 42"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider(["""{"total":42,"invoice_id":"INV-7"}"""]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "invoice_summary",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "invoice_id": { "type": "string" },
                          "total": { "type": "number" }
                        },
                        "required": ["invoice_id", "total"],
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("extraction_kind").GetString().Should().Be("schema_bound_json");
            rootElement.GetProperty("media_type").GetString().Should().Be("text/plain");
            rootElement.GetProperty("schema_name").GetString().Should().Be("invoice_summary");
            rootElement.GetProperty("schema_hash").GetString().Should().StartWith("sha256:");
            rootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(result.FileRef.FileId);
            var structured = rootElement.GetProperty("structured_result");
            structured.GetProperty("invoice_id").GetString().Should().Be("INV-7");
            structured.GetProperty("total").GetDecimal().Should().Be(42);
            output.ResultJson.Should().Contain("\"structured_result\":{\"invoice_id\":\"INV-7\",\"total\":42}");
            output.ResultJson.Should().NotContain("invoice INV-7 total 42");
            output.ResultJson.Contains("Return only", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            output.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();

            llmProvider.Requests.Should().ContainSingle();
            var request = llmProvider.Requests.Single();
            request.ResponseFormat.Should().NotBeNull();
            request.ResponseFormat!.Kind.Should().Be(LLMResponseFormatKind.JsonSchema);
            request.Messages.Should().HaveCount(2);
            request.Messages[1].Content.Should().Contain("invoice INV-7 total 42");
            request.Messages[1].Content.Should().Contain("invoice_summary");
            request.Messages[1].ContentParts.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnSchemaBoundImageJsonWithoutRawBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-image-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageBytes = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 };
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                imageBytes,
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var llmProvider = new RecordingImageLlmProvider(["""{"invoice_id":"IMG-1"}"""]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                ArgumentsJson: BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "receipt_summary",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "invoice_id": { "type": "string" }
                        },
                        "required": ["invoice_id"],
                        "additionalProperties": false
                      }
                    }
                    """),
                RunId: "run-1",
                StepId: "extract",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new ProtoWorkflowCallerCredential { BearerToken = "caller-alpha" },
                RuntimeContext: WorkflowToolRuntimeContext.Empty,
                LlmControl: new Aevatar.Workflow.Abstractions.WorkflowLlmControlContext
                {
                    ModelOverride = "model-alpha",
                    RoutePreference = "route-alpha",
                    MaxToolRoundsOverride = 5,
                    UserMemoryPrompt = "memory-alpha",
                    SenderNyxIdAccessToken = "sender-alpha",
                }));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("extraction_kind").GetString().Should().Be("schema_bound_json");
            rootElement.GetProperty("media_type").GetString().Should().Be("image/png");
            rootElement.GetProperty("structured_result").GetProperty("invoice_id").GetString().Should().Be("IMG-1");
            output.ResultJson.Contains(Convert.ToBase64String(imageBytes), StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("data:image", StringComparison.OrdinalIgnoreCase).Should().BeFalse();

            llmProvider.Requests.Should().ContainSingle();
            var request = llmProvider.Requests.Single();
            request.Messages[1].ContentParts.Should().ContainSingle(part =>
                part.Kind == ContentPartKind.Image &&
                part.MediaType == "image/png" &&
                part.DataBase64 == Convert.ToBase64String(imageBytes));
            AssertWorkflowLlmContext(request);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRequireSchemaContractForSchemaBoundJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-missing-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("invoice total 42"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider(["unused"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(result.FileRef, schemaContractJson: null),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("schema_contract");
            llmProvider.Requests.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectSchemaBoundJsonProviderOutputOutsideContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-validation-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("invoice total 42"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider([
                """{"invoice_id":"INV-7","raw_prompt":"Return only a JSON object with c3RhY2s="}""",
            ]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "invoice_summary",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "invoice_id": { "type": "string" }
                        },
                        "required": ["invoice_id"],
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("schema_bound_validation_failed");
            document.RootElement.GetProperty("detail").GetString().Should().Be("Schema-bound extraction result failed validation.");
            output.ResultJson.Should().NotContain("raw_prompt");
            output.ResultJson.Contains("Return only", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            output.ResultJson.Contains("c3RhY2s=", StringComparison.Ordinal).Should().BeFalse();
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldAcceptSchemaBoundArrayItems()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-array-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("line items include sku A1 and B2"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider([
                """{"line_items":[{"sku":"A1","quantity":2},{"sku":"B2","quantity":1}]}""",
            ]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "invoice_items",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "line_items": {
                            "type": "array",
                            "items": {
                              "type": "object",
                              "properties": {
                                "sku": { "type": "string" },
                                "quantity": { "type": "integer" }
                              },
                              "required": ["sku", "quantity"],
                              "additionalProperties": false
                            }
                          }
                        },
                        "required": ["line_items"],
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            var structured = document.RootElement.GetProperty("structured_result");
            var lineItems = structured.GetProperty("line_items").EnumerateArray().ToArray();
            lineItems.Should().HaveCount(2);
            lineItems[0].GetProperty("sku").GetString().Should().Be("A1");
            lineItems[0].GetProperty("quantity").GetInt32().Should().Be(2);
            lineItems[1].GetProperty("sku").GetString().Should().Be("B2");
            lineItems[1].GetProperty("quantity").GetInt32().Should().Be(1);
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectSchemaBoundArrayItemsWithWrongType()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-array-reject-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("line items include sku A1"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider([
                """{"line_items":[{"sku":"A1","quantity":"2"}]}""",
            ]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "invoice_items",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "line_items": {
                            "type": "array",
                            "items": {
                              "type": "object",
                              "properties": {
                                "sku": { "type": "string" },
                                "quantity": { "type": "integer" }
                              },
                              "required": ["sku", "quantity"],
                              "additionalProperties": false
                            }
                          }
                        },
                        "required": ["line_items"],
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("schema_bound_validation_failed");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("Schema-bound extraction result failed validation.");
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldAcceptSchemaBoundEnumValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-enum-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("receipt status approved"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider(["""{"status":"approved"}"""]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "receipt_status",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "status": {
                            "type": "string",
                            "enum": ["approved", "rejected", "pending"]
                          }
                        },
                        "required": ["status"],
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("structured_result")
                .GetProperty("status")
                .GetString()
                .Should().Be("approved");
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectSchemaBoundEnumValuesOutsideContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-enum-reject-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("receipt status escalated"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider(["""{"status":"escalated"}"""]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "receipt_status",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "status": {
                            "type": "string",
                            "enum": ["approved", "rejected", "pending"]
                          }
                        },
                        "required": ["status"],
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("schema_bound_validation_failed");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("Schema-bound extraction result failed validation.");
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldAcceptSchemaBoundPrimitiveAndNullableTypes()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-primitive-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("receipt paid with optional memo missing"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider([
                """{"approved":true,"line_count":3,"optional_note":null,"void_marker":null}""",
            ]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "receipt_flags",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "approved": { "type": "boolean" },
                          "line_count": { "type": "integer" },
                          "optional_note": { "type": ["string", "null"] },
                          "void_marker": { "type": "null" }
                        },
                        "required": ["approved", "line_count", "optional_note", "void_marker"],
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            var structured = document.RootElement.GetProperty("structured_result");
            structured.GetProperty("approved").GetBoolean().Should().BeTrue();
            structured.GetProperty("line_count").GetInt32().Should().Be(3);
            structured.GetProperty("optional_note").ValueKind.Should().Be(JsonValueKind.Null);
            structured.GetProperty("void_marker").ValueKind.Should().Be(JsonValueKind.Null);
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectSchemaBoundPrimitiveTypeMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-primitive-reject-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("receipt has three lines"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider([
                """{"approved":true,"line_count":3.5,"optional_note":5,"void_marker":null}""",
            ]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "receipt_flags",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "approved": { "type": "boolean" },
                          "line_count": { "type": "integer" },
                          "optional_note": { "type": ["string", "null"] },
                          "void_marker": { "type": "null" }
                        },
                        "required": ["approved", "line_count", "optional_note", "void_marker"],
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("schema_bound_validation_failed");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("Schema-bound extraction result failed validation.");
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectInvalidSchemaBoundContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-contract-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("invoice total 42"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider(["unused"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "invoice_summary",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "invoice_id": { "type": "string", "pattern": "INV-[0-9]+" }
                        }
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("unsupported schema keyword");
            llmProvider.Requests.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectSchemaContractsThatAllowLeakFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-leak-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("invoice total 42"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var llmProvider = new RecordingImageLlmProvider(["unused"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "unsafe_invoice_summary",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "invoice_id": { "type": "string" },
                          "provider_raw_payload": { "type": "string" }
                        },
                        "additionalProperties": false
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("unsafe result property");
            output.ResultJson.Should().NotContain("unused");
            llmProvider.Requests.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectInvalidExtractionKind()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-kind-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("invoice total 42"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef, extractionKind: "second_public_ocr_tool"),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("extraction_kind");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnUnavailableWhenSchemaBoundProviderMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-schema-provider-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("invoice total 42"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildSchemaBoundDocumentExtractArguments(
                    result.FileRef,
                    """
                    {
                      "name": "invoice_summary",
                      "schema": {
                        "type": "object",
                        "properties": {
                          "invoice_id": { "type": "string" }
                        }
                      }
                    }
                    """),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("schema_bound_provider_unavailable");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("document_extract schema-bound extraction requires a configured LLM provider.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldUseSingleInputFileRefWhenArgumentsOmitFileRef()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-input-file-ref-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                System.Text.Encoding.UTF8.GetBytes("invoice total 42"),
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                "{}",
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential(),
                [ToProtoInputFileRef(result.FileRef)]));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("extraction_kind").GetString().Should().Be("utf8_text");
            rootElement.GetProperty("media_type").GetString().Should().Be("text/plain");
            rootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(result.FileRef.FileId);
            rootElement.GetProperty("text").GetString().Should().Be("invoice total 42");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldNotTreatAttachmentRefAsFileIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-attachment-ref-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                """
                {
                  "attachment_ref": "file_v3_1"
                }
                """,
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Contain("fileRef object or exactly one input file ref");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldWireDocumentExtractImageProviderFromLlmFactory()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-di-image-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageProvider = new RecordingImageLlmProvider(["factory image text"]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ILLMProviderFactory>(imageProvider);
            services.Configure<FileSystemFileArtifactOptions>(options =>
                options.RootDirectory = root);

            services.AddWorkflowInfrastructure();

            using var provider = services.BuildServiceProvider();
            var filePort = provider.GetRequiredService<FileSystemFileArtifactPort>();
            var result = await filePort.IngestAsync(new FileArtifactIngressRequest(
                new byte[] { 1, 2, 3 },
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var tools = new List<IWorkflowTool>();
            foreach (var toolSource in provider.GetServices<IWorkflowToolSource>())
            {
                tools.AddRange(await toolSource.GetToolsAsync());
            }

            var output = await tools.Single(x => x.Name == "document_extract")
                .ExecuteAsync(new WorkflowToolExecutionRequest(
                    BuildDocumentExtractArguments(result.FileRef),
                    "run-1",
                    "extract",
                    "exec-1",
                    "call-1",
                    "scope-1",
                    new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("text").GetString().Should().Be("factory image text");
            imageProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    public async Task WorkflowDocumentExtractTool_ShouldExtractImageTextThroughStreamingProvider(string mediaType)
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageBytes = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 };
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                imageBytes,
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: mediaType == "image/png" ? "receipt.png" : "receipt.jpg",
                MediaType: mediaType));
            var llmProvider = new RecordingImageLlmProvider(["receipt ", "total 42"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                ArgumentsJson: BuildDocumentExtractArguments(result.FileRef),
                RunId: "run-1",
                StepId: "extract",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new ProtoWorkflowCallerCredential { BearerToken = "caller-alpha" },
                RuntimeContext: WorkflowToolRuntimeContext.Empty,
                LlmControl: new Aevatar.Workflow.Abstractions.WorkflowLlmControlContext
                {
                    ModelOverride = "model-alpha",
                    RoutePreference = "route-alpha",
                    MaxToolRoundsOverride = 5,
                    UserMemoryPrompt = "memory-alpha",
                    SenderNyxIdAccessToken = "sender-alpha",
                }));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("extraction_kind").GetString().Should().Be("image_text");
            rootElement.GetProperty("media_type").GetString().Should().Be(mediaType);
            rootElement.GetProperty("text").GetString().Should().Be("receipt total 42");
            rootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
            rootElement.GetProperty("extracted_chars").GetInt32().Should().Be(16);
            rootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(result.FileRef.FileId);
            output.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            output.ResultJson.Contains(Convert.ToBase64String(imageBytes), StringComparison.Ordinal).Should().BeFalse();

            llmProvider.Requests.Should().ContainSingle();
            var request = llmProvider.Requests.Single();
            request.Messages.Should().HaveCount(2);
            request.Messages[0].Role.Should().Be("system");
            request.Messages[1].Role.Should().Be("user");
            request.Messages[1].ContentParts.Should().ContainSingle(part =>
                part.Kind == ContentPartKind.Image &&
                part.MediaType == mediaType &&
                part.Name == result.FileRef.FileName &&
                part.DataBase64 == Convert.ToBase64String(imageBytes));
            AssertWorkflowLlmContext(request);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldTruncateImageTextToRequestedMaxChars()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-truncation-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageBytes = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 };
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                imageBytes,
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var llmProvider = new RecordingImageLlmProvider(["receipt total 42"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef, maxChars: 7),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("text").GetString().Should().Be("receipt");
            rootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
            rootElement.GetProperty("extracted_chars").GetInt32().Should().Be(7);
            llmProvider.Requests.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnUnavailableWhenImageProviderMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-unavailable-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                new byte[] { 1, 2, 3 },
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_provider_unavailable");
            document.RootElement.GetProperty("detail").GetString()
                .Should().NotContain("base64");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectSpreadsheetMediaType()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-spreadsheet-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                new byte[] { 80, 75, 3, 4 },
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "budget.xlsx",
                MediaType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("unsupported_media_type");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("spreadsheetml.sheet");
            output.Failure.Should().NotBeNull();
            output.Failure!.ErrorCode.Should().Be("unsupported_media_type");
            output.Failure.ErrorMessage.Should().Contain("spreadsheetml.sheet");
            output.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnUnavailableWhenProviderDoesNotSupportImageInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-text-provider-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                new byte[] { 1, 2, 3 },
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.jpeg",
                MediaType: "image/jpeg"));
            var tool = await GetDocumentExtractToolAsync(port, new TextOnlyLlmProvider());

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_provider_unavailable");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectImagesOverFiveMiB()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-large-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                new byte[(5 * 1024 * 1024) + 1],
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "large.png",
                MediaType: "image/png"));
            var llmProvider = new RecordingImageLlmProvider(["unused"]);
            var tool = await GetDocumentExtractToolAsync(port, llmProvider);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_too_large");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("5242880");
            llmProvider.Requests.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectImagesWhenStreamExceedsFiveMiB()
    {
        var fileRef = new ApplicationFileArtifactRef
        {
            FileId = "underreported-image",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "underreported.png",
            MediaType = "image/png",
            SizeBytes = 0,
        };
        var port = new StaticWorkflowFileArtifactReadPort(
            fileRef,
            new MemoryStream(new byte[(5 * 1024 * 1024) + 1]));
        var llmProvider = new RecordingImageLlmProvider(["unused"]);
        var tool = await GetDocumentExtractToolAsync(port, llmProvider);

        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            BuildDocumentExtractArguments(fileRef),
            "run-1",
            "extract",
            "exec-1",
            "call-1",
            "scope-1",
            new ProtoWorkflowCallerCredential()));

        using var document = JsonDocument.Parse(output.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("image_too_large");
        document.RootElement.GetProperty("detail").GetString().Should().Contain("5242880");
        llmProvider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldSanitizeProviderFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-failure-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var imageBytes = new byte[] { 1, 2, 3, 4 };
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                imageBytes,
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var tool = await GetDocumentExtractToolAsync(
                port,
                new ThrowingImageLlmProvider("provider failed with c3RhY2s= raw payload"));

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_extraction_failed");
            var detail = document.RootElement.GetProperty("detail").GetString();
            detail.Should().Be("Image extraction provider failed.");
            output.ResultJson.Contains(Convert.ToBase64String(imageBytes), StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("c3RhY2s=", StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("raw payload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldSanitizeProviderFactoryFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-image-factory-failure-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var imageBytes = new byte[] { 1, 2, 3, 4 };
            var result = await port.IngestAsync(new FileArtifactIngressRequest(
                imageBytes,
                ApplicationFileArtifactSourceKind.ChatInput,
                FileName: "receipt.png",
                MediaType: "image/png"));
            var tool = await GetDocumentExtractToolAsync(
                port,
                llmProviderFactory: new ThrowingLlmProviderFactory("factory failed with c3RhY2s= raw payload"));

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("image_extraction_failed");
            var detail = document.RootElement.GetProperty("detail").GetString();
            detail.Should().Be("Image extraction provider failed.");
            output.ResultJson.Contains(Convert.ToBase64String(imageBytes), StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("c3RhY2s=", StringComparison.Ordinal).Should().BeFalse();
            output.ResultJson.Contains("raw payload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static FileSystemFileArtifactPort CreateFileArtifactPort(string root) =>
        new(Microsoft.Extensions.Options.Options.Create(new FileSystemFileArtifactOptions
        {
            RootDirectory = root,
            TimeToLive = TimeSpan.FromMinutes(30),
        }));

    private static async Task<IWorkflowTool> GetDocumentExtractToolAsync(
        IFileArtifactReadPort readPort,
        ILLMProvider? llmProvider = null,
        ILLMProviderFactory? llmProviderFactory = null)
    {
        var source = new WorkflowDocumentExtractToolSource(readPort, llmProvider, llmProviderFactory);
        var tools = await source.GetToolsAsync();
        return tools.Should().ContainSingle(x => x.Name == "document_extract").Subject;
    }

    private static ProtoWorkflowFileRef ToProtoInputFileRef(ApplicationFileArtifactRef fileRef) =>
        new()
        {
            FileId = fileRef.FileId ?? string.Empty,
            ArtifactId = fileRef.ArtifactId ?? string.Empty,
            SourceKind = fileRef.SourceKind switch
            {
                ApplicationFileArtifactSourceKind.ChatInput => ProtoWorkflowFileSourceKind.ChatInput,
                ApplicationFileArtifactSourceKind.FormUpload => ProtoWorkflowFileSourceKind.FormUpload,
                ApplicationFileArtifactSourceKind.ConnectedServiceResource => ProtoWorkflowFileSourceKind.ConnectedServiceResource,
                ApplicationFileArtifactSourceKind.ExternalResource => ProtoWorkflowFileSourceKind.ExternalResource,
                ApplicationFileArtifactSourceKind.Generated => ProtoWorkflowFileSourceKind.Generated,
                _ => ProtoWorkflowFileSourceKind.Unspecified,
            },
            SourceMessageId = fileRef.SourceMessageId ?? string.Empty,
            SourceResourceKey = fileRef.SourceResourceKey ?? string.Empty,
            FileName = fileRef.FileName ?? string.Empty,
            MediaType = fileRef.MediaType ?? string.Empty,
            SizeBytes = fileRef.SizeBytes,
            Sha256 = fileRef.Sha256 ?? string.Empty,
            CreatedAtUnixMs = fileRef.CreatedAtUnixMs,
            ExpiresAtUnixMs = fileRef.ExpiresAtUnixMs,
            OwnerRunId = fileRef.OwnerRunId ?? string.Empty,
            OwnerScopeId = fileRef.OwnerScopeId ?? string.Empty,
        };

    private static string BuildDocumentExtractArguments(
        ApplicationFileArtifactRef fileRef,
        int? maxChars = null,
        string? extractionKind = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["file_ref"] = new Dictionary<string, object?>
            {
                ["file_id"] = fileRef.FileId,
                ["artifact_id"] = fileRef.ArtifactId,
                ["source_kind"] = fileRef.SourceKind.ToString(),
                ["file_name"] = fileRef.FileName,
                ["media_type"] = fileRef.MediaType,
                ["size_bytes"] = fileRef.SizeBytes,
                ["sha256"] = fileRef.Sha256,
                ["created_at_unix_ms"] = fileRef.CreatedAtUnixMs,
                ["expires_at_unix_ms"] = fileRef.ExpiresAtUnixMs,
            },
        };
        if (maxChars != null)
            payload["max_chars"] = maxChars.Value;
        if (extractionKind != null)
            payload["extraction_kind"] = extractionKind;

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildSchemaBoundDocumentExtractArguments(
        ApplicationFileArtifactRef fileRef,
        string? schemaContractJson)
    {
        using var schemaContract = schemaContractJson == null
            ? null
            : JsonDocument.Parse(schemaContractJson);
        var payload = new Dictionary<string, object?>
        {
            ["file_ref"] = new Dictionary<string, object?>
            {
                ["file_id"] = fileRef.FileId,
                ["artifact_id"] = fileRef.ArtifactId,
                ["source_kind"] = fileRef.SourceKind.ToString(),
                ["file_name"] = fileRef.FileName,
                ["media_type"] = fileRef.MediaType,
                ["size_bytes"] = fileRef.SizeBytes,
                ["sha256"] = fileRef.Sha256,
                ["created_at_unix_ms"] = fileRef.CreatedAtUnixMs,
                ["expires_at_unix_ms"] = fileRef.ExpiresAtUnixMs,
            },
            ["extraction_kind"] = "schema_bound_json",
        };
        if (schemaContract != null)
            payload["schema_contract"] = schemaContract.RootElement.Clone();

        return JsonSerializer.Serialize(payload);
    }

    private static void AssertWorkflowLlmContext(LLMRequest request)
    {
        request.CallerContext.Should().NotBeNull();
        request.CallerContext!.ScopeId.Should().Be("scope-1");
        request.CallerContext.OwnerSubject.Should().Be("scope-1");
        request.CallerContext.Credentials.Should().NotBeNull();
        request.CallerContext.Credentials!.NyxIdBearer.Should().Be("caller-alpha");
        request.LlmControl.Should().NotBeNull();
        request.LlmControl!.ModelOverride.Should().Be("model-alpha");
        request.LlmControl.NyxIdRoutePreference.Should().Be("route-alpha");
        request.LlmControl.MaxToolRoundsOverride.Should().Be(5);
        request.LlmControl.UserMemoryPrompt.Should().Be("memory-alpha");
        request.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-alpha");
    }

    private sealed class StaticWorkflowFileArtifactReadPort(
        ApplicationFileArtifactRef fileRef,
        Stream content) : IFileArtifactReadPort
    {
        public ValueTask<ApplicationFileArtifactRef> DescribeAsync(
            ApplicationFileArtifactRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(fileRef);

        public ValueTask<FileArtifactContent> OpenReadAsync(
            ApplicationFileArtifactRef requestedFileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FileArtifactContent(fileRef, content));
    }

    private sealed class RecordingImageLlmProvider(IReadOnlyList<string> chunks) : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "recording-image";

        public LLMProviderCapabilities Capabilities { get; } = new()
        {
            SupportedInputModalities = new HashSet<ContentPartKind>
            {
                ContentPartKind.Text,
                ContentPartKind.Image,
            },
        };

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaContent = chunk };
                await Task.Yield();
            }
        }
    }

    private sealed class TextOnlyLlmProvider : ILLMProvider
    {
        public string Name => "text-only";

        public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, CancellationToken ct = default) =>
            AsyncEnumerable.Empty<LLMStreamChunk>();
    }

    private sealed class ThrowingImageLlmProvider(string message) : ILLMProvider
    {
        public string Name => "throwing-image";

        public LLMProviderCapabilities Capabilities { get; } = new()
        {
            SupportedInputModalities = new HashSet<ContentPartKind>
            {
                ContentPartKind.Text,
                ContentPartKind.Image,
            },
        };

        public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            CancellationToken ct = default)
        {
            return ThrowAsync();

            async IAsyncEnumerable<LLMStreamChunk> ThrowAsync()
            {
                if (ct.IsCancellationRequested)
                {
                    yield return new LLMStreamChunk();
                    yield break;
                }

                await Task.Yield();
                throw new InvalidOperationException(message);
            }
        }
    }

    private sealed class ThrowingLlmProviderFactory(string message) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => throw new InvalidOperationException(message);

        public ILLMProvider GetDefault() => throw new InvalidOperationException(message);

        public IReadOnlyList<string> GetAvailableProviders() => [];
    }
}
