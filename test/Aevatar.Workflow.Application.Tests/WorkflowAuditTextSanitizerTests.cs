using System.Text;
using Aevatar.Workflow.Abstractions.Security;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowAuditTextSanitizerTests
{
    private const string Sentinel = "audit-secret-sentinel";

    [Fact]
    public void Sanitize_ShouldRedactSensitiveJsonFieldsAndInlineCredentials()
    {
        var raw = $$"""
        {
          "query": "weather",
          "authorization": "Bearer {{Sentinel}}",
          "nested": {
            "api_key": "{{Sentinel}}",
            "note": "url=https://example.test/path?access_token={{Sentinel}}"
          }
        }
        """;

        var sanitized = WorkflowAuditTextSanitizer.Sanitize(raw);

        sanitized.Should().NotContain(Sentinel);
        sanitized.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        sanitized.Should().Contain("weather");
    }

    [Fact]
    public void SanitizeMap_ShouldUseFieldNameWhenValueIsShortSecret()
    {
        var opaqueCredential = new string('A', 48);
        var sanitized = WorkflowAuditTextSanitizer.SanitizeMap(
            new Dictionary<string, string>
            {
                ["token"] = Sentinel,
                ["summary"] = $"Bearer {Sentinel}",
                ["request"] = $"opaque={opaqueCredential}",
            });

        sanitized["token"].Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        sanitized["summary"].Should().NotContain(Sentinel);
        sanitized["request"].Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        sanitized["request"].Should().NotContain(opaqueCredential);
    }

    [Fact]
    public void Sanitize_ShouldRedactContactIdentifiersFromNestedJsonAndFreeText()
    {
        const string email = "synthetic.contact@example.test";
        const string userId = "synthetic-user-identifier";
        const string openId = "ou_synthetic_open_identifier";
        var raw = $$"""
        {
          "request": {
            "emails": ["{{email}}"]
          },
          "response": {
            "user_id": "{{userId}}",
            "open_id": "{{openId}}"
          },
          "note": "resolved {{email}}"
        }
        """;

        var sanitized = WorkflowAuditTextSanitizer.Sanitize(raw);

        sanitized.Should().NotContain(email);
        sanitized.Should().NotContain(userId);
        sanitized.Should().NotContain(openId);
        sanitized.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
    }

    [Fact]
    public void Sanitize_ShouldRedactEmailAddressFromNonJsonText()
    {
        const string email = "synthetic.contact@example.test";

        var sanitized = WorkflowAuditTextSanitizer.Sanitize($"resolved contact {email}");

        sanitized.Should().NotContain(email);
        sanitized.Should().Be($"resolved contact {WorkflowAuditTextSanitizer.RedactedValue}");
    }

    [Fact]
    public void Sanitize_ShouldRedactSensitiveKeySuffixes_WithoutRedactingPluralDomainFields()
    {
        var raw = $$"""
        {
          "lark_app_token": "{{Sentinel}}-lark",
          "fooSecret": "{{Sentinel}}-secret",
          "service_password": "{{Sentinel}}-password",
          "maxTokens": 4096,
          "automobile": "sedan"
        }
        """;

        var sanitized = WorkflowAuditTextSanitizer.Sanitize(raw);

        sanitized.Should().NotContain(Sentinel);
        sanitized.Should().Contain("\"lark_app_token\":\"[redacted]\"");
        sanitized.Should().Contain("\"fooSecret\":\"[redacted]\"");
        sanitized.Should().Contain("\"service_password\":\"[redacted]\"");
        sanitized.Should().Contain("\"maxTokens\":4096");
        sanitized.Should().Contain("\"automobile\":\"sedan\"");
    }

    [Fact]
    public void Sanitize_ShouldRedactSensitiveKeySuffixesInFreeTextAssignmentsAndHeaders()
    {
        var raw = $$"""
        LARK_APP_TOKEN={{Sentinel}}-lark
        fooSecret: {{Sentinel}}-secret
        Authorization-Header: {{Sentinel}}-authorization
        token-value: {{Sentinel}}-token
        SIGNING_KEY={{Sentinel}}-signing-snake
        signingKey: {{Sentinel}}-signing-camel
        Signing-Key: {{Sentinel}}-signing-kebab
          Cookie: session={{Sentinel}}-cookie; csrf={{Sentinel}}-csrf
        """ + $"\n\tSet-Cookie: session={Sentinel}-set-cookie; Secure\n" + """
        maxTokens=4096
        automobile=sedan
        """;

        var sanitized = WorkflowAuditTextSanitizer.SanitizeForStorage(raw);

        sanitized.Should().NotContain(Sentinel);
        sanitized.Should().Contain("LARK_APP_TOKEN=[redacted]");
        sanitized.Should().Contain("fooSecret: [redacted]");
        sanitized.Should().Contain("Authorization-Header: [redacted]");
        sanitized.Should().Contain("token-value: [redacted]");
        sanitized.Should().Contain("SIGNING_KEY=[redacted]");
        sanitized.Should().Contain("signingKey: [redacted]");
        sanitized.Should().Contain("Signing-Key: [redacted]");
        sanitized.Should().Contain("  Cookie: [redacted]");
        sanitized.Should().Contain("\tSet-Cookie: [redacted]");
        sanitized.Should().Contain("maxTokens=4096");
        sanitized.Should().Contain("automobile=sedan");
    }

    [Fact]
    public void Sanitize_ShouldRedactCompoundSensitiveKeysInStructuredValues()
    {
        var raw = $$"""
        {
          "token-value": "{{Sentinel}}-token",
          "AuthorizationHeader": "{{Sentinel}}-authorization",
          "fooSecretValue": "{{Sentinel}}-secret",
          "private_key_value": "{{Sentinel}}-private-key",
          "signing_key": "{{Sentinel}}-signing-snake",
          "signingKey": "{{Sentinel}}-signing-camel",
          "Signing-Key": "{{Sentinel}}-signing-kebab",
          "maxTokens": 4096,
          "automobile": "sedan"
        }
        """;

        var sanitizedJson = WorkflowAuditTextSanitizer.SanitizeForStorage(raw);
        var sanitizedMap = WorkflowAuditTextSanitizer.SanitizeMap(new Dictionary<string, string>
        {
            ["token-value"] = Sentinel + "-token",
            ["authorization-header"] = Sentinel + "-authorization",
            ["signing_key"] = Sentinel + "-signing-snake",
            ["signingKey"] = Sentinel + "-signing-camel",
            ["Signing-Key"] = Sentinel + "-signing-kebab",
            ["maxTokens"] = "4096",
            ["automobile"] = "sedan",
        });

        sanitizedJson.Should().NotContain(Sentinel);
        sanitizedJson.Should().Contain("\"token-value\":\"[redacted]\"");
        sanitizedJson.Should().Contain("\"AuthorizationHeader\":\"[redacted]\"");
        sanitizedJson.Should().Contain("\"fooSecretValue\":\"[redacted]\"");
        sanitizedJson.Should().Contain("\"private_key_value\":\"[redacted]\"");
        sanitizedJson.Should().Contain("\"signing_key\":\"[redacted]\"");
        sanitizedJson.Should().Contain("\"signingKey\":\"[redacted]\"");
        sanitizedJson.Should().Contain("\"Signing-Key\":\"[redacted]\"");
        sanitizedJson.Should().Contain("\"maxTokens\":4096");
        sanitizedJson.Should().Contain("\"automobile\":\"sedan\"");
        sanitizedMap["token-value"].Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        sanitizedMap["authorization-header"].Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        sanitizedMap["signing_key"].Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        sanitizedMap["signingKey"].Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        sanitizedMap["Signing-Key"].Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        sanitizedMap["maxTokens"].Should().Be("4096");
        sanitizedMap["automobile"].Should().Be("sedan");
    }

    [Fact]
    public void SanitizeForStorage_ShouldScrubThenRetainUtf8BoundedHeadAndTail()
    {
        const int maxUtf8Bytes = 64 * 1024;
        const string secret = "short-secret-value";
        var raw = $"BEGIN token={secret} " +
                  string.Concat(Enumerable.Repeat("payload-block ", 7000)) +
                  " END-🙂";

        var sanitized = WorkflowAuditTextSanitizer.SanitizeForStorage(
            raw,
            maxUtf8Bytes,
            out var truncated);

        truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(sanitized).Should().BeLessThanOrEqualTo(maxUtf8Bytes);
        sanitized.Should().StartWith($"BEGIN token={WorkflowAuditTextSanitizer.RedactedValue}");
        sanitized.Should().Contain(WorkflowAuditTextSanitizer.HeadTailTruncationMarker);
        sanitized.Should().EndWith(" END-🙂");
        sanitized.Should().NotContain(secret);
        sanitized.Should().NotContain("�");
    }

    [Fact]
    public void SanitizeForStorage_ShouldReturnCompleteSanitizedText_WhenWithinByteLimit()
    {
        var sanitized = WorkflowAuditTextSanitizer.SanitizeForStorage(
            "result token=short-secret-value",
            1024,
            out var truncated);

        truncated.Should().BeFalse();
        sanitized.Should().Be($"result token={WorkflowAuditTextSanitizer.RedactedValue}");
    }

    [Fact]
    public void SanitizeForDisplay_ShouldNotSplitUtf16SurrogatePairAtLimit()
    {
        var prefix = string.Concat(Enumerable.Repeat("x ", 119)) + "x";

        var sanitized = WorkflowAuditTextSanitizer.SanitizeForDisplay(prefix + "🙂tail", 240);

        sanitized.Should().Be(prefix + "...");
        sanitized.Should().NotContain("\ud83d");
        sanitized.Should().NotContain("\ude42");
    }

    [Fact]
    public void SanitizeForStorage_ShouldBeIdempotentForAssignmentRedactionMarker()
    {
        var once = WorkflowAuditTextSanitizer.SanitizeForStorage("provider failed token=short-secret-value");
        var twice = WorkflowAuditTextSanitizer.SanitizeForStorage(once);

        twice.Should().Be(once);
        twice.Should().Be($"provider failed token={WorkflowAuditTextSanitizer.RedactedValue}");
    }

    [Fact]
    public void SanitizeForStorage_ShouldReplaceInvalidUtf16InRetainedSegments()
    {
        var raw = "\ud800HEAD " + string.Concat(Enumerable.Repeat("content ", 20)) + " TAIL\udc00";

        var sanitized = WorkflowAuditTextSanitizer.SanitizeForStorage(raw, 48, out var truncated);

        truncated.Should().BeTrue();
        sanitized.Should().NotContain("\ud800");
        sanitized.Should().NotContain("\udc00");
        sanitized.Should().StartWith("�HEAD");
        sanitized.Should().EndWith("TAIL�");
    }
}
