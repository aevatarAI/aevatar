using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ToolExecutionAuditErrorCodeTests
{
    [Theory]
    [InlineData("NYXID_PROXY_HTTP_429")]
    [InlineData("NYXID_PROXY_HTTP_502")]
    [InlineData("code_execution_route_missing")]
    [InlineData("code_execution_route_inactive")]
    [InlineData("code_execution_route_policy_mismatch")]
    [InlineData("code_execution_route_ambiguous")]
    [InlineData("code_execution_route_access_denied")]
    public void Resolve_CodeExecutionProxyStableCode_ShouldPreserveExactCode(string failureCode)
    {
        ToolExecutionAuditErrorCode.Resolve(failureCode).Should().Be(failureCode);
    }

    [Theory]
    [InlineData("code_execution_cancel_outcome_uncertain")]
    [InlineData("code_execution_cancelled")]
    [InlineData("code_execution_cancellation_requested")]
    [InlineData("code_execution_cancellation_unconfirmed")]
    [InlineData("code_execution_submit_recovery_expired")]
    [InlineData("OPERATION_EXPIRED")]
    [InlineData("FORBIDDEN")]
    [InlineData("UNAUTHENTICATED")]
    public void ResolveForTool_CodeExecuteStableCode_ShouldPreserveExactCode(
        string failureCode)
    {
        ToolExecutionAuditErrorCode.ResolveForTool("code_execute", failureCode)
            .Should().Be(failureCode);
    }

    [Theory]
    [InlineData("FORBIDDEN")]
    [InlineData("UNAUTHENTICATED")]
    public void ResolveForTool_UnrelatedProviderAuthorizationCode_ShouldRemainUntrusted(
        string failureCode)
    {
        ToolExecutionAuditErrorCode.ResolveForTool("provider_tool", failureCode)
            .Should().BeNull();
        ToolExecutionAuditErrorCode.Resolve(failureCode).Should().BeNull();
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
