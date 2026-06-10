using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.Lark.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public class LarkToolsTests
{
    [Fact]
    public async Task LarkMessagesSendTool_SendsTextMessage_AndNormalizesResponse()
    {
        var client = new StubLarkNyxClient
        {
            SendResponse = """{"code":0,"data":{"message_id":"om_123","chat_id":"oc_456","create_time":"1730000000"}}""",
        };
        var tool = new LarkMessagesSendTool(client);
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        });

        try
        {
            var result = await tool.ExecuteAsync(
                """{"target_type":"chat_id","target_id":"oc_456","message_type":"text","text":"Hello from Aevatar"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("message_id").GetString().Should().Be("om_123");
            document.RootElement.GetProperty("target_type").GetString().Should().Be("chat_id");
            client.LastSendToken.Should().Be("token-123");
            client.LastSendRequest.Should().NotBeNull();
            client.LastSendRequest!.MessageType.Should().Be("text");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task LarkDocxCreateTool_ShouldCreateAppendPermission_AndReturnLink()
    {
        var client = new StubLarkNyxClient
        {
            DocxCreateResponse = """{"code":0,"data":{"document":{"document_id":"doccn_123","url":"https://example.feishu.cn/docx/doccn_123"}}}""",
            DocxAppendResponse = """{"code":0,"data":{"children":[]}}""",
            DrivePermissionResponse = """{"code":0,"data":{"link_share_entity":"tenant_readable"}}""",
        };
        var tool = new LarkDocxCreateTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.lark.receive_id"] = "oc_chat_1",
                ["channel.lark.receive_id_type"] = "chat_id",
            });

        var result = await tool.ExecuteAsync(
            """{"title":"Daily report","markdown_text":"# Daily\n\nFull text","visibility":"readable"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("document_token").GetString().Should().Be("doccn_123");
        document.RootElement.GetProperty("document_url").GetString().Should().Be("https://example.feishu.cn/docx/doccn_123");
        document.RootElement.GetProperty("visibility_applied").GetBoolean().Should().BeTrue();
        client.LastDocxCreateToken.Should().Be("token-123");
        client.LastDocxCreateRequest.Should().Be(new LarkDocxCreateRequest("Daily report"));
        client.LastDocxAppendRequest.Should().Be(new LarkDocxAppendBlocksRequest("doccn_123", "# Daily\n\nFull text"));
        client.LastDrivePermissionRequest.Should().Be(new LarkDrivePermissionRequest("doccn_123", LarkDocxVisibility.Readable, "oc_chat_1", "chat_id"));
    }

    [Fact]
    public async Task LarkDocxCreateTool_ShouldFail_WhenPermissionOrUrlMissing()
    {
        using var _ = new AgentToolRequestMetadataScope("token-123");
        var permissionFailure = new LarkDocxCreateTool(new StubLarkNyxClient
        {
            DocxCreateResponse = """{"code":0,"data":{"document":{"document_id":"doccn_123","url":"https://example.feishu.cn/docx/doccn_123"}}}""",
            DocxAppendResponse = """{"code":0,"data":{}}""",
            DrivePermissionResponse = """{"code":999,"msg":"permission denied"}""",
        });

        var permissionResult = await permissionFailure.ExecuteAsync(
            """{"title":"Daily report","markdown_text":"full text"}""");

        permissionResult.Should().Contain("\"success\":false");
        permissionResult.Should().Contain("\"visibility_applied\":false");
        permissionResult.Should().Contain("lark_code=999");

        var missingUrl = new LarkDocxCreateTool(new StubLarkNyxClient
        {
            DocxCreateResponse = """{"code":0,"data":{"document":{"document_id":"doccn_123"}}}""",
            DocxAppendResponse = """{"code":0,"data":{}}""",
            DrivePermissionResponse = """{"code":0,"data":{}}""",
        });

        var missingUrlResult = await missingUrl.ExecuteAsync(
            """{"title":"Daily report","markdown_text":"full text"}""");

        missingUrlResult.Should().Contain("\"success\":false");
        missingUrlResult.Should().Contain("docx_create_missing_url");
    }

    [Fact]
    public async Task LarkDocxCreateTool_ShouldFail_WhenCreateReturnsProxyError()
    {
        using var _ = new AgentToolRequestMetadataScope("token-123");
        var client = new StubLarkNyxClient
        {
            DocxCreateResponse = """{"error":true,"status":503,"message":"docx unavailable"}""",
        };
        var tool = new LarkDocxCreateTool(client);

        var result = await tool.ExecuteAsync(
            """{"title":"Daily report","markdown_text":"full text"}""");

        result.Should().Contain("\"success\":false");
        result.Should().Contain("nyx_proxy_error status=503");
        result.Should().Contain("docx unavailable");
        client.LastDocxCreateRequest.Should().Be(new LarkDocxCreateRequest("Daily report"));
        client.LastDocxAppendRequest.Should().BeNull();
        client.LastDrivePermissionRequest.Should().BeNull();
    }

    [Fact]
    public async Task LarkDocxCreateTool_ShouldFail_WhenCreateOmitsDocumentToken()
    {
        using var _ = new AgentToolRequestMetadataScope("token-123");
        var client = new StubLarkNyxClient
        {
            DocxCreateResponse = """{"code":0,"data":{"document":{"url":"https://example.feishu.cn/docx/missing-token"}}}""",
        };
        var tool = new LarkDocxCreateTool(client);

        var result = await tool.ExecuteAsync(
            """{"title":"Daily report","markdown_text":"full text"}""");

        result.Should().Contain("\"success\":false");
        result.Should().Contain("docx_create_missing_token");
        client.LastDocxCreateRequest.Should().Be(new LarkDocxCreateRequest("Daily report"));
        client.LastDocxAppendRequest.Should().BeNull();
        client.LastDrivePermissionRequest.Should().BeNull();
    }

    [Fact]
    public async Task LarkDocxCreateTool_ShouldFail_WhenAppendReturnsProxyError()
    {
        using var _ = new AgentToolRequestMetadataScope("token-123");
        var client = new StubLarkNyxClient
        {
            DocxCreateResponse = """{"code":0,"data":{"document":{"document_id":"doccn_123","url":"https://example.feishu.cn/docx/doccn_123"}}}""",
            DocxAppendResponse = """{"code":999,"msg":"append rejected"}""",
        };
        var tool = new LarkDocxCreateTool(client);

        var result = await tool.ExecuteAsync(
            """{"title":"Daily report","markdown_text":"full text"}""");

        result.Should().Contain("\"success\":false");
        result.Should().Contain("\"document_token\":\"doccn_123\"");
        result.Should().Contain("\"document_url\":\"https://example.feishu.cn/docx/doccn_123\"");
        result.Should().Contain("lark_code=999");
        result.Should().Contain("append rejected");
        client.LastDocxAppendRequest.Should().Be(new LarkDocxAppendBlocksRequest("doccn_123", "full text"));
        client.LastDrivePermissionRequest.Should().BeNull();
    }

    [Fact]
    public async Task LarkDocxCreateTool_ShouldValidateInputs()
    {
        var tool = new LarkDocxCreateTool(new StubLarkNyxClient());
        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"title":"t","markdown_text":"body"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{"markdown_text":"body"}"""))
                .Should().Contain("title is required");
            (await tool.ExecuteAsync(JsonSerializer.Serialize(new
            {
                title = new string('t', 201),
                markdown_text = "body",
            })))
                .Should().Contain("title exceeds the maximum of 200 characters");
            (await tool.ExecuteAsync("""{"title":"t","markdown_text":" "}"""))
                .Should().Contain("markdown_text is required");
            (await tool.ExecuteAsync("""{"title":"t","markdown_text":"body","visibility":"public"}"""))
                .Should().Contain("visibility must be one of");
            (await tool.ExecuteAsync("""{"title":"t","markdown_text":"body","receive_id":"oc_1"}"""))
                .Should().Contain("receive_id and receive_id_type must be provided together");
            (await tool.ExecuteAsync("""{"title":"t","markdown_text":"body","receive_id":"oc_1","receive_id_type":"email"}"""))
                .Should().Contain("receive_id_type must be one of");
        }
    }

    [Fact]
    public async Task LarkMessagesSendTool_PropagatesTypedCallerScope()
    {
        var client = new StubLarkNyxClient
        {
            SendResponse = """{"code":0,"data":{"message_id":"om_123"}}""",
        };
        var tool = new LarkMessagesSendTool(client);
        using var _ = new AgentToolRequestContextScope(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials("caller-token", null, null),
            AgentToolCallerContext.Empty,
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)));

        var result = await tool.ExecuteAsync(
            """{"target_type":"chat_id","target_id":"oc_456","message_type":"text","text":"Hello from caller scope"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        client.LastSendToken.Should().Be("caller-token");
        client.LastSendRequest.Should().NotBeNull();
        client.LastSendRequest!.TargetId.Should().Be("oc_456");
    }

    [Fact]
    public async Task LarkMessagesSendTool_DoesNotLetPayloadOrExternalMetadataOverrideCallerScope()
    {
        var client = new StubLarkNyxClient
        {
            SendResponse = """{"code":0,"data":{"message_id":"om_123"}}""",
        };
        var tool = new LarkMessagesSendTool(client);
        using var _ = new AgentToolRequestContextScope(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials("trusted-caller-token", null, null),
            AgentToolCallerContext.Empty,
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "external-metadata-token",
            }));

        var result = await tool.ExecuteAsync(
            """
            {
              "target_type": "chat_id",
              "target_id": "oc_456",
              "message_type": "text",
              "text": "Hello from caller scope",
              "nyx_id_access_token": "payload-token",
              "headers": {
                "x-nyxid-access-token": "payload-header-token"
              }
            }
            """);

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        client.LastSendToken.Should().Be("trusted-caller-token");
    }

    [Fact]
    public async Task LarkMessagesSendTool_ValidatesInteractiveCardJson()
    {
        var tool = new LarkMessagesSendTool(new StubLarkNyxClient());
        using var _ = new AgentToolRequestMetadataScope("token-123");
        var result = await tool.ExecuteAsync(
            """{"target_type":"chat_id","target_id":"oc_456","message_type":"interactive_card","card_json":"{bad json}"}""");

        result.Should().Contain("card_json is not valid JSON");
    }

    [Fact]
    public async Task LarkMessagesSendTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkMessagesSendTool(new StubLarkNyxClient());
        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"target_type":"chat_id"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{"target_type":"channel_id","target_id":"oc_456","message_type":"text","text":"hello"}"""))
                .Should().Contain("target_type must be one of");
            (await tool.ExecuteAsync("""{"target_type":"chat_id","target_id":" ","message_type":"text","text":"hello"}"""))
                .Should().Contain("target_id is required");
            (await tool.ExecuteAsync("""{"target_type":"chat_id","target_id":"oc_456","message_type":"markdown","text":"hello"}"""))
                .Should().Contain("message_type must be one of");
            (await tool.ExecuteAsync("""{"target_type":"chat_id","target_id":"oc_456","message_type":"text","text":" "}"""))
                .Should().Contain("text is required when message_type=text");
            (await tool.ExecuteAsync("""{"target_type":"chat_id","target_id":"oc_456","message_type":"interactive_card"}"""))
                .Should().Contain("card_json is required when message_type=interactive_card");
        }

        var errorTool = new LarkMessagesSendTool(new StubLarkNyxClient
        {
            SendResponse = """{"error":true,"status":503,"message":"offline"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync(
                """{"target_type":"chat_id","target_id":"oc_456","message_type":"text","text":"Hello","thread_id":"om_1"}""");

            result.Should().Contain("nyx_proxy_error status=503");
            result.Should().Contain("thread_id is ignored");
            result.Should().Contain("\"target_type\":\"chat_id\"");
            result.Should().Contain("\"target_id\":\"oc_456\"");
        }
    }

    [Fact]
    // Refactor (issue1378/first-slice):
    //   Old pattern: ResolveOrCurrent hid missing message_id by replying to the current message.
    //   New principle: reply tests require structured error and explicit external message_id.
    public async Task LarkMessagesReplyTool_ShouldRejectMissingMessageId_AndKeepExplicitExternalReply()
    {
        var client = new StubLarkNyxClient
        {
            ReplyResponse = """{"code":0,"data":{"message_id":"om_reply_1","chat_id":"oc_456","create_time":"1730000002"}}""",
        };
        var tool = new LarkMessagesReplyTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>
            {
                ["channel.platform_message_id"] = "om_current_2",
            });

        var missingResult = await tool.ExecuteAsync("""{"text":"收到，我继续看一下","reply_in_thread":true}""");
        using (var missingDocument = JsonDocument.Parse(missingResult))
        {
            missingDocument.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            missingDocument.RootElement.GetProperty("code").GetString().Should().Be("missing_message_id");
            missingDocument.RootElement.GetProperty("error").GetString().Should().Be("message_id is required");
            missingDocument.RootElement.GetProperty("recommended_action").GetString().Should().Be("final_answer");
        }

        client.LastReplyRequest.Should().BeNull();

        var result = await tool.ExecuteAsync("""{"message_id":"om_external_2","text":"收到，我继续看一下","reply_in_thread":true}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("message_id").GetString().Should().Be("om_reply_1");
        document.RootElement.GetProperty("reply_in_thread").GetBoolean().Should().BeTrue();
        client.LastReplyRequest.Should().NotBeNull();
        client.LastReplyRequest!.MessageId.Should().Be("om_external_2");
        client.LastReplyRequest.ReplyInThread.Should().BeTrue();
        client.LastReplyRequest.MessageType.Should().Be("text");
    }

    [Fact]
    public async Task LarkMessagesReplyTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkMessagesReplyTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"message_id":"om_1","text":"hello"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{"text":"hello"}"""))
                .Should().Contain("message_id is required");
            (await tool.ExecuteAsync("""{"message_id":"msg_1","text":"hello"}"""))
                .Should().Contain("message_id must be a Lark message id like om_xxx");
            (await tool.ExecuteAsync("""{"message_id":"om_1","message_type":"markdown","text":"hello"}"""))
                .Should().Contain("message_type must be one of");
            (await tool.ExecuteAsync("""{"message_id":"om_1","message_type":"text","text":" "}"""))
                .Should().Contain("text is required when message_type=text");
            (await tool.ExecuteAsync("""{"message_id":"om_1","message_type":"interactive_card"}"""))
                .Should().Contain("card_json is required when message_type=interactive_card");
            (await tool.ExecuteAsync("""{"message_id":"om_1","message_type":"interactive_card","card_json":"{bad json}"}"""))
                .Should().Contain("card_json is not valid JSON");
        }

        var errorTool = new LarkMessagesReplyTool(new StubLarkNyxClient
        {
            ReplyResponse = """{"error":true,"status":500,"message":"reply failed"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync("""{"message_id":"om_1","text":"hello"}""");
            result.Should().Contain("nyx_proxy_error status=500");
            result.Should().Contain("\"message_id\":\"om_1\"");
        }
    }

    [Fact]
    // Refactor (issue1378/first-slice):
    //   Old pattern: ResolveOrCurrent hid missing message_id by reacting to the current message.
    //   New principle: reaction tests require structured error and explicit external message_id.
    public async Task LarkMessagesReactTool_ShouldRejectMissingMessageId_AndKeepExplicitExternalReaction()
    {
        var client = new StubLarkNyxClient
        {
            ReactionCreateResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "reaction_id": "reaction_123",
                    "operator": {
                      "operator_id": "cli_app",
                      "operator_type": "app"
                    },
                    "action_time": "1730000001",
                    "reaction_type": {
                      "emoji_type": "OK"
                    }
                  }
                }
                """,
        };
        var tool = new LarkMessagesReactTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>
            {
                ["channel.message_id"] = "om_current_1",
            });

        var missingResult = await tool.ExecuteAsync("""{}""");
        using (var missingDocument = JsonDocument.Parse(missingResult))
        {
            missingDocument.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            missingDocument.RootElement.GetProperty("code").GetString().Should().Be("missing_message_id");
            missingDocument.RootElement.GetProperty("error").GetString().Should().Be("message_id is required");
            missingDocument.RootElement.GetProperty("recommended_action").GetString().Should().Be("final_answer");
        }

        client.LastReactionRequest.Should().BeNull();

        var result = await tool.ExecuteAsync("""{"message_id":"om_external_1"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("message_id").GetString().Should().Be("om_external_1");
        document.RootElement.GetProperty("emoji_type").GetString().Should().Be("OK");
        document.RootElement.GetProperty("reaction_id").GetString().Should().Be("reaction_123");
        client.LastReactionRequest.Should().NotBeNull();
        client.LastReactionRequest!.MessageId.Should().Be("om_external_1");
        client.LastReactionRequest.EmojiType.Should().Be("OK");
    }

    [Fact]
    public async Task LarkMessagesReactTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkMessagesReactTool(new StubLarkNyxClient());
        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"message_id":"om_1"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{}"""))
                .Should().Contain("message_id is required");
            (await tool.ExecuteAsync("""{"message_id":"msg_1"}"""))
                .Should().Contain("message_id must be a Lark message id like om_xxx");
        }

        using (new AgentToolRequestMetadataScope(
                   "token-123",
                   new Dictionary<string, string>
                   {
                       ["channel.message_id"] = "msg_from_relay",
                   }))
        {
            (await tool.ExecuteAsync("""{}"""))
                .Should().Contain("message_id is required");
        }

        var errorTool = new LarkMessagesReactTool(new StubLarkNyxClient
        {
            ReactionCreateResponse = """{"error":true,"status":429,"message":"rate limited"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync("""{"message_id":"om_1","emoji_type":"收到"}""");

            result.Should().Contain("nyx_proxy_error status=429");
            result.Should().Contain("\"message_id\":\"om_1\"");
            result.Should().Contain("\"emoji_type\":\"OK\"");
        }
    }

    [Fact]
    public async Task LarkMessagesReactionsListTool_ShouldDefaultToCurrentMessage_AndNormalizeFilter()
    {
        var client = new StubLarkNyxClient
        {
            ReactionListResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "items": [
                      {
                        "reaction_id": "reaction_1",
                        "operator": {
                          "operator_id": "ou_1",
                          "operator_type": "user"
                        },
                        "action_time": "1730000003",
                        "reaction_type": {
                          "emoji_type": "OK"
                        }
                      }
                    ],
                    "has_more": false
                  }
                }
                """,
        };
        var tool = new LarkMessagesReactionsListTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>
            {
                ["channel.platform_message_id"] = "om_current_3",
            });

        var result = await tool.ExecuteAsync("""{"emoji_type":"收到"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("message_id").GetString().Should().Be("om_current_3");
        document.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        client.LastReactionListRequest.Should().NotBeNull();
        client.LastReactionListRequest!.MessageId.Should().Be("om_current_3");
        client.LastReactionListRequest.EmojiType.Should().Be("OK");
    }

    [Fact]
    public async Task LarkMessagesReactionsListTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkMessagesReactionsListTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"message_id":"om_1"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{}"""))
                .Should().Contain("message_id is required");
            (await tool.ExecuteAsync("""{"message_id":"om_1","user_id_type":"email"}"""))
                .Should().Contain("user_id_type must be one of");
            (await tool.ExecuteAsync("""{"message_id":"om_1","page_size":51}"""))
                .Should().Contain("page_size must be between 1 and 50");
        }

        var errorTool = new LarkMessagesReactionsListTool(new StubLarkNyxClient
        {
            ReactionListResponse = """{"error":true,"status":404,"message":"missing"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync("""{"message_id":"om_1"}""");
            result.Should().Contain("nyx_proxy_error status=404");
            result.Should().Contain("\"message_id\":\"om_1\"");
        }
    }

    [Fact]
    public async Task LarkMessagesReactionsDeleteTool_ShouldDeleteReactionFromCurrentMessage()
    {
        var client = new StubLarkNyxClient
        {
            ReactionDeleteResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "reaction_id": "reaction_1",
                    "operator": {
                      "operator_id": "ou_1",
                      "operator_type": "user"
                    },
                    "action_time": "1730000004",
                    "reaction_type": {
                      "emoji_type": "OK"
                    }
                  }
                }
                """,
        };
        var tool = new LarkMessagesReactionsDeleteTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>
            {
                ["channel.platform_message_id"] = "om_current_4",
            });

        var result = await tool.ExecuteAsync("""{"reaction_id":"reaction_1"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("message_id").GetString().Should().Be("om_current_4");
        document.RootElement.GetProperty("reaction_id").GetString().Should().Be("reaction_1");
        client.LastReactionDeleteRequest.Should().NotBeNull();
        client.LastReactionDeleteRequest!.MessageId.Should().Be("om_current_4");
    }

    [Fact]
    public async Task LarkMessagesReactionsDeleteTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkMessagesReactionsDeleteTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"message_id":"om_1","reaction_id":"reaction_1"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{"message_id":"om_1"}"""))
                .Should().Contain("reaction_id is required");
        }

        var errorTool = new LarkMessagesReactionsDeleteTool(new StubLarkNyxClient
        {
            ReactionDeleteResponse = """{"error":true,"status":409,"message":"already removed"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync("""{"message_id":"om_1","reaction_id":"reaction_1"}""");
            result.Should().Contain("nyx_proxy_error status=409");
            result.Should().Contain("\"reaction_id\":\"reaction_1\"");
        }
    }

    [Fact]
    public async Task LarkMessagesBatchGetTool_ShouldNormalizeMessages()
    {
        var client = new StubLarkNyxClient
        {
            MessagesBatchGetResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "items": [
                      {
                        "message_id": "om_1",
                        "msg_type": "text",
                        "create_time": "1710000000",
                        "chat_id": "oc_1",
                        "thread_id": "omt_1",
                        "sender": {
                          "id": "ou_sender",
                          "name": "Alice",
                          "sender_type": "user"
                        },
                        "body": {
                          "content": "{\"text\":\"hello\"}"
                        }
                      }
                    ]
                  }
                }
                """,
        };
        var tool = new LarkMessagesBatchGetTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-123");
        var result = await tool.ExecuteAsync("""{"message_ids":["om_1"]}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("hello");
        client.LastBatchGetRequest.Should().NotBeNull();
        client.LastBatchGetRequest!.MessageIds.Should().ContainSingle().Which.Should().Be("om_1");
    }

    [Fact]
    public async Task LarkMessagesBatchGetTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkMessagesBatchGetTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"message_ids":["om_1"]}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{}"""))
                .Should().Contain("message_ids must contain at least one");
            (await tool.ExecuteAsync("""{"message_ids":["msg_1"]}"""))
                .Should().Contain("message_id must be a Lark message id like om_xxx");
        }

        var errorTool = new LarkMessagesBatchGetTool(new StubLarkNyxClient
        {
            MessagesBatchGetResponse = """{"error":true,"status":503,"message":"mget offline"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await errorTool.ExecuteAsync("""{"message_ids":["om_1"]}"""))
                .Should().Contain("nyx_proxy_error status=503");
        }
    }

    [Fact]
    public async Task LarkMessagesSearchTool_ShouldSearchAndHydrateMessages()
    {
        var client = new StubLarkNyxClient
        {
            MessageSearchResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "items": [
                      { "meta_data": { "message_id": "om_1" } }
                    ],
                    "has_more": true,
                    "page_token": "page-2"
                  }
                }
                """,
            MessagesBatchGetResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "items": [
                      {
                        "message_id": "om_1",
                        "msg_type": "text",
                        "create_time": "1710000000",
                        "chat_id": "oc_1",
                        "sender": {
                          "id": "ou_sender",
                          "name": "Alice",
                          "sender_type": "user"
                        },
                        "body": {
                          "content": "{\"text\":\"incident handled\"}"
                        }
                      }
                    ]
                  }
                }
                """,
        };
        var tool = new LarkMessagesSearchTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-123");
        var result = await tool.ExecuteAsync("""{"query":"incident","chat_ids":["oc_1"],"start_time":"2026-04-20T00:00:00+08:00","end_time":"2026-04-23T23:59:59+08:00"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("has_more").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("page_token").GetString().Should().Be("page-2");
        document.RootElement.GetProperty("message_ids")[0].GetString().Should().Be("om_1");
        document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("incident handled");
        client.LastMessageSearchRequest.Should().NotBeNull();
        client.LastMessageSearchRequest!.Query.Should().Be("incident");
    }

    [Fact]
    public async Task LarkMessagesSearchTool_ShouldValidateInputs_AndDegradeWhenHydrationFails()
    {
        var tool = new LarkMessagesSearchTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"query":"incident"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{}"""))
                .Should().Contain("At least one search filter is required");
            (await tool.ExecuteAsync("""{"query":"incident","include_attachment_type":"doc"}"""))
                .Should().Contain("include_attachment_type must be one of");
            (await tool.ExecuteAsync("""{"query":"incident","chat_type":"channel"}"""))
                .Should().Contain("chat_type must be one of");
            (await tool.ExecuteAsync("""{"query":"incident","sender_type":"app"}"""))
                .Should().Contain("sender_type must be one of");
            (await tool.ExecuteAsync("""{"query":"incident","sender_type":"bot","exclude_sender_type":"bot"}"""))
                .Should().Contain("sender_type and exclude_sender_type cannot be the same");
            (await tool.ExecuteAsync("""{"query":"incident","start_time":"bad-time"}"""))
                .Should().Contain("start_time and end_time must be ISO 8601");
            (await tool.ExecuteAsync("""{"query":"incident","page_size":51}"""))
                .Should().Contain("page_size must be between 1 and 50");
        }

        var degradeTool = new LarkMessagesSearchTool(new StubLarkNyxClient
        {
            MessageSearchResponse = """{"code":0,"data":{"items":[{"meta_data":{"message_id":"om_1"}}]}}""",
            MessagesBatchGetResponse = """{"error":true,"status":502,"message":"mget failed"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await degradeTool.ExecuteAsync("""{"query":"incident"}""");
            result.Should().Contain("message hydration failed");
            result.Should().Contain("\"message_ids\":[\"om_1\"]");
        }
    }

    [Fact]
    public async Task LarkChatsLookupTool_ReturnsNormalizedCandidates()
    {
        var client = new StubLarkNyxClient
        {
            SearchResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "items": [
                      { "meta_data": { "chat_id": "oc_2", "name": "Beta", "chat_mode": "group", "chat_status": "normal" } },
                      { "meta_data": { "chat_id": "oc_1", "name": "Alpha", "chat_mode": "group", "chat_status": "normal" } }
                    ],
                    "total": 2,
                    "has_more": false
                  }
                }
                """,
        };
        var tool = new LarkChatsLookupTool(client);
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        });

        try
        {
            var result = await tool.ExecuteAsync("""{"query":"Alpha","exact_match_hint":true}""");

            using var document = JsonDocument.Parse(result);
            var chats = document.RootElement.GetProperty("chats");
            chats.GetArrayLength().Should().Be(2);
            chats[0].GetProperty("chat_id").GetString().Should().Be("oc_1");
            chats[0].GetProperty("exact_name_match").GetBoolean().Should().BeTrue();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task LarkChatsLookupTool_RequiresQueryOrMemberIds()
    {
        var tool = new LarkChatsLookupTool(new StubLarkNyxClient());
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var result = await tool.ExecuteAsync("""{}""");
        result.Should().Contain("At least one of query or member_ids is required");
    }

    [Fact]
    public async Task LarkChatsLookupTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkChatsLookupTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"query":"alpha"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync(JsonSerializer.Serialize(new
            {
                query = new string('a', 65),
            })))
                .Should().Contain("query exceeds the maximum of 64 characters");
            (await tool.ExecuteAsync(
                JsonSerializer.Serialize(new
                {
                    member_ids = Enumerable.Range(1, 51).Select(i => $"ou_{i}").ToArray(),
                })))
                .Should().Contain("member_ids exceeds the maximum of 50 values");
            (await tool.ExecuteAsync("""{"query":"alpha","search_types":["private","bad-type"]}"""))
                .Should().Contain("search_types contains invalid values: bad-type");
            (await tool.ExecuteAsync("""{"query":"alpha","page_size":101}"""))
                .Should().Contain("page_size must be between 1 and 100");
        }

        var errorTool = new LarkChatsLookupTool(new StubLarkNyxClient
        {
            SearchResponse = """{"error":true,"status":502,"message":"gateway"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await errorTool.ExecuteAsync("""{"query":"alpha","search_types":["public_joined"]}"""))
                .Should().Contain("nyx_proxy_error status=502");
        }
    }

    [Fact]
    public async Task LarkSheetsAppendRowsTool_NormalizesRangeAndReturnsSummary()
    {
        var client = new StubLarkNyxClient
        {
            AppendSheetResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "tableRange": "sheet_1!A1:B2",
                    "updates": {
                      "updatedRange": "sheet_1!C2:D3",
                      "updatedRows": 2,
                      "updatedColumns": 2,
                      "updatedCells": 4
                    }
                  }
                }
                """,
        };
        var tool = new LarkSheetsAppendRowsTool(client);
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        });

        try
        {
            var result = await tool.ExecuteAsync(
                """{"spreadsheet_url":"https://example.feishu.cn/sheets/shtcn_123","sheet_id":"sheet_1","range":"C2","rows":[["Alice","100"],["Bob","95"]]}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("updated_range").GetString().Should().Be("sheet_1!C2:D3");
            client.LastSheetAppendRequest.Should().NotBeNull();
            client.LastSheetAppendRequest!.SpreadsheetToken.Should().Be("shtcn_123");
            client.LastSheetAppendRequest.Range.Should().Be("sheet_1!C2:C2");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task LarkSheetsAppendRowsTool_RequiresSheetContextForRelativeRange()
    {
        var tool = new LarkSheetsAppendRowsTool(new StubLarkNyxClient());
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var result = await tool.ExecuteAsync(
            """{"spreadsheet_token":"shtcn_123","range":"A1","rows":[["Alice"]]}""");

        result.Should().Contain("range without a sheet prefix requires sheet_id");
    }

    [Fact]
    public async Task LarkSheetsAppendRowsTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkSheetsAppendRowsTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"spreadsheet_token":"shtcn_123","rows":[["Alice"]]}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{"rows":[["Alice"]]}"""))
                .Should().Contain("One of spreadsheet_token or spreadsheet_url is required");
            (await tool.ExecuteAsync("""{"spreadsheet_token":"shtcn_123","rows":[[],[]]}"""))
                .Should().Contain("rows must contain at least one non-empty row");
        }

        var errorTool = new LarkSheetsAppendRowsTool(new StubLarkNyxClient
        {
            AppendSheetResponse = """{"error":true,"status":500,"message":"sheet offline"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync(
                """{"spreadsheet_token":"shtcn_123","sheet_id":"sheet_1","range":"A1","rows":[["Alice"]]}""");

            result.Should().Contain("nyx_proxy_error status=500");
            result.Should().Contain("\"spreadsheet_token\":\"shtcn_123\"");
            result.Should().Contain("\"range\":\"sheet_1!A1:A1\"");
        }
    }

    [Fact]
    public async Task LarkApprovalsListTool_NormalizesTopicAndResponse()
    {
        var client = new StubLarkNyxClient
        {
            ApprovalListResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "count": 1,
                    "has_more": false,
                    "tasks": [
                      {
                        "task_id": "task_1",
                        "instance_code": "inst_1",
                        "title": "Expense Approval",
                        "status": "1",
                        "topic": "1",
                        "support_api_operate": true,
                        "definition_code": "def_1",
                        "definition_name": "Expense",
                        "initiator": "ou_init",
                        "initiator_name": "Alice",
                        "user_id": "ou_owner",
                        "instance_status": "1",
                        "summaries": [
                          { "key": "amount", "value": "100" }
                        ]
                      }
                    ]
                  }
                }
                """,
        };
        var tool = new LarkApprovalsListTool(client);
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        });

        try
        {
            var result = await tool.ExecuteAsync("""{"topic":"todo"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            var tasks = document.RootElement.GetProperty("tasks");
            tasks.GetArrayLength().Should().Be(1);
            tasks[0].GetProperty("topic").GetString().Should().Be("todo");
            tasks[0].GetProperty("status").GetString().Should().Be("todo");
            client.LastApprovalQueryRequest.Should().NotBeNull();
            client.LastApprovalQueryRequest!.Topic.Should().Be("1");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task LarkApprovalsListTool_ShouldValidateInputs_AndNormalizeAdditionalStatuses()
    {
        var tool = new LarkApprovalsListTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"topic":"todo"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{"topic":"unknown"}"""))
                .Should().Contain("topic must be one of");
            (await tool.ExecuteAsync("""{"topic":"todo","locale":"fr-FR"}"""))
                .Should().Contain("locale must be one of");
            (await tool.ExecuteAsync("""{"topic":"todo","user_id_type":"email"}"""))
                .Should().Contain("user_id_type must be one of");
            (await tool.ExecuteAsync("""{"topic":"todo","page_size":101}"""))
                .Should().Contain("page_size must be between 1 and 100");
        }

        var errorTool = new LarkApprovalsListTool(new StubLarkNyxClient
        {
            ApprovalListResponse = """{"error":true,"status":504,"message":"timeout"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await errorTool.ExecuteAsync("""{"topic":"todo"}"""))
                .Should().Contain("nyx_proxy_error status=504");
        }

        var successTool = new LarkApprovalsListTool(new StubLarkNyxClient
        {
            ApprovalListResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "count": 5,
                    "has_more": false,
                    "tasks": [
                      { "task_id": "task_2", "instance_code": "inst_2", "status": "2", "topic": "2", "instance_status": "2", "summaries": [] },
                      { "task_id": "task_3", "instance_code": "inst_3", "status": "17", "topic": "3", "instance_status": "3", "summaries": [] },
                      { "task_id": "task_4", "instance_code": "inst_4", "status": "18", "topic": "17", "instance_status": "4", "summaries": [] },
                      { "task_id": "task_5", "instance_code": "inst_5", "status": "33", "topic": "18", "instance_status": "5", "summaries": [] },
                      { "task_id": "task_6", "instance_code": "inst_6", "status": "34", "topic": "99", "instance_status": "0", "summaries": [] }
                    ]
                  }
                }
                """,
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await successTool.ExecuteAsync("""{"topic":"done"}""");

            using var document = JsonDocument.Parse(result);
            var tasks = document.RootElement.GetProperty("tasks");
            tasks[0].GetProperty("topic").GetString().Should().Be("done");
            tasks[0].GetProperty("status").GetString().Should().Be("done");
            tasks[0].GetProperty("instance_status").GetString().Should().Be("approved");
            tasks[1].GetProperty("topic").GetString().Should().Be("initiated");
            tasks[1].GetProperty("status").GetString().Should().Be("unread");
            tasks[1].GetProperty("instance_status").GetString().Should().Be("rejected");
            tasks[2].GetProperty("topic").GetString().Should().Be("cc_unread");
            tasks[2].GetProperty("status").GetString().Should().Be("read");
            tasks[2].GetProperty("instance_status").GetString().Should().Be("withdrawn");
            tasks[3].GetProperty("topic").GetString().Should().Be("cc_read");
            tasks[3].GetProperty("status").GetString().Should().Be("processing");
            tasks[3].GetProperty("instance_status").GetString().Should().Be("terminated");
            tasks[4].GetProperty("topic").GetString().Should().Be("99");
            tasks[4].GetProperty("status").GetString().Should().Be("withdrawn");
            tasks[4].GetProperty("instance_status").GetString().Should().Be("none");
        }
    }

    [Fact]
    public async Task LarkApprovalsGetTool_ReturnsControlFlowFields()
    {
        var client = new StubLarkNyxClient
        {
            ApprovalGetResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "instance": {
                      "instance_code": "inst_1",
                      "approval_code": "def_1",
                      "approval_name": "Expense",
                      "status": "2",
                      "start_time": "1710000000",
                      "end_time": "1710000300",
                      "serial_number": "SN-1",
                      "user_id": "ou_init",
                      "user_name": "Alice",
                      "department_id": "od_1",
                      "department_name": "Finance",
                      "uuid": "uuid-1",
                      "task_list": [
                        {
                          "task_id": "task_1",
                          "user_id": "ou_approver",
                          "user_name": "Bob",
                          "status": "2",
                          "start_time": "1710000100",
                          "end_time": "1710000200"
                        }
                      ],
                      "form": [
                        { "id": "field_1", "name": "Amount", "type": "input", "value": "100", "ext": "{}" }
                      ]
                    }
                  }
                }
                """,
        };
        var tool = new LarkApprovalsGetTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-123");
        var result = await tool.ExecuteAsync("""{"instance_code":"inst_1","locale":"en-US","user_id_type":"open_id"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("instance_code").GetString().Should().Be("inst_1");
        root.GetProperty("approval_code").GetString().Should().Be("def_1");
        root.GetProperty("status").GetString().Should().Be("approved");
        root.GetProperty("raw_status").GetString().Should().Be("2");
        root.GetProperty("is_terminal").GetBoolean().Should().BeTrue();
        root.GetProperty("terminal_status").GetString().Should().Be("approved");
        root.GetProperty("should_continue_waiting").GetBoolean().Should().BeFalse();
        root.GetProperty("approved").GetBoolean().Should().BeTrue();
        root.GetProperty("rejected").GetBoolean().Should().BeFalse();
        root.GetProperty("task_count").GetInt32().Should().Be(1);
        root.GetProperty("tasks")[0].GetProperty("status").GetString().Should().Be("done");
        root.GetProperty("form")[0].GetProperty("name").GetString().Should().Be("Amount");
        client.LastApprovalGetRequest.Should().Be(new LarkApprovalInstanceGetRequest("inst_1", "en-US", "open_id"));
    }

    [Fact]
    public async Task LarkApprovalsGetTool_ShouldParseEncodedFormPayload()
    {
        var client = new StubLarkNyxClient
        {
            ApprovalGetResponse =
                """
                {
                  "code": 0,
                  "data": {
                    "instance_code": "inst_1",
                    "status": "1",
                    "form": "[{\"id\":\"field_1\",\"name\":\"Amount\",\"type\":\"input\",\"value\":\"100\",\"ext\":\"{}\"}]"
                  }
                }
                """,
        };
        var tool = new LarkApprovalsGetTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-123");
        var result = await tool.ExecuteAsync("""{"instance_code":"inst_1"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("status").GetString().Should().Be("running");
        root.GetProperty("form").GetArrayLength().Should().Be(1);
        root.GetProperty("form")[0].GetProperty("id").GetString().Should().Be("field_1");
        root.GetProperty("form")[0].GetProperty("value").GetString().Should().Be("100");
    }

    [Fact]
    public async Task LarkApprovalsGetTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkApprovalsGetTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"instance_code":"inst_1"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{}"""))
                .Should().Contain("instance_code is required");
            (await tool.ExecuteAsync("""{"instance_code":"inst_1","locale":"fr-FR"}"""))
                .Should().Contain("locale must be one of");
            (await tool.ExecuteAsync("""{"instance_code":"inst_1","user_id_type":"email"}"""))
                .Should().Contain("user_id_type must be one of");
        }

        var runningTool = new LarkApprovalsGetTool(new StubLarkNyxClient
        {
            ApprovalGetResponse = """{"code":0,"data":{"instance_code":"inst_2","status":"1"}}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await runningTool.ExecuteAsync("""{"instance_code":"inst_2"}""");
            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("running");
            document.RootElement.GetProperty("is_terminal").GetBoolean().Should().BeFalse();
            document.RootElement.GetProperty("should_continue_waiting").GetBoolean().Should().BeTrue();
        }

        var errorTool = new LarkApprovalsGetTool(new StubLarkNyxClient
        {
            ApprovalGetResponse = """{"error":true,"status":504,"message":"timeout"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync("""{"instance_code":"inst_3"}""");
            result.Should().Contain("nyx_proxy_error status=504");
            result.Should().Contain("\"instance_code\":\"inst_3\"");
        }
    }

    [Fact]
    public async Task LarkApprovalsActTool_ValidatesTransferTarget()
    {
        var tool = new LarkApprovalsActTool(new StubLarkNyxClient());
        using (new AgentToolRequestMetadataScope("token-123", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.lark.operator_user_id"] = "lark-user-1",
        }))
        {
            var result = await tool.ExecuteAsync("""{"action":"transfer","instance_code":"inst_1","task_id":"task_1"}""");
            result.Should().Contain("transfer_user_id is required");
        }
    }

    [Fact]
    public async Task LarkApprovalsActTool_SendsApproveAction()
    {
        var client = new StubLarkNyxClient
        {
            ApprovalActionResponse = """{"code":0,"data":{}}""",
        };
        var tool = new LarkApprovalsActTool(client);
        using (new AgentToolRequestMetadataScope("token-123", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.lark.operator_user_id"] = "lark-user-1",
        }))
        {
            var result = await tool.ExecuteAsync(
                """{"action":"approve","instance_code":"inst_1","task_id":"task_1","comment":"LGTM","form_json":"[{\"id\":\"field_1\",\"type\":\"input\",\"value\":\"ok\"}]"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            client.LastApprovalActionRequest.Should().NotBeNull();
            client.LastApprovalActionRequest!.Action.Should().Be("approve");
            client.LastApprovalActionRequest.UserId.Should().Be("lark-user-1");
            client.LastApprovalActionRequest.FormJson.Should().Contain("\"field_1\"");
        }
    }

    [Fact]
    public async Task LarkApprovalsActTool_UsesLarkOperatorUserIdFromTurnMetadataOverToolArgument()
    {
        var client = new StubLarkNyxClient
        {
            ApprovalActionResponse = """{"code":0,"data":{}}""",
        };
        var tool = new LarkApprovalsActTool(client);

        using (new AgentToolRequestMetadataScope("token-123", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.lark.operator_user_id"] = "lark-user-1",
            ["channel.lark.operator_open_id"] = "ou_4159cd4d1af9b836b0fb2dc05ef52efe",
        }))
        {
            var result = await tool.ExecuteAsync(
                """{"action":"approve","instance_code":"inst_1","task_id":"task_1","user_id":"ou_4159cd4d1af9b836b0fb2dc05ef52efe","comment":"LGTM"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            client.LastApprovalActionRequest.Should().NotBeNull();
            client.LastApprovalActionRequest!.UserId.Should().Be("lark-user-1");
            client.LastApprovalActionRequest.UserId.Should().NotBe("ou_4159cd4d1af9b836b0fb2dc05ef52efe");
        }
    }

    [Fact]
    public async Task LarkApprovalsActTool_UsesLarkOperatorUserIdFromProductionToolLoopMetadataBridge()
    {
        var client = new StubLarkNyxClient
        {
            ApprovalActionResponse = """{"code":0,"data":{}}""",
        };
        var tools = new ToolManager();
        tools.Register(new LarkApprovalsActTool(client));
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-lark-approval",
                        Name = "lark_approvals_act",
                        ArgumentsJson = """{"action":"approve","instance_code":"inst_1","task_id":"task_1","comment":"LGTM"}""",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var request = new LLMRequest
        {
            Messages = [],
            RequestId = "session-lark-approval",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.lark.operator_user_id"] = "lark-user-1",
                ["channel.lark.operator_open_id"] = "ou_operator_1",
            },
            ToolContext = AgentToolExecutionContext.Empty with
            {
                Credentials = new AgentToolCredentials("token-123", null, null),
            },
        };

        await new ToolCallLoop(tools).ExecuteAsync(
            provider,
            [ChatMessage.User("approve the task")],
            request,
            maxRounds: 2,
            CancellationToken.None);

        client.LastApprovalActionRequest.Should().NotBeNull();
        client.LastApprovalActionRequest!.UserId.Should().Be("lark-user-1");
        client.LastApprovalActionRequest.UserId.Should().NotBe("ou_operator_1");
    }

    [Fact]
    public async Task LarkApprovalsActTool_ShouldValidateInputs_AndSurfaceProxyErrors()
    {
        var tool = new LarkApprovalsActTool(new StubLarkNyxClient());

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123"))
        {
            (await tool.ExecuteAsync("""{"action":"pause","instance_code":"inst_1","task_id":"task_1"}"""))
                .Should().Contain("action must be one of");
            (await tool.ExecuteAsync("""{"action":"approve","task_id":"task_1"}"""))
                .Should().Contain("instance_code is required");
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1"}"""))
                .Should().Contain("task_id is required");
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1"}"""))
                .Should().Contain("user_id is required");
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1","user_id_type":"email"}"""))
                .Should().Contain("user_id_type must be one of");
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1","user_id":"lark-user-1","transfer_user_id":"ou_1"}"""))
                .Should().Contain("transfer_user_id is only allowed when action=transfer");
            (await tool.ExecuteAsync("""{"action":"reject","instance_code":"inst_1","task_id":"task_1","user_id":"lark-user-1","form_json":"{}"}"""))
                .Should().Contain("form_json is only supported when action=approve");
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1","user_id":"lark-user-1","form_json":"{bad json}"}"""))
                .Should().Contain("form_json is not valid JSON");
        }

        var errorTool = new LarkApprovalsActTool(new StubLarkNyxClient
        {
            ApprovalActionResponse = """{"error":true,"status":409,"message":"already processed"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync(
                """{"action":"reject","instance_code":"inst_1","task_id":"task_1","user_id":"lark-user-1","comment":"nope"}""");

            result.Should().Contain("nyx_proxy_error status=409");
            result.Should().Contain("\"action\":\"reject\"");
            result.Should().Contain("\"instance_code\":\"inst_1\"");
            result.Should().Contain("\"task_id\":\"task_1\"");
        }
    }

    [Fact]
    public async Task LarkAgentToolSource_RegistersTools_WhenNyxConfigured()
    {
        var source = new LarkAgentToolSource(
            new LarkToolOptions(),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new StubLarkNyxClient());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().HaveCount(13);
        tools.Should().Contain(tool => tool is LarkMessagesSendTool);
        tools.Should().Contain(tool => tool is LarkMessagesReplyTool);
        tools.Should().Contain(tool => tool is LarkMessagesReactTool);
        tools.Should().Contain(tool => tool is LarkMessagesReactionsListTool);
        tools.Should().Contain(tool => tool is LarkMessagesReactionsDeleteTool);
        tools.Should().Contain(tool => tool is LarkMessagesSearchTool);
        tools.Should().Contain(tool => tool is LarkMessagesBatchGetTool);
        tools.Should().Contain(tool => tool is LarkChatsLookupTool);
        tools.Should().Contain(tool => tool is LarkSheetsAppendRowsTool);
        tools.Should().Contain(tool => tool is LarkApprovalsListTool);
        tools.Should().Contain(tool => tool is LarkApprovalsGetTool);
        tools.Should().Contain(tool => tool is LarkApprovalsActTool);
        tools.Should().Contain(tool => tool is LarkDocxCreateTool);
    }

    [Fact]
    public async Task LarkAgentToolSource_SkipsTools_WhenNyxBaseUrlMissing()
    {
        var source = new LarkAgentToolSource(
            new LarkToolOptions(),
            new NyxIdToolOptions(),
            new StubLarkNyxClient());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task LarkNyxClient_SendMessage_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"message_id":"om_1"}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.SendMessageAsync(
            "token-123",
            new LarkSendMessageRequest("chat_id", "oc_123", "text", """{"text":"Hello"}""", "uuid-1"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=chat_id");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("token-123");

        var body = handler.LastBody;
        body.Should().Contain("receive_id");
        body.Should().Contain("uuid-1");
    }

    [Fact]
    public async Task LarkNyxClient_ReplyToMessage_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"message_id":"om_reply_1"}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.ReplyToMessageAsync(
            "token-123",
            new LarkReplyMessageRequest("om_123", "text", """{"text":"Roger that"}""", true, "uuid-2"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_123/reply");
        handler.LastBody.Should().Contain("\"reply_in_thread\":true");
        handler.LastBody.Should().Contain("\"uuid\":\"uuid-2\"");
    }

    [Fact]
    public async Task LarkNyxClient_CreateMessageReaction_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"reaction_id":"reaction_1"}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.CreateMessageReactionAsync(
            "token-123",
            new LarkMessageReactionRequest("om_123", "OK"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_123/reactions");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("token-123");
        handler.LastBody.Should().Contain("\"emoji_type\":\"OK\"");
    }

    [Fact]
    public async Task LarkNyxClient_ListAndDeleteMessageReactions_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"items":[]}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.ListMessageReactionsAsync(
            "token-123",
            new LarkMessageReactionListRequest("om_123", "SMILE", 50, "page-1", "open_id"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_123/reactions?page_size=50&reaction_type=SMILE&page_token=page-1&user_id_type=open_id");

        await client.DeleteMessageReactionAsync(
            "token-123",
            new LarkMessageReactionDeleteRequest("om_123", "reaction_1"),
            CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_123/reactions/reaction_1");
    }

    [Fact]
    public async Task LarkNyxClient_SearchAndBatchGetMessages_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"items":[]}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.SearchMessagesAsync(
            "token-123",
            new LarkMessageSearchRequest(
                Query: "incident",
                ChatIds: ["oc_1"],
                SenderIds: ["ou_1"],
                IncludeAttachmentType: "file",
                ChatType: "group",
                SenderType: "user",
                ExcludeSenderType: "bot",
                IsAtMe: true,
                StartTime: "2026-04-20T00:00:00+08:00",
                EndTime: "2026-04-23T23:59:59+08:00",
                PageSize: 20,
                PageToken: "page-2"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/search?page_size=20&page_token=page-2");
        handler.LastBody.Should().Contain("\"query\":\"incident\"");
        handler.LastBody.Should().Contain("\"chat_ids\"");
        handler.LastBody.Should().Contain("\"from_ids\"");
        handler.LastBody.Should().Contain("\"include_attachment_types\"");
        handler.LastBody.Should().Contain("\"time_range\"");

        await client.BatchGetMessagesAsync(
            "token-123",
            new LarkMessagesBatchGetRequest(["om_1", "om_2"]),
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/mget?card_msg_content_type=raw_card_content&message_ids=om_1&message_ids=om_2");
    }

    [Fact]
    public async Task LarkNyxClient_DownloadMessageResource_ShouldShapeImageResourceProxyRequest()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            response.Content.Headers.ContentDisposition =
                System.Net.Http.Headers.ContentDispositionHeaderValue.Parse("attachment; filename=\"receipt.png\"");
            return response;
        });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        var result = await client.DownloadMessageResourceAsync(
            "token-123",
            new LarkMessageResourceDownloadRequest("om_123", "img_v3_abc", LarkMessageResourceKind.Image),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Content.Should().Equal(payload);
        result.ContentType.Should().Be("image/png");
        result.FileName.Should().Be("receipt.png");
        result.HttpStatus.Should().Be(200);
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_123/resources/img_v3_abc?type=image");
        handler.LastBody.Should().BeNull();
    }

    [Fact]
    public async Task LarkNyxClient_DownloadMessageResource_ShouldShapeFileResourceProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([9, 8, 7]),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.DownloadMessageResourceAsync(
            "token-123",
            new LarkMessageResourceDownloadRequest("om_123", "file_v3_abc", LarkMessageResourceKind.File),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_123/resources/file_v3_abc?type=file");
    }

    [Theory]
    [InlineData("", "img_v3_abc", LarkMessageResourceKind.Image)]
    [InlineData("msg_123", "img_v3_abc", LarkMessageResourceKind.Image)]
    [InlineData("om_123", "", LarkMessageResourceKind.Image)]
    [InlineData("om_123", "img_v3_abc", (LarkMessageResourceKind)99)]
    public async Task LarkNyxClient_DownloadMessageResource_ShouldValidateInputs(
        string messageId,
        string resourceKey,
        LarkMessageResourceKind kind)
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await FluentActions.Invoking(() => client.DownloadMessageResourceAsync(
                "token-123",
                new LarkMessageResourceDownloadRequest(messageId, resourceKey, kind),
                CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task LarkNyxClient_SearchChats_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"items":[],"total":0}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.SearchChatsAsync(
            "token-123",
            new LarkChatSearchRequest("team-alpha", ["ou_1"], ["public_joined"], true, false, 10, "page-1"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v2/chats/search?page_size=10&page_token=page-1");

        var body = handler.LastBody;
        body.Should().Contain("\"query\":\"\\u0022team-alpha\\u0022\"");
        body.Should().Contain("member_ids");
        body.Should().Contain("search_types");
    }

    [Fact]
    public async Task LarkNyxClient_AppendSheetRows_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"updates":{"updatedRange":"sheet_1!A1:B1"}}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.AppendSheetRowsAsync(
            "token-123",
            new LarkSheetAppendRowsRequest(
                "shtcn_123",
                "sheet_1!A1:A1",
                [["Alice", "100"]]),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/sheets/v2/spreadsheets/shtcn_123/values_append");

        var body = handler.LastBody;
        body.Should().Contain("valueRange");
        body.Should().Contain("sheet_1!A1:A1");
        body.Should().Contain("Alice");
    }

    [Fact]
    public async Task LarkNyxClient_ListApprovalTasks_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"tasks":[],"count":0}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.ListApprovalTasksAsync(
            "token-123",
            new LarkApprovalTaskQueryRequest("1", "def_1", "zh-CN", 10, "page-1", "open_id"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks?topic=1&page_size=10&definition_code=def_1&locale=zh-CN&page_token=page-1&user_id_type=open_id");
    }

    [Fact]
    public async Task LarkNyxClient_GetApprovalInstance_ShapesProxyRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"instance_code":"inst_1","status":"1"}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.GetApprovalInstanceAsync(
            "token-123",
            new LarkApprovalInstanceGetRequest("inst_1", "zh-CN", "open_id"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/instances/inst_1?locale=zh-CN&user_id_type=open_id");
        handler.LastBody.Should().BeNull();
    }

    [Fact]
    public async Task LarkNyxClient_ActOnApprovalTask_ShapesTransferRequest()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.ActOnApprovalTaskAsync(
            "token-123",
            new LarkApprovalTaskActionRequest("transfer", "inst_1", "task_1", "lark-user-1", "reassign", null, "ou_target", "open_id"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks/forward?user_id_type=open_id");
        handler.LastBody.Should().Contain("\"user_id\":\"lark-user-1\"");
        handler.LastBody.Should().Contain("\"transfer_user_id\":\"ou_target\"");
    }

    [Fact]
    public async Task LarkNyxClient_DocxCreateAppendPermission_ShapesProxyRequests()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.CreateDocxDocumentAsync(
            "token-123",
            new LarkDocxCreateRequest("Daily report"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/docx/v1/documents");
        handler.LastBody.Should().Contain("\"title\":\"Daily report\"");

        const int ExpectedDocxBlockTextLength = 2_000;
        var longText = new string('x', ExpectedDocxBlockTextLength + 5);
        await client.AppendDocxTextBlocksAsync(
            "token-123",
            new LarkDocxAppendBlocksRequest("doccn_123", longText),
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/docx/v1/documents/doccn_123/blocks/doccn_123/children");
        using (var appendBody = JsonDocument.Parse(handler.LastBody!))
        {
            var children = appendBody.RootElement.GetProperty("children");
            children.GetArrayLength().Should().Be(2);
            children[0].GetProperty("text").GetProperty("elements")[0].GetProperty("text_run").GetProperty("content").GetString()!
                .Length.Should().Be(ExpectedDocxBlockTextLength);
            children[1].GetProperty("text").GetProperty("elements")[0].GetProperty("text_run").GetProperty("content").GetString()!
                .Length.Should().Be(5);
        }

        await client.SetDrivePermissionAsync(
            "token-123",
            new LarkDrivePermissionRequest("doccn_123", LarkDocxVisibility.Editable, "oc_chat_1", "chat_id"),
            CancellationToken.None);

        handler.LastRequest!.Method.Method.Should().Be("PATCH");
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/drive/v1/permissions/doccn_123/public?type=docx");
        handler.LastBody.Should().Contain("\"link_share_entity\":\"tenant_editable\"");
        handler.LastBody.Should().Contain("\"receive_id\":\"oc_chat_1\"");
        // share_entity must use Lark's enum (anyone | same_tenant | only_full_access), never the
        // security/comment "anyone_can_view" value which Lark rejects with a 400 param error.
        handler.LastBody.Should().Contain("\"share_entity\":\"same_tenant\"");
        handler.LastBody.Should().NotContain("\"share_entity\":\"anyone_can_view\"");
    }

    [Fact]
    public async Task LarkNyxClient_DriveMediaUpload_UsesFixedMultipartProxyShape()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"file_token":"file_123"}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("upload bytes"));

        await client.UploadDriveMediaAsync(
            "token-123",
            new LarkDriveMediaUploadRequest(
                "report.txt",
                "doc_file",
                "doccn_123",
                12,
                "text/plain",
                content,
                "checksum-1",
                """{"source":"workflow"}"""),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/drive/v1/medias/upload_all");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("token-123");
        handler.LastRequest.Content!.Headers.ContentType!.MediaType.Should().Be("multipart/form-data");
        handler.LastRequest.Content.Headers.ContentType!.ToString().Should().Contain("boundary=");
        handler.LastBody.Should().Contain("""name=file_name""");
        handler.LastBody.Should().Contain("report.txt");
        handler.LastBody.Should().Contain("""name=parent_type""");
        handler.LastBody.Should().Contain("doc_file");
        handler.LastBody.Should().Contain("""name=parent_node""");
        handler.LastBody.Should().Contain("doccn_123");
        handler.LastBody.Should().Contain("""name=size""");
        handler.LastBody.Should().Contain("12");
        handler.LastBody.Should().Contain("""name=checksum""");
        handler.LastBody.Should().Contain("checksum-1");
        handler.LastBody.Should().Contain("""name=extra""");
        handler.LastBody.Should().Contain("""{"source":"workflow"}""");
        handler.LastBody.Should().Contain("""name=file; filename=report.txt""");
        handler.LastBody.Should().Contain("Content-Type: text/plain");
        handler.LastBody.Should().Contain("upload bytes");
    }

    [Fact]
    public async Task WorkflowFileSubmit_MissingBearer_ShouldFailWithoutOpeningArtifact()
    {
        var port = new RecordingWorkflowFileArtifactReadPort(BuildFileRef(sizeBytes: 5));
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(BuildFileRef(sizeBytes: 5)),
            bearerToken: null));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("missing_bearer");
        port.OpenCount.Should().Be(0);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Theory]
    [InlineData(false, "api-lark-bot")]
    [InlineData(true, " ")]
    public async Task WorkflowFileSubmitSource_ShouldReturnEmpty_WhenDisabledOrProviderSlugMissing(
        bool enabled,
        string providerSlug)
    {
        var source = CreateWorkflowFileSubmitSource(
            new LarkToolOptions
            {
                EnableWorkflowFileSubmit = enabled,
                ProviderSlug = providerSlug,
            },
            new StubLarkNyxClient(),
            new RecordingWorkflowFileArtifactReadPort(BuildFileRef(sizeBytes: 5)));

        var tools = await source.GetToolsAsync();

        tools.Should().BeEmpty();
    }

    [Theory]
    [InlineData("bad-target", "doc_file", "doccn_123", "invalid_target")]
    [InlineData("lark_drive_media", "folder", "doccn_123", "unsupported_parent_type")]
    [InlineData("lark_drive_media", "doc_file", " ", "missing_parent_node")]
    public async Task WorkflowFileSubmit_InvalidTargetParentTypeOrParentNode_ShouldFailBeforeProviderCall(
        string target,
        string parentType,
        string parentNode,
        string expectedError)
    {
        var port = new RecordingWorkflowFileArtifactReadPort(BuildFileRef(sizeBytes: 5));
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(
                BuildFileRef(sizeBytes: 5),
                target: target,
                parentType: parentType,
                parentNode: parentNode),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
        result.ResultJson.Should().NotContain(parentType);
        result.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        port.OpenCount.Should().Be(0);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowFileSubmit_MissingFileRef_ShouldFailBeforeProviderCall()
    {
        var port = new RecordingWorkflowFileArtifactReadPort(BuildFileRef(sizeBytes: 5));
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(BuildFileRef(fileId: "", artifactId: "", sizeBytes: 5)),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_file_ref");
        port.OpenCount.Should().Be(0);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "scope-1")]
    [InlineData("run-other", "scope-1")]
    [InlineData("run-1", "scope-other")]
    public async Task WorkflowFileSubmit_InvalidFileScope_ShouldFailBeforeProviderCall(
        string? ownerRunId,
        string? ownerScopeId)
    {
        var fileRef = BuildFileRef(sizeBytes: 5, ownerRunId: ownerRunId, ownerScopeId: ownerScopeId);
        var port = new RecordingWorkflowFileArtifactReadPort(fileRef);
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(fileRef),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_file_scope");
        port.OpenCount.Should().Be(0);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowFileSubmit_ArtifactUnavailable_ShouldMapToArtifactUnavailable()
    {
        var port = new RecordingWorkflowFileArtifactReadPort(BuildFileRef(sizeBytes: 5))
        {
            OpenException = new FileNotFoundException("missing"),
        };
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(BuildFileRef(sizeBytes: 5)),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("artifact_unavailable");
        port.OpenCount.Should().Be(1);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowFileSubmit_ArtifactInvalidOperation_ShouldNotEchoStorageDetail()
    {
        var port = new RecordingWorkflowFileArtifactReadPort(BuildFileRef(sizeBytes: 5))
        {
            OpenException = new InvalidOperationException("""{"body":"bad upstream","data_base64":"AAAA"}"""),
        };
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(BuildFileRef(sizeBytes: 5)),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("artifact_unavailable");
        document.RootElement.GetProperty("detail").GetString().Should().Be("Workflow file artifact content could not be read.");
        result.ResultJson.Should().NotContain("bad upstream");
        result.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        port.OpenCount.Should().Be(1);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20 * 1024 * 1024 + 1)]
    public async Task WorkflowFileSubmit_InvalidOrOversizeDescriptor_ShouldFailBeforeProviderCall(long sizeBytes)
    {
        var port = new RecordingWorkflowFileArtifactReadPort(BuildFileRef(sizeBytes: sizeBytes));
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(BuildFileRef(sizeBytes: sizeBytes)),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be(sizeBytes <= 0 ? "invalid_file_size" : "file_too_large");
        port.OpenCount.Should().Be(0);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowFileSubmit_ArtifactSizeMismatch_ShouldFailBeforeProviderCall()
    {
        var requested = BuildFileRef(sizeBytes: 5);
        var port = new RecordingWorkflowFileArtifactReadPort(BuildFileRef(sizeBytes: 6), Encoding.UTF8.GetBytes("123456"));
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(requested),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("artifact_size_mismatch");
        port.OpenCount.Should().Be(0);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowFileSubmit_UnsupportedMediaType_ShouldFailAfterDescriptorReadBeforeOpeningArtifact()
    {
        var requested = BuildFileRef(mediaType: "text/plain", sizeBytes: 5);
        var descriptor = BuildFileRef(mediaType: "application/x-msdownload", sizeBytes: 5);
        var port = new RecordingWorkflowFileArtifactReadPort(descriptor);
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(requested),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("unsupported_media_type");
        result.ResultJson.Should().NotContain(descriptor.MediaType);
        result.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        port.OpenCount.Should().Be(0);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowFileSubmit_OwnerlessDescriptor_ShouldFailBeforeOpeningArtifact()
    {
        var requested = BuildFileRef(sizeBytes: 5, ownerRunId: "run-1", ownerScopeId: "scope-1");
        var descriptor = BuildFileRef(sizeBytes: 5, ownerRunId: null, ownerScopeId: null);
        var port = new RecordingWorkflowFileArtifactReadPort(descriptor);
        var client = new StubLarkNyxClient();
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(requested),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_file_scope");
        port.OpenCount.Should().Be(0);
        client.LastDriveMediaUploadRequest.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowFileSubmit_Success_ShouldReturnTypedTokenFactsOnly()
    {
        var fileRef = BuildFileRef(fileName: "descriptor.txt", mediaType: "text/plain", sizeBytes: 12);
        var port = new RecordingWorkflowFileArtifactReadPort(fileRef, Encoding.UTF8.GetBytes("upload bytes"));
        var client = new StubLarkNyxClient
        {
            DriveMediaUploadResponse = """{"code":0,"msg":"ok","data":{"file_token":"file_123"}}""",
        };
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(
                fileRef,
                fileName: "argument.txt",
                checksum: "checksum-1",
                extra: """{"source":"workflow"}"""),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("provider").GetString().Should().Be("lark");
        root.GetProperty("target").GetString().Should().Be("lark_drive_media");
        root.GetProperty("file_token").GetString().Should().Be("file_123");
        root.GetProperty("parent_type").GetString().Should().Be("doc_file");
        root.GetProperty("parent_node").GetString().Should().Be("doccn_123");
        root.GetProperty("file_name").GetString().Should().Be("argument.txt");
        root.GetProperty("size_bytes").GetInt64().Should().Be(12);
        result.ResultJson.Should().NotContain("upload bytes");
        result.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        result.ResultJson.Contains("data_base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        client.LastDriveMediaUploadToken.Should().Be("token-123");
        client.LastDriveMediaUploadRequest.Should().NotBeNull();
        client.LastDriveMediaUploadRequest!.ParentType.Should().Be("doc_file");
        client.LastDriveMediaUploadRequest.ParentNode.Should().Be("doccn_123");
        client.LastDriveMediaUploadRequest.FileName.Should().Be("argument.txt");
        client.LastDriveMediaUploadRequest.Size.Should().Be(12);
        client.LastDriveMediaUploadRequest.ContentType.Should().Be("text/plain");
        client.LastDriveMediaUploadRequest.Checksum.Should().Be("checksum-1");
        client.LastDriveMediaUploadRequest.Extra.Should().Be("""{"source":"workflow"}""");
    }

    [Theory]
    [InlineData("""{"code":999,"msg":"denied"}""", "lark_error")]
    [InlineData("""{"error":true,"status":502,"message":"gateway","body":"bad upstream"}""", "nyx_proxy_error")]
    public async Task WorkflowFileSubmit_ProviderFailures_ShouldFailClosedWithoutEchoingBody(
        string providerResponse,
        string expectedError)
    {
        var fileRef = BuildFileRef(sizeBytes: 12);
        var port = new RecordingWorkflowFileArtifactReadPort(fileRef, Encoding.UTF8.GetBytes("upload bytes"));
        var client = new StubLarkNyxClient { DriveMediaUploadResponse = providerResponse };
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(fileRef),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
        document.RootElement.GetProperty("detail").GetString().Should().NotContain("bad upstream");
        document.RootElement.GetProperty("detail").GetString().Should().NotContain("denied");
        document.RootElement.GetProperty("detail").GetString().Should().NotContain("gateway");
        document.RootElement.TryGetProperty("msg", out _).Should().BeFalse();
        document.RootElement.GetProperty("detail").GetString()!.Contains("base64", StringComparison.OrdinalIgnoreCase)
            .Should().BeFalse();
        document.RootElement.TryGetProperty("file_token", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WorkflowFileSubmit_ProviderException_ShouldFailClosedWithoutEchoingMessage()
    {
        var fileRef = BuildFileRef(sizeBytes: 12);
        var port = new RecordingWorkflowFileArtifactReadPort(fileRef, Encoding.UTF8.GetBytes("upload bytes"));
        var client = new StubLarkNyxClient
        {
            DriveMediaUploadException = new InvalidOperationException("""{"body":"bad upstream","data_base64":"AAAA"}"""),
        };
        var tool = await GetWorkflowFileSubmitToolAsync(port, client);

        var result = await tool.ExecuteAsync(NewWorkflowToolRequest(
            BuildWorkflowFileSubmitArguments(fileRef),
            bearerToken: "token-123"));

        using var document = JsonDocument.Parse(result.ResultJson);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetString().Should().Be("provider_call_failed");
        document.RootElement.GetProperty("detail").GetString().Should().Be("Lark media upload request failed.");
        result.ResultJson.Should().NotContain("bad upstream");
        result.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        document.RootElement.TryGetProperty("file_token", out _).Should().BeFalse();
    }

    [Fact]
    public void LarkNyxClient_NormalizeChatSearchQuery_ShouldKeepOriginalWhenUnquotingFails()
    {
        var method = typeof(LarkNyxClient).GetMethod(
            "NormalizeChatSearchQuery",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (string)method.Invoke(null, new object?[] { "team-\\" })!;

        result.Should().Be(JsonSerializer.Serialize("team-\\"));
    }

    private sealed class StubLarkNyxClient : ILarkNyxClient
    {
        public string SendResponse { get; set; } = """{"code":0,"data":{}}""";
        public string ReplyResponse { get; set; } = """{"code":0,"data":{}}""";
        public string ReactionCreateResponse { get; set; } = """{"code":0,"data":{}}""";
        public string ReactionListResponse { get; set; } = """{"code":0,"data":{"items":[]}}""";
        public string ReactionDeleteResponse { get; set; } = """{"code":0,"data":{}}""";
        public string MessageSearchResponse { get; set; } = """{"code":0,"data":{"items":[],"count":0}}""";
        public string MessagesBatchGetResponse { get; set; } = """{"code":0,"data":{"items":[]}}""";
        public LarkMessageResourceDownloadResult MessageResourceResponse { get; set; } = new(
            true,
            [],
            "application/octet-stream");
        public string SearchResponse { get; set; } = """{"code":0,"data":{"items":[],"total":0}}""";
        public string AppendSheetResponse { get; set; } = """{"code":0,"data":{"updates":{}}}""";
        public string ApprovalListResponse { get; set; } = """{"code":0,"data":{"tasks":[],"count":0}}""";
        public string ApprovalGetResponse { get; set; } = """{"code":0,"data":{"instance_code":"inst_default","status":"1"}}""";
        public string ApprovalActionResponse { get; set; } = """{"code":0,"data":{}}""";
        public string DocxCreateResponse { get; set; } = """{"code":0,"data":{"document":{"document_id":"doccn_default","url":"https://example.feishu.cn/docx/doccn_default"}}}""";
        public string DocxAppendResponse { get; set; } = """{"code":0,"data":{}}""";
        public string DrivePermissionResponse { get; set; } = """{"code":0,"data":{}}""";
        public string DriveMediaUploadResponse { get; set; } = """{"code":0,"data":{"file_token":"file_default"}}""";
        public Exception? DriveMediaUploadException { get; set; }

        public string? LastSendToken { get; private set; }
        public string? LastDocxCreateToken { get; private set; }
        public string? LastDriveMediaUploadToken { get; private set; }
        public LarkSendMessageRequest? LastSendRequest { get; private set; }
        public LarkReplyMessageRequest? LastReplyRequest { get; private set; }
        public LarkMessageReactionRequest? LastReactionRequest { get; private set; }
        public LarkMessageReactionListRequest? LastReactionListRequest { get; private set; }
        public LarkMessageReactionDeleteRequest? LastReactionDeleteRequest { get; private set; }
        public LarkMessageSearchRequest? LastMessageSearchRequest { get; private set; }
        public LarkMessagesBatchGetRequest? LastBatchGetRequest { get; private set; }
        public LarkMessageResourceDownloadRequest? LastMessageResourceRequest { get; private set; }
        public LarkChatSearchRequest? LastSearchRequest { get; private set; }
        public LarkSheetAppendRowsRequest? LastSheetAppendRequest { get; private set; }
        public LarkApprovalTaskQueryRequest? LastApprovalQueryRequest { get; private set; }
        public LarkApprovalInstanceGetRequest? LastApprovalGetRequest { get; private set; }
        public LarkApprovalTaskActionRequest? LastApprovalActionRequest { get; private set; }
        public LarkDocxCreateRequest? LastDocxCreateRequest { get; private set; }
        public LarkDocxAppendBlocksRequest? LastDocxAppendRequest { get; private set; }
        public LarkDrivePermissionRequest? LastDrivePermissionRequest { get; private set; }
        public LarkDriveMediaUploadRequest? LastDriveMediaUploadRequest { get; private set; }

        public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct)
        {
            LastSendToken = token;
            LastSendRequest = request;
            return Task.FromResult(SendResponse);
        }

        public Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct)
        {
            LastReplyRequest = request;
            return Task.FromResult(ReplyResponse);
        }

        public Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct)
        {
            LastReactionRequest = request;
            return Task.FromResult(ReactionCreateResponse);
        }

        public Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct)
        {
            LastReactionListRequest = request;
            return Task.FromResult(ReactionListResponse);
        }

        public Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct)
        {
            LastReactionDeleteRequest = request;
            return Task.FromResult(ReactionDeleteResponse);
        }

        public Task<string> SearchMessagesAsync(string token, LarkMessageSearchRequest request, CancellationToken ct)
        {
            LastMessageSearchRequest = request;
            return Task.FromResult(MessageSearchResponse);
        }

        public Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct)
        {
            LastBatchGetRequest = request;
            return Task.FromResult(MessagesBatchGetResponse);
        }

        public Task<LarkMessageResourceDownloadResult> DownloadMessageResourceAsync(
            string token,
            LarkMessageResourceDownloadRequest request,
            CancellationToken ct)
        {
            LastMessageResourceRequest = request;
            return Task.FromResult(MessageResourceResponse);
        }

        public Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct)
        {
            LastSearchRequest = request;
            return Task.FromResult(SearchResponse);
        }

        public Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct)
        {
            LastSheetAppendRequest = request;
            return Task.FromResult(AppendSheetResponse);
        }

        public Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct)
        {
            LastApprovalQueryRequest = request;
            return Task.FromResult(ApprovalListResponse);
        }

        public Task<string> GetApprovalInstanceAsync(string token, LarkApprovalInstanceGetRequest request, CancellationToken ct)
        {
            LastApprovalGetRequest = request;
            return Task.FromResult(ApprovalGetResponse);
        }

        public Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct)
        {
            LastApprovalActionRequest = request;
            return Task.FromResult(ApprovalActionResponse);
        }

        public Task<string> CreateDocxDocumentAsync(string token, LarkDocxCreateRequest request, CancellationToken ct)
        {
            LastDocxCreateToken = token;
            LastDocxCreateRequest = request;
            return Task.FromResult(DocxCreateResponse);
        }

        public Task<string> AppendDocxTextBlocksAsync(string token, LarkDocxAppendBlocksRequest request, CancellationToken ct)
        {
            LastDocxAppendRequest = request;
            return Task.FromResult(DocxAppendResponse);
        }

        public Task<string> SetDrivePermissionAsync(string token, LarkDrivePermissionRequest request, CancellationToken ct)
        {
            LastDrivePermissionRequest = request;
            return Task.FromResult(DrivePermissionResponse);
        }

        public Task<string> UploadDriveMediaAsync(string token, LarkDriveMediaUploadRequest request, CancellationToken ct)
        {
            LastDriveMediaUploadToken = token;
            LastDriveMediaUploadRequest = request;
            if (DriveMediaUploadException != null)
                throw DriveMediaUploadException;
            return Task.FromResult(DriveMediaUploadResponse);
        }
    }

    private static async Task<IWorkflowTool> GetWorkflowFileSubmitToolAsync(
        IWorkflowFileArtifactReadPort fileArtifacts,
        ILarkNyxClient client)
    {
        var source = CreateWorkflowFileSubmitSource(
            new LarkToolOptions { EnableWorkflowFileSubmit = true },
            client,
            fileArtifacts);
        var tools = await source.GetToolsAsync();
        return tools.Should().ContainSingle(x => x.Name == "workflow_file_submit").Subject;
    }

    private static LarkWorkflowFileSubmitToolSource CreateWorkflowFileSubmitSource(
        LarkToolOptions options,
        ILarkNyxClient client,
        IWorkflowFileArtifactReadPort? fileArtifacts)
    {
        var services = new ServiceCollection();
        if (fileArtifacts != null)
            services.AddSingleton(fileArtifacts);
        return new LarkWorkflowFileSubmitToolSource(options, client, services.BuildServiceProvider());
    }

    private static WorkflowToolExecutionRequest NewWorkflowToolRequest(string argumentsJson, string? bearerToken) =>
        new(
            ArgumentsJson: argumentsJson,
            RunId: "run-1",
            StepId: "step-1",
            ExecutionId: "exec-1",
            CallId: "call-1",
            ScopeId: "scope-1",
            CallerCredential: new Aevatar.Workflow.Abstractions.WorkflowCallerCredential
            {
                BearerToken = bearerToken ?? string.Empty,
            });

    private static WorkflowFileRef BuildFileRef(
        string fileId = "file-1",
        string artifactId = "artifact-1",
        string fileName = "report.txt",
        string mediaType = "text/plain",
        long sizeBytes = 12,
        string? ownerRunId = "run-1",
        string? ownerScopeId = "scope-1") =>
        new()
        {
            FileId = fileId,
            ArtifactId = artifactId,
            SourceKind = WorkflowFileSourceKind.ChatInput,
            FileName = fileName,
            MediaType = mediaType,
            SizeBytes = sizeBytes,
            Sha256 = "sha256-value",
            CreatedAtUnixMs = 1,
            ExpiresAtUnixMs = 2,
            OwnerRunId = ownerRunId,
            OwnerScopeId = ownerScopeId,
        };

    private static string BuildWorkflowFileSubmitArguments(
        WorkflowFileRef fileRef,
        string target = "lark_drive_media",
        string parentType = "doc_file",
        string parentNode = "doccn_123",
        string? fileName = null,
        string? checksum = null,
        string? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["target"] = target,
            ["parent_type"] = parentType,
            ["parent_node"] = parentNode,
            ["file_ref"] = new Dictionary<string, object?>
            {
                ["file_id"] = fileRef.FileId,
                ["artifact_id"] = fileRef.ArtifactId,
                ["source_kind"] = fileRef.SourceKind.ToString(),
                ["source_message_id"] = fileRef.SourceMessageId,
                ["source_resource_key"] = fileRef.SourceResourceKey,
                ["file_name"] = fileRef.FileName,
                ["media_type"] = fileRef.MediaType,
                ["size_bytes"] = fileRef.SizeBytes,
                ["sha256"] = fileRef.Sha256,
                ["created_at_unix_ms"] = fileRef.CreatedAtUnixMs,
                ["expires_at_unix_ms"] = fileRef.ExpiresAtUnixMs,
                ["owner_run_id"] = fileRef.OwnerRunId,
                ["owner_scope_id"] = fileRef.OwnerScopeId,
            },
        };

        if (fileName != null)
            payload["file_name"] = fileName;
        if (checksum != null)
            payload["checksum"] = checksum;
        if (extra != null)
            payload["extra"] = extra;

        return JsonSerializer.Serialize(payload);
    }

    private sealed class RecordingWorkflowFileArtifactReadPort(
        WorkflowFileRef descriptor,
        byte[]? content = null) : IWorkflowFileArtifactReadPort
    {
        private readonly byte[] _content = content ?? Encoding.UTF8.GetBytes("upload bytes");

        public Exception? OpenException { get; init; }
        public int OpenCount { get; private set; }

        public ValueTask<WorkflowFileRef> DescribeAsync(
            WorkflowFileRef fileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(descriptor);

        public ValueTask<WorkflowFileArtifactContent> OpenReadAsync(
            WorkflowFileRef fileRef,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            if (OpenException != null)
                throw OpenException;

            return ValueTask.FromResult(new WorkflowFileArtifactContent(
                descriptor,
                new MemoryStream(_content, writable: false)));
        }
    }

    private sealed class QueueLLMProvider : ILLMProvider
    {
        private readonly Queue<LLMResponse> _responses;

        public QueueLLMProvider(IEnumerable<LLMResponse> responses)
        {
            _responses = new Queue<LLMResponse>(responses);
        }

        public string Name => "queue";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var response = _responses.Count > 0 ? _responses.Dequeue() : new LLMResponse();

            if (!string.IsNullOrEmpty(response.Content))
                yield return new LLMStreamChunk { DeltaContent = response.Content };

            if (response.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in response.ToolCalls)
                    yield return new LLMStreamChunk { DeltaToolCall = toolCall };
            }

            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = response.Usage,
                FinishReason = response.FinishReason,
            };
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    private sealed class AgentToolRequestContextScope : IDisposable
    {
        private readonly AgentToolExecutionContext? _previous;

        public AgentToolRequestContextScope(AgentToolExecutionContext context)
        {
            _previous = AgentToolRequestContext.Current;
            AgentToolRequestContext.Current = context;
        }

        public void Dispose()
        {
            AgentToolRequestContext.Current = _previous;
        }
    }

    private sealed class AgentToolRequestMetadataScope : IDisposable
    {
        private readonly AgentToolExecutionContext? _previous;

        public AgentToolRequestMetadataScope(
            string? accessToken = null,
            IReadOnlyDictionary<string, string>? extraMetadata = null)
        {
            _previous = AgentToolRequestContext.Current;
            if (string.IsNullOrWhiteSpace(accessToken) && (extraMetadata == null || extraMetadata.Count == 0))
            {
                AgentToolRequestContext.Current = null;
                return;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(accessToken))
                metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken;
            if (extraMetadata != null)
            {
                foreach (var entry in extraMetadata)
                    metadata[entry.Key] = entry.Value;
            }

            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(metadata);
        }

        public void Dispose()
        {
            AgentToolRequestContext.Current = _previous;
        }
    }
}
