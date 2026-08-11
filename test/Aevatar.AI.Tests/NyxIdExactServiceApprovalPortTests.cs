using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ExactServiceApprovals;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdExactServiceApprovalPortTests
{
    [Fact]
    public async Task CreateAsync_ShouldBindExactSelectorAndCanonicalOperationDigest()
    {
        JsonObject? captured = null;
        var handler = new RecordingHandler(async request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be(
                "/api/v1/approvals/exact-service/requests");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("user-token");
            captured = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return JsonResponse(SnapshotJson(
                "pending",
                captured["operation_digest"]!.GetValue<string>()));
        });
        var port = CreatePort(handler);
        var arguments = JsonNode.Parse("""
            {
              "query": { "receive_id_type": "chat_id" },
              "body": {
                "receive_id": "oc_alpha",
                "msg_type": "text",
                "content": "{\"text\":\"marker-alpha\"}",
                "uuid": "idem-alpha"
              }
            }
            """)!;

        var result = await port.CreateAsync(
            "user-token",
            LarkMessageAdmission(),
            arguments,
            "operation-alpha",
            7,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Created);
        result.Snapshot!.State.Should().Be(NyxIdExactServiceApprovalState.Pending);
        captured.Should().NotBeNull();
        captured!["user_service_id"]!.GetValue<string>().Should().Be("us-alpha");
        captured["endpoint_id"]!.GetValue<string>().Should().Be("message.create");
        captured["catalog_digest"]!.GetValue<string>().Should().Be("sha256:catalog");
        captured["endpoint_contract_digest"]!.GetValue<string>().Should().Be("sha256:contract");
        captured["operation_id"]!.GetValue<string>().Should().Be("operation-alpha");
        captured["operation_generation"]!.GetValue<long>().Should().Be(7);
        captured["idempotency_key"]!.GetValue<string>().Should().Be("idem-alpha");
        var exactArguments = JsonNode.Parse("""
            {
              "receive_id_type": "chat_id",
              "receive_id": "oc_alpha",
              "msg_type": "text",
              "content": "{\"text\":\"marker-alpha\"}",
              "uuid": "idem-alpha"
            }
            """)!;
        JsonNode.DeepEquals(captured["arguments"], exactArguments).Should().BeTrue();
        captured["operation_digest"]!.GetValue<string>().Should().Be(
            NyxIdExactServiceApprovalPort.ComputeOperationDigest(
                "us-alpha",
                "message.create",
                "sha256:contract",
                exactArguments));
        NyxIdExactServiceApprovalPort.ComputeOperationDigest(
                "us-alpha",
                "message.create",
                "sha256:contract",
                JsonNode.Parse("""{"message":"你好 <team>"}""")!)
            .Should().Be(
                "sha256:1bcaa1b49841edd6af5c6787659938cfe47196f3c6d2e38b460d955c4ccad4e3");
    }

    [Fact]
    public async Task CreateAsync_WhenArgumentsWrapperIsNotAnObject_ShouldRejectBeforeRequest()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "An invalid argument wrapper must not reach NyxID."));
        var port = CreatePort(handler);

        var result = await port.CreateAsync(
            "user-token",
            ExactAdmission(),
            new JsonArray("invalid"),
            "operation-alpha",
            1,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Rejected);
        result.FailureCode.Should().Be("exact_service_arguments_invalid");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenPublishedParameterNamesCollide_ShouldRejectBeforeRequest()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "Ambiguous endpoint arguments must not reach NyxID."));
        var port = CreatePort(handler);
        var admission = ExactAdmission() with
        {
            Parameters =
            [
                new AgentToolOperationParameter(
                    "identity",
                    AgentToolOperationParameterLocation.Path,
                    false,
                    AgentToolOperationValueSchema.Text),
                new AgentToolOperationParameter(
                    "identity",
                    AgentToolOperationParameterLocation.Query,
                    false,
                    AgentToolOperationValueSchema.Text),
            ],
        };

        var result = await port.CreateAsync(
            "user-token",
            admission,
            new JsonObject(),
            "operation-alpha",
            1,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Rejected);
        result.FailureCode.Should().Be("exact_service_argument_name_collision");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenOptionalJsonBodyIsPresent_ShouldKeepNyxIdBodyWrapper()
    {
        JsonObject? captured = null;
        var handler = new RecordingHandler(async request =>
        {
            captured = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return JsonResponse(SnapshotJson(
                "pending",
                captured["operation_digest"]!.GetValue<string>()));
        });
        var port = CreatePort(handler);
        var admission = ExactAdmission() with
        {
            RequestBody = new AgentToolOperationRequestBody(
                false,
                "application/json",
                ObjectSchema(
                    false,
                    ("value", AgentToolOperationValueSchema.Text))),
        };

        var result = await port.CreateAsync(
            "user-token",
            admission,
            JsonNode.Parse("""{"body":{"value":"alpha"}}""")!,
            "operation-alpha",
            7,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Created);
        JsonNode.DeepEquals(
            captured!["arguments"],
            JsonNode.Parse("""{"body":{"value":"alpha"}}""")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WhenAdditionalBodyFieldConflictsWithHeader_ShouldRejectBeforeRequest()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "Case-insensitive header/body collisions must not reach NyxID."));
        var port = CreatePort(handler);
        var admission = ExactAdmission() with
        {
            Parameters =
            [
                new AgentToolOperationParameter(
                    "If-Match",
                    AgentToolOperationParameterLocation.Header,
                    true,
                    AgentToolOperationValueSchema.Text),
            ],
            RequestBody = new AgentToolOperationRequestBody(
                true,
                "application/json",
                ObjectSchema(
                    true,
                    ("value", AgentToolOperationValueSchema.Text))),
        };
        var arguments = JsonNode.Parse("""
            {
              "headers": { "If-Match": "etag-alpha" },
              "body": { "value": "alpha", "if-match": "body-value" }
            }
            """)!;

        var result = await port.CreateAsync(
            "user-token",
            admission,
            arguments,
            "operation-alpha",
            7,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Rejected);
        result.FailureCode.Should().Be("exact_service_argument_name_collision");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldExcludeResponseModeWithoutMutatingGroupedArguments()
    {
        JsonObject? captured = null;
        var handler = new RecordingHandler(async request =>
        {
            captured = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return JsonResponse(SnapshotJson(
                "pending",
                captured["operation_digest"]!.GetValue<string>()));
        });
        var port = CreatePort(handler);
        var admission = ExactAdmission() with
        {
            HttpMethod = "GET",
            ResponsePolicy = new AgentToolOperationResponsePolicy(
                true,
                true,
                ["application/octet-stream"]),
        };
        var arguments = JsonNode.Parse("""{"response_mode":"file_artifact"}""")!;
        var originalArguments = arguments.ToJsonString();

        var result = await port.CreateAsync(
            "user-token",
            admission,
            arguments,
            "operation-alpha",
            7,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Created);
        captured!["arguments"]!.AsObject().Count.Should().Be(0);
        arguments.ToJsonString().Should().Be(originalArguments);
    }

    [Fact]
    public async Task CreateAsync_WhenBodyPropertyCollidesWithPath_ShouldUseNyxIdBodyWrapper()
    {
        JsonObject? captured = null;
        var handler = new RecordingHandler(async request =>
        {
            captured = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return JsonResponse(SnapshotJson(
                "pending",
                captured["operation_digest"]!.GetValue<string>()));
        });
        var port = CreatePort(handler);
        var admission = ExactAdmission() with
        {
            PathTemplate = "/messages/{identity}",
            Parameters =
            [
                new AgentToolOperationParameter(
                    "identity",
                    AgentToolOperationParameterLocation.Path,
                    true,
                    AgentToolOperationValueSchema.Text),
            ],
            RequestBody = new AgentToolOperationRequestBody(
                true,
                "application/json",
                ObjectSchema(
                    false,
                    ("identity", AgentToolOperationValueSchema.Text),
                    ("value", AgentToolOperationValueSchema.Text))),
        };
        var arguments = JsonNode.Parse("""
            {
              "path_params": { "identity": "path-alpha" },
              "body": { "identity": "body-alpha", "value": "payload-alpha" }
            }
            """)!;

        var result = await port.CreateAsync(
            "user-token",
            admission,
            arguments,
            "operation-alpha",
            7,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Created);
        JsonNode.DeepEquals(
            captured!["arguments"],
            JsonNode.Parse("""
                {
                  "identity": "path-alpha",
                  "body": { "identity": "body-alpha", "value": "payload-alpha" }
                }
                """)).Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"unknown\":{}}")]
    [InlineData("{\"query\":{\"unknown\":\"value\"}}")]
    public async Task CreateAsync_WhenGroupedArgumentsAreNotAdmitted_ShouldRejectBeforeRequest(
        string argumentsJson)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "Unadmitted grouped arguments must not reach NyxID."));
        var port = CreatePort(handler);

        var result = await port.CreateAsync(
            "user-token",
            ExactAdmission(),
            JsonNode.Parse(argumentsJson)!,
            "operation-alpha",
            7,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Rejected);
        result.FailureCode.Should().StartWith("nyxid_operation_");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenJsonObjectSchemaHasNoProperties_ShouldFailClosed()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException(
            "An ambiguous JSON body contract must not reach NyxID."));
        var port = CreatePort(handler);
        var admission = ExactAdmission() with
        {
            RequestBody = new AgentToolOperationRequestBody(
                true,
                "application/json",
                ObjectSchema(additionalPropertiesAllowed: true)),
        };

        var result = await port.CreateAsync(
            "user-token",
            admission,
            JsonNode.Parse("""{"body":{"value":"alpha"}}""")!,
            "operation-alpha",
            7,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Rejected);
        result.FailureCode.Should().Be("exact_service_body_contract_ambiguous");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task CreateAsync_WhenTierAEndpointIsAbsent_ShouldAllowTierBFallback(
        HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ => Task.FromResult(
            JsonResponse(
                status == HttpStatusCode.NotFound ? "Not Found" : "Method Not Allowed",
                status)));
        var port = CreatePort(handler);

        var result = await port.CreateAsync(
            "user-token",
            ExactAdmission(),
            new JsonObject(),
            "operation-alpha",
            1,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(
            NyxIdExactServiceApprovalCreateDisposition.TierAUnavailable);
    }

    [Fact]
    public async Task CreateAsync_WhenExactSelectorIsNotFound_ShouldNotFallback()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(
            JsonResponse("{\"code\":\"exact_user_service_not_found\"}",
                HttpStatusCode.NotFound)));
        var port = CreatePort(handler);

        var result = await port.CreateAsync(
            "user-token",
            ExactAdmission(),
            new JsonObject(),
            "operation-alpha",
            1,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Rejected);
        result.FailureCode.Should().Contain("exact_user_service_not_found");
    }

    [Fact]
    public async Task CreateAsync_WhenTierARejectsRequest_ShouldFailClosed()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(
            JsonResponse("{\"code\":\"requester_scope_mismatch\"}",
                HttpStatusCode.Forbidden)));
        var port = CreatePort(handler);

        var result = await port.CreateAsync(
            "user-token",
            ExactAdmission(),
            new JsonObject(),
            "operation-alpha",
            1,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Rejected);
        result.FailureCode.Should().Contain("requester_scope_mismatch");
    }

    [Fact]
    public async Task CreateAsync_WhenReturnedSelectorDrifts_ShouldFailClosed()
    {
        var handler = new RecordingHandler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return JsonResponse(SnapshotJson(
                "pending",
                body["operation_digest"]!.GetValue<string>(),
                endpointId: "message.delete"));
        });
        var port = CreatePort(handler);

        var result = await port.CreateAsync(
            "user-token",
            ExactAdmission(),
            new JsonObject(),
            "operation-alpha",
            1,
            "idem-alpha",
            CancellationToken.None);

        result.Disposition.Should().Be(NyxIdExactServiceApprovalCreateDisposition.Rejected);
        result.FailureCode.Should().Be("invalid_exact_approval_response");
    }

    [Fact]
    public async Task ObserveAsync_WhenPersistedAuthorityDrifts_ShouldFailClosed()
    {
        var authority = Authority();
        var handler = new RecordingHandler(_ => Task.FromResult(JsonResponse(
            SnapshotJson("approved", authority.OperationDigest, catalogDigest: "sha256:changed"))));
        var port = CreatePort(handler);

        var snapshot = await port.ObserveAsync(
            "user-token", authority, CancellationToken.None);

        snapshot.State.Should().Be(NyxIdExactServiceApprovalState.Failed);
        snapshot.FailureCode.Should().Be("exact_service_authority_mismatch");
        snapshot.Authority.Should().BeEquivalentTo(authority);
    }

    [Fact]
    public async Task DecideAsync_WhenExactDecisionAlreadyExists_ShouldNotMutateAgain()
    {
        var authority = Authority();
        var handler = new RecordingHandler(request => Task.FromResult(JsonResponse(
            SnapshotJson("approved", authority.OperationDigest))));
        var port = CreatePort(handler);

        var snapshot = await port.DecideAsync(
            "user-token", authority, true, CancellationToken.None);

        snapshot.State.Should().Be(NyxIdExactServiceApprovalState.Approved);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task RedeemAsync_ShouldReplaySameBoundReceipt()
    {
        var authority = Authority();
        JsonObject? captured = null;
        var handler = new RecordingHandler(async request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be(
                "/api/v1/approvals/exact-service/requests/request-alpha/redeem");
            captured = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return JsonResponse(SnapshotJson(
                "redeemed",
                authority.OperationDigest,
                receipt: new JsonObject
                {
                    ["http_status"] = 201,
                    ["response_body"] = "{\"id\":\"message-alpha\"}",
                    ["response_digest"] = "sha256:response",
                }));
        });
        var port = CreatePort(handler);

        var snapshot = await port.RedeemAsync(
            "user-token", authority, CancellationToken.None);

        snapshot.State.Should().Be(NyxIdExactServiceApprovalState.Redeemed);
        snapshot.Receipt.Should().Be(new NyxIdExactServiceApprovalReceipt(
            201,
            "{\"id\":\"message-alpha\"}",
            "sha256:response"));
        captured.Should().NotBeNull();
        captured!["catalog_digest"]!.GetValue<string>().Should().Be(authority.CatalogDigest);
        captured["operation_digest"]!.GetValue<string>().Should().Be(authority.OperationDigest);
        captured["operation_id"]!.GetValue<string>().Should().Be(authority.OperationId);
        captured["operation_generation"]!.GetValue<long>().Should().Be(
            authority.OperationGeneration);
        captured["idempotency_key"]!.GetValue<string>().Should().Be(authority.IdempotencyKey);
    }

    private static NyxIdExactServiceApprovalPort CreatePort(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);
        return new NyxIdExactServiceApprovalPort(client);
    }

    private static AgentToolOperationAdmission ExactAdmission() => new(
        "us-alpha",
        "lark",
        new AgentToolOperationIdentity.PublishedEndpoint("message.create"),
        AgentToolOperationAuthorizationBasis.PublishedContract,
        "POST",
        "/messages",
        "sha256:contract",
        [],
        null,
        AgentToolOperationResponsePolicy.TextOnly,
        new AgentToolOperationExecutionPolicy(
            AgentToolOperationRisk.Write,
            AgentToolOperationApproval.Required,
            AgentToolOperationEnforcementOwner.NyxId,
            [AgentToolOperationExecutionMode.Interactive]),
        "sha256:catalog");

    private static AgentToolOperationAdmission LarkMessageAdmission() => ExactAdmission() with
    {
        Parameters =
        [
            new AgentToolOperationParameter(
                "receive_id_type",
                AgentToolOperationParameterLocation.Query,
                true,
                AgentToolOperationValueSchema.Text),
        ],
        RequestBody = new AgentToolOperationRequestBody(
            true,
            "application/json",
            ObjectSchema(
                false,
                ("receive_id", AgentToolOperationValueSchema.Text),
                ("msg_type", AgentToolOperationValueSchema.Text),
                ("content", AgentToolOperationValueSchema.Text),
                ("uuid", AgentToolOperationValueSchema.Text))),
    };

    private static AgentToolOperationValueSchema ObjectSchema(
        bool additionalPropertiesAllowed,
        params (string Name, AgentToolOperationValueSchema Schema)[] properties) => new(
        AgentToolOperationValueKind.Object,
        properties
            .Select(static property => new AgentToolOperationSchemaProperty(
                property.Name,
                property.Schema))
            .ToArray(),
        new HashSet<string>(properties.Select(static property => property.Name),
            StringComparer.Ordinal),
        null,
        [],
        additionalPropertiesAllowed);

    private static NyxIdExactServiceApprovalAuthority Authority() => new()
    {
        RequestId = "request-alpha",
        UserServiceId = "us-alpha",
        EndpointId = "message.create",
        CatalogDigest = "sha256:catalog",
        EndpointContractDigest = "sha256:contract",
        OperationDigest = "sha256:operation",
        OperationId = "operation-alpha",
        OperationGeneration = 7,
        IdempotencyKey = "idem-alpha",
        ExpiresAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)),
    };

    private static string SnapshotJson(
        string state,
        string operationDigest,
        string endpointId = "message.create",
        string catalogDigest = "sha256:catalog",
        JsonObject? receipt = null)
    {
        var snapshot = new JsonObject
        {
            ["request_id"] = "request-alpha",
            ["state"] = state,
            ["user_service_id"] = "us-alpha",
            ["endpoint_id"] = endpointId,
            ["catalog_digest"] = catalogDigest,
            ["endpoint_contract_digest"] = "sha256:contract",
            ["operation_digest"] = operationDigest,
            ["operation_id"] = "operation-alpha",
            ["operation_generation"] = 7,
            ["idempotency_key"] = "idem-alpha",
            ["expires_at"] = "2026-08-11T12:00:00Z",
        };
        if (receipt is not null)
            snapshot["receipt"] = receipt;
        return snapshot.ToJsonString();
    }

    private static HttpResponseMessage JsonResponse(
        string body,
        HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await responder(request);
        }
    }
}
