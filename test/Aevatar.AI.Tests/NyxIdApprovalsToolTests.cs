using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApprovalsToolTests
{
    [Theory]
    [InlineData("list", "/api/v1/approvals/requests")]
    [InlineData("show", "/api/v1/approvals/requests/approval-1")]
    public async Task ExecuteAsync_ListAndShow_ShouldExposeOnlyAllowlistedApprovalFields(
        string action,
        string expectedPath)
    {
        const string credential = "credential-secret-sentinel";
        const string providerInput = "raw-provider-input-sentinel";
        const string invalidAction = "invalid-action-input-sentinel";
        var persistedApproval = $$"""
            {
              "id": "approval-1",
              "service_name": "nyxid_services",
              "service_slug": "tool",
              "requester_type": "delegated",
              "requester_label": "automation-alpha",
              "operation_summary": "tool:nyxid_services",
              "action_description": "nyxid_services({\"action\":\"unknown\",\"credential\":\"{{credential}}\"})",
              "http_method": "POST",
              "resource": "{{providerInput}}",
              "verb": "destructive",
              "tool_name": "nyxid_services",
              "tool_call_id": "call-approval-1",
              "tool_arguments": "{\"action\":\"{{invalidAction}}\",\"credential\":\"{{credential}}\",\"provider_input\":\"{{providerInput}}\"}",
              "is_destructive": true,
              "approval_mode": "per_request",
              "status": "pending",
              "created_at": "2026-07-31T01:02:03Z",
              "decided_at": null,
              "decision_channel": null,
              "from_org_policy": false,
              "org_id": null,
              "org_name": null
            }
            """;
        var responseJson = action == "list"
            ? $$"""{"requests":[{{persistedApproval}}],"total":1,"page":1,"per_page":20,"provider_debug":"{{providerInput}}"}"""
            : persistedApproval;
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        var tool = new NyxIdApprovalsTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance));
        var executor = new AdmittedAgentToolExecutor(
            AlwaysStartingAgentToolAdmissionLedger.Instance,
            new AppendedAuditTrail(),
            new StableIdentityHasher());
        var argumentsJson = action == "list"
            ? "{\"action\":\"list\"}"
            : "{\"action\":\"show\",\"id\":\"approval-1\"}";

        var outcome = await executor.ExecuteAsync(new AgentToolExecutionRequest(
            tool,
            argumentsJson,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with { NyxIdAccessToken = "token-1" },
                Request = new AgentToolRequestIdentity("request-1", "call-1"),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-approvals"),
            },
            AgentToolApprovalContinuationMode.None,
            null));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.ResultJson.Should().Be(outcome.ResultJson);
        outcome.ResultJson.Should()
            .NotContain(credential)
            .And.NotContain(providerInput)
            .And.NotContain(invalidAction)
            .And.NotContain("tool_arguments")
            .And.NotContain("action_description")
            .And.NotContain("requester_label")
            .And.NotContain("requester_type")
            .And.NotContain("operation_summary");
        using var result = JsonDocument.Parse(outcome.ResultJson);
        var approval = action == "list"
            ? result.RootElement.GetProperty("requests")[0]
            : result.RootElement;
        if (action == "list")
        {
            result.RootElement.GetProperty("total").GetUInt64().Should().Be(1);
            result.RootElement.GetProperty("page").GetUInt64().Should().Be(1);
            result.RootElement.GetProperty("per_page").GetUInt64().Should().Be(20);
        }
        approval.GetProperty("id").GetString().Should().Be("approval-1");
        approval.GetProperty("service_name").GetString().Should().Be("nyxid_services");
        approval.GetProperty("service_slug").GetString().Should().Be("tool");
        approval.GetProperty("tool_name").GetString().Should().Be("nyxid_services");
        approval.GetProperty("tool_call_id").GetString().Should().Be("call-approval-1");
        approval.GetProperty("is_destructive").GetBoolean().Should().BeTrue();
        approval.GetProperty("approval_mode").GetString().Should().Be("per_request");
        approval.GetProperty("status").GetString().Should().Be("pending");
        approval.GetProperty("created_at").GetString().Should().Be("2026-07-31T01:02:03Z");
        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsolutePath.Should().Be(expectedPath);
    }

    [Theory]
    [MemberData(nameof(MalformedApprovalResponses))]
    public async Task ExecuteAsync_ListAndShowMalformedResponses_ShouldReturnGenericUnknownWithoutRawData(
        string caseName,
        string action,
        string responseJson)
    {
        _ = caseName;
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        var tool = new NyxIdApprovalsTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance));
        var executor = new AdmittedAgentToolExecutor(
            AlwaysStartingAgentToolAdmissionLedger.Instance,
            new AppendedAuditTrail(),
            new StableIdentityHasher());
        var argumentsJson = action == "list"
            ? "{\"action\":\"list\"}"
            : "{\"action\":\"show\",\"id\":\"approval-1\"}";

        var outcome = await executor.ExecuteAsync(new AgentToolExecutionRequest(
            tool,
            argumentsJson,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with
                {
                    NyxIdAccessToken = "credential-secret-sentinel",
                },
                Request = new AgentToolRequestIdentity("request-malformed", "call-malformed"),
                ExecutionOwner = AgentToolExecutionOwners.Actor("actor-approvals"),
            },
            AgentToolApprovalContinuationMode.None,
            null));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        Assert.Equal(
            "{\"status\":\"unknown\",\"message\":\"The tool outcome could not be verified.\"}",
            outcome.ResultJson);
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Unspecified);
        outcome.Receipt.ErrorCode.Should().Be("tool_outcome_unknown");
        outcome.Receipt.ErrorMessage.Should().Be("The tool outcome could not be verified.");
        outcome.Receipt.ResultJson.Should().Be(outcome.ResultJson);
        outcome.ResultJson.Should()
            .NotContain("raw-approval-payload-sentinel")
            .And.NotContain("credential-secret-sentinel")
            .And.NotContain("provider-input-sentinel")
            .And.NotContain("unknown-field-value-sentinel")
            .And.NotContain("tool_arguments")
            .And.NotContain("credential")
            .And.NotContain("provider_input")
            .And.NotContain("provider_debug")
            .And.NotContain("unknown_field");
    }

    public static IEnumerable<object[]> MalformedApprovalResponses()
    {
        yield return InvalidCase("list-malformed-json", "list",
            "{\"raw_payload\":\"raw-approval-payload-sentinel\",\"tool_arguments\":\"credential-secret-sentinel\"");
        yield return InvalidCase("list-wrong-root-array", "list",
            Serialize(new JsonArray(ApprovalItem())));
        yield return InvalidCase("list-wrong-root-string", "list", "\"raw-approval-payload-sentinel\"");
        yield return InvalidCase("list-wrong-root-number", "list", "42");
        yield return InvalidCase("list-wrong-root-boolean", "list", "false");
        yield return InvalidCase("list-wrong-root-object", "list", Serialize(ApprovalItem()));
        yield return InvalidCase("list-null-root", "list", "null");
        yield return InvalidCase("show-malformed-json", "show",
            "{\"raw_payload\":\"raw-approval-payload-sentinel\",\"tool_arguments\":\"credential-secret-sentinel\"");
        yield return InvalidCase("show-wrong-root-array", "show",
            Serialize(new JsonArray(ApprovalItem())));
        yield return InvalidCase("show-wrong-root-string", "show", "\"raw-approval-payload-sentinel\"");
        yield return InvalidCase("show-wrong-root-number", "show", "42");
        yield return InvalidCase("show-wrong-root-boolean", "show", "false");
        yield return InvalidCase("show-wrong-root-object", "show", Serialize(ApprovalList()));
        yield return InvalidCase("show-null-root", "show", "null");

        foreach (var propertyName in new[] { "requests", "total", "page", "per_page" })
        {
            var missing = ApprovalList();
            missing.Remove(propertyName);
            yield return InvalidCase($"list-missing-{propertyName}", "list", Serialize(missing));

            var nullValue = ApprovalList();
            nullValue[propertyName] = null;
            yield return InvalidCase($"list-null-{propertyName}", "list", Serialize(nullValue));
        }

        foreach (var (propertyName, wrongValue) in new (string, JsonNode)[]
                 {
                     ("requests", JsonValue.Create("wrong-array-type")!),
                     ("total", JsonValue.Create("wrong-number-type")!),
                     ("page", JsonValue.Create(false)!),
                     ("per_page", new JsonObject()),
                 })
        {
            var wrongType = ApprovalList();
            wrongType[propertyName] = wrongValue;
            yield return InvalidCase($"list-wrong-type-{propertyName}", "list", Serialize(wrongType));
        }

        var nullItem = ApprovalList();
        nullItem["requests"] = new JsonArray((JsonNode?)null);
        yield return InvalidCase("list-null-item", "list", Serialize(nullItem));

        var wrongItemType = ApprovalList();
        wrongItemType["requests"] = new JsonArray("wrong-item-type");
        yield return InvalidCase("list-wrong-item-type", "list", Serialize(wrongItemType));

        foreach (var action in new[] { "list", "show" })
        {
            foreach (var propertyName in RequiredApprovalProperties)
            {
                var missing = ApprovalItem();
                missing.Remove(propertyName);
                yield return InvalidApprovalItemCase(action, $"missing-{propertyName}", missing);

                var nullValue = ApprovalItem();
                nullValue[propertyName] = null;
                yield return InvalidApprovalItemCase(action, $"null-{propertyName}", nullValue);

                if (propertyName != "from_org_policy")
                {
                    var blankValue = ApprovalItem();
                    blankValue[propertyName] = " ";
                    yield return InvalidApprovalItemCase(action, $"blank-{propertyName}", blankValue);
                }

                var wrongType = ApprovalItem();
                wrongType[propertyName] = propertyName == "from_org_policy"
                    ? JsonValue.Create("wrong-bool-type")
                    : JsonValue.Create(42);
                yield return InvalidApprovalItemCase(action, $"wrong-type-{propertyName}", wrongType);
            }

            foreach (var propertyName in OptionalApprovalProperties)
            {
                var wrongType = ApprovalItem();
                wrongType[propertyName] = propertyName == "is_destructive"
                    ? JsonValue.Create("wrong-bool-type")
                    : JsonValue.Create(42);
                yield return InvalidApprovalItemCase(action, $"wrong-type-{propertyName}", wrongType);
            }
        }
    }

    private static readonly string[] RequiredApprovalProperties =
    [
        "id",
        "service_name",
        "service_slug",
        "approval_mode",
        "status",
        "created_at",
        "from_org_policy",
    ];

    private static readonly string[] OptionalApprovalProperties =
    [
        "tool_name",
        "tool_call_id",
        "is_destructive",
        "decided_at",
        "org_name",
    ];

    private static object[] InvalidApprovalItemCase(string action, string caseName, JsonObject item)
    {
        JsonNode response = item;
        if (action == "list")
        {
            var list = ApprovalList();
            list["requests"] = new JsonArray(item);
            response = list;
        }

        return InvalidCase($"{action}-{caseName}", action, Serialize(response));
    }

    private static object[] InvalidCase(string caseName, string action, string responseJson) =>
        [caseName, action, responseJson];

    private static JsonObject ApprovalList() => new()
    {
        ["requests"] = new JsonArray(ApprovalItem()),
        ["total"] = 7,
        ["page"] = 3,
        ["per_page"] = 25,
        ["provider_debug"] = "provider-input-sentinel",
        ["unknown_field"] = "unknown-field-value-sentinel",
    };

    private static JsonObject ApprovalItem() => new()
    {
        ["id"] = "approval-1",
        ["service_name"] = "nyxid_services",
        ["service_slug"] = "tool",
        ["tool_name"] = "nyxid_services",
        ["tool_call_id"] = "call-approval-1",
        ["tool_arguments"] = "{\"credential\":\"credential-secret-sentinel\",\"provider_input\":\"provider-input-sentinel\"}",
        ["is_destructive"] = true,
        ["approval_mode"] = "per_request",
        ["status"] = "pending",
        ["created_at"] = "2026-07-31T01:02:03Z",
        ["decided_at"] = null,
        ["from_org_policy"] = false,
        ["org_name"] = null,
        ["raw_payload"] = "raw-approval-payload-sentinel",
        ["unknown_field"] = "unknown-field-value-sentinel",
    };

    private static string Serialize(JsonNode node) => node.ToJsonString();

    [Theory]
    [InlineData("approve", true)]
    [InlineData("reject", false)]
    [InlineData("deny", false)]
    public async Task ExecuteAsync_ShouldSendNyxIdApprovedBooleanForDecisionActions(
        string action,
        bool expectedApproved)
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"approval-1","status":"approved"}""", Encoding.UTF8, "application/json"),
        });
        var tool = new NyxIdApprovalsTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance));

        using var _ = AgentToolContextScope.Push(WithNyxIdAccessToken("token-1"));
        var result = await tool.ExecuteAsync($$"""{"action":"{{action}}","id":"approval-1"}""");

        result.Should().Contain("\"status\":\"approved\"");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should()
            .Be("/api/v1/approvals/requests/approval-1/decide");

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        body.RootElement.GetProperty("approved").GetBoolean().Should().Be(expectedApproved);
        body.RootElement.TryGetProperty("decision", out var ignored).Should().BeFalse();
    }

    private static AgentToolExecutionContext WithNyxIdAccessToken(string token) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with { NyxIdAccessToken = token },
        };

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return response;
        }
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
}
