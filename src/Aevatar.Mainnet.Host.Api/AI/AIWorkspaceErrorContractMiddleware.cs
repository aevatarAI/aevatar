using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Mainnet.Host.Api.AI;

internal sealed class AIWorkspaceErrorContractMiddleware(RequestDelegate next)
{
    private const string AIPathPrefix = "/api/ai";

    public async Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!http.Request.Path.StartsWithSegments(AIPathPrefix))
        {
            await next(http).ConfigureAwait(false);
            return;
        }

        try
        {
            await next(http).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (
            ex.StatusCode == StatusCodes.Status400BadRequest &&
            !http.Response.HasStarted)
        {
            http.Response.Clear();
            await WriteErrorAsync(
                http,
                StatusCodes.Status400BadRequest,
                "AI_REQUEST_INVALID",
                "AI request is invalid.").ConfigureAwait(false);
            return;
        }
        if (http.Response.HasStarted || http.Response.ContentLength is > 0)
            return;

        if (http.Response.StatusCode == StatusCodes.Status400BadRequest)
        {
            await WriteErrorAsync(
                http,
                StatusCodes.Status400BadRequest,
                "AI_REQUEST_INVALID",
                "AI request is invalid.").ConfigureAwait(false);
        }
        else if (http.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            await WriteErrorAsync(
                http,
                StatusCodes.Status401Unauthorized,
                "AI_AUTHENTICATION_REQUIRED",
                "Authentication is required.").ConfigureAwait(false);
        }
    }

    private static async Task WriteErrorAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentLength = null;
        await http.Response.WriteAsJsonAsync(
            new AIWorkspaceErrorResponse(code, message),
            cancellationToken: http.RequestAborted).ConfigureAwait(false);
    }
}

internal static class AIWorkspaceErrorContractApplicationBuilderExtensions
{
    public static IApplicationBuilder UseAIWorkspaceErrorContract(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AIWorkspaceErrorContractMiddleware>();
    }
}
