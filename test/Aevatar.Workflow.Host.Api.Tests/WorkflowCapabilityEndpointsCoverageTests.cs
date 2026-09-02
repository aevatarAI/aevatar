using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCapabilityEndpointsCoverageTests
{
    [Fact]
    public void ChatRunRequestNormalizer_ShouldPreserveWorkflowName_WhenInlineWorkflowBundleIsProvided()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Workflow = "auto",
            SessionId = " session-1 ",
            WorkflowYamls = ["name: inline"],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.InlineYamlBundle(["name: inline"], "auto"), Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive,
                SessionId: "session-1",
                Metadata: new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldAcceptLegacyWorkflowYamlAlias()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            WorkflowYaml = "name: inline",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.InlineYamlBundle(["name: inline"]), Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive,
                SessionId: null,
                Metadata: new Dictionary<string, string>()));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectBlankLegacyWorkflowYaml()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            WorkflowYaml = "   ",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectMixedLegacyAndBundleWorkflowYaml()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            WorkflowYaml = "name: legacy",
            WorkflowYamls = ["name: bundle"],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldLeaveWorkflowUnset_WhenCreatingNewRun()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "hello",
                WorkflowChatSource.Direct(), Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive,
                Metadata: new Dictionary<string, string>(),
                LlmControl: null));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNotRewritePrompt_WhenLegacyAuthoringMetadataAndEnvArePresent()
    {
        const string envName = "AEVATAR_WORKFLOW_AUTHORING_AUTO_INJECT";
        var previous = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "true");
        try
        {
            var input = new ChatInput
            {
                Prompt = "design the workflow",
                Workflow = " auto ",
                Metadata = new Dictionary<string, string>
                {
                    ["workflow.authoring.enabled"] = "true",
                    ["workflow.intent"] = "workflow_authoring",
                },
            };

            var result = ChatRunRequestNormalizer.Normalize(input);

            result.Succeeded.Should().BeTrue();
            result.Request!.Prompt.Should().Be("design the workflow");
            result.Request.Prompt.Should().NotContain("[skill:workflow_authoring]");
            result.Request.Source.Should().BeEquivalentTo(WorkflowChatSource.CatalogWorkflow("auto"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldKeepDirectAndInlineYamlPromptsUnchanged()
    {
        var direct = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = " direct prompt ",
        });
        var inline = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = " inline prompt ",
            Source = new WorkflowChatSourceInput
            {
                Kind = "inline-yaml-bundle",
                WorkflowName = " auto ",
                WorkflowYamls = ["name: auto"],
            },
        });

        direct.Succeeded.Should().BeTrue();
        inline.Succeeded.Should().BeTrue();
        direct.Request!.Prompt.Should().Be("direct prompt");
        direct.Request.Source.Should().BeEquivalentTo(WorkflowChatSource.Direct());
        inline.Request!.Prompt.Should().Be("inline prompt");
        inline.Request.Source.Should().BeEquivalentTo(WorkflowChatSource.InlineYamlBundle(["name: auto"], "auto"));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNormalizeTypedSourceAndLlmControl()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "inline-yaml-bundle",
                WorkflowName = " auto ",
                WorkflowYamls = ["name: auto"],
            },
            LlmControl = new ChatLlmControlInput
            {
                NyxIdAccessToken = " token ",
                ModelOverride = " model ",
                NyxIdRoutePreference = " route ",
                MaxToolRoundsOverride = 3,
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Should().BeEquivalentTo(
            WorkflowChatSource.InlineYamlBundle(["name: auto"], "auto"));
        result.Request.LlmControl.Should().Be(new WorkflowLlmControl(
            "model",
            3,
            UserMemoryPrompt: null,
            RoutePreference: "route"));
        result.Request.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldKeepToolContextOutOfWorkflowCommand()
    {
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(" req-1 ", " call-1 "),
            Caller = new AgentToolCallerContext(" scope-1 ", " owner-1 ", " response-1 "),
            Routing = LLMRequestRoutingContext.Empty with
            {
                ModelOverride = " model-a ",
                NyxIdRoutePreference = " route-a ",
                MaxToolRoundsOverride = 2,
            },
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["trace-id"] = "trace-1",
            },
        };
        var input = new ChatInput
        {
            Prompt = "hello",
            ToolContext = toolContext,
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().BeNull();
        result.Request.LlmControl.Should().BeNull();
        result.Request.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNormalizeTypedInlineBundleSubmessage()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "inline-yaml-bundle",
                InlineBundle = new WorkflowChatInlineYamlBundleSourceInput
                {
                    EntryName = " auto ",
                    ActorId = " source-actor-1 ",
                    YamlDocuments =
                    [
                        new WorkflowChatInlineYamlDocumentInput
                        {
                            Name = "auto",
                            Yaml = "name: auto",
                        },
                    ],
                },
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Kind.Should().Be(WorkflowChatSourceKind.InlineYamlBundle);
        result.Request.Source.InlineBundle.Should().NotBeNull();
        result.Request.Source.InlineBundle!.EntryName.Should().Be("auto");
        result.Request.Source.InlineBundle.ActorId.Should().Be("source-actor-1");
        result.Request.Source.InlineBundle.YamlDocuments.Should().ContainSingle()
            .Which.Should().Be(new WorkflowChatInlineYamlDocument("auto", "name: auto"));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldPreferTypedInlineBundleSubmessageOverLegacyInlineAliases()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "inline-yaml-bundle",
                WorkflowName = "legacy",
                WorkflowYamls = ["name: legacy"],
                InlineBundle = new WorkflowChatInlineYamlBundleSourceInput
                {
                    EntryName = " typed ",
                    ActorId = " typed-actor ",
                    YamlDocuments =
                    [
                        new WorkflowChatInlineYamlDocumentInput
                        {
                            Name = "typed",
                            Yaml = "name: typed",
                        },
                    ],
                },
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Kind.Should().Be(WorkflowChatSourceKind.InlineYamlBundle);
        result.Request.Source.InlineBundle.Should().BeEquivalentTo(new WorkflowChatInlineYamlBundleSource(
            "typed",
            [new WorkflowChatInlineYamlDocument("typed", "name: typed")],
            "typed-actor"));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectBlankTypedInlineBundleYamlDocument()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "inline-yaml-bundle",
                InlineBundle = new WorkflowChatInlineYamlBundleSourceInput
                {
                    EntryName = "auto",
                    YamlDocuments =
                    [
                        new WorkflowChatInlineYamlDocumentInput
                        {
                            Name = "auto",
                            Yaml = "   ",
                        },
                    ],
                },
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldTrimTypedCatalogNameSubmessage()
    {
        var result = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "catalog",
                CatalogName = new WorkflowChatCatalogNameSourceInput
                {
                    WorkflowName = " auto ",
                },
            },
        });
        var blankResult = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "catalog",
                CatalogName = new WorkflowChatCatalogNameSourceInput
                {
                    WorkflowName = "   ",
                },
            },
        });

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Kind.Should().Be(WorkflowChatSourceKind.CatalogWorkflow);
        result.Request.Source.CatalogName.Should().Be(new WorkflowChatCatalogNameSource("auto"));
        blankResult.Succeeded.Should().BeFalse();
        blankResult.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldPreferTypedCatalogNameSubmessageOverLegacyAlias()
    {
        var result = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "catalog",
                WorkflowName = "legacy",
                CatalogName = new WorkflowChatCatalogNameSourceInput
                {
                    WorkflowName = " typed ",
                },
            },
        });

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Kind.Should().Be(WorkflowChatSourceKind.CatalogWorkflow);
        result.Request.Source.CatalogName.Should().Be(new WorkflowChatCatalogNameSource("typed"));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldTrimTypedDefinitionActorSubmessage()
    {
        var result = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "actor",
                DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                {
                    ActorId = " actor-1 ",
                    WorkflowName = " auto ",
                },
            },
        });
        var blankResult = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "actor",
                DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                {
                    ActorId = "   ",
                    WorkflowName = " auto ",
                },
            },
        });

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        result.Request.Source.DefinitionActorSource.Should().Be(new WorkflowChatDefinitionActorSource("actor-1", "auto"));
        blankResult.Succeeded.Should().BeFalse();
        blankResult.Error.Should().Be(WorkflowChatRunStartError.AgentNotFound);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldPreferTypedDefinitionActorSubmessageOverLegacyWorkflowNameAlias()
    {
        var result = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "actor",
                WorkflowName = "legacy-workflow",
                DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                {
                    ActorId = " typed-actor ",
                    WorkflowName = " typed-workflow ",
                },
            },
        });

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        result.Request.Source.DefinitionActorSource.Should()
            .Be(new WorkflowChatDefinitionActorSource("typed-actor", "typed-workflow"));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldPreserveTypedInlineYamlSubmessageActorId()
    {
        var result = ChatRunRequestNormalizer.Normalize(new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "inline-yaml-bundle",
                InlineBundle = new WorkflowChatInlineYamlBundleSourceInput
                {
                    EntryName = " auto ",
                    ActorId = " source-actor-1 ",
                    YamlDocuments =
                    [
                        new WorkflowChatInlineYamlDocumentInput
                        {
                            Name = "auto",
                            Yaml = "name: auto",
                        },
                    ],
                },
            },
        });

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Should().BeEquivalentTo(
            WorkflowChatSource.InlineYamlBundle(
                "auto",
                [new WorkflowChatInlineYamlDocument("auto", "name: auto")],
                "source-actor-1"));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNormalizeTopLevelInlineYamlWithoutActorAuthority()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Workflow = " auto ",
            WorkflowYamls = ["name: auto"],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Should().BeEquivalentTo(
            WorkflowChatSource.InlineYamlBundle(["name: auto"], "auto"));
        result.Request.Source.ActorId.Should().BeNull();
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNormalizeTopLevelWorkflowAsCatalogSource()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Workflow = " direct ",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Should().BeEquivalentTo(WorkflowChatSource.CatalogWorkflow("direct"));
        result.Request.Source.WorkflowName.Should().Be("direct");
        result.Request.Source.ActorId.Should().BeNull();
    }

    [Theory]
    [InlineData("catalog", WorkflowChatSourceKind.CatalogWorkflow, "auto", null, null)]
    [InlineData("workflow", WorkflowChatSourceKind.CatalogWorkflow, "auto", null, null)]
    [InlineData("direct", WorkflowChatSourceKind.Direct, null, null, null)]
    public void ChatRunRequestNormalizer_ShouldNormalizeTypedSourceAliases(
        string kind,
        WorkflowChatSourceKind expectedKind,
        string? workflowName,
        string? expectedActorId,
        string[]? workflowYamls)
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = kind,
                WorkflowName = workflowName,
                WorkflowYamls = workflowYamls ?? [],
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Source!.Kind.Should().Be(expectedKind);
        result.Request.Source.WorkflowName.Should().Be(workflowName);
        result.Request.Source.ActorId.Should().Be(expectedActorId);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNormalizeDefinitionActorFromTypedSubmessageOnly()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = "actor",
                DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                {
                    ActorId = " actor-1 ",
                    WorkflowName = " auto ",
                },
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        result.Request.Source.ActorId.Should().Be("actor-1");
        result.Request.Source.WorkflowName.Should().Be("auto");
    }

    [Theory]
    [InlineData("catalog", null, WorkflowChatRunStartError.WorkflowNotFound)]
    [InlineData("actor", "auto", WorkflowChatRunStartError.AgentNotFound)]
    [InlineData("inline-yaml", "auto", WorkflowChatRunStartError.InvalidWorkflowYaml)]
    [InlineData("unknown", "auto", WorkflowChatRunStartError.InvalidWorkflowYaml)]
    public void ChatRunRequestNormalizer_ShouldRejectInvalidTypedSources(
        string kind,
        string? workflowName,
        WorkflowChatRunStartError expectedError)
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Source = new WorkflowChatSourceInput
            {
                Kind = kind,
                WorkflowName = workflowName,
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(expectedError);
    }

    [Theory]
    [InlineData("""{"prompt":"hello","agentId":"actor-1"}""")]
    [InlineData("""{"prompt":"hello","source":{"kind":"actor","actorId":"actor-1"}}""")]
    public void ChatJsonContract_ShouldRejectDeletedActorAuthorityFields(string json)
    {
        var act = () => JsonSerializer.Deserialize<ChatInput>(json, ChatWebSocketProtocol.JsonOptions);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldDerivePromptAndInputParts_FromMultimodalInput()
    {
        var input = new ChatInput
        {
            InputParts =
            [
                new ChatInputContentPart
                {
                    Type = "text",
                    Text = "describe this",
                },
                new ChatInputContentPart
                {
                    Type = "image",
                    Uri = "https://example.com/cat.png",
                    MediaType = "image/png",
                    Name = "cat",
                },
            ],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request.Should().BeEquivalentTo(
            new WorkflowChatRunRequest(
                "describe this",
                WorkflowChatSource.Direct(), Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive,
                InputParts:
                [
                    new WorkflowChatInputPart
                    {
                        Kind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Text,
                        Text = "describe this",
                    },
                    new WorkflowChatInputPart
                    {
                        Kind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Image,
                        Uri = "https://example.com/cat.png",
                        MediaType = "image/png",
                        Name = "cat",
                    },
                ],
                Metadata: new Dictionary<string, string>(),
                LlmControl: null));
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldAcceptFileInlineFileWithMatchingSizeBytes()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "name": "hello.png",
                    "sizeBytes": 5
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().Be("[image]");
        result.Request.InputParts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new WorkflowChatInputPart
            {
                Kind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Image,
                DataBase64 = "aGVsbG8=",
                MediaType = "image/png",
                Name = "hello.png",
            });
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldAcceptFileInlineFileWithoutSizeBytes()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "name": "hello.png"
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.InputParts.Should().ContainSingle()
            .Which.DataBase64.Should().Be("aGVsbG8=");
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNormalizeFileRefInputPart()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "fileRef": {
                    "uri": "artifact://file-1",
                    "mediaType": "image/png",
                    "name": "hello.png"
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().Be("[image]");
        result.Request.InputParts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new WorkflowChatInputPart
            {
                Kind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Image,
                Uri = "artifact://file-1",
                MediaType = "image/png",
                Name = "hello.png",
                FileRef = new ApplicationFileArtifactRef
                {
                    ArtifactId = "artifact://file-1",
                    MediaType = "image/png",
                    FileName = "hello.png",
                },
            });
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNormalizeTypedFileRefDescriptor()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "fileRef": {
                    "fileId": "file-1",
                    "artifactId": "artifact-1",
                    "sourceKind": "connected_service_resource",
                    "sourceMessageId": "om_1",
                    "sourceResourceKey": "image_key_1",
                    "fileName": "invoice.png",
                    "mediaType": "image/png",
                    "createdAtUnixMs": 1710000000000,
                    "expiresAtUnixMs": 1710003600000,
                    "sha256": "abc",
                    "ownerRunId": "run-1",
                    "ownerScopeId": "scope-1"
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        var part = result.Request!.InputParts.Should().ContainSingle().Which;
        part.Uri.Should().Be("artifact-1");
        part.MediaType.Should().Be("image/png");
        part.Name.Should().Be("invoice.png");
        part.FileRef.Should().BeEquivalentTo(new ApplicationFileArtifactRef
        {
            FileId = "file-1",
            ArtifactId = "artifact-1",
            SourceKind = ApplicationFileArtifactSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = "image_key_1",
            FileName = "invoice.png",
            MediaType = "image/png",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
            Sha256 = "abc",
            OwnerRunId = "run-1",
            OwnerScopeId = "scope-1",
        });
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectFileRefWithoutStableIdentity()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "fileRef": {
                    "mediaType": "image/png",
                    "name": "hello.png"
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidFileInput);
    }

    [Theory]
    [InlineData("""{ "fileId": "file-1", "sourceKind": "not-a-source" }""")]
    [InlineData("""{ "fileId": "file-1", "createdAtUnixMs": -1 }""")]
    [InlineData("""{ "fileId": "file-1", "createdAtUnixMs": 1710003600000, "expiresAtUnixMs": 1710000000000 }""")]
    public void ChatRunRequestNormalizer_ShouldRejectInvalidFileRefDescriptor(string fileRefJson)
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            $$"""
            {
              "inputParts": [
                {
                  "type": "image",
                  "fileRef": {{fileRefJson}}
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidFileInput);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldAcceptInlineFileBase64WithWhitespace()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aG Vs\nbG8=",
                    "mediaType": "image/png",
                    "sizeBytes": 5
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.InputParts.Should().ContainSingle()
            .Which.DataBase64.Should().Be("aG Vs\nbG8=");
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectFilePartWithBothInlineFileAndFileRef()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "prompt": "describe this",
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png"
                  },
                  "fileRef": {
                    "uri": "artifact://file-1",
                    "mediaType": "image/png"
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidFileInput);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectInlineFileWithoutDataBase64()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "prompt": "describe this",
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "mediaType": "image/png"
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidFileInput);
    }

    [Theory]
    [InlineData("aGVsbG8=AAAA")]
    [InlineData("aGVsbG8!")]
    [InlineData("====")]
    [InlineData("abc")]
    public void ChatRunRequestNormalizer_ShouldRejectInlineFileWithInvalidBase64(string dataBase64)
    {
        var input = new ChatInput
        {
            Prompt = "describe this",
            InputParts =
            [
                new ChatInputContentPart
                {
                    Type = "image",
                    InlineFile = new ChatInputInlineFile
                    {
                        DataBase64 = dataBase64,
                        MediaType = "image/png",
                    },
                },
            ],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidFileInput);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectFileInlineFileWithMismatchedSizeBytes()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "sizeBytes": 6
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidFileInput);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectFileInlineFileWithNegativeSizeBytes()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "sizeBytes": -1
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidFileInput);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectUnsupportedInputPartWithInvalidInlineFileSizeBytes()
    {
        var input = JsonSerializer.Deserialize<ChatInput>(
            """
            {
              "prompt": "describe this",
              "inputParts": [
                {
                  "type": "unsupported",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "sizeBytes": -1
                  }
                }
              ]
            }
            """,
            ChatWebSocketProtocol.JsonOptions)!;

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidFileInput);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectFileRefSizeBytes()
    {
        const string json = """
            {
              "inputParts": [
                {
                  "type": "image",
                  "fileRef": {
                    "uri": "artifact://file-1",
                    "mediaType": "image/png",
                    "name": "hello.png",
                    "sizeBytes": 5
                  }
                }
              ]
            }
            """;

        var act = () => JsonSerializer.Deserialize<ChatInput>(json, ChatWebSocketProtocol.JsonOptions);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectInlineFileUnmappedJsonMembers()
    {
        const string json = """
            {
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "checksum": "sha256:abc"
                  }
                }
              ]
            }
            """;

        var act = () => JsonSerializer.Deserialize<ChatInput>(json, ChatWebSocketProtocol.JsonOptions);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectTopLevelFileSizeBytes()
    {
        const string json = """
            {
              "inputParts": [
                {
                  "type": "image",
                  "dataBase64": "aGVsbG8=",
                  "mediaType": "image/png",
                  "sizeBytes": 5
                }
              ]
            }
            """;

        var act = () => JsonSerializer.Deserialize<ChatInput>(json, ChatWebSocketProtocol.JsonOptions);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldDerivePlaceholderPrompt_FromMediaOnlyInput()
    {
        var input = new ChatInput
        {
            InputParts =
            [
                new ChatInputContentPart
                {
                    Type = "image",
                    Uri = "https://example.com/cat.png",
                },
                new ChatInputContentPart
                {
                    Type = "audio",
                    Uri = "https://example.com/cat.wav",
                },
            ],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.Prompt.Should().Be("[image], [audio]");
        result.Request.InputParts.Should().HaveCount(2);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectBlankPrompt_WhenNoMultimodalInput()
    {
        var input = new ChatInput
        {
            Prompt = "   ",
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.PromptRequired);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectUnsupportedOnlyInputParts_WhenPromptMissing()
    {
        var input = new ChatInput
        {
            InputParts =
            [
                new ChatInputContentPart
                {
                    Type = "foo",
                },
            ],
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.PromptRequired);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldUseTypedScopeId()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            ScopeId = " user-1 ",
            Metadata = new Dictionary<string, string>
            {
                ["trace"] = "trace-1",
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("user-1");
        result.Request.Metadata.Should().Contain("trace", "trace-1");
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldIgnoreScopeMetadata_WhenTypedScopeIdIsSet()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            ScopeId = "typed-scope",
            Metadata = new Dictionary<string, string>
            {
                ["Scope_Id"] = "metadata-scope",
                ["WORKFLOW.SCOPE_ID"] = "metadata-scope",
                ["trace"] = "trace-1",
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().Be("typed-scope");
        result.Request.Metadata.Should().NotContainKey("Scope_Id");
        result.Request.Metadata.Should().NotContainKey("WORKFLOW.SCOPE_ID");
        result.Request.Metadata.Should().Contain("trace", "trace-1");
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldNotPromoteScopeMetadata_WhenTypedScopeIdIsMissing()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Metadata = new Dictionary<string, string>
            {
                ["scope_id"] = "metadata-scope",
                [WorkflowRunCommandMetadataKeys.ScopeId] = "metadata-scope",
            },
        };

        var result = ChatRunRequestNormalizer.Normalize(input);

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().BeNull();
        result.Request.Metadata.Should().NotContainKey("scope_id");
        result.Request.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldIgnoreScopeMetadata_FromDefaults()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
        };
        var defaultMetadata = new Dictionary<string, string>
        {
            ["scope_id"] = "default-scope",
            [WorkflowRunCommandMetadataKeys.ScopeId] = "default-scope",
            ["trace"] = "trace-1",
        };

        var result = ChatRunRequestNormalizer.Normalize(input, defaultMetadata: defaultMetadata);

        result.Succeeded.Should().BeTrue();
        result.Request!.ScopeId.Should().BeNull();
        result.Request.Metadata.Should().NotContainKey("scope_id");
        result.Request.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        result.Request.Metadata.Should().Contain("trace", "trace-1");
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldScrubConnectorAuthorizationMetadata_AndUseTrustedCallerCredential()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
            Metadata = new Dictionary<string, string>
            {
                ["connector.http.authorization"] = "Bearer untrusted",
                ["trace"] = "trace-1",
            },
        };
        var defaultMetadata = new Dictionary<string, string>
        {
            ["connector.http.authorization"] = "Bearer default",
        };

        var result = ChatRunRequestNormalizer.Normalize(
            input,
            defaultMetadata: defaultMetadata,
            trustedCallerCredential: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential(" trusted "));

        result.Succeeded.Should().BeTrue();
        result.Request!.CallerCredential!.BearerToken.Should().Be("trusted");
        result.Request.Metadata.Should().Contain("trace", "trace-1");
        result.Request.Metadata.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public void ChatRunRequestNormalizer_ShouldRejectMalformedTrustedCallerCredential()
    {
        var input = new ChatInput
        {
            Prompt = "hello",
        };

        var result = ChatRunRequestNormalizer.Normalize(
            input,
            trustedCallerCredential: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential("Bearer trusted"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(WorkflowChatRunStartError.InvalidCallerCredential);
    }

    [Fact]
    public void CapabilityTraceContext_CreateAcceptedPayload_ShouldUseReceiptValues()
    {
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1");

        var payload = CapabilityTraceContext.CreateAcceptedPayload(receipt);

        payload.CommandId.Should().Be("cmd-1");
        payload.CorrelationId.Should().Be("corr-1");
        payload.ActorId.Should().Be("actor-1");
    }

    [Fact]
    public void CapabilityTraceContext_ResolveCorrelationId_ShouldFallbackToCommandId()
    {
        var correlationId = CapabilityTraceContext.ResolveCorrelationId("", "cmd-1");

        correlationId.Should().Be("cmd-1");
    }
}
