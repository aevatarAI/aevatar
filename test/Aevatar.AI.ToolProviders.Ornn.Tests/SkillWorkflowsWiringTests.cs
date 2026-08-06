using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class SkillWorkflowsWiringTests
{
    [Fact]
    public async Task OrnnRemoteSkillFetcher_ExtractsWorkflowsAndStripsThemFromAssociatedFiles()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translator",
                "description": "Translates",
                "files": {
                  "SKILL.md": "# Translator\nUse this.",
                  "workflows/translate.yaml": "name: translate_flow\ndescription: Translate text\nwhen_to_use: When user asks to translate\nsteps:\n  - id: do\n    type: llm_call\n",
                  "scripts/run.sh": "echo hi"
                }
              }
            }
            """);
        var client = CreateClient(handler);
        var fetcher = new OrnnRemoteSkillFetcher(client);

        var skill = await fetcher.FetchSkillAsync("token", "Translator");

        skill.Should().NotBeNull();
        skill!.Workflows.Should().ContainSingle();
        skill.Workflows![0].WorkflowId.Should().Be("translate");
        skill.Workflows[0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_flow");

        // Workflow file must not also appear in AssociatedFiles.
        skill.AssociatedFiles.Should().NotBeNull();
        skill.AssociatedFiles.Should().NotContainKey("workflows/translate.yaml");
        skill.AssociatedFiles.Should().ContainKey("scripts/run.sh");
    }

    [Fact]
    public async Task OrnnRemoteSkillFetcher_FallsBackToAssetsYamlWhenNoWorkflowsDir()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translator",
                "files": {
                  "SKILL.md": "# Translator",
                  "assets/translate.yaml": "name: translate_asset\nsteps:\n  - id: do\n",
                  "assets/prompt.txt": "raw"
                }
              }
            }
            """);
        var client = CreateClient(handler);
        var fetcher = new OrnnRemoteSkillFetcher(client);

        var skill = await fetcher.FetchSkillAsync("token", "Translator");

        skill!.Workflows.Should().ContainSingle(w => w.WorkflowId == "translate_asset");
        skill.Workflows![0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_asset");
        skill.AssociatedFiles.Should().NotContainKey("assets/translate.yaml");
        skill.AssociatedFiles.Should().ContainKey("assets/prompt.txt");
    }

    [Fact]
    public async Task OrnnRemoteSkillFetcher_ReadsSkillMarkdownCaseInsensitively()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translator",
                "files": {
                  "skill.md": "---\nname: translator\n---\nUse lowercase file name.",
                  "workflows/translate.yaml": "name: translate_flow\nsteps:\n  - id: do\n",
                  "docs/readme.md": "reference"
                }
              }
            }
            """);
        var client = CreateClient(handler);
        var fetcher = new OrnnRemoteSkillFetcher(client);

        var skill = await fetcher.FetchSkillAsync("token", "Translator");

        skill.Should().NotBeNull();
        skill!.Name.Should().Be("translator");
        skill.Instructions.Should().Be("Use lowercase file name.");
        skill.AssociatedFiles.Should().NotContainKey("skill.md");
        skill.AssociatedFiles.Should().ContainKey("docs/readme.md");
    }

    [Fact]
    public async Task UseSkillTool_RendersUnMountedWorkflowsAsTemplates()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "translate_flow",
                    WorkflowYamls =
                    [
                        "name: translate_flow\nsteps:\n  - id: do\n    type: llm_call\n",
                    ],
                },
            ],
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":false}""");
        var text = ExtractText(output);

        text.Should().Contain("## Workflow Templates");
        text.Should().Contain("templates/import sources");
        text.Should().Contain("Scope Workflow command path");
        text.Should().Contain("not runnable scope workflows by themselves");
        text.Should().Contain("workflow_yamls");
        text.Should().Contain("translate_flow");
        text.Should().Contain("```json");
        text.Should().Contain("type: llm_call");
    }

    [Fact]
    public async Task UseSkillTool_OmitsWorkflowsSectionWhenSkillHasNoWorkflows()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "plain",
            Description = "no workflows",
            Instructions = "body",
            Source = SkillSource.Local,
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"plain"}""");
        var text = ExtractText(output);

        text.Should().NotContain("## Workflow Templates");
        text.Should().NotContain("aevatar_start_workflow");
    }

    [Fact]
    public async Task UseSkillTool_RendersScriptHandoffAfterWorkflowAndBeforeAssociatedFiles()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "scripted",
            Description = "Runs script",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "prepare",
                    WorkflowYamls = ["name: prepare\nsteps: []\n"],
                },
            ],
            Scripts =
            [
                new SkillScriptDescriptor
                {
                    ScriptId = "scripted-main",
                    SourceFiles = new Dictionary<string, string>
                    {
                        ["scripts/Main.cs"] = "public sealed class MainBehavior {}",
                    },
                    ProtoFiles = new Dictionary<string, string>
                    {
                        ["scripts/contract.proto"] = "syntax = \"proto3\";",
                    },
                    EntryBehaviorTypeName = "MainBehavior",
                },
            ],
            AssociatedFiles = new Dictionary<string, string>
            {
                ["docs/readme.md"] = "reference",
            },
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"scripted"}""");
        var text = ExtractText(output);

        text.Should().Contain("## script_compile/script_execute Handoff");
        text.Should().Contain("Call `script_compile`");
        text.Should().Contain("### script_compile");
        text.Should().Contain("\"script_id\": \"scripted-main\"");
        text.Should().Contain("\"source_files\"");
        text.Should().Contain("\"scripts/Main.cs\"");
        text.Should().Contain("\"proto_files\"");
        text.Should().Contain("\"scripts/contract.proto\"");
        text.Should().Contain("\"entry_behavior_type_name\": \"MainBehavior\"");
        text.Should().Contain("### script_execute");
        text.Should().Contain("\"input\": \"Use the current user request and skill arguments.\"");

        text.IndexOf("## Workflow Templates", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("## script_compile/script_execute Handoff", StringComparison.Ordinal));
        text.IndexOf("## script_compile/script_execute Handoff", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("## Associated Files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UseSkillTool_OmitsScriptHandoffWhenSkillHasNoScripts()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "plain",
            Description = "no scripts",
            Instructions = "body",
            Source = SkillSource.Local,
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"plain"}""");
        var text = ExtractText(output);

        text.Should().NotContain("## script_compile/script_execute Handoff");
        text.Should().NotContain("script_compile");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsFalse_DoesNotRequireApprovalAndDoesNotCallMountPort()
    {
        var catalog = CreateCatalogWithWorkflowSkill();
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);

        tool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        tool.RequiresApproval("""{"skill":"translator"}""").Should().BeFalse();
        tool.RequiresApproval("""{"skill":"translator","mount_workflows":false}""").Should().BeFalse();

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":false}""");

        output.Should().Contain("## Workflow Templates");
        output.Should().Contain("templates/import sources");
        output.Should().NotContain("## Mounted Workflows");
        mountPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public void UseSkillTool_MountPreviewIsReadOnly_AndConfirmedMountRequiresApproval()
    {
        var tool = new UseSkillTool(new LocalSkillCatalog());
        const string tokenMount = """
            {
              "skill": "translator",
              "mount_workflows": true,
              "workflow_mount_confirmation_token": "sha256:alpha"
            }
            """;

        const string confirmedMount = """
            {
              "skill": "translator",
              "mount_workflows": true,
              "workflow_mount_confirmations": [
                {
                  "workflow_id": "translate_flow",
                  "revision_id": "rev-skill-alpha",
                  "workflow_bundle_digest": "sha256:alpha",
                  "explicit_requests": []
                }
              ]
            }
            """;

        tool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        tool.RequiresApproval("""{"skill":"translator","mount_workflows":true}""").Should().BeFalse();
        ((IAgentTool)tool).GetCallSafety("""{"skill":"translator","mount_workflows":true}""").Should().Be(
            new AgentToolCallSafety(
                RequiresApproval: false,
                IsReadOnly: true,
                IsDestructive: false));
        tool.RequiresApproval(tokenMount).Should().BeTrue();
        ((IAgentTool)tool).GetCallSafety(tokenMount).Should().Be(
            new AgentToolCallSafety(
                RequiresApproval: true,
                IsReadOnly: false,
                IsDestructive: false));
        tool.RequiresApproval(confirmedMount).Should().BeTrue();
        ((IAgentTool)tool).GetCallSafety(confirmedMount).Should().Be(
            new AgentToolCallSafety(
                RequiresApproval: true,
                IsReadOnly: false,
                IsDestructive: false));
        tool.ParametersSchema.Should().Contain("workflow_mount_confirmation_token");
    }

    [Fact]
    public async Task UseSkillTool_MountPreviewReceipt_ShouldRemainReadOnly()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "token-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var catalog = CreateCatalogWithWorkflowSkill();
        var tool = new UseSkillTool(catalog, workflowMountPort: new RecordingSkillWorkflowMountPort());
        const string matchingArguments = "{\"skill\":\"translator\",\"mount_workflows\":true}";
        var result = await tool.ExecuteAsync(matchingArguments);

        var receipt = tool.CreateResultReceipt("call-mount", tool.Name, matchingArguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.Effect.Should().Be(AgentToolReceiptEffect.ReadOnly);
        receipt.SideEffectKind.Should().BeEmpty();
        receipt.SubjectKind.Should().Be("ornn.skill");
        receipt.SubjectId.Should().Be("translator");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithoutScope_ReturnsErrorAndDoesNotUpsert()
    {
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = null;
            var mountPort = new RecordingSkillWorkflowMountPort();
            var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), workflowMountPort: mountPort);

            var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

            output.Should().Contain("## Mounted Workflows");
            output.Should().Contain("\"status\": \"missing_scope\"");
            output.Should().Contain("scope_id is missing from the request context");
            mountPort.Requests.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithoutMountPort_ReturnsErrorAfterLoadingSkill()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-1",
            token: "token-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill());

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        output.Should().Contain("# translator");
        output.Should().Contain("## Mounted Workflows");
        output.Should().Contain("Workflow mounting is not available in this host");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithoutCallerAuthority_ReturnsMissingIdentityAndDoesNotUpsert()
    {
        using var _ = BeginContextScope(scopeId: "scope-alpha", ownerSubject: "owner-alpha");
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), workflowMountPort: mountPort);

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        output.Should().Contain("\"status\": \"missing_identity\"");
        mountPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithNoWorkflows_ReturnsErrorAndDoesNotUpsert()
    {
        using var _ = BeginContextScope(scopeId: "scope-1");
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "plain",
            Description = "no workflows",
            Instructions = "body",
            Source = SkillSource.Local,
        });
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);

        var output = await tool.ExecuteAsync("""{"skill":"plain","mount_workflows":true}""");

        output.Should().Contain("The skill does not expose workflow YAML bundles");
        mountPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithBlankWorkflowId_ReturnsErrorAndDoesNotUpsert()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "token-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = " ",
                    WorkflowYamls = ["name: translate_flow\nsteps: []\n"],
                },
            ],
        });
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        output.Should().Contain("## Mounted Workflows");
        output.Should().Contain("\"status\": \"invalid_workflow\"");
        mountPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueWithBlankWorkflowYamls_ReturnsErrorAndDoesNotUpsert()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "token-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "translate_flow",
                    WorkflowYamls = ["", "   ", "\t"],
                },
            ],
        });
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        output.Should().Contain("## Mounted Workflows");
        output.Should().Contain("\"status\": \"invalid_workflow\"");
        mountPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task UseSkillTool_MountPreview_PassesAllSkillWorkflowsThroughMountPortWithoutMutation()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "token-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), workflowMountPort: mountPort);

        var output = await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        mountPort.Requests.Should().ContainSingle();
        mountPort.Requests[0].ScopeId.Should().Be("scope-alpha");
        mountPort.Requests[0].CallerId.Should().Be("nyx-user-alpha");
        mountPort.Requests[0].Workflows.Select(static workflow => workflow.WorkflowId)
            .Should().Equal("translate_flow", "qa_flow");
        mountPort.Requests[0].Workflows[0].WorkflowYamls.Should().Equal(
            "name: translate_flow\nsteps: []\n",
            "name: helper_flow\nsteps: []\n");
        output.Should().Contain("## Mounted Workflows");
        output.Should().Contain("\"status\": \"confirmation_required\"");
        output.Should().Contain("\"confirmation_requests\"");
    }

    [Fact]
    public async Task UseSkillTool_ConfirmedMount_PassesOpaqueConfirmationTokenWithoutMutation()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "token-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), workflowMountPort: mountPort);

        await tool.ExecuteAsync(
            """{"skill":"translator","mount_workflows":true,"workflow_mount_confirmation_token":"sha256:opaque-alpha"}""");

        mountPort.Requests.Should().ContainSingle();
        mountPort.Requests[0].ConfirmationToken.Should().Be("sha256:opaque-alpha");
        mountPort.Requests[0].Confirmations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AgentToolNyxIdCredentialKind.ProxyDelegation)]
    [InlineData(AgentToolNyxIdCredentialKind.Unspecified)]
    public async Task UseSkillTool_MountWorkflowsTrue_WhenCredentialIsNotSourceReadable_ShouldRejectBeforeMount(
        AgentToolNyxIdCredentialKind credentialKind)
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "caller-token",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha",
            credentialKind: credentialKind);
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), workflowMountPort: mountPort);

        await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        mountPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrue_WithProxyDelegation_UsesOnlySourceReadableCredential()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "proxy-delegation-alpha",
            sourceReadableToken: "source-readable-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha",
            credentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation);
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(CreateCatalogWithWorkflowSkill(), workflowMountPort: mountPort);

        await tool.ExecuteAsync("""{"skill":"translator","mount_workflows":true}""");

        mountPort.Requests.Should().ContainSingle();
        mountPort.Requests[0].SourceReadableNyxIdAccessToken.Should().Be("source-readable-alpha");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrue_PreservesDescriptorWorkflowIdForMountPort()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "token-alpha",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "packaged-skill",
            Description = "Uses packaged workflow identity",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "packaged-entry",
                    WorkflowYamls = ["name: parsed_entry\nsteps: []\n"],
                },
            ],
        });
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(catalog, workflowMountPort: mountPort);

        await tool.ExecuteAsync("""{"skill":"packaged-skill","mount_workflows":true}""");

        mountPort.Requests.Should().ContainSingle();
        mountPort.Requests[0].Workflows.Should().ContainSingle();
        mountPort.Requests[0].Workflows[0].WorkflowId.Should().Be("packaged-entry");
        mountPort.Requests[0].Workflows[0].WorkflowYamls.Should()
            .Equal("name: parsed_entry\nsteps: []\n");
    }

    [Fact]
    public async Task UseSkillTool_MountWorkflowsTrueForRemoteSkill_UsesCurrentTokenAndMountsFetchedDescriptors()
    {
        using var _ = BeginContextScope(
            scopeId: "scope-alpha",
            token: "current-token",
            ownerSubject: "owner-alpha",
            nyxIdUserId: "nyx-user-alpha");
        var fetcher = new RecordingRemoteSkillFetcher(new SkillDefinition
        {
            Name = "remote-translator",
            Description = "remote skill",
            Instructions = "remote body",
            Source = SkillSource.Remote,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "remote_flow",
                    WorkflowYamls = ["name: remote_flow\nsteps: []\n"],
                },
            ],
        });
        var mountPort = new RecordingSkillWorkflowMountPort();
        var tool = new UseSkillTool(
            new LocalSkillCatalog(),
            fetcher,
            workflowMountPort: mountPort);

        var output = await tool.ExecuteAsync("""{"skill":"remote-translator","mount_workflows":true}""");

        fetcher.Requests.Should().ContainSingle().Which.Should().Be(("current-token", "remote-translator"));
        mountPort.Requests.Should().ContainSingle();
        mountPort.Requests[0].Workflows.Should().ContainSingle()
            .Which.WorkflowId.Should().Be("remote_flow");
        output.Should().Contain("# remote-translator");
        output.Should().Contain("\"workflow_id\": \"remote_flow\"");
    }

    [Fact]
    public void SkillDiscovery_PicksUpWorkflowsFromSkillDirectory()
    {
        using var tempDir = new TempDirectory();
        var skillDir = Path.Combine(tempDir.Path, "translator");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: translator
            description: Translates
            ---
            Body
            """);
        var workflowsDir = Path.Combine(skillDir, "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(Path.Combine(workflowsDir, "translate.yaml"), """
            name: translate_flow
            description: Run translation
            when_to_use: User asks to translate
            steps:
              - id: do
                type: llm_call
            """);

        var skills = new SkillDiscovery().ScanDirectory(tempDir.Path);

        skills.Should().ContainSingle();
        skills[0].Name.Should().Be("translator");
        skills[0].Workflows.Should().ContainSingle();
        skills[0].Workflows![0].WorkflowId.Should().Be("translate_flow");
        skills[0].Workflows![0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_flow");
    }

    private static OrnnSkillClient CreateClient(OrnnTestHttpMessageHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var options = new OrnnOptions { NyxIdSlug = "ornn" };
        return new OrnnSkillClient(options, nyxClient);
    }

    private static LocalSkillCatalog CreateCatalogWithWorkflowSkill()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "translate_flow",
                    WorkflowYamls =
                    [
                        "name: translate_flow\nsteps: []\n",
                        "name: helper_flow\nsteps: []\n",
                    ],
                },
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "qa_flow",
                    WorkflowYamls = ["name: qa_flow\nsteps: []\n"],
                },
            ],
        });
        return catalog;
    }

    private static AgentToolRequestContextScope BeginContextScope(
        string? scopeId = null,
        string? token = null,
        string? sourceReadableToken = null,
        string? ownerSubject = null,
        string? nyxIdUserId = null,
        AgentToolNyxIdCredentialKind credentialKind = AgentToolNyxIdCredentialKind.SourceReadableUserBearer)
    {
        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(scopeId))
            metadata[LLMRequestMetadataKeys.ScopeId] = scopeId;
        if (!string.IsNullOrWhiteSpace(token))
            metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = token;
        if (!string.IsNullOrWhiteSpace(ownerSubject))
            metadata[LLMRequestMetadataKeys.OwnerSubject] = ownerSubject;
        var context = global::TestAgentToolContexts.FromMetadata(metadata);
        context = context with
        {
            Credentials = context.Credentials with
            {
                NyxIdCredentialKind = credentialKind,
                SourceReadableNyxIdAccessToken = sourceReadableToken,
            },
        };
        if (!string.IsNullOrWhiteSpace(nyxIdUserId))
        {
            context = context with
            {
                NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                    "nyxid",
                    "tenant-alpha",
                    nyxIdUserId.Trim()),
            };
        }

        return new AgentToolRequestContextScope(context);
    }

    private sealed class AgentToolRequestContextScope : IDisposable
    {
        private readonly AgentToolExecutionContext? _previous;

        public AgentToolRequestContextScope(AgentToolExecutionContext context)
        {
            _previous = AgentToolRequestContext.Current;
            AgentToolRequestContext.Current = context;
        }

        public void Dispose() => AgentToolRequestContext.Current = _previous;
    }

    private sealed class RecordingRemoteSkillFetcher(SkillDefinition skill) : IRemoteSkillFetcher
    {
        public List<(string AccessToken, string NameOrId)> Requests { get; } = [];

        public Task<SkillDefinition?> FetchSkillAsync(string accessToken, string nameOrId, CancellationToken ct = default)
        {
            Requests.Add((accessToken, nameOrId));
            return Task.FromResult<SkillDefinition?>(skill);
        }
    }

    private sealed class RecordingSkillWorkflowMountPort : ISkillWorkflowMountPort
    {
        public List<SkillWorkflowMountRequest> Requests { get; } = [];

        public Task<SkillWorkflowMountResult> MountAsync(
            SkillWorkflowMountRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (request.Workflows.Any(static workflow => string.IsNullOrWhiteSpace(workflow.WorkflowId)) ||
                request.Workflows.Any(static workflow =>
                    workflow.WorkflowYamls.Count == 0 ||
                    workflow.WorkflowYamls.All(string.IsNullOrWhiteSpace)))
            {
                return Task.FromResult(new SkillWorkflowMountResult(
                    "invalid_workflow",
                    false,
                    [],
                    "The skill workflow bundle is invalid.",
                    FailureCode: "USE_SKILL_MOUNT_INVALID_WORKFLOW"));
            }

            var confirmations = request.Workflows.Select(workflow =>
                new SkillWorkflowMountConfirmation(
                    workflow.WorkflowId,
                    $"rev-{workflow.WorkflowId}",
                    $"sha256:{workflow.WorkflowId}",
                    [])).ToArray();
            return Task.FromResult(new SkillWorkflowMountResult(
                "confirmation_required",
                false,
                [],
                "Review before mounting.",
                confirmations.Select(confirmation => new SkillWorkflowMountPreview(
                    confirmation.WorkflowId,
                    confirmation.RevisionId,
                    confirmation.WorkflowBundleDigest,
                    [],
                    confirmation)).ToArray()));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static string ExtractText(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }
}
