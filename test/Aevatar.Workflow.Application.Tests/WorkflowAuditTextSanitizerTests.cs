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
}
