using System.Text.Json.Serialization;
using Aevatar.Studio.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting;

internal static class ChronoStorageErrorResponses
{
    private const int ServiceUnavailableStatusCode = StatusCodes.Status503ServiceUnavailable;

    public static IResult DisabledResult() =>
        Results.Json(
            CreatePayload("Studio could not access chrono-storage-service because it is not enabled for this host."),
            statusCode: ServiceUnavailableStatusCode);

    public static IResult ToResult(ChronoStorageServiceException exception) =>
        Results.Json(CreatePayload(exception), statusCode: ServiceUnavailableStatusCode);

    public static ObjectResult ToActionResult(ChronoStorageServiceException exception) =>
        new(CreatePayload(exception))
        {
            StatusCode = ServiceUnavailableStatusCode,
        };

    private static ChronoStorageErrorPayload CreatePayload(ChronoStorageServiceException exception) =>
        new(
            exception.Code,
            exception.Message,
            exception.Dependency,
            exception.UpstreamStatusCode is null ? null : (int)exception.UpstreamStatusCode.Value);

    private static ChronoStorageErrorPayload CreatePayload(string message) =>
        new(
            ChronoStorageServiceException.DefaultCode,
            message,
            ChronoStorageServiceException.DependencyName,
            null);

    private sealed record ChronoStorageErrorPayload(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("dependency")] string Dependency,
        [property: JsonPropertyName("upstreamStatusCode")] int? UpstreamStatusCode);
}
