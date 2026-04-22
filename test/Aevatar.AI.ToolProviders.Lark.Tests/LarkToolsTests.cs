using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Lark.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
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
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

        try
        {
            var result = await tool.ExecuteAsync(
                """{"target_type":"chat_id","target_id":"oc_456","message_type":"text","text":"Hello from Aevatar"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("message_id").GetString().Should().Be("om_123");
            document.RootElement.GetProperty("target_type").GetString().Should().Be("chat_id");
            client.LastSendRequest.Should().NotBeNull();
            client.LastSendRequest!.MessageType.Should().Be("text");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
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
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

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
            AgentToolRequestContext.CurrentMetadata = null;
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
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

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
            AgentToolRequestContext.CurrentMetadata = null;
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
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

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
            AgentToolRequestContext.CurrentMetadata = null;
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
    public async Task LarkApprovalsActTool_ValidatesTransferTarget()
    {
        var tool = new LarkApprovalsActTool(new StubLarkNyxClient());
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

        try
        {
            var result = await tool.ExecuteAsync("""{"action":"transfer","instance_code":"inst_1","task_id":"task_1"}""");
            result.Should().Contain("transfer_user_id is required");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
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
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-123",
        };

        try
        {
            var result = await tool.ExecuteAsync(
                """{"action":"approve","instance_code":"inst_1","task_id":"task_1","comment":"LGTM","form_json":"[{\"id\":\"field_1\",\"type\":\"input\",\"value\":\"ok\"}]"}""");

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            client.LastApprovalActionRequest.Should().NotBeNull();
            client.LastApprovalActionRequest!.Action.Should().Be("approve");
            client.LastApprovalActionRequest.FormJson.Should().Contain("\"field_1\"");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
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
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1","user_id_type":"email"}"""))
                .Should().Contain("user_id_type must be one of");
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1","transfer_user_id":"ou_1"}"""))
                .Should().Contain("transfer_user_id is only allowed when action=transfer");
            (await tool.ExecuteAsync("""{"action":"reject","instance_code":"inst_1","task_id":"task_1","form_json":"{}"}"""))
                .Should().Contain("form_json is only supported when action=approve");
            (await tool.ExecuteAsync("""{"action":"approve","instance_code":"inst_1","task_id":"task_1","form_json":"{bad json}"}"""))
                .Should().Contain("form_json is not valid JSON");
        }

        var errorTool = new LarkApprovalsActTool(new StubLarkNyxClient
        {
            ApprovalActionResponse = """{"error":true,"status":409,"message":"already processed"}""",
        });
        using (new AgentToolRequestMetadataScope("token-123"))
        {
            var result = await errorTool.ExecuteAsync(
                """{"action":"reject","instance_code":"inst_1","task_id":"task_1","comment":"nope"}""");

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

        tools.Should().HaveCount(5);
        tools.Should().Contain(tool => tool is LarkMessagesSendTool);
        tools.Should().Contain(tool => tool is LarkChatsLookupTool);
        tools.Should().Contain(tool => tool is LarkSheetsAppendRowsTool);
        tools.Should().Contain(tool => tool is LarkApprovalsListTool);
        tools.Should().Contain(tool => tool is LarkApprovalsActTool);
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
            new LarkApprovalTaskActionRequest("transfer", "inst_1", "task_1", "reassign", null, "ou_target", "open_id"),
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks/forward?user_id_type=open_id");
        handler.LastBody.Should().Contain("\"transfer_user_id\":\"ou_target\"");
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
        public string SearchResponse { get; set; } = """{"code":0,"data":{"items":[],"total":0}}""";
        public string AppendSheetResponse { get; set; } = """{"code":0,"data":{"updates":{}}}""";
        public string ApprovalListResponse { get; set; } = """{"code":0,"data":{"tasks":[],"count":0}}""";
        public string ApprovalActionResponse { get; set; } = """{"code":0,"data":{}}""";

        public LarkSendMessageRequest? LastSendRequest { get; private set; }
        public LarkChatSearchRequest? LastSearchRequest { get; private set; }
        public LarkSheetAppendRowsRequest? LastSheetAppendRequest { get; private set; }
        public LarkApprovalTaskQueryRequest? LastApprovalQueryRequest { get; private set; }
        public LarkApprovalTaskActionRequest? LastApprovalActionRequest { get; private set; }

        public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct)
        {
            LastSendRequest = request;
            return Task.FromResult(SendResponse);
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

        public Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct)
        {
            LastApprovalActionRequest = request;
            return Task.FromResult(ApprovalActionResponse);
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

    private sealed class AgentToolRequestMetadataScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string>? _previous;

        public AgentToolRequestMetadataScope(string? accessToken = null)
        {
            _previous = AgentToolRequestContext.CurrentMetadata;
            AgentToolRequestContext.CurrentMetadata = string.IsNullOrWhiteSpace(accessToken)
                ? null
                : new Dictionary<string, string>
                {
                    [LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken,
                };
        }

        public void Dispose()
        {
            AgentToolRequestContext.CurrentMetadata = _previous;
        }
    }
}
