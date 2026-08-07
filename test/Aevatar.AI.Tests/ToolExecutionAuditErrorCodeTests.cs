using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ToolExecutionAuditErrorCodeTests
{
    [Theory]
    [InlineData("NYXID_PROXY_HTTP_429")]
    [InlineData("NYXID_PROXY_HTTP_502")]
    public void Resolve_CodeExecutionProxyStableCode_ShouldPreserveExactCode(string failureCode)
    {
        ToolExecutionAuditErrorCode.Resolve(failureCode).Should().Be(failureCode);
    }

    [Theory]
    [InlineData("NYXID_PROXY_HTTP_429_suffix")]
    [InlineData("NYXID_PROXY_HTTP_503")]
    [InlineData("provider-owned-secret")]
    public void Resolve_UnownedExternalCode_ShouldRemainUntrusted(string failureCode)
    {
        ToolExecutionAuditErrorCode.Resolve(failureCode).Should().BeNull();
    }
}
