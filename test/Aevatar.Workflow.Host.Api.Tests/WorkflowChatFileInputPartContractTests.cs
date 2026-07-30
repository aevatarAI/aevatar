using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using ApplicationWorkflowChatInputPartKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind;
using ApplicationWorkflowChatInputParts = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputParts;
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
    public void WorkflowChatInputParts_FromFileRef_ShouldShapeTypedFileParts()
    {
        var image = ApplicationWorkflowChatInputParts.FromFileRef(new ApplicationFileArtifactRef
        {
            FileId = "file-image",
            ArtifactId = "workflow-file://file-image",
            SourceKind = ApplicationFileArtifactSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = "image_key_1",
            FileName = "image.png",
            MediaType = "image/png",
        });
        var audio = ApplicationWorkflowChatInputParts.FromFileRef(new ApplicationFileArtifactRef
        {
            FileId = "file-audio",
            FileName = "audio.mp3",
            MediaType = "audio/mpeg",
        });
        var video = ApplicationWorkflowChatInputParts.FromFileRef(new ApplicationFileArtifactRef
        {
            ArtifactId = "artifact://video",
            FileName = "video.mp4",
            MediaType = "video/mp4",
        });
        var document = ApplicationWorkflowChatInputParts.FromFileRef(new ApplicationFileArtifactRef
        {
            FileId = "file-document",
            FileName = "invoice.pdf",
            MediaType = "application/pdf",
        });
        var invalid = () => ApplicationWorkflowChatInputParts.FromFileRef(new ApplicationFileArtifactRef
        {
            FileName = "missing-id.pdf",
            MediaType = "application/pdf",
        });

        image.Kind.Should().Be(ApplicationWorkflowChatInputPartKind.Image);
        image.Uri.Should().Be("workflow-file://file-image");
        image.DataBase64.Should().BeNull();
        image.FileRef.Should().NotBeNull();
        image.FileRef!.SourceMessageId.Should().Be("om_1");
        image.FileRef.SourceResourceKey.Should().Be("image_key_1");
        audio.Kind.Should().Be(ApplicationWorkflowChatInputPartKind.Audio);
        audio.Uri.Should().Be("workflow-file://file-audio");
        video.Kind.Should().Be(ApplicationWorkflowChatInputPartKind.Video);
        video.Uri.Should().Be("artifact://video");
        document.Kind.Should().Be(ApplicationWorkflowChatInputPartKind.File);
        document.Uri.Should().Be("workflow-file://file-document");
        invalid.Should().Throw<ArgumentException>()
            .WithMessage("Workflow chat file input requires fileId or artifactId.*");
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

    [Fact]
    public async Task ChatRunRequestNormalizer_ShouldIngressInlineFileAsTypedFileRef()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-chat-inline-file-contract-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonSerializer.Deserialize<ChatInput>(
                """
                {
                  "inputParts": [
                    {
                      "type": "file",
                      "inlineFile": {
                        "dataBase64": "aW52b2ljZSB0b3RhbCA0Mg==",
                        "mediaType": "application/pdf",
                        "name": "invoice.pdf",
                        "sizeBytes": 16,
                        "ownerScopeId": "scope-1"
                      }
                    }
                  ]
                }
                """,
                ChatWebSocketProtocol.JsonOptions)!;
            var filePort = new FileSystemFileArtifactPort(Options.Create(new FileSystemFileArtifactOptions
            {
                RootDirectory = root,
                TimeToLive = TimeSpan.FromMinutes(30),
            }));

            var result = await ChatRunRequestNormalizer.NormalizeAsync(input, filePort);

            result.Succeeded.Should().BeTrue();
            result.Request!.Prompt.Should().Be("[file]");
            var part = result.Request.InputParts.Should().ContainSingle().Subject;
            part.Kind.Should().Be(ApplicationWorkflowChatInputPartKind.File);
            part.DataBase64.Should().BeNull();
            part.Uri.Should().StartWith("workflow-file://");
            part.MediaType.Should().Be("application/pdf");
            part.Name.Should().Be("invoice.pdf");
            part.FileRef.Should().NotBeNull();
            part.FileRef!.FileId.Should().NotBeNullOrWhiteSpace();
            part.FileRef.ArtifactId.Should().Be(part.Uri);
            part.FileRef.SourceKind.Should().Be(ApplicationFileArtifactSourceKind.ChatInput);
            part.FileRef.FileName.Should().Be("invoice.pdf");
            part.FileRef.MediaType.Should().Be("application/pdf");
            part.FileRef.SizeBytes.Should().Be(16);
            part.FileRef.Sha256.Should().NotBeNullOrWhiteSpace();
            part.FileRef.OwnerScopeId.Should().Be("scope-1");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
