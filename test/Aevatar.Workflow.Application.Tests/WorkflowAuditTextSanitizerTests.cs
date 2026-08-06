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
        var sanitized = WorkflowAuditTextSanitizer.SanitizeMap(
            new Dictionary<string, string>
            {
                ["token"] = Sentinel,
                ["summary"] = $"Bearer {Sentinel}",
            });

        sanitized["token"].Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        sanitized["summary"].Should().NotContain(Sentinel);
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
}
