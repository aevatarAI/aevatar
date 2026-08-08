using System.Text.Json.Nodes;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.ExternalCapabilities;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ManagedCodexServiceApiSkillDiscoveryOutputDecoderTests
{
    private const string TargetUserServiceId = "usvc-alpha";
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly ManagedCodexServiceApiSkillDiscoveryOutputDecoder _decoder = new();

    [Fact]
    public void Decode_WhenReliableSkillIsValid_ReturnsTypedCandidateWithNormalizedRequestShape()
    {
        var result = _decoder.Decode(ReliableJson(), TargetUserServiceId, Fingerprint);

        result.ResultCase.Should().Be(ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.ReliableSkill);
        result.ReliableSkill.CanonicalName.Should().Be("example-messaging-service-api");
        result.ReliableSkill.Guid.Should().Be("d47a95c5-db2a-4f00-9057-27f674566bd5");
        result.ReliableSkill.LiteralVersion.Should().Be("1.1");
        result.ReliableSkill.SkillHash.Should().Be("75f0e0480c4cbeed68ba97ffe0b26a0c0cc0ec2d8d0bed631306b383eec0f486");
        result.ReliableSkill.PublisherId.Should().Be("9f42ce90-8b05-406d-8461-acb5fdfa4fab");
        result.ReliableSkill.RequestShape.Selector.UserServiceId.Should().Be(TargetUserServiceId);
        result.ReliableSkill.RequestShape.Selector.Method.Should().Be(NyxIdRequestMethod.Post);
        result.ReliableSkill.RequestShape.Selector.PathTemplate.Should().Be("/v1/messages");
        result.ReliableSkill.RequestShape.Selector.HeaderParameters.Should().Equal("Accept");
        result.ReliableSkill.RequestShape.Selector.BodyMode.Should().Be(NyxIdRequestBodyMode.Json);
        result.ReliableSkill.RequestShape.Selector.BodyRequired.Should().BeTrue();
        result.ReliableSkill.RequestShape.Selector.ResponseMode.Should().Be(NyxIdRequestResponseMode.Text);
        result.ReliableSkill.RequestShape.Selector.Risk.Should().Be(NyxIdOperationRisk.Write);
        result.ReliableSkill.Evidence.Should().ContainSingle()
            .Which.OperationId.Should().Be("send-message");
    }

    [Theory]
    [InlineData("GET", "none", false, "file_artifact", "READ_ONLY", NyxIdRequestMethod.Get,
        NyxIdRequestBodyMode.None, NyxIdRequestResponseMode.FileArtifact, NyxIdOperationRisk.ReadOnly)]
    [InlineData("HEAD", "none", false, "text", "READ_ONLY", NyxIdRequestMethod.Head,
        NyxIdRequestBodyMode.None, NyxIdRequestResponseMode.Text, NyxIdOperationRisk.ReadOnly)]
    [InlineData("OPTIONS", "none", false, "text", "READ_ONLY", NyxIdRequestMethod.Options,
        NyxIdRequestBodyMode.None, NyxIdRequestResponseMode.Text, NyxIdOperationRisk.ReadOnly)]
    [InlineData("PUT", "json", true, "text", "WRITE", NyxIdRequestMethod.Put,
        NyxIdRequestBodyMode.Json, NyxIdRequestResponseMode.Text, NyxIdOperationRisk.Write)]
    [InlineData("PATCH", "json", true, "text", "WRITE", NyxIdRequestMethod.Patch,
        NyxIdRequestBodyMode.Json, NyxIdRequestResponseMode.Text, NyxIdOperationRisk.Write)]
    [InlineData("DELETE", "json", true, "text", "DESTRUCTIVE", NyxIdRequestMethod.Delete,
        NyxIdRequestBodyMode.Json, NyxIdRequestResponseMode.Text, NyxIdOperationRisk.Destructive)]
    public void Decode_WhenSupportedRequestShapeVariantIsReturned_NormalizesTypedSelector(
        string method,
        string bodyMode,
        bool bodyRequired,
        string responseMode,
        string risk,
        NyxIdRequestMethod expectedMethod,
        NyxIdRequestBodyMode expectedBodyMode,
        NyxIdRequestResponseMode expectedResponseMode,
        NyxIdOperationRisk expectedRisk)
    {
        var stdout = MutateReliable(root =>
        {
            var shape = ReliableShape(root);
            shape["method"] = method;
            shape["body_mode"] = bodyMode;
            shape["body_required"] = bodyRequired;
            shape["response_mode"] = responseMode;
            shape["risk"] = risk;
        });

        var selector = _decoder.Decode(stdout, TargetUserServiceId, Fingerprint)
            .ReliableSkill.RequestShape.Selector;

        selector.Method.Should().Be(expectedMethod);
        selector.BodyMode.Should().Be(expectedBodyMode);
        selector.BodyRequired.Should().Be(bodyRequired);
        selector.ResponseMode.Should().Be(expectedResponseMode);
        selector.Risk.Should().Be(expectedRisk);
    }

    [Fact]
    public void Decode_WhenNoReliableSkillIsValid_ReturnsTypedNoReliableReason()
    {
        var result = _decoder.Decode(NoReliableJson("NO_MATCHING_SKILL"), TargetUserServiceId, Fingerprint);

        result.ResultCase.Should().Be(ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.NoReliableApiSkill);
        result.NoReliableApiSkill.Reason.Should().Be(ServiceApiNoReliableSkillReason.NoMatchingSkill);
    }

    [Theory]
    [InlineData("ALL_CANDIDATES_REJECTED", ServiceApiNoReliableSkillReason.AllCandidatesRejected)]
    [InlineData("EXACT_SKILL_READ_FAILED", ServiceApiNoReliableSkillReason.ExactSkillReadFailed)]
    [InlineData("SKILL_IDENTITY_MISMATCH", ServiceApiNoReliableSkillReason.SkillIdentityMismatch)]
    [InlineData("SKILL_INTEGRITY_MISMATCH", ServiceApiNoReliableSkillReason.SkillIntegrityMismatch)]
    [InlineData("REQUEST_SHAPE_UNSUPPORTED", ServiceApiNoReliableSkillReason.RequestShapeUnsupported)]
    [InlineData("REQUEST_SHAPE_ADMISSION_REJECTED", ServiceApiNoReliableSkillReason.RequestShapeAdmissionRejected)]
    public void Decode_WhenNoReliableReasonIsSupported_ReturnsTypedReason(
        string reason,
        ServiceApiNoReliableSkillReason expected)
    {
        var result = _decoder.Decode(NoReliableJson(reason), TargetUserServiceId, Fingerprint);

        result.NoReliableApiSkill.Reason.Should().Be(expected);
    }

    [Theory]
    [InlineData("```json\n{}\n```", "managed_service_api_discovery_stdout_not_json_object")]
    [InlineData("[]", "managed_service_api_discovery_stdout_not_json_object")]
    [InlineData("", "managed_service_api_discovery_stdout_not_json_object")]
    [InlineData("  {\"schema_version\":", "managed_service_api_discovery_stdout_not_single_json_object")]
    [InlineData("{\"schema_version\":\"service_api_skill_discovery.v1\"} {}", "managed_service_api_discovery_stdout_not_single_json_object")]
    [InlineData("{\"schema_version\":\"service_api_skill_discovery.v2\",\"target_user_service_id\":\"usvc-alpha\",\"capability_fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"outcome\":\"no_reliable_skill\",\"no_reliable_skill\":{\"reason\":\"NO_MATCHING_SKILL\"}}", "managed_service_api_discovery_schema_unsupported")]
    [InlineData("{\"schema_version\":\"service_api_skill_discovery.v1\",\"target_user_service_id\":\"other\",\"capability_fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"outcome\":\"no_reliable_skill\",\"no_reliable_skill\":{\"reason\":\"NO_MATCHING_SKILL\"}}", "managed_service_api_discovery_correlation_mismatch")]
    [InlineData("{\"schema_version\":\"service_api_skill_discovery.v1\",\"target_user_service_id\":\"usvc-alpha\",\"capability_fingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"outcome\":\"no_reliable_skill\",\"no_reliable_skill\":{\"reason\":\"NO_MATCHING_SKILL\"},\"extra\":true}", "managed_service_api_discovery_unknown_field")]
    public void Decode_WhenEnvelopeIsInvalid_FailsClosed(string stdout, string expectedCode)
    {
        var act = () => _decoder.Decode(stdout, TargetUserServiceId, Fingerprint);

        act.Should().Throw<ManagedCodexServiceApiSkillDiscoveryOutputException>()
            .Which.Code.Should().Be(expectedCode);
    }

    [Theory]
    [InlineData("outcome", "managed_service_api_discovery_outcome_invalid")]
    [InlineData("missing-field", "managed_service_api_discovery_field_missing")]
    [InlineData("wrong-string-type", "managed_service_api_discovery_field_type_invalid")]
    [InlineData("blank-string", "managed_service_api_discovery_field_invalid")]
    [InlineData("branch-conflict", "managed_service_api_discovery_branch_invalid")]
    [InlineData("missing-reliable-branch", "managed_service_api_discovery_branch_invalid")]
    [InlineData("reliable-not-object", "managed_service_api_discovery_field_type_invalid")]
    [InlineData("evidence-empty", "managed_service_api_discovery_evidence_invalid")]
    [InlineData("evidence-duplicate", "managed_service_api_discovery_evidence_invalid")]
    [InlineData("evidence-not-object", "managed_service_api_discovery_field_type_invalid")]
    [InlineData("publisher-too-long", "managed_service_api_discovery_skill_identity_invalid")]
    [InlineData("guid-invalid", "managed_service_api_discovery_skill_identity_invalid")]
    [InlineData("shape-not-object", "managed_service_api_discovery_field_type_invalid")]
    [InlineData("body-required-type", "managed_service_api_discovery_field_type_invalid")]
    [InlineData("query-not-array", "managed_service_api_discovery_request_shape_invalid")]
    [InlineData("query-item-type", "managed_service_api_discovery_request_shape_invalid")]
    [InlineData("method-unsupported", "managed_service_api_discovery_request_shape_invalid")]
    [InlineData("body-mode-unsupported", "managed_service_api_discovery_request_shape_invalid")]
    [InlineData("response-mode-unsupported", "managed_service_api_discovery_request_shape_invalid")]
    [InlineData("risk-unsupported", "managed_service_api_discovery_request_shape_invalid")]
    public void Decode_WhenReliableContractIsInvalid_FailsClosed(string scenario, string expectedCode)
    {
        var stdout = MutateReliable(root =>
        {
            var reliable = ReliableSkill(root);
            var shape = reliable["request_shape"]?.AsObject();
            switch (scenario)
            {
                case "outcome":
                    root["outcome"] = "unknown";
                    break;
                case "missing-field":
                    root.Remove("schema_version");
                    break;
                case "wrong-string-type":
                    root["schema_version"] = 1;
                    break;
                case "blank-string":
                    root["schema_version"] = " ";
                    break;
                case "branch-conflict":
                    root["no_reliable_skill"] = new JsonObject { ["reason"] = "NO_MATCHING_SKILL" };
                    break;
                case "missing-reliable-branch":
                    root.Remove("reliable_skill");
                    break;
                case "reliable-not-object":
                    root["reliable_skill"] = "invalid";
                    break;
                case "evidence-empty":
                    reliable["evidence"] = new JsonArray();
                    break;
                case "evidence-duplicate":
                    var evidence = reliable["evidence"]!.AsArray();
                    evidence.Add(evidence[0]!.DeepClone());
                    break;
                case "evidence-not-object":
                    reliable["evidence"] = new JsonArray("invalid");
                    break;
                case "publisher-too-long":
                    reliable["publisher_id"] = new string('p', 129);
                    break;
                case "guid-invalid":
                    reliable["guid"] = "not-a-guid";
                    break;
                case "shape-not-object":
                    reliable["request_shape"] = "invalid";
                    break;
                case "body-required-type":
                    shape!["body_required"] = "true";
                    break;
                case "query-not-array":
                    shape!["query_parameters"] = "invalid";
                    break;
                case "query-item-type":
                    shape!["query_parameters"] = new JsonArray(1);
                    break;
                case "method-unsupported":
                    shape!["method"] = "TRACE";
                    break;
                case "body-mode-unsupported":
                    shape!["body_mode"] = "xml";
                    break;
                case "response-mode-unsupported":
                    shape!["response_mode"] = "bytes";
                    break;
                case "risk-unsupported":
                    shape!["risk"] = "UNKNOWN";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }
        });

        var act = () => _decoder.Decode(stdout, TargetUserServiceId, Fingerprint);

        act.Should().Throw<ManagedCodexServiceApiSkillDiscoveryOutputException>()
            .Which.Code.Should().Be(expectedCode);
    }

    [Theory]
    [InlineData("", Fingerprint)]
    [InlineData(TargetUserServiceId, "not-a-fingerprint")]
    public void Decode_WhenAuthoritativeCorrelationIsInvalid_FailsClosed(
        string targetUserServiceId,
        string capabilityFingerprint)
    {
        var act = () => _decoder.Decode(ReliableJson(), targetUserServiceId, capabilityFingerprint);

        act.Should().Throw<ManagedCodexServiceApiSkillDiscoveryOutputException>()
            .Which.Code.Should().Be("managed_service_api_discovery_correlation_invalid");
    }

    [Theory]
    [InlineData("invalid-reason", "managed_service_api_discovery_no_reliable_reason_invalid")]
    [InlineData("branch-conflict", "managed_service_api_discovery_branch_invalid")]
    [InlineData("branch-not-object", "managed_service_api_discovery_field_type_invalid")]
    public void Decode_WhenNoReliableContractIsInvalid_FailsClosed(string scenario, string expectedCode)
    {
        var root = JsonNode.Parse(NoReliableJson("NO_MATCHING_SKILL"))!.AsObject();
        switch (scenario)
        {
            case "invalid-reason":
                root["no_reliable_skill"]!["reason"] = "UNKNOWN";
                break;
            case "branch-conflict":
                root["reliable_skill"] = new JsonObject();
                break;
            case "branch-not-object":
                root["no_reliable_skill"] = "invalid";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var act = () => _decoder.Decode(root.ToJsonString(), TargetUserServiceId, Fingerprint);

        act.Should().Throw<ManagedCodexServiceApiSkillDiscoveryOutputException>()
            .Which.Code.Should().Be(expectedCode);
    }

    [Theory]
    [InlineData("\"skill_hash\": \"75f0e0480c4cbeed68ba97ffe0b26a0c0cc0ec2d8d0bed631306b383eec0f486\"", "\"skill_hash\": \"latest\"")]
    [InlineData("\"literal_version\": \"1.1\"", "\"literal_version\": \"latest\"")]
    [InlineData("\"path_template\": \"/v1/messages\"", "\"path_template\": \"https://api.example.com/v1/messages\"")]
    [InlineData("\"header_parameters\": [\"Accept\"]", "\"header_parameters\": [\"Authorization\"]")]
    [InlineData("\"body_mode\": \"json\"", "\"body_mode\": \"none\"")]
    public void Decode_WhenReliableRequestShapeOrIdentityIsInvalid_FailsClosed(
        string original,
        string replacement)
    {
        var act = () => _decoder.Decode(
            ReliableJson().Replace(original, replacement, StringComparison.Ordinal),
            TargetUserServiceId,
            Fingerprint);

        act.Should().Throw<ManagedCodexServiceApiSkillDiscoveryOutputException>();
    }

    private static string MutateReliable(Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(ReliableJson())!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    private static JsonObject ReliableSkill(JsonObject root) =>
        root["reliable_skill"]!.AsObject();

    private static JsonObject ReliableShape(JsonObject root) =>
        ReliableSkill(root)["request_shape"]!.AsObject();

    private static string NoReliableJson(string reason) =>
        $$"""
        {
          "schema_version": "service_api_skill_discovery.v1",
          "target_user_service_id": "{{TargetUserServiceId}}",
          "capability_fingerprint": "{{Fingerprint}}",
          "outcome": "no_reliable_skill",
          "no_reliable_skill": {
            "reason": "{{reason}}"
          }
        }
        """;

    private static string ReliableJson() =>
        """
        {
          "schema_version": "service_api_skill_discovery.v1",
          "target_user_service_id": "usvc-alpha",
          "capability_fingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "outcome": "reliable_skill",
          "reliable_skill": {
            "canonical_name": "example-messaging-service-api",
            "guid": "d47a95c5-db2a-4f00-9057-27f674566bd5",
            "literal_version": "1.1",
            "skill_hash": "75f0e0480c4cbeed68ba97ffe0b26a0c0cc0ec2d8d0bed631306b383eec0f486",
            "publisher_id": "9f42ce90-8b05-406d-8461-acb5fdfa4fab",
            "request_shape": {
              "method": "POST",
              "path_template": "/v1/messages",
              "query_parameters": [],
              "header_parameters": ["Accept"],
              "body_mode": "json",
              "body_required": true,
              "response_mode": "text",
              "risk": "WRITE"
            },
            "evidence": [
              {
                "skill_file_path": "SKILL.md",
                "section": "Send a message",
                "operation_id": "send-message"
              }
            ]
          }
        }
        """;
}
