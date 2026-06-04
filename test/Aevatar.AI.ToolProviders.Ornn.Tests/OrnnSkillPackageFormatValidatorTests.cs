using System.Net;
using System.Net.Http.Headers;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSkillPackageFormatValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ShouldPostRawZipBytesToFormatEndpoint()
    {
        var zipBytes = new byte[] { 1, 2, 3, 4 };
        var handler = new CapturingHandler("""{ "data": { "valid": true, "violations": [] } }""");
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync("caller-token", zipBytes);

        result.IsValid.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsoluteUri.Should().Be("https://nyx.example/api/v1/proxy/s/ornn/api/v1/skill-format/validate");
        request.Authorization!.Parameter.Should().Be("caller-token");
        request.ContentType.Should().Be("application/zip");
        request.Body.Should().Equal(zipBytes);
    }

    [Fact]
    public async Task ValidateAsync_ShouldParseViolationsFromValidationResponse()
    {
        var handler = new CapturingHandler("""
            {
              "data": {
                "valid": false,
                "violations": [
                  { "rule": "skill-md", "message": "missing SKILL.md" }
                ]
              }
            }
            """);
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync("token", [1]);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainSingle()
            .Which.Should().Be(new OrnnSkillPackageFormatViolation("skill-md", "missing SKILL.md"));
    }

    [Fact]
    public async Task ValidateAsync_ShouldSurfaceNyxIdProxyError()
    {
        var handler = new CapturingHandler("""{ "error": "bad" }""", HttpStatusCode.BadGateway);
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync("token", [1]);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("status=502");
    }

    private static OrnnSkillPackageFormatValidator CreateValidator(CapturingHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return new OrnnSkillPackageFormatValidator(new OrnnOptions { NyxIdSlug = "ornn" }, nyxClient);
    }

    private sealed class CapturingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content == null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken)));

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            };
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        AuthenticationHeaderValue? Authorization,
        string? ContentType,
        byte[] Body);
}
