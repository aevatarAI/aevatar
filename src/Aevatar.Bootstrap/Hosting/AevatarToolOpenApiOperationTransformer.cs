using System.Text.Json.Nodes;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aevatar.Bootstrap.Hosting;

public sealed class AevatarToolOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var tool = context.Description.ActionDescriptor.EndpointMetadata.OfType<AevatarToolMetadata>().LastOrDefault();
        if (tool != null)
        {
            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            operation.Extensions["x-aevatar-tool"] = new JsonNodeExtension(new JsonObject { ["readOnly"] = tool.ReadOnly });
        }
        return Task.CompletedTask;
    }
}
