using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.Lark.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Application.Abstractions.Runs;
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
                ["channel.delivery.address_id"] = "oc_chat_1",
                ["channel.delivery.address_type"] = "chat_id",
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
    public async Task LarkBaseCreateTool_ShouldGrantSenderFullAccess_AndNotCallPublic()
    {
        var client = new StubLarkNyxClient
        {
            BitableCreateResponse = """{"code":0,"data":{"app":{"app_token":"bascn_123","url":"https://example.feishu.cn/base/bascn_123","default_table_id":"tbl_1"}}}""",
            GrantResourceMemberResponse = """{"code":0,"data":{"member":{"member_id":"ou_req","perm":"full_access"}}}""",
        };
        var tool = new LarkBaseCreateTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.sender_id"] = "ou_req",
            });

        var result = await tool.ExecuteAsync("""{"name":"项目跟踪"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("granted").GetBoolean().Should().BeTrue();
        root.GetProperty("app_token").GetString().Should().Be("bascn_123");
        root.GetProperty("grantee").GetString().Should().Be("ou_req");

        client.LastBitableCreateRequest.Should().Be(new LarkBitableCreateRequest("项目跟踪"));
        client.GrantCallCount.Should().Be(1);
        client.LastGrantRequest.Should().Be(
            new LarkResourceMemberGrantRequest("bascn_123", "bitable", "ou_req", "openid", "full_access"));
        client.LastDrivePermissionRequest.Should().BeNull();
    }

    [Fact]
    public async Task LarkBaseCreateTool_ShouldFallBackToPublic_WhenMemberGrantFails()
    {
        var client = new StubLarkNyxClient
        {
            BitableCreateResponse = """{"code":0,"data":{"app":{"app_token":"bascn_123","url":"https://example.feishu.cn/base/bascn_123"}}}""",
            GrantResourceMemberResponse = """{"code":1254000,"msg":"grant failed"}""",
            DrivePermissionResponse = """{"code":0,"data":{"link_share_entity":"tenant_editable"}}""",
        };
        var tool = new LarkBaseCreateTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["channel.sender_id"] = "ou_req" });

        var result = await tool.ExecuteAsync("""{"name":"Tracker"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("granted").GetBoolean().Should().BeFalse();
        root.GetProperty("fallback_to_public").GetBoolean().Should().BeTrue();
        client.LastGrantRequest.Should().NotBeNull();
        client.LastDrivePermissionRequest.Should().NotBeNull();
        client.LastDrivePermissionRequest!.ObjType.Should().Be("bitable");
    }

    [Fact]
    public async Task LarkBaseCreateTool_ShouldFallBackToPublic_WhenNoSender()
    {
        var client = new StubLarkNyxClient
        {
            BitableCreateResponse = """{"code":0,"data":{"app":{"app_token":"bascn_123","url":"https://example.feishu.cn/base/bascn_123"}}}""",
            DrivePermissionResponse = """{"code":0,"data":{"link_share_entity":"tenant_editable"}}""",
        };
        var tool = new LarkBaseCreateTool(client);

        using var _ = new AgentToolRequestMetadataScope("token-123");

        var result = await tool.ExecuteAsync("""{"name":"Tracker"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("granted").GetBoolean().Should().BeFalse();
        root.GetProperty("fallback_to_public").GetBoolean().Should().BeTrue();
        root.GetProperty("reason").GetString().Should().Be("no_sender");
        client.GrantCallCount.Should().Be(0);
        client.LastDrivePermissionRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task LarkBaseCreateTool_ShouldFail_WhenNameMissing()
    {
        var tool = new LarkBaseCreateTool(new StubLarkNyxClient());
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var result = await tool.ExecuteAsync("""{"name":"  "}""");

        result.Should().Contain("\"success\":false");
        result.Should().Contain("name is required");
    }

    [Fact]
    public async Task LarkDocxCreateTool_ShouldAlsoGrantSender_AndKeepTenantShare()
    {
        var client = new StubLarkNyxClient
        {
            DocxCreateResponse = """{"code":0,"data":{"document":{"document_id":"doccn_123","url":"https://example.feishu.cn/docx/doccn_123"}}}""",
            DocxAppendResponse = """{"code":0,"data":{"children":[]}}""",
            DrivePermissionResponse = """{"code":0,"data":{"link_share_entity":"tenant_readable"}}""",
            GrantResourceMemberResponse = """{"code":0,"data":{"member":{"member_id":"ou_req","perm":"full_access"}}}""",
        };
        var tool = new LarkDocxCreateTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["channel.sender_id"] = "ou_req" });

        var result = await tool.ExecuteAsync("""{"title":"Daily","markdown_text":"# Daily"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("granted_to_sender").GetBoolean().Should().BeTrue();
        client.LastDrivePermissionRequest.Should().NotBeNull();
        client.LastDrivePermissionRequest!.ObjType.Should().Be("docx");
        client.GrantCallCount.Should().Be(1);
        client.LastGrantRequest.Should().Be(
            new LarkResourceMemberGrantRequest("doccn_123", "docx", "ou_req", "openid", "full_access"));
    }

    [Fact]
    public async Task LarkResourceGrantTool_ShouldDefaultToSender_AndGrant()
    {
        var client = new StubLarkNyxClient
        {
            GrantResourceMemberResponse = """{"code":0,"data":{"member":{"member_id":"ou_req","perm":"full_access"}}}""",
        };
        var tool = new LarkResourceGrantTool(client);

        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["channel.sender_id"] = "ou_req" });

        var result = await tool.ExecuteAsync("""{"token":"bascn_123","obj_type":"bitable"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("member_id").GetString().Should().Be("ou_req");
        client.LastGrantRequest.Should().Be(
            new LarkResourceMemberGrantRequest("bascn_123", "bitable", "ou_req", "openid", "full_access"));
        tool.RequiresApproval("""{"token":"bascn_123","obj_type":"bitable"}""").Should().BeNull();
    }

    [Fact]
    public async Task LarkResourceGrantTool_ShouldRejectMentionPlaceholderAndBadShape()
    {
        var client = new StubLarkNyxClient();
        var tool = new LarkResourceGrantTool(client);
        using var _ = new AgentToolRequestMetadataScope("token-123");

        var placeholder = await tool.ExecuteAsync("""{"token":"bascn_1","obj_type":"bitable","member_id":"@_user_1"}""");
        placeholder.Should().Contain("\"success\":false");
        placeholder.Should().Contain("@_user_N");

        var displayName = await tool.ExecuteAsync("""{"token":"bascn_1","obj_type":"bitable","member_id":"Zhang San"}""");
        displayName.Should().Contain("\"success\":false");

        var notOpenId = await tool.ExecuteAsync("""{"token":"bascn_1","obj_type":"bitable","member_id":"abc123"}""");
        notOpenId.Should().Contain("\"success\":false");
        notOpenId.Should().Contain("ou_");

        client.GrantCallCount.Should().Be(0);
    }

    [Fact]
    public void LarkResourceGrantTool_ShouldRequireApproval_ForNonSenderOrNonDefault()
    {
        var tool = new LarkResourceGrantTool(new StubLarkNyxClient());
        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["channel.sender_id"] = "ou_req" });

        tool.RequiresApproval("""{"token":"t","obj_type":"bitable","member_id":"ou_other"}""").Should().BeTrue();
        tool.RequiresApproval("""{"token":"t","obj_type":"bitable","perm":"edit"}""").Should().BeTrue();
        tool.RequiresApproval("""{"token":"t","obj_type":"bitable","member_type":"email"}""").Should().BeTrue();
        tool.RequiresApproval("""{"token":"t","obj_type":"bitable"}""").Should().BeNull();
    }

    [Fact]
    public async Task LarkResourceGrantTool_ShouldValidateObjTypeAndPerm()
    {
        var client = new StubLarkNyxClient();
        var tool = new LarkResourceGrantTool(client);
        using var _ = new AgentToolRequestMetadataScope(
            "token-123",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["channel.sender_id"] = "ou_req" });

        var badObjType = await tool.ExecuteAsync("""{"token":"t","obj_type":"banana"}""");
        badObjType.Should().Contain("\"success\":false");
        badObjType.Should().Contain("obj_type");

        var badPerm = await tool.ExecuteAsync("""{"token":"t","obj_type":"bitable","perm":"god_mode"}""");
        badPerm.Should().Contain("\"success\":false");
        badPerm.Should().Contain("perm");

        client.GrantCallCount.Should().Be(0);
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
    public async Task LarkMessageResourceFetchAdapter_ShouldMapWorkflowRouteToBinaryDownload()
    {
        var client = new StubLarkNyxClient
        {
            MessageResourceResponse = new LarkMessageResourceDownloadResult(
                Succeeded: true,
                Content: Encoding.UTF8.GetBytes("downloaded"),
                ContentType: "image/png",
                FileName: "picture.png"),
        };
        var adapter = new LarkMessageResourceFetchAdapter(client);

        var result = await adapter.FetchAsync(new WorkflowConnectedServiceResourceFetchRequest(
            Route: new WorkflowConnectedServiceResourceFetchRoute("lark", "message_resource_download", "image"),
            SourceMessageId: "om_123",
            SourceResourceKey: "img_v3_456",
            CallerCredential: new WorkflowCallerCredential("token-123")));

        result.Succeeded.Should().BeTrue();
        result.Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes("downloaded"));
        result.MediaType.Should().Be("image/png");
        result.FileName.Should().Be("picture.png");
        client.LastMessageResourceToken.Should().Be("token-123");
        client.LastMessageResourceRequest.Should().Be(new LarkMessageResourceDownloadRequest(
            "om_123",
            "img_v3_456",
            LarkMessageResourceKind.Image));
    }

    [Fact]
    public async Task LarkMessageResourceFetchAdapter_ShouldMapFileRouteToBinaryDownload()
    {
        var client = new StubLarkNyxClient
        {
            MessageResourceResponse = new LarkMessageResourceDownloadResult(
                Succeeded: true,
                Content: Encoding.UTF8.GetBytes("downloaded-file"),
                ContentType: "application/pdf",
                FileName: "receipt.pdf"),
        };
        var adapter = new LarkMessageResourceFetchAdapter(client);

        var result = await adapter.FetchAsync(new WorkflowConnectedServiceResourceFetchRequest(
            Route: new WorkflowConnectedServiceResourceFetchRoute("lark", "message_resource_download", "file"),
            SourceMessageId: "om_123",
            SourceResourceKey: "file_v3_456",
            CallerCredential: new WorkflowCallerCredential("token-123")));

        result.Succeeded.Should().BeTrue();
        result.Content.ToArray().Should().Equal(Encoding.UTF8.GetBytes("downloaded-file"));
        result.MediaType.Should().Be("application/pdf");
        result.FileName.Should().Be("receipt.pdf");
        client.LastMessageResourceToken.Should().Be("token-123");
        client.LastMessageResourceRequest.Should().Be(new LarkMessageResourceDownloadRequest(
            "om_123",
            "file_v3_456",
            LarkMessageResourceKind.File));
    }

    [Fact]
    public async Task LarkMessageResourceFetchAdapter_ShouldFailClosedForWrongRoute()
    {
        var client = new StubLarkNyxClient();
        var adapter = new LarkMessageResourceFetchAdapter(client);

        Func<Task> act = () => adapter.FetchAsync(new WorkflowConnectedServiceResourceFetchRequest(
            Route: new WorkflowConnectedServiceResourceFetchRoute("nyxid", "message_resource_download", "image"),
            SourceMessageId: "om_123",
            SourceResourceKey: "img_v3_456",
            CallerCredential: new WorkflowCallerCredential("token-123"))).AsTask();

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*lark/message_resource_download/image*");
        client.LastMessageResourceRequest.Should().BeNull();
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
                    "count": { "total": 1, "has_more": false },
                    "has_more": false,
                    "page_token": "pt-1",
                    "tasks": [
                      {
                        "task_id": "task_1",
                        "process_id": "1214564545474",
                        "process_code": "inst_1",
                        "title": "Expense Approval",
                        "status": "1",
                        "process_status": "1",
                        "topic": "1",
                        "definition_code": "def_1",
                        "definition_name": "Expense",
                        "initiators": ["ou_init"],
                        "initiator_names": ["Alice"],
                        "user_id": "ou_owner",
                        "urls": { "pc": "https://applink.example/pc", "mobile": "https://applink.example/mobile" }
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
            ["channel.lark.operator_user_id"] = "lark-user-1",
        });

        try
        {
            var result = await tool.ExecuteAsync("""{"topic":"todo"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("count").GetInt32().Should().Be(1);
            document.RootElement.GetProperty("page_token").GetString().Should().Be("pt-1");
            var tasks = document.RootElement.GetProperty("tasks");
            tasks.GetArrayLength().Should().Be(1);
            tasks[0].GetProperty("topic").GetString().Should().Be("todo");
            tasks[0].GetProperty("status").GetString().Should().Be("todo");
            tasks[0].GetProperty("process_status").GetString().Should().Be("running");
            tasks[0].GetProperty("instance_code").GetString().Should().Be("inst_1");
            tasks[0].GetProperty("definition_code").GetString().Should().Be("def_1");
            tasks[0].GetProperty("initiators")[0].GetString().Should().Be("ou_init");
            tasks[0].GetProperty("initiator_names")[0].GetString().Should().Be("Alice");
            tasks[0].GetProperty("link").GetString().Should().Be("https://applink.example/pc");
            client.LastApprovalQueryRequest.Should().NotBeNull();
            client.LastApprovalQueryRequest!.Topic.Should().Be("1");
            client.LastApprovalQueryRequest.UserId.Should().Be("lark-user-1");
            client.LastApprovalQueryRequest.UserIdType.Should().Be("user_id");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task LarkApprovalsListTool_PinsUserIdFromChannelContext_IgnoringToolArguments()
    {
        var client = new StubLarkNyxClient();
        var tool = new LarkApprovalsListTool(client);

        // Without any channel sender identity the tool must fail closed: api-lark-bot is an
        // org-shared tenant credential, so a caller-supplied user_id would let any org member
        // list anyone's approval tasks.
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await tool.ExecuteAsync("""{"topic":"todo","user_id":"ou_someone_else"}""");
            result.Should().Contain("\"success\":false");
            result.Should().Contain("operator identity");
            client.LastApprovalQueryRequest.Should().BeNull();
        }

        // Lark sender open_id from the typed channel context is an acceptable identity source.
        using (new AgentToolRequestMetadataScope("token-123", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.platform"] = "lark",
            ["channel.sender_id"] = "ou_sender_1",
        }))
        {
            var result = await tool.ExecuteAsync("""{"topic":"todo","user_id":"ou_someone_else"}""");
            result.Should().Contain("\"success\":true");
            client.LastApprovalQueryRequest!.UserId.Should().Be("ou_sender_1");
            client.LastApprovalQueryRequest.UserIdType.Should().Be("open_id");
        }

        // A non-Lark platform sender id must NOT be treated as a Lark user id.
        using (new AgentToolRequestMetadataScope("token-123", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.platform"] = "telegram",
            ["channel.sender_id"] = "tg-sender-1",
        }))
        {
            var result = await tool.ExecuteAsync("""{"topic":"todo"}""");
            result.Should().Contain("\"success\":false");
            result.Should().Contain("operator identity");
        }
    }

    [Fact]
    public async Task LarkApprovalsListTool_ShouldValidateInputs_AndNormalizeAdditionalStatuses()
    {
        var tool = new LarkApprovalsListTool(new StubLarkNyxClient());
        var operatorMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.lark.operator_user_id"] = "lark-user-1",
        };

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"topic":"todo"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123", operatorMetadata))
        {
            (await tool.ExecuteAsync("""{"topic":"unknown"}"""))
                .Should().Contain("topic must be one of");
            (await tool.ExecuteAsync("""{"topic":"todo","page_size":101}"""))
                .Should().Contain("page_size must be between 1 and 100");
        }

        var errorTool = new LarkApprovalsListTool(new StubLarkNyxClient
        {
            ApprovalListResponse = """{"error":true,"status":504,"message":"timeout"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123", operatorMetadata))
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
                    "count": { "total": 5, "has_more": false },
                    "has_more": false,
                    "tasks": [
                      { "task_id": "task_2", "process_code": "inst_2", "status": "2", "topic": "2", "process_status": "2" },
                      { "task_id": "task_3", "process_code": "inst_3", "status": "17", "topic": "3", "process_status": "3" },
                      { "task_id": "task_4", "process_code": "inst_4", "status": "18", "topic": "17", "process_status": "4" },
                      { "task_id": "task_5", "process_code": "inst_5", "status": "33", "topic": "18", "process_status": "5" },
                      { "task_id": "task_6", "process_code": "inst_6", "status": "34", "topic": "99", "process_status": "0" }
                    ]
                  }
                }
                """,
        });
        using (new AgentToolRequestMetadataScope("token-123", operatorMetadata))
        {
            var result = await successTool.ExecuteAsync("""{"topic":"done"}""");

            using var document = JsonDocument.Parse(result);
            var tasks = document.RootElement.GetProperty("tasks");
            tasks[0].GetProperty("topic").GetString().Should().Be("done");
            tasks[0].GetProperty("status").GetString().Should().Be("done");
            tasks[0].GetProperty("process_status").GetString().Should().Be("approved");
            tasks[1].GetProperty("topic").GetString().Should().Be("initiated");
            tasks[1].GetProperty("status").GetString().Should().Be("unread");
            tasks[1].GetProperty("process_status").GetString().Should().Be("rejected");
            tasks[2].GetProperty("topic").GetString().Should().Be("cc_unread");
            tasks[2].GetProperty("status").GetString().Should().Be("read");
            tasks[2].GetProperty("process_status").GetString().Should().Be("withdrawn");
            tasks[3].GetProperty("topic").GetString().Should().Be("cc_read");
            tasks[3].GetProperty("status").GetString().Should().Be("processing");
            tasks[3].GetProperty("process_status").GetString().Should().Be("terminated");
            tasks[4].GetProperty("topic").GetString().Should().Be("99");
            tasks[4].GetProperty("status").GetString().Should().Be("withdrawn");
            tasks[4].GetProperty("process_status").GetString().Should().Be("none");
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
            var result = await tool.ExecuteAsync("""{"action":"transfer","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1"}""");
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
                """{"action":"approve","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1","comment":"LGTM","form_json":"[{\"id\":\"field_1\",\"type\":\"input\",\"value\":\"ok\"}]"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            client.LastApprovalActionRequest.Should().NotBeNull();
            client.LastApprovalActionRequest!.Action.Should().Be("approve");
            client.LastApprovalActionRequest.ApprovalCode.Should().Be("def_1");
            client.LastApprovalActionRequest.UserId.Should().Be("lark-user-1");
            client.LastApprovalActionRequest.UserIdType.Should().Be("user_id");
            client.LastApprovalActionRequest.FormJson.Should().Contain("\"field_1\"");
        }
    }

    [Fact]
    public async Task LarkApprovalsActTool_IgnoresCallerSuppliedUserId_AndPinsOperatorIdentity()
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
            // user_id/user_id_type are no longer tool parameters; a model-supplied value must
            // never reach the Lark request (org-shared credential + self-reported user_id would
            // let any caller approve on behalf of anyone).
            var result = await tool.ExecuteAsync(
                """{"action":"approve","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1","user_id":"ou_someone_else","user_id_type":"open_id","comment":"LGTM"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            client.LastApprovalActionRequest.Should().NotBeNull();
            client.LastApprovalActionRequest!.UserId.Should().Be("lark-user-1");
            client.LastApprovalActionRequest.UserIdType.Should().Be("user_id");
        }
    }

    [Fact]
    public async Task LarkApprovalsActTool_ResolvesOperatorIdentityByPriority()
    {
        var client = new StubLarkNyxClient
        {
            ApprovalActionResponse = """{"code":0,"data":{}}""",
        };
        var tool = new LarkApprovalsActTool(client);
        const string args =
            """{"action":"approve","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1"}""";

        // union_id is tenant-stable and cross-app safe, so it wins over user_id/open_id.
        using (new AgentToolRequestMetadataScope("token-123", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.lark.operator_union_id"] = "on_union_1",
            ["channel.lark.operator_user_id"] = "lark-user-1",
            ["channel.lark.operator_open_id"] = "ou_operator_1",
        }))
        {
            (await tool.ExecuteAsync(args)).Should().Contain("\"success\":true");
            client.LastApprovalActionRequest!.UserId.Should().Be("on_union_1");
            client.LastApprovalActionRequest.UserIdType.Should().Be("union_id");
        }

        // Card-operator open_id is used when no union_id/user_id is available.
        using (new AgentToolRequestMetadataScope("token-123", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.lark.operator_open_id"] = "ou_operator_1",
        }))
        {
            (await tool.ExecuteAsync(args)).Should().Contain("\"success\":true");
            client.LastApprovalActionRequest!.UserId.Should().Be("ou_operator_1");
            client.LastApprovalActionRequest.UserIdType.Should().Be("open_id");
        }

        // Plain chat turns fall back to the inbound Lark sender (union_id over open_id).
        using (new AgentToolRequestMetadataScope("token-123", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.platform"] = "lark",
            ["channel.sender_id"] = "ou_sender_1",
            ["channel.lark.union_id"] = "on_sender_union_1",
        }))
        {
            (await tool.ExecuteAsync(args)).Should().Contain("\"success\":true");
            client.LastApprovalActionRequest!.UserId.Should().Be("on_sender_union_1");
            client.LastApprovalActionRequest.UserIdType.Should().Be("union_id");
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
                        ArgumentsJson = """{"action":"approve","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1","comment":"LGTM"}""",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var request = new LLMRequest
        {
            Messages = [],
            Tools = tools.GetAll(),
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

        await new ToolCallLoop(
            tools,
            toolExecutionPort: new PassthroughAgentToolExecutionPort()).ExecuteAsync(
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
        var operatorMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.lark.operator_user_id"] = "lark-user-1",
        };

        using (new AgentToolRequestMetadataScope())
        {
            (await tool.ExecuteAsync("""{"action":"approve","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1"}"""))
                .Should().Contain("No NyxID access token available");
        }

        using (new AgentToolRequestMetadataScope("token-123", operatorMetadata))
        {
            (await tool.ExecuteAsync("""{"action":"pause","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1"}"""))
                .Should().Contain("action must be one of");
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1"}"""))
                .Should().Contain("approval_code is required");
            (await tool.ExecuteAsync("""{"action":"approve","approval_code":"def_1","task_id":"task_1"}"""))
                .Should().Contain("instance_code is required");
            (await tool.ExecuteAsync("""{"action":"approve","approval_code":"def_1","instance_code":"inst_1"}"""))
                .Should().Contain("task_id is required");
            (await tool.ExecuteAsync("""{"action":"approve","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1","transfer_user_id":"ou_1"}"""))
                .Should().Contain("transfer_user_id is only allowed when action=transfer");
            (await tool.ExecuteAsync("""{"action":"reject","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1","form_json":"{}"}"""))
                .Should().Contain("form_json is only supported when action=approve");
            (await tool.ExecuteAsync("""{"action":"approve","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1","form_json":"{bad json}"}"""))
                .Should().Contain("form_json is not valid JSON");
        }

        // No channel operator identity → fail closed instead of trusting any caller-supplied id.
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await tool.ExecuteAsync(
                """{"action":"approve","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1","user_id":"ou_spoofed"}""");
            result.Should().Contain("\"success\":false");
            result.Should().Contain("operator identity");
        }

        var errorTool = new LarkApprovalsActTool(new StubLarkNyxClient
        {
            ApprovalActionResponse = """{"error":true,"status":409,"message":"already processed"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123", operatorMetadata))
        {
            var result = await errorTool.ExecuteAsync(
                """{"action":"reject","approval_code":"def_1","instance_code":"inst_1","task_id":"task_1","comment":"nope"}""");

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

        tools.Should().HaveCount(14);
        tools.Should().Contain(tool => tool is LarkMessagesSendTool);
        tools.Should().Contain(tool => tool is LarkMessagesReplyTool);
        tools.Should().Contain(tool => tool is LarkMessagesReactTool);
        tools.Should().Contain(tool => tool is LarkMessagesReactionsListTool);
        tools.Should().Contain(tool => tool is LarkMessagesReactionsDeleteTool);
        tools.Should().NotContain(tool => tool.Name == "lark_messages_search");
        tools.Should().Contain(tool => tool is LarkMessagesBatchGetTool);
        tools.Should().Contain(tool => tool is LarkChatsLookupTool);
        tools.Should().Contain(tool => tool is LarkSheetsAppendRowsTool);
        tools.Should().Contain(tool => tool is LarkApprovalsListTool);
        tools.Should().Contain(tool => tool is LarkApprovalsGetTool);
        tools.Should().Contain(tool => tool is LarkApprovalsActTool);
        tools.Should().Contain(tool => tool is LarkDocxCreateTool);
        tools.Should().Contain(tool => tool is LarkBaseCreateTool);
        tools.Should().Contain(tool => tool is LarkResourceGrantTool);
    }

    [Fact]
    public async Task LarkAgentToolSource_SkipsTools_WhenNyxBaseUrlMissing()
    {
        var source = new LarkAgentToolSource(
            new LarkToolOptions(),
            new NyxIdToolOptions { BaseUrl = null },
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
    public async Task LarkNyxClient_BatchGetMessages_ShapesProxyRequest()
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

        await client.BatchGetMessagesAsync(
            "token-123",
            new LarkMessagesBatchGetRequest(["om_1", "om_2"]),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
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
                Content = new StringContent("""{"code":0,"data":{"tasks":[],"count":{"total":0,"has_more":false}}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.ListApprovalTasksAsync(
            "token-123",
            new LarkApprovalTaskQueryRequest("1", "ou_operator_1", 10, "page-1", "open_id"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        // Official endpoint is GET /approval/v4/tasks/query with user_id REQUIRED; the bare
        // /approval/v4/tasks path does not exist (Lark answers it with a misleading 99991663).
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks/query?user_id=ou_operator_1&topic=1&page_size=10&page_token=page-1&user_id_type=open_id");
    }

    [Fact]
    public async Task LarkNyxClient_ListApprovalTasks_OmitsOptionalQueryParameters()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"tasks":[],"count":{"total":0,"has_more":false}}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.ListApprovalTasksAsync(
            "token-123",
            new LarkApprovalTaskQueryRequest("1", "ou_operator_1", 20, null, null),
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks/query?user_id=ou_operator_1&topic=1&page_size=20");
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
            new LarkApprovalTaskActionRequest("transfer", "approval_def_1", "inst_1", "task_1", "lark-user-1", "reassign", null, "ou_target", "open_id"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        // Official endpoint is /tasks/transfer; /tasks/forward does not exist on the Lark side.
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks/transfer?user_id_type=open_id");
        handler.LastBody.Should().Contain("\"approval_code\":\"approval_def_1\"");
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
        public string MessagesBatchGetResponse { get; set; } = """{"code":0,"data":{"items":[]}}""";
        public LarkMessageResourceDownloadResult MessageResourceResponse { get; set; } = new(
            true,
            [],
            "application/octet-stream");
        public string SearchResponse { get; set; } = """{"code":0,"data":{"items":[],"total":0}}""";
        public string AppendSheetResponse { get; set; } = """{"code":0,"data":{"updates":{}}}""";
        public string ApprovalListResponse { get; set; } = """{"code":0,"data":{"tasks":[],"count":{"total":0,"has_more":false}}}""";
        public string ApprovalGetResponse { get; set; } = """{"code":0,"data":{"instance_code":"inst_default","status":"1"}}""";
        public string ApprovalActionResponse { get; set; } = """{"code":0,"data":{}}""";
        public string DocxCreateResponse { get; set; } = """{"code":0,"data":{"document":{"document_id":"doccn_default","url":"https://example.feishu.cn/docx/doccn_default"}}}""";
        public string DocxAppendResponse { get; set; } = """{"code":0,"data":{}}""";
        public string DrivePermissionResponse { get; set; } = """{"code":0,"data":{}}""";
        public string DriveMediaUploadResponse { get; set; } = """{"code":0,"data":{"file_token":"file_default"}}""";
        public string ApprovalFileUploadResponse { get; set; } = """{"code":0,"data":{"code":"approval_file_default"}}""";
        public string BitableCreateResponse { get; set; } = """{"code":0,"data":{"app":{"app_token":"bascn_default","url":"https://example.feishu.cn/base/bascn_default","default_table_id":"tbl_default"}}}""";
        public string GrantResourceMemberResponse { get; set; } = """{"code":0,"data":{"member":{"member_id":"ou_default","perm":"full_access"}}}""";
        public Exception? DriveMediaUploadException { get; set; }
        public Exception? ApprovalFileUploadException { get; set; }

        public string? LastSendToken { get; private set; }
        public string? LastDocxCreateToken { get; private set; }
        public string? LastMessageResourceToken { get; private set; }
        public LarkSendMessageRequest? LastSendRequest { get; private set; }
        public LarkReplyMessageRequest? LastReplyRequest { get; private set; }
        public LarkMessageReactionRequest? LastReactionRequest { get; private set; }
        public LarkMessageReactionListRequest? LastReactionListRequest { get; private set; }
        public LarkMessageReactionDeleteRequest? LastReactionDeleteRequest { get; private set; }
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
        public LarkApprovalFileUploadRequest? LastApprovalFileUploadRequest { get; private set; }
        public string? LastBitableCreateToken { get; private set; }
        public LarkBitableCreateRequest? LastBitableCreateRequest { get; private set; }
        public string? LastGrantToken { get; private set; }
        public LarkResourceMemberGrantRequest? LastGrantRequest { get; private set; }
        public int GrantCallCount { get; private set; }

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
            LastMessageResourceToken = token;
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

        public Task<string> CreateBitableAppAsync(string token, LarkBitableCreateRequest request, CancellationToken ct)
        {
            LastBitableCreateToken = token;
            LastBitableCreateRequest = request;
            return Task.FromResult(BitableCreateResponse);
        }

        public Task<string> GrantResourceMemberAsync(string token, LarkResourceMemberGrantRequest request, CancellationToken ct)
        {
            LastGrantToken = token;
            LastGrantRequest = request;
            GrantCallCount++;
            return Task.FromResult(GrantResourceMemberResponse);
        }

        public Task<string> UploadDriveMediaAsync(string token, LarkDriveMediaUploadRequest request, CancellationToken ct)
        {
            LastDriveMediaUploadRequest = request;
            if (DriveMediaUploadException != null)
                throw DriveMediaUploadException;
            return Task.FromResult(DriveMediaUploadResponse);
        }

        public Task<string> UploadApprovalFileAsync(string token, LarkApprovalFileUploadRequest request, CancellationToken ct)
        {
            LastApprovalFileUploadRequest = request;
            if (ApprovalFileUploadException != null)
                throw ApprovalFileUploadException;
            return Task.FromResult(ApprovalFileUploadResponse);
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

    private sealed class PassthroughAgentToolExecutionPort : IAgentToolExecutionPort
    {
        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            var safety = request.Tool.GetCallSafety(request.ArgumentsJson)
                ?? new AgentToolCallSafety(true, false, true);
            var result = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
            var receipt = new AgentToolReceipt
            {
                CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                ToolName = request.Tool.Name,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = result,
            };
            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                result,
                receipt,
                !safety.IsReadOnly,
                string.Empty,
                string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true);
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
