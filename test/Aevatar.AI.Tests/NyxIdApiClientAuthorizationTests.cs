using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApiClientAuthorizationTests
{
    [Theory]
    [InlineData(401, "unauthorized", 1001)]
    [InlineData(403, "forbidden", 1002)]
    public void TryParseProxyError_ShouldExposeStructuredNyxIdAuthorizationFields(
        int status,
        string errorKey,
        int errorCode)
    {
        var response = $$"""
            {"error":true,"status":{{status}},"body":"{\"error\":\"{{errorKey}}\",\"error_code\":{{errorCode}},\"message\":\"credential bearer-secret rejected\"}"}
            """;

        NyxIdApiClient.TryParseProxyError(response, out var error).Should().BeTrue();
        error.Should().NotBeNull();
        error!.HttpStatus.Should().Be(status);
        error.ErrorKey.Should().Be(errorKey);
        error.ErrorCode.Should().Be(errorCode);
        error.Message.Should().Contain("bearer-secret");
        error.IsAuthorizationRequired.Should().BeTrue();
    }
}
