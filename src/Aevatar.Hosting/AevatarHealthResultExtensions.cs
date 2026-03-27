using Microsoft.AspNetCore.Http;

namespace Aevatar.Hosting;

public static class AevatarHealthResultExtensions
{
    public static IResult ToHttpResult(this AevatarHealthResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Ok
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
