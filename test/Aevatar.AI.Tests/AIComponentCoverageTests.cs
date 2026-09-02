using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.ClientModel.Primitives;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Prompt;
using Aevatar.AI.Core.Secrets;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.LLMProviders.Tornado;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
using LlmTornado.Chat;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using Microsoft.Extensions.AI;

using AevatarChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Aevatar.AI.Tests;

public class AIComponentCoverageTests
{
    [Fact]
    public void ChatHistory_ShouldTruncateExportImportAndBuildMessages()
    {
        var history = new ChatHistory { MaxMessages = 2 };
        history.Add(new AevatarChatMessage { Role = "user", Content = "u1" });
        history.AddRange([
            new AevatarChatMessage { Role = "assistant", Content = "a1" },
            new AevatarChatMessage { Role = "user", Content = "u2" },
        ]);

        history.Count.Should().Be(2);
        history.Messages.Select(x => x.Content).Should().Equal("a1", "u2");

        var llmMessages = history.BuildMessages("system");
        llmMessages.Should().HaveCount(3);
        llmMessages[0].Role.Should().Be("system");

        var exported = history.Export();
        exported.Should().HaveCount(2);
        exported[0].Role.Should().Be("assistant");

        var imported = new ChatHistory();
        imported.Import(exported);
        imported.Messages.Select(x => x.Content).Should().Equal("a1", "u2");

        imported.Clear();
        imported.Count.Should().Be(0);
    }

    [Fact]
    public void ChatHistory_ShouldPreserveReasoningContentOnExportImport()
    {
        var history = new ChatHistory();
        history.Add(new AevatarChatMessage { Role = "assistant", Content = "hello", ReasoningContent = "thinking..." });
        history.Add(new AevatarChatMessage { Role = "user", Content = "follow-up" });

        var exported = history.Export();
        exported[0].ReasoningContent.Should().Be("thinking...");
        exported[1].ReasoningContent.Should().BeNull();

        var imported = new ChatHistory();
        imported.Import(exported);
        imported.Messages[0].ReasoningContent.Should().Be("thinking...");
        imported.Messages[1].ReasoningContent.Should().BeNull();
    }

    [Fact]
    public async Task MEAILLMProvider_ConvertMessages_ShouldIncludeReasoningContent()
    {
        IReadOnlyList<MeaiChatMessage>? capturedMessages = null;
        var client = new StubChatClient
        {
            OnGetStreamingResponse = (messages, _, _) =>
            {
                capturedMessages = messages.ToList();
                return Stream(["ok"]);
            },
        };

        var provider = new MEAILLMProvider("meai-reasoning-outbound", client);
        await foreach (var _ in provider.ChatStreamAsync(new LLMRequest
        {
            Messages =
            [
                new AevatarChatMessage { Role = "user", Content = "hi" },
                new AevatarChatMessage { Role = "assistant", Content = "thought", ReasoningContent = "reasoning-text" },
            ],
        }))
        {
        }

        capturedMessages.Should().NotBeNull();
        capturedMessages!.Should().HaveCount(2);
        var assistantMsg = capturedMessages[1];
        assistantMsg.Role.Should().Be(ChatRole.Assistant);
        assistantMsg.Contents.OfType<TextReasoningContent>().Should().ContainSingle()
            .Which.Text.Should().Be("reasoning-text");
        assistantMsg.RawRepresentation.Should().BeAssignableTo<OpenAI.Chat.AssistantChatMessage>();

        var openAiMessages = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(capturedMessages!)
            .ToList();
        var assistantJson = ModelReaderWriter.Write(openAiMessages[1]).ToString();
        assistantJson.Should().Contain("\"reasoning_content\":\"reasoning-text\"");
    }

    [Fact]
    public async Task MEAILLMProvider_ConvertMessages_ShouldIncludeReasoningContentWithToolCalls()
    {
        IReadOnlyList<MeaiChatMessage>? capturedMessages = null;
        var client = new StubChatClient
        {
            OnGetStreamingResponse = (messages, _, _) =>
            {
                capturedMessages = messages.ToList();
                return Stream(["ok"]);
            },
        };

        var provider = new MEAILLMProvider("meai-reasoning-tools", client);
        await foreach (var _ in provider.ChatStreamAsync(new LLMRequest
        {
            Messages =
            [
                new AevatarChatMessage { Role = "user", Content = "hi" },
                new AevatarChatMessage
                {
                    Role = "assistant",
                    Content = "using tool",
                    ReasoningContent = "thinking about tools",
                    ToolCalls =
                    [
                        new Aevatar.AI.Abstractions.LLMProviders.ToolCall
                        {
                            Id = "tc1", Name = "search", ArgumentsJson = "{}",
                        },
                    ],
                },
                new AevatarChatMessage { Role = "tool", ToolCallId = "tc1", Content = "result" },
            ],
        }))
        {
        }

        capturedMessages.Should().NotBeNull();
        var assistantWithTools = capturedMessages![1];
        assistantWithTools.Role.Should().Be(ChatRole.Assistant);
        assistantWithTools.Contents.OfType<TextReasoningContent>().Should().ContainSingle()
            .Which.Text.Should().Be("thinking about tools");
        assistantWithTools.Contents.OfType<FunctionCallContent>().Should().ContainSingle();
        assistantWithTools.RawRepresentation.Should().BeAssignableTo<OpenAI.Chat.AssistantChatMessage>();

        var openAiMessages = OpenAI.Chat.MicrosoftExtensionsAIChatExtensions
            .AsOpenAIChatMessages(capturedMessages!)
            .ToList();
        var assistantJson = ModelReaderWriter.Write(openAiMessages[1]).ToString();
        assistantJson.Should().Contain("\"reasoning_content\":\"thinking about tools\"");
        assistantJson.Should().Contain("\"tool_calls\"");
        assistantJson.Should().Contain("\"name\":\"search\"");
    }

    [Fact]
    public async Task MEAILLMProvider_ConvertMessages_ShouldSkipRawReasoningPatchForEmptyAssistantContent()
    {
        IReadOnlyList<MeaiChatMessage>? capturedMessages = null;
        var client = new StubChatClient
        {
            OnGetStreamingResponse = (messages, _, _) =>
            {
                capturedMessages = messages.ToList();
                return Stream(["ok"]);
            },
        };

        var provider = new MEAILLMProvider("meai-reasoning-empty-assistant", client);
        await foreach (var _ in provider.ChatStreamAsync(new LLMRequest
        {
            Messages =
            [
                new AevatarChatMessage { Role = "user", Content = "hi" },
                new AevatarChatMessage { Role = "assistant", Content = string.Empty, ReasoningContent = "thinking-only" },
            ],
        }))
        {
        }

        capturedMessages.Should().NotBeNull();
        var assistantMsg = capturedMessages![1];
        assistantMsg.Contents.OfType<TextReasoningContent>().Should().ContainSingle()
            .Which.Text.Should().Be("thinking-only");
        assistantMsg.RawRepresentation.Should().BeNull();
    }

    [Fact]
    public async Task MEAILLMProvider_ChatStreamAsync_ShouldExtractReasoningDeltaFromOpenAIRawPatch()
    {
        var handler = new CapturingHttpHandler(_ =>
        {
            var responseContent = string.Join("\n\n",
                """
                data: {"id":"x","object":"chat.completion.chunk","created":0,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"thinking from raw patch"},"finish_reason":null}]}
                """,
                """
                data: {"id":"x","object":"chat.completion.chunk","created":0,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"content":"final"},"finish_reason":"stop"}]}
                """,
                "data: [DONE]",
                string.Empty);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent, System.Text.Encoding.UTF8, "text/event-stream"),
            });
        });

        var clientOptions = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://test.example.com/v1/"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var openAiClient = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential("test-key"), clientOptions);
        var provider = new MEAILLMProvider("deepseek", openAiClient.GetChatClient("deepseek-v4-pro").AsIChatClient());

        var chunks = new List<LLMStreamChunk>();
        await foreach (var chunk in provider.ChatStreamAsync(new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
        }))
        {
            chunks.Add(chunk);
        }

        chunks.Select(chunk => chunk.DeltaReasoningContent)
            .Should().Contain("thinking from raw patch");
        chunks.Select(chunk => chunk.DeltaContent).Should().Contain("final");
    }

    [Fact]
    public void PromptTemplate_Render_ShouldApplyDefaultsAndRuntimeAndExamples()
    {
        var template = new PromptTemplate
        {
            Content = "Hello {{name}} {{title}} {{topic}}",
            Defaults = new Dictionary<string, string>
            {
                ["name"] = "Alice",
                ["title"] = "Engineer",
            },
            Examples =
            [
                new PromptExample { Input = "Q1", Output = "A1" },
            ],
        };

        var rendered = template.Render(new Dictionary<string, string> { ["name"] = "Bob" });

        rendered.Should().Contain("Hello Alice Engineer {{topic}}");
        rendered.Should().Contain("## Examples");
        rendered.Should().Contain("User: Q1");
        rendered.Should().Contain("Assistant: A1");

        var renderedWithTopic = template.Render(new Dictionary<string, string>
        {
            ["topic"] = "AI",
        });
        renderedWithTopic.Should().Contain("Hello Alice Engineer AI");
    }

    [Fact]
    public void SecretManager_ShouldSetResolveLoadAndExpand()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"secret-manager-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var secretsFile = Path.Combine(tempDir, "secrets.json");
        File.WriteAllText(secretsFile, "{\"FILE_KEY\":\"file-value\"}");

        var previousFromEnv = Environment.GetEnvironmentVariable("AEVATAR_TEST_ENV_KEY");
        var previousResolveEnv = Environment.GetEnvironmentVariable("DIRECT_ENV_KEY");
        var previousTemp = Environment.GetEnvironmentVariable("AEVATAR_TEST_SECRETS_PATH");

        try
        {
            Environment.SetEnvironmentVariable("AEVATAR_TEST_ENV_KEY", "env-value");
            Environment.SetEnvironmentVariable("DIRECT_ENV_KEY", "direct-value");
            Environment.SetEnvironmentVariable("AEVATAR_TEST_SECRETS_PATH", secretsFile);

            var manager = new SecretManager()
                .Set("LOCAL_KEY", "local-value")
                .LoadFromEnvironment("AEVATAR_")
                .LoadFromFile("%AEVATAR_TEST_SECRETS_PATH%");

            manager.Get("LOCAL_KEY").Should().Be("local-value");
            manager.Get("AEVATAR_TEST_ENV_KEY").Should().Be("env-value");
            manager.Get("FILE_KEY").Should().Be("file-value");
            manager.Get("DIRECT_ENV_KEY").Should().Be("direct-value");

            manager.Resolve("x=${LOCAL_KEY}, y=${DIRECT_ENV_KEY}, z=${MISSING}")
                .Should().Be("x=local-value, y=direct-value, z=${MISSING}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AEVATAR_TEST_ENV_KEY", previousFromEnv);
            Environment.SetEnvironmentVariable("DIRECT_ENV_KEY", previousResolveEnv);
            Environment.SetEnvironmentVariable("AEVATAR_TEST_SECRETS_PATH", previousTemp);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MEAILLMProvider_ShouldMapChatAndStreamingResponses()
    {
        ChatOptions? capturedOptions = null;
        IReadOnlyList<MeaiChatMessage>? capturedMessages = null;
        ChatOptions? capturedStreamingOptions = null;

        var client = new StubChatClient
        {
            OnGetStreamingResponse = (messages, options, _) =>
            {
                capturedMessages = messages.ToList();
                capturedOptions = options;

                return Stream([
                    new ChatResponseUpdate(ChatRole.Assistant, "hello"),
                    new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent("call-1", "calc", new Dictionary<string, object?>
                            {
                                ["x"] = 1,
                            }),
                        ]),
                ]);
            },
        };

        var provider = new MEAILLMProvider("meai", client, toolExecutionPort: TestAgentToolExecutionPort.Instance);

        var tool = new StubTool("search");
        var response = await ChatStreamContentAggregator.AggregateResponseAsync(provider, new LLMRequest
        {
            RequestId = "session-1",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow.run_id"] = "run-1",
            },
            Model = "demo-model",
            Temperature = 0.7,
            MaxTokens = 42,
            Messages =
            [
                new AevatarChatMessage
                {
                    Role = "assistant",
                    Content = "tool call",
                    ToolCalls =
                    [
                        new Aevatar.AI.Abstractions.LLMProviders.ToolCall
                        {
                            Id = "tc1",
                            Name = "search",
                            ArgumentsJson = "{\"q\":\"a\"}",
                        },
                    ],
                },
                new AevatarChatMessage { Role = "tool", ToolCallId = "tc1", Content = "{\"ok\":true}" },
            ],
            Tools = [tool],
        });

        response.Content.Should().Be("hello");
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls![0].Id.Should().Be("call-1");
        response.ToolCalls[0].Name.Should().Be("calc");

        capturedMessages.Should().NotBeNull();
        capturedMessages!.Should().HaveCount(2);
        capturedMessages[0].Role.Should().Be(ChatRole.Assistant);
        capturedMessages[1].Role.Should().Be(ChatRole.Tool);
        capturedOptions.Should().NotBeNull();
        capturedOptions!.ModelId.Should().Be("demo-model");
        capturedOptions.ConversationId.Should().Be("session-1");
        capturedOptions.AdditionalProperties.Should().NotBeNull();
        capturedOptions.AdditionalProperties![LLMRequestMetadataKeys.RequestId].Should().Be("session-1");
        capturedOptions.AdditionalProperties["workflow.run_id"].Should().Be("run-1");
        capturedOptions.MaxOutputTokens.Should().Be(42);
        capturedOptions.Tools.Should().NotBeNull();
        capturedOptions.Tools.Should().ContainSingle();

        client = new StubChatClient
        {
            OnGetStreamingResponse = (_, options, _) =>
            {
                capturedStreamingOptions = options;
                return Stream(["a", "b"]);
            },
        };
        provider = new MEAILLMProvider("meai", client);

        var chunks = new List<LLMStreamChunk>();
        await foreach (var chunk in provider.ChatStreamAsync(new LLMRequest
        {
            RequestId = "session-stream",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow.step_id"] = "llm-1",
            },
            Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
        }))
        {
            chunks.Add(chunk);
        }

        chunks.Select(x => x.DeltaContent).Should().ContainInOrder("a", "b");
        chunks.Last().IsLast.Should().BeTrue();
        capturedStreamingOptions.Should().NotBeNull();
        capturedStreamingOptions!.ConversationId.Should().Be("session-stream");
        capturedStreamingOptions.AdditionalProperties.Should().NotBeNull();
        capturedStreamingOptions.AdditionalProperties![LLMRequestMetadataKeys.RequestId].Should().Be("session-stream");
        capturedStreamingOptions.AdditionalProperties["workflow.step_id"].Should().Be("llm-1");

        var nonStreamingFallbackCalls = 0;
        var emptyStreamClient = new StubChatClient
        {
            OnGetStreamingResponse = static (_, _, _) => EmptyChatStream(),
            OnGetResponse = (_, _, _) =>
            {
                nonStreamingFallbackCalls++;
                return Task.FromResult(new ChatResponse(new MeaiChatMessage(ChatRole.Assistant, "fallback-content"))
                {
                    Usage = new UsageDetails
                    {
                        InputTokenCount = 3,
                        OutputTokenCount = 2,
                        TotalTokenCount = 5,
                    },
                });
            },
        };
        var emptyStreamProvider = new MEAILLMProvider("meai-empty-stream", emptyStreamClient);
        var emptyStreamResponse = await ChatStreamContentAggregator.AggregateResponseAsync(
            emptyStreamProvider,
            new LLMRequest
            {
                Messages = [new AevatarChatMessage { Role = "user", Content = "hello fallback" }],
            });

        nonStreamingFallbackCalls.Should().Be(1);
        emptyStreamResponse.Content.Should().Be("fallback-content");
        emptyStreamResponse.Usage.Should().NotBeNull();
        emptyStreamResponse.Usage!.PromptTokens.Should().Be(3);
        emptyStreamResponse.Usage.CompletionTokens.Should().Be(2);
        emptyStreamResponse.Usage.TotalTokens.Should().Be(5);

        var reasoningOnlyFallbackCalls = 0;
        var reasoningOnlyClient = new StubChatClient
        {
            OnGetStreamingResponse = static (_, _, _) => StreamWithReasoningOnly(),
            OnGetResponse = (_, _, _) =>
            {
                reasoningOnlyFallbackCalls++;
                return Task.FromResult(new ChatResponse(new MeaiChatMessage(ChatRole.Assistant, "should-not-run")));
            },
        };
        var reasoningOnlyProvider = new MEAILLMProvider("meai-reasoning-only", reasoningOnlyClient);
        var reasoningOnlyChunks = new List<LLMStreamChunk>();
        await foreach (var chunk in reasoningOnlyProvider.ChatStreamAsync(new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "reasoning only" }],
        }))
        {
            reasoningOnlyChunks.Add(chunk);
        }

        reasoningOnlyFallbackCalls.Should().Be(0);
        reasoningOnlyChunks.Should().Contain(x => x.DeltaReasoningContent == "thinking-only");
        reasoningOnlyChunks.Last().IsLast.Should().BeTrue();

        var toolStreamClient = new StubChatClient
        {
            OnGetStreamingResponse = static (_, _, _) => StreamWithToolCall(),
        };
        var toolStreamProvider = new MEAILLMProvider("meai-tool-stream", toolStreamClient);
        var toolStreamChunks = new List<LLMStreamChunk>();
        await foreach (var chunk in toolStreamProvider.ChatStreamAsync(new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "trigger tool" }],
        }))
        {
            toolStreamChunks.Add(chunk);
        }

        toolStreamChunks.Should().Contain(x => x.DeltaToolCall != null);
        var streamedToolCall = toolStreamChunks.First(x => x.DeltaToolCall != null).DeltaToolCall!;
        streamedToolCall.Id.Should().Be("call-stream-1");
        streamedToolCall.Name.Should().Be("lookup");
        streamedToolCall.ArgumentsJson.Should().Contain("term");

        var toolStreamMissingIdClient = new StubChatClient
        {
            OnGetStreamingResponse = static (_, _, _) => StreamWithToolCallMissingId(),
        };
        var toolStreamMissingIdProvider = new MEAILLMProvider("meai-tool-stream-missing-id", toolStreamMissingIdClient);
        var toolStreamMissingIdChunks = new List<LLMStreamChunk>();
        await foreach (var chunk in toolStreamMissingIdProvider.ChatStreamAsync(new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "trigger tool without id" }],
        }))
        {
            toolStreamMissingIdChunks.Add(chunk);
        }

        var missingIdToolCall = toolStreamMissingIdChunks.First(x => x.DeltaToolCall != null).DeltaToolCall!;
        missingIdToolCall.Id.Should().BeEmpty();
        missingIdToolCall.Name.Should().Be("lookup");
        missingIdToolCall.ArgumentsJson.Should().Contain("term");
    }

    [Fact]
    public async Task MEAILLMProvider_ShouldExtractTextContentWhenChatMessageTextIsEmpty()
    {
        var client = new StubChatClient
        {
            OnGetResponse = (_, _, _) =>
            {
                var assistant = new MeaiChatMessage(ChatRole.Assistant, string.Empty);
                assistant.Contents.Clear();
                assistant.Contents.Add(new TextContent("hello "));
                assistant.Contents.Add(new TextContent("world"));
                return Task.FromResult(new ChatResponse(assistant));
            },
        };

        var provider = new MEAILLMProvider("meai-text-content", client);
        var response = await ChatStreamContentAggregator.AggregateResponseAsync(provider, new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
        });

        response.Content.Should().Be("hello world");
    }

    [Fact]
    public void LLMProviderFactories_ShouldRegisterResolveAndThrowOnMissing()
    {
        var meaiFactory = new MEAILLMProviderFactory();
        meaiFactory.Register("alpha", new StubChatClient()).SetDefault("alpha");

        meaiFactory.GetDefault().Name.Should().Be("alpha");
        meaiFactory.GetAvailableProviders().Should().Contain("alpha");
        Action missingMeai = () => meaiFactory.GetProvider("missing");
        missingMeai.Should().Throw<InvalidOperationException>();

        var tornadoFactory = new TornadoLLMProviderFactory();
        tornadoFactory.Register("t1", LLmProviders.OpenAi, "key", "model");
        tornadoFactory.RegisterOpenAICompatible("t2", "key", "model");
        tornadoFactory.RegisterOpenAICompatible("t3", "key", "model", "https://example.test/v1");
        tornadoFactory.SetDefault("t2");

        tornadoFactory.GetDefault().Name.Should().Be("t2");
        tornadoFactory.GetAvailableProviders().Should().Contain(["t1", "t2", "t3"]);
        Action missingTornado = () => tornadoFactory.GetProvider("missing");
        missingTornado.Should().Throw<InvalidOperationException>();

        var t3Provider = (TornadoLLMProvider)tornadoFactory.GetProvider("t3");
        var t3Api = GetPrivateField<LlmTornado.TornadoApi>(t3Provider, "_api");
        t3Api.ApiUrlFormat.Should().Contain("example.test");
    }

    [Fact]
    public async Task SkillsComponents_ShouldDiscoverDeduplicateAndAdapt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skills-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "SKILL.md"), "# Writer Skill\n\nCreate concise text\n\nUse markdown.");
        File.WriteAllText(Path.Combine(dirB, "SKILL.md"), "# Editor Skill\n\nRewrite text\n\nUse plain text.");

        try
        {
            var discovery = new SkillDiscovery();
            discovery.ScanDirectory(Path.Combine(root, "missing")).Should().BeEmpty();

            var skills = discovery.ScanDirectory(root);
            skills.Should().HaveCount(2);
            skills.All(x => x.DirectoryPath.Length > 0).Should().BeTrue();

            var registry = new LocalSkillCatalog();
            var source = new SkillsAgentToolSource(
                new SkillsOptions { Directories = { dirA, dirB } },
                discovery,
                registry);

            var tools = await source.DiscoverToolsAsync();
            tools.Should().ContainSingle();
            tools[0].Name.Should().Be("use_skill");

            // Registry should have both skills
            registry.Count.Should().Be(2);

            // UseSkillTool should load skill content
            var useSkill = tools[0];
            var toolOutput = await useSkill.ExecuteAsync("{\"skill\":\"Writer Skill\"}");
            toolOutput.Should().Contain("Writer Skill");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MCPToolUtilities_ShouldSupportOptionsSanitizeAndEmptyDiscovery()
    {
        var options = new MCPToolsOptions().AddServer("srv", "cmd", "--a");
        options.Servers.Should().ContainSingle();
        options.Servers[0].Arguments.Should().ContainSingle().Which.Should().Be("--a");

        var adapter = new MCPToolAdapter("Weather Tool!*", "desc", "{}", client: null!, serverName: "srv");
        adapter.Name.Should().Be("Weather_Tool");

        var source = new MCPAgentToolSource(new MCPToolsOptions(), new MCPClientManager());
        var discovered = await source.DiscoverToolsAsync();
        discovered.Should().BeEmpty();
    }

    [Fact]
    public void TornadoProvider_PrivateMappers_ShouldMapRequestAndStreamDeltas()
    {
        var provider = new TornadoLLMProvider(
            "tor",
            new LlmTornado.TornadoApi(LLmProviders.OpenAi, "key"),
            "model-x");

        var mappedRequest = InvokeNonPublic<LlmTornado.Chat.ChatRequest>(
            provider,
            "MapRequest",
            new LLMRequest
            {
                RequestId = "session-tornado",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["workflow.run_id"] = "run-tornado",
                },
                Temperature = 0.4,
                MaxTokens = 128,
                Messages =
                [
                    new AevatarChatMessage { Role = "system", Content = "s" },
                    new AevatarChatMessage { Role = "user", Content = "u" },
                    new AevatarChatMessage { Role = "assistant", Content = "a" },
                    new AevatarChatMessage { Role = "tool", Content = "t" },
                    new AevatarChatMessage { Role = "unknown", Content = "x" },
                ],
            });

        mappedRequest.Model.Should().NotBeNull();
        mappedRequest.Model!.GetType().Name.Should().Contain("ChatModel");
        mappedRequest.Messages.Should().HaveCount(4);
        mappedRequest.Messages[0].Role.Should().Be(ChatMessageRoles.System);
        mappedRequest.Messages[1].Role.Should().Be(ChatMessageRoles.User);
        mappedRequest.Messages[2].Role.Should().Be(ChatMessageRoles.Assistant);
        mappedRequest.Messages[3].Role.Should().Be(ChatMessageRoles.User);
        mappedRequest.Temperature.Should().Be(0.4);
        mappedRequest.MaxTokens.Should().Be(128);
        mappedRequest.Metadata.Should().NotBeNull();
        mappedRequest.Metadata![LLMRequestMetadataKeys.RequestId].Should().Be("session-tornado");
        mappedRequest.Metadata["workflow.run_id"].Should().Be("run-tornado");

        var deltaToolCall = InvokePrivateStatic<Aevatar.AI.Abstractions.LLMProviders.ToolCall>(
            typeof(TornadoLLMProvider),
            "ConvertToolCallDelta",
            new LlmTornado.ChatFunctions.ToolCall
            {
                Id = string.Empty,
                FunctionCall = new FunctionCall
                {
                    Name = "lookup",
                    Arguments = "{\"term\":\"aevatar\"}",
                },
            });
        deltaToolCall.Id.Should().BeEmpty();
        deltaToolCall.Name.Should().Be("lookup");
        deltaToolCall.ArgumentsJson.Should().Be("{\"term\":\"aevatar\"}");
    }

    [Fact]
    public void TornadoProvider_MapRequest_ShouldLeaveMetadataAndSamplingUnset_WhenRequestOmitsThem()
    {
        var provider = new TornadoLLMProvider(
            "tor",
            new LlmTornado.TornadoApi(LLmProviders.OpenAi, "key"),
            "model-x");

        var mappedRequest = InvokeNonPublic<LlmTornado.Chat.ChatRequest>(
            provider,
            "MapRequest",
            new LLMRequest
            {
                Messages =
                [
                    new AevatarChatMessage { Role = "user", Content = null },
                ],
            });

        mappedRequest.Messages.Should().ContainSingle();
        mappedRequest.Messages[0].Content.Should().BeEmpty();
        mappedRequest.Metadata.Should().BeNull();
        mappedRequest.Temperature.Should().BeNull();
        mappedRequest.MaxTokens.Should().BeNull();
    }

    [Fact]
    public void TornadoProvider_StripNonTextContentParts_ShouldPreserveReasoningAndToolCalls()
    {
        var stripped = InvokePrivateStatic<AevatarChatMessage>(
            typeof(TornadoLLMProvider),
            "StripNonTextContentParts",
            new AevatarChatMessage
            {
                Role = "assistant",
                Content = "fallback",
                ReasoningContent = "thinking",
                ContentParts =
                [
                    ContentPart.TextPart("visible"),
                    ContentPart.ImagePart("aW1n", "image/png"),
                ],
                ToolCalls =
                [
                    new Aevatar.AI.Abstractions.LLMProviders.ToolCall
                    {
                        Id = "tc-1",
                        Name = "lookup",
                        ArgumentsJson = "{}",
                    },
                ],
            });

        stripped.Content.Should().Contain("visible");
        stripped.Content.Should().Contain("image content was attached");
        stripped.ReasoningContent.Should().Be("thinking");
        stripped.ToolCalls.Should().ContainSingle()
            .Which.Id.Should().Be("tc-1");
    }

    [Fact]
    public void TornadoProvider_ToolCallDeltaConverter_ShouldHandleSparsePayloads()
    {
        var emptyDelta = InvokePrivateStatic<Aevatar.AI.Abstractions.LLMProviders.ToolCall>(
            typeof(TornadoLLMProvider),
            "ConvertToolCallDelta",
            new LlmTornado.ChatFunctions.ToolCall
            {
                FunctionCall = null,
            });
        emptyDelta.Id.Should().BeEmpty();
        emptyDelta.Name.Should().BeEmpty();
        emptyDelta.ArgumentsJson.Should().BeEmpty();
    }

    [Fact]
    public async Task MCPClientManager_ShouldThrowOnInvalidCommandAndDisposeGracefully()
    {
        var manager = new MCPClientManager();
        var badServer = new MCPServerConfig
        {
            Name = "bad",
            Command = "/path/does/not/exist",
            Arguments = [],
        };

        var act = async () => await manager.ConnectAndDiscoverAsync(badServer);
        await act.Should().ThrowAsync<Exception>();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task MCPConnector_DisposeAsync_BeforeConnect_ShouldDisposeOwnedRemoteHttpClient()
    {
        var handler = new RecordingDisposeHandler();
        var client = new HttpClient(handler);
        var connector = new MCPConnector(
            name: "mcp-owned-http",
            serverConfig: new MCPServerConfig
            {
                Name = "server-owned-http",
                Url = "https://example.com/mcp",
                HttpClient = client,
                OwnsHttpClient = true,
            }, toolExecutionPort: TestAgentToolExecutionPort.Instance);

        await connector.DisposeAsync();
        await connector.DisposeAsync();

        handler.DisposeCount.Should().Be(1);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<string> parts)
    {
        foreach (var part in parts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, part);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<ChatResponseUpdate> updates)
    {
        foreach (var update in updates)
        {
            yield return update;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithToolCall()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "prefix");
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-stream-1",
                    "lookup",
                    new Dictionary<string, object?> { ["term"] = "aevatar" }),
            ]);
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithToolCallMissingId()
    {
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    string.Empty,
                    "lookup",
                    new Dictionary<string, object?> { ["term"] = "aevatar" }),
            ]);
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyChatStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithReasoningOnly()
    {
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [
                new TextReasoningContent("thinking-only"),
            ]);
        await Task.Yield();
    }

    private sealed class StubTool : IAgentTool
    {
        public StubTool(string name) => Name = name;

        public string Name { get; }
        public string Description => Name;
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{}");
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Func<IEnumerable<MeaiChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? OnGetResponse { get; init; }

        public Func<IEnumerable<MeaiChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? OnGetStreamingResponse { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (OnGetResponse != null)
                return OnGetResponse(messages, options, cancellationToken);

            return Task.FromResult(new ChatResponse(new MeaiChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (OnGetStreamingResponse != null)
                return OnGetStreamingResponse(messages, options, cancellationToken);

            return EmptyStream(cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private static T InvokeNonPublic<T>(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"Method {methodName} should exist on {target.GetType().Name}");
        return (T)method!.Invoke(target, args)!;
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull($"Static method {methodName} should exist on {type.Name}");
        return (T)method!.Invoke(null, args)!;
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull($"Field {fieldName} should exist on {target.GetType().Name}");
        return (T)field!.GetValue(target)!;
    }

    /// <summary>
    /// End-to-end test: verifies that tool names survive the full serialization
    /// pipeline from IAgentTool → AgentToolAIFunction → MEAI OpenAIChatClient → HTTP JSON body.
    /// Captures the actual HTTP request body that would be sent to the OpenAI API.
    /// This catches the exact bug: "Missing required parameter: tools[0].name".
    /// </summary>
    [Fact]
    public async Task AgentToolAIFunction_ShouldSerializeToolNameInOpenAIWireFormat()
    {
        string? capturedRequestBody = null;

        // Create a mock HTTP transport that captures the request body
        var handler = new CapturingHttpHandler(async request =>
        {
            capturedRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync();

            // Return a minimal valid streaming response so the SDK doesn't throw
            var responseContent = "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"test\"," +
                                  "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent, System.Text.Encoding.UTF8, "text/event-stream"),
            };
            return response;
        });

        // Build the same pipeline as NyxIdLLMProvider: OpenAIClient → IChatClient → MEAILLMProvider
        var clientOptions = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://test.example.com/v1/"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var openAiClient = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential("test-key"), clientOptions);
        var chatClient = openAiClient.GetChatClient("gpt-5.4").AsIChatClient();
        var provider = new MEAILLMProvider("test", chatClient, toolExecutionPort: TestAgentToolExecutionPort.Instance);

        var tools = new IAgentTool[]
        {
            new StubTool("nyxid_services"),
            new StubTool("web_search"),
        };

        // Stream through the full pipeline
        await foreach (var _ in provider.ChatStreamAsync(new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
            Tools = tools,
        }))
        { }

        // Verify the captured HTTP body contains tool names
        capturedRequestBody.Should().NotBeNullOrWhiteSpace("request body must be captured");

        using var doc = System.Text.Json.JsonDocument.Parse(capturedRequestBody!);
        var root = doc.RootElement;

        root.TryGetProperty("tools", out var toolsArray).Should().BeTrue("request must contain 'tools' array");
        toolsArray.GetArrayLength().Should().Be(2);

        for (var i = 0; i < toolsArray.GetArrayLength(); i++)
        {
            var toolElement = toolsArray[i];
            toolElement.TryGetProperty("function", out var function).Should().BeTrue(
                $"tools[{i}] must have 'function' property");
            function.TryGetProperty("name", out var nameElement).Should().BeTrue(
                $"tools[{i}].function must have 'name' property — " +
                $"this is the exact field the OpenAI API rejects as missing");
            nameElement.GetString().Should().NotBeNullOrWhiteSpace(
                $"tools[{i}].function.name must be non-empty");
        }

        // Verify specific names
        var toolNames = Enumerable.Range(0, toolsArray.GetArrayLength())
            .Select(i => toolsArray[i].GetProperty("function").GetProperty("name").GetString())
            .ToList();
        toolNames.Should().ContainInOrder("nyxid_services", "web_search");
    }

    private sealed class CapturingHttpHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            onSend(request);
    }

    private sealed class RecordingDisposeHandler : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeCount++;

            base.Dispose(disposing);
        }
    }

}
