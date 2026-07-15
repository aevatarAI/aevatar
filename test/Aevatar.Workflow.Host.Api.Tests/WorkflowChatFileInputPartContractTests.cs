using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Google.Protobuf;
using ApplicationWorkflowChatInputPartKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowChatFileInputPartContractTests
{
    [Fact]
    public void WorkflowChatInputPartPayload_FileKind_ShouldRoundtripFileRefDescriptor()
    {
        WorkflowChatInputPartPayload.Descriptor.Fields.InDeclarationOrder()
            .Should().Contain(field => field.FieldNumber == 7 && field.Name == "file_ref");
        ((int)WorkflowChatInputPartKind.File).Should().Be(5);

        var part = new WorkflowChatInputPartPayload
        {
            Kind = WorkflowChatInputPartKind.File,
            MediaType = "application/pdf",
            Name = "invoice.pdf",
            FileRef = new WorkflowFileRef
            {
                FileId = "file-1",
                ArtifactId = "artifact-1",
                SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
                SourceMessageId = "om_1",
                SourceResourceKey = "file_key_1",
                FileName = "invoice.pdf",
                MediaType = "application/pdf",
                Sha256 = "abc",
                CreatedAtUnixMs = 1710000000000,
                ExpiresAtUnixMs = 1710003600000,
            },
        };

        var parsed = WorkflowChatInputPartPayload.Parser.ParseFrom(part.ToByteArray());

        parsed.Kind.Should().Be(WorkflowChatInputPartKind.File);
        parsed.FileRef.FileId.Should().Be("file-1");
        parsed.FileRef.ArtifactId.Should().Be("artifact-1");
        parsed.FileRef.SourceKind.Should().Be(WorkflowFileSourceKind.ConnectedServiceResource);
        parsed.FileRef.SourceMessageId.Should().Be("om_1");
        parsed.FileRef.SourceResourceKey.Should().Be("file_key_1");
        parsed.FileRef.FileName.Should().Be("invoice.pdf");
        parsed.FileRef.MediaType.Should().Be("application/pdf");
        parsed.FileRef.Sha256.Should().Be("abc");
        parsed.FileRef.CreatedAtUnixMs.Should().Be(1710000000000);
        parsed.FileRef.ExpiresAtUnixMs.Should().Be(1710003600000);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNormalizeFileInputPart()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "file",
                  "fileRef": {
                    "artifactId": "artifact://file-1",
                    "sourceKind": "form_upload",
                    "fileName": "invoice.pdf",
                    "mediaType": "application/pdf",
                    "createdAtUnixMs": 1710000000000,
                    "expiresAtUnixMs": 1710003600000,
                    "sha256": "abc"
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().Be("[file]");
        result.Request.InputParts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPart
            {
                Kind = ApplicationWorkflowChatInputPartKind.File,
                Uri = "artifact://file-1",
                MediaType = "application/pdf",
                Name = "invoice.pdf",
                FileRef = new ApplicationFileArtifactRef
                {
                    ArtifactId = "artifact://file-1",
                    SourceKind = ApplicationFileArtifactSourceKind.FormUpload,
                    FileName = "invoice.pdf",
                    MediaType = "application/pdf",
                    CreatedAtUnixMs = 1710000000000,
                    ExpiresAtUnixMs = 1710003600000,
                    Sha256 = "abc",
                },
            });
    }
}
