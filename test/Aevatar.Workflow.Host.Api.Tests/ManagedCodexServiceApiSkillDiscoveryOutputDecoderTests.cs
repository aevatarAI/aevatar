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

    [Fact]
    public void Decode_WhenNoReliableSkillIsValid_ReturnsTypedNoReliableReason()
    {
        var result = _decoder.Decode(
            """
            {
              "schema_version": "service_api_skill_discovery.v1",
              "target_user_service_id": "usvc-alpha",
              "capability_fingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "outcome": "no_reliable_skill",
              "no_reliable_skill": {
                "reason": "NO_MATCHING_SKILL"
              }
            }
            """,
            TargetUserServiceId,
            Fingerprint);

        result.ResultCase.Should().Be(ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.NoReliableApiSkill);
        result.NoReliableApiSkill.Reason.Should().Be(ServiceApiNoReliableSkillReason.NoMatchingSkill);
    }

    [Theory]
    [InlineData("```json\n{}\n```", "managed_service_api_discovery_stdout_not_json_object")]
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
